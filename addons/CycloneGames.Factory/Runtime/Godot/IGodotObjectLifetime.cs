using global::Godot;

namespace CycloneGames.Factory.Runtime
{
    /// <summary>
    /// Creates Godot objects and provides their permanent release operation on the main thread.
    /// Release is terminal and must not return the object for reuse.
    /// </summary>
    public interface IGodotObjectLifetime : IGodotObjectSpawner
    {
        /// <summary>
        /// Permanently releases a node. The node represents the instantiated scene that owns it.
        /// </summary>
        /// <param name="instance">The node whose lifetime has ended.</param>
        /// <remarks>
        /// The owner invokes this operation once and does not retry after an exception. An
        /// implementation must make the ownership transition terminal before executing
        /// failure-prone callbacks.
        /// </remarks>
        void Release(Node instance);
    }
}
