using System;
using System.Collections.Generic;
using System.Text;
using CycloneGames.Hash.Core;

namespace CycloneGames.Hash.SelfCheck
{
    /// <summary>
    /// Dependency-free invariant harness for <c>CycloneGames.Hash</c>, ported from the Unity module's
    /// <c>Tests/Editor</c> suite.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a self-check instead of tests.</b> The Unity module ships NUnit fixtures under
    /// <c>Tests/Editor</c> and performance fixtures under <c>Tests/Performance</c>. Godot has no built-in C#
    /// test runner and no equivalent of the Performance Testing package, so the same coverage is delivered as
    /// a harness that runs anywhere: in CI over a plain console host, and inside Godot if a project wants it.
    /// This is the same substitution <c>CycloneGames.Logging</c> already makes, and for the same reason.
    /// </para>
    /// <para>
    /// <b>What the golden vectors are, and how they were established.</b> Every vector below was verified
    /// against an independent implementation before being written here — XXH64 against Microsoft's
    /// <c>System.IO.Hashing.XxHash64</c>, and FNV-1a against reference values computed in Python. Recording
    /// this module's own output would only prove that it still agrees with itself.
    /// </para>
    /// <para>
    /// <b>Engine-free by construction.</b> This file references no Godot and no UnityEngine type, so the
    /// whole contract can be validated without either engine present. That is the property that makes the
    /// module portable in the first place, and it is asserted by the gate script rather than assumed.
    /// </para>
    /// </remarks>
    public static class HashSelfCheck
    {
        /// <summary>One named result. Mirrors the shape used by <c>CycloneGames.Logging</c>'s harness.</summary>
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

            builder.Append('\n');
            builder.Append(FormatThroughput());

            return builder.ToString();
        }

        /// <summary>Runs every check.</summary>
        public static List<CheckResult> RunAll()
        {
            var results = new List<CheckResult>
            {
                XxHash64ReferenceVectors(),
                XxHash64StateContract(),
                XxHash64ChunkBoundariesMatchOneShot(),
                XxHash64WriteRepresentations(),
                FnvVectors(),
                FnvSeededChunksMatchOneShot(),
                FnvUtf16OrdinalSemantics(),
                FnvCombineMatchesLittleEndian(),
                StableHashContract(),
                ByteOrderContract(),
                AllocationContract(),
            };

            return results;
        }

        /// <summary>
        /// Reported, never asserted: throughput depends on the machine, so a threshold here would be a
        /// flaky gate rather than an invariant.
        /// </summary>
        public static string FormatThroughput()
        {
            byte[] buffer = Sequential(64 * 1024);
            for (int i = 0; i < 8; i++)
            {
                _ = XxHash64.Compute(buffer);
            }

            const int iterations = 512;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                _ = XxHash64.Compute(buffer);
            }

            sw.Stop();

            double megabytesPerSecond = (long)buffer.Length * iterations / (1024.0 * 1024.0)
                / (sw.Elapsed.TotalMilliseconds / 1000.0);

