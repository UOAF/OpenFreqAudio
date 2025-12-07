// - Assumes DEM mipmaps: mipmaps[0] = finest (native), mipmaps[last] = coarsest.
// - Uses bilinear sampling and an adaptive sample/refine policy.
// - Returns AudioParams (gain linear, lowpassHz, noiseLevel [0..1], dropoutProb).
//
// PHYSICS MODEL:
// - Knife-edge diffraction theory (ITU-R P.526) as foundation
// - Smooth continuous degradation
// - Wavelength-dependent corrections for VHF (better diffraction) vs UHF (more LOS-dependent)

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using Microsoft.Extensions.Logging;

namespace BMSAudioSim
{
    // ================================================================
    // Object pool for AudioParams to reduce allocations
    // ================================================================
    public class AudioParamsPool
    {
        private readonly ConcurrentBag<AudioParams> _pool = new();
        private int _count = 0;
        private readonly int _maxPoolSize;

        public AudioParamsPool(int maxPoolSize = 100)
        {
            _maxPoolSize = maxPoolSize;
        }

        public AudioParams Rent()
        {
            if (_pool.TryTake(out var obj))
            {
                return obj;
            }

            return new AudioParams();
        }

        public void Return(AudioParams obj)
        {
            if (obj == null) return;

            // Reset the object
            obj.Gain = 0;
            obj.LowpassHz = 0;
            obj.NoiseLevel = 0;
            obj.DropoutRate = 0;
            obj.DeepFadeRate = 0;
            obj.RadioFrequencyMHz = 0;
            obj.Distance_km = 0;
            obj.SNR_dB = 0;
            obj.PathLoss_dB = 0;
            obj.TerrainProfile = null;

            // Only return to pool if we haven't exceeded max size
            if (_count < _maxPoolSize)
            {
                _pool.Add(obj);
                System.Threading.Interlocked.Increment(ref _count);
            }
        }
    }

    // ================================================================
    public class AudioParams
    {
        public float Gain; // linear gain
        public float LowpassHz; // cutoff for low-pass filter (analog voice bandwidth)
        public float NoiseLevel; // 0..1 (analog static/hiss level)
        public float DropoutRate; // fast multipath flutter (events per second, can exceed 1.0)
        public float DeepFadeRate; // slow deep fades (events per second, typically 0-0.5)
        public float RadioFrequencyMHz;

        // RF propagation parameters (for physics-based stepped-on interference)
        public float Distance_km; // Distance from transmitter to receiver
        public float SNR_dB; // Signal-to-noise ratio
        public float PathLoss_dB; // Total path loss

        // Debug/visualization data
        public List<(double dist, double elev)>? TerrainProfile;

