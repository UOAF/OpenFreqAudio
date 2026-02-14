// - Assumes DEM mipmaps: mipmaps[0] = finest (native), mipmaps[last] = coarsest.
// - Uses bilinear sampling and an adaptive sample/refine policy.
// - Returns AudioParams (gain linear, lowpassHz, noiseLevel [0..1], dropoutProb).
//
// PHYSICS MODEL:
// - Knife-edge diffraction theory (ITU-R P.526) as foundation
// - Smooth continuous degradation
// - Wavelength-dependent corrections with configurable AM/FM modulation
// - Parametrized radio band characteristics (bandwidth, diffraction, modulation type)

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using Microsoft.Extensions.Logging;
// ReSharper disable InconsistentNaming

namespace OpenFreqAudio
{
    // ================================================================
    // Radio modulation and band configuration
    // ================================================================

    /// <summary>
    /// Radio modulation type - affects bandwidth, noise characteristics, and future capture/threshold modeling
    /// </summary>
    public enum ModulationType
    {
        AM,
        FM
    }

    /// <summary>
    /// Configuration for a radio band's physical characteristics
    /// </summary>
    public class RadioBandConfig
    {
        public string BandName { get; init; }
        public int FrequencyMin_KHz { get; init; }
        public int FrequencyMax_KHz { get; init; }
        public float VoiceBandwidth_Hz { get; init; }
        public double DiffractionCorrection_dB { get; init; }
        public ModulationType Modulation { get; init; }

        public RadioBandConfig(string bandName, int freqMinKhz, int freqMaxKhz,
            float bandwidth, double diffractionDb, ModulationType modulation)
        {
            BandName = bandName;
            FrequencyMin_KHz = freqMinKhz;
            FrequencyMax_KHz = freqMaxKhz;
            VoiceBandwidth_Hz = bandwidth;
            DiffractionCorrection_dB = diffractionDb;
            Modulation = modulation;
        }
    }


    // ================================================================
    public class AudioParams
    {
        public float Gain; // linear gain
        public float NoiseLevel; // 0..1 (analog static/hiss level)
        public float DropoutRate; // fast multipath flutter (events per second, can exceed 1.0)
        public float DeepFadeRate; // slow deep fades (events per second, typically 0-0.5)
        public int RadioFrequencyKHz;

        // RF propagation parameters (for physics-based stepped-on interference)
        public float SNR_dB; // Signal-to-noise ratio

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

            mmf = MemoryMappedFile.CreateFromFile(path,FileMode.Open,null,0, MemoryMappedFileAccess.Read);
            accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
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
            accessor.Dispose();
            mmf.Dispose();
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
        private const double MinimumGainDb = -60.0; // Below this, signal is completely lost

        // Default radio band configurations
        public static List<RadioBandConfig> bandConfigs = new()
        {
            new RadioBandConfig(
                bandName: "BMS Lobby",
                freqMinKhz: 1234,
                freqMaxKhz: 1234,
                bandwidth: 4000.0f,
                diffractionDb: 3.0,
                modulation: ModulationType.AM),

            new RadioBandConfig(
                bandName: "VHF",
                freqMinKhz: 30000,
                freqMaxKhz: 199999,
                bandwidth: 3000.0f,
                diffractionDb: 3.0, // Better diffraction than UHF
                modulation: ModulationType.AM
            ),

            new RadioBandConfig(
                bandName: "UHF",
                freqMinKhz: 200000,
                freqMaxKhz: 520000,
                bandwidth: 3000.0f,
                diffractionDb: -7.0, // More LOS-dependent
                modulation: ModulationType.FM
            )
        };

        private readonly DEMReader dem;
        private readonly double originX, originY, cellSizeMeters;
        private readonly int maxSamplesPerPath = 512;
        private readonly double weatherDbPerKm = 0.02;
        private readonly ILogger<FastPathAudioSim> _logger;
        public FastPathAudioSim(DEMReader dem, double originX, double originY, double cellSizeMeters,
            ILogger<FastPathAudioSim> logger)
        {
            this.dem = dem;
            this.originX = originX;
            this.originY = originY;
            this.cellSizeMeters = cellSizeMeters;
            _logger = logger;
        }

