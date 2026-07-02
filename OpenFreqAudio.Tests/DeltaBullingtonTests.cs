using System;
using System.Collections.Generic;

namespace OpenFreqAudio.Tests
{
    /// <summary>
    /// Tests for the delta-Bullington diffraction model (ITU-R P.526-16 / ERDC TR-22-1).
    /// Pins the behaviours the rewrite was meant to fix: a grazing edge is no longer 0 dB,
    /// deeper obstructions lose more, clear high paths are ~free space, and the model is
    /// reciprocal.
    /// </summary>
    public class DeltaBullingtonTests
    {
        private const double Lambda = 2.5;     // 120 MHz
        private const double REff = 8504000.0; // k = 4/3 * 6378 km

        private static List<(double dist, double elev)> Profile(double dpM, int n, Func<double, double> elev)
        {
            var p = new List<(double dist, double elev)>(n);
            for (int i = 0; i < n; i++)
            {
                double d = dpM * i / (n - 1);
                p.Add((d, elev(d)));
            }
            return p;
        }

        // Triangular ridge centred at xc (m), half-width w (m), peak h (m).
        private static Func<double, double> Ridge(double xc, double w, double h)
            => d => Math.Max(0.0, h * (1.0 - Math.Abs(d - xc) / w));

        [Fact]
        public void ClearHighPath_LossNearZero()
        {
            var p = Profile(10000.0, 11, _ => 0.0);
            double loss = DeltaBullington.Loss(p, 500.0, 500.0, Lambda, REff);
            Assert.True(loss < 1.0, $"clear high LOS path should be ~0 dB, got {loss:F2}");
        }

        [Fact]
        public void GrazingEdge_IsNotZero()
        {
            // A ridge grazing the LOS used to read as 0 dB (the top-of-zone clearance bug).
            // Delta-Bullington must report a real knife-edge + correction loss.
            var p = Profile(20000.0, 21, Ridge(10000.0, 2000.0, 97.0));
            double loss = DeltaBullington.Loss(p, 100.0, 100.0, Lambda, REff);
            Assert.True(loss > 5.0, $"grazing edge should not be ~0 dB, got {loss:F2}");
            Assert.True(loss < 60.0, $"grazing edge loss implausibly large: {loss:F2}");
        }

        [Fact]
        public void DeeperObstruction_LosesMoreThanGrazing()
        {
            double graze = DeltaBullington.Loss(
                Profile(20000.0, 21, Ridge(10000.0, 2000.0, 97.0)), 100.0, 100.0, Lambda, REff);
            double deep = DeltaBullington.Loss(
                Profile(20000.0, 21, Ridge(10000.0, 2000.0, 250.0)), 100.0, 100.0, Lambda, REff);
            Assert.True(deep > graze + 5.0, $"deeper obstruction should lose more: graze={graze:F1}, deep={deep:F1}");
        }

        [Fact]
        public void Reciprocity_ForwardEqualsReverse()
        {
            const double dp = 30000.0;
            const int n = 31;
            var fwd = Profile(dp, n, Ridge(5000.0, 3000.0, 150.0));
            var rev = new List<(double dist, double elev)>(n);
            for (int j = 0; j < n; j++)
            {
                var src = fwd[n - 1 - j];
                rev.Add((dp - src.dist, src.elev));
            }

            double lf = DeltaBullington.Loss(fwd, 120.0, 80.0, Lambda, REff);
            double lr = DeltaBullington.Loss(rev, 80.0, 120.0, Lambda, REff);

            Assert.True(lf > 1.0, $"test geometry should be obstructed, got fwd={lf:F2}");
            Assert.True(Math.Abs(lf - lr) < 1.0, $"reciprocity: fwd={lf:F2}, rev={lr:F2}");
        }
    }
}
