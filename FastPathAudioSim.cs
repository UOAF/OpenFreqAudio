// Lightweight DEM-based path-loss -> audio-params proof-of-concept
// - Assumes DEM mipmaps: mipmaps[0] = finest (native), mipmaps[last] = coarsest.
// - Uses bilinear sampling and an adaptive sample/refine policy.
// - Returns AudioParams (gain linear, lowpassHz, noiseLevel [0..1], dropoutProb).

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace BMSAudioSim
{
    public class AudioParams
    {
        public float Gain; // linear gain
        public float LowpassHz; // cutoff for low-pass filter
        public float NoiseLevel; // 0..1 (amount of added noise)
        public float DropoutProb; // 0..1 (chance of packet drop / glitch)
        public float FlutterDepth; // 0..1 amplitude flutter depth

        // Debug/visualization data
        public List<(double dist, double elev)>? TerrainProfile;
    }

    // ================================================================
    // Fast, memory-mapped DEM reader (cross-platform)
    // ================================================================
    public class DEMReader : IDisposable
    {
        private readonly MemoryMappedFile mmf;
        private readonly MemoryMappedViewAccessor accessor;
        private int width { get; }
        private int height { get; }
        private readonly int bytesPerSample;
        private readonly long headerBytes;
        public int Width => width;
        public int Height => height;

        public DEMReader(string path, int width, int height, int bytesPerSample = 2, long headerBytes = 0)
        {
            this.width = width;
            this.height = height;
            this.bytesPerSample = bytesPerSample;
            this.headerBytes = headerBytes;

            mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null);
            accessor = mmf.CreateViewAccessor();
        }

        public float Sample(int row, int col)
        {
            if (row < 0 || row >= height || col < 0 || col >= width)
                return 0f;
            long offset = headerBytes + ((long)row * width + col) * bytesPerSample;
            return accessor.ReadInt16(offset) * 0.3048f; // ft to m
        }

        public void Dispose()
        {
            accessor?.Dispose();
            mmf?.Dispose();
        }
    }

    // ================================================================
    // Main audio-path propagation simulator
    // ================================================================
    public class FastPathAudioSim
    {
        private readonly DEMReader dem;
        private readonly double originX, originY, cellSizeMeters;
        private readonly int maxSamplesPerPath = 4096;
        private readonly double weatherDbPerKm = 0.02;

        public FastPathAudioSim(DEMReader dem, double originX, double originY, double cellSizeMeters)
        {
            this.dem = dem;
            this.originX = originX;
            this.originY = originY;
            this.cellSizeMeters = cellSizeMeters;
        }

        public double PixelsToMeters(double pixelDistance)
        {
            return pixelDistance * cellSizeMeters;
        }

        // Bilinear elevation sampling
        private double SampleElevation(double xMeters, double yMeters)
        {
            // Convert world coordinates → DEM pixel coordinates
            double gx = (xMeters - originX) / cellSizeMeters;
            double gy = (yMeters - originY) / cellSizeMeters;

            int col = (int)Math.Floor(gx);
            int row = (int)Math.Floor(gy);
            double fx = gx - col;
            double fy = gy - row;

            // Bilinear interpolation — sample four nearest grid points
            float v00 = dem.Sample(row, col);
            float v10 = dem.Sample(row, col + 1);
            float v01 = dem.Sample(row + 1, col);
            float v11 = dem.Sample(row + 1, col + 1);

            // Interpolate horizontally and vertically
            double v0 = v00 * (1 - fx) + v10 * fx;
            double v1 = v01 * (1 - fx) + v11 * fx;
            double val = v0 * (1 - fy) + v1 * fy;

            return val;
        }

        // Free-space path loss (dB)
        private static double FSPL_dB(double distanceMeters, double freqHz)
        {
            if (distanceMeters < 1.0) distanceMeters = 1.0;
            double c = 299792458.0;
            double lambda = c / freqHz;
            return 20.0 * Math.Log10(4.0 * Math.PI * distanceMeters / lambda);
        }

        // Knife-edge diffraction loss (dB)
        private static double KnifeEdgeLoss_dB(double v)
        {
            if (v < -0.78) return 0.0;
            double term = Math.Sqrt((v - 0.1) * (v - 0.1) + 1.0) + (v - 0.1);
            return 6.9 + 20.0 * Math.Log10(term);
        }

        // Adaptive sampling of terrain along the line
        private List<(double dist, double elev)> SampleProfileAdaptive(
            double txX, double txY, double rxX, double rxY, int desiredSamples)
        {
            var result = new List<(double, double)>();
            double dx = rxX - txX, dy = rxY - txY;
            double D = Math.Sqrt(dx * dx + dy * dy);
            if (D < 1.0)
            {
                result.Add((0.0, SampleElevation(txX, txY)));
                return result;
            }

            int coarseSamples = Math.Min(desiredSamples, 256);
            for (int i = 0; i <= coarseSamples; i++)
            {
                double t = i / (double)coarseSamples;
                double sx = txX + t * dx;
                double sy = txY + t * dy;
                double elev = SampleElevation(sx, sy);
                result.Add((t * D, elev));
            }

            double txBase = SampleElevation(txX, txY);
            double rxBase = SampleElevation(rxX, rxY);
            for (int i = 0; i + 1 < result.Count && result.Count < desiredSamples; i++)
            {
                var (distA, elevA) = result[i];
                var (distB, elevB) = result[i + 1];

                double midDist = 0.5 * (distA + distB);
                double frac = midDist / D;
                double losMid = (1 - frac) * txBase + frac * rxBase;
                double terrainMid = SampleElevation(txX + (midDist / D) * dx, txY + (midDist / D) * dy);

                if (terrainMid > losMid - 200.0)
                {
                    result.Insert(i + 1, (midDist, terrainMid));
                    i = Math.Max(-1, i - 2);
                }
            }

            if (result.Count > desiredSamples)
                result.RemoveRange(desiredSamples, result.Count - desiredSamples);

            return result;
        }


        // ================================================================
        // Main path computation
        // ================================================================
        public AudioParams ComputeAudioForPath(
            double txX, double txY, double txH,
            double rxX, double rxY, double rxH,
            double txPowerDbm, double receiverSensitivityDbm, double freqHz)
        {
            AudioParams ap = new AudioParams();

            // -- Earth curvature --
            const double earthRadius = 6371000.0; // meters
            const double refractivityK = 4.0 / 3.0; // standard effective earth radius factor
            double R_eff = earthRadius * refractivityK;

            // --- Basic geometry ---
            double dx = rxX - txX, dy = rxY - txY;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist < 1.0) dist = 1.0;

            // --- Free-space path loss baseline ---
            double fspl = FSPL_dB(dist, freqHz);
            double c = 299792458.0;
            double lambda = c / freqHz;

            // --- Adaptive sampling ---
            double r1 = 0.5 * Math.Sqrt(lambda * dist);
            double samplesAcross = (freqHz > 300e6) ? 8.0 : 5.0;
            double targetSpacing = Math.Max(Math.Min(r1 / samplesAcross, 100.0), 5.0);
            int desiredSamples = (int)Math.Min(maxSamplesPerPath, Math.Max(64, Math.Ceiling(dist / targetSpacing)));

            // --- Sample terrain profile ---
            var profile = SampleProfileAdaptive(txX, txY, rxX, rxY, desiredSamples);

            // --- LOS and diffraction analysis ---
            double txTop = SampleElevation(txX, txY) + txH;
            double rxTop = SampleElevation(rxX, rxY) + rxH;
            double worstExcess = double.NegativeInfinity;
            double worstDistFromTx = 0.0;
            double oceanFrac = 0.0;

            foreach (var pt in profile)
            {
                // pt.dist = distance from TX to sample (meters)
                // Compute curvature bulge (meters) at this sample relative to straight chord
                // bulge = x * (D - x) / (2 * R_eff)
                double bulge = (pt.dist * (dist - pt.dist)) / (2.0 * R_eff);

                // effective ground elevation includes curvature bulge
                double effectiveGround = pt.elev + bulge;

                // LOS line between antenna tops (straight line)
                double zLine = txTop + (pt.dist / dist) * (rxTop - txTop);

                // compare effective ground to LOS
                double excess = effectiveGround - zLine;
                if (excess > worstExcess)
                {
                    worstExcess = excess;
                    worstDistFromTx = pt.dist;
                }
            }

            oceanFrac /= profile.Count;

            // --- Diffraction ---
            double diffLoss = 0.0;
            if (worstExcess > 0.0)
            {
                double d1 = Math.Max(1.0, worstDistFromTx);
                double d2 = Math.Max(1.0, dist - worstDistFromTx);
                double v = worstExcess * Math.Sqrt((2.0 / lambda) * ((d1 + d2) / (d1 * d2)));
                diffLoss = KnifeEdgeLoss_dB(v);
            }

            // --- Weather attenuation ---
            double weatherLoss = weatherDbPerKm * (dist / 1000.0);

            // --- Total path loss ---
            double pathLossDb = fspl + diffLoss + weatherLoss;

            // --- Tunables (adjust to taste) ---
            const double seaR0 = 0.95; // baseline seawater reflection magnitude (0..1)
            const double Hscale = 200.0; // altitude softening scale (meters) - larger => slower softening
            const double Dscale = 200000.0; // distance softening scale (meters) - larger => slower softening
            const double sigmaSeaDefault = 0.05; // default sea rms (m) when no data (calm sea)

// --- Cheap specular point: midpoint approximation ---
            double specX = 0.5 * (txX + rxX);
            double specY = 0.5 * (txY + rxY);
            double specElev = SampleElevation(specX, specY);

// Only proceed if path is mostly over water (oceanFrac computed earlier)
            if (oceanFrac > 0.2)
            {
                // --------------------------------------------------------------------
                // Two-ray model with Earth curvature and standard refraction
                // --------------------------------------------------------------------
                const double Re = 6371000.0; // mean Earth radius (m)
                const double kRef = 4.0 / 3.0; // effective refraction factor
                double Reff = Re * kRef;

                // Compute central angle between antennas (surface arc)
                double theta = dist / Reff; // radians

                // Direct (chord) path length between antenna tops
                double Ld = Math.Sqrt(
                    (Reff + txTop) * (Reff + txTop) +
                    (Reff + rxTop) * (Reff + rxTop) -
                    2.0 * (Reff + txTop) * (Reff + rxTop) * Math.Cos(theta)
                );

                // Approximate specular reflection at midpoint on curved Earth
                double phi = theta / 2.0;

                double L1 = Math.Sqrt(
                    (Reff + txTop) * (Reff + txTop) + Reff * Reff -
                    2.0 * (Reff + txTop) * Reff * Math.Cos(phi)
                );

                double L2 = Math.Sqrt(
                    (Reff + rxTop) * (Reff + rxTop) + Reff * Reff -
                    2.0 * (Reff + rxTop) * Reff * Math.Cos(theta - phi)
                );

                double Lr = L1 + L2; // reflected total path
                double delta = Lr - Ld; // path length difference
                double phiRad = 2.0 * Math.PI * delta / lambda; // phase shift

                // incidence cosine at TX (approx)
                double cosInc = Math.Abs((Reff + txTop - Reff * Math.Cos(phi)) / Math.Max(1e-6, L1));


                // simple sea sigma (we don't have wind/roughness data)
                double sigmaSea = sigmaSeaDefault;

                // Debye roughness factor (amplitude reduction factor sqrt of power factor)
                double debPow = Math.Exp(-Math.Pow((4.0 * Math.PI * sigmaSea * cosInc / lambda), 2.0));
                debPow = Math.Max(1e-8, debPow); // power factor (0..1)
                double debAmp = Math.Sqrt(debPow); // amplitude factor

                // altitude & distance softening (amplitude multipliers)
                double altSoft = Math.Exp(-(txH + rxH) / Hscale); // reduces coherence with increasing heights
                double distSoft = Math.Exp(-dist / Dscale); // reduces coherence for very long hops

                // ocean fraction scaling (mixed land/sea reduces coherent reflection)
                double oceanScale = Math.Clamp(oceanFrac, 0.0, 1.0);

                // effective reflection amplitude (signed: negative for phase inversion at grazing)
                double ReffMag = seaR0 * debAmp * altSoft * distSoft * oceanScale;
                double R = -ReffMag; // negative for typical phase inversion at grazing (tunable)

                double totalAmp = Math.Sqrt(1.0 + R * R + 2.0 * R * Math.Cos(phiRad));

                double twoRayDb = 20.0 * Math.Log10(Math.Max(1e-12, totalAmp));
                twoRayDb = Math.Clamp(twoRayDb, -20.0, 6.0);

                // Adjust total path loss
                pathLossDb -= twoRayDb;
            }

            // --- Simplified noise floor estimation ---
            // Thermal noise floor = -174 dBm/Hz + 10*log10(B)
            double bandwidthHz = 3000.0; // typical AM voice channel
            double thermalNoise = -174.0 + 10.0 * Math.Log10(bandwidthHz); // ≈ -139.2 dBm
            double receiverNoiseFigure = 7.0; // dB
            double noiseFloorDbm = thermalNoise + receiverNoiseFigure; // ≈ -132 dBm typical

            // --- Received power and SNR ---
            double prDbm = txPowerDbm - pathLossDb;
            double snrDb = Math.Clamp(prDbm - noiseFloorDbm, -20.0, 40.0);

            // === Audio mappings ===
            double gainDb = Math.Clamp(prDbm - receiverSensitivityDbm, -60.0, 0.0);
            ap.Gain = (float)Math.Pow(10.0, gainDb / 20.0);

            double cutoff = 300.0 * Math.Pow(2.0, (snrDb + 20.0) / 10.0);
            ap.LowpassHz = (float)Math.Clamp(cutoff, 300.0, 8000.0);

            double noise = Math.Clamp((30.0 - snrDb) / 50.0, 0.0, 1.0);
            ap.NoiseLevel = (float)noise;

            // --- Dropout probability mapping tuned for FM voice realism ---
            double dropout;

            // Logistic curve centered lower (~5 dB) and shallower slope (~3 dB)
            dropout = 1.0 / (1.0 + Math.Exp((snrDb - 5.0) / 3.0));

            // Slightly soften the curve to keep comms intelligible down to ~3 dB
            dropout = Math.Pow(dropout, 1.8);

            // Add diffraction penalty if significant terrain obstruction exists
            if (worstExcess > 50.0)
                dropout = Math.Min(1.0, dropout + 0.25);

            // Clamp final result
            ap.DropoutProb = (float)Math.Clamp(dropout, 0.0, 1.0);

            double flutter = Math.Min(1.0, Math.Abs(diffLoss) / 25.0);
            ap.FlutterDepth = (float)flutter;

            ap.TerrainProfile = profile;
            return ap;
        }
    }
}