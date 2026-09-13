using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CycloneGames.Logging.Pipeline;
using CycloneGames.Logging.Pipeline.Internal;

namespace CycloneGames.Logging.Godot;

/// <summary>
/// A deterministic, dependency-free invariant harness for the logging stack.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a harness and not a unit-test framework.</b> The Unity repository uses NUnit through the
/// Unity Test Framework. A Godot C# project has no test runner in the box: adding one means adding
/// an editor extension, a package dependency, and a CI step, all of which are the host project's
/// decisions rather than an addon's. This harness therefore has no dependencies at all. It runs from
/// the editor dock, from the headless runner scene (<c>Tools/self_check.tscn</c>), or from user code
/// — and because every check is written against the pure-.NET kernel with host state passed in as a
/// parameter, the same source also compiles into a plain console harness so CI can run it without
/// launching Godot at all.
/// </para>
/// <para>
/// <b>Every channel binds its writer explicitly, and every check proves it ran.</b> Both rules exist
/// because of one defect this harness shipped with: channels were created without a writer, so they
/// resolved the process-wide fallback — the allocating <c>NullLogWriter</c> when no backend is
/// installed — and five checks silently measured nothing while reporting results. The
/// zero-allocation check was the worst of them: it <i>passed</i>, because enqueueing nothing
/// allocates nothing. A measurement that can silently measure nothing is worse than no measurement,
/// so each check now asserts that the pipeline it built actually carried the records it expected,
/// and fails with an explicit "NOT EXERCISED" detail when it did not.
/// </para>
/// <para>
/// <b>No check may depend on ambient state.</b> The same file runs in two very different environments:
/// inside a bootstrapped game, where <c>LogRuntime</c> holds the live pipeline, and in an engine-free
/// console harness, where nothing has installed a writer at all. An assertion whose expected value changes
/// between the two is not a test but a coin flip, and it fails in whichever environment the author is not
/// developing in. <c>writer.null-is-noop</c> learned this the expensive way: it asserted that an unbound
/// channel reports disabled, which is true only where no backend exists. Anything that genuinely depends
/// on the host is either passed in as a parameter (the handoff counters, which report <c>SKIP</c> when no
/// snapshot is supplied) or reported in a detail string rather than asserted.
/// </para>
/// <para>
/// <b>What it actually proves.</b> The producer path's allocation measurement matters most.
/// Everything in this design is in service of "logging from game code allocates nothing", and a
/// claim that strong has to be measured, not asserted.
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> gives an exact per-thread byte delta, so the
/// check reports the real number — including zero — and fails loudly if a change ever reintroduces an
/// allocation. The other checks cover the invariants that make the zero plausible: deferred builders
/// that are not invoked when suppressed, truncation that bounds retained characters, reserve capacity
/// that survives overflow, a bounded ring that recycles, a pool deep enough to absorb the queue it
/// serves, and a shutdown that drains.
/// </para>
/// <para>
/// <b>Isolation.</b> Every check builds and tears down its own pipeline through
/// <see cref="LogPipelineFactory"/> and never installs a process writer, so running the harness cannot
/// disturb a live game session. The pools it warms are process-wide shared state; that is by design
/// and only ever helps a later measurement, but it does mean check order can affect raw pool counts.
/// </para>
/// </remarks>
public static class LoggingSelfCheck
{
    private const string SteadyStateMessage = "steady state frame record";

