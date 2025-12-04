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
        public float LowpassHz; // cutoff for low-pass filter (analog voice bandwidth)
        public float NoiseLevel; // 0..1 (analog static/hiss level)
        public float DropoutProb; // 0..1 (multipath fading events per second)
        public float RadioFrequencyMHz;
        
        // RF propagation parameters (for physics-based stepped-on interference)
        public float Distance_km;    // Distance from transmitter to receiver
        public float SNR_dB;         // Signal-to-noise ratio
        public float PathLoss_dB;    // Total path loss
        
        // Debug/visualization data
        public List<(double dist, double elev)>? TerrainProfile;

        public AudioParams Copy()
        {
            return new AudioParams()
            {
                Distance_km = Distance_km, DropoutProb = DropoutProb, LowpassHz = LowpassHz, NoiseLevel = NoiseLevel,
                Gain = Gain, PathLoss_dB = PathLoss_dB, RadioFrequencyMHz = RadioFrequencyMHz, SNR_dB = SNR_dB,
                TerrainProfile = TerrainProfile
            };
        }
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
        // Physical constants
        private const double EarthRadius = 6378000.0; // meters
        private const double SpeedOfLight = 299792458.0; // m/s
        private const double FourPi = 12.566370614359172; // 4 * π (precomputed)
        
        // RF modulation constants
        private const double VhfUhfBoundaryMHz = 200.0; // VHF: 30-174 MHz, UHF: 225-512 MHz
        private const float VhfBandwidthHz = 3000.0f; // AM voice bandwidth
        private const float UhfBandwidthHz = 3400.0f; // FM voice bandwidth
        
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
            double lambda = SpeedOfLight / freqHz;
            return 20.0 * Math.Log10(FourPi * distanceMeters / lambda);
        }

        // Knife-edge diffraction loss (dB)
        private static double KnifeEdgeLoss_dB(double v)
        {
            if (v < -0.78) return 0.0;
            double term = Math.Sqrt((v - 0.1) * (v - 0.1) + 1.0) + (v - 0.1);
            return 6.9 + 20.0 * Math.Log10(term);
        }

        // Helper to finalize AudioParams with common fields and optional terrain profile
        private void FinalizeAudioParams(AudioParams ap, double dist, double snrDb, double pathLossDb, 
            List<(double dist, double elev)> profile, bool includeTerrainProfile)
        {
            ap.Distance_km = (float)(dist / 1000.0);
            ap.SNR_dB = (float)snrDb;
            ap.PathLoss_dB = (float)pathLossDb;
            if (includeTerrainProfile)
                ap.TerrainProfile = profile;
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
            double txPowerDbm, double receiverSensitivityDbm, double freqHz,
            bool includeTerrainProfile = false) // Opt-in debug data
        {
            AudioParams ap = new AudioParams();
            ap.RadioFrequencyMHz = (float)freqHz / 1_000_000;

            // --- Basic geometry ---
            double dx = rxX - txX, dy = rxY - txY;
            double distSquared = dx * dx + dy * dy;
            double dist = Math.Sqrt(distSquared);
            if (dist < 1.0) dist = 1.0;

            // === Early exit for extreme distances ===
            // >500km is unrealistic for VHF/UHF tactical radio
            if (dist > 500000.0) 
            {
                ap.Gain = 0.0f;
                ap.LowpassHz = VhfBandwidthHz;
                ap.NoiseLevel = 0.95f;
                ap.DropoutProb = 0.0f;
                ap.Distance_km = (float)(dist / 1000.0);
                return ap;
            }

            // === Earth curvature and atmospheric refraction ===
            double refractivityK = CalculateKAvg(txH, rxH);
            double R_eff = EarthRadius * refractivityK;

            // === RF propagation parameters ===
            double lambda = SpeedOfLight / freqHz;
            double fspl = FSPL_dB(dist, freqHz);

            // === Terrain profile sampling (adaptive based on Fresnel zone) ===
            double r1 = 0.5 * Math.Sqrt(lambda * dist);
            double samplesAcross = (freqHz > 300e6) ? 8.0 : 5.0;
            double targetSpacing = Math.Max(Math.Min(r1 / samplesAcross, 100.0), 5.0);
            int desiredSamples = (int)Math.Min(maxSamplesPerPath, Math.Max(64, Math.Ceiling(dist / targetSpacing)));
            var profile = SampleProfileAdaptive(txX, txY, rxX, rxY, desiredSamples);

            // === Line-of-sight and diffraction analysis ===
            double txTop = SampleElevation(txX, txY) + txH;
            double rxTop = SampleElevation(rxX, rxY) + rxH;
            double worstExcess = double.NegativeInfinity;
            double worstDistFromTx = 0.0;
            double oceanFrac = 0.0;
            double inverseTwoReff = 1.0 / (2.0 * R_eff); // Precompute for loop efficiency

            foreach (var pt in profile)
            {
                // pt.dist = distance from TX to sample (meters)
                // Compute curvature bulge (meters) at this sample relative to straight chord
                // bulge = x * (D - x) / (2 * R_eff)
                double bulge = pt.dist * (dist - pt.dist) * inverseTwoReff;

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

            // === Diffraction loss calculation (knife-edge approximation) ===
            double diffLoss = 0.0;
            if (worstExcess > 0.0)
            {
                double d1 = Math.Max(1.0, worstDistFromTx);
                double d2 = Math.Max(1.0, dist - worstDistFromTx);
                double v = worstExcess * Math.Sqrt((2.0 / lambda) * ((d1 + d2) / (d1 * d2)));
                diffLoss = KnifeEdgeLoss_dB(v);
            }

            // === Total path loss ===
            double weatherLoss = weatherDbPerKm * (dist / 1000.0);
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

            double twoRayDb = 0;
            // Only proceed if path is mostly over water (oceanFrac computed earlier)
            if (oceanFrac > 0.2)
            {
                // --------------------------------------------------------------------
                // Two-ray model with Earth curvature and standard refraction
                // --------------------------------------------------------------------

                // Compute central angle between antennas (surface arc)
                double theta = dist / R_eff; // radians

                // Direct (chord) path length between antenna tops
                double Ld = Math.Sqrt(
                    (R_eff + txTop) * (R_eff + txTop) +
                    (R_eff + rxTop) * (R_eff + rxTop) -
                    2.0 * (R_eff + txTop) * (R_eff + rxTop) * Math.Cos(theta)
                );

                // Approximate specular reflection at midpoint on curved Earth
                double phi = theta / 2.0;

                double L1 = Math.Sqrt(
                    (R_eff + txTop) * (R_eff + txTop) + R_eff * R_eff -
                    2.0 * (R_eff + txTop) * R_eff * Math.Cos(phi)
                );

                double L2 = Math.Sqrt(
                    (R_eff + rxTop) * (R_eff + rxTop) + R_eff * R_eff -
                    2.0 * (R_eff + rxTop) * R_eff * Math.Cos(theta - phi)
                );

                double Lr = L1 + L2; // reflected total path
                double delta = Lr - Ld; // path length difference
                double phiRad = 2.0 * Math.PI * delta / lambda; // phase shift

                // incidence cosine at TX (approx)
                double cosInc = Math.Abs((R_eff + txTop - R_eff * Math.Cos(phi)) / Math.Max(1e-6, L1));

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

                twoRayDb = 20.0 * Math.Log10(Math.Max(1e-12, totalAmp));
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

            // --- Received power and SNR (needed for LOS checks) ---
            double prDbm = txPowerDbm - pathLossDb;
            double snrDb = Math.Clamp(prDbm - noiseFloorDbm, -20.0, 40.0);
            double gainDb;
            
            bool isVHF = ap.RadioFrequencyMHz < VhfUhfBoundaryMHz; // VHF: 30-174 MHz, UHF: 225-512 MHz

            // === STRICT LOS ENFORCEMENT (frequency-dependent) ===
            // Calculate first Fresnel zone radius at worst obstruction point
            double F1_radius = Math.Sqrt(lambda * dist / 4.0); // approximate for midpoint

            // Fresnel clearance percentage (0 = fully blocked, 1 = fully clear)
            double fresnelClearance = 1.0;
            if (worstExcess > 0)
            {
                fresnelClearance = Math.Max(0.0, 1.0 - worstExcess / (F1_radius * 1.4));
            }

            // === UHF: STRICT LOS ===
            if (!isVHF)
            {
                // HARD cutoff
                if (worstExcess > F1_radius * 0.3 || diffLoss > 6.0)
                {
                    ap.Gain = 0.0f;
                    ap.LowpassHz = UhfBandwidthHz;
                    ap.NoiseLevel = 0.95f; // Full noise floor
                    ap.DropoutProb = 0.0f; // No signal to drop out
                    FinalizeAudioParams(ap, dist, snrDb, pathLossDb, profile, includeTerrainProfile);
                    return ap;
                }
                
                // Very tight tolerance for partial obstruction
                // Even 20% Fresnel zone blockage severely degrades UHF
                if (worstExcess > F1_radius * 0.1 || diffLoss > 3.0)
                {
                    // Heavy penalty but not complete loss
                    double terrainPenaltyDb = 25.0 + diffLoss * 2.0;
                    gainDb = Math.Clamp(prDbm - receiverSensitivityDbm, -60.0, 0.0);
                    gainDb -= terrainPenaltyDb;
                    
                    ap.Gain = (float)Math.Pow(10.0, gainDb / 20.0);
                    ap.Gain = Math.Clamp(ap.Gain, 0.0f, 0.08f); // Severely limited
                    ap.LowpassHz = UhfBandwidthHz;
                    ap.NoiseLevel = 0.85f; // Very noisy
                    ap.DropoutProb = 1.2f; // Heavy fading
                    FinalizeAudioParams(ap, dist, snrDb, pathLossDb, profile, includeTerrainProfile);
                    return ap;
                }
            }
            // === VHF: MORE FORGIVING ===
            else
            {
                // Complete blockage threshold (60%+ Fresnel zone blocked)
                if (worstExcess > F1_radius * 0.6 || diffLoss > 20.0)
                {
                    ap.Gain = 0.0f;
                    ap.LowpassHz = VhfBandwidthHz;
                    ap.NoiseLevel = 0.95f;
                    ap.DropoutProb = 0.0f;
                    FinalizeAudioParams(ap, dist, snrDb, pathLossDb, profile, includeTerrainProfile);
                    return ap;
                }
                
                // Partial obstruction - smooth degradation
                if (worstExcess > F1_radius * 0.2 || diffLoss > 8.0)
                {
                    // Apply smooth degradation based on Fresnel clearance
                    double degradationFactor = Math.Pow(fresnelClearance, 2.0);
                    
                    // Moderate penalty
                    double terrainPenaltyDb = 12.0 + diffLoss * 1.0;
                    gainDb = Math.Clamp(prDbm - receiverSensitivityDbm, -60.0, 0.0);
                    gainDb -= terrainPenaltyDb;
                    
                    ap.Gain = (float)Math.Pow(10.0, gainDb / 20.0);
                    ap.Gain = (float)(ap.Gain * degradationFactor);
                    ap.Gain = Math.Clamp(ap.Gain, 0.0f, 0.35f);
                    
                    ap.LowpassHz = VhfBandwidthHz;
                    ap.NoiseLevel = (float)(0.55 + (1.0 - degradationFactor) * 0.30);
                    ap.DropoutProb = (float)(0.4 + (1.0 - degradationFactor) * 0.5);
                    FinalizeAudioParams(ap, dist, snrDb, pathLossDb, profile, includeTerrainProfile);
                    return ap;
                }
            }

            // === CLEAN LOS PATH - Normal operation ===
            gainDb = Math.Clamp(prDbm - receiverSensitivityDbm, -60.0, 0.0);
            ap.Gain = (float)Math.Pow(10.0, gainDb / 20.0);

            // === ANALOG MODULATION CHARACTERISTICS ===
            
            if (isVHF)
            {
                // === VHF AM (30-174 MHz): Amplitude Modulation ===
                ap.LowpassHz = VhfBandwidthHz; // AM voice bandwidth (~300-3000 Hz)
                
                // Analog static increases smoothly with decreasing SNR
                if (snrDb > 20.0)
                {
                    ap.NoiseLevel = 0.02f; // Clean signal
                }
                else if (snrDb > 10.0)
                {
                    // Light static: 0.02 → 0.20
                    ap.NoiseLevel = (float)(0.02 + (20.0 - snrDb) / 10.0 * 0.18);
                }
                else if (snrDb > 0.0)
                {
                    // Heavy static: 0.20 → 0.55
                    ap.NoiseLevel = (float)(0.20 + (10.0 - snrDb) / 10.0 * 0.35);
                }
                else
                {
                    // Barely intelligible: 0.55 → 0.85
                    ap.NoiseLevel = (float)(0.55 + Math.Min(-snrDb / 20.0, 0.30));
                }
            }
            else
            {
                // === UHF FM (225-512 MHz): Frequency Modulation ===
                ap.LowpassHz = UhfBandwidthHz; // FM voice bandwidth (~300-3400 Hz)
                
                // FM "quieting" - noise suppression improves with stronger signal
                // FM threshold effect: below ~10 dB SNR, noise increases rapidly
                if (snrDb > 15.0)
                {
                    ap.NoiseLevel = 0.01f; // Excellent FM quieting
                }
                else if (snrDb > 10.0)
                {
                    // Good quieting: 0.01 → 0.10
                    ap.NoiseLevel = (float)(0.01 + (15.0 - snrDb) / 5.0 * 0.09);
                }
                else if (snrDb > 5.0)
                {
                    // FM threshold region: 0.10 → 0.40
                    ap.NoiseLevel = (float)(0.10 + (10.0 - snrDb) / 5.0 * 0.30);
                }
                else if (snrDb > 0.0)
                {
                    // Below FM threshold: 0.40 → 0.75
                    ap.NoiseLevel = (float)(0.40 + (5.0 - snrDb) / 5.0 * 0.35);
                }
                else
                {
                    // Very weak signal: 0.75 → 0.95
                    ap.NoiseLevel = (float)(0.75 + Math.Min(-snrDb / 10.0, 0.20));
                }
            }

            // === MULTIPATH FADING AND DROPOUTS ===
            double dropout;
            if (snrDb > 15.0)
            {
                dropout = 0.0; // Clean signal, no fading
            }
            else if (snrDb > 5.0)
            {
                // Light fading at marginal SNR
                dropout = (15.0 - snrDb) / 10.0 * 0.3; // 0→0.3 events/sec
            }
            else if (snrDb > 0.0)
            {
                // Moderate fading
                dropout = 0.3 + (5.0 - snrDb) / 5.0 * 0.4; // 0.3→0.7 events/sec
            }
            else
            {
                // Heavy fading
                dropout = 0.7 + Math.Min(-snrDb / 10.0, 0.5); // 0.7→1.2 events/sec
            }

            // VHF is less affected by multipath (longer wavelength)
            if (isVHF)
                dropout *= 0.7;

            ap.DropoutProb = (float)Math.Clamp(dropout, 0.0, 1.5);

            // Finalize and return
            FinalizeAudioParams(ap, dist, snrDb, pathLossDb, profile, includeTerrainProfile);
            return ap;
        }

        public static double CalculateKAvg(double senderAltitude, double receiverAltitude)
        {
            // Calculates average atmospheric refractivity factor k_avg
            // Based on SAND2012-10690, section 3.2.3
            // Used to compute effective Earth radius: R_eff = k_avg * R_earth

            if (senderAltitude < 0) senderAltitude = 0;
            if (receiverAltitude < 0) receiverAltitude = 0;
            
            double altitudeDiff = senderAltitude - receiverAltitude;
            if (altitudeDiff == 0) return 1.0;

            // Atmospheric refractivity constants
            const double N_s = 324.8; // Average global surface refractivity (Altshuler)
            const double h_b = 12192; // Breakpoint altitude in meters (~40k ft)
            const double N_b = 66.65; // Breakpoint refractivity
            const double LogNsOverNb = 1.5829767628777844; // Precomputed Math.Log(N_s / N_b)
            
            double H_b = (h_b - receiverAltitude) / LogNsOverNb;
            double altDiffOverHb = altitudeDiff / H_b;

            // psi_g = 0 (grazing angle negligible per section 3.2)
            // Therefore Math.Cos(psi_g) = 1.0
            double term1 = (1e-6 * N_s * EarthRadius) / H_b;
            double term3 = Math.Exp(altDiffOverHb) - 1.0;
            double kAvg = 1.0 / (1.0 - term1 * (altDiffOverHb / term3));

            return kAvg;
        }
    }
}