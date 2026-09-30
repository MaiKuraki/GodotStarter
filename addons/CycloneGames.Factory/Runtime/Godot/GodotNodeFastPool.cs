using global::Godot;

namespace CycloneGames.Factory.Runtime
{
    /// <summary>
    /// A specialized <see cref="FastObjectPool{T}"/> for Godot nodes.
    /// Handles instantiation, visibility, and engine release during permanent cleanup.
    /// </summary>
    /// <remarks>
    /// The Unity original is <c>MonoFastPool&lt;T&gt;</c> where <c>T : Component</c>. Two engine facts
    /// change the shape of this adapter rather than just its type names:
    ///
    /// Godot has no per-node "active" flag that also stops processing. <c>Visible</c> is a
    /// <see cref="CanvasItem"/> concern, so it is applied only when the node actually is one; a
    /// <see cref="Node3D"/> or a plain <see cref="Node"/> has no visibility to toggle and must instead
    /// stop processing. Both are applied where they exist.
    ///
    /// Godot nodes are not refcounted and cannot be resurrected, so the pool must never hand back a
    /// node whose deletion was merely queued. Release is routed through
    /// <see cref="IGodotObjectLifetime.Release"/>, which defers the delete to the end of the frame
    /// after the pool has already dropped ownership.
    /// </remarks>
    public sealed class GodotNodeFastPool<T> : FastObjectPool<T> where T : Node
    {
        private readonly IGodotObjectLifetime _lifetime;
        private readonly PackedScene _scene;
        private readonly Node _root;
        private readonly bool _autoSetActive;

        public GodotNodeFastPool(
            PackedScene scene,
            int initialCapacity = 0,
            Node root = null,
            bool autoSetActive = true,
            int maxCapacity = -1)
            : this(
                new DefaultGodotObjectSpawner(),
                scene,
                new PoolCapacitySettings(initialCapacity, maxCapacity),
                root,
                autoSetActive)
        {
        }

        public GodotNodeFastPool(
            PackedScene scene,
            PoolCapacitySettings capacitySettings,
            Node root = null,
            bool autoSetActive = true)
            : this(new DefaultGodotObjectSpawner(), scene, capacitySettings, root, autoSetActive)
        {
        }

        public GodotNodeFastPool(
            IGodotObjectLifetime lifetime,
            PackedScene scene,
            PoolCapacitySettings capacitySettings,
            Node root = null,
            bool autoSetActive = true)
            : base(capacitySettings, deferInitialPrewarm: true)
        {
            _lifetime = lifetime ?? throw new System.ArgumentNullException(nameof(lifetime));
            _scene = scene != null
                ? scene
                : throw new System.ArgumentNullException(nameof(scene));
            _root = root;
            _autoSetActive = autoSetActive;

            if (capacitySettings.SoftCapacity > 0)
            {
                Prewarm(capacitySettings.SoftCapacity);
            }
        }

        protected override T CreateNew()
        {
            if (_scene == null)
            {
                throw new System.InvalidOperationException(
                    "Packed scene has been released. The pool cannot create new items.");
            }

            T instance = _root != null
                ? _lifetime.Create<T>(_scene, _root)
                : _lifetime.Create<T>(_scene);

            if (_autoSetActive)
            {
                Deactivate(instance);
            }

            return instance;
        }

        protected override void OnSpawn(T item)
        {
            if (_autoSetActive)
            {
                Activate(item);
            }
        }

        protected override void OnDespawn(T item)
        {
            if (_autoSetActive)
            {
                Deactivate(item);
            }

            if (_root != null && GodotObject.IsInstanceValid(item) && item.GetParent() != _root)
            {
                item.Reparent(_root, keepGlobalTransform: false);
            }
        }

        protected override bool IsValid(T item)
        {
            return item != null && GodotObject.IsInstanceValid(item) && !item.IsQueuedForDeletion();
        }

        protected override void DestroyItem(T item)
        {
            if (item != null && GodotObject.IsInstanceValid(item))
            {
                _lifetime.Release(item);
            }

            base.DestroyItem(item);
        }

        private static void Activate(T item)
        {
            SetVisible(item, true);
            item.SetProcess(true);
            item.SetPhysicsProcess(true);
        }

        private static void Deactivate(T item)
        {
            SetVisible(item, false);
            item.SetProcess(false);
            item.SetPhysicsProcess(false);
        }

        /// <summary>
        /// Applies the engine's visibility flag where the node actually has one.
        /// </summary>
        /// <remarks>
        /// <see cref="Node3D"/> is NOT a <see cref="CanvasItem"/>: 2D/UI and 3D are separate branches,
        /// and only the 2D branch exposes <c>Visible</c>. Handling just <see cref="CanvasItem"/> would
        /// leave every 3D node permanently visible, because the type test would silently fail and
        /// nothing would be set. Both branches are covered here.
        /// </remarks>
        private static void SetVisible(T item, bool visible)
        {
            switch (item)
            {
                case CanvasItem canvasItem:
                    canvasItem.Visible = visible;
                    break;
                case Node3D node3D:
                    node3D.Visible = visible;
                    break;
            }
        }
    }
}
