namespace CycloneGames.IO.Godot
{
    /// <summary>
    /// Why a Godot location could not be resolved.
    /// </summary>
    /// <remarks>
    /// Deliberately shares the shape of the Unity module's <c>UnityFileUriError</c>: callers moving between
    /// the two engines keep the same classification. One member carries Godot-specific meaning —
    /// <see cref="NotOsPathBacked"/> replaces the Unity-specific <c>UnsupportedScheme</c>, because the
    /// question that actually matters here is not "which URI scheme is this" but "can I use System.IO on
    /// it". Godot's <c>HTTPRequest</c> takes a plain URL directly, so the URI-construction classification
    /// the Unity layer needed does not survive the port.
    /// </remarks>
    public enum GodotFileUriError
    {
        None,

        /// <summary>The supplied path was null, empty, whitespace, or otherwise not a usable path.</summary>
        InvalidPath,

        /// <summary>The location was not a defined <see cref="GodotFileLocation"/> value.</summary>
        InvalidLocation,

        /// <summary>The path resolved outside the trusted root for its location.</summary>
        PathOutsideLocation,

        /// <summary>
        /// The location has no OS path, so <c>SystemFileStore</c> cannot be used on it. This is the expected
        /// result for <see cref="GodotFileLocation.Res"/>, and for URLs.
        /// </summary>
        NotOsPathBacked,

        /// <summary>The location's backing directory could not be obtained from the engine.</summary>
        LocationUnavailable
    }
}
