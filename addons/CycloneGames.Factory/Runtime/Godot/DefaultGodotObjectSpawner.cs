using System;
using global::Godot;

namespace CycloneGames.Factory.Runtime
{
    /// <summary>
    /// A default implementation of <see cref="IGodotObjectLifetime"/> backed by Godot scene APIs.
    /// </summary>
    /// <remarks>
    /// The Unity original keys its Edit/Play destruction split off <c>Application.isPlaying</c>. Godot
    /// has no equivalent flag: <see cref="Node.QueueFree"/> is deferred to the end of the frame and
    /// performs deferred deletion from any thread, so it is correct in the editor and at runtime
    /// alike. That removes the entire branch rather than translating it.
    /// </remarks>
    public sealed class DefaultGodotObjectSpawner : IGodotObjectLifetime
    {
        public T Create<T>(PackedScene origin) where T : Node
        {
            return Instantiate<T>(origin, null);
        }

        public T Create<T>(PackedScene origin, Node parent) where T : Node
        {
            if (parent == null)
            {
                throw new ArgumentNullException(nameof(parent));
            }

            return Instantiate<T>(origin, parent);
        }

        /// <summary>
        /// Permanently releases an instance previously produced by <see cref="Create{T}(PackedScene)"/>.
        /// </summary>
        /// <remarks>
        /// <see cref="Node.QueueFree"/> is used rather than <c>Free()</c>: the release path can run from
        /// a pool callback, and freeing a node synchronously while the tree is mid-traversal is the one
        /// shape Godot rejects. Deferring the delete is what makes this safe; the node is already
        /// detached from its pool, so nothing can hand it back out in the interval.
        /// </remarks>
        public void Release(Node instance)
        {
            if (instance == null)
            {
                return;
            }

            if (GodotObject.IsInstanceValid(instance))
            {
                instance.QueueFree();
            }
        }

        private static T Instantiate<T>(PackedScene origin, Node parent) where T : Node
        {
            if (origin == null)
            {
                throw new ArgumentNullException(nameof(origin));
            }

            Node node = origin.Instantiate();
            if (node == null)
            {
                throw new InvalidOperationException(
                    $"The packed scene failed to instantiate (instantiate returns null when the source scene has no root node).");
            }

            if (node is not T typed)
            {
                // The scene graph is data, so a root of the wrong type is a wiring mistake that would
                // otherwise surface much later as an InvalidCastException inside a pool callback.
                node.Free();
                throw new InvalidOperationException(
                    $"Packed scene root is {node.GetType().Name}, but {typeof(T).Name} was requested.");
            }

            parent?.AddChild(typed);
            return typed;
        }
    }
}
