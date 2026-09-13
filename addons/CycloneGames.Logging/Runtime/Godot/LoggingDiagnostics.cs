using System;
using System.Globalization;
using CycloneGames.Logging.Pipeline;
using Godot;

namespace CycloneGames.Logging.Godot;

/// <summary>
/// Immutable point-in-time telemetry snapshot. Copying is a plain struct copy and reading its
/// fields never allocates.
/// </summary>
/// <remarks>
/// Counters are split into two families and both are kept, because they answer different
/// questions. <b>Interval</b> fields describe what happened since the previous
/// <see cref="LoggingDiagnostics.Sample"/> and are what you read while watching a live
/// frame curve. <b>Cumulative</b> fields describe the whole run and are what you attach to a bug
/// report. A snapshot that only offered one of the two would force the reader to guess which.
/// </remarks>
public readonly struct LoggingDiagnosticsSnapshot
{
    // ---- Interval: pump cost (the frame-time curve) ----

    /// <summary>Frames on which the handoff pump ran during this interval.</summary>
    public readonly long PumpFrames;

    /// <summary>
    /// Frames on which the pump hit its budget with records still queued. The sharpest single
    /// indicator of a log pulse that outran the per-frame budget.
    /// </summary>
    public readonly long PumpBudgetExhaustedFrames;

    /// <summary>Mean pump duration over this interval, in microseconds.</summary>
    public readonly double AveragePumpMicroseconds;

    /// <summary>Worst pump duration over this interval, in microseconds.</summary>
    public readonly long MaxPumpMicroseconds;

    // ---- Interval: throughput ----

    public readonly long EnqueuedMessages;
    public readonly long ProcessedMessages;
    public readonly long DroppedMessages;
    public readonly long DroppedCriticalMessages;
    public readonly long HandoffDroppedMessages;
    public readonly long HandoffDroppedCriticalMessages;

    // ---- Interval: garbage collection ----

    public readonly int Gen0Collections;
    public readonly int Gen1Collections;
    public readonly int Gen2Collections;
    public readonly long AllocatedBytes;

    // ---- Interval: engine context ----

    public readonly double SampleIntervalSeconds;
    public readonly double FramesPerSecond;
    public readonly double ProcessUptimeSeconds;

    // ---- Live depth at the moment of sampling ----

    public readonly int PipelineQueuedCount;
    public readonly int PipelineQueuedCharacters;
    public readonly int PipelinePeakQueuedCount;
    public readonly int PipelinePeakQueuedCharacters;
    public readonly int HandoffQueuedCount;
    public readonly int HandoffQueuedCharacters;
    public readonly int HandoffPeakQueuedCount;
    public readonly int HandoffPeakQueuedCharacters;

    // ---- Cumulative: whole-run totals ----

    public readonly long TotalEnqueuedMessages;
    public readonly long TotalProcessedMessages;
    public readonly long TotalDroppedMessages;
    public readonly long TotalHandoffDroppedMessages;
    public readonly long TotalSinkFailures;
    public readonly long TotalQuarantinedSinks;
    public readonly long TotalPumpFrames;
    public readonly long TotalPumpBudgetExhaustedFrames;
    public readonly long CumulativeMaxPumpMicroseconds;

    /// <summary>True when a pipeline was owned and its counters are meaningful.</summary>
    public readonly bool HasPipeline;

    /// <summary>True when the backend is faulted and should be restarted rather than inspected.</summary>
    public readonly bool IsFaulted;

    internal LoggingDiagnosticsSnapshot(
        long pumpFrames,
        long pumpBudgetExhaustedFrames,
        double averagePumpMicroseconds,
        long maxPumpMicroseconds,
        long enqueuedMessages,
        long processedMessages,
        long droppedMessages,
        long droppedCriticalMessages,
        long handoffDroppedMessages,
        long handoffDroppedCriticalMessages,
        int gen0Collections,
        int gen1Collections,
        int gen2Collections,
        long allocatedBytes,
        double sampleIntervalSeconds,
        double framesPerSecond,
        double processUptimeSeconds,
        int pipelineQueuedCount,
        int pipelineQueuedCharacters,
        int pipelinePeakQueuedCount,
        int pipelinePeakQueuedCharacters,
        int handoffQueuedCount,
        int handoffQueuedCharacters,
        int handoffPeakQueuedCount,
        int handoffPeakQueuedCharacters,
        long totalEnqueuedMessages,
        long totalProcessedMessages,
        long totalDroppedMessages,
        long totalHandoffDroppedMessages,
        long totalSinkFailures,
        long totalQuarantinedSinks,
        long totalPumpFrames,
        long totalPumpBudgetExhaustedFrames,
        long cumulativeMaxPumpMicroseconds,
        bool hasPipeline,
        bool isFaulted)
    {
        PumpFrames = pumpFrames;
        PumpBudgetExhaustedFrames = pumpBudgetExhaustedFrames;
        AveragePumpMicroseconds = averagePumpMicroseconds;
        MaxPumpMicroseconds = maxPumpMicroseconds;
        EnqueuedMessages = enqueuedMessages;
        ProcessedMessages = processedMessages;
        DroppedMessages = droppedMessages;
        DroppedCriticalMessages = droppedCriticalMessages;
        HandoffDroppedMessages = handoffDroppedMessages;
        HandoffDroppedCriticalMessages = handoffDroppedCriticalMessages;
        Gen0Collections = gen0Collections;
        Gen1Collections = gen1Collections;
        Gen2Collections = gen2Collections;
        AllocatedBytes = allocatedBytes;
        SampleIntervalSeconds = sampleIntervalSeconds;
        FramesPerSecond = framesPerSecond;
        ProcessUptimeSeconds = processUptimeSeconds;
        PipelineQueuedCount = pipelineQueuedCount;
        PipelineQueuedCharacters = pipelineQueuedCharacters;
        PipelinePeakQueuedCount = pipelinePeakQueuedCount;
        PipelinePeakQueuedCharacters = pipelinePeakQueuedCharacters;
        HandoffQueuedCount = handoffQueuedCount;
        HandoffQueuedCharacters = handoffQueuedCharacters;
        HandoffPeakQueuedCount = handoffPeakQueuedCount;
        HandoffPeakQueuedCharacters = handoffPeakQueuedCharacters;
        TotalEnqueuedMessages = totalEnqueuedMessages;
        TotalProcessedMessages = totalProcessedMessages;
        TotalDroppedMessages = totalDroppedMessages;
        TotalHandoffDroppedMessages = totalHandoffDroppedMessages;
        TotalSinkFailures = totalSinkFailures;
        TotalQuarantinedSinks = totalQuarantinedSinks;
        TotalPumpFrames = totalPumpFrames;
        TotalPumpBudgetExhaustedFrames = totalPumpBudgetExhaustedFrames;
        CumulativeMaxPumpMicroseconds = cumulativeMaxPumpMicroseconds;
        HasPipeline = hasPipeline;
        IsFaulted = isFaulted;
    }
}

