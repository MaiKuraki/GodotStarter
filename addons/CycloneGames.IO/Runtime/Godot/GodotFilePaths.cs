using System;
using System.IO;

namespace CycloneGames.IO.Godot
{
    /// <summary>
    /// Resolves a <see cref="GodotFileLocation"/> plus a relative path into either a Godot virtual path
    /// (for <c>FileAccess</c>) or an OS path (for <c>SystemFileStore</c> and <c>System.IO</c>).
    /// </summary>
    /// <remarks>
    /// res:// has no OS path in an exported build: the content lives in the PCK. GlobalizePath happens to
    /// return a usable path in the editor, which is why this must fail loudly instead of returning a path
    /// that only works while developing. user:// resolves through OS.GetUserDataDir and is fully backed by
    /// the filesystem, so SystemFileStore applies there.
    /// </remarks>
    public static class GodotFilePaths
    {
        public const string ResScheme = "res://";
        public const string UserScheme = "user://";

        /// <summary>
        /// True when a location is backed by a real filesystem path, meaning the full
        /// <c>SystemFileStore</c> capability set — bounded reads, atomic commits, streaming, retry — applies.
        /// </summary>
        public static bool IsOsPathBacked(GodotFileLocation location)
        {
            return location != GodotFileLocation.Res;
        }

        /// <summary>
        /// Builds the Godot virtual path for a location and a validated relative path, for use with
        /// <c>FileAccess</c>. Relative input is normalized and sandboxed, so rooted paths, dot segments,
        /// control characters, and non-portable filename characters are rejected rather than joined.
        /// </summary>
        public static string Combine(GodotFileLocation location, string relativePath)
        {
            string normalized = FilePathSandbox.NormalizeRelativePath(relativePath).Replace('\\', '/');

            switch (location)
            {
                case GodotFileLocation.Res:
                    return ResScheme + normalized;
                case GodotFileLocation.UserData:
                    return UserScheme + normalized;
                case GodotFileLocation.AbsolutePathOrUri:
                    return relativePath;
                default:
                    throw new ArgumentOutOfRangeException(nameof(location));
            }
        }

        /// <summary>
        /// Resolves a location and relative path to an OS path. Returns false with
        /// <see cref="GodotFileUriError.NotOsPathBacked"/> for <c>res://</c> and for URLs, rather than
        /// returning a path that only works in the editor.
        /// </summary>
        public static bool TryResolveOsPath(
            GodotFileLocation location,
            string relativePath,
            out string osPath,
            out GodotFileUriError error)
        {
            osPath = null;
            error = GodotFileUriError.None;

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                error = GodotFileUriError.InvalidPath;
                return false;
            }

            switch (location)
            {
                case GodotFileLocation.Res:
                    error = GodotFileUriError.NotOsPathBacked;
                    return false;

                case GodotFileLocation.UserData:
                    return TryResolveUnderRoot(
                        GetUserDataRoot(),
                        relativePath,
                        out osPath,
                        out error);

                case GodotFileLocation.AbsolutePathOrUri:
                    return TryResolveAbsolute(relativePath, out osPath, out error);

                default:
                    error = GodotFileUriError.InvalidLocation;
                    return false;
            }
        }

        /// <summary>
        /// Resolves to an OS path, throwing the exception that matches the reported
        /// <see cref="GodotFileUriError"/>.
        /// </summary>
        public static string ResolveOsPath(GodotFileLocation location, string relativePath)
        {
            if (TryResolveOsPath(location, relativePath, out string osPath, out GodotFileUriError error))
            {
                return osPath;
            }

            throw CreateException(location, relativePath, error);
        }

        private static bool TryResolveUnderRoot(
            string root,
            string relativePath,
            out string osPath,
            out GodotFileUriError error)
        {
            osPath = null;

            if (string.IsNullOrWhiteSpace(root))
            {
                error = GodotFileUriError.LocationUnavailable;
                return false;
            }

            try
            {
                osPath = new FilePathSandbox(root).Resolve(relativePath);
                error = GodotFileUriError.None;
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                error = GodotFileUriError.PathOutsideLocation;
                return false;
            }
            catch (ArgumentException)
            {
                error = GodotFileUriError.InvalidPath;
                return false;
            }
            catch (IOException)
            {
                error = GodotFileUriError.InvalidPath;
                return false;
            }
            catch (NotSupportedException)
            {
                error = GodotFileUriError.InvalidPath;
                return false;
            }
        }

        private static bool TryResolveAbsolute(
            string pathOrUri,
            out string osPath,
            out GodotFileUriError error)
        {
            osPath = null;

            if (Path.IsPathRooted(pathOrUri))
            {
                osPath = Path.GetFullPath(pathOrUri);
                error = GodotFileUriError.None;
                return true;
            }

            if (Uri.TryCreate(pathOrUri, UriKind.Absolute, out Uri _))
            {
                error = GodotFileUriError.NotOsPathBacked;
                return false;
            }

            error = GodotFileUriError.InvalidPath;
            return false;
        }

        private static string GetUserDataRoot()
        {
            return global::Godot.OS.GetUserDataDir();
        }

        private static Exception CreateException(
            GodotFileLocation location,
            string relativePath,
            GodotFileUriError error)
        {
            switch (error)
            {
                case GodotFileUriError.PathOutsideLocation:
                    return new UnauthorizedAccessException(
                        $"Path '{relativePath}' resolves outside {location}.");

                case GodotFileUriError.NotOsPathBacked:
                    return new NotSupportedException(
                        $"{location} is not backed by an OS path. Read it with FileAccess (or GodotFileStore) "
                        + "instead of SystemFileStore; res:// is read-only in an exported build and has no "
                        + "filesystem path, even though the editor will hand out one.");

                case GodotFileUriError.LocationUnavailable:
                    return new InvalidOperationException(
                        $"Godot file location {location} is unavailable.");

                case GodotFileUriError.InvalidLocation:
                    return new ArgumentOutOfRangeException(nameof(location));

                default:
                    return new ArgumentException(
                        $"Path '{relativePath}' is invalid for {location}.",
                        nameof(relativePath));
            }
        }
    }
}
