using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CycloneGames.IO;
using CycloneGames.IO.Godot;

namespace CycloneGames.IO.Godot
{
    /// <summary>
    /// Dependency-free invariant harness for the Godot-facing layer of CycloneGames.IO.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this file exists, and what in it is a port versus an adaptation.</b> The Unity module tests
    /// its engine-facing layer: <c>Tests/Unity/UnityFileUriTests.cs</c> covers path containment, traversal
    /// rejection, and typed failures for <c>UnityFileUri</c>. That coverage did not survive the port,
    /// because NUnit needs a test runner and Godot has none — and the Godot layer is new code with no
    /// counterpart to inherit coverage from. So the principle (the engine-facing layer must be covered) is
    /// the original's, while the mechanism is necessarily different: the same checks, expressed the way
    /// <c>CycloneGames.Hash</c>'s and <c>CycloneGames.Logging</c>'s self-checks are, runnable inside Godot
    /// with nothing installed.
    /// </para>
    /// <para>
    /// <b>Every assertion is a property, not a constant, wherever the input could change.</b> The checks
    /// never assert a golden hash of a source file: a file that gets edited would make the report drift,
    /// and a drifting report is not a regression signal. Lengths are compared against what
    /// <c>FileAccess</c> reports; failures are compared against the exception contract. The one fixed
    /// digest below is over a literal string, which cannot drift.
    /// </para>
    /// <para>
    /// The portable contracts at the end are also covered by the NUnit suite in plain CI; they are here so
    /// that a Godot consumer gets one report covering the whole module, not two half-answers.
    /// </para>
    /// </remarks>
    public static class IOSelfCheck
    {
        /// <summary>One named result. Mirrors the shape used by the Hash and Logging harnesses.</summary>
        public readonly struct CheckResult
        {
            public readonly string Name;

            public readonly bool Passed;

            /// <summary>
            /// True when the check could not run for lack of an input. Neither a pass nor a failure, and
            /// reported separately so a run that silently lost a check cannot look green.
            /// </summary>
            public readonly bool Skipped;

            public readonly string Detail;

            internal CheckResult(string name, bool passed, string detail, bool skipped = false)
            {
                Name = name;
                Passed = passed;
                Skipped = skipped;
                Detail = detail;
            }
        }

        /// <summary>Runs every check and formats the report. One line per check, then a summary line.</summary>
        public static string RunAndFormat()
        {
            List<CheckResult> results = RunAll();

            var builder = new StringBuilder();
            foreach (CheckResult result in results)
            {
                if (result.Skipped)
                {
                    builder.Append("SKIP  ").Append(result.Name);
                }
                else
                {
                    builder.Append(result.Passed ? "PASS  " : "FAIL  ").Append(result.Name);
                }

                if (!string.IsNullOrEmpty(result.Detail))
                {
                    builder.Append(" — ").Append(result.Detail);
                }

                builder.Append('\n');
            }

            int passed = 0;
            int failed = 0;
            int skipped = 0;
            foreach (CheckResult result in results)
            {
                if (result.Skipped)
                {
                    skipped++;
                }
                else if (result.Passed)
                {
                    passed++;
                }
                else
                {
                    failed++;
                }
            }

            builder.Append(passed).Append('/').Append(results.Count - skipped).Append(" checks passed");
            if (skipped > 0)
            {
                builder.Append(" (").Append(skipped).Append(" skipped)");
            }

            if (failed > 0)
            {
                builder.Append(" — ").Append(failed).Append(" FAILED");
            }

            return builder.ToString();
        }

        /// <summary>Runs every check.</summary>
        public static List<CheckResult> RunAll()
        {
            var results = new List<CheckResult>
            {
                UserPathStaysInsideItsLocation(),
                TraversalIsRejectedWithATypedError(),
                ResHasNoOsPathAndSaysSo(),
                ResBoundedReadMatchesTheEngine(),
                ResReadCeilingIsEnforced(),
                ResWriteIsRejected(),
                UserAtomicCommitRoundTripsAndLeavesNoTemp(),
                CleanupLeavesNothingBehind(),
                ContentHashIsCanonicalLowercaseHex(),
                TextCodecRoundTrips(),
            };

            return results;
        }

        // ---- The engine-facing layer, mirroring what Tests/Unity covered on the Unity side ----------

