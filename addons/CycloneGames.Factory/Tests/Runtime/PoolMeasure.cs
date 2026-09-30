// Ported from CycloneGames.Factory.Tests (Unity). Keep both repositories in lockstep; see Modules/CycloneGames.Factory/README.md.
// Godot has no assembly definitions, so there is no way to keep a test assembly out of a consuming
// project build. These fixtures therefore compile only when the test host defines
// CYCLONEGAMES_FACTORY_TESTS; Tools/FactoryCheck does, and a consuming Godot project does not.
#if CYCLONEGAMES_FACTORY_TESTS
using System;
using System.Diagnostics;

namespace CycloneGames.Factory.Tests.Performance
{
    /// <summary>
    /// Minimal replacement for the Unity.PerformanceTesting <c>Measure.Method</c> sampling harness.
    /// </summary>
    /// <remarks>
    /// The Unity original's three measurement fixtures carry no assertion: they emit a median so two
    /// builds can be compared on one machine, and the author explicitly documents that they are never
    /// used as a gate. Unity.PerformanceTesting has no Godot counterpart and no NuGet equivalent, so
    /// the harness is reimplemented here rather than the fixtures being deleted — deleting them would
    /// drop the only prewarm/throughput evidence the module has, and reimplementing the sampling is
    /// twenty lines with no engine dependency.
    ///
    /// The complexity curve IS asserted, by PoolPrewarmComplexityGateTests, which uses this same
    /// sample. That is what keeps the harness honest: a timer that stopped working would make the
    /// complexity gate fail rather than silently report a clean curve.
    /// </remarks>
    internal static class PoolMeasure
    {
        /// <summary>
        /// Runs <paramref name="action"/> for the requested warmup and measurement counts and returns
        /// the best measurement in milliseconds. Best-of rather than median: the value feeds an
        /// asymptotic-curve assertion, and a stray GC during one sample would otherwise dominate a
        /// median taken over a small count.
        /// </summary>
        public static double Method(Action action, int warmupCount, int measurementCount)
        {
            for (int i = 0; i < warmupCount; i++)
            {
                action();
            }

            double best = double.MaxValue;
            var stopwatch = new Stopwatch();
            for (int i = 0; i < measurementCount; i++)
            {
                stopwatch.Restart();
                action();
                stopwatch.Stop();
                double elapsed = stopwatch.Elapsed.TotalMilliseconds;
                if (elapsed < best)
                {
                    best = elapsed;
                }
            }

            return best;
        }
    }
}
#endif
