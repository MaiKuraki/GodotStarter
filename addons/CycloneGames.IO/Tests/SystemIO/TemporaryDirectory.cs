// Ported from CycloneGames.IO.Tests (Unity). Keep both repositories in lockstep; see Modules/CycloneGames.IO/README.md.
// Godot has no assembly definitions, so there is no way to keep a test assembly out of a consuming
// project's build. These fixtures therefore compile only when the test host defines
// CYCLONEGAMES_IO_TESTS; Tools/IOCheck does, and a consuming Godot project does not.
#if CYCLONEGAMES_IO_TESTS

using System;
using System.IO;

namespace CycloneGames.IO.Tests.SystemIO
{
    internal sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "CycloneGames.IO.SystemIO.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        internal string GetPath(string relativePath)
        {
            return System.IO.Path.Combine(Path, relativePath);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}

#endif
