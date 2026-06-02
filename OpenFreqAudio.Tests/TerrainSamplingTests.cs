using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenFreqAudio.Tests
{
    /// <summary>
    /// Unit tests for FastPathAudioSim.SampleProfileAdaptive — the priority-queue
    /// adaptive terrain sampler. Tests exercise the structural invariants and the
    /// behaviours the rewrite was meant to fix:
    ///   - flat/linear terrain → no wasted refinement (just the coarse backbone)
    ///   - sharp features in the FIRST half of a coarse cell are still localized
    ///     (the old forward-pass dropped them)
    ///   - budget goes to the largest feature, not whatever comes first left-to-right
    ///   - chord-deviation catches a feature even when the radio ray clears it
    ///   - the sample budget cap is never exceeded
    /// </summary>
    public class TerrainSamplingTests
    {
        // Mirror of the sampler's internals so assertions track the implementation.
        private const int DesiredSamples = 512;
        private const int CoarseIntervals = 64;            // Clamp(512/8, 16, 64)
        private const int BackboneCount = CoarseIntervals + 1;

        // 120 MHz VHF link: lambda = 2.5 m.
        private const double FreqHz = 120e6;

        // Standard horizontal path geometry used by most tests.
        private const double TxX = 50.0, RxX = 1050.0, PathY = 1.5;
        private const double PathLen = RxX - TxX; // 1000 m, cellSize = 1 m

        private static List<(double dist, double elev)> Sample(
            TerrainHarness h, double txAlt = 1000.0, double rxAlt = 1000.0)
            => h.Sim.SampleProfileAdaptive(TxX, PathY, RxX, PathY, txAlt, rxAlt, FreqHz, DesiredSamples);

        // Triangular ridge centred at column x0, half-width w (cols), peak height H (m).
        private static Func<int, double> Ridge(double x0, double w, double h)
            => col => Math.Max(0.0, h * (1.0 - Math.Abs(col - x0) / w));

        [Fact]
        public void FlatTerrain_ReturnsOnlyBackbone_NoWastedRefinement()
        {
            using var h = new TerrainHarness(1100, 4, _ => 0.0);
            var p = Sample(h);

            Assert.Equal(BackboneCount, p.Count);
        }

        [Fact]
        public void LinearRamp_ReturnsOnlyBackbone_InterpolationIsExact()
        {
            // Constant slope => zero curvature => linear interpolation is exact => no refinement.
            // 1 ft/col keeps the int16-feet DEM exactly linear (no quantization curvature).
            using var h = new TerrainHarness(1100, 4, col => col * 0.3048);
            var p = Sample(h);

            Assert.Equal(BackboneCount, p.Count);
        }

        [Fact]
        public void DegeneratePath_ReturnsSinglePoint()
        {
            using var h = new TerrainHarness(64, 4, _ => 0.0);
            var p = h.Sim.SampleProfileAdaptive(10.0, 2.0, 10.2, 2.0, 100.0, 100.0, FreqHz, DesiredSamples);

            Assert.Single(p);
            Assert.Equal(0.0, p[0].dist);
        }

        [Fact]
        public void Profile_IsSortedAscending_WithinPathBounds_AndUnderBudget()
        {
            // Rough deterministic terrain to force heavy refinement.
            using var h = new TerrainHarness(1100, 4,
                col => 40.0 * Math.Sin(col * 0.3) + 25.0 * Math.Sin(col * 0.07));
            var p = Sample(h);

            Assert.True(p.Count <= DesiredSamples, $"budget exceeded: {p.Count}");
            Assert.True(p.Count > BackboneCount, "rough terrain should trigger refinement");

            Assert.Equal(0.0, p[0].dist, 6);
            Assert.True(p[^1].dist <= PathLen + 1e-6);
            for (int i = 1; i < p.Count; i++)
                Assert.True(p[i].dist > p[i - 1].dist, $"not strictly ascending at {i}");
        }

        [Fact]
        public void SharpRidge_InFirstHalf_IsLocalized()
        {
            // Peak at 25% of the path — the case the old forward-pass could miss because
            // the front half of a split cell was never re-examined.
            const double peakCol = TxX + 0.25 * PathLen; // 300
            const double height = 200.0;
            using var h = new TerrainHarness(1100, 4, Ridge(peakCol, 60.0, height));
            var p = Sample(h);

            var top = p.OrderByDescending(pt => pt.elev).First();
            double peakDist = peakCol - TxX;

            Assert.True(Math.Abs(top.dist - peakDist) < 10.0,
                $"peak localized at {top.dist:F1} m, expected ~{peakDist:F1} m");
            Assert.True(top.elev > height - 12.0,
                $"captured peak {top.elev:F1} m, expected ~{height} m");
        }

        [Fact]
        public void SharpRidge_NearReceiver_IsLocalized_BudgetNotExhaustedEarly()
        {
            // Peak at 85% — verifies refinement reaches the far end instead of spending
            // the whole budget left-to-right.
            const double peakCol = TxX + 0.85 * PathLen; // 900
            const double height = 200.0;
            using var h = new TerrainHarness(1100, 4, Ridge(peakCol, 60.0, height));
            var p = Sample(h);

            var top = p.OrderByDescending(pt => pt.elev).First();
            double peakDist = peakCol - TxX;

            Assert.True(Math.Abs(top.dist - peakDist) < 10.0,
                $"peak localized at {top.dist:F1} m, expected ~{peakDist:F1} m");
            Assert.True(top.elev > height - 12.0,
                $"captured peak {top.elev:F1} m, expected ~{height} m");
        }

        [Fact]
        public void Budget_FavorsLargerFeature_OverSmallEarlierOne()
        {
            // Small ridge near TX (15%), big ridge near RX (85%). Priority subdivision
            // should spend more samples on the big ridge despite it coming later.
            const double smallCol = TxX + 0.15 * PathLen; // 200
            const double bigCol = TxX + 0.85 * PathLen;   // 900
            using var h = new TerrainHarness(1100, 4,
                col => Math.Max(Ridge(smallCol, 50.0, 20.0)(col), Ridge(bigCol, 50.0, 300.0)(col)));
            var p = Sample(h);

            double smallDist = smallCol - TxX, bigDist = bigCol - TxX;
            int Near(double d) => p.Count(pt => Math.Abs(pt.dist - d) <= 50.0);

            Assert.True(Near(bigDist) > Near(smallDist),
                $"big ridge points={Near(bigDist)} should exceed small ridge points={Near(smallDist)}");
        }

        [Fact]
        public void ChordDeviation_CatchesFeature_EvenWhenRayClearsIt()
        {
            // High link (3 km) over a 200 m ridge — the ray clears the Fresnel zone, so a
            // ground-line "excess" detector would not refine here, but chord-deviation must
            // still localize the terrain feature for the later ray-vs-terrain check.
            const double peakCol = TxX + 0.5 * PathLen;
            const double height = 200.0;
            using var h = new TerrainHarness(1100, 4, Ridge(peakCol, 60.0, height));
            var p = Sample(h, txAlt: 3000.0, rxAlt: 3000.0);

            var top = p.OrderByDescending(pt => pt.elev).First();
            Assert.True(top.elev > height - 12.0,
                $"feature missed under clearing ray: captured {top.elev:F1} m");
            Assert.True(p.Count > BackboneCount, "feature should still trigger refinement");
        }
    }
}
