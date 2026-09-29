using System;
// Godot compiles a project into one assembly, so an optional integration is gated by a preprocessor
// symbol rather than by an asmdef defineConstraint. This body compiles only when the consuming
// project defines CYCLONEGAMES_EVENTBUS_HAS_R3 (see the module README).
#if CYCLONEGAMES_EVENTBUS_HAS_R3

using CycloneGames.EventBus.Runtime;
using global::Godot;
using R3;

namespace CycloneGames.EventBus.Godot
{
    /// <summary>
    /// Godot frame source for R3, replacing what <c>R3.Unity</c> supplies on the Unity side.
    ///
    /// <para>
    /// R3 asks its host for exactly one thing: a <see cref="FrameProvider"/> that counts frames and
    /// hands back work items to poll once per frame. <c>R3.Unity</c> implements that against Unity's
    /// PlayerLoop; Godot's process loop is a different mechanism, so <see cref="GodotFrameProvider"/>
    /// implements R3's contract and this node drives it from <see cref="Node._Process"/>. Every R3
    /// operator that schedules on the frame clock — frame-based throttling,
    /// <c>ObserveOnFrameProvider</c>, <c>DelayFrame</c> — then works in Godot with no engine-specific
    /// R3 package.
    /// </para>
    ///
    /// <para>
    /// The provider and the node are separate types because C# allows one base class and R3 already
    /// owns it: <see cref="FrameProvider"/> is an abstract class, not an interface, so the node cannot
    /// also derive from <see cref="Node"/>. Composition is the only shape that works, and it also lets
    /// the provider be advanced manually under test with no scene tree.
    /// </para>
    ///
    /// Install one at startup; <see cref="ObservableSystem.DefaultFrameProvider"/> is process-global,
    /// so a second live instance would advance frame-dependent operators twice per rendered frame.
    ///
    /// Main-thread only.
    /// </summary>
    public sealed class GodotFrameProvider : FrameProvider
    {
        // The live list and a scratch list of the same capacity are swapped on every Advance. R3
        // re-enters Register from inside MoveNext (a pending delay re-registers itself), so the list
        // being polled must not be the list being appended to. Two alternating buffers keep the walk
        // immune to those appends and stop allocating once the high-water mark is reached.
        private IFrameRunnerWorkItem[] _workItems = Array.Empty<IFrameRunnerWorkItem>();
        private IFrameRunnerWorkItem[] _scratch = Array.Empty<IFrameRunnerWorkItem>();
        private int _count;
        private long _frameCount;

        /// <summary>Frames advanced since construction.</summary>
        public long FrameCount => _frameCount;

        /// <summary>Work items currently registered.</summary>
        public int RegisteredCount => _count;

        public override long GetFrameCount()
        {
            return _frameCount;
        }

        public override void Register(IFrameRunnerWorkItem callback)
        {
            if (callback == null)
            {
                throw new ArgumentNullException(nameof(callback));
            }

            if (_count == _workItems.Length)
            {
                int capacity = _count == 0 ? 4 : _count * 2;
                var grown = new IFrameRunnerWorkItem[capacity];
                Array.Copy(_workItems, grown, _count);
                _workItems = grown;

                // The scratch buffer must be able to hold everything the live one can, including
                // entries appended during a walk.
                if (_scratch.Length < capacity)
                {
                    _scratch = new IFrameRunnerWorkItem[capacity];
                }
            }

            _workItems[_count++] = callback;
        }

        /// <summary>
        /// Advances the frame clock and polls every work item registered at entry exactly once.
        ///
        /// An item that returns false from <see cref="IFrameRunnerWorkItem.MoveNext"/> is finished and
        /// is dropped; the rest are carried to the next frame. Items registered during this frame's
        /// poll are carried too, but only polled next frame — never twice in one.
        /// </summary>
        public void Advance()
        {
            _frameCount++;

            int polled = _count;
            if (polled == 0)
            {
                return;
            }

            IFrameRunnerWorkItem[] current = _workItems;
            IFrameRunnerWorkItem[] survivors = _scratch;
            int survivorCount = 0;

            for (int index = 0; index < polled; index++)
            {
                IFrameRunnerWorkItem item = current[index];
                if (item != null && item.MoveNext(_frameCount))
                {
                    survivors[survivorCount++] = item;
                }
            }

            // Anything registered during the walk sits at or past `polled`; copying it forward keeps it
            // registered without polling it this frame. _count is read afresh because Register may have
            // grown the array and appended while the loop above was running.
            int total = _count;
            for (int index = polled; index < total; index++)
            {
                survivors[survivorCount++] = current[index];
            }

            // Clear the vacated tail: a work item is a reference type, so a stale slot would pin a
            // completed R3 subscription for as long as this provider lives.
            for (int index = survivorCount; index < total; index++)
            {
                survivors[index] = null;
            }

            _workItems = survivors;
            _scratch = current;
            _count = survivorCount;
        }
    }

    /// <summary>
    /// Scene-tree host that installs a <see cref="GodotFrameProvider"/> as R3's default frame source
    /// and advances it once per frame.
    /// </summary>
    [GlobalClass]
    public partial class R3GodotFrameProvider : Node
    {
        private readonly GodotFrameProvider _provider = new GodotFrameProvider();

        // Captured on install so teardown can put back whatever was there. R3's own default is an
        // internal NotSupported provider, so it cannot be reconstructed — only restored.
        private FrameProvider _previousProvider;
        private bool _installed;

        /// <summary>The provider this node advances. Exposed so callers can register or inspect it.</summary>
        public GodotFrameProvider Provider => _provider;

        /// <summary>Frames advanced since this node was installed.</summary>
        public long FrameCount => _provider.FrameCount;

        public override void _Ready()
        {
            _previousProvider = ObservableSystem.DefaultFrameProvider;
            ObservableSystem.DefaultFrameProvider = _provider;
            _installed = true;
        }

        public override void _Process(double delta)
        {
            _provider.Advance();
        }

        public override void _ExitTree()
        {
            // Only restore when this node's provider is still the installed one: a second provider
            // that took over must not be clobbered by this one's teardown.
            if (_installed && ReferenceEquals(ObservableSystem.DefaultFrameProvider, _provider))
            {
                ObservableSystem.DefaultFrameProvider = _previousProvider;
            }

            _installed = false;
            _previousProvider = null;
        }
    }
}
#endif
