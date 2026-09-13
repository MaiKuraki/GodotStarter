using System.Collections.Generic;
using Godot;

namespace CycloneGames.Logging.Godot;

/// <summary>
/// Runs the invariant harness and exits, so a CI step can gate on it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a node and not a static call from a script.</b> A GDScript main loop cannot reach
/// a static C# method — static members are not part of the script-visible binding surface. The
/// reachable seam is a <c>[GlobalClass]</c> type instantiated as a scene, so the harness ships as a
/// node that prints its report and quits with an exit code. That also makes it usable by hand: drop
/// it in a scene, or run it as the main scene from the command line.
/// </para>
/// <para>
/// <b>Exit code is the machine-readable part.</b> The report goes to stdout for a human or a log
/// archive; the exit code is what the pipeline reads. A run with any failing check exits 1, so a
/// build that regresses the zero-allocation producer path fails instead of quietly printing FAIL.
/// A <i>skipped</i> check does not fail the run — it means the input for that check was absent —
/// but it is printed as SKIP so a green run cannot hide a check that never executed.
/// </para>
/// <para>
/// <b>Side effects of a headless run, stated plainly.</b> Running this inside a real project loads
/// the project's autoloads, so the logging backend starts and a file sink writes a log file under
/// <c>user://</c>. That is not incidental noise: it is the only place where the full path
/// (project settings → bootstrap → sinks → handoff) is exercised end to end without a window. If a
/// CI job must not touch the filesystem, turn the file sink off in Project Settings for that job.
/// </para>
/// <para>
/// <b>Which builds carry it, precisely.</b> Guarded by <c>#if TOOLS || DEBUG</c>, so it is present in
/// <c>Debug</c> (editor and play-in-editor), present in <c>ExportDebug</c> — which defines <c>DEBUG</c> but
/// deliberately not <c>TOOLS</c> — and compiled away in <c>ExportRelease</c>. That was verified by
/// inspecting the built assemblies in all three configurations rather than by reading the guard, because
/// the difference between "the guard looks right" and "the type is gone" is the whole point.
/// </para>
/// <para>
/// <b>Why a debug export keeps it.</b> Running the harness on the target device is the only way to check
/// allocation and thread behaviour on that platform, and a debug export is how you get it there. It is
/// inert unless instantiated, and nothing in the addon instantiates it — the runner is reachable only
/// through <c>Tools/self_check.tscn</c>, which is run deliberately. One small type in an artifact that is
/// not for shipping is a better trade than no on-device verification at all.
/// </para>
/// <para>
/// <b>One consequence worth knowing.</b> Because the class is compiled away in <c>ExportRelease</c>,
/// <c>Tools/self_check.tscn</c> references a script that does not exist in that configuration. Nothing loads
/// that scene in a shipped game, so it is dormant rather than broken; add <c>Tools/</c> to the export
/// preset's exclude filter if you would rather it not be in the package at all.
/// </para>
/// </remarks>
#if TOOLS || DEBUG
[GlobalClass]
public partial class LoggingSelfCheckRunner : Node
{
    /// <summary>Producer-path iterations for the allocation measurement.</summary>
    [Export] public int Iterations { get; set; } = 200_000;

    /// <summary>Quit with an exit code once the harness finishes. Off when run by hand.</summary>
    [Export] public bool QuitOnFinish { get; set; } = true;

    /// <summary>Also print the idle-pool report.</summary>
    [Export] public bool IncludePoolReport { get; set; } = true;

    public override void _Ready()
    {
        LoggingSelfCheck.HandoffCounters? counters = LoggingBootstrap.TryGetOwnedPipeline(out _)
            ? LoggingRuntimeHost.ReadHarnessCounters()
            : (LoggingSelfCheck.HandoffCounters?)null;

        List<LoggingSelfCheck.CheckResult> results = LoggingSelfCheck.RunAll(Iterations, counters);

        int passed = 0;
        int failed = 0;
        int skipped = 0;

        for (int i = 0; i < results.Count; i++)
        {
            LoggingSelfCheck.CheckResult result = results[i];
            if (result.Skipped)
            {
                skipped++;
            }
            else if (result.Passed)
            {
                passed++;
            }
            else
            {
                failed++;
            }

            GD.Print((result.Skipped ? "SKIP  " : result.Passed ? "PASS  " : "FAIL  ") + result.Name
                + (string.IsNullOrEmpty(result.Detail) ? string.Empty : " — " + result.Detail));
        }

        GD.Print(passed + " passed, " + failed + " failed, " + skipped + " skipped.");

        if (IncludePoolReport)
        {
            GD.Print(LoggingSelfCheck.FormatPoolReport());
        }

        if (!counters.HasValue)
        {
            GD.Print("Note: no owned pipeline was running, so the handoff check was skipped. This is "
                + "expected only when the plugin autoload is absent.");
        }

        GD.Print(failed == 0
            ? "CycloneGames.Logging self-check: ALL PASSED"
            : "CycloneGames.Logging self-check: FAILURES PRESENT");

        if (QuitOnFinish)
        {
            GetTree().Quit(failed == 0 ? 0 : 1);
        }
    }
}
#endif