        public AudioParams Copy()
        {
            return new AudioParams()
            {
                Distance_km = Distance_km, DropoutRate = DropoutRate, DeepFadeRate = DeepFadeRate,
                LowpassHz = LowpassHz, NoiseLevel = NoiseLevel,
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

        // Physics calibration parameters (tunable based on field measurements)
        private const double VhfDiffractionBonus_dB = 3.0; // VHF diffracts better than knife-edge theory predicts
        private const double UhfDiffractionPenalty_dB = 6.0; // UHF is more LOS-dependent
        private const double MinimumGainDb = -60.0; // Below this, signal is completely lost

        private readonly DEMReader dem;
        private readonly double originX, originY, cellSizeMeters;
        private readonly int maxSamplesPerPath = 512;
        private readonly double weatherDbPerKm = 0.02;
        private readonly ILogger<FastPathAudioSim> _logger;
        private readonly AudioParamsPool paramsPool;

        public FastPathAudioSim(DEMReader dem, double originX, double originY, double cellSizeMeters,
            ILogger<FastPathAudioSim> logger, AudioParamsPool paramsPool = null)
        {
            this.dem = dem;
            this.originX = originX;
            this.originY = originY;
            this.cellSizeMeters = cellSizeMeters;
            this.paramsPool = paramsPool ?? new AudioParamsPool();
            _logger = logger;
        }

        public double PixelsToMeters(double pixelDistance)
        {
            return pixelDistance * cellSizeMeters;
        }

        /// <summary>
        /// Return an AudioParams object to the pool for reuse.
        /// Call this when you're done using an AudioParams object to reduce allocations.
        /// </summary>
        public void ReturnAudioParams(AudioParams ap)
        {
            paramsPool.Return(ap);
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

        // Knife-edge diffraction loss (dB) - ITU-R P.526
        private static double KnifeEdgeLoss_dB(double v)
        {
            if (v < -0.78) return 0.0;
            double term = Math.Sqrt((v - 0.1) * (v - 0.1) + 1.0) + (v - 0.1);
            return 6.9 + 20.0 * Math.Log10(term);
        }

        /// <summary>
        /// Calculate the thermal noise floor amplitude for background noise playback.
        /// This is the noise level heard through speakers when squelch is open but no signal present.
        /// </summary>
        /// <param name="frequencyMHz">Radio frequency in MHz</param>
        /// <param name="receiverSensitivityDbm">Receiver sensitivity in dBm (optional, uses defaults if not provided)</param>
        /// <returns>Background noise amplitude (0.0 to 1.0 scale where 1.0 = 0 dBm)</returns>
        public static float CalculateBackgroundNoiseAmplitude(double frequencyMHz,
            double? receiverSensitivityDbm = null)
        {
            bool isVHF = frequencyMHz < VhfUhfBoundaryMHz;

            // Use provided sensitivity or default values
            double rxSensitivity = receiverSensitivityDbm ?? (isVHF ? -113.0 : -107.0);

            // Thermal noise floor calculation
            double bandwidthHz = isVHF ? 3000.0 : 3400.0;
            double thermalNoise = -174.0 + 10.0 * Math.Log10(bandwidthHz); // ≈ -139.2 dBm
            double receiverNoiseFigure = 7.0; // dB
            double noiseFloorDbm = thermalNoise + receiverNoiseFigure; // ≈ -132 dBm

            // Noise floor relative to receiver sensitivity
            double noiseRelativeDb = noiseFloorDbm - rxSensitivity;
            // VHF (default -113 dBm): -132 - (-113) = -19 dB
            // UHF (default -107 dBm): -132 - (-107) = -25 dB

            // Convert to linear amplitude (0 dB = 1.0)
            float noiseFloorAmplitude = (float)Math.Pow(10.0, noiseRelativeDb / 20.0);
            // VHF: ≈ 0.112
            // UHF: ≈ 0.056

            // Reduce by 6 dB for playback (allows weak signals at noise floor + 3dB to be heard)
            return noiseFloorAmplitude * 0.5f;
            // VHF: ≈ 0.056
            // UHF: ≈ 0.028
        }

        /// <summary>
        /// Calculate minimum gain threshold for signal detection.
        /// Signals below this are considered drowned by thermal noise.
        /// </summary>
        /// <param name="frequencyMHz">Radio frequency in MHz</param>
        /// <param name="receiverSensitivityDbm">Receiver sensitivity in dBm (optional, uses defaults if not provided)</param>
        public static float CalculateNoiseFloorAmplitude(double frequencyMHz, double? receiverSensitivityDbm = null)
        {
            bool isVHF = frequencyMHz < VhfUhfBoundaryMHz;

            // Use provided sensitivity or default values
            double rxSensitivity = receiverSensitivityDbm ?? (isVHF ? -113.0 : -107.0);

            double bandwidthHz = isVHF ? 3000.0 : 3400.0;
            double thermalNoise = -174.0 + 10.0 * Math.Log10(bandwidthHz);
            double receiverNoiseFigure = 7.0;
            double noiseFloorDbm = thermalNoise + receiverNoiseFigure;
            double noiseRelativeDb = noiseFloorDbm - rxSensitivity;

            return (float)Math.Pow(10.0, noiseRelativeDb / 20.0);
            // VHF (default -113 dBm): ≈ 0.112
            // UHF (default -107 dBm): ≈ 0.056
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
            // Pre-allocate with maximum expected capacity to avoid resizing
            var result = new List<(double, double)>(desiredSamples);
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
                double tA = distA / D, tB = distB / D;
                double losA = txBase + tA * (rxBase - txBase);
                double losB = txBase + tB * (rxBase - txBase);
                double excessA = elevA - losA;
                double excessB = elevB - losB;
                if (Math.Abs(excessB - excessA) > 5.0)
                {
                    double tMid = (tA + tB) * 0.5;
                    double sx = txX + tMid * dx;
                    double sy = txY + tMid * dy;
                    double elevMid = SampleElevation(sx, sy);
                    result.Insert(i + 1, (tMid * D, elevMid));
                }
            }

            return result;
        }

        /// <summary>
        /// Calculate noise level and dropout probability from SNR.
        /// Physics-informed: based on FM threshold effect and thermal noise characteristics.
        /// 
        /// MULTI-SCALE FADING MODEL:
        /// - DropoutProb: Fast flutter (20-80ms, 0-1.2 events/sec) - rapid multipath interference
        /// - DeepFadeProb: Slow deep fades (400-2000ms, 0-0.3 events/sec) - terrain nulls, severe multipath
        /// </summary>
        private void CalculateNoiseAndDropout(AudioParams ap, double snrDb, bool isVHF)
        {
            if (isVHF)
            {
                // VHF AM: Analog static increases smoothly with decreasing SNR
                if (snrDb > 20.0)
                    ap.NoiseLevel = 0.02f; // Clean signal
                else if (snrDb > 10.0)
                    ap.NoiseLevel = (float)(0.02 + (20.0 - snrDb) / 10.0 * 0.18); // 0.02 → 0.20
                else if (snrDb > 0.0)
                    ap.NoiseLevel = (float)(0.20 + (10.0 - snrDb) / 10.0 * 0.35); // 0.20 → 0.55
                else
                    ap.NoiseLevel = (float)(0.55 + Math.Min(-snrDb / 20.0, 0.30)); // 0.55 → 0.85
            }
            else
            {
                // UHF FM: FM threshold effect - noise suppression until below threshold
                if (snrDb > 15.0)
                    ap.NoiseLevel = 0.01f; // Excellent FM quieting
                else if (snrDb > 10.0)
                    ap.NoiseLevel = (float)(0.01 + (15.0 - snrDb) / 5.0 * 0.09); // 0.01 → 0.10
                else if (snrDb > 5.0)
                    ap.NoiseLevel = (float)(0.10 + (10.0 - snrDb) / 5.0 * 0.30); // 0.10 → 0.40 (FM threshold)
                else if (snrDb > 0.0)
                    ap.NoiseLevel = (float)(0.40 + (5.0 - snrDb) / 5.0 * 0.35); // 0.40 → 0.75
                else
                    ap.NoiseLevel = (float)(0.75 + Math.Min(-snrDb / 10.0, 0.20)); // 0.75 → 0.95
            }

            // === FAST FLUTTER: Rapid multipath fading (20-80ms) ===
            // This is the "picket-fencing" effect from rapid phase cancellation
            double dropout;
            if (snrDb > 15.0)
                dropout = 0.0; // Clean signal
            else if (snrDb > 5.0)
                dropout = (15.0 - snrDb) / 10.0 * 0.3; // 0 → 0.3 events/sec
            else if (snrDb > 0.0)
                dropout = 0.3 + (5.0 - snrDb) / 5.0 * 0.4; // 0.3 → 0.7 events/sec
            else
                dropout = 0.7 + Math.Min(-snrDb / 10.0, 0.5); // 0.7 → 1.2 events/sec

            // VHF is less affected by multipath (longer wavelength)
            if (isVHF)
                dropout *= 0.7;

            ap.DropoutRate = (float)Math.Clamp(dropout, 0.0, 1.5);

            // === DEEP FADES: Slow severe dropouts (400-2000ms) ===
            // These are from terrain shadowing, deep multipath nulls, atmospheric ducting changes
            // They can drop signal below squelch threshold → trigger squelch pops
            double deepFade;
            if (snrDb > 10.0)
                deepFade = 0.0; // Good signal - no deep fades
            else if (snrDb > 5.0)
                deepFade = (10.0 - snrDb) / 5.0 * 0.05; // 0 → 0.05 events/sec
            else if (snrDb > 0.0)
                deepFade = 0.05 + (5.0 - snrDb) / 5.0 * 0.10; // 0.05 → 0.15 events/sec
            else
                deepFade = 0.15 + Math.Min(-snrDb / 10.0, 0.15); // 0.15 → 0.30 events/sec

            // UHF more susceptible to deep fades (terrain nulls, shorter wavelength)
            if (!isVHF)
                deepFade *= 1.5;

            ap.DeepFadeRate = (float)Math.Clamp(deepFade, 0.0, 0.5);
        }

        /// <summary>
        /// Apply physics-informed smooth degradation based on terrain obstruction.
        /// Uses knife-edge diffraction theory with wavelength-dependent corrections.
        /// No discrete branches - single continuous function for realistic "degradation window".
        /// </summary>
        private void ApplyTerrainDegradation(AudioParams ap, double fresnelClearance, double diffLoss,
            double baseGainDb, bool isVHF, double rxSensitivity, double worstExcess, double F1_radius)
        {
            _logger.LogDebug($"ApplyTerrainDegradation:");
            _logger.LogDebug($"  fresnelClearance: {fresnelClearance:F3}");
            _logger.LogDebug($"  diffLoss: {diffLoss:F1} dB");
            _logger.LogDebug($"  baseGainDb: {baseGainDb:F1} dB");

            double effectiveSignalDbm;
            double snrDb;

            double bandwidthHz = isVHF ? 3000.0 : 3400.0;
            double thermalNoise = -174.0 + 10.0 * Math.Log10(bandwidthHz);
            double receiverNoiseFigure = 7.0;
            double noiseFloorDbm = thermalNoise + receiverNoiseFigure;

            double rfGainLinear;

            // === CHECK FOR CLEAR LOS FIRST ===
            // fresnelClearance >= 1.0 means terrain is below Fresnel zone edge (definitely clear)
            // OR fresnelClearance >= 0.6 with low diffraction loss (mostly clear)
            if (fresnelClearance >= 1.0 || (fresnelClearance >= 0.6 && diffLoss < 3.0))
            {
                _logger.LogDebug($"  → Taking CLEAR LOS path");
                // Clear LOS - no terrain degradation needed
                rfGainLinear = Math.Pow(10.0, baseGainDb / 20.0);
                ap.Gain = ApplyAGC((float)rfGainLinear);

                // Calculate SNR for clean signal
                effectiveSignalDbm = rxSensitivity + baseGainDb;
                noiseFloorDbm = thermalNoise + receiverNoiseFigure;
                snrDb = effectiveSignalDbm - noiseFloorDbm;

                CalculateNoiseAndDropout(ap, snrDb, isVHF);
                ap.LowpassHz = CalculateDynamicBandwidth(snrDb, isVHF);
                return;
            }

            _logger.LogDebug($"  → Taking OBSTRUCTED path");

            // === PHYSICS-INFORMED SMOOTH DEGRADATION ===
            // Based on knife-edge diffraction theory, but applied continuously

            // Calculate approximate Fresnel parameter from clearance
            // CORRECTED MAPPING:
            // clearance = 1.0 (100% clear) → v = -2.0 (well below obstacle)
            // clearance = 0.0 (obstacle at Fresnel zone) → v = 0.0 (grazing)
            // clearance = -1.0 (obstacle beyond Fresnel zone) → v = +2.0 (blocked)
            double v_approx = -2.0 * fresnelClearance;

            // Calculate knife-edge diffraction loss (ITU-R P.526)
            // Only applies when v > -0.78 (obstructed or grazing)
            double theoreticalDiffractionLoss = 0.0;
            if (v_approx > -0.78)
            {
                theoreticalDiffractionLoss = KnifeEdgeLoss_dB(v_approx);
            }

            // Apply wavelength-dependent corrections
            double wavelengthCorrection;
            if (isVHF)
            {
                // VHF: Longer wavelength diffracts better than knife-edge theory predicts
                wavelengthCorrection = -VhfDiffractionBonus_dB;

                // However, for obstructions beyond 1.5 Fresnel zones,
                // wavelength advantage diminishes - you can't diffract around a mountain!
                if (fresnelClearance < -0.5)
                {
                    // Start reducing VHF advantage at 1.5 zones blocked
                    // Use aggressive exponential scaling - essentially eliminates VHF advantage at 2+ zones
                    double excessBlocked = Math.Max(0.0, -fresnelClearance - 0.5); // 0 at -0.5, 1.5 at -2.0

                    // Exponential reduction: 2^(-2x) gives very fast decay
                    // At 1.5 zones (-0.5 clearance): factor ≈ 1.0 (no reduction)
                    // At 2.0 zones (-1.0 clearance): factor ≈ 0.25 (75% reduction)
                    // At 2.5 zones (-1.5 clearance): factor ≈ 0.06 (94% reduction)
                    double reductionFactor = Math.Pow(2.0, -2.0 * excessBlocked);
                    wavelengthCorrection *= reductionFactor;

                    _logger.LogDebug(
                        $"VHF advantage reduction: {excessBlocked:F2} excess → factor {reductionFactor:F3} → correction {wavelengthCorrection:F2} dB");
                }
            }
            else
            {
                // UHF: Shorter wavelength, more LOS-dependent
                wavelengthCorrection = UhfDiffractionPenalty_dB;
            }

            // Combine theoretical loss with wavelength correction
            double totalTerrainLoss = theoreticalDiffractionLoss + wavelengthCorrection;

            // SMOOTH BLENDING: For severe obstruction, blend between theoretical and measured diffraction loss
            // - clearance > 0.4: Use pure theoretical (approximation works well)
            // - clearance 0.1-0.4: Smooth linear blend
            // - clearance < 0.1: Use pure measured (multiple obstacles, theory breaks down)
            if (fresnelClearance < 0.4)
            {
                double blendFactor;
                if (fresnelClearance < 0.1)
                {
                    blendFactor = 1.0; // Full measured loss (severe obstruction)
                }
                else
                {
                    // Linear blend from 0.1 (full measured) to 0.4 (full theoretical)
                    blendFactor = (0.4 - fresnelClearance) / 0.3;
                }
                
                // Blend: theoretical * (1 - blend) + measured * blend
                totalTerrainLoss = totalTerrainLoss * (1.0 - blendFactor) + diffLoss * blendFactor;
                
                _logger.LogDebug($"Blending: theoretical={theoreticalDiffractionLoss + wavelengthCorrection:F1} dB, measured={diffLoss:F1} dB, blend={blendFactor:F3} → final={totalTerrainLoss:F1} dB");
            }

            // Knife-edge theory assumes single sharp obstacle and saturates ~30-40 dB.
            // For mountains blocking multiple Fresnel zones, add additional loss factor.
            if (fresnelClearance < 0.4 && diffLoss > 15.0)
            {
                // Calculate how many Fresnel radii the terrain penetrates into obstruction zone
                // fresnelClearance = 1.0 - (worstExcess / F1_radius)
                // So: totalPenetration = worstExcess / F1_radius = 1.0 - fresnelClearance (when negative)
                double totalPenetration = Math.Max(0.0, 1.0 - fresnelClearance);

                // Knife-edge theory handles up to ~0.6 clearance (40% obstruction)
                // Apply penalty for deeper penetration
                double additionalZonesBlocked = Math.Max(0.0, totalPenetration - 0.6);

                if (additionalZonesBlocked > 0.1)
                {
                    // Multi-zone penalty with exponential scaling for severe obstructions
                    // Base: 12 dB per zone for moderate obstruction (up to 1.5 zones)
                    // Exponential: penalty increases dramatically beyond 1.5 zones
                    double multiZonePenalty;

                    if (additionalZonesBlocked < 1.0)
                    {
                        // Linear region: 12 dB per zone
                        multiZonePenalty = additionalZonesBlocked * 12.0;
                    }
                    else
                    {
                        // Exponential region for severe obstruction (>2 total zones blocked)
                        // First zone: 12 dB
                        // Additional zones: 18 dB × (2.5^n) where n is zones beyond first
                        // This creates very aggressive scaling for massive obstructions
                        multiZonePenalty = 12.0; // First zone
                        double excessZones = additionalZonesBlocked - 1.0;
                        multiZonePenalty += 18.0 * (Math.Pow(2.5, excessZones) - 1.0);

                        // Cap at 80 dB - beyond this the signal is completely gone anyway
                        multiZonePenalty = Math.Min(multiZonePenalty, 80.0);
                    }

                    // VHF gets less relief for massive obstructions
                    if (isVHF)
                        multiZonePenalty *= 1;
                    else
                        multiZonePenalty *= 1.3; // UHF suffers more from multi-zone blockage

                    totalTerrainLoss += multiZonePenalty;

                    _logger.LogDebug(
                        $"Multi-zone blockage: {totalPenetration:F2} Fresnel radii, {additionalZonesBlocked:F2} zones → +{multiZonePenalty:F1} dB penalty");
                }
            }

            // Apply terrain loss to base gain
            double finalGainDb = baseGainDb - totalTerrainLoss;

            // UHF hard cutoff: severe obstructions should completely block UHF
            // When 3+ Fresnel zones are blocked, signal is essentially gone
            if (!isVHF && fresnelClearance < -2.0) // More than 3 zones blocked
            {
                finalGainDb = Math.Min(finalGainDb, MinimumGainDb + 10.0); // Cap at -50 dB
            }

            // Clamp to physical limits
            finalGainDb = Math.Clamp(finalGainDb, MinimumGainDb, 20.0);

            // Convert to linear gain
            rfGainLinear = Math.Pow(10.0, finalGainDb / 20.0);
            // Apply AGC to get audio gain (0.0-1.0)
            ap.Gain = ApplyAGC((float)rfGainLinear);
            _logger.LogDebug($"AGC: rfGain={rfGainLinear:F2} → audioGain={ApplyAGC((float)rfGainLinear):F3}");

            // Calculate SNR from final gain
            effectiveSignalDbm = rxSensitivity + finalGainDb;
            snrDb = effectiveSignalDbm - noiseFloorDbm;

            // Calculate noise and dropout from SNR (physics-based)
            CalculateNoiseAndDropout(ap, snrDb, isVHF);

            // Set bandwidth
            ap.LowpassHz = CalculateDynamicBandwidth(snrDb, isVHF);
        }



        public void UpdateStaticParamsOnly(AudioParams ap, double snrDb, bool isVHF)
        {
            // Update noise/dropout without recalculating gain
            // (used when gain is already correctly set but we need to recalculate noise/dropout)

            // Calculate noise and dropout from SNR (physics-based)
            CalculateNoiseAndDropout(ap, snrDb, isVHF);

            // Set bandwidth
            ap.LowpassHz = CalculateDynamicBandwidth(snrDb, isVHF);
        }

        public AudioParams CalculateAudioParams(
            double txX, double txY, double txAlt,
            double rxX, double rxY, double rxAlt,
            double txPowerDbm, double frequencyMHz,
            double? receiverSensitivityDbm = null, // Optional: uses defaults if not provided
            bool includeTerrainProfile = false,
            bool altitudeIsMSL = false)
        {
            // Convert AGL to MSL
            if (!altitudeIsMSL)
            {
                txAlt += SampleElevation(txX, txY);
                rxAlt += SampleElevation(rxX, rxY);
            }

            // Rent from pool instead of allocating
            var ap = paramsPool.Rent();
            ap.RadioFrequencyMHz = (float)frequencyMHz;
            bool isVHF = frequencyMHz < VhfUhfBoundaryMHz;

            // Use provided sensitivity or default values
            double rxSensitivity = receiverSensitivityDbm ?? (isVHF ? -113.0 : -107.0);

            // Distance between transmitter and receiver
            double dx = rxX - txX;
            double dy = rxY - txY;
            double dz = rxAlt - txAlt;
            double dist2D = Math.Sqrt(dx * dx + dy * dy);
            double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

            if (dist < 1.0)
            {
                ap.Gain = 1.0f;
                ap.LowpassHz = isVHF ? 3300f : 3500f;
                ap.NoiseLevel = 0.01f;
                ap.DropoutRate = 0.0f;
                ap.DeepFadeRate = 0.0f;
                FinalizeAudioParams(ap, dist, 40.0, 0.0, new List<(double, double)>(), includeTerrainProfile);
                return ap;
            }

            // Free-space path loss
            double freqHz = frequencyMHz * 1e6;
            double fspl = FSPL_dB(dist, freqHz);

            // Weather attenuation (light rain/fog)
            double weatherLoss = weatherDbPerKm * (dist / 1000.0);

            // Atmospheric refraction (effective Earth radius)
            double kAvg = CalculateKAvg(txAlt, rxAlt);
            double effectiveEarthRadius = kAvg * EarthRadius;

            // Received power before terrain effects
            double prDbm = txPowerDbm - fspl - weatherLoss;

            // Base gain without terrain effects
            double baseGainDb = Math.Clamp(prDbm - rxSensitivity, MinimumGainDb, 50.0);

            // Sample terrain profile
            var profile = SampleProfileAdaptive(txX, txY, rxX, rxY, maxSamplesPerPath);
            double snrDb = 0;
            if (profile.Count < 2)
            {
                double rfGainLinear = Math.Pow(10.0, baseGainDb / 20.0);
                ap.Gain = ApplyAGC((float)rfGainLinear);
                ap.LowpassHz = CalculateDynamicBandwidth(snrDb, isVHF);
                
                // SNR = Received Power - Noise Floor
                snrDb = prDbm - rxSensitivity;
                
                CalculateNoiseAndDropout(ap, snrDb, isVHF);
                FinalizeAudioParams(ap, dist, snrDb, fspl + weatherLoss, profile, includeTerrainProfile);
                return ap;
            }

            // Calculate wavelength and First Fresnel zone radius
            double lambda = SpeedOfLight / freqHz;
            double F1_radius = 0.0;
            double worstExcess = double.MinValue;
            double diffLoss = 0.0;

            // Cache frequently used calculations outside the loop
            double invDist2D = 1.0 / dist2D;
            double rxMinusTx = rxAlt - txAlt;
            double invTwoEffectiveRadius = 1.0 / (2.0 * effectiveEarthRadius); // FIX: was dividing by half radius
            double lambdaInv = 1.0 / lambda;

            // Find worst obstruction along path
            for (int i = 1; i < profile.Count - 1; i++)
            {
                var (d, h) = profile[i];
                double d1 = d;
                double d2 = dist2D - d;
                if (d2 < 1.0) continue;

                // Fresnel zone radius at this point
                double d1d2 = d1 * d2;
                double F1 = Math.Sqrt((lambda * d1d2) / (d1 + d2));
                if (F1 > F1_radius) F1_radius = F1;

                // LOS height at this distance (accounting for Earth curvature)
                double curvature = d1d2 * invTwoEffectiveRadius;
                double losHeight = txAlt + rxMinusTx * (d1 * invDist2D) - curvature;

                // Clearance excess: negative = clear, positive = obstructed
                double excess = h - (losHeight + F1);
                if (excess > worstExcess)
                {
                    worstExcess = excess;
            
                    // Calculate diffraction parameter
                    double h_diff = h - losHeight;
                    double v = h_diff * Math.Sqrt(2.0 * (d1 + d2) * lambdaInv / d1d2);
                    diffLoss = KnifeEdgeLoss_dB(v);
                }
            }

            // Calculate Fresnel clearance (1.0 = perfect, 0.0 = grazing, negative = blocked)
            double fresnelClearance = F1_radius > 0 ? (1.0 - worstExcess / F1_radius) : 1.0;

            _logger.LogDebug($"Terrain Analysis:");
            _logger.LogDebug($"  worstExcess: {worstExcess:F1}m");
            _logger.LogDebug($"  F1_radius: {F1_radius:F1}m");
            _logger.LogDebug($"  fresnelClearance: {fresnelClearance:F3}");
            _logger.LogDebug($"  diffLoss: {diffLoss:F1} dB");
            _logger.LogDebug($"  Profile points: {profile.Count}");

            // === APPLY PHYSICS-INFORMED SMOOTH DEGRADATION ===
            ApplyTerrainDegradation(ap, fresnelClearance, diffLoss, baseGainDb, isVHF, rxSensitivity, worstExcess,
                F1_radius);

            // Calculate final path loss and SNR for output
            double pathLossDb = fspl + weatherLoss + (baseGainDb - 20.0 * Math.Log10(Math.Max(ap.Gain, 1e-6)));
            
            // SNR = Received Power - Noise Floor
            // rxSensitivity is the receiver noise floor (minimum detectable signal)
            snrDb = prDbm - rxSensitivity;

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

        /// <summary>
        /// Apply AGC (Automatic Gain Control) curve to map RF gain to audio gain.
        /// </summary>
        private static float ApplyAGC(float rfGain)
        {
            // Use logarithmic compression (similar to real AGC circuits)
            // Formula: audioGain = tanh(log10(rfGain + 1) * k) where k controls compression

            if (rfGain <= 0.0f)
                return 0.0f;

            // Logarithmic scaling factor (tune this to taste)
            const double agcCompressionFactor = 1.2;

            // Log compression: compress the dynamic range
            double logGain = Math.Log10(rfGain + 1.0) * agcCompressionFactor;

            // Soft saturation using tanh (prevents hard clipping)
            double audioGain = Math.Tanh(logGain);

            // Scale to comfortable range (max 1.0)
            return (float)Math.Clamp(audioGain, 0.0, 1.0);
        }
        
        private float CalculateDynamicBandwidth(double snrDb, bool isVHF)
        {
            // Full bandwidth targets
            float maxBandwidth = isVHF ? VhfBandwidthHz : UhfBandwidthHz;
    
            // Minimum intelligible bandwidth (telephone quality)
            const float minBandwidth = 1200f;
    
            if (snrDb > 20.0)
            {
                // Excellent signal - full bandwidth
                return maxBandwidth;
            }
            else if (snrDb > 10.0)
            {
                // Good signal - slight HF rolloff (3000 → 2600 Hz)
                float reduction = (float)((20.0 - snrDb) / 10.0 * 0.15); // 0% → 15% reduction
                return maxBandwidth * (1f - reduction);
            }
            else if (snrDb > 5.0)
            {
                // Marginal signal - noticeable narrowing (2600 → 2000 Hz)
                float startBw = maxBandwidth * 0.85f;
                float endBw = maxBandwidth * 0.60f;
                float t = (float)((10.0 - snrDb) / 5.0);
                return startBw + t * (endBw - startBw);
            }
            else if (snrDb > 0.0)
            {
                // Poor signal - telephone quality (2000 → 1500 Hz)
                float startBw = maxBandwidth * 0.60f;
                float endBw = maxBandwidth * 0.45f;
                float t = (float)((5.0 - snrDb) / 5.0);
                return startBw + t * (endBw - startBw);
            }
            else
            {
                // Very poor - minimal intelligibility (1500 → 1200 Hz)
                float startBw = maxBandwidth * 0.45f;
                float reduction = (float) Math.Min(-snrDb / 10.0, 0.2); // Additional 20% max
                return Math.Max(minBandwidth, startBw * (1f - (float)reduction));
            }
        }
    }
    
    
}