        /// <summary>Mirrors Create_PersistentDataPath_ReturnsContainedFileUri: a resolved path must stay
        /// under its location's root.</summary>
        private static CheckResult UserPathStaysInsideItsLocation()
        {
            string root = global::Godot.OS.GetUserDataDir();
            string resolved = GodotFilePaths.ResolveOsPath(GodotFileLocation.UserData, "iodemo/nested/save.json");
            string fullRoot = Path.GetFullPath(root);

            bool contained = resolved.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(resolved, fullRoot, StringComparison.OrdinalIgnoreCase);

            return new CheckResult(
                "godot.user-path-containment",
                contained,
                contained ? "resolved under OS.GetUserDataDir()" : "resolved to '" + resolved + "' outside '" + fullRoot + "'");
        }

        /// <summary>
        /// Mirrors TryCreate_TraversalPath_ReturnsTypedFailure, which asserts
        /// <c>UnityFileUriError.InvalidPath</c> — not a containment error. That is deliberate and worth
        /// pinning down: a dot segment is rejected during <b>normalization</b>, before containment is ever
        /// evaluated, so the failure is reported as an invalid path rather than an escape. An earlier
        /// version of this check expected PathOutsideLocation and failed; the code was right and the
        /// expectation was wrong, which is exactly the kind of mistake a self-check should catch in itself.
        /// </summary>
        private static CheckResult TraversalIsRejectedWithATypedError()
        {
            bool refused = !GodotFilePaths.TryResolveOsPath(
                GodotFileLocation.UserData,
                "../outside.yaml",
                out string resolved,
                out GodotFileUriError error);

            bool typed = refused
                && resolved == null
                && error == GodotFileUriError.InvalidPath;

            return new CheckResult(
                "godot.traversal-rejected",
                typed,
                typed ? "typed InvalidPath at normalization, no path returned" : "refused=" + refused + " error=" + error);
        }

        /// <summary>Mirrors TryCreate_UnsupportedScheme_ReturnsTypedFailure, adapted: on Godot the question
        /// is not the scheme but whether System.IO can be used at all, and res:// must say no.</summary>
        private static CheckResult ResHasNoOsPathAndSaysSo()
        {
            bool refused = !GodotFilePaths.TryResolveOsPath(
                GodotFileLocation.Res,
                "config/settings.json",
                out _,
                out GodotFileUriError error);

            bool typed = refused && error == GodotFileUriError.NotOsPathBacked;

            return new CheckResult(
                "godot.res-os-path-refused",
                typed,
                typed ? "typed NotOsPathBacked" : "refused=" + refused + " error=" + error);
        }

        private static CheckResult ResBoundedReadMatchesTheEngine()
        {
            const string path = "res://LogDemo.cs";

            using (global::Godot.FileAccess file = global::Godot.FileAccess.Open(path, global::Godot.FileAccess.ModeFlags.Read))
            {
                if (file == null)
                {
                    return new CheckResult(
                        "godot.res-bounded-read",
                        false,
                        "'" + path + "' could not be opened — the check did not run",
                        true);
                }

                // FileAccess.GetLength() returns ulong; the store contract is long. Guarded for the same
                // reason as in GodotFileStore: an unguarded cast could wrap into a negative length.
                long engineLength = ToInt64Length(file.GetLength());
                byte[] read = GodotFileStore.Default.ReadBytes(path, (int)Math.Min(engineLength, int.MaxValue));
                bool matches = read.Length == engineLength;

                return new CheckResult(
                    "godot.res-bounded-read",
                    matches,
                    matches ? read.Length + " bytes, same as FileAccess reports" : "read " + read.Length + " but FileAccess reports " + engineLength);
            }
        }

        private static CheckResult ResReadCeilingIsEnforced()
        {
            const string path = "res://LogDemo.cs";

            try
            {
                GodotFileStore.Default.ReadBytes(path, 16);
                return new CheckResult("godot.res-ceiling-enforced", false, "no exception — the ceiling was ignored");
            }
            catch (IOException ex)
            {
                // The message must be the same one SystemFileStore produces, so a caller cannot tell which
                // route served the read by the exception it throws.
                bool wordingMatches = ex.Message.Contains("exceeds the") && ex.Message.Contains("read limit");
                return new CheckResult(
                    "godot.res-ceiling-enforced",
                    wordingMatches,
                    wordingMatches ? ex.Message : "wrong message: " + ex.Message);
            }
        }

