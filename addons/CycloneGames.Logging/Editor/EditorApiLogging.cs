#if TOOLS
using Godot;

namespace CycloneGames.Logging.Godot.Editor;

/// <summary>
/// The only place where the addon's C# code touches Godot's editor API.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a funnel.</b> Godot's editor API is the part of the engine most likely to change shape
/// between releases, and it is also the part this addon uses least. Routing every call through one
/// file means an editor API change is a single-file edit, and it makes the addon's editor surface
/// reviewable at a glance: everything below is the complete list.
/// </para>
/// <para>
/// <b>Null-tolerant by contract.</b> During plugin reload, script compilation, and export, the
/// editor singletons can be momentarily absent. Every member here returns null rather than
/// throwing, and callers are required to tolerate null — a logging addon that breaks the editor
/// when the editor is mid-reload is worse than one with no panel at all.
/// </para>
/// <para>
/// <b>Why the guard is <c>#if TOOLS</c> and not a runtime check.</b> <c>EditorInterface</c> lives
/// in the editor-only assembly. Referencing it from code that also compiles into an exported game
/// is a load-time failure, not a branch that gets skipped — the same reason the Godot25D module
/// keeps its editor code behind the same guard and keeps its plugin entry point in GDScript.
/// </para>
/// </remarks>
internal static class EditorApiLogging
{
    /// <summary>
    /// True when an editor context exists. Safe to call from a game run: it references no
    /// editor-only type.
    /// </summary>
    internal static bool Available => Engine.IsEditorHint();

    /// <summary>The editor singleton, or null when there is no editor context.</summary>
    internal static EditorInterface Interface => global::Godot.EditorInterface.Singleton;

    /// <summary>
    /// True when the engine is an editor build, even while the game is playing from it. This is
    /// the difference between "the editor UI exists" (<see cref="Available"/>) and "engine
    /// diagnostics are being recorded", and it is what decides whether a record's source path is
    /// worth retaining for a clickable backtrace.
    /// </summary>
    internal static bool IsEditorBuild => OS.HasFeature("editor");
}
#endif
