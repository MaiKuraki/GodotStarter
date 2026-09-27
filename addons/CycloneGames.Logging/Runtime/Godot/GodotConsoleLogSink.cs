using System;
using System.Text;
using System.Threading;
using CycloneGames.Logging.Pipeline;
using CycloneGames.Logging.Pipeline.Internal;

namespace CycloneGames.Logging.Godot;

/// <summary>
/// Bounded adapter from the synchronous pipeline sink contract to Godot's main thread.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where this sits in the stack.</b> A pipeline sink may be invoked on a background worker.
/// Godot's output functions and its scene tree are main-thread only. This adapter is the seam:
/// it renders the record into its final string on the <i>calling</i> thread — where the
/// <see cref="LogEvent"/> is still borrowed and valid — then hands only the string across
/// <see cref="LoggingRuntimeHost"/>'s bounded queue. Rendering on the worker is deliberate: it
/// moves the only per-record allocation off the frame thread, so a log burst costs the main
/// thread nothing except the <c>GD.Print</c> call itself.
/// </para>
/// <para>
/// <b>Reserve, then render.</b> The queue budget is claimed before the string is built and
/// released if the record loses its race with a teardown or a shutdown. A saturated queue
/// therefore drops records <i>without paying to format them</i>, which is what keeps a flood of
/// suppressed records nearly free instead of merely invisible.
/// </para>
/// <para>
/// <b>Lifetime.</b> The adapter keeps the host alive for as long as it is registered, via a
/// generation token taken at construction. Disposing out of order — a stale adapter, a double
/// dispose, a reload that skipped a teardown — cannot decrement the current generation's count,
/// so an orphaned adapter can never free a host that a live pipeline still depends on.
/// </para>
/// </remarks>
public sealed class GodotConsoleLogSink : ILogSink, IFlushableLogSink, IIdempotentLogSinkDisposal
{
    private readonly int _adapterGeneration;
    private readonly LogOutputMode _outputMode;
    private readonly bool _padSeverity;
    private int _disposed;

    /// <summary>
    /// Creates the adapter with default capacity, mounted on a host created on demand.
    /// </summary>
    public GodotConsoleLogSink()
        : this(null)
    {
    }

    /// <summary>
    /// Creates the adapter with an explicit capacity policy.
    /// </summary>
    /// <param name="options">
    /// Handoff capacity and output routing. Pass null to use the host's current configuration,
    /// which is what <see cref="LoggingBootstrap"/> relies on when it has already
    /// configured the queue from project settings.
    /// </param>
    public GodotConsoleLogSink(GodotConsoleLogSinkOptions options)
    {
        if (options != null)
        {
            LoggingRuntimeHost.Configure(options);
        }

        _outputMode = options != null ? options.OutputMode : LogOutputMode.StreamOnly;
        _padSeverity = options != null && options.PadSeverity;
        _adapterGeneration = LoggingRuntimeHost.RegisterAdapter();

        try
        {
            LoggingRuntimeHost.EnsureInstance();
        }
        catch
        {
            LoggingRuntimeHost.UnregisterAdapter(_adapterGeneration);
            throw;
        }
    }

    /// <summary>Snapshot of the handoff queue this adapter feeds.</summary>
    public static LoggingHandoffStatistics GetStatistics()
    {
        return LoggingRuntimeHost.GetStatistics();
    }

    /// <summary>Output routing this adapter installed.</summary>
    public LogOutputMode OutputMode => _outputMode;

    /// <inheritdoc/>
    public void Emit(LogEvent logEvent)
    {
        if (logEvent == null)
        {
            throw new ArgumentNullException(nameof(logEvent));
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(GodotConsoleLogSink));
        }

        int sourcePathCharacters = logEvent.FilePath?.Length ?? 0;
        int estimate = GodotConsoleLogSinkOptions.EstimateRetainedCharacters(
            logEvent.MessageLength,
            logEvent.Category?.Length ?? 0,
            sourcePathCharacters);

        if (!LoggingRuntimeHost.TryReserve(logEvent.Severity, estimate, out LoggingRuntimeHost.Reservation reservation))
        {
            return;
        }