    /// <summary>
    /// Live main-thread handoff counters, supplied by the caller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is a parameter and not a call into the host.</b> Reading the counters from
    /// <see cref="LoggingRuntimeHost"/> directly would make this file depend on a <c>Node</c>, and a
    /// file that depends on a <c>Node</c> cannot be compiled outside the engine. The checks below are
    /// about the kernel's invariants and have nothing to do with the scene tree, so tying them to it
    /// would mean the whole suite could only ever run inside a running game — the one place where a
    /// developer is least willing to run it and CI cannot run it at all. Passing the counters in keeps
    /// this file pure .NET, which is what lets the same source compile into a plain console harness
    /// for CI and into the addon for the editor dock.
    /// </para>
    /// <para>
    /// <b>Null is a valid input and means "no host".</b> The handoff check is then reported as skipped
    /// rather than silently passing on zeroes. A check that passes vacuously is worse than no check,
    /// because the summary line still claims it ran.
    /// </para>
    /// </remarks>
    public readonly struct HandoffCounters
    {
        public readonly int QueuedCount;
        public readonly int InFlightCount;
        public readonly int ReservedCount;
        public readonly int PeakQueuedCount;
        public readonly long PumpFrameCount;
        public readonly long PumpBudgetExhaustedCount;
        public readonly long DroppedMessageCount;
        public readonly long DroppedCriticalCount;

        public HandoffCounters(
            int queuedCount,
            int inFlightCount,
            int reservedCount,
            int peakQueuedCount,
            long pumpFrameCount,
            long pumpBudgetExhaustedCount,
            long droppedMessageCount,
            long droppedCriticalCount)
        {
            QueuedCount = queuedCount;
            InFlightCount = inFlightCount;
            ReservedCount = reservedCount;
            PeakQueuedCount = peakQueuedCount;
            PumpFrameCount = pumpFrameCount;
            PumpBudgetExhaustedCount = pumpBudgetExhaustedCount;
            DroppedMessageCount = droppedMessageCount;
            DroppedCriticalCount = droppedCriticalCount;
        }
    }

    public readonly struct CheckResult
    {
        public readonly string Name;
        public readonly bool Passed;

        /// <summary>
        /// True when the check could not run for lack of an input. Neither a pass nor a failure, and
        /// reported separately so a run that silently lost a check cannot look green.
        /// </summary>
        public readonly bool Skipped;

        public readonly string Detail;

        internal CheckResult(string name, bool passed, string detail, bool skipped = false)
        {
            Name = name;
            Passed = passed;
            Skipped = skipped;
            Detail = detail;
        }
    }

    private sealed class CountingSink : ILogSink
    {
        internal int Count;
        internal int TotalMessageCharacters;
        internal int TruncatedCount;
        internal int HighestSeverity;