        private static CheckResult ResWriteIsRejected()
        {
            bool writeRejected = false;
            bool deleteRejected = false;

            try
            {
                GodotFileStore.Default.WriteBytes("res://LogDemo.cs", new byte[] { 1 });
            }
            catch (NotSupportedException)
            {
                writeRejected = true;
            }

            try
            {
                GodotFileStore.Default.Delete("res://LogDemo.cs");
            }
            catch (NotSupportedException)
            {
                deleteRejected = true;
            }

            return new CheckResult(
                "godot.res-write-rejected",
                writeRejected && deleteRejected,
                "write=" + writeRejected + ", delete=" + deleteRejected);
        }

        private static CheckResult UserAtomicCommitRoundTripsAndLeavesNoTemp()
        {
            string path = GodotFilePaths.ResolveOsPath(GodotFileLocation.UserData, "iodemo/selfcheck.json");
            string directory = Path.GetDirectoryName(path);

            try
            {
                const string first = "{\"level\":7}";
                SystemFileStore.Default.WriteTextAtomically(path, first);
                string readBack = SystemFileStore.Default.ReadText(path, 1024);
                bool roundTrip = readBack == first;

                // A second commit must replace in place, never delete-then-move, and must leave no
                // temporary file behind.
                SystemFileStore.Default.WriteTextAtomically(path, first + " ");
                long length = SystemFileStore.Default.GetLength(path);
                bool replaced = length == first.Length + 1;

                string[] leftover = Directory.Exists(directory) ? Directory.GetFiles(directory) : new string[0];
                bool noTemp = leftover.Length == 1 && string.Equals(leftover[0], path, StringComparison.OrdinalIgnoreCase);

                return new CheckResult(
                    "godot.user-atomic-commit",
                    roundTrip && replaced && noTemp,
                    "roundTrip=" + roundTrip + ", replaced=" + replaced + ", noTemp=" + noTemp);
            }
            finally
            {
                TryDelete(path);
            }
        }

        private static CheckResult CleanupLeavesNothingBehind()
        {
            string path = GodotFilePaths.ResolveOsPath(GodotFileLocation.UserData, "iodemo/temp/cleanup.json");
            string nestedDirectory = Path.GetDirectoryName(path);
            string demoDirectory = Path.GetDirectoryName(nestedDirectory);

            try
            {
                SystemFileStore.Default.WriteTextAtomically(path, "x");
                SystemFileStore.Default.Delete(path);

                if (Directory.Exists(nestedDirectory))
                {
                    Directory.Delete(nestedDirectory);
                }

                if (Directory.Exists(demoDirectory))
                {
                    Directory.Delete(demoDirectory);
                }

                bool clean = !File.Exists(path)
                    && !Directory.Exists(nestedDirectory)
                    && !Directory.Exists(demoDirectory);

                return new CheckResult(
                    "godot.cleanup",
                    clean,
                    clean ? "file, nested directory and demo directory all gone" : "residue remains");
            }
            catch (IOException ex)
            {
                return new CheckResult("godot.cleanup", false, ex.Message);
            }
        }

        // ---- Portable contracts, re-checked here so one report covers the whole module ---------------

        private static CheckResult ContentHashIsCanonicalLowercaseHex()
        {
            byte[] content = Encoding.UTF8.GetBytes("CycloneGames.IO self-check");
            string hex = ContentHasher.ComputeHex(content, FileHashAlgorithm.Sha256);

            // A fixed digest over a literal string: unlike a hash of a source file, this cannot drift.
            const string expected = "588c84bbcaf0752118116b99bea1a69a71387ef979ab4fd6aeccfc45a50a19c7";

            bool correct = hex == expected;
            bool canonical = hex.Length == 64 && hex == hex.ToLowerInvariant();

            return new CheckResult(
                "portable.content-hash",
                correct && canonical,
                correct ? "64 lowercase hex chars, matches the fixed digest" : "got " + hex);
        }

        private static CheckResult TextCodecRoundTrips()
        {
            const string text = "配置 settings — 7";
            string decoded = TextCodec.Decode(TextCodec.Encode(text));

            return new CheckResult(
                "portable.text-codec",
                decoded == text,
                decoded == text ? "BOM-aware round-trip ok" : "got '" + decoded + "'");
        }

        private static long ToInt64Length(ulong length)
        {
            if (length > long.MaxValue)
            {
                throw new IOException("A length of " + length + " bytes exceeds this contract's range.");
            }

            return (long)length;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