/// <summary>
/// Opt-in, allocation-free telemetry for the logging stack, in the same shape as
/// <c>Godot25D.Node25DDiagnostics</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cost when disabled.</b> Off by default. Every hot-path guard is a single static field read,
/// and the only work this class does — the GC counters and the interval arithmetic — happens
/// inside <see cref="Sample"/>, which a monitor node calls on its own schedule. A shipped build
/// that never enabled telemetry pays nothing beyond the counters the host already keeps for its
/// own bound enforcement.
/// </para>
/// <para>
/// <b>Why GC counters are part of the logging diagnostics.</b> A logging backend is one of the
/// few systems that can quietly reintroduce garbage into an otherwise zero-allocation frame: a
/// record that formats to a fresh string on the main thread is a per-frame allocation that no
/// profiler labels as "logging". Watching <see cref="LoggingDiagnosticsSnapshot.AllocatedBytes"/>
/// and the Gen0 count deltas next to the pump cost is how you tell "the pipeline is dropping
/// records" apart from "the pipeline is doing work on the frame thread", which is the actual
/// distinction that matters when a frame curve degrades.
/// </para>
/// <para>
/// <b>Interval semantics.</b> <see cref="Sample"/> closes the current measurement window and opens
/// a new one, so calling it at a fixed period gives per-interval rates. The very first call has no
/// previous window and therefore reports the counts accumulated since process start; a monitor
/// should discard or label that first sample.
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="Sample"/> reads snapshot state from the host and the pipeline and
/// is intended to be called from the main thread, once per interval. It takes no lock beyond the
/// ones the sources already take, and it never blocks.
/// </para>
/// </remarks>
public static class LoggingDiagnostics
{
    /// <summary>
    /// Master switch. Toggling at runtime is always safe. Off by default because observable
    /// telemetry is a debugging aid, not a cost every shipped build should pay.
    /// </summary>
    public static bool Enabled;

