// Additional self-checks that exercise the R3 integration.
//
// Split into its own file so the core harness compiles in a project that has not referenced R3:
// Godot builds one assembly, so an optional integration cannot be conditionally excluded by an
// assembly reference the way Unity's asmdef defineConstraints did.
#if CYCLONEGAMES_EVENTBUS_HAS_R3

using System;
using System.Collections.Generic;
using CycloneGames.EventBus.Core;
using CycloneGames.EventBus.Runtime;
using CycloneGames.EventBus.Runtime.Integrations.R3;
using R3;

namespace CycloneGames.EventBus.Godot
{
    public static partial class EventBusSelfCheck
    {
        static partial void AppendR3Checks(List<CheckResult> results)
        {
            results.Add(R3BridgeDeliversAndUnsubscribes());
            results.Add(FrameProviderAdvancesAndPollsOnce());
        }

        // ---- R3 -------------------------------------------------------------------------------------

        private static CheckResult R3BridgeDeliversAndUnsubscribes()
        {
            var bus = new EventBus<Ping>();
            var received = new List<int>();

            // Bus -> observable: every publish reaches the observer.
            Observable<Ping> observable = bus.ToObservable();
            IDisposable observerSubscription = observable.Subscribe(evt => received.Add(evt.Value));

            bus.Publish(new Ping { Value = 1 });
            bus.Publish(new Ping { Value = 2 });
            bool forwards = received.Count == 2 && received[0] == 1 && received[1] == 2;

            // Disposing the observable subscription detaches it from the bus.
            observerSubscription.Dispose();
            bus.Publish(new Ping { Value = 3 });
            bool detaches = received.Count == 2;

            // Observable -> bus: a Subject drives publishes into the bus, and disposing the returned
            // handle unsubscribes the source.
            var subject = new Subject<Ping>();
            var forwarded = new List<int>();
            bus.Subscribe(evt => forwarded.Add(evt.Value));

            IEventSubscription bridge = bus.SubscribeTo(subject);
            subject.OnNext(new Ping { Value = 4 });
            bool intoBus = forwarded.Count == 1 && forwarded[0] == 4;

            bridge.Dispose();
            subject.OnNext(new Ping { Value = 5 });
            bool bridgeDetached = forwarded.Count == 1 && bridge.IsReleased;

            subject.Dispose();
            bus.Dispose();

            bool ok = forwards && detaches && intoBus && bridgeDetached;
            return new CheckResult(
                "r3.bridge-both-directions",
                ok,
                ok
                    ? "bus<->observable both directions deliver, and disposal detaches each side"
                    : "forwards=" + forwards + " detaches=" + detaches + " intoBus=" + intoBus
                        + " bridgeDetached=" + bridgeDetached);
        }

        private static CheckResult FrameProviderAdvancesAndPollsOnce()
        {
            var provider = new GodotFrameProvider();

            var polled = new List<long>();
            int finishedAt = -1;

            // A work item that stays registered for three frames then reports completion.
            var item = new CountingWorkItem(polled, 3);
            provider.Register(item);

            long startFrame = provider.GetFrameCount();

            provider.Advance();
            provider.Advance();
            provider.Advance();

            bool polledOncePerFrame = polled.Count == 3
                && polled[0] == startFrame + 1
                && polled[1] == startFrame + 2
                && polled[2] == startFrame + 3;

            // The item reported false on its third poll, so it must be gone.
            provider.Advance();
            bool droppedWhenFinished = polled.Count == 3 && provider.RegisteredCount == 0;

            // A re-registration from inside MoveNext must not be polled in the same frame. This is
            // the shape R3's pending delays take, so it is the one behaviour the provider exists to
            // get right.
            var selfRegistering = new SelfRegisteringWorkItem(provider);
            provider.Register(selfRegistering);
            provider.Advance();

            bool noDoublePollInOneFrame = selfRegistering.PollCount == 1
                && provider.RegisteredCount == 1;

            provider.Advance();
            bool polledNextFrame = selfRegistering.PollCount == 2;

            _ = finishedAt;

            bool ok = polledOncePerFrame && droppedWhenFinished && noDoublePollInOneFrame && polledNextFrame;
            return new CheckResult(
                "r3.frame-provider",
                ok,
                ok
                    ? "advances once per call, polls each item once per frame, drops finished items"
                    : "perFrame=" + polledOncePerFrame + " dropped=" + droppedWhenFinished
                        + " selfOnce=" + noDoublePollInOneFrame + " nextFrame=" + polledNextFrame);
        }

        private sealed class CountingWorkItem : IFrameRunnerWorkItem
        {
            private readonly List<long> _polled;
            private readonly int _lifetime;
            private int _remaining;

            public CountingWorkItem(List<long> polled, int lifetime)
            {
                _polled = polled;
                _lifetime = lifetime;
                _remaining = lifetime;
            }

            public bool MoveNext(long frameCount)
            {
                _polled.Add(frameCount);
                _remaining--;
                return _remaining > 0;
            }
        }

        private sealed class SelfRegisteringWorkItem : IFrameRunnerWorkItem
        {
            private readonly FrameProvider _provider;
            private bool _reregistered;

            public SelfRegisteringWorkItem(FrameProvider provider)
            {
                _provider = provider;
            }

            public int PollCount { get; private set; }

            public bool MoveNext(long frameCount)
            {
                PollCount++;

                if (!_reregistered)
                {
                    _reregistered = true;

                    // Re-registering from inside a poll is what R3's pending delays do. The new
                    // registration must be polled next frame, not again in this one.
                    _provider.Register(this);
                    return false;
                }

                return false;
            }
        }
    }
}

#endif