        public void Emit(LogEvent logEvent)
        {
            Count++;
            TotalMessageCharacters += logEvent.MessageLength;
            if (logEvent.WasTruncated)
            {
                TruncatedCount++;
            }

            if ((int)logEvent.Severity > HighestSeverity)
            {
                HighestSeverity = (int)logEvent.Severity;
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingSink : ILogSink
    {
        internal int Attempts;

        public void Emit(LogEvent logEvent)
        {
            Attempts++;
            throw new InvalidOperationException("sink failure");
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Runs every check and returns one line of text per check.</summary>
    public static string RunAndFormat(int iterations = 200_000, HandoffCounters? handoff = null)
    {
        List<CheckResult> results = RunAll(iterations, handoff);
        var builder = new StringBuilder(results.Count * 128);
        int passed = 0;
        int skipped = 0;

        for (int i = 0; i < results.Count; i++)
        {
            CheckResult result = results[i];
            if (result.Skipped)
            {
                skipped++;
            }
            else if (result.Passed)
            {
                passed++;
            }

            builder.Append(result.Skipped ? "SKIP " : result.Passed ? "PASS " : "FAIL ");
            builder.Append(result.Name);
            if (!string.IsNullOrEmpty(result.Detail))
            {
                builder.Append(" — ");
                builder.Append(result.Detail);
            }

            builder.Append('\n');
        }

        builder.Append(passed);
        builder.Append('/');
        builder.Append(results.Count - skipped);
        builder.Append(" checks passed");
        if (skipped > 0)
        {
            builder.Append(" (");
            builder.Append(skipped);
            builder.Append(" skipped: no host snapshot supplied)");
        }

        builder.Append('.');
        return builder.ToString();
    }

    /// <summary>Runs every check. Allocates by design — harness path only.</summary>
    public static List<CheckResult> RunAll(int iterations = 200_000, HandoffCounters? handoff = null)
    {
        var results = new List<CheckResult>(11)
        {
            CheckSeverityOrdering(),
            CheckNullWriterIsFree(),
            CheckDeferredBuilderIsDeferred(),
            CheckTruncationBoundsRetention(),
            CheckReserveCapacityUnderOverflow(),
            CheckDropOldestRecyclesCapacity(),
            CheckSinkFailureIsContained(),
            CheckProducerPathAllocatesNothing(iterations),
            CheckPoolRetentionCoversQueueDepth(),
            CheckHandoffCountersConsistent(handoff),
            CheckShutdownDrains()
        };

        return results;
    }

    private static CheckResult CheckSeverityOrdering()
    {
        bool ordered = LogSeverity.Trace < LogSeverity.Debug
            && LogSeverity.Debug < LogSeverity.Info
            && LogSeverity.Info < LogSeverity.Warning
            && LogSeverity.Warning < LogSeverity.Error
            && LogSeverity.Error < LogSeverity.Fatal
            && LogSeverity.Fatal < LogSeverity.None;

        bool emittable = LogWriterGuard.IsEmittable(LogSeverity.Trace)
            && LogWriterGuard.IsEmittable(LogSeverity.Fatal)
            && !LogWriterGuard.IsEmittable(LogSeverity.None);

        return new CheckResult(
            "severity.order",
            ordered && emittable,
            ordered ? "Trace<Debug<Info<Warning<Error<Fatal<None, None is not emittable" : "ordering broken");
    }

    /// <summary>
    /// The <see cref="NullLogWriter"/> contract, plus the process-fallback resolution rule.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this check no longer leaves a channel unbound and expects "disabled".</b> That is what it
    /// used to do, and it passed in the engine-free console harness while failing inside a running game —
    /// the worst split, because the console harness is where it was developed. An unbound
    /// <see cref="LogChannel"/> resolves the <i>process</i> writer, and a bootstrapped Godot project has
    /// installed the live pipeline there, so the channel correctly reported enabled. The assertion was
    /// environment-dependent, so the assertion was wrong, not the code under test.
    /// </para>
    /// <para>
    /// What is environment-independent is the contract itself, so that is what is asserted now: a channel
    /// bound to <see cref="NullLogWriter.Instance"/> reports disabled and its writes are no-ops; an
    /// identically shaped channel bound to a real pipeline reports enabled (the control that makes the
    /// first result meaningful); and an unbound channel <i>agrees</i> with whatever the process writer
    /// says, which holds whether or not a backend is installed.
    /// </para>
    /// </remarks>
    private static CheckResult CheckNullWriterIsFree()
    {
        var nullBound = LogChannel.Create("SelfCheck.Null", NullLogWriter.Instance);
        bool nullEnabled = nullBound.IsEnabled(LogSeverity.Fatal);
        nullBound.Fatal("must not throw");

        LogPipeline pipeline = LogPipelineFactory.CreateSingleThreaded();
        try
        {
            pipeline.RegisterSink(new CountingSink());
            bool pipelineEnabled = LogChannel.Create("SelfCheck.Null", pipeline).IsEnabled(LogSeverity.Fatal);

            bool processWriterInstalled = LogRuntime.HasWriter;
            bool resolvedEnabled = LogChannel.Create("SelfCheck.Null").IsEnabled(LogSeverity.Fatal);
            bool expectedResolved = processWriterInstalled
                && LogWriterGuard.IsEnabled(LogRuntime.Writer, LogSeverity.Fatal, "SelfCheck.Null");
            bool resolutionHolds = resolvedEnabled == expectedResolved;

            return new CheckResult(
                "writer.null-is-noop",
                !nullEnabled && pipelineEnabled && resolutionHolds,
                "null-bound=" + nullEnabled + " (want false), pipeline-bound=" + pipelineEnabled
                + " (want true), process writer installed=" + processWriterInstalled
                + ", unbound channel resolves " + (resolutionHolds ? "consistently" : "INCONSISTENTLY"));
        }
        finally
        {
            pipeline.Shutdown();
        }
    }

    private static CheckResult CheckDeferredBuilderIsDeferred()
    {
        var sink = new CountingSink();
        LogPipeline pipeline = LogPipelineFactory.CreateSingleThreaded();
        try
        {
            pipeline.RegisterSink(sink);
            pipeline.MinimumSeverity = LogSeverity.Error;

            // Bound explicitly. An unbound channel would resolve the process-wide fallback and this
            // check would measure nothing while looking like it passed.
            var channel = LogChannel.Create("SelfCheck.Deferred", pipeline);

            int builderInvocations = 0;
            for (int i = 0; i < 1000; i++)
            {
                channel.Info((Action<StringBuilder>)(builder =>
                {
                    builderInvocations++;
                    builder.Append("built");
                }));
            }

            pipeline.Pump(1024);

            // Control record: proves the pipeline was actually live, so "zero invocations" is a fact
            // about the severity filter rather than a fact about a dead pipeline.
            channel.Error("control");
            pipeline.Pump(16);

            bool controlDelivered = sink.Count == 1 && sink.HighestSeverity == (int)LogSeverity.Error;

            return new CheckResult(
                "producer.builder-not-invoked-when-suppressed",
                builderInvocations == 0 && controlDelivered,
                "invocations=" + builderInvocations + " delivered=" + sink.Count
                + " (control record delivered=" + controlDelivered + ")");
        }
        finally
        {
            pipeline.Shutdown();
        }
    }

    private static CheckResult CheckTruncationBoundsRetention()
    {
        var options = new LogPipelineOptions { MaxMessageCharacters = 64 };
        var sink = new CountingSink();
        LogPipeline pipeline = LogPipelineFactory.CreateSingleThreaded(options);
        try
        {
            pipeline.RegisterSink(sink);
            var channel = LogChannel.Create("SelfCheck.Truncation", pipeline);

            var oversized = new string('x', 500);

            // Must be the single-argument overload. Writing channel.Info("category", oversized)
            // compiles — it binds the second string to [CallerFilePath] — and would then assert
            // truncation of a 20-character category while the 500-character message went out
            // untruncated, turning this check into a false failure. The compile-only trap is why the
            // assertion below also verifies the delivered length rather than just the flag.
            channel.Info(oversized);
            pipeline.Pump(16);

            bool delivered = sink.Count == 1;
            bool bounded = sink.TruncatedCount == 1 && sink.TotalMessageCharacters <= 64;

            return new CheckResult(
                "pipeline.truncation-bounds-retention",
                delivered && bounded,
                "delivered=" + sink.Count + " truncated=" + sink.TruncatedCount
                + " retainedChars=" + sink.TotalMessageCharacters + " limit=64"
                + (delivered ? string.Empty : " — NOT EXERCISED"));
        }
        finally
        {
            pipeline.Shutdown();
        }
    }

    private static CheckResult CheckReserveCapacityUnderOverflow()
    {
        var options = new LogPipelineOptions
        {
            MaxQueuedMessages = 16,
            ReservedCriticalMessages = 4,
            OverflowPolicy = LogQueueOverflowPolicy.DropNewest
        };

        var sink = new CountingSink();
        LogPipeline pipeline = LogPipelineFactory.CreateSingleThreaded(options);
        try
        {
            pipeline.RegisterSink(sink);
            var channel = LogChannel.Create("SelfCheck.Reserve", pipeline);

            for (int i = 0; i < 64; i++)
            {
                channel.Info("normal traffic");
            }

            channel.Fatal("critical survives saturation");
            pipeline.Pump(4096);

            bool criticalArrived = sink.Count > 0 && sink.HighestSeverity >= (int)LogSeverity.Fatal;
            LogPipelineStatistics statistics = pipeline.GetStatistics();

            // No finite queue can promise delivery under unbounded overload, so the honest invariant
            // is narrower and stronger: the critical record must still get out, and the bound must
            // have been enforced rather than silently ignored.
            return new CheckResult(
                "handoff.reserve-capacity-under-overflow",
                criticalArrived && statistics.DroppedMessageCount > 0,
                "criticalArrived=" + criticalArrived + " delivered=" + sink.Count
                + " dropped=" + statistics.DroppedMessageCount
                + " capacity=16 reserve=4"
                + (sink.Count > 0 ? string.Empty : " — NOT EXERCISED"));
        }
        finally
        {
            pipeline.Shutdown();
        }
    }

    private static CheckResult CheckDropOldestRecyclesCapacity()
    {
        var options = new LogPipelineOptions
        {
            MaxQueuedMessages = 8,
            ReservedCriticalMessages = 2,
            OverflowPolicy = LogQueueOverflowPolicy.DropOldest
        };

        var sink = new CountingSink();
        LogPipeline pipeline = LogPipelineFactory.CreateSingleThreaded(options);
        try
        {
            pipeline.RegisterSink(sink);
            var channel = LogChannel.Create("SelfCheck.DropOldest", pipeline);

            for (int i = 0; i < 32; i++)
            {
                channel.Info("payload");
            }

            pipeline.Pump(4096);
            LogPipelineStatistics statistics = pipeline.GetStatistics();

            // DropOldest must lose the oldest *normal* records and keep accepting, so the queue stays
            // full rather than latching shut, and the ring-buffer compaction must not corrupt the
            // retained order.
            bool processed = statistics.ProcessedMessageCount > 0;
            bool recycled = statistics.DroppedOldestCount > 0;
            bool noneDroppedNewest = statistics.DroppedNewestCount == 0;

            return new CheckResult(
                "pipeline.drop-oldest-recycles",
                processed && recycled && noneDroppedNewest,
                "processed=" + statistics.ProcessedMessageCount
                + " droppedOldest=" + statistics.DroppedOldestCount
                + " droppedNewest=" + statistics.DroppedNewestCount + " (want 0)"
                + (processed ? string.Empty : " — NOT EXERCISED"));
        }
        finally
        {
            pipeline.Shutdown();
        }
    }

    private static CheckResult CheckSinkFailureIsContained()
    {
        var sink = new ThrowingSink();
        LogPipeline pipeline = LogPipelineFactory.CreateSingleThreaded();
        try
        {
            pipeline.RegisterSink(sink);
            var channel = LogChannel.Create("SelfCheck.Throwing", pipeline);

            for (int i = 0; i < 8; i++)
            {
                // A sink that throws must never propagate into the producer's call site. The guard's
                // whole reason to exist is that a backend defect cannot become a page defect.
                channel.Error("boom");
                pipeline.Pump(16);
            }

            LogPipelineStatistics statistics = pipeline.GetStatistics();
            bool attempted = sink.Attempts > 0;
            bool quarantined = statistics.QuarantinedSinkCount > 0;

            return new CheckResult(
                "pipeline.sink-failure-contained-and-quarantined",
                attempted && quarantined,
                "attempts=" + sink.Attempts + " quarantined=" + statistics.QuarantinedSinkCount
                + " sinkFailures=" + statistics.SinkFailureCount
                + (attempted ? string.Empty : " — NOT EXERCISED"));
        }
        finally
        {
            pipeline.Shutdown();
        }
    }

    /// <summary>
    /// The check that matters most: a producer enqueueing and a frame draining must allocate nothing.
    /// </summary>
    /// <remarks>
    /// The measured region is the realistic frame loop — enqueue a frame's worth of records, then
    /// drain — rather than enqueue-only. Enqueue-only into a queue deep enough to hold the whole run
    /// would exceed the record pool's retention capacity and measure pool misses instead of the
    /// producer path; enqueue-only into a shallow queue would saturate it and measure the drop fast
    /// path. The frame loop is both the honest shape and the one that fits inside the pool, so it is
    /// the shape whose zero is worth asserting.
    /// </remarks>
    private static CheckResult CheckProducerPathAllocatesNothing(int iterations)
    {
        const int FrameSize = 64;
        if (iterations < FrameSize * 16)
        {
            iterations = FrameSize * 16;
        }

        int frames = iterations / FrameSize;
        var sink = new CountingSink();
        LogPipeline pipeline = LogPipelineFactory.CreateSingleThreaded();
        try
        {
            pipeline.RegisterSink(sink);
            var channel = LogChannel.Create("SelfCheck.Allocation", pipeline);

            // Warm up so every pool is populated and no first-use path (pool fill, array growth, JIT)
            // can be mistaken for steady-state allocation.
            for (int f = 0; f < 256; f++)
            {
                for (int i = 0; i < FrameSize; i++)
                {
                    channel.Info(SteadyStateMessage);
                }

                pipeline.Pump(FrameSize);
            }

            long deliveredBefore = sink.Count;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int f = 0; f < frames; f++)
            {
                for (int i = 0; i < FrameSize; i++)
                {
                    channel.Info(SteadyStateMessage);
                }

                pipeline.Pump(FrameSize);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            long delivered = sink.Count - deliveredBefore;
            long expected = (long)frames * FrameSize;

            // Non-vacuity guard. This is the assertion that catches the failure mode this check
            // originally shipped with: an unbound channel enqueues nothing, allocates nothing, and
            // reports a flawless, meaningless zero.
            bool exercised = delivered == expected;
            LogPipelineStatistics statistics = pipeline.GetStatistics();

            return new CheckResult(
                "producer.zero-allocation-enqueue",
                exercised && allocated == 0L,
                "records=" + expected + " delivered=" + delivered
                + " allocated=" + allocated + " bytes"
                + " (pool retained=" + LogMemoryPools.GetStatistics().RetainedLogEvents
                + ", dropped=" + statistics.DroppedMessageCount + ")"
                + (exercised ? string.Empty : " — NOT EXERCISED, the measurement is meaningless"));
        }
        finally
        {
            pipeline.Shutdown();
        }
    }

    /// <summary>
    /// Asserts that the record pool is deep enough to absorb the default queue depth.
    /// </summary>
    /// <remarks>
    /// The pool holds only records that are <i>not</i> queued, so a burst that fills a queue deeper
    /// than the pool's retention capacity leaves nothing pooled and every further acquisition is a
    /// heap allocation on the producer thread. This is a load-dependent, silent failure: it is
    /// invisible in a light run and appears only under the pulse traffic the bounded queue exists to
    /// absorb. Measured with the previous capacity of 4096 against the default depth of 8192, each
    /// full-queue pulse allocated 4032 records / ~315 KB.
    /// </remarks>
    private static CheckResult CheckPoolRetentionCoversQueueDepth()
    {
        int queueDepth = LogPipelineOptions.DefaultMaxQueuedMessages;
        int retention = LogEventPool.RetentionCapacity;
        bool covered = retention >= queueDepth;

        return new CheckResult(
            "pool.retention-covers-default-queue-depth",
            covered,
            "queueDepth=" + queueDepth + " poolRetention=" + retention
            + (covered
                ? string.Empty
                : " — a full-queue pulse cannot return the burst to the pool, so every pulse allocates "
                    + "the shortfall (" + (queueDepth - retention) + " records) on the producer thread"));
    }

    /// <summary>
    /// Validates the handoff counters against each other. Needs a live host snapshot, so it is
    /// skipped when none is supplied.
    /// </summary>
    /// <remarks>
    /// The assertions are deliberately internal-consistency ones rather than performance thresholds:
    /// a threshold would fail on a slow dev machine and pass on a fast one while the defect it was
    /// meant to catch went unnoticed, whereas "you cannot exhaust more frames than you pumped" and
    /// "retained is never above peak" hold on every machine or the bookkeeping is broken. The
    /// pressure figures are reported rather than asserted, because drops and budget exhaustion are
    /// bounds being enforced — not bugs.
    /// </remarks>
    private static CheckResult CheckHandoffCountersConsistent(HandoffCounters? handoff)
    {
        if (!handoff.HasValue)
        {
            return new CheckResult(
                "handoff.counters-consistent",
                false,
                "no host snapshot supplied; run this from the editor dock or pass HandoffCounters "
                + "to measure a live queue",
                skipped: true);
        }

        HandoffCounters counters = handoff.Value;
        int retained = counters.QueuedCount + counters.InFlightCount + counters.ReservedCount;
        bool retainedWithinPeak = retained <= counters.PeakQueuedCount;
        bool pumpBudgetWithinPumps = counters.PumpBudgetExhaustedCount <= counters.PumpFrameCount;
        bool nonNegative = counters.QueuedCount >= 0
            && counters.InFlightCount >= 0
            && counters.ReservedCount >= 0
            && counters.PeakQueuedCount >= 0
            && counters.DroppedMessageCount >= 0
            && counters.DroppedCriticalCount >= 0;

        return new CheckResult(
            "handoff.counters-consistent",
            retainedWithinPeak && pumpBudgetWithinPumps && nonNegative,
            "retained=" + retained + " peak=" + counters.PeakQueuedCount
            + " pumpFrames=" + counters.PumpFrameCount
            + " budgetExhausted=" + counters.PumpBudgetExhaustedCount
            + " dropped=" + counters.DroppedMessageCount
            + " (critical " + counters.DroppedCriticalCount + ")"
            + (counters.PumpBudgetExhaustedCount > 0 ? " — PRESSURE observed" : " — clean"));
    }

    private static CheckResult CheckShutdownDrains()
    {
        const int Expected = 32;
        var sink = new CountingSink();
        LogPipeline pipeline = LogPipelineFactory.CreateSingleThreaded();
        try
        {
            pipeline.RegisterSink(sink);
            var channel = LogChannel.Create("SelfCheck.Shutdown", pipeline);

            for (int i = 0; i < Expected; i++)
            {
                channel.Info("pending");
            }

            LogPipelineShutdownResult result = pipeline.Shutdown(LogFlushMode.Buffered);

            // Shutdown owns the drain: by the time it reports complete, nothing may still be sitting
            // in the queue, because after this point nothing will ever pump it.
            bool drained = result.IsComplete && sink.Count == Expected && result.DroppedMessageCount == 0;

            return new CheckResult(
                "lifecycle.shutdown-drains",
                drained,
                "status=" + result.Status + " delivered=" + sink.Count + "/" + Expected
                + " dropped=" + result.DroppedMessageCount
                + (sink.Count > 0 ? string.Empty : " — NOT EXERCISED"));
        }
        finally
        {
            pipeline.Shutdown();
        }
    }

    /// <summary>
    /// Idle-pool state, reported rather than asserted: a deep warm-up is a deliberate trade (a few
    /// hundred microseconds at boot for a guaranteed allocation-free steady state) and reporting it
    /// makes the trade visible instead of suspicious.
    /// </summary>
    public static string FormatPoolReport()
    {
        LogMemoryPoolStatistics pools = LogMemoryPools.GetStatistics();
        return "logEvents retained=" + pools.RetainedLogEvents
            + " peak=" + pools.PeakRetainedLogEvents
            + " misses=" + pools.LogEventPoolMisses
            + " discards=" + pools.LogEventPoolDiscards
            + " invalidReturns=" + pools.InvalidLogEventReturns
            + " capacity=" + LogEventPool.RetentionCapacity
            + " | builders retained=" + pools.RetainedStringBuilders
            + " peak=" + pools.PeakRetainedStringBuilders
            + " misses=" + pools.StringBuilderPoolMisses
            + " discards=" + pools.StringBuilderPoolDiscards;
    }
}
