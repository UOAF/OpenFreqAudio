using System;

namespace OpenFreqAudio.Tests
{
    /// <summary>
    /// Geometry tests for the two-ray specular path (FastPathAudioSim.TwoRaySpecularGeometry).
    /// The curved-earth path-length excess should approach the flat-earth two-ray limit
    /// delta ≈ 2·h1·h2/d for short paths, fall with distance, and yield a shallow grazing
    /// angle for low antennas over a long path.
    /// </summary>
    public class TwoRayTests
    {
        private const double REff = 8504000.0; // k = 4/3 * 6378 km

        [Fact]
        public void SpecularDelta_EqualHeights_MatchesFlatEarthLimit()
        {
            var (delta, _, _, _) = FastPathAudioSim.TwoRaySpecularGeometry(100.0, 100.0, 10000.0, REff);
            // Flat-earth two-ray: delta ≈ 2·h1·h2/d = 2.0 m; curved earth a touch less.
            Assert.InRange(delta, 1.8, 2.05);
        }

        [Fact]
        public void SpecularDelta_UnequalHeights_MatchesFlatEarthLimit()
        {
            var (delta, _, _, _) = FastPathAudioSim.TwoRaySpecularGeometry(100.0, 50.0, 10000.0, REff);
            // delta ≈ 2·h1·h2/d = 1.0 m.
            Assert.InRange(delta, 0.85, 1.05);
        }

        [Fact]
        public void SpecularDelta_DecreasesWithDistance()
        {
            double near = FastPathAudioSim.TwoRaySpecularGeometry(100.0, 100.0, 10000.0, REff).delta;
            double far = FastPathAudioSim.TwoRaySpecularGeometry(100.0, 100.0, 20000.0, REff).delta;
            Assert.True(far < near, $"delta should fall with distance: near={near:F3}, far={far:F3}");
            Assert.True(far > 0.0);
        }

        [Fact]
        public void GrazingAngle_IsSmallForLowGrazing()
        {
            var (_, _, _, sinPsi) = FastPathAudioSim.TwoRaySpecularGeometry(100.0, 100.0, 50000.0, REff);
            // 100 m antennas over a 50 km path → shallow grazing.
            Assert.InRange(sinPsi, 0.0, 0.05);
        }
    }
}
