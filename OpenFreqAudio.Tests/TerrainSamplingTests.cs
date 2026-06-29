using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenFreqAudio.Tests
{
    /// <summary>
    /// Tests for FastPathAudioSim.SampleProfile — the conservative max-pyramid traversal
    /// (HeightPyramid.SampleProfile) that replaced the adaptive backbone sampler. They pin
    /// the new contract:
    ///   - terrain far below the ray prunes to an all-clear verdict with a sparse profile
    ///   - terrain entering the first Fresnel zone forces a native-resolution descent that
    ///     captures the feature (including narrow ridges the old fixed backbone could alias
    ///     past)
    ///   - the profile is strictly ascending in distance with TX/RX endpoints present
    /// </summary>
    public class TerrainSamplingTests
    {
        private const double FreqHz = 120e6; // VHF, lambda = 2.5 m
        private const double TxX = 50.0, RxX = 1050.0, PathY = 1.5;
        private const double PathLen = RxX - TxX; // 1000 m, cellSize = 1 m

        private static (List<(double dist, double elev)> profile, bool allClear) Sample(
            TerrainHarness h, double txAlt, double rxAlt)
            => h.Sim.SampleProfile(TxX, PathY, RxX, PathY, txAlt, rxAlt, FreqHz);

        // Triangular ridge centred at column x0, half-width w (cols), peak height H (m).
        private static Func<int, double> Ridge(double x0, double w, double h)
            => col => Math.Max(0.0, h * (1.0 - Math.Abs(col - x0) / w));

        [Fact]
        public void ClearPath_HighAntennas_AllClear()
        {
            using var h = new TerrainHarness(1100, 4, _ => 0.0);
            var (profile, allClear) = Sample(h, txAlt: 1000.0, rxAlt: 1000.0);

            Assert.True(allClear, "flat terrain far below the ray should prune to all-clear");
            Assert.True(profile.Count >= 2, "endpoints are always present");
            // Pruned at a coarse level → a handful of samples, not a dense native walk.
            Assert.True(profile.Count < 32, $"clear path should stay sparse, got {profile.Count}");
            Assert.True(profile.All(p => p.elev < 1.0), "clear-path samples are the (flat) ground");
        }

        [Fact]
        public void TerrainInZone_NotAllClear_CapturesPeak()
        {
            const double peakCol = TxX + 0.5 * PathLen; // 550
            const double height = 200.0;
            using var h = new TerrainHarness(1100, 4, Ridge(peakCol, 60.0, height));
            // Antennas just above the ridge so it intrudes the first Fresnel zone.
            var (profile, allClear) = Sample(h, txAlt: 210.0, rxAlt: 210.0);

            Assert.False(allClear, "a ridge inside the Fresnel zone must force a descent");
            double maxElev = profile.Max(p => p.elev);
            Assert.True(maxElev > height - 12.0, $"captured peak {maxElev:F1} m, expected ~{height} m");

            var top = profile.OrderByDescending(p => p.elev).First();
            Assert.True(Math.Abs(top.dist - 0.5 * PathLen) < 20.0,
                $"peak localized at {top.dist:F1} m, expected ~{0.5 * PathLen:F1} m");
        }

        [Fact]
        public void NarrowRidge_InZone_IsCaptured_NotAliased()
        {
            // A near-single-column spike: the old fixed backbone (~16 m spacing) could step
            // over it; the max-pyramid raises the containing cell's max, forcing a native
            // DDA that samples the spike.
            const double peakCol = TxX + 0.5 * PathLen;
            const double height = 150.0;
            using var h = new TerrainHarness(1100, 4, Ridge(peakCol, 1.0, height));
            var (profile, allClear) = Sample(h, txAlt: 160.0, rxAlt: 160.0);

            Assert.False(allClear);
            double maxElev = profile.Max(p => p.elev);
            Assert.True(maxElev > height - 12.0, $"narrow ridge missed: captured {maxElev:F1} m");
        }

        [Fact]
        public void Profile_IsAscending_WithEndpoints()
        {
            using var h = new TerrainHarness(1100, 4,
                col => 40.0 * Math.Sin(col * 0.3) + 25.0 * Math.Sin(col * 0.07) + 80.0);
            // Low link so the rough terrain stays in/near the zone and forces descents.
            var (profile, _) = Sample(h, txAlt: 120.0, rxAlt: 120.0);

            Assert.True(profile.Count >= 2);
            Assert.Equal(0.0, profile[0].dist, 6);
            Assert.True(profile[^1].dist <= PathLen + 1e-6);
            Assert.True(Math.Abs(profile[^1].dist - PathLen) < 2.0, "last sample ~ path end");
            for (int i = 1; i < profile.Count; i++)
                Assert.True(profile[i].dist > profile[i - 1].dist, $"not strictly ascending at {i}");
        }

        [Fact]
        public void DegeneratePath_ReturnsSinglePoint()
        {
            using var h = new TerrainHarness(64, 4, _ => 0.0);
            var (profile, allClear) = h.Sim.SampleProfile(10.0, 2.0, 10.2, 2.0, 100.0, 100.0, FreqHz);

            Assert.Single(profile);
            Assert.Equal(0.0, profile[0].dist);
            Assert.True(allClear);
        }
    }
}