    private static long _previousEnqueued;
    private static long _previousProcessed;
    private static long _previousDropped;
    private static long _previousDroppedCritical;
    private static long _previousHandoffDropped;
    private static long _previousHandoffDroppedCritical;
    private static long _previousPumpFrames;
    private static long _previousPumpBudgetExhausted;
    private static long _previousTotalPumpMicroseconds;
    private static long _previousCumulativeMaxPumpMicroseconds;
    private static int _previousGen0;
    private static int _previousGen1;
    private static int _previousGen2;
    private static long _previousAllocatedBytes;
    private static ulong _previousSampleTimestampUsec;
    private static bool _hasBaseline;

    /// <summary>
    /// Closes the current measurement window and returns it. Returns a zeroed snapshot
    /// immediately while <see cref="Enabled"/> is false, which is what gives the flag real
    /// meaning: the host's own queue counters are always live because the bound enforcement
    /// needs them, but the four <c>GC</c> queries and the interval arithmetic below are not free
    /// and are skipped entirely.
    /// </summary>
    public static LoggingDiagnosticsSnapshot Sample()
    {
        if (!Enabled)
        {
            return default;
        }

        LoggingHandoffStatistics handoff = LoggingRuntimeHost.GetStatistics();

        long enqueued = 0L;
        long processed = 0L;
        long dropped = 0L;
        long droppedCritical = 0L;
        long sinkFailures = 0L;
        long quarantinedSinks = 0L;
        int pipelineQueued = 0;
        int pipelineQueuedCharacters = 0;
        int pipelinePeakQueued = 0;
        int pipelinePeakQueuedCharacters = 0;
        bool hasPipeline = false;
        bool isFaulted = false;

        if (LoggingBootstrap.TryGetOwnedPipeline(out LogPipeline pipeline))
        {
            hasPipeline = true;
            isFaulted = pipeline.IsFaulted;

            // Snapshot() itself allocates nothing: LogPipelineStatistics is a readonly struct
            // built from fields that are already live, so reading it does not touch the queue.
            LogPipelineStatistics statistics = pipeline.GetStatistics();
            pipelineQueued = statistics.QueuedCount;
            pipelineQueuedCharacters = statistics.QueuedCharacters;
            pipelinePeakQueued = statistics.PeakQueuedCount;
            pipelinePeakQueuedCharacters = statistics.PeakQueuedCharacters;
            enqueued = statistics.EnqueuedMessageCount;
            processed = statistics.ProcessedMessageCount;
            dropped = statistics.DroppedMessageCount;
            droppedCritical = statistics.DroppedCriticalCount;
            sinkFailures = statistics.SinkFailureCount;
            quarantinedSinks = statistics.QuarantinedSinkCount;
        }

        int gen0 = GC.CollectionCount(0);
        int gen1 = GC.CollectionCount(1);
        int gen2 = GC.CollectionCount(2);
        long allocatedBytes = GC.GetTotalAllocatedBytes(false);

        ulong nowUsec = Time.GetTicksUsec();
        double intervalSeconds = 0.0;
        if (_hasBaseline && nowUsec > _previousSampleTimestampUsec)
        {
            intervalSeconds = (nowUsec - _previousSampleTimestampUsec) / 1_000_000.0;
        }

        long pumpFrames = _hasBaseline ? handoff.PumpFrameCount - _previousPumpFrames : handoff.PumpFrameCount;
        long pumpExhausted = _hasBaseline
            ? handoff.PumpBudgetExhaustedCount - _previousPumpBudgetExhausted
            : handoff.PumpBudgetExhaustedCount;
        long pumpMicroseconds = _hasBaseline
            ? handoff.TotalPumpMicroseconds - _previousTotalPumpMicroseconds
            : handoff.TotalPumpMicroseconds;

        double averagePumpMicroseconds = pumpFrames > 0
            ? (double)pumpMicroseconds / pumpFrames
            : 0.0;

        long intervalEnqueued = _hasBaseline ? enqueued - _previousEnqueued : enqueued;
        long intervalProcessed = _hasBaseline ? processed - _previousProcessed : processed;
        long intervalDropped = _hasBaseline ? dropped - _previousDropped : dropped;
        long intervalHandoffDropped = _hasBaseline
            ? handoff.DroppedMessageCount - _previousHandoffDropped
            : handoff.DroppedMessageCount;

        // The cumulative maximum is monotonic by construction, so an interval maximum is only
        // knowable when it grew during this window. Reporting the growth (zero when the worst
        // case is unchanged) is the honest reading; reporting the cumulative value as if it were
        // an interval maximum would make a single early spike look permanent.
        long maxPumpMicroseconds = handoff.MaxPumpMicroseconds > _previousCumulativeMaxPumpMicroseconds
            ? handoff.MaxPumpMicroseconds
            : 0L;

        var snapshot = new LoggingDiagnosticsSnapshot(
            pumpFrames,
            pumpExhausted,
            averagePumpMicroseconds,
            maxPumpMicroseconds,
            intervalEnqueued,
            intervalProcessed,
            intervalDropped,
            _hasBaseline ? droppedCritical - _previousDroppedCritical : droppedCritical,
            intervalHandoffDropped,
            _hasBaseline
                ? handoff.DroppedCriticalCount - _previousHandoffDroppedCritical
                : handoff.DroppedCriticalCount,
            _hasBaseline ? gen0 - _previousGen0 : gen0,
            _hasBaseline ? gen1 - _previousGen1 : gen1,
            _hasBaseline ? gen2 - _previousGen2 : gen2,
            _hasBaseline ? allocatedBytes - _previousAllocatedBytes : allocatedBytes,
            intervalSeconds,
            Engine.GetFramesPerSecond(),
            Time.GetTicksMsec() / 1000.0,
            pipelineQueued,
            pipelineQueuedCharacters,
            pipelinePeakQueued,
            pipelinePeakQueuedCharacters,
            handoff.QueuedCount,
            handoff.QueuedCharacters,
            handoff.PeakQueuedCount,
            handoff.PeakQueuedCharacters,
            enqueued,
            processed,
            dropped,
            handoff.DroppedMessageCount,
            sinkFailures,
            quarantinedSinks,
            handoff.PumpFrameCount,
            handoff.PumpBudgetExhaustedCount,
            handoff.MaxPumpMicroseconds,
            hasPipeline,
            isFaulted);

        _previousEnqueued = enqueued;
        _previousProcessed = processed;
        _previousDropped = dropped;
        _previousDroppedCritical = droppedCritical;
        _previousHandoffDropped = handoff.DroppedMessageCount;
        _previousHandoffDroppedCritical = handoff.DroppedCriticalCount;
        _previousPumpFrames = handoff.PumpFrameCount;
        _previousPumpBudgetExhausted = handoff.PumpBudgetExhaustedCount;
        _previousTotalPumpMicroseconds = handoff.TotalPumpMicroseconds;
        _previousCumulativeMaxPumpMicroseconds = handoff.MaxPumpMicroseconds;
        _previousGen0 = gen0;
        _previousGen1 = gen1;
        _previousGen2 = gen2;
        _previousAllocatedBytes = allocatedBytes;
        _previousSampleTimestampUsec = nowUsec;
        _hasBaseline = true;

        return snapshot;
    }

