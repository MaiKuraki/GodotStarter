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
    /// Report-only steady-state throughput baselines for the spawn/despawn hot loop.
    /// These tests make no assertions: wall-clock median and spread are emitted so two builds can be
    /// compared on the same machine, and are never used as a gate.
    /// </summary>
    public sealed class PoolSpawnDespawnThroughputTests
    {
        private const int PREWARM_COUNT = 1024;
        private const int ITERATIONS_PER_MEASUREMENT = 4096;
        private const int WARMUP_COUNT = 5;
        private const int MEASUREMENT_COUNT = 15;

        private static int _intSink;
        private static bool _boolSink;

        [Test]
        public void ObjectPool_SpawnDespawn_SteadyState()
        {
            var factory = new PerfPoolableFactory();
            using var pool = new ObjectPool<int, PerfPoolable>(
                factory,
                new PoolCapacitySettings(softCapacity: PREWARM_COUNT, hardCapacity: -1));

            // One activate/deactivate cycle before measuring: warms JIT and the list/dictionary capacity.
            PerfPoolable warmup = pool.Spawn(0);
            pool.Despawn(warmup);

            PoolMeasure.Method(() => ObjectPoolStep(pool), WARMUP_COUNT, MEASUREMENT_COUNT);
        }

        [Test]
        public void FastObjectPool_SpawnDespawn_SteadyState()
        {
            using var pool = new PerfFastObjectPool(
                new PoolCapacitySettings(softCapacity: PREWARM_COUNT, hardCapacity: -1));

            PerfFastPoolable warmup = pool.Spawn();
            pool.Despawn(warmup);

            PoolMeasure.Method(() => FastPoolStep(pool), WARMUP_COUNT, MEASUREMENT_COUNT);
        }

        private static void ObjectPoolStep(ObjectPool<int, PerfPoolable> pool)
        {
            PerfPoolable item = pool.Spawn(1);
            _intSink ^= item.Id;
            pool.Despawn(item);
        }

        private static void FastPoolStep(PerfFastObjectPool pool)
        {
            PerfFastPoolable item = pool.Spawn();
            _boolSink = item.IsActive;
            pool.Despawn(item);
        }
    }
}
#endif
