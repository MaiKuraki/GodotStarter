// Ported from CycloneGames.EventBus.Tests.Integrations (Unity). Keep both repositories in lockstep; see Modules/CycloneGames.EventBus/README.md.
// Godot has no assembly definitions, so there is no way to keep a test assembly out of a consuming
// project build. These fixtures therefore compile only when the test host defines
// CYCLONEGAMES_EVENTBUS_TESTS; Tools/EventBusCheck does, and a consuming Godot project does not.
#if CYCLONEGAMES_EVENTBUS_TESTS

using System;
using System.Collections.Generic;
using CycloneGames.EventBus.Core;
using CycloneGames.EventBus.Runtime.Integrations.R3;
using NUnit.Framework;
using R3;

namespace CycloneGames.EventBus.Tests.Integrations
{
    /// <summary>
    /// Coverage for the R3 adapter. Both directions are thin, so what matters here is teardown: a
    /// bridge that leaves a handler on the bus leaks one subscription per binding, and the leak is
    /// invisible until the binding is created every frame.
    /// </summary>
    [TestFixture]
    public class EventBusR3IntegrationTests
    {
        private struct Ping
        {
            public int Value;
        }

        [Test]
        public void ToObservable_ForwardsPublishedEventsInOrder()
        {
            var bus = new EventBus<Ping>();
            var received = new List<int>();

            using (bus.ToObservable().Subscribe(ping => received.Add(ping.Value)))
            {
                bus.Publish(new Ping { Value = 1 });
                bus.Publish(new Ping { Value = 2 });
            }

            CollectionAssert.AreEqual(new[] { 1, 2 }, received);
        }

        [Test]
        public void ToObservable_Disposal_UnsubscribesFromBus()
        {
            var bus = new EventBus<Ping>();
            int count = 0;

            IDisposable subscription = bus.ToObservable().Subscribe(_ => count++);
            Assert.AreEqual(1, bus.SubscriptionCount);

            subscription.Dispose();

            // The whole point of the bridge: disposing the observable must not leave a handler behind.
            Assert.AreEqual(0, bus.SubscriptionCount);
            bus.Publish(new Ping { Value = 1 });
            Assert.AreEqual(0, count);
        }

        [Test]
        public void SubscribeTo_PublishesSourceValuesIntoBus()
        {
            var bus = new EventBus<Ping>();
            var received = new List<int>();
            bus.Subscribe(ping => received.Add(ping.Value));

            var subject = new Subject<Ping>();
            IEventSubscription subscription = bus.SubscribeTo(subject);

            subject.OnNext(new Ping { Value = 7 });
            CollectionAssert.AreEqual(new[] { 7 }, received);

            subscription.Dispose();
            subject.OnNext(new Ping { Value = 8 });

            // Disposing releases the R3 subscription, so the source stops feeding the bus.
            CollectionAssert.AreEqual(new[] { 7 }, received);
        }

        [Test]
        public void SubscribeTo_WrapsAnR3SubscriptionNotABusHandler()
        {
            var bus = new EventBus<Ping>();
            var subject = new Subject<Ping>();

            IEventSubscription subscription = bus.SubscribeTo(subject);

            // The handle wraps an R3 subscription; the bus itself has no handler to remove.
            Assert.AreEqual(0, bus.SubscriptionCount);
            Assert.IsFalse(subscription.IsReleased);

            subscription.Dispose();
            Assert.IsTrue(subscription.IsReleased);
        }
    }
}

#endif
