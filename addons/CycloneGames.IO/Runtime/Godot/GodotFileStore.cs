using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CycloneGames.IO.Godot
{
    /// <summary>
    /// An <see cref="IFileStore"/> that accepts Godot virtual paths: bounded reads work on
    /// <c>res://</c> (through <c>FileAccess</c>) as well as on <c>user://</c> and OS paths (through the
    /// engine-free <c>SystemFileStore</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists at all, given that <c>SystemFileStore</c> is already complete.</b> Because the
    /// bounded read is a safety property and <c>res://</c> is where it would otherwise be lost.
    /// <c>FileAccess.GetBuffer(length)</c> allocates whatever the file happens to be: a hostile or merely
    /// corrupt manifest inside the PCK would be read in full, with no ceiling. Wrapping <c>res://</c> in the
    /// same contract means config, mod manifests, and any other packaged content are read under an explicit
    /// allocation limit, exactly like a file on disk.
    /// </para>
    /// <para>
    /// <b>Why it implements only <c>IFileStore</c>.</b> The other two contracts cannot be honoured on
    /// <c>res://</c>: it is not writable, so atomic commits and writable streams have nothing to operate on.
    /// Implementing them and throwing for half the inputs would be worse than not implementing them — an
    /// optional capability should not advertise itself where it structurally cannot exist. For atomic
    /// commits, streaming, retry, and comparison on writable storage, resolve a <c>user://</c> path with
    /// <see cref="GodotFilePaths.ResolveOsPath"/> and use <c>SystemFileStore.Default</c> directly:
    /// </para>
    /// <code>
    /// string savePath = GodotFilePaths.ResolveOsPath(GodotFileLocation.UserData, "save.json");
    /// SystemFileStore.Default.WriteTextAtomically(savePath, json);
    /// </code>
    /// <para>
    /// <b>Async on <c>res://</c> is synchronous.</b> Godot exposes no asynchronous <c>FileAccess</c> API, so
    /// <see cref="ReadBytesAsync"/> checks the token and then reads inline. That is acceptable precisely
    /// because the read is bounded: <c>res://</c> content is packaged, small by construction, and rejected
    /// outright when it exceeds the caller's ceiling. Large payloads belong in <c>user://</c>, where real
    /// asynchronous I/O is available.
    /// </para>
    /// <para>
    /// Reads on OS-backed paths delegate to an <see cref="IFileStore"/> supplied at construction, so the
    /// storage policy — buffer size, pooled-buffer clearing — stays the caller's, and the whole class can be
    /// exercised without touching a filesystem.
    /// </para>
    /// </remarks>
    public sealed class GodotFileStore : IFileStore
    {
        public static readonly GodotFileStore Default = new GodotFileStore(SystemFileStore.Default);

        private readonly IFileStore _store;

        public GodotFileStore(IFileStore osPathStore)
        {
            _store = osPathStore ?? throw new ArgumentNullException(nameof(osPathStore));
        }

        /// <summary>The store used for OS-backed paths. Defaults to <c>SystemFileStore.Default</c>.</summary>
        public IFileStore OsPathStore => _store;

        public bool Exists(string path)
        {
            ValidatePath(path);

            if (IsResPath(path))
            {
                return global::Godot.FileAccess.FileExists(path);
            }

            return _store.Exists(ToOsPath(path));
        }

        public long GetLength(string path)
        {
            ValidatePath(path);

            if (!IsResPath(path))
            {
                return _store.GetLength(ToOsPath(path));
            }

            using (global::Godot.FileAccess file = OpenResForRead(path))
            {
                return ToInt64Length(file.GetLength(), path);
            }
        }

        public void Delete(string path)
        {
            ValidatePath(path);
            RejectResForWrite(path, "delete");
            _store.Delete(ToOsPath(path));
        }

        public byte[] ReadBytes(string path, int maxByteCount)
        {
            Validate(path, maxByteCount);

            if (!IsResPath(path))
            {
                return _store.ReadBytes(ToOsPath(path), maxByteCount);
            }

            using (global::Godot.FileAccess file = OpenResForRead(path))
            {
                int length = ValidateLength(ToInt64Length(file.GetLength(), path), maxByteCount, path);
                return length == 0 ? EmptyBytes : file.GetBuffer(length);
            }
        }

        public Task<byte[]> ReadBytesAsync(
            string path,
            int maxByteCount,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ReadBytes(path, maxByteCount));
        }

        public void WriteBytes(string path, byte[] content)
        {
            ValidatePath(path);
            RejectResForWrite(path, "write");
            _store.WriteBytes(ToOsPath(path), content);
        }

        public Task WriteBytesAsync(
            string path,
            byte[] content,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteBytes(path, content);
            return Task.CompletedTask;
        }

        private static readonly byte[] EmptyBytes = new byte[0];

        private static bool IsResPath(string path)
        {
            return path.StartsWith(GodotFilePaths.ResScheme, StringComparison.OrdinalIgnoreCase);
        }

        private static string ToOsPath(string path)
        {
            if (path.StartsWith(GodotFilePaths.UserScheme, StringComparison.OrdinalIgnoreCase))
            {
                return GodotFilePaths.ResolveOsPath(
                    GodotFileLocation.UserData,
                    path.Substring(GodotFilePaths.UserScheme.Length));
            }

            return path;
        }

        private static global::Godot.FileAccess OpenResForRead(string path)
        {
            global::Godot.FileAccess file = global::Godot.FileAccess.Open(path, global::Godot.FileAccess.ModeFlags.Read);
            if (file == null)
            {
                throw new FileNotFoundException(
                    "Godot could not open the packaged path for reading. It may not exist, or it may be "
                    + "unreadable in this build mode.",
                    path);
            }

            return file;
        }

        private static void RejectResForWrite(string path, string operation)
        {
            if (!IsResPath(path))
            {
                return;
            }

            throw new NotSupportedException(
                $"Cannot {operation} '{path}': res:// is read-only. In an exported build, packaged content "
                + "lives inside the PCK and has no writable filesystem location, and writing there from the "
                + "editor would produce a game that only works when run from the editor. Write to user:// "
                + "instead, via GodotFilePaths.ResolveOsPath(GodotFileLocation.UserData, ...).");
        }

        // The following three mirror BoundedFileReader's contract exactly, so a caller cannot tell which
        // route served a read by the exception it throws. Diverging here would make res:// a different API
        // wearing the same interface.
        private static void ValidatePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException(
                    "Path cannot be null, empty, or whitespace.",
                    nameof(path));
            }
        }

        private static void Validate(string path, int maxByteCount)
        {
            ValidatePath(path);

            if (maxByteCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxByteCount));
            }
        }

        /// <summary>
        /// Godot reports a file length as <c>ulong</c>, while the store contract is <c>long</c>. The
        /// conversion is guarded rather than cast, because a silent wrap on an absurd value would turn a
        /// length into a small or negative one — which a bounded read would then happily accept.
        /// </summary>
        private static long ToInt64Length(ulong length, string path)
        {
            if (length > long.MaxValue)
            {
                throw new IOException(
                    $"File '{Path.GetFileName(path)}' reports {length} bytes, which exceeds the "
                    + "addressable range of this contract.");
            }

            return (long)length;
        }

        private static int ValidateLength(long length, int maxByteCount, string path)
        {
            if (length < 0L || length > maxByteCount || length > int.MaxValue)
            {
                throw new IOException(
                    $"File '{Path.GetFileName(path)}' is {length} bytes and exceeds the "
                    + $"{maxByteCount}-byte read limit.");
            }

            return (int)length;
        }
    }
}
