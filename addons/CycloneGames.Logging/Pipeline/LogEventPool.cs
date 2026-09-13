// Ported from CycloneGames.Logging (Unity). Keep both repositories in lockstep; see Modules/CycloneGames.Logging/README.md.
using System;
using System.Threading;

namespace CycloneGames.Logging.Pipeline
{
    internal static class LogEventPool
    {
        private const int DefaultPrewarmCount = 256;

        /// <summary>
        /// Upper bound on how many idle records the pool will retain.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This must be at least as deep as the deepest queue a pipeline is configured with.</b>
        /// The pool only holds records that are <i>not</i> in a queue, so a burst that fills a queue
        /// of N records leaves nothing pooled: the producer's next N acquisitions all miss and every
        /// miss is a <c>new LogEvent</c> on the heap. A cap below the queue depth does not merely
        /// reduce the pool's effectiveness, it converts each pulse into a fixed allocation — which is
        /// the opposite of what a bounded queue is for.
        /// </para>
        /// <para>
        /// Measured: with the previous cap of 4096 against the default queue depth of 8192, every
        /// full-queue pulse allocated 4032 records / ~315 KB and recorded 4032 pool misses, with no
        /// gen0 collection at that size — i.e. silent garbage accumulating toward one. Raising the cap
        /// above the queue depth takes the same pulse to 0 bytes and 0 misses, because the drain can
        /// then return the whole burst.
        /// </para>
        /// <para>
        /// The array is a reference array, so the upfront cost is pointers, not records: 16384
        /// references (~128 KB) regardless of how many records are ever materialized. Growth is lazy —
        /// the pool only fills to what the workload actually returns — and
        /// <see cref="TrimStep"/> can release idle entries at runtime.
        /// </para>
        /// <para>
        /// Exposed so the host layer can report a mismatch between this and a configured queue depth
        /// rather than leaving it as a silent tuning trap.
        /// </para>
        /// </remarks>
        internal const int RetentionCapacity = 16384;

        private const int MaxRetainedCount = RetentionCapacity;

        private static readonly object SyncRoot = new object();
        private static readonly LogEvent[] Items = new LogEvent[MaxRetainedCount];

        private static int _count;
        private static int _peakSize;
        private static long _totalGets;
        private static long _totalReturns;
        private static long _totalMisses;
        private static long _totalDiscards;
        private static long _invalidReturns;

        internal static LogEvent Get()
        {
            Interlocked.Increment(ref _totalGets);

            lock (SyncRoot)
            {
                if (_count > 0)
                {
                    int index = --_count;
                    LogEvent message = Items[index];
                    Items[index] = null;
                    if (!message.TryMarkRented())
                    {
                        throw new InvalidOperationException("LogEvent pool state is corrupted.");
                    }

                    return message;
                }
            }

            Interlocked.Increment(ref _totalMisses);
            return new LogEvent();
        }

        internal static void Return(LogEvent message)
        {
            if (message == null)
            {
                return;
            }

            Interlocked.Increment(ref _totalReturns);
            if (!message.TryMarkReturned())
            {
                Interlocked.Increment(ref _invalidReturns);
                return;
            }

            message.Reset();
            lock (SyncRoot)
            {
                if (_count >= Items.Length)
                {
                    Interlocked.Increment(ref _totalDiscards);
                    return;
                }

                Items[_count++] = message;
                if (_count > _peakSize)
                {
                    _peakSize = _count;
                }
            }
        }

        internal static void Prewarm(int count = DefaultPrewarmCount)
        {
            count = Math.Min(Math.Max(count, 0), Items.Length);
            lock (SyncRoot)
            {
                while (_count < count)
                {
                    var message = new LogEvent();
                    if (!message.TryMarkReturned())
                    {
                        throw new InvalidOperationException("Unable to initialize LogEvent pool state.");
                    }

                    Items[_count++] = message;
                }

                if (_count > _peakSize)
                {
                    _peakSize = _count;
                }
            }
        }

        internal static void Clear()
        {
            lock (SyncRoot)
            {
                Array.Clear(Items, 0, _count);
                _count = 0;
            }
        }

        internal static int TrimStep(int targetCount, int maxWork)
        {
            lock (SyncRoot)
            {
                int releaseCount = Math.Min(Math.Max(_count - targetCount, 0), maxWork);
                if (releaseCount == 0)
                {
                    return 0;
                }

                int firstReleasedIndex = _count - releaseCount;
                Array.Clear(Items, firstReleasedIndex, releaseCount);
                _count = firstReleasedIndex;
                return releaseCount;
            }
        }

        internal static PoolStatistics GetStatistics()
        {
            int count;
            int peak;
            lock (SyncRoot)
            {
                count = _count;
                peak = _peakSize;
            }

            return new PoolStatistics(
                count,
                peak,
                Interlocked.Read(ref _totalGets),
                Interlocked.Read(ref _totalReturns),
                Interlocked.Read(ref _totalMisses),
                Interlocked.Read(ref _totalDiscards),
                Interlocked.Read(ref _invalidReturns));
        }

        internal static void ResetStatistics()
        {
            Interlocked.Exchange(ref _totalGets, 0);
            Interlocked.Exchange(ref _totalReturns, 0);
            Interlocked.Exchange(ref _totalMisses, 0);
            Interlocked.Exchange(ref _totalDiscards, 0);
            Interlocked.Exchange(ref _invalidReturns, 0);
            lock (SyncRoot)
            {
                _peakSize = _count;
            }
        }

        internal readonly struct PoolStatistics
        {
            internal readonly int CurrentSize;
            internal readonly int PeakSize;
            internal readonly long TotalGets;
            internal readonly long TotalReturns;
            internal readonly long TotalMisses;
            internal readonly long TotalDiscards;
            internal readonly long InvalidReturns;

            internal double HitRate => TotalGets > 0 ? 1.0 - (double)TotalMisses / TotalGets : 1.0;
            internal double DiscardRate => TotalReturns > 0 ? (double)TotalDiscards / TotalReturns : 0.0;

            internal PoolStatistics(
                int currentSize,
                int peakSize,
                long totalGets,
                long totalReturns,
                long totalMisses,
                long totalDiscards,
                long invalidReturns)
            {
                CurrentSize = currentSize;
                PeakSize = peakSize;
                TotalGets = totalGets;
                TotalReturns = totalReturns;
                TotalMisses = totalMisses;
                TotalDiscards = totalDiscards;
                InvalidReturns = invalidReturns;
            }
        }
    }
}
