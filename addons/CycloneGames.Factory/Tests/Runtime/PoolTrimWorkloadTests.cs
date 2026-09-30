// Ported from CycloneGames.Factory.Tests (Unity). Keep both repositories in lockstep; see Modules/CycloneGames.Factory/README.md.
// Godot has no assembly definitions, so there is no way to keep a test assembly out of a consuming
// project build. These fixtures therefore compile only when the test host defines
// CYCLONEGAMES_FACTORY_TESTS; Tools/FactoryCheck does, and a consuming Godot project does not.
#if CYCLONEGAMES_FACTORY_TESTS
using NUnit.Framework;

namespace CycloneGames.Factory.Tests.Performance
{
    /// <summary>
    /// Report-only trim workload probe: "peak -> idle -> second peak". Timing is reported as a
    /// distribution; the deterministic <c>Diagnostics.TotalCreated</c> delta is additionally recorded
    /// as a custom sample group so the same XML carries a machine-independent counter alongside the
    /// timing, since the counter is invariant to which items eviction removes.
    /// </summary>
    public sealed class PoolTrimWorkloadTests
    {
        private const int WARMUP_COUNT = 5;
        private const int MEASUREMENT_COUNT = 15;
        private const int ITERATIONS_PER_MEASUREMENT = 1;

        private static int _workloadSink;

        [Test]
        public void PeakIdleSecondPeak_TrimWorkload()
        {
            PoolMeasure.Method(TrimWorkloadStep, WARMUP_COUNT, MEASUREMENT_COUNT);
        }

        [Test]
        public void PeakIdleSecondPeak_DeterministicCounters()
        {
            TrimWorkloadResult result = PoolWorkload.ExecuteTrimWorkload();

            // The Unity original published these through Measure.Custom so they landed in the
            // performance XML. There is no such sink here, so they are asserted against the workload's
            // own declared expectations instead — which is strictly stronger than reporting them, and
            // is what keeps this fixture from being a print statement that can never fail.
            Assert.That(result.PeakInactive, Is.EqualTo(PoolWorkload.PeakActive));
            Assert.That(result.TrimmedInactive, Is.EqualTo(PoolWorkload.TrimTarget));
            Assert.That(result.TotalCreatedAfterWorkload, Is.EqualTo(PoolWorkload.ExpectedTotalCreatedAfterWorkload));
            Assert.That(result.TotalCreatedDelta, Is.EqualTo(PoolWorkload.ExpectedTotalCreatedDelta));

            TestContext.WriteLine(
                "TrimWorkload deterministic counters: " +
                $"TotalCreatedAfter={result.TotalCreatedAfterWorkload}, " +
                $"TotalCreatedDelta={result.TotalCreatedDelta}, " +
                $"PeakInactive={result.PeakInactive}, " +
                $"TrimmedInactive={result.TrimmedInactive}");
        }

        private static void TrimWorkloadStep()
        {
            TrimWorkloadResult result = PoolWorkload.ExecuteTrimWorkload();
            _workloadSink = result.TotalCreatedAfterWorkload;
        }
    }
}
#endif
