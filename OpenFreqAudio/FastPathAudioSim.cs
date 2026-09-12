using Microsoft.Extensions.Logging;
using OpenFreqAudio.TerrainSampling;

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
        // Decibels of received power (before AGC)
        public float ReceivedDb;

        // SNR compared to the noise floor of the receiver (thermal + noise figure)
        public float ReceivedSnrDb;

        // Free-space path loss (dB, >= 0) over the slant range. Already folded into ReceivedDb;
        // kept so the receive path can log the budget. Zero when we had no positions to work with.
        public float FreeSpaceLossDb;

        // Delta-Bullington terrain diffraction loss (dB, >= 0). Also already folded into ReceivedDb.
        // Zero when the first Fresnel zone was proven clear, or the path was too short to profile.
        public float TerrainLossDb;

        // fast multipath flutter (events per second, can exceed 1.0). A function of SNR, but cached here.
        public float DropoutRate;

        // slow deep fades (events per second, typically 0-0.5). Also a function of SNR, but cached here.
        public float DeepFadeRate;

        public int RadioFrequencyKHz;

        // Tune offset of the radio in parts per million.
        public float TuneOffsetPPM;

        // Debug/visualization data
        public List<(double dist, double elev)>? TerrainProfile;

        public override string ToString()
        {
            return $"ReceivedDb: {ReceivedDb}, ReceivedSnrDb: {ReceivedSnrDb}, FreeSpaceLossDb: {FreeSpaceLossDb}, TerrainLossDb: {TerrainLossDb}, DropoutRate : {DropoutRate}, DeepFadeRate: {DeepFadeRate}, TuneOffsetPPM: {TuneOffsetPPM}";
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
        private const double FT_TO_M = HeightPyramid.FeetToMeters; // 0.3048; pyramid stores raw int16 feet

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
                modulation: ModulationType.AM
            )
        };

        private readonly HeightPyramid pyramid;
        private readonly double originX, originY, cellSizeMeters;
        private readonly double weatherDbPerKm = 0.02;
        private readonly ILogger<FastPathAudioSim> _logger;

        public FastPathAudioSim(HeightPyramid pyramid, double originX, double originY, double cellSizeMeters,
            ILogger<FastPathAudioSim> logger)
        {
            this.pyramid = pyramid;
            this.originX = originX;
            this.originY = originY;
            this.cellSizeMeters = cellSizeMeters;
            _logger = logger;
        }

        /// <summary>
        /// Determine which radio band configuration to use for a given frequency
        /// </summary>
        public static RadioBandConfig GetBandConfig(int frequencyKhz)
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


        // Get the elevation at the given location in meters.
        // Deliberately use nearest-neighbor sampling.
        // Bilinear/trilinear/sinc/etc. would bias towards _lower_ heights
        // by averaging out peaks, when peak height is exactly what we want!
        public double SampleElevation(double xMeters, double yMeters)
        {
            // Convert world coordinates → DEM pixel coordinates
            double gx = (xMeters - originX) / cellSizeMeters;
            double gy = (yMeters - originY) / cellSizeMeters;

            int col = (int)Math.Round(gx);
            int row = (int)Math.Round(gy);

            return pyramid.SampleNativeFeet(col, row) * FT_TO_M;
        }

        // Free-space path loss (dB)
        private static double FSPL_dB(double distanceMeters, double freqHz)
        {
            if (distanceMeters < 1.0) distanceMeters = 1.0;
            double lambda = SpeedOfLight / freqHz;
            return 20.0 * Math.Log10(FourPi * distanceMeters / lambda);
        }

        // Knife-edge diffraction loss (dB) - ITU-R P.526
        internal static double KnifeEdgeLoss_dB(double v)
        {
            if (v < -0.78) return 0.0;
            double term = Math.Sqrt((v - 0.1) * (v - 0.1) + 1.0) + (v - 0.1);
            return 6.9 + 20.0 * Math.Log10(term);
        }

        private static void SetTerrainProfile(AudioParams ap,
            List<(double dist, double elev)> profile, bool includeTerrainProfile)
        {
            if (includeTerrainProfile)
                ap.TerrainProfile = profile;
        }

        // Sample the terrain profile along the path via the conservative max-pyramid
        // traversal (HeightPyramid.SampleProfile). Returns the profile (dist meters from
        // TX, ASL meters; dense at native resolution where terrain nears the first
        // Fresnel zone, sparse where it clears) plus an all-clear verdict (true => terrain
        // is below the zone everywhere, so the diffraction model can be skipped).
        internal (List<(double dist, double elev)> profile, bool allClear) SampleProfile(
            double txX, double txY, double rxX, double rxY,
            double txAlt, double rxAlt, double freqHz)
        {
            double wavelength = SpeedOfLight / freqHz;
            double rEff = CalculateKAvg(txAlt, rxAlt) * EarthRadius;
            double cell = cellSizeMeters;
            return pyramid.SampleProfile(
                (txX - originX) / cell, (txY - originY) / cell,
                (rxX - originX) / cell, (rxY - originY) / cell,
                txAlt, rxAlt, wavelength, rEff, cell);
        }

        /// <summary>
        /// Fast flutter rate from rapid multipath fading (20-80ms "picket-fencing" effect).
        /// </summary>
        private static float CalculateDropoutRate(double snrDb, RadioBandConfig bandConfig)
        {
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

            return (float)Math.Clamp(dropout, 0.0, 1.5);
        }

        /// <summary>
        /// Slow deep fade rate from terrain shadowing, deep multipath nulls, atmospheric ducting (400-2000ms).
        /// Can drop signal below squelch threshold, triggering squelch pops.
        /// </summary>
        public static float CalculateDeepFadeRate(double snrDb, RadioBandConfig bandConfig)
        {
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

            return (float)Math.Clamp(deepFade, 0.0, 0.5);
        }

        /// <summary>
        /// Calculates the Audio Parameters for a receiver
        /// </summary>
        public AudioParams CalculateAudioParams(
            double? txX, double? txY, double? txAlt,
            double? rxX, double? rxY, double? rxAlt,
            int frequencyKhz,
            float ppm = 0.0f,
            double txPowerWatts = 10.0,
            double? receiverSensitivityDbm = null, // Optional: uses defaults if not provided
            bool includeTerrainProfile = false,
            bool txAltitudeIsMSL = false,
            bool rxAltitudeIsMSL = false,
            (double x, double y, double z)? txVelocity = null,
            (double x, double y, double z)? rxVelocity = null)
        {
            var ap = GetDefaultAudioParams(frequencyKhz, ppm);
            if (txX == null || txY == null || txAlt == null || rxX == null || rxY == null || rxAlt == null)
            {
                return ap;
            }

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

            // Distance between transmitter and receiver
            double dx = rxXVal - txXVal;
            double dy = rxYVal - txYVal;
            double dz = rxAltVal - txAltVal;
            double dist2D = Math.Sqrt(dx * dx + dy * dy);
            double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

            // Free-space path loss
            double freqHz = frequencyKhz * 1e3; // kHz to Hz
            double fspl = FSPL_dB(dist, freqHz);
            ap.FreeSpaceLossDb = (float)fspl;

            // Weather attenuation (light rain/fog)
            double weatherLoss = weatherDbPerKm * (dist / 1000.0);

            // Atmospheric refraction (effective Earth radius)
            double kAvg = CalculateKAvg(txAltVal, rxAltVal);
            double effectiveEarthRadius = kAvg * EarthRadius;

            // Get band configuration for this frequency
            RadioBandConfig bandConfig = GetBandConfig(frequencyKhz);

            // Use provided sensitivity or band-based defaults
            double rxSensitivity = receiverSensitivityDbm ??
                                   (RadioStationPreset.IsVHF(frequencyKhz) ? -113.0 : -107.0);

            // Received power before terrain effects
            ap.ReceivedDb = (float)(txPowerDbm - fspl - weatherLoss);

            // Sample terrain profile
            var (profile, allClear) = SampleProfile(txXVal, txYVal, rxXVal, rxYVal,
                txAltVal, rxAltVal, freqHz);
            // Bail now if there's not any terrain to obstruct us.
            // This should only fire for very short paths, so assume no additional losses.
            if (profile.Count < 2)
            {
                ap.ReceivedSnrDb = ap.ReceivedDb - (float)rxSensitivity;
                ap.DropoutRate = CalculateDropoutRate(ap.ReceivedSnrDb, bandConfig);
                ap.DeepFadeRate = CalculateDeepFadeRate(ap.ReceivedSnrDb, bandConfig);
                SetTerrainProfile(ap, profile, includeTerrainProfile);
                return ap;
            }

            // Wavelength (used by the two-ray model and the delta-Bullington diffraction below).
            double wavelength = SpeedOfLight / freqHz;

            // **Two-Ray (Sea) Reflection**
            // The simplest multipath model - a ray from TX -> RX,
            // and one bouncing off of the Earth and coming back up to the receiver.
            // Only apply this over the sea - assume the ground is mostly diffuse,
            // scattering the waves that bounce off of it.
            // Only apply this model when the specular midpoint (where the waves would bounce)
            // is over water.
            // (BMS marks ocean tiles with negative elevation.)
            // Reflection strength is set by the surface around that bounce point.
            double twoRayDb = 0.0;
            double txTop = Math.Max(0.0, txAltVal);
            double rxTop = Math.Max(0.0, rxAltVal);
            if (txTop + rxTop > 0.0 && dist2D > 1.0)
            {
                const double seaR0 = 0.95;           // baseline seawater reflection magnitude
                const double sigmaSeaDefault = 0.25; // sea-surface rms roughness (m), moderate swell

                double R_eff = effectiveEarthRadius;
                double theta = dist2D / R_eff;       // central surface angle (horizontal arc)

                // Specular point for unequal antenna heights: flat-earth proportional split
                // (phi = theta·txTop/(txTop+rxTop)) — exact for a flat earth, good at grazing.
                double specFrac = txTop / (txTop + rxTop);
                double phi = theta * specFrac;
                double specX = txXVal + specFrac * (rxXVal - txXVal);
                double specY = txYVal + specFrac * (rxYVal - txYVal);

                // Gate: only reflect when the bounce point itself is over water.
                if (SampleElevation(specX, specY) < 0.0)
                {
                    // Curved-earth law-of-cosines geometry: specular path-length excess,
                    // reflected legs L1/L2, and the grazing-angle sine.
                    var (delta, L1, L2, sinPsi) = TwoRaySpecularGeometry(txTop, rxTop, dist2D, R_eff);

                    // Ament/Miller-Brown specular roughness factor (power form → amplitude).
                    double roughPow = Math.Exp(-Math.Pow(4.0 * Math.PI * sigmaSeaDefault * sinPsi / wavelength, 2.0));
                    double roughAmp = Math.Sqrt(Math.Max(1e-8, roughPow));

                    // Divergence factor: the convex earth spreads the reflected ray, lowering its
                    // amplitude. Full ITU-R P.528-5 §8 eq (59).
                    // This holds at steeper reflection angles, not just grazing ones.
                    // (The classic form from "Propagation of Short Radio Waves" (Kerr, 1951) is its sin²ψ→0 limit).
                    // Rr is the reduced reflected-ray length r1·r2/(r1+r2)
                    // (eqs 57-58, with our exact slant legs L1/L2 in place of D1,2/cosψ)
                    // aa = effective Earth radius.
                    // Replaces the old ad-hoc range fade.
                    double sinPsiSafe = Math.Max(sinPsi, 1e-6);
                    double rr = L1 * L2 / Math.Max(L1 + L2, 1.0);
                    double divTerm2 = 2.0 * rr * (1.0 + sinPsi * sinPsi) / (R_eff * sinPsiSafe);
                    double divTerm3 = 2.0 * rr / R_eff;
                    double divergence = 1.0 / Math.Sqrt(1.0 + divTerm2 + divTerm3 * divTerm3);

                    // Local ocean fraction over the projected first-Fresnel footprint around the
                    // specular point (elongated along the path at grazing) — handles coastlines.
                    double f1Spec = Math.Sqrt(wavelength * L1 * L2 / Math.Max(L1 + L2, 1.0));
                    double halfLen = f1Spec / Math.Max(sinPsi, 1e-3);
                    double ux = (rxXVal - txXVal) / dist2D, uy = (rxYVal - txYVal) / dist2D;
                    const int footprintSamples = 7;
                    int oceanHits = 0;
                    for (int s = 0; s < footprintSamples; s++)
                    {
                        double frac = (s - (footprintSamples - 1) / 2.0) / ((footprintSamples - 1) / 2.0);
                        double off = frac * halfLen;
                        if (SampleElevation(specX + ux * off, specY + uy * off) < 0.0) oceanHits++;
                    }
                    double oceanScale = (double)oceanHits / footprintSamples;

                    // Ray-length factor (ITU-R P.528-5 §8 eq (60)/(61): Fr = min(r0/r12, 1)). The
                    // reflected ray is longer than the direct ray (r12 = L1+L2; r0 = r12 − delta),
                    // so it spreads more and arrives weaker: Fr = 1 − delta/(L1+L2). Bites only when
                    // the direct ray dominates — both terminals high and close (two aircraft).
                    double rayLengthFactor = Math.Min(1.0 - delta / Math.Max(L1 + L2, 1.0), 1.0);

                    // Effective reflection amplitude (P.528 RTg = Rg·Dv·Fr). Negative: grazing
                    // seawater inverts phase.
                    double R = -seaR0 * roughAmp * oceanScale * divergence * rayLengthFactor;

                    // Two-ray interference relative to free-space unit amplitude:
                    //   |1 + R·exp(jφ)| = sqrt(1 + R² + 2R·cosφ).
                    // Single evaluation at the centre frequency: averaging over the voice bandwidth
                    // is a no-op (two-ray coherence bandwidth ≫ a few kHz).
                    double phiRad = 2.0 * Math.PI * delta / wavelength;
                    double totalAmp = Math.Sqrt(1.0 + R * R + 2.0 * R * Math.Cos(phiRad));
                    // Cap at unity (no boost above free space): ITU-R P.528-5 §8 eq (64),
                    // WRL = min(|1 + R|, 1). P.528 treats the LOS two-ray region as loss-only.
                    totalAmp = Math.Min(totalAmp, 1.0);
                    twoRayDb = 20.0 * Math.Log10(Math.Max(1e-12, totalAmp));

                    #if DEBUG
                    _logger.LogDebug($"Two-Ray Model:");
                    _logger.LogDebug($"  specFrac={specFrac:F3}, oceanScale={oceanScale:F3}");
                    _logger.LogDebug($"  delta={delta:F2}m, sinPsi={sinPsi:F4}, divergence={divergence:F3}");
                    _logger.LogDebug($"  R={R:F4}, twoRayDb={twoRayDb:F2} dB");
                    #endif
                }
            }

            // twoRayDb ≤ 0: loss-only after the unity cap (P.528 eq 64) — a destructive null
            // subtracts, constructive interference is capped at free space. Applied before terrain
            // so an over-sea path already in a null still accumulates terrain loss on top.
            ap.ReceivedDb += (float)twoRayDb;
            
            // Doppler shift
            if (txVelocity.HasValue && rxVelocity.HasValue)
            {
                double ux = dx / dist, uy = dy / dist, uz = dz / dist;
                var (tvx, tvy, tvz) = txVelocity.Value;
                var (rvx, rvy, rvz) = rxVelocity.Value;

                // u points TX→RX, so closing speed = (v_tx - v_rx)·u (positive when approaching).
                // Clamp against weird velocity vectors from BMS lag.
                double closingSpeed = (tvx - rvx) * ux + (tvy - rvy) * uy + (tvz - rvz) * uz;
                closingSpeed = Math.Clamp(closingSpeed, -10000.0, 10000.0); // max ~Mach 29

                double shiftPpm = closingSpeed / SpeedOfLight * 1e6;
                ap.TuneOffsetPPM += (float)shiftPpm;
            }

            // **Terrain Diffraction**
            // Skipped entirely when the pyramid proved the first Fresnel zone is clear,
            // otherwise see DeltaBullington for details.
            if (!allClear)
            {
                ap.TerrainLossDb = (float)DeltaBullington.Loss(profile, txAltVal, rxAltVal, wavelength, effectiveEarthRadius);
                ap.ReceivedDb -= ap.TerrainLossDb;
            }
            ap.ReceivedSnrDb = ap.ReceivedDb - (float)rxSensitivity;
            ap.DropoutRate = CalculateDropoutRate(ap.ReceivedSnrDb, bandConfig);
            ap.DeepFadeRate = CalculateDeepFadeRate(ap.ReceivedSnrDb, bandConfig);
            SetTerrainProfile(ap, profile, includeTerrainProfile);
            return ap;
        }

        /// <summary>
        /// Two-ray specular geometry on a curved Earth. Returns the reflected-vs-direct path
        /// length excess (delta, m), the reflected leg lengths L1/L2 (m), and the grazing-angle
        /// sine. The specular point uses the flat-earth proportional split phi = theta·txTop/
        /// (txTop+rxTop). For a flat earth (large rEff) the excess tends to 2·txTop·rxTop/dist.
        /// </summary>
        internal static (double delta, double L1, double L2, double sinPsi) TwoRaySpecularGeometry(
            double txTop, double rxTop, double dist2D, double rEff)
        {
            double theta = dist2D / rEff;
            double specFrac = (txTop + rxTop) > 0.0 ? txTop / (txTop + rxTop) : 0.5;
            double phi = theta * specFrac;
            double a = rEff + txTop, b = rEff + rxTop;
            double Ld = Math.Sqrt(a * a + b * b - 2.0 * a * b * Math.Cos(theta));
            double L1 = Math.Sqrt(a * a + rEff * rEff - 2.0 * a * rEff * Math.Cos(phi));
            double L2 = Math.Sqrt(b * b + rEff * rEff - 2.0 * b * rEff * Math.Cos(theta - phi));
            double delta = (L1 + L2) - Ld;
            double sinPsi = Math.Abs((a * Math.Cos(phi) - rEff) / Math.Max(1e-6, L1));
            return (delta, L1, L2, sinPsi);
        }

        // Refractivity model constants (SAND2012-10690 §3.1, eqs 23-25)
        private const double GlobalSurfaceRefractivity = 324.8; // N_s: average global surface refractivity (Altshuler)
        private const double RefractivityBreakpointAltitude = 12192.0; // h_b: 40 kft, chosen for the 0-50 kft range
        private const double BreakpointRefractivity = 66.65; // N_b: Bean & Thayer's upper segment evaluated at h_b

        /// <summary>
        /// Average effective Earth radius factor k for a path between two altitudes (m MSL),
        /// used as R_eff = k * EarthRadius. Symmetric in its arguments.
        /// </summary>
        /// <remarks>
        /// SAND2012-10690 §3.2.3, "Method 2 - Average Radius of Curvature", which the report
        /// recommends as the best single formula (§3.5). Refractivity decays exponentially above
        /// the surface h_s: N(h) = N_s e^{-(h - h_s)/H_b}, with H_b = (h_b - h_s) / ln(N_s / N_b)
        /// (eqs 23-24). That makes a ray's radius of curvature
        /// rho(h) = H_b e^{(h - h_s)/H_b} / (1e-6 N_s cos psi) (eq 30), and k = 1 / (1 - R_e / rho_avg) (eq 37).
        ///
        /// The report derives this for a radar looking down at a target on the ground, averaging
        /// rho from h_s up to the aircraft. We need any two antennas, so:
        /// - h_s is sea level. N_s stays anchored to the ground instead of either antenna,
        ///   and elevated sites still see thinner air (N ≈ 267 at 1500 m), consistent with the
        ///   report's note that high terrain has lower surface refractivity.
        /// - rho is averaged over the altitudes the path spans, [lo, hi]. With lo = 0 this is exactly
        ///   eq 37. It ignores long paths sagging below the lower antenna, which underestimates k a bit.
        /// - cos psi = 1, as the report allows for shallow angles. Steep paths are short enough that
        ///   Earth curvature barely matters for them.
        ///
        /// This previously used the receiver's altitude as h_s, which drove H_b to zero as the receiver
        /// approached h_b: k went negative, and the two-ray divergence term produced NaN.
        /// </remarks>
        public static double CalculateKAvg(double altitudeA, double altitudeB)
            => CalculateKAvg(altitudeA, altitudeB, GlobalSurfaceRefractivity);

        /// <summary>
        /// <see cref="CalculateKAvg(double, double)"/> with a given surface refractivity N_s, in N-units.
        /// </summary>
        internal static double CalculateKAvg(double altitudeA, double altitudeB, double surfaceRefractivity)
        {
            double lo = Math.Max(0.0, Math.Min(altitudeA, altitudeB));
            double hi = Math.Max(0.0, Math.Max(altitudeA, altitudeB));

            // H_b, the height over which refractivity decays by a factor of e (eq 24, h_s = 0)
            double scaleHeight = RefractivityBreakpointAltitude / Math.Log(surfaceRefractivity / BreakpointRefractivity);
            // R_e / rho at sea level
            double surfaceCurvatureRatio = 1e-6 * surfaceRefractivity * EarthRadius / scaleHeight;

            // Averaging rho over [lo, hi] gives rho(lo) * (e^u - 1) / u, where u = (hi - lo) / H_b.
            // We need the reciprocal, u / (e^u - 1), which goes to 1 as the altitudes meet.
            // Use its series there instead of dividing 0 by 0.
            double u = (hi - lo) / scaleHeight;
            double spanFactor = u < 1e-6 ? 1.0 - 0.5 * u : u / (Math.Exp(u) - 1.0);

            return 1.0 / (1.0 - surfaceCurvatureRatio * Math.Exp(-lo / scaleHeight) * spanFactor);
        }

        public static AudioParams GetDefaultAudioParams(int frequencyKhz, float ppm = 0f)
        {
            var ap = new AudioParams
            {
                RadioFrequencyKHz = frequencyKhz,
                TuneOffsetPPM = ppm,
                ReceivedDb = 0f, // no losses
                ReceivedSnrDb = 50, // Clear as day.
                DropoutRate = 0f,
                DeepFadeRate = 0f
            };
            return ap;
        }
    }
}