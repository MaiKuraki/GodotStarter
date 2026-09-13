using System;
using System.IO;
using Godot;

namespace CycloneGames.Logging.Godot;

/// <summary>
/// Turns absolute <c>[CallerFilePath]</c> values into the shortest portable form, and resolves
/// the log file location.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is much smaller than the Unity original.</b> The Unity package needs a link
/// registry, a generation token, and a 2048-entry ring just to make a source location
/// clickable, because the Unity console only hyperlinks URLs it is explicitly handed. Godot's
/// Output panel already recognises and links <c>res://</c> and <c>user://</c> paths in the form
/// <c>res://Scripts/Foo.cs:42</c>, so the whole feature reduces to "print the project-relative
/// path instead of the machine-absolute one". Removing a subsystem is the best kind of port.
/// </para>
/// <para>
/// <b>Privacy by construction.</b> Anything outside the project and the user directory degrades
/// to a bare file name. A shipped build therefore never writes a build machine's user name or
/// directory layout into a log that a player might attach to a bug report — the same default the
/// Unity package settled on after shipping otherwise.
/// </para>
/// <para>
/// <b>Cached, because the input set is tiny and finite.</b> <c>[CallerFilePath]</c> is a
/// compile-time literal, so the number of distinct inputs in a process equals the number of
/// source files that log — a few hundred at most. One dictionary lookup replaces
/// <c>Path.GetFullPath</c> plus two prefix comparisons per record, which matters because this
/// runs on the pipeline's dispatch path.
/// </para>
/// </remarks>
internal static class LoggingPaths
{
    private const int MaxDisplayPathCacheEntries = 2048;

    private static readonly object SyncRoot = new object();
    private static readonly string[] KeyRing = new string[MaxDisplayPathCacheEntries];
    private static readonly System.Collections.Generic.Dictionary<string, string> DisplayPathBySource =
        new System.Collections.Generic.Dictionary<string, string>(MaxDisplayPathCacheEntries, StringComparer.Ordinal);

    private static int _nextIndex;
    private static string _resRoot;
    private static string _userRoot;
    private static bool _rootsResolved;
    private static StringComparison _pathComparison = StringComparison.Ordinal;

    /// <summary>
    /// Resolves the effective log file path from validated settings, reusing the same guard rails
    /// the Unity bootstrap applies so a misconfigured path fails loudly at boot instead of
    /// silently writing somewhere unexpected.
    /// </summary>
    internal static string ResolveLogFilePath(LoggingSettings settings)
    {
        if (settings.UseUserDataPath)
        {
            ValidatePortableFileName(settings.FileName);
            string root = NormalizeDirectory(ProjectSettings.GlobalizePath("user://"));
            if (string.IsNullOrEmpty(root))
            {
                throw new InvalidOperationException(
                    "Godot could not resolve the user data directory. Set UseUserDataPath to false and "
                    + "supply an absolute CustomFilePath instead.");
            }

            string combined = Path.GetFullPath(Path.Combine(root, settings.FileName));
            string parent = Path.GetDirectoryName(combined);
            if (!PathEquals(root, parent))
            {
                throw new InvalidOperationException(
                    "The log file name must remain directly inside the resolved user data directory.");
            }

            return combined;
        }

        if (!settings.AllowCustomFilePath || string.IsNullOrWhiteSpace(settings.CustomFilePath))
        {
            throw new InvalidOperationException(
                "A custom log path requires AllowCustomFilePath and a non-empty CustomFilePath.");
        }

        if (!Path.IsPathFullyQualified(settings.CustomFilePath))
        {
            // Godot resolves res:// and user:// through GlobalizePath; a Godot-style path is
            // accepted here because it is the path form a Godot developer would naturally type.
            string globalized = ProjectSettings.GlobalizePath(settings.CustomFilePath);
            if (string.IsNullOrEmpty(globalized) || !Path.IsPathFullyQualified(globalized))
            {
                throw new InvalidOperationException(
                    "The custom log path must be a fully-qualified OS path or a res:// / user:// path.");
            }

            return Path.GetFullPath(globalized);
        }

        return Path.GetFullPath(settings.CustomFilePath);
    }