        string formatted = null;
        bool reservationOwned = true;
        try
        {
            formatted = FormatMessage(logEvent, _padSeverity);
            reservationOwned = false;
#if TOOLS
            LoggingRuntimeHost.Commit(logEvent.Severity, formatted, reservation, logEvent.FilePath, logEvent.LineNumber);
#else
            LoggingRuntimeHost.Commit(logEvent.Severity, formatted, reservation);
#endif
        }
        finally
        {
            if (reservationOwned)
            {
                LoggingRuntimeHost.CancelReservation(reservation);
            }
        }
    }

    /// <summary>
    /// Renders one record into its final single-line form.
    /// </summary>
    /// <remarks>
    /// Allocates exactly one string per admitted record, through the shared pool so the
    /// intermediate <see cref="StringBuilder"/> is recycled rather than collected. The layout is
    /// deliberately the same shape the Unity package emits, with <c>res://…:line</c> in place of
    /// Unity's hyperlink markup, because Godot's Output panel links that form natively.
    /// </remarks>
    /// <summary>
    /// Width of the severity column when <see cref="GodotConsoleLogSinkOptions.PadSeverity"/> is
    /// enabled: the longest emittable severity name plus the <c>": "</c> separator.
    /// </summary>
    /// <remarks>
    /// Computed rather than written as a literal, so it cannot silently disagree with
    /// <see cref="LogSeverityNames"/> if a name ever changes. <c>None</c> is deliberately excluded
    /// — it is never emitted, so letting it widen the column would pad every real line for a level
    /// that can never appear.
    /// </remarks>
    private static readonly int SeverityColumnWidth = ComputeSeverityColumnWidth();

    private static int ComputeSeverityColumnWidth()
    {
        int width = 0;
        for (int level = (int)LogSeverity.Trace; level <= (int)LogSeverity.Fatal; level++)
        {
            int length = LogSeverityNames.Get((LogSeverity)level).Length;
            if (length > width)
            {
                width = length;
            }
        }

        return width + 2;   // the ": " that follows the name
    }

    internal static string FormatMessage(LogEvent logEvent, bool padSeverity = false)
    {
        StringBuilder builder = StringBuilderPool.Get();
        try
        {
            // Severity first, matching the kernel's ConsoleLogSink byte for byte in shape.
            //
            // This is not decoration. In EngineDiagnostics mode Godot supplies the prefix itself
            // ("WARNING:" / "ERROR:"), so leaving it out here was invisible — until the default became
            // StreamOnly, where GD.Print adds nothing and a Warning became indistinguishable from an
            // Info once the log was redirected to a file. That is precisely the use case StreamOnly
            // exists for, so relying on the engine to convey severity was a latent defect the mode change
            // exposed. Encoding it here also means a project shipping both a dedicated server and a Godot
            // client reads one log format instead of two.
            //
            // In EngineDiagnostics a Warning therefore renders as "WARNING: WARNING: …" — Godot's own
            // prefix plus ours. The duplication is deliberate and only appears in that mode: the
            // editor's prefix states what the engine classified the line as, ours states which level the
            // record actually carries, and in EngineDiagnostics those two are different claims.
            string severityName = LogSeverityNames.Get(logEvent.Severity);
            builder.Append(severityName);
            builder.Append(": ");

            // Padding goes AFTER the colon, not before it. Both placements produce the same
            // alignment, but this one keeps "INFO:" contiguous so a parser anchored on the severity
            // prefix keeps working; padding before the colon would turn "INFO: " into "INFO   : "
            // and break it, for no visual gain.
            if (padSeverity)
            {
                int padding = SeverityColumnWidth - severityName.Length - 2;
                if (padding > 0)
                {
                    builder.Append(' ', padding);
                }
            }

            if (!string.IsNullOrEmpty(logEvent.Category))
            {
                builder.Append('[');
                AppendEscaped(builder, logEvent.Category);
                builder.Append("] ");
            }

            logEvent.AppendMessageTo(builder, escapeControlCharacters: true);
            AppendSourceLocation(builder, logEvent.FilePath, logEvent.LineNumber);
            return builder.ToString();
        }
        finally
        {
            StringBuilderPool.Return(builder);
        }
    }

    /// <inheritdoc/>
    public bool TryFlush(LogFlushMode mode)
    {
        if (mode != LogFlushMode.Buffered && mode != LogFlushMode.Durable)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), "Unknown flush mode.");
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            return true;
        }

        bool mainThread = LoggingThreading.IsMainThread;
        return LoggingRuntimeHost.TryFlushQueue(mainThread ? 20.0 : 0.0);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            LoggingRuntimeHost.UnregisterAdapter(_adapterGeneration);
        }
    }

    private static void AppendSourceLocation(StringBuilder builder, string sourcePath, int lineNumber)
    {
        if (string.IsNullOrEmpty(sourcePath))
        {
            return;
        }

        string displayPath = LoggingPaths.ToDisplayPath(sourcePath);
        if (string.IsNullOrEmpty(displayPath))
        {
            return;
        }

        // "(at res://Scripts/Foo.cs:42)" — Godot's Output panel turns the res://…:line token
        // into a click that opens the file at that line, so no link registry is needed.
        builder.Append(" (at ");
        AppendEscaped(builder, displayPath);
        builder.Append(':');
        InvariantText.AppendInt32(builder, lineNumber);
        builder.Append(')');
    }

    private static void AppendEscaped(StringBuilder builder, string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            char character = value[i];
            builder.Append(char.IsControl(character) ? '_' : character);
        }
    }
}
