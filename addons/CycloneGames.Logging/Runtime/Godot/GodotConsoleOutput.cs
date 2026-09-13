using System;
using Godot;

namespace CycloneGames.Logging.Godot;

/// <summary>
/// Routes a formatted record to Godot's output. Two modes, because "a log line" and
/// "an engine error" are not the same thing in Godot.
/// </summary>
public enum LogOutputMode : byte
{
    /// <summary>
    /// Stream lines only. Every record — <b>including <c>Error</c> and <c>Fatal</c></b> — is written
    /// with <c>GD.Print</c>, so nothing reaches the engine's error handler: no error icon, no
    /// <c>ERROR:</c> prefix, no entry in the Debugger's Errors tab, and no C# backtrace.
    /// <b>This is the default.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why nothing goes to stderr, not even <c>Error</c>.</b> Godot installs
    /// <c>EditorLog::_error_handler</c> as the engine's global error handler, and it maps
    /// <i>everything</i> that arrives through it — <c>printerr</c>, <c>print_error</c>,
    /// <c>GD.PrintErr</c> and <c>push_error</c> alike — to <c>MSG_TYPE_ERROR</c>. The editor renders
    /// such a line with the error icon, a bold <c>ERROR:</c> prefix, and a count in the Errors tab.
    /// There is therefore no neutral stderr in Godot: writing to it <i>is</i> reporting an error. A
    /// revision of this mode sent <c>Warning</c> and above to <c>GD.PrintErr</c>, claimed it kept the
    /// Errors tab clean, and did the opposite in the editor — the one place that mattered.
    /// </para>
    /// <para>
    /// <b>What that costs.</b> Severity is carried by the record's own tag and nothing else, so a
    /// process-level capture that wants to separate levels must parse the line rather than split on the
    /// stream. That is the price of leaving the error surface alone, and every line is self-describing,
    /// so it is a price that can be paid.
    /// </para>
    /// <para>
    /// <b>Why this is the default and not <see cref="EngineDiagnostics"/>.</b> A record's
    /// <c>Severity</c> is a <i>domain</i> judgement — how much this event matters to the game — while
    /// Godot's error surface is an <i>engine diagnosis</i> primitive meaning the program misused
    /// something. Mapping one onto the other one-to-one conflates them, and the cost is concrete: a game
    /// logs <c>Warning</c> for "handshake retry exhausted" and <c>Error</c> for "peer disconnected" as
    /// ordinary operation, and every one of those would land in the Errors tab and in a CI error count.
    /// That tab is the primary tool for finding real engine and misuse defects, so filling it with
    /// legitimate business events destroys its value — and each record also drags nine frames of this
    /// addon's own call stack into every console and log capture.
    /// </para>
    /// <para>
    /// <b>No special case for <c>Fatal</c>.</b> A revision of this mode routed <c>Fatal</c> to
    /// <c>PushError</c> in both modes, on the argument that an unrecoverable event must be impossible to
    /// miss. It was removed for three reasons. The library does not get to impose "Fatal means the
    /// process is dying" on a project's severity taxonomy — <c>Fatal</c> is the highest level, not a
    /// contractual statement about the process. The Unity package maps <c>Error</c> and <c>Fatal</c> to
    /// the same <c>LogType.Error</c>, so a special case here would have broken parity rather than
    /// improved on it. And a default whose whole purpose is a predictable, unmarked console should not
    /// carry an exception that marks it. A project that wants Fatal to reach a crash path should do
    /// that in a sink or select <see cref="EngineDiagnostics"/>, both of which state the intent
    /// explicitly.
    /// </para>
    /// <para>
    /// Use <see cref="EngineDiagnostics"/> when the engine's error surface <i>is</i> the thing you are
    /// validating — a smoke test or a soak run whose pass/fail is "no engine errors" — or when you
    /// deliberately want <c>Error</c> and above visible to the engine and to CI.
    /// </para>
    /// </remarks>
    StreamOnly = 0,