            return "measured: xxh64 " + megabytesPerSecond.ToString("F0") + " MB/s (reported, not asserted)";
        }

        private static CheckResult XxHash64ReferenceVectors()
        {
            (byte[] Data, ulong Expected)[] vectors =
            {
                (Sequential(0), 0xEF46DB3751D8E999UL),
                (Sequential(1), 0xE934A84ADB052768UL),
                (Sequential(31), 0xC346D2B59B4D8EE1UL),
                (Sequential(32), 0xCBF59C5116FF32B4UL),
                (Sequential(33), 0x0C535D1ACAFB8EADUL),
                (Sequential(64), 0xF7C67301DB6713F0UL),
                (Utf8("a"), 0xD24EC4F1A98C6E5BUL),
                (Utf8("abc"), 0x44BC2CF5AD770999UL),
            };

            string firstBad = null;
            foreach ((byte[] data, ulong expected) in vectors)
            {
                ulong actual = XxHash64.Compute(data);
                if (actual != expected && firstBad == null)
                {
                    firstBad = "length=" + data.Length + " got=0x" + actual.ToString("X16") + " want=0x" + expected.ToString("X16");
                }
            }

            ulong seeded = XxHash64.Compute(Sequential(257), 42UL);
            if (seeded != 0xE3DC51B1D7346E1BUL && firstBad == null)
            {
                firstBad = "seeded(257, 42) got=0x" + seeded.ToString("X16") + " want=0xE3DC51B1D7346E1B";
            }

            return new CheckResult(
                "xxh64.reference-vectors",
                firstBad == null,
                firstBad ?? (vectors.Length + " lengths plus one seeded vector"));
        }

        private static CheckResult XxHash64StateContract()
        {
            // A default-initialized struct must behave like Create(): the first Append lazily resets to seed 0
            // rather than silently hashing from uninitialized state.
            XxHash64 defaultState = default;
            defaultState.Append(Sequential(257));

            XxHash64 createdState = XxHash64.Create();
            createdState.Append(Sequential(257));

            bool defaultMatchesCreated = defaultState.GetDigest() == createdState.GetDigest();

            // GetDigest is a snapshot: it does not consume the state, so it can be read twice and the state can
            // continue afterwards.
            XxHash64 continuing = XxHash64.Create();
            continuing.Append(Sequential(257), 0, 31);
            ulong first = continuing.GetDigest();
            bool nonDestructive = continuing.GetDigest() == first;
            continuing.Append(Sequential(257), 31, Sequential(257).Length - 31);
            bool continues = continuing.GetDigest() == XxHash64.Compute(Sequential(257));

            // Reset reuses the value with a new seed and must clear the buffered tail, or the previous input
            // would leak into the next digest.
            XxHash64 reused = XxHash64.Create();
            reused.Append(Sequential(257));
            reused.Reset(42UL);
            reused.Append(Sequential(257));
            bool resetOk = reused.GetDigest() == XxHash64.Compute(Sequential(257), 42UL);

            return new CheckResult(
                "xxh64.state-contract",
                defaultMatchesCreated && nonDestructive && continues && resetOk,
                "default==created=" + defaultMatchesCreated
                + ", digestNonDestructive=" + nonDestructive
                + ", continues=" + continues
                + ", reset(seed)=" + resetOk);
        }

        private static CheckResult XxHash64ChunkBoundariesMatchOneShot()
        {
            byte[] data = Sequential(257);
            ulong expected = XxHash64.Compute(data, 42UL);

            string firstBad = null;
            for (int chunkSize = 1; chunkSize <= 65; chunkSize++)
            {
                XxHash64 state = XxHash64.Create(42UL);
                int offset = 0;
                while (offset < data.Length)
                {
                    int count = Math.Min(chunkSize, data.Length - offset);
                    state.Append(data, offset, count);
                    offset += count;
                }

                if (state.GetDigest() != expected && firstBad == null)
                {
                    firstBad = "chunkSize=" + chunkSize;
                }
            }

            return new CheckResult(
                "xxh64.chunk-boundaries-match-oneshot",
                firstBad == null,
                firstBad ?? "chunk sizes 1..65 over 257 bytes");
        }

        private static CheckResult XxHash64WriteRepresentations()
        {
            XxHash64 state = XxHash64.Create();
            state.Append(ReadOnlySpan<byte>.Empty);

            var canonical = new byte[XxHash64.HashSizeInBytes];
            var littleEndian = new byte[XxHash64.HashSizeInBytes];

            bool wroteCanonical = state.TryWriteHash(canonical) && state.TryWriteHashBigEndian(canonical);
            bool wroteLittle = state.TryWriteHashLittleEndian(littleEndian);

            byte[] expectedCanonical = { 0xEF, 0x46, 0xDB, 0x37, 0x51, 0xD8, 0xE9, 0x99 };
            byte[] expectedLittle = { 0x99, 0xE9, 0xD8, 0x51, 0x37, 0xDB, 0x46, 0xEF };

            bool canonicalOk = SameBytes(canonical, expectedCanonical);
            bool littleOk = SameBytes(littleEndian, expectedLittle);

            // The two representations must actually differ, otherwise this check proves nothing.
            bool differ = !SameBytes(canonical, littleEndian);

            // A short destination must be refused, and must not leave a partially written buffer or a mutated
            // state behind.
            XxHash64 shortState = XxHash64.Create();
            shortState.Append(Sequential(257));
            ulong before = shortState.GetDigest();
            var shortDestination = new byte[XxHash64.HashSizeInBytes - 1];
            bool refused = !shortState.TryWriteHash(shortDestination)
                && !shortState.TryWriteHashBigEndian(shortDestination)
                && !shortState.TryWriteHashLittleEndian(shortDestination);
            bool unmutated = shortState.GetDigest() == before;

            return new CheckResult(
                "xxh64.write-representations",
                wroteCanonical && wroteLittle && canonicalOk && littleOk && differ && refused && unmutated,
                "canonicalBigEndian=" + canonicalOk + ", littleEndian=" + littleOk + ", ordersDiffer=" + differ
                + ", refusesShort=" + refused + ", unmutated=" + unmutated);
        }

        private static CheckResult FnvVectors()
        {
            bool empty32 = Fnv1a32.Compute(ReadOnlySpan<byte>.Empty) == 0x811C9DC5u;
            bool a32 = Fnv1a32.Compute(Utf8("a")) == 0xE40C292Cu;
            bool empty64 = Fnv1a64.Compute(ReadOnlySpan<byte>.Empty) == 0xCBF29CE484222325UL;
            bool hello64 = Fnv1a64.Compute(Utf8("hello")) == 0xA430D84680AABD0BUL;

            return new CheckResult(
                "fnv.vectors",
                empty32 && a32 && empty64 && hello64,
                "32: empty=" + empty32 + " 'a'=" + a32 + "; 64: empty=" + empty64 + " \"hello\"=" + hello64);
        }

        private static CheckResult FnvSeededChunksMatchOneShot()
        {
            var laddered = new byte[257];
            for (int i = 0; i < laddered.Length; i++)
            {
                laddered[i] = (byte)(i * 31);
            }

            const uint seed32 = 0x12345678U;
            uint chunked32 = Fnv1a32.Compute(laddered.AsSpan(0, 17), seed32);
            chunked32 = Fnv1a32.Compute(laddered.AsSpan(17, 91), chunked32);
            chunked32 = Fnv1a32.Compute(laddered.AsSpan(108), chunked32);
            bool ok32 = chunked32 == Fnv1a32.Compute(laddered, seed32);

            const ulong seed64 = 0x0123456789ABCDEFUL;
            byte[] sequential = Sequential(257);
            ulong chunked64 = Fnv1a64.Compute(sequential.AsSpan(0, 31), seed64);
            chunked64 = Fnv1a64.Compute(sequential.AsSpan(31, 128), chunked64);
            chunked64 = Fnv1a64.Compute(sequential.AsSpan(159), chunked64);
            bool ok64 = chunked64 == Fnv1a64.Compute(sequential, seed64);

            return new CheckResult(
                "fnv.seeded-chunks-match-oneshot",
                ok32 && ok64,
                "32-bit=" + ok32 + ", 64-bit=" + ok64);
        }

        private static CheckResult FnvUtf16OrdinalSemantics()
        {
            // The contract folds each UTF-16 code unit once. Compare against a hand-written fold rather than
            // against another call into the same helper.
            uint expected32;
            unchecked
            {
                expected32 = Fnv1a32.OffsetBasis;
                expected32 ^= 'A';
                expected32 *= Fnv1a32.Prime;
                expected32 ^= '.';
                expected32 *= Fnv1a32.Prime;
                expected32 ^= 'B';
                expected32 *= Fnv1a32.Prime;
            }

            bool manual32 = Fnv1a32.ComputeUtf16Ordinal("A.B") == expected32;

            ulong expected64;
            unchecked
            {
                expected64 = Fnv1a64.OffsetBasis;
                expected64 ^= 'A';
                expected64 *= Fnv1a64.Prime;
                expected64 ^= '\u4F60';
                expected64 *= Fnv1a64.Prime;
            }

            bool manual64 = Fnv1a64.ComputeUtf16Ordinal("A\u4F60") == expected64;

            // For ASCII the ordinal fold coincides with the byte-wise hash. It must NOT coincide for anything
            // above 0xFF the two diverge, which is why cross-language text must be encoded first.
            bool asciiAgrees = Fnv1a32.ComputeUtf16Ordinal("abc") == Fnv1a32.Compute(Utf8("abc"));
            uint ordinalNonAscii = Fnv1a32.ComputeUtf16Ordinal("\u4F60");
            uint utf16LeBytes = Fnv1a32.Compute(new byte[] { 0x60, 0x4F });
            bool nonAsciiDiffers = ordinalNonAscii != utf16LeBytes;

            return new CheckResult(
                "fnv.utf16-ordinal-semantics",
                manual32 && manual64 && asciiAgrees && nonAsciiDiffers,
                "manual32=" + manual32 + ", manual64=" + manual64
                + ", asciiAgrees=" + asciiAgrees + ", nonAsciiDiffersFromUtf16Le=" + nonAsciiDiffers);
        }

        private static CheckResult FnvCombineMatchesLittleEndian()
        {
            const uint value32 = 0x01020304u;
            bool ok32 = Fnv1a32.CombineUInt32LittleEndian(Fnv1a32.OffsetBasis, value32)
                == Fnv1a32.Compute(new byte[] { 0x04, 0x03, 0x02, 0x01 }, Fnv1a32.OffsetBasis);

            const ulong value64 = 0x0102030405060708UL;
            bool ok64 = Fnv1a64.CombineUInt64LittleEndian(Fnv1a64.OffsetBasis, value64)
                == Fnv1a64.Compute(new byte[] { 0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01 }, Fnv1a64.OffsetBasis);

            return new CheckResult(
                "fnv.combine-matches-little-endian",
                ok32 && ok64,
                "32-bit=" + ok32 + ", 64-bit=" + ok64);
        }

        private static CheckResult StableHashContract()
        {
            bool rejectsNull32 = Throws<ArgumentNullException>(() => StableHash32.ComputeUtf16Ordinal((string)null));
            bool rejectsNull64 = Throws<ArgumentNullException>(() => StableHash64.ComputeUtf16Ordinal((string)null));

            bool mapsZero32 = StableHash32.EnsureNonZero(0U) == StableHash32.NonZeroFallback;
            bool mapsZero64 = StableHash64.EnsureNonZero(0UL) == StableHash64.NonZeroFallback;
            bool keepsNonZero32 = StableHash32.EnsureNonZero(7U) == 7U;
            bool keepsNonZero64 = StableHash64.EnsureNonZero(7UL) == 7UL;

            // The whole point is that 0 stays reserved, so the mapping must never leak a zero in practice.
            var rng = new Random(20260920);
            bool neverZero = true;
            for (int i = 0; i < 10000; i++)
            {
                var buffer = new byte[rng.Next(0, 40)];
                rng.NextBytes(buffer);
                if (StableHash32.ComputeBytes(buffer) == 0U || StableHash64.ComputeBytes(buffer) == 0UL)
                {
                    neverZero = false;
                    break;
                }
            }

            return new CheckResult(
                "stablehash.contract",
                rejectsNull32 && rejectsNull64 && mapsZero32 && mapsZero64 && keepsNonZero32 && keepsNonZero64 && neverZero,
                "nullsRejected=" + (rejectsNull32 && rejectsNull64)
                + ", zeroMapped=" + (mapsZero32 && mapsZero64)
                + ", noZeroLeak=" + neverZero);
        }

        private static CheckResult ByteOrderContract()
        {
            var buffer32 = new byte[8];

            HashByteOrder.WriteUInt32LittleEndian(buffer32, 0x01020304U);
            bool le32 = buffer32[0] == 0x04 && buffer32[1] == 0x03 && buffer32[2] == 0x02 && buffer32[3] == 0x01
                && HashByteOrder.ReadUInt32LittleEndian(buffer32) == 0x01020304U;

            HashByteOrder.WriteUInt32BigEndian(buffer32, 0x01020304U);
            bool be32 = buffer32[0] == 0x01 && buffer32[1] == 0x02 && buffer32[2] == 0x03 && buffer32[3] == 0x04
                && HashByteOrder.ReadUInt32BigEndian(buffer32) == 0x01020304U;

            var buffer64 = new byte[8];

            HashByteOrder.WriteUInt64LittleEndian(buffer64, 0x0102030405060708UL);
            bool le64 = buffer64[0] == 0x08 && buffer64[7] == 0x01
                && HashByteOrder.ReadUInt64LittleEndian(buffer64) == 0x0102030405060708UL;

            HashByteOrder.WriteUInt64BigEndian(buffer64, 0x0102030405060708UL);
            bool be64 = buffer64[0] == 0x01 && buffer64[7] == 0x08
                && HashByteOrder.ReadUInt64BigEndian(buffer64) == 0x0102030405060708UL;

            bool rejectsShort = Throws<ArgumentOutOfRangeException>(() => HashByteOrder.ReadUInt32LittleEndian(new byte[3]))
                && Throws<ArgumentOutOfRangeException>(() => HashByteOrder.WriteUInt32BigEndian(new byte[3], 1U))
                && Throws<ArgumentOutOfRangeException>(() => HashByteOrder.ReadUInt64BigEndian(new byte[7]))
                && Throws<ArgumentOutOfRangeException>(() => HashByteOrder.WriteUInt64LittleEndian(new byte[7], 1UL));

            return new CheckResult(
                "byteorder.contract",
                le32 && be32 && le64 && be64 && rejectsShort,
                "u32LE=" + le32 + ", u32BE=" + be32 + ", u64LE=" + le64 + ", u64BE=" + be64 + ", shortRejected=" + rejectsShort);
        }

        private static CheckResult AllocationContract()
        {
            byte[] buffer = Sequential(4096);

            // Warm up so the measurement does not include first-call JIT.
            _ = XxHash64.Compute(buffer);
            _ = Fnv1a64.Compute(buffer);
            XxHash64 warm = XxHash64.Create();
            warm.Append(buffer);
            _ = warm.GetDigest();

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                _ = XxHash64.Compute(buffer);
            }

            long oneShot = (GC.GetAllocatedBytesForCurrentThread() - before) / 1000;

            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                _ = Fnv1a64.Compute(buffer);
            }

            long fnv = (GC.GetAllocatedBytesForCurrentThread() - before) / 1000;

            XxHash64 streaming = XxHash64.Create();
            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                streaming.Reset();
                streaming.Append(buffer);
                _ = streaming.GetDigest();
            }

            long streamed = (GC.GetAllocatedBytesForCurrentThread() - before) / 1000;

            return new CheckResult(
                "allocation.zero",
                oneShot == 0 && fnv == 0 && streamed == 0,
                "xxh64OneShot=" + oneShot + " B/call, fnv1a64=" + fnv + " B/call, xxh64Streaming=" + streamed + " B/call");
        }

        private static byte[] Sequential(int length)
        {
            var data = new byte[length];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)i;
            }

            return data;
        }

        private static byte[] Utf8(string text)
        {
            return Encoding.UTF8.GetBytes(text);
        }

        private static bool SameBytes(byte[] left, byte[] right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static bool Throws<TException>(Action action)
            where TException : Exception
        {
            try
            {
                action();
                return false;
            }
            catch (TException)
            {
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
