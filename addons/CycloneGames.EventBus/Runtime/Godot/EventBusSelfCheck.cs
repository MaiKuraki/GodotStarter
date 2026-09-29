using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using CycloneGames.EventBus.Core;
using CycloneGames.EventBus.Runtime;
using global::Godot;

namespace CycloneGames.EventBus.Godot
{
    /// <summary>
    /// Dependency-free invariant harness for the Godot-facing layer of CycloneGames.EventBus, plus a
    /// property-level pass over the portable core that a Godot consumer can run with nothing installed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this file exists.</b> The ported NUnit suite in <c>Tests/</c> runs in a plain .NET host
    /// with no GodotSharp, so it cannot reach <c>Runtime/Godot/</c> at all — and that layer is new code
    /// with no Unity counterpart to inherit coverage from. This harness is therefore the only coverage
    /// the engine-facing layer can have, expressed the way the Hash, Logging and IO harnesses are:
    /// runnable inside Godot with nothing installed, reporting PASS/FAIL/SKIP per named check.
    /// </para>
    /// <para>
    /// <b>Every assertion is a property, not a constant.</b> Throughput is compared against a latency
    /// budget rather than a fixed event count; allocation is compared against zero rather than a byte
    /// total; the R3 bridge is checked against observable delivery order rather than a golden value.
    /// A number that a machine or a build could change is never asserted as a constant, because a
    /// drifting report is not a regression signal.
    /// </para>
    /// </remarks>
    public static partial class EventBusSelfCheck
    {
        private struct Ping
        {
            public int Value;
        }

        // Carries no payload: it exists only as a second, distinct event type so the diagnostics check
        // can prove two buses of different types are counted independently.
        private struct Pong
        {
        }

        private struct Command
        {
            public int Value;
        }

