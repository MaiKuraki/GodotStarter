// Ported from CycloneGames.Factory.Tests (Unity). Keep both repositories in lockstep; see Modules/CycloneGames.Factory/README.md.
// Godot has no assembly definitions, so there is no way to keep a test assembly out of a consuming
// project build. These fixtures therefore compile only when the test host defines
// CYCLONEGAMES_FACTORY_TESTS; Tools/FactoryCheck does, and a consuming Godot project does not.
#if CYCLONEGAMES_FACTORY_TESTS
using CycloneGames.Factory.Runtime;
using NUnit.Framework;

namespace CycloneGames.Factory.Tests.Performance
{
    /// <summary>
    /// Report-only prewarm scaling probes. Each N gets its own measured test so the recorded medians
    /// can be tabulated into a complexity curve: linear prewarm roughly doubles per doubling of N, a
    /// quadratic one roughly quadruples. No assertion is made here; the curve is asserted by
    /// <see cref="PoolPrewarmComplexityGateTests"/>.
    /// </summary>
    public sealed class PoolPrewarmScalingTests
    {
        private const int WARMUP_COUNT = 5;
        private const int MEASUREMENT_COUNT = 15;
        private const int ITERATIONS_PER_MEASUREMENT = 1;

        private static int _sink;

        [Test]
        public void Prewarm_1K()
        {
            MeasurePrewarm(1024);
        }

        [Test]
        public void Prewarm_2K()
        {
            MeasurePrewarm(2048);
        }

        [Test]
        public void Prewarm_4K()
        {
            MeasurePrewarm(4096);
        }

        [Test]
        public void Prewarm_8K()
        {
            MeasurePrewarm(8192);
        }

        [Test]
        public void Prewarm_16K()
        {
            MeasurePrewarm(16384);
        }

        private static void MeasurePrewarm(int count)
        {
            PoolMeasure.Method(() => PrewarmOnce(count), WARMUP_COUNT, MEASUREMENT_COUNT);
        }

        private static void PrewarmOnce(int count)
        {
            // softCapacity: 0 disables the constructor prewarm so this method measures exactly one
            // explicit Prewarm(count) call; hardCapacity: -1 removes any capacity clamp.
            var factory = new PerfPoolableFactory();
            using var pool = new ObjectPool<int, PerfPoolable>(
                factory,
                new PoolCapacitySettings(softCapacity: 0, hardCapacity: -1));

            pool.Prewarm(count);
            _sink = pool.CountInactive;
        }
    }
}
#endif
