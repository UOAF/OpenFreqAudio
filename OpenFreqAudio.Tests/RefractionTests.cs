using System;

namespace OpenFreqAudio.Tests
{
    /// <summary>
    /// Tests for the atmospheric refraction model (FastPathAudioSim.CalculateKAvg, SAND2012-10690
    /// §3.2.3) and the propagation math that depends on the effective Earth radius it produces.
    /// </summary>
    public class RefractionTests
    {
        // Altitudes (m MSL) to sweep: below sea level, the old breakpoint where k went negative,
        // and up past the report's 50 kft design range.
        private static readonly double[] Altitudes =
        [
            -50.0, 0.0, 1.0, 600.0, 3000.0, 6000.0, 8911.0, 9000.0, 11000.0, 11958.0,
            12191.0, 12192.0, 12193.0, 13000.0, 15240.0, 20000.0
        ];

        [Theory]
        // SAND2012-10690 eq 37 (method 2) with h_s = 0 and N_s = 313, as plotted in Figure 8:
        // ~1.33 at 1 kft, ~1.26 at 10 kft, ~1.09 at 50 kft.
        [InlineData(304.8, 1.3305)]
        [InlineData(3048.0, 1.2617)]
        [InlineData(15240.0, 1.0903)]
        public void KAvg_GroundToAir_MatchesReportFigure8(double aircraftAltitude, double expected)
        {
            double k = FastPathAudioSim.CalculateKAvg(0.0, aircraftAltitude, surfaceRefractivity: 313.0);
            Assert.InRange(k, expected - 5e-4, expected + 5e-4);
        }

        [Fact]
        public void KAvg_IsSymmetric()
        {
            // A link bends the same way in both directions.
            foreach (double a in Altitudes)
            foreach (double b in Altitudes)
                Assert.Equal(FastPathAudioSim.CalculateKAvg(a, b), FastPathAudioSim.CalculateKAvg(b, a));
        }

        [Fact]
        public void KAvg_StaysBetweenNoRefractionAndSeaLevel()
        {
            // Refraction always bends rays toward the Earth (k > 1), and bends them hardest at sea
            // level, where k = 1 / (1 - 1e-6 N_s R_e / H_b) ≈ 1.368 for N_s = 324.8.
            double seaLevel = FastPathAudioSim.CalculateKAvg(0.0, 0.0);
            Assert.InRange(seaLevel, 1.3677, 1.3687);

            foreach (double a in Altitudes)
            foreach (double b in Altitudes)
                Assert.InRange(FastPathAudioSim.CalculateKAvg(a, b), 1.0, seaLevel);
        }

        [Fact]
        public void KAvg_IsContinuousAcrossEqualAltitudes()
        {
            // No special case where the altitudes meet: nudging one antenna barely moves k.
            foreach (double h in Altitudes)
            {
                double equal = FastPathAudioSim.CalculateKAvg(h, h);
                Assert.InRange(FastPathAudioSim.CalculateKAvg(h, h + 1.0) - equal, -1e-4, 1e-4);
                Assert.InRange(FastPathAudioSim.CalculateKAvg(h, h + 1e-3) - equal, -1e-6, 1e-6);
            }
        }

        [Fact]
        public void CalculateAudioParams_HighReceiverLowTransmitterOverSea_IsFinite()
        {
            // Field bug in 1.1.0: an F-15 at 11,958 m hearing a transmitter at 596 m,
            // 102.9 km away over water. k came out at -0.0009 (R_eff = -5.9 km),
            // the two-ray divergence factor took the square root of a negative number,
            // and the NaN SNR latched that radio's squelch shut for the rest of the session.
            const double txAlt = 596.0, rxAlt = 11958.0;
            Assert.InRange(FastPathAudioSim.CalculateKAvg(txAlt, rxAlt), 1.12, 1.125);

            const double cell = 100.0;
            using var h = new TerrainHarness(1200, 4, _ => -10.0, cell); // all ocean
            const double txX = 5000.0, y = 150.0;
            double rxX = txX + Math.Sqrt(102900.0 * 102900.0 - (rxAlt - txAlt) * (rxAlt - txAlt));

            var ap = h.Sim.CalculateAudioParams(txX, y, txAlt, rxX, y, rxAlt, 396825,
                txAltitudeIsMSL: true, rxAltitudeIsMSL: true);

            Assert.True(float.IsFinite(ap.ReceivedDb), $"ReceivedDb = {ap.ReceivedDb}");
            Assert.True(float.IsFinite(ap.ReceivedSnrDb), $"ReceivedSnrDb = {ap.ReceivedSnrDb}");
        }
    }
}
