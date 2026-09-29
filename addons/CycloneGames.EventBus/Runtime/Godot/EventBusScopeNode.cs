using System;
using CycloneGames.EventBus.Core;
using CycloneGames.EventBus.Runtime;
using global::Godot;

namespace CycloneGames.EventBus.Godot
{
    /// <summary>
    /// Binds a subscription scope to a node's lifetime. Add this to a mode, window, or controller
    /// root and subscribe through <see cref="Scope"/>; every subscription is released when the node
    /// leaves the tree. Main-thread only.
    /// </summary>
    [GlobalClass]
    public partial class EventBusScopeNode : Node
    {
        private readonly SubscriptionScope _scope = new SubscriptionScope();

        public ISubscriptionScope Scope => _scope;

        public IEventSubscription Subscribe<T>(EventBus<T> bus, Action<T> handler) where T : struct
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            return _scope.Add(bus, handler);
        }

        public override void _ExitTree()
        {
            _scope.Dispose();
        }
    }
}