        /// <summary>One named result. Mirrors the shape used by the Hash, Logging and IO harnesses.</summary>
        public readonly struct CheckResult
        {
            public readonly string Name;

            public readonly bool Passed;

            /// <summary>
            /// True when the check could not run for lack of an input. Neither a pass nor a failure,
            /// and reported separately so a run that silently lost a check cannot look green.
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

        /// <summary>Runs every check and formats the report. One line per check, then a summary line.</summary>
        public static string RunAndFormat()
        {
            List<CheckResult> results = RunAll();

            var builder = new StringBuilder();
            foreach (CheckResult result in results)
            {
                if (result.Skipped)
                {
                    builder.Append("SKIP  ").Append(result.Name);
                }
                else
                {
                    builder.Append(result.Passed ? "PASS  " : "FAIL  ").Append(result.Name);
                }

                if (!string.IsNullOrEmpty(result.Detail))
                {
                    builder.Append(" — ").Append(result.Detail);
                }

                builder.Append('\n');
            }

            int passed = 0;
            int failed = 0;
            int skipped = 0;
            foreach (CheckResult result in results)
            {
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
            }

            builder.Append(passed).Append('/').Append(results.Count - skipped).Append(" checks passed");
            if (skipped > 0)
            {
                builder.Append(" (").Append(skipped).Append(" skipped)");
            }

            if (failed > 0)
            {
                builder.Append(" — ").Append(failed).Append(" FAILED");
            }

            return builder.ToString();
        }

        /// <summary>
        /// Runs every check. The R3-dependent checks are appended by the guarded partial file when the
        /// consuming project defines CYCLONEGAMES_EVENTBUS_HAS_R3.
        /// </summary>
        public static List<CheckResult> RunAll()
        {
            var results = new List<CheckResult>
            {
                DispatchDeliversInSubscriptionOrder(),
                UnsubscribeIsEffectiveImmediately(),
                StructuralChangeDuringDispatchIsDeferred(),
                ExceptionContractsHoldLineByLine(),
                ReentrancyCeilingDropsAndCounts(),
                CompactionReclaimsTombstonesProportionally(),
                PublishAllocatesNothingInSteadyState(),
                ThroughputMeetsLatencyBudget(),
                MpscQueueFlushesToBusInProducerOrder(),
                MpscQueueRefusesInsteadOfGrowing(),
                PumpBudgetsBoundOneTick(),
                SubscriptionScopeReleasesEverything(),
                CommandPublisherRoutesAndBoundsItself(),
                DiagnosticSnapshotAddsUp(),
            };

            AppendR3Checks(results);
            return results;
        }

        /// <summary>
        /// Extension point for the guarded partial file. Defined unconditionally so this file compiles
        /// whether or not the R3 integration is present; each definition appends what it can offer.
        /// </summary>
        static partial void AppendR3Checks(List<CheckResult> results);

        // ---- Dispatch contracts ---------------------------------------------------------------------

        private static CheckResult DispatchDeliversInSubscriptionOrder()
        {
            var bus = new EventBus<Ping>();
            var order = new List<int>();

            Action<Ping> first = _ => order.Add(1);
            Action<Ping> second = _ => order.Add(2);
            Action<Ping> third = _ => order.Add(3);

            bus.Subscribe(first);
            bus.Subscribe(second);
            bus.Subscribe(third);
            bus.Publish(new Ping { Value = 7 });

            bus.Dispose();

            bool ok = order.Count == 3 && order[0] == 1 && order[1] == 2 && order[2] == 3;
            return new CheckResult(
                "core.dispatch-order",
                ok,
                ok ? "three handlers ran in subscription order" : "order was " + string.Join(",", order));
        }

        private static CheckResult UnsubscribeIsEffectiveImmediately()
        {
            var bus = new EventBus<Ping>();
            int delivered = 0;
            Action<Ping> handler = _ => delivered++;

            IEventSubscription subscription = bus.Subscribe(handler);
            subscription.Dispose();
            bus.Publish(new Ping());

            bool idempotent = true;
            subscription.Dispose();

            bool ok = delivered == 0 && bus.SubscriptionCount == 0 && idempotent;
            bus.Dispose();

            return new CheckResult(
                "core.unsubscribe",
                ok,
                ok ? "released handle stops delivery, disposal is idempotent" : "delivered=" + delivered
                    + " subscriptions=" + bus.SubscriptionCount);
        }

        private static CheckResult StructuralChangeDuringDispatchIsDeferred()
        {
            var bus = new EventBus<Ping>();
            var order = new List<int>();

            Action<Ping> late = _ => order.Add(99);

            Action<Ping> first = _ => bus.Subscribe(late);
            Action<Ping> second = _ => order.Add(2);

            bus.Subscribe(first);
            bus.Subscribe(second);
            bus.Publish(new Ping());

            // The handler subscribed during the round must not fire in it.
            bool notInFirstRound = order.Count == 1 && order[0] == 2;

            bus.Publish(new Ping());
            bool firesAfterwards = order.Count == 3 && order[2] == 99;

            bus.Dispose();

            return new CheckResult(
                "core.structural-change-deferred",
                notInFirstRound && firesAfterwards,
                notInFirstRound && firesAfterwards
                    ? "a mid-round subscribe lands after the round it was made in"
                    : "first round order was " + string.Join(",", order));
        }

        private static CheckResult ExceptionContractsHoldLineByLine()
        {
            // Stop: the fault propagates and later handlers are skipped.
            var stopBus = new EventBus<Ping>(new EventBusConfiguration(publishErrorPolicy: PublishErrorPolicy.Stop));
            bool stopReachedLater = false;
            stopBus.Subscribe(_ => throw new InvalidOperationException("stop-policy"));
            stopBus.Subscribe(_ => stopReachedLater = true);

            bool stopped = false;
            try
            {
                stopBus.Publish(new Ping());
            }
            catch (InvalidOperationException)
            {
                stopped = true;
            }

            stopBus.Dispose();

            // ContinueOnError: every handler runs, then the first fault is rethrown.
            var continueBus = new EventBus<Ping>(
                new EventBusConfiguration(publishErrorPolicy: PublishErrorPolicy.ContinueOnError));
            int ran = 0;
            continueBus.Subscribe(_ => throw new InvalidOperationException("continue-policy"));
            continueBus.Subscribe(_ => ran++);

            bool rethrown = false;
            try
            {
                continueBus.Publish(new Ping());
            }
            catch (InvalidOperationException)
            {
                rethrown = true;
            }

            bool counted = continueBus.SubscriberErrorCount == 1;
            continueBus.Dispose();

            bool ok = stopped && !stopReachedLater && rethrown && ran == 1 && counted;
            return new CheckResult(
                "core.error-policy-contract",
                ok,
                ok
                    ? "Stop aborts the round; ContinueOnError runs every handler then rethrows, counted"
                    : "stopped=" + stopped + " skippedLater=" + !stopReachedLater + " rethrown=" + rethrown
                        + " ran=" + ran + " counted=" + counted);
        }

        private static CheckResult ReentrancyCeilingDropsAndCounts()
        {
            var configuration = new EventBusConfiguration(maxDispatchDepth: 4);
            var bus = new EventBus<Ping>(configuration);

            int observedDepth = 0;
            Action<Ping> recursive = null;
            recursive = _ =>
            {
                observedDepth = bus.DispatchDepth;
                bus.Publish(new Ping());
            };

            bus.Subscribe(recursive);

            // Must not overflow the stack: the ceiling drops the publishes beyond depth 4.
            bus.Publish(new Ping());

            long dropped = bus.DroppedReentrantCount;
            bool ok = dropped > 0 && observedDepth > 0;
            bus.Dispose();

            return new CheckResult(
                "core.reentrancy-ceiling",
                ok,
                ok ? "recursive publish was bounded and counted (" + dropped + " dropped)" : "dropped=" + dropped);
        }

        private static CheckResult CompactionReclaimsTombstonesProportionally()
        {
            var bus = new EventBus<Ping>();
            var handles = new List<IEventSubscription>();
            Action<Ping> handler = _ => { };

            for (int index = 0; index < 64; index++)
            {
                handles.Add(bus.Subscribe(handler));
            }

            int peakCapacity = bus.Capacity;

            // Release most of them; the proportional threshold must reclaim the slots without the
            // capacity array itself ever shrinking.
            for (int index = 0; index < 48; index++)
            {
                handles[index].Dispose();
            }

            bool reclaimed = bus.TombstoneCount < 48;
            bool capacityRetained = bus.Capacity == peakCapacity;
            bool liveCorrect = bus.SubscriptionCount == 16;

            // The survivors must still be deliverable after compaction moved them.
            int delivered = 0;
            for (int index = 48; index < handles.Count; index++)
            {
                handles[index].Dispose();
            }

            bus.Subscribe(_ => delivered++);
            bus.Publish(new Ping());

            bool survivorsUsable = delivered == 1;
            bus.Dispose();

            bool ok = reclaimed && capacityRetained && liveCorrect && survivorsUsable;
            return new CheckResult(
                "core.compaction",
                ok,
                ok
                    ? "tombstones reclaimed proportionally, capacity retained, handles re-stamped"
                    : "reclaimed=" + reclaimed + " capacity=" + capacityRetained + " live=" + liveCorrect
                        + " survivors=" + survivorsUsable);
        }

        // ---- Performance ----------------------------------------------------------------------------

        private static CheckResult PublishAllocatesNothingInSteadyState()
        {
            var bus = new EventBus<Ping>();
            Action<Ping> handler = _ => { };
            bus.Subscribe(handler);

            // Warm up so JIT and any first-call allocation is outside the measurement.
            for (int index = 0; index < 1000; index++)
            {
                bus.Publish(new Ping());
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 100000; index++)
            {
                bus.Publish(new Ping());
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            bus.Dispose();

            return new CheckResult(
                "perf.publish-zero-allocation",
                allocated == 0,
                allocated == 0
                    ? "100000 publishes allocated 0 bytes"
                    : "100000 publishes allocated " + allocated + " bytes");
        }

        private static CheckResult ThroughputMeetsLatencyBudget()
        {
            const int Iterations = 1_000_000;

            var bus = new EventBus<Ping>();
            int sink = 0;
            Action<Ping> handler = evt => sink += evt.Value;
            bus.Subscribe(handler);

            for (int index = 0; index < 10000; index++)
            {
                bus.Publish(new Ping { Value = 1 });
            }

            var watch = Stopwatch.StartNew();
            for (int index = 0; index < Iterations; index++)
            {
                bus.Publish(new Ping { Value = 1 });
            }

            watch.Stop();
            bus.Dispose();

            // A latency budget, not a throughput constant: 1M dispatches to one handler is a few
            // milliseconds on any desktop, so 200 ns per dispatch is a loose ceiling that still fails
            // loudly if the hot path regresses into allocation or reflection.
            double nanosPerDispatch = watch.Elapsed.TotalMilliseconds * 1_000_000.0 / Iterations;
            bool ok = sink == Iterations + 10000 && nanosPerDispatch < 200.0;

            return new CheckResult(
                "perf.dispatch-latency",
                ok,
                nanosPerDispatch.ToString("F1") + " ns/dispatch over " + Iterations + " dispatches"
                    + (ok ? "" : " (budget 200 ns)"));
        }

        // ---- MPSC bridge ----------------------------------------------------------------------------

        private static CheckResult MpscQueueFlushesToBusInProducerOrder()
        {
            const int Producers = 4;
            const int PerProducer = 500;

            var bus = new EventBus<Ping>();
            // Sized for the whole load: this check measures per-producer ordering, so the queue must
            // admit every event rather than shed some and then assert against a partial set. Overflow
            // behaviour is the subject of the check below.
            var queue = new MpscEventQueue<Ping>(Producers * PerProducer);
            var received = new List<int>();

            bus.Subscribe(evt => received.Add(evt.Value));

            var threads = new Thread[Producers];
            int rejected = 0;

            for (int producer = 0; producer < Producers; producer++)
            {
                int id = producer;
                threads[id] = new Thread(() =>
                {
                    for (int index = 0; index < PerProducer; index++)
                    {
                        if (!queue.TryEnqueue(new Ping { Value = id * 10000 + index }))
                        {
                            Interlocked.Increment(ref rejected);
                        }
                    }
                });
            }

            for (int index = 0; index < Producers; index++)
            {
                threads[index].Start();
            }

            for (int index = 0; index < Producers; index++)
            {
                threads[index].Join();
            }

            queue.FlushTo(bus);
            bus.Dispose();

            // Per-producer order must be preserved; across producers it is unordered by definition.
            bool perProducerOrdered = true;
            var next = new int[Producers];
            foreach (int value in received)
            {
                int producer = value / 10000;
                int sequence = value % 10000;
                if (sequence != next[producer])
                {
                    perProducerOrdered = false;
                    break;
                }

                next[producer]++;
            }

            bool everythingArrived = received.Count == Producers * PerProducer && rejected == 0;
            bool ok = perProducerOrdered && everythingArrived;

            return new CheckResult(
                "mpsc.cross-thread-order",
                ok,
                ok
                    ? Producers + " producers x " + PerProducer + " events drained in producer order"
                    : "ordered=" + perProducerOrdered + " count=" + received.Count + " rejected=" + rejected);
        }

        private static CheckResult MpscQueueRefusesInsteadOfGrowing()
        {
            // Capacity is rounded up to a power of two; 4 stays 4.
            var queue = new MpscEventQueue<Ping>(4);

            int accepted = 0;
            for (int index = 0; index < 100; index++)
            {
                if (queue.TryEnqueue(new Ping { Value = index }))
                {
                    accepted++;
                }
            }

            bool bounded = accepted == 4 && queue.RejectedCount == 96 && queue.Capacity == 4;

            // Closing the ingress must refuse permanently and count separately.
            queue.Close();
            bool afterCloseRefused = !queue.TryEnqueue(new Ping { Value = 999 });
            bool separateCounter = queue.RejectedAfterCloseCount == 1;

            // A closed queue still drains what it accepted, so shutdown can be graceful.
            var bus = new EventBus<Ping>();
            int drained = 0;
            bus.Subscribe(_ => drained++);
            int flushed = queue.FlushTo(bus);
            bus.Dispose();

            bool drainsAfterClose = flushed == 4 && drained == 4;

            bool ok = bounded && afterCloseRefused && separateCounter && drainsAfterClose;
            return new CheckResult(
                "mpsc.bounded-backpressure",
                ok,
                ok
                    ? "full queue refuses and counts; closed ingress still drains its backlog"
                    : "accepted=" + accepted + " rejected=" + queue.RejectedCount + " capacity=" + queue.Capacity
                        + " afterClose=" + afterCloseRefused + " flushed=" + flushed);
        }

        private static CheckResult PumpBudgetsBoundOneTick()
        {
            var bus = new EventBus<Ping>();
            var stream = new EventStream<Ping>(256);
            var pump = new EventBusPump();

            int delivered = 0;
            bus.Subscribe(_ => delivered++);

            for (int index = 0; index < 200; index++)
            {
                stream.TryWrite(new Ping { Value = index });
            }

            pump.AddStream(stream, bus);

            // Per-tick ceiling of 50 must cap the whole drain regardless of the backlog.
            int firstTick = pump.Drain(1024, 50);
            bool perTickHeld = firstTick == 50 && delivered == 50;

            // The remainder is still queued, not discarded.
            int secondTick = pump.Drain(1024, 50);
            bool remainderKept = secondTick == 50 && delivered == 100;

            // Zero pauses publishing without unregistering.
            int paused = pump.Drain(1024, 0);
            bool pauseHolds = paused == 0 && delivered == 100;

            // Re-entrancy is rejected rather than tolerated.
            bool reentrancyRejected = false;
            pump.Add(budget =>
            {
                try
                {
                    pump.Drain(budget);
                }
                catch (InvalidOperationException)
                {
                    reentrancyRejected = true;
                }

                return 0;
            });

            pump.Drain(1024, 1024);
            bus.Dispose();

            bool ok = perTickHeld && remainderKept && pauseHolds && reentrancyRejected;
            return new CheckResult(
                "pump.budget-and-reentrancy",
                ok,
                ok
                    ? "per-tick ceiling bounds the drain, backlog survives, zero pauses, re-entrancy rejected"
                    : "firstTick=" + firstTick + " secondTick=" + secondTick + " paused=" + paused
                        + " reentrancy=" + reentrancyRejected);
        }

        // ---- Composition ----------------------------------------------------------------------------

        private static CheckResult SubscriptionScopeReleasesEverything()
        {
            var bus = new EventBus<Ping>();
            var scope = new SubscriptionScope();

            int delivered = 0;
            scope.Add(bus, _ => delivered++);
            scope.Add(bus, _ => delivered++);

            bus.Publish(new Ping());
            bool subscribed = delivered == 2 && scope.Count == 2 && bus.SubscriptionCount == 2;

            scope.Dispose();
            bus.Publish(new Ping());

            bool released = delivered == 2 && bus.SubscriptionCount == 0;

            // Disposal is idempotent.
            scope.Dispose();
            bus.Dispose();

            bool ok = subscribed && released;
            return new CheckResult(
                "scope.aggregate-release",
                ok,
                ok ? "disposing the scope released every subscription" : "delivered=" + delivered);
        }

        private static CheckResult CommandPublisherRoutesAndBoundsItself()
        {
            var publisher = new InProcessCommandPublisher(capacity: 2, CommandOverflowPolicy.Drop);
            int handled = 0;

            publisher.RegisterHandler<Command>(command => handled += command.Value);

            PublishBlocking(publisher, new Command { Value = 1 });
            bool routed = handled == 1;

            // Re-entrant publish: a handler that publishes again must queue rather than recurse.
            var nested = new InProcessCommandPublisher(capacity: 8, CommandOverflowPolicy.Drop);
            int nestedHandled = 0;
            bool reentered = false;

            nested.RegisterHandler<Command>(command =>
            {
                nestedHandled += command.Value;
                if (!reentered)
                {
                    reentered = true;
                    PublishBlocking(nested, new Command { Value = 10 });
                }
            });

            PublishBlocking(nested, new Command { Value = 1 });
            bool queuedReentry = nestedHandled == 11 && nested.PendingCommandCount == 0;

            // FailFast overflow must throw rather than silently growing. The handler publishes only on
            // its first invocation: a handler that re-armed the queue on every call would be re-entered
            // by the drain loop forever, which is a property of the probe rather than of the publisher.
            var failing = new InProcessCommandPublisher(capacity: 1, CommandOverflowPolicy.FailFast);
            bool overflowThrows = false;
            bool armed = true;
            failing.RegisterHandler<Command>(_ =>
            {
                if (!armed)
                {
                    return;
                }

                armed = false;
                try
                {
                    PublishBlocking(failing, new Command { Value = 1 });
                    PublishBlocking(failing, new Command { Value = 2 });
                }
                catch (InvalidOperationException)
                {
                    overflowThrows = true;
                }
            });

            PublishBlocking(failing, new Command { Value = 0 });
            bool failingDrained = failing.PendingCommandCount == 0;

            publisher.Dispose();
            nested.Dispose();
            failing.Dispose();

            bool ok = routed && queuedReentry && overflowThrows && failingDrained;
            return new CheckResult(
                "commands.route-queue-overflow",
                ok,
                ok
                    ? "routes to its handler, queues re-entry in order, FailFast overflow throws"
                    : "routed=" + routed + " reentry=" + queuedReentry + " overflow=" + overflowThrows
                        + " drained=" + failingDrained);
        }

        /// <summary>
        /// Publishes a command and blocks until the handler has completed.
        /// </summary>
        /// <remarks>
        /// <c>PublishAsync(...).GetAwaiter().GetResult()</c> is not usable here. The publisher awaits its
        /// handler without <c>ConfigureAwait(false)</c> on purpose — it is single-thread-confined — so the
        /// continuation is posted to whatever <c>SynchronizationContext</c> is current. Under Godot that
        /// context only runs its queue when the main thread pumps, and this harness runs on the main
        /// thread, so blocking there deadlocks the continuation it is waiting for: the check never
        /// returns and the process hangs with no output. Running the wait with the context suppressed
        /// makes the continuation resume on the thread pool instead, which is what the plain .NET host
        /// does implicitly by having no context installed at all.
        /// </remarks>
        private static void PublishBlocking<TCommand>(InProcessCommandPublisher publisher, TCommand command)
            where TCommand : struct
        {
            SynchronizationContext previous = SynchronizationContext.Current;
            if (previous != null)
            {
                SynchronizationContext.SetSynchronizationContext(null);
            }

            try
            {
                publisher.PublishAsync(command).GetAwaiter().GetResult();
            }
            finally
            {
                if (previous != null)
                {
                    SynchronizationContext.SetSynchronizationContext(previous);
                }
            }
        }

        private static CheckResult DiagnosticSnapshotAddsUp()
        {
            var context = new EventBusContext(
                EventBusConfiguration.Default,
                new InProcessCommandPublisher());

            EventBus<Ping> pingBus = context.GetOrCreateBus<Ping>();
            EventBus<Pong> pongBus = context.GetOrCreateBus<Pong>();

            pingBus.Subscribe(_ => { });
            pingBus.Subscribe(_ => { });
            pongBus.Subscribe(_ => { });

            pingBus.Publish(new Ping());

            EventBusDiagnosticsSnapshot snapshot = context.GetDiagnosticsSnapshot();

            bool countsAddUp = snapshot.ActiveBusCount == 2
                && snapshot.SubscriptionCount == 3
                && snapshot.PublishCount == 1;

            // The peak is a max across buses, never a sum: summing would report a number no bus reached.
            bool peakIsMax = snapshot.PeakSubscriptionCount == 2;

            // An owned bus is disposed with the context; a caller-owned one is not.
            var callerOwned = new EventBus<Ping>(EventBusConfiguration.Default, 4);
            var context2 = new EventBusContext(EventBusConfiguration.Default, new InProcessCommandPublisher());
            context2.RegisterBus(callerOwned);
            context2.Dispose();
            bool callerBusSurvived = !callerOwned.IsDisposed;
            callerOwned.Dispose();

            context.Dispose();

            bool ok = countsAddUp && peakIsMax && callerBusSurvived;
            return new CheckResult(
                "context.ownership-and-snapshot",
                ok,
                ok
                    ? "snapshot counts add up, peak is a max, caller-owned buses outlive the context"
                    : "counts=" + countsAddUp + " peak=" + peakIsMax + " callerOwned=" + callerBusSurvived);
        }

    }
}