        /// <summary>
        /// Determine which radio band configuration to use for a given frequency
        /// </summary>
        private static RadioBandConfig GetBandConfig(int frequencyKhz)
        {
            foreach (var config in bandConfigs)
            {
                if (frequencyKhz >= config.FrequencyMin_KHz && frequencyKhz <= config.FrequencyMax_KHz)
                {
                    return config;
                }
            }

            // Default fallback to VHF-like characteristics if frequency doesn't match any band
            return bandConfigs[0];
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
        /// <param name="frequencyKhz"></param>
        /// <param name="receiverSensitivityDbm">Receiver sensitivity in dBm (optional, uses defaults if not provided)</param>
        /// <param name="modulation">Modulation type (affects noise characteristics)</param>
        /// <returns>Background noise amplitude (0.0 to 1.0 scale where 1.0 = 0 dBm)</returns>
        public static float CalculateBackgroundNoiseAmplitude(int frequencyKhz,
            double? receiverSensitivityDbm = null, ModulationType modulation = ModulationType.AM)
        {
            var bandwidthHz = GetBandConfig(frequencyKhz).VoiceBandwidth_Hz;
            if (bandwidthHz == 0) throw new Exception($"Frequency {frequencyKhz / 1000.0:F3} MHz not found in Band Config");

            // Default sensitivities based on modulation type
            double rxSensitivity = receiverSensitivityDbm ?? (modulation == ModulationType.AM ? -113.0 : -107.0);

            // Thermal noise floor calculation
            double thermalNoise = -174.0 + 10.0 * Math.Log10(bandwidthHz); // ≈ -139.2 dBm for 3000 Hz
            double receiverNoiseFigure = 7.0; // dB
            double noiseFloorDbm = thermalNoise + receiverNoiseFigure; // ≈ -132 dBm

            // Noise floor relative to receiver sensitivity
            double noiseRelativeDb = noiseFloorDbm - rxSensitivity;
            // AM (default -113 dBm): -132 - (-113) = -19 dB
            // FM (default -107 dBm): -132 - (-107) = -25 dB

            // Convert to linear amplitude (0 dB = 1.0)
            float noiseFloorAmplitude = (float)Math.Pow(10.0, noiseRelativeDb / 20.0);
            // AM: ≈ 0.112
            // FM: ≈ 0.056

            // Reduce by 6 dB for playback (allows weak signals at noise floor + 3dB to be heard)
            return noiseFloorAmplitude * 0.5f;
            // AM: ≈ 0.056
            // FM: ≈ 0.028
        }

        /// <summary>
        /// Calculate minimum gain threshold for signal detection.
        /// Signals below this are considered drowned by thermal noise.
        /// </summary>
        /// <param name="frequencyKhz"></param>
        /// <param name="receiverSensitivityDbm">Receiver sensitivity in dBm (optional, uses defaults if not provided)</param>
        /// <param name="modulation">Modulation type (affects default sensitivity)</param>
        public static float CalculateNoiseFloorAmplitude(int frequencyKhz,
            double? receiverSensitivityDbm = null, ModulationType modulation = ModulationType.AM)
        {
            var bandwidthHz = GetBandConfig(frequencyKhz).VoiceBandwidth_Hz;
            if (bandwidthHz == 0) throw new Exception($"Frequency {frequencyKhz / 1000.0:F3} MHz not found in Band Config");

            // Default sensitivities based on modulation type
            double rxSensitivity = receiverSensitivityDbm ?? (modulation == ModulationType.AM ? -113.0 : -107.0);

            double thermalNoise = -174.0 + 10.0 * Math.Log10(bandwidthHz);
            double receiverNoiseFigure = 7.0;
            double noiseFloorDbm = thermalNoise + receiverNoiseFigure;
            double noiseRelativeDb = noiseFloorDbm - rxSensitivity;

            return (float)Math.Pow(10.0, noiseRelativeDb / 20.0);
            // AM (default -113 dBm): ≈ 0.112
            // FM (default -107 dBm): ≈ 0.056
        }

        // Helper to finalize AudioParams with common fields and optional terrain profile
        private void FinalizeAudioParams(AudioParams ap, double snrDb,
            List<(double dist, double elev)> profile, bool includeTerrainProfile)
        {
            ap.SNR_dB = (float)snrDb;
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
        /// Physics-informed: based on modulation type characteristics and thermal noise.
        /// 
        /// MULTI-SCALE FADING MODEL:
        /// - DropoutRate: Fast flutter (20-80ms, 0-1.2 events/sec) - rapid multipath interference
        /// - DeepFadeRate: Slow deep fades (400-2000ms, 0-0.3 events/sec) - terrain nulls, severe multipath
        /// </summary>
        private void CalculateNoiseAndDropout(AudioParams ap, double snrDb, RadioBandConfig bandConfig)
        {
            if (bandConfig.Modulation == ModulationType.AM)
            {
                // AM: Analog static increases smoothly with decreasing SNR
                if (snrDb > 20.0)
                    ap.NoiseLevel = 0.02f; // Clean signal
                else if (snrDb > 10.0)
                    ap.NoiseLevel = (float)(0.02 + (20.0 - snrDb) / 10.0 * 0.18); // 0.02 → 0.20
                else if (snrDb > 0.0)
                    ap.NoiseLevel = (float)(0.20 + (10.0 - snrDb) / 10.0 * 0.35); // 0.20 → 0.55
                else
                    ap.NoiseLevel = (float)(0.55 + Math.Min(-snrDb / 20.0, 0.30)); // 0.55 → 0.85
            }
            else // FM
            {
                // FM: FM threshold effect - noise suppression until below threshold
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

            // Wavelength-dependent multipath: Longer wavelengths less affected
            // VHF (AM) typically has longer wavelengths than UHF (FM)
            if (bandConfig.DiffractionCorrection_dB > 0) // Positive correction = better diffraction = longer wavelength
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
            if (bandConfig.DiffractionCorrection_dB < 0) // Negative correction = worse diffraction = shorter wavelength
                deepFade *= 1.5;

            ap.DeepFadeRate = (float)Math.Clamp(deepFade, 0.0, 0.5);
        }

        /// <summary>
        /// Apply physics-informed smooth degradation based on terrain obstruction.
        /// Uses knife-edge diffraction theory with wavelength-dependent corrections.
        /// No discrete branches - single continuous function for realistic "degradation window".
        /// </summary>
        private void ApplyTerrainDegradation(AudioParams ap, double fresnelClearance, double diffLoss,
            double baseGainDb, RadioBandConfig bandConfig, double rxSensitivity)
        {
            _logger.LogDebug($"ApplyTerrainDegradation:");
            _logger.LogDebug($"  fresnelClearance: {fresnelClearance:F3}");
            _logger.LogDebug($"  diffLoss: {diffLoss:F1} dB");
            _logger.LogDebug($"  baseGainDb: {baseGainDb:F1} dB");
            _logger.LogDebug($"  Band: {bandConfig.BandName} ({bandConfig.Modulation})");

            double effectiveSignalDbm;
            double snrDb;

            double bandwidthHz = bandConfig.VoiceBandwidth_Hz;
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

                CalculateNoiseAndDropout(ap, snrDb, bandConfig);
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

            // Apply wavelength-dependent corrections from band config
            double wavelengthCorrection = bandConfig.DiffractionCorrection_dB;

            // However, for obstructions beyond 1.5 Fresnel zones,
            // wavelength advantage diminishes - you can't diffract around a mountain!
            if (bandConfig.DiffractionCorrection_dB > 0 && fresnelClearance < -0.5)
            {
                // Start reducing wavelength advantage at 1.5 zones blocked
                // Use aggressive exponential scaling - essentially eliminates advantage at 2+ zones
                double excessBlocked = Math.Max(0.0, -fresnelClearance - 0.5); // 0 at -0.5, 1.5 at -2.0

                // Exponential reduction: 2^(-2x) gives very fast decay
                // At 1.5 zones (-0.5 clearance): factor ≈ 1.0 (no reduction)
                // At 2.0 zones (-1.0 clearance): factor ≈ 0.25 (75% reduction)
                // At 2.5 zones (-1.5 clearance): factor ≈ 0.06 (94% reduction)
                double reductionFactor = Math.Pow(2.0, -2.0 * excessBlocked);
                wavelengthCorrection *= reductionFactor;

                _logger.LogDebug(
                    $"Wavelength advantage reduction: {excessBlocked:F2} excess → factor {reductionFactor:F3} → correction {wavelengthCorrection:F2} dB");
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

                _logger.LogDebug(
                    $"Blending: theoretical={theoreticalDiffractionLoss + wavelengthCorrection:F1} dB, measured={diffLoss:F1} dB, blend={blendFactor:F3} → final={totalTerrainLoss:F1} dB");
            }

            // Knife-edge theory assumes single sharp obstacle and saturates ~30-40 dB.
            // For mountains blocking multiple Fresnel zones, add additional loss factor.
            if (fresnelClearance < 0.4 && diffLoss > 15.0)
            {
                // Calculate how many Fresnel radii the terrain penetrates into obstruction zone
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

                    // Wavelength-dependent multi-zone behavior
                    if (bandConfig.DiffractionCorrection_dB > 0) // Better diffraction (longer wavelength)
                        multiZonePenalty *= 1.0;
                    else // Worse diffraction (shorter wavelength)
                        multiZonePenalty *= 1.3;

                    totalTerrainLoss += multiZonePenalty;

                    _logger.LogDebug(
                        $"Multi-zone blockage: {totalPenetration:F2} Fresnel radii, {additionalZonesBlocked:F2} zones → +{multiZonePenalty:F1} dB penalty");
                }
            }

            // Apply terrain loss to base gain
            double finalGainDb = baseGainDb - totalTerrainLoss;

            // Shorter wavelength hard cutoff: severe obstructions should completely block signal
            // When 3+ Fresnel zones are blocked, signal is essentially gone
            if (bandConfig.DiffractionCorrection_dB < 0 && fresnelClearance < -2.0) // More than 3 zones blocked
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
            CalculateNoiseAndDropout(ap, snrDb, bandConfig);
        }

        /// <summary>
        /// Calculates the Audio Parameters for a receiver
        /// </summary>
        public AudioParams CalculateAudioParams(
            double? txX, double? txY, double? txAlt,
            double? rxX, double? rxY, double? rxAlt,
            int frequencyKhz, double txPowerWatts = 10.0,
            double? receiverSensitivityDbm = null, // Optional: uses defaults if not provided
            bool includeTerrainProfile = false,
            bool txAltitudeIsMSL = false,
            bool rxAltitudeIsMSL = false)
        {
            if (txX == null || txY == null || txAlt == null || rxX == null || rxY == null || rxAlt == null)
            {
                return GetDefaultAudioParams(frequencyKhz);
            }

            // Get band configuration for this frequency
            RadioBandConfig bandConfig = GetBandConfig(frequencyKhz);

            // Rent from pool instead of allocating
            var ap = new AudioParams();
            ap.RadioFrequencyKHz = frequencyKhz;

            // Stupid C# does not recognize null-safety with the early return;
            double txXVal = txX.Value;
            double txYVal = txY.Value;
            double txAltVal = txAlt.Value;
            double rxXVal = rxX.Value;
            double rxYVal = rxY.Value;
            double rxAltVal = rxAlt.Value;

            // Convert transmit power from watts to dBm
            // Formula: dBm = 10 * log10(powerWatts * 1000)
            double txPowerDbm = 10.0 * Math.Log10(txPowerWatts * 1000.0);

            // Convert AGL to MSL
            if (!rxAltitudeIsMSL)
            {
                rxAltVal += SampleElevation(rxXVal, rxYVal);
            }

            if (!txAltitudeIsMSL)
            {
                txAltVal += SampleElevation(txXVal, txYVal);
            }

            // Use provided sensitivity or default values based on modulation
            double rxSensitivity = receiverSensitivityDbm ??
                                   (bandConfig.Modulation == ModulationType.AM ? -113.0 : -107.0);

            // Distance between transmitter and receiver
            double dx = rxXVal - txXVal;
            double dy = rxYVal - txYVal;
            double dz = rxAltVal - txAltVal;
            double dist2D = Math.Sqrt(dx * dx + dy * dy);
            double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

            if (dist < 1.0)
            {
                ap.Gain = 1.0f;
                ap.NoiseLevel = 0.01f;
                ap.DropoutRate = 0.0f;
                ap.DeepFadeRate = 0.0f;
                FinalizeAudioParams(ap, 40.0, new List<(double, double)>(), includeTerrainProfile);
                return ap;
            }

            // Free-space path loss
            double freqHz = frequencyKhz * 1e3; // kHz to Hz
            double fspl = FSPL_dB(dist, freqHz);

            // Weather attenuation (light rain/fog)
            double weatherLoss = weatherDbPerKm * (dist / 1000.0);

            // Atmospheric refraction (effective Earth radius)
            double kAvg = CalculateKAvg(txAltVal, rxAltVal);
            double effectiveEarthRadius = kAvg * EarthRadius;

            // Received power before terrain effects
            double prDbm = txPowerDbm - fspl - weatherLoss;

            // Base gain without terrain effects
            double baseGainDb = Math.Clamp(prDbm - rxSensitivity, MinimumGainDb, 50.0);

            // Sample terrain profile
            var profile = SampleProfileAdaptive(txXVal, txYVal, rxXVal, rxYVal, maxSamplesPerPath);
            double snrDb = 0;
            if (profile.Count < 2)
            {
                double rfGainLinear = Math.Pow(10.0, baseGainDb / 20.0);
                ap.Gain = ApplyAGC((float)rfGainLinear);

                // SNR = Received Power - Noise Floor
                snrDb = prDbm - rxSensitivity;

                CalculateNoiseAndDropout(ap, snrDb, bandConfig);
                FinalizeAudioParams(ap, snrDb, profile, includeTerrainProfile);
                return ap;
            }

            // Calculate wavelength and First Fresnel zone radius
            double lambda = SpeedOfLight / freqHz;
            double F1_radius = 0.0;
            double worstExcess = double.MinValue;
            double diffLoss = 0.0;

            // Cache frequently used calculations outside the loop
            double invDist2D = 1.0 / dist2D;
            double rxMinusTx = rxAltVal - txAltVal;
            double invTwoEffectiveRadius = 1.0 / (2.0 * effectiveEarthRadius);
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
                double losHeight = txAltVal + rxMinusTx * (d1 * invDist2D) - curvature;

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
            ApplyTerrainDegradation(ap, fresnelClearance, diffLoss, baseGainDb, bandConfig, rxSensitivity);

            // Calculate final path loss and SNR for output
            double pathLossDb = fspl + weatherLoss + (baseGainDb - 20.0 * Math.Log10(Math.Max(ap.Gain, 1e-6)));

            // SNR = Received Power - Noise Floor
            // rxSensitivity is the receiver noise floor (minimum detectable signal)
            snrDb = prDbm - rxSensitivity;

            // Finalize and return
            FinalizeAudioParams(ap, snrDb, profile, includeTerrainProfile);
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
            // const double N_b = 66.65; // Breakpoint refractivity
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

        public static AudioParams GetDefaultAudioParams(int frequencyKhz)
        {
            var bandConfig = GetBandConfig(frequencyKhz);
            var ap = new AudioParams
            {
                RadioFrequencyKHz = frequencyKhz,
                Gain = 1.0f,
                NoiseLevel = 0f,
                DropoutRate = 0f,
                DeepFadeRate = 0f
            };
            return ap;
        }
    }
}