    /// <summary>
    /// Maps a source path to its shortest portable display form:
    /// <c>res://…</c> when inside the project, <c>user://…</c> when inside the user directory,
    /// otherwise just the file name.
    /// </summary>
    internal static string ToDisplayPath(string sourcePath)
    {
        if (string.IsNullOrEmpty(sourcePath))
        {
            return string.Empty;
        }

        string normalized = Normalize(sourcePath);

        lock (SyncRoot)
        {
            if (DisplayPathBySource.TryGetValue(normalized, out string cached))
            {
                return cached;
            }
        }

        string displayPath = ResolveDisplayPath(normalized);

        lock (SyncRoot)
        {
            string previousKey = KeyRing[_nextIndex];
            if (!string.IsNullOrEmpty(previousKey))
            {
                DisplayPathBySource.Remove(previousKey);
            }

            KeyRing[_nextIndex] = normalized;
            DisplayPathBySource[normalized] = displayPath;
            _nextIndex = (_nextIndex + 1) % MaxDisplayPathCacheEntries;
        }

        return displayPath;
    }

    private static string ResolveDisplayPath(string normalizedSourcePath)
    {
        EnsureRootsResolved();

        string resRoot;
        string userRoot;
        StringComparison comparison;
        lock (SyncRoot)
        {
            resRoot = _resRoot;
            userRoot = _userRoot;
            comparison = _pathComparison;
        }

        if (!string.IsNullOrEmpty(resRoot) && IsSameOrChildPath(normalizedSourcePath, resRoot, comparison))
        {
            return "res://" + normalizedSourcePath.Substring(resRoot.Length).TrimStart('/');
        }

        if (!string.IsNullOrEmpty(userRoot) && IsSameOrChildPath(normalizedSourcePath, userRoot, comparison))
        {
            return "user://" + normalizedSourcePath.Substring(userRoot.Length).TrimStart('/');
        }

        return GetFileName(normalizedSourcePath);
    }

    private static void EnsureRootsResolved()
    {
        bool alreadyResolved;
        lock (SyncRoot)
        {
            alreadyResolved = _rootsResolved;
        }

        if (alreadyResolved)
        {
            return;
        }

        string resRoot = NormalizeDirectory(ProjectSettings.GlobalizePath("res://"));
        string userRoot = NormalizeDirectory(ProjectSettings.GlobalizePath("user://"));
        // Windows is the only platform where two differing paths can name the same file, so the
        // comparison stays ordinal everywhere else.
        bool ignoreCase = OS.GetName() == "Windows";

        lock (SyncRoot)
        {
            _resRoot = resRoot;
            _userRoot = userRoot;
            _pathComparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            _rootsResolved = true;
        }
    }

    private static void ValidatePortableFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || Path.IsPathRooted(fileName)
            || fileName == "."
            || fileName == ".."
            || fileName.IndexOf('/') >= 0
            || fileName.IndexOf('\\') >= 0
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException(
                "The log file name must be a portable file name without directory segments.");
        }
    }

    private static bool PathEquals(string left, string right)
    {
        if (string.IsNullOrEmpty(right))
        {
            return false;
        }

        StringComparison comparison = OS.GetName() == "Windows"
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(
            NormalizeDirectory(left),
            NormalizeDirectory(right),
            comparison);
    }

    private static bool IsSameOrChildPath(string candidate, string parent, StringComparison comparison)
    {
        if (string.Equals(candidate, parent, comparison))
        {
            return true;
        }

        return candidate.Length > parent.Length
            && candidate[parent.Length] == '/'
            && candidate.StartsWith(parent, comparison);
    }

    private static string GetFileName(string path)
    {
        int start = 0;
        for (int i = 0; i < path.Length; i++)
        {
            if (path[i] == '/')
            {
                start = i + 1;
            }
        }

        return start < path.Length ? path.Substring(start) : path;
    }

    private static string Normalize(string path)
    {
        return string.IsNullOrEmpty(path) ? string.Empty : path.Replace('\\', '/');
    }

    private static string NormalizeDirectory(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        return path.Replace('\\', '/').TrimEnd('/');
    }
}
