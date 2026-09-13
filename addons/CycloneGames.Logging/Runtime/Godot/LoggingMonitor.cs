using System.Globalization;
using CycloneGames.Logging.Pipeline;
using Godot;

namespace CycloneGames.Logging.Godot;

/// <summary>
/// Drop-in runtime monitor for the logging stack. Add it anywhere in the scene tree — or as an
/// autoload — and it reports logging pressure and its cost to the frame on a fixed interval.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it is for.</b> The three ways a logging backend degrades a shipped game are all
/// invisible in a normal log: it starts dropping records (the queue is saturated), it starts
/// costing frame time (the pump is hitting its budget every frame), or it starts allocating on
/// the frame thread (a sink is formatting where it should not). This node separates those three
/// by reporting them as different signals instead of one "logs look fine" impression, so a
/// performance regression is attributable instead of mysterious.
/// </para>
/// <para>
/// <b>Spike warnings are rate-limited by construction.</b> They fire once per interval, and the
/// interval is the measurement window, so a sustained problem produces a steady cadence rather
/// than a per-frame flood that would itself become the performance problem. A warning that
/// repeats every interval is a real signal; a warning that repeats every frame is noise.
/// </para>
/// <para>
/// <b>First sample is discarded.</b> The initial window has no predecessor, so its counts are
/// "since process start" and its rates are meaningless. This node consumes that first sample
/// without logging it, which keeps a startup burst from being reported as if it were steady
/// state.
/// </para>
/// </remarks>
[GlobalClass]
public partial class LoggingMonitor : Node
{
    /// <summary>Seconds between reports.</summary>
    [Export] public double IntervalSeconds { get; set; } = 5.0;

    /// <summary>Print the full report every interval.</summary>
    [Export] public bool LogToConsole { get; set; } = true;

    /// <summary>
    /// Warn when the worst pump in the interval exceeded this many microseconds. The default of
    /// 4000 µs is deliberately generous: a healthy pump is tens of microseconds, so anything
    /// past a few milliseconds means a sink is doing real work on the frame thread.
    /// </summary>
    [Export] public long PumpSpikeWarningMicroseconds { get; set; } = 4000L;

    /// <summary>
    /// Warn when the pump hit its budget with records still queued on any frame in the interval —
    /// the direct signature of a log pulse outrunning the per-frame budget.
    /// </summary>
    [Export] public bool WarnOnPumpBudgetExhaustion { get; set; } = true;

    /// <summary>
    /// Warn when any record was dropped in the interval. Dropping is a bound being enforced, not
    /// a bug, but an operator needs to know when the bound is the thing shaping their logs.
    /// </summary>
    [Export] public bool WarnOnDroppedRecords { get; set; } = true;

    /// <summary>
    /// Warn when Gen0 collections in the interval exceed this count. Zero disables the check.
    /// A logging stack that allocates nothing on the frame path leaves this at zero.
    /// </summary>
    [Export] public int Gen0WarningThreshold { get; set; }

    private double _accumulator;
    private bool _discardNextSample = true;

    public override void _Ready()
    {
        if (!LoggingDiagnostics.Enabled)
        {
            // The monitor is the consumer of telemetry, not its enabler: a build that never
            // turned sampling on should not start paying for it just because a monitor was
            // dropped into the scene. Saying so once is more useful than silently doing nothing.
            GD.PushWarning(
                "LoggingMonitor is in the tree but LoggingDiagnostics.Enabled is false, so "
                + "it has nothing to report. Set LoggingDiagnostics.Enabled = true in your "
                + "diagnostics composition root to enable telemetry.");
            SetProcess(false);
        }
    }

    public override void _Process(double delta)
    {
        _accumulator += delta;
        if (_accumulator < IntervalSeconds)
        {
            return;
        }

        _accumulator = 0.0;
        LoggingDiagnosticsSnapshot snapshot = LoggingDiagnostics.Sample();

        if (_discardNextSample)
        {
            _discardNextSample = false;
            return;
        }

        if (LogToConsole)
        {
            GD.Print(LoggingDiagnostics.ToReport(snapshot));
        }

        ReportPressure(snapshot);
    }