    /// <summary>
    /// Drops the measurement baseline so the next <see cref="Sample"/> starts a fresh window.
    /// Does not reset any counter owned by the host or the pipeline.
    /// </summary>
    public static void ResetBaseline()
    {
        _hasBaseline = false;
    }

    /// <summary>
    /// Human-readable report. Allocates by design and is formatted with the invariant culture so
    /// an automated log parser survives non-English locales.
    /// </summary>
    public static string ToReport(LoggingDiagnosticsSnapshot snapshot)
    {
        CultureInfo invariant = CultureInfo.InvariantCulture;
        return "[CycloneGames.Logging] window=" + snapshot.SampleIntervalSeconds.ToString("F2", invariant)
            + "s fps=" + snapshot.FramesPerSecond.ToString("F1", invariant)
            + " | pump frames=" + snapshot.PumpFrames
            + " exhausted=" + snapshot.PumpBudgetExhaustedFrames
            + " avgUs=" + snapshot.AveragePumpMicroseconds.ToString("F1", invariant)
            + " maxUs=" + snapshot.MaxPumpMicroseconds
            + " | msgs enq=" + snapshot.EnqueuedMessages
            + " done=" + snapshot.ProcessedMessages
            + " drop=" + snapshot.DroppedMessages
            + " handoffDrop=" + snapshot.HandoffDroppedMessages
            + " | depth pipeline=" + snapshot.PipelineQueuedCount
            + " handoff=" + snapshot.HandoffQueuedCount
            + " peaks=" + snapshot.PipelinePeakQueuedCount + "/" + snapshot.HandoffPeakQueuedCount
            + " | gc gen0=" + snapshot.Gen0Collections
            + " gen1=" + snapshot.Gen1Collections
            + " gen2=" + snapshot.Gen2Collections
            + " allocKB=" + (snapshot.AllocatedBytes / 1024L)
            + " | lifetime enq=" + snapshot.TotalEnqueuedMessages
            + " done=" + snapshot.TotalProcessedMessages
            + " drop=" + snapshot.TotalDroppedMessages
            + " sinkFail=" + snapshot.TotalSinkFailures
            + " quarantined=" + snapshot.TotalQuarantinedSinks
            + " pumpBudgetExhausted=" + snapshot.TotalPumpBudgetExhaustedFrames
            + (snapshot.HasPipeline ? string.Empty : " | PIPELINE=ABSENT")
            + (snapshot.IsFaulted ? " | FAULTED" : string.Empty);
    }

    /// <summary>
    /// The pressure verdict for one window, as a single line suitable for a CI log or a bug
    /// report. Kept separate from <see cref="ToReport"/> because the full report is for a human
    /// watching a live session, while this is for a machine deciding whether the run was clean.
    /// </summary>
    public static string ToVerdict(LoggingDiagnosticsSnapshot snapshot)
    {
        CultureInfo invariant = CultureInfo.InvariantCulture;
        bool pressured = snapshot.PumpBudgetExhaustedFrames > 0
            || snapshot.DroppedMessages > 0
            || snapshot.HandoffDroppedMessages > 0;

        return "logging=" + (pressured ? "PRESSURED" : "CLEAN")
            + " pumpExhaustedFrames=" + snapshot.PumpBudgetExhaustedFrames
            + " dropped=" + (snapshot.DroppedMessages + snapshot.HandoffDroppedMessages)
            + " gen0=" + snapshot.Gen0Collections
            + " allocKB=" + (snapshot.AllocatedBytes / 1024L)
            + " fps=" + snapshot.FramesPerSecond.ToString("F1", invariant);
    }
}
