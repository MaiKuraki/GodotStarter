using System;
using global::Godot;

namespace CycloneGames.Factory.Runtime
{
    /// <summary>
    /// Creates inactive instances from a <see cref="PackedScene"/> so the pool controls activation.
    /// </summary>
    /// <remarks>
    /// The Unity original is <c>MonoPrefabFactory&lt;T&gt;</c> where <c>T : MonoBehaviour</c>. Godot's
    /// unit of instantiation is a <see cref="Node"/>, so the constraint follows the engine rather than
    /// the component model.
    /// </remarks>
    public sealed class PackedSceneFactory<T> : IFactory<T> where T : Node
    {
        private readonly IGodotObjectSpawner _spawner;
        private readonly PackedScene _scene;
        private readonly Node _parent;

        public PackedSceneFactory(IGodotObjectSpawner spawner, PackedScene scene, Node parent = null)
        {
            _spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
            _scene = scene != null ? scene : throw new ArgumentNullException(nameof(scene));
            _parent = parent;
        }

        public T Create()
        {
            if (_scene == null)
            {
                throw new InvalidOperationException("Packed scene is null. The factory cannot create an instance from a null scene.");
            }

            T instance = _parent != null
                ? _spawner.Create<T>(_scene, _parent)
                : _spawner.Create<T>(_scene);

            if (instance == null)
            {
                throw new InvalidOperationException(
                    $"The Godot object spawner returned null for scene {typeof(T).Name}.");
            }

            instance.SetProcess(false);
            instance.SetPhysicsProcess(false);
            return instance;
        }
    }
}