    private void ReportPressure(LoggingDiagnosticsSnapshot snapshot)
    {
        if (snapshot.IsFaulted)
        {
            GD.PushError(
                "[CycloneGames.Logging] The pipeline is faulted and has stopped accepting records. "
                + "Call LoggingBootstrap.Reinitialize after resolving the underlying failure.");
            return;
        }

        if (PumpSpikeWarningMicroseconds > 0L
            && snapshot.MaxPumpMicroseconds > PumpSpikeWarningMicroseconds)
        {
            GD.PushWarning(
                "[CycloneGames.Logging] Pump spike: " + snapshot.MaxPumpMicroseconds
                + " us in this interval (threshold " + PumpSpikeWarningMicroseconds
                + " us). A sink is doing work on the frame thread; check file-sink flush settings "
                + "and the per-frame pump budgets. Frames=" + snapshot.PumpFrames);
        }

        if (WarnOnPumpBudgetExhaustion && snapshot.PumpBudgetExhaustedFrames > 0)
        {
            GD.PushWarning(
                "[CycloneGames.Logging] Log pulse exceeded the per-frame pump budget on "
                + snapshot.PumpBudgetExhaustedFrames + " of " + snapshot.PumpFrames
                + " pumping frames. Backlog is being spread over later frames; raise the budget "
                + "only if the frame curve can absorb it.");
        }

        if (WarnOnDroppedRecords)
        {
            long dropped = snapshot.DroppedMessages + snapshot.HandoffDroppedMessages;
            if (dropped > 0)
            {
                GD.PushWarning(
                    "[CycloneGames.Logging] " + dropped + " records dropped in this interval ("
                    + snapshot.DroppedMessages + " pipeline, " + snapshot.HandoffDroppedMessages
                    + " handoff). Critical drops="
                    + (snapshot.DroppedCriticalMessages + snapshot.HandoffDroppedCriticalMessages)
                    + ". The configured queue bounds are shaping the log; raise them only with a "
                    + "matching memory budget.");
            }
        }

        if (Gen0WarningThreshold > 0 && snapshot.Gen0Collections > Gen0WarningThreshold)
        {
            GD.PushWarning(
                "[CycloneGames.Logging] " + snapshot.Gen0Collections
                + " Gen0 collections in this interval (threshold " + Gen0WarningThreshold
                + "), allocated " + (snapshot.AllocatedBytes / 1024L)
                + " KB. Logging should not allocate on the frame path; this points at a sink "
                + "formatting where the producer path should not be.");
        }
    }

    /// <summary>
    /// One-line pressure verdict for CI logs or an automated soak run. Kept as a static helper so
    /// it can be called without a monitor in the tree.
    /// </summary>
    public static string SampleVerdict()
    {
        if (!LoggingDiagnostics.Enabled)
        {
            return "logging=TELEMETRY_DISABLED";
        }

        return LoggingDiagnostics.ToVerdict(LoggingDiagnostics.Sample());
    }

    /// <summary>
    /// Reads the live queue depths into out-parameters without sampling telemetry and without
    /// allocating. Intended for a debug overlay that needs the depth every frame and would
    /// otherwise reintroduce exactly the per-frame garbage this stack exists to avoid.
    /// </summary>
    public static void ReadLiveDepth(
        out int pipelineQueued,
        out int handoffQueued,
        out int pipelinePeak,
        out int handoffPeak)
    {
        LoggingHandoffStatistics handoff = GodotConsoleLogSink.GetStatistics();
        handoffQueued = handoff.QueuedCount;
        handoffPeak = handoff.PeakQueuedCount;

        if (LoggingBootstrap.TryGetOwnedPipeline(out LogPipeline pipeline))
        {
            LogPipelineStatistics pipelineStatistics = pipeline.GetStatistics();
            pipelineQueued = pipelineStatistics.QueuedCount;
            pipelinePeak = pipelineStatistics.PeakQueuedCount;
        }
        else
        {
            pipelineQueued = 0;
            pipelinePeak = 0;
        }
    }
}