    /// <summary>
    /// Engine diagnostics: <c>GD.PushWarning</c> / <c>GD.PushError</c> at and above <c>Warning</c>,
    /// <c>GD.Print</c> below.
    /// </summary>
    /// <remarks>
    /// Makes an <c>Error</c> or <c>Fatal</c> record <i>count</i> in Godot: the Debugger's Errors tab
    /// shows it, an unattended run reports a non-zero failure surface, and a CI gate can read that
    /// surface instead of parsing log text. The two costs are inherent to <c>PushWarning</c> /
    /// <c>PushError</c> and cannot be avoided: Godot attaches its own script backtrace, which points at
    /// <see cref="GodotConsoleOutput"/> rather than the call site, and every such record is counted as
    /// an engine error regardless of whether it is a defect. Use this for validation runs, not for a
    /// shipping game — see <see cref="StreamOnly"/> for why.
    /// </remarks>
    EngineDiagnostics = 1
}

/// <summary>
/// The single Godot output boundary for the whole logging stack.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why one boundary.</b> Exactly as in the Unity package: every record that leaves the
/// pipeline funnels through here, so the question "what can this stack actually emit, and can
/// it ever throw?" has one answer in one file. Nothing else in the addon calls
/// <c>GD.Print</c>, <c>GD.PrintRich</c>, <c>GD.PushWarning</c> or <c>GD.PushError</c> outside of
/// explicit diagnostics reporting.
/// </para>
/// <para>
/// <b>Threading.</b> Godot's output functions are main-thread only. The host's bounded handoff
/// queue exists precisely to move records from a background pipeline worker onto the main
/// thread before they reach this method, so the assertion below is a real contract and not a
/// formality.
/// </para>
/// <para>
/// <b>The one unavoidable allocation.</b> Godot's managed bindings take <c>Variant</c>
/// arguments, so every <c>GD.Print</c> boxes the string and allocates the <c>params</c> array.
/// That cost is paid only here, only for records that survived severity filtering, queue
/// admission, and the per-frame pump budget — never on a producer's hot path. The producer
/// side of the stack (the <c>LogChannel.Write</c> path) allocates nothing at all, which is the
/// property that actually matters for frame stability.
/// </para>
/// <para>
/// <b>Never throws.</b> A logging backend that can throw into a producer's call site is worse
/// than no logging backend. Every failure path here is swallowed, because there is no lower
/// boundary left to report to.
/// </para>
/// </remarks>
internal static class GodotConsoleOutput
{
    /// <summary>
    /// BBCode opening tag per severity, or null for "leave the theme's default text colour alone".
    /// </summary>
    /// <remarks>
    /// Only colour names that Godot's own <c>print_rich</c> BBCode-to-ANSI converter recognises are used,
    /// so the editor's RichTextLabel and a terminal agree on what each level looks like. The ladder is
    /// deliberate: Trace and Debug are dimmed because they are low-signal, Info keeps the theme default so
    /// the common case reads like native output, Warning is yellow, Error is red, and Fatal is bold red —
    /// a step past Error rather than a different hue, which is the only escalation Godot's two-level
    /// diagnostic model leaves room for.
    /// </remarks>
    private static readonly string[] RichOpenTagBySeverity =
    {
        "[color=gray]",   // Trace
        "[color=gray]",   // Debug
        null,             // Info — theme default
        "[color=yellow]", // Warning
        "[color=red]",    // Error
        "[b][color=red]", // Fatal
    };

    private static readonly string[] RichCloseTagBySeverity =
    {
        "[/color]",
        "[/color]",
        null,
        "[/color]",
        "[/color]",
        "[/color][/b]",
    };

    /// <summary>
    /// True when the output is being rendered by something that understands colour: the editor's Output
    /// panel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>OS.HasFeature("editor")</c> is the right predicate, not the <c>TOOLS</c> symbol and not
    /// <c>Engine.IsEditorHint()</c>. Pressing Play runs the editor binary in run mode, so
    /// <c>IsEditorHint()</c> is false while the build is still an editor build and its stdout still lands
    /// in the Output panel. A compile-time <c>TOOLS</c> check would be wrong in both directions, since a
    /// standalone Debug run also compiles with <c>TOOLS</c> defined.
    /// </para>
    /// <para>
    /// The headless exclusion is deliberate. A headless run has no panel to colour, and
    /// <c>print_rich</c> converts its BBCode to ANSI escapes before writing them to stdout — so without
    /// this check a CI log fills with escape sequences nothing renders and every grep has to skip. Colour
    /// only where it will be seen.
    /// </para>
    /// </remarks>
    private static bool RendersRichText =>
        OS.HasFeature("editor") && DisplayServer.GetName() != "headless";

