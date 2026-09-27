namespace CycloneGames.IO.Godot
{
    /// <summary>
    /// The Godot virtual locations a path can be resolved against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the counterpart of the Unity module's <c>UnityFileLocation</c>, but the members are not
    /// translations of it. Unity needed <c>StreamingAssets</c> as a distinct entry because on Android and
    /// WebGL that content lives inside a compressed archive and cannot be reached through the filesystem at
    /// all, which is why the Unity layer had to build <c>jar:file://</c> URIs. Godot has no such split:
    /// <see cref="Res"/> is read through <c>FileAccess</c>, which already understands the virtual
    /// filesystem, so a location that exists only to work around archive packaging is not needed here.
    /// </para>
    /// <para>
    /// The distinction that <i>does</i> matter on Godot is whether a location is backed by a real OS path,
    /// because that decides whether the engine-free <c>SystemFileStore</c> — with its bounded reads, atomic
    /// commits, retry, and streaming — can be used on it at all. See
    /// <see cref="GodotFilePaths.IsOsPathBacked"/>.
    /// </para>
    /// </remarks>
    public enum GodotFileLocation
    {
        /// <summary>
        /// <c>res://</c> — project content. Readable everywhere through <c>FileAccess</c>. In an exported
        /// build this content lives inside the PCK and is <b>not writable</b>, and it has no OS path.
        /// </summary>
        Res,

        /// <summary>
        /// <c>user://</c> — the per-project user directory (<c>OS.GetUserDataDir()</c>). A real, writable
        /// filesystem directory on every Godot platform, so the full <c>SystemFileStore</c> capability set
        /// applies here. This is where saves, caches, and downloaded content belong.
        /// </summary>
        UserData,

        /// <summary>
        /// An already-absolute OS path, or a URL. A rooted OS path resolves to itself; a URL is not a path
        /// and is rejected by <see cref="GodotFilePaths.TryResolveOsPath"/> rather than being coerced.
        /// </summary>
        AbsolutePathOrUri
    }
}
