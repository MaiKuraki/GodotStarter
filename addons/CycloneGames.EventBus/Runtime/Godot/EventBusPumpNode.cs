using System;
using CycloneGames.EventBus.Runtime;
using global::Godot;

namespace CycloneGames.EventBus.Godot
{
    /// <summary>
    /// Godot host for an <see cref="EventBusPump"/>. Add one node to the scene tree, register the
    /// queues and streams that need draining, and every buffered event enters its bus at a known
    /// point in the frame with a known ceiling.
    ///
    /// Runs early in the frame. Cross-thread events that arrived since the last tick are delivered
    /// before gameplay <c>_Process</c> runs, so gameplay reads current state in the same frame rather
    /// than lagging one behind. Godot has no execution-order attribute, so the priority is applied
    /// through <see cref="Node.ProcessPriority"/> instead; change it on the node if the game wants
    /// the opposite.
    ///
    /// Two budgets are exposed: the per-source one bounds how much of a single backlog a frame may
    /// spend, the per-frame one bounds the whole drain. Only the second bounds the frame, because the
    /// first multiplies by the number of registered sources.
    ///
    /// Main-thread only, like everything it drives.
    /// </summary>
    [GlobalClass]
    public partial class EventBusPumpNode : Node
    {
        /// <summary>
        /// Matches the Unity host's <c>[DefaultExecutionOrder(-500)]</c>.
        /// </summary>
        public const int EarlyProcessPriority = -500;

        private const int DefaultMaxEventsPerTargetPerFrame = 1024;
        private const int DefaultMaxEventsPerFrame = 8192;

        private readonly EventBusPump _pump = new EventBusPump();

        private int _maxEventsPerTargetPerFrame = DefaultMaxEventsPerTargetPerFrame;
        private int _maxEventsPerFrame = DefaultMaxEventsPerFrame;
        private bool _pumpingEnabled = true;

        /// <summary>The pump this node drives. Register sources here during setup.</summary>
        public EventBusPump Pump => _pump;

        /// <summary>
        /// Per-source, per-frame publish ceiling. Tunable at runtime so a build can lower it on
        /// mobile hardware without a code change. Negative values collapse to zero.
        /// </summary>
        public int MaxEventsPerTargetPerFrame
        {
            get => _maxEventsPerTargetPerFrame;
            set => _maxEventsPerTargetPerFrame = ClampBudget(value);
        }

        /// <summary>
        /// Whole-drain, per-frame publish ceiling across every registered source. Zero or negative
        /// means no additional cap beyond the per-source budget. Tunable at runtime so a build can
        /// lower it on mobile hardware without a code change.
        /// </summary>
        public int MaxEventsPerFrame
        {
            get => _maxEventsPerFrame;
            set => _maxEventsPerFrame = value;
        }

        /// <summary>Whether the pump publishes. Disabling leaves registrations intact.</summary>
        public bool PumpingEnabled
        {
            get => _pumpingEnabled;
            set => _pumpingEnabled = value;
        }

        /// <summary>
        /// Clamps the per-source budget to its documented domain: 0 pauses publishing, any positive
        /// value is the budget, negatives collapse to 0.
        /// </summary>
        public static int ClampBudget(int value)
        {
            return EventBusPump.ClampBudget(value);
        }

        /// <summary>
        /// Maps the configured per-frame budget onto the pump parameter. Zero and negative mean "no
        /// additional cap" rather than "spend nothing": a scene authored against an older revision
        /// carries zero, and reading that as a zero budget would silently stop every buffered event
        /// from being delivered. Pausing is <see cref="PumpingEnabled"/>, so this needs no pause value.
        /// </summary>
        public static int ResolveFrameBudget(int value)
        {
            return EventBusPump.ResolveFrameBudget(value);
        }

        public override void _Ready()
        {
            // The priority is applied in code rather than left to the scene file: a node attached to
            // a bootstrap scene must run early regardless of where it was instanced.
            ProcessPriority = EarlyProcessPriority;
        }

        public override void _Process(double delta)
        {
            if (!_pumpingEnabled)
            {
                return;
            }

            _pump.Drain(
                ClampBudget(_maxEventsPerTargetPerFrame),
                ResolveFrameBudget(_maxEventsPerFrame));
        }

        public override void _ExitTree()
        {
            _pump.Clear();
        }
    }
}