    /// <summary>
    /// Emits one fully formatted record. Must be called on the main thread.
    /// </summary>
    /// <param name="severity">Severity used to pick the output channel.</param>
    /// <param name="line">The complete, pre-formatted line (already carries its severity tag).</param>
    /// <param name="mode">Routing policy.</param>
    internal static void Write(LogSeverity severity, string line, LogOutputMode mode)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        LoggingThreading.AssertMainThread("GodotConsoleOutput.Write");

        try
        {
            if (mode == LogOutputMode.StreamOnly)
            {
                WriteStreamLine(severity, line);
                return;
            }

            if (severity >= LogSeverity.Error)
            {
                GD.PushError(line);
            }
            else if (severity >= LogSeverity.Warning)
            {
                GD.PushWarning(line);
            }
            else
            {
                GD.Print(line);
            }
        }
        catch (Exception exception) when (!(exception is OutOfMemoryException))
        {
            // Redirected stdout can be a closed pipe, a full disk, or a dead console. There is
            // nothing below this boundary to report to, so the record is dropped by design.
        }
    }

    /// <summary>
    /// One line for <see cref="LogOutputMode.StreamOnly"/>, coloured when a palette-aware consumer
    /// is listening.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why every level uses the standard output stream, including <c>Error</c>.</b> Godot registers
    /// <c>EditorLog::_error_handler</c> as the engine's global error handler, and that handler maps
    /// everything arriving through it to <c>MSG_TYPE_ERROR</c> — <c>printerr</c>, <c>print_error</c>,
    /// <c>GD.PrintErr</c> and <c>push_error</c> alike. The editor renders such a line with the error icon,
    /// a bold <c>ERROR:</c> prefix, and a count in the Errors tab. There is therefore no neutral stderr in
    /// Godot: writing to it <i>is</i> reporting an error, so "put the warning on stderr but do not report
    /// it as an error" is not expressible. An earlier revision of this mode tried exactly that, described
    /// it as keeping the Errors tab clean, and did the opposite in the editor — the one place it mattered.
    /// </para>
    /// <para>
    /// <b>Why <c>print_rich</c> rather than <c>push_warning</c> / <c>push_error</c>.</b> Those are the only
    /// way to get Godot's own yellow and red, and they come as a package with the error and warning
    /// counters plus a C# backtrace that points at this file rather than the call site. <c>print_rich</c>
    /// renders BBCode in the Output panel — the same visual result — while staying on the standard
    /// message type, so counters and backtraces stay untouched. That is what makes "coloured like native,
    /// inert like a plain print" possible at all; Godot offers no third diagnostic channel, and its output
    /// surface has only two levels (warning and error), so there is no native Fatal to reach for.
    /// </para>
    /// </remarks>
    private static void WriteStreamLine(LogSeverity severity, string line)
    {
        int index = (int)severity;
        if (!RendersRichText || index < 0 || index >= RichOpenTagBySeverity.Length)
        {
            GD.Print(line);
            return;
        }

        string open = RichOpenTagBySeverity[index];
        if (open == null)
        {
            // Info: no colour tag, so no escaping either — GD.Print renders the literal line and the
            // category bracket needs no protection from a parser that will not run.
            GD.Print(line);
            return;
        }

        GD.PrintRich(open + EscapeForRichText(line) + RichCloseTagBySeverity[index]);
    }

    /// <summary>
    /// Escapes <c>[</c> so the line survives RichTextLabel's BBCode parser unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every formatted line begins with <c>[Category]</c>, and RichTextLabel would try to read that as a
    /// tag. <c>[lb]</c> is its documented escape for a literal <c>[</c>, so the panel shows
    /// <c>[Category]</c> exactly as the uncoloured path would.
    /// </para>
    /// <para>
    /// This is the one place the coloured and plain renderings differ in bytes, and the difference is
    /// confined to the editor. Godot's own <c>print_rich</c> also converts the string to ANSI for stdout,
    /// and its converter does not recognise <c>[lb]</c>, so a redirected stdout from an editor-run game
    /// shows <c>[lb]Category]</c>. That stream is a development convenience and is not parsed; an exported
    /// build never takes this path at all, because <see cref="RendersRichText"/> is false there.
    /// </para>
    /// </remarks>
    private static string EscapeForRichText(string line)
    {
        return line.IndexOf('[') < 0 ? line : line.Replace("[", "[lb]");
    }
}
