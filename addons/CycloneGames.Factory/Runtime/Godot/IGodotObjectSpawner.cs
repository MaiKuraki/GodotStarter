using global::Godot;

namespace CycloneGames.Factory.Runtime
{
    /// <summary>
    /// Creates Godot objects on the main thread.
    /// </summary>
    /// <remarks>
    /// The Unity original is generic over <c>UnityEngine.Object</c>. Godot's equivalent is
    /// <see cref="GodotObject"/>, but the useful creation origin is always a <see cref="PackedScene"/>:
    /// Godot has no "instantiate this node" call that takes a live node as a template. The two shapes
    /// are kept apart on purpose so a caller cannot pass a live node under the impression it will be
    /// cloned.
    /// </remarks>
    public interface IGodotObjectSpawner : IFactory
    {
        /// <summary>Instantiates <paramref name="origin"/> with no parent.</summary>
        T Create<T>(PackedScene origin) where T : Node;

        /// <summary>Instantiates <paramref name="origin"/> and parents the result to <paramref name="parent"/>.</summary>
        T Create<T>(PackedScene origin, Node parent) where T : Node;
    }
}
