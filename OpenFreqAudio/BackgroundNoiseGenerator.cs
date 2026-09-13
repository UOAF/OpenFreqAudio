using NWaves.Filters.Butterworth;

namespace OpenFreqAudio;

/// <summary>
/// Receiver noise, generated as complex baseband (IQ),
/// added to the AM signals (per their SNR) before envelope detection.
///
/// Two components, summed before the IF filter:
/// <list type="number">
/// <item>Thermal noise, modeled as a complex Gaussian distribution</item>
/// <item>Impulses — Poisson arrivals in time, each a complex delta with
/// uniformly random phase and log-normal magnitude.</item>
/// </list>
/// Both are run through a complex lowpass at IF_BW/2.
/// This band-limits the noise to the channel, and gives each impulse its duration.
///
/// Three properties must hold:
///
/// <b>Circular symmetry.</b> I and Q must be independent. Expanding what the
/// envelope detector sees, for a carrier A·e^(jθ),
///
///   |A·e^(jθ) + n|² = A² + 2A·(n_I·cos θ + n_Q·sin θ) + |n|²
///
/// so the noise reaches the output through its projection onto the carrier, and
/// that projection has variance
///
///   Var(n_I·cos θ + n_Q·sin θ) = σ²·(1 + ρ·sin 2θ),   ρ = corr(n_I, n_Q)
///
/// Any ρ ≠ 0 therefore makes detected noise power depend on θ — and θ advances at
/// the carrier's Doppler offset, so the static picks up a tremolo at twice the
/// Doppler shift, peak-to-trough (1+ρ)/(1−ρ). The previous generator drew I and Q
/// from consecutive samples of one pink process, ρ = 0.43, which predicts 4.0 dB
/// and measured 3.95. A real-valued impulse dropped into I alone breaks this the
/// same way, hence the random phase in <see cref="Next"/>.
///
/// <b>White within the channel.</b> RF noise is flat across an 8 kHz slice of a
/// 120 MHz band. The pink, crackling character of a radio is the audio chain
/// that lives after the envelope detector.
/// Coloring the I/Q instead makes the timbre of the static track the carrier offset.
///
/// <b>Unit power.</b> E|n|² = 1, so <c>ReceivedSnrDb</c> is the accurate
/// carrier-to-noise measurement (C/N) within the radio's bandwidth.
/// Measured through the whole chain (envelope detection, AGC, 300–3000 Hz bandpass),
/// detected audio SNR — the same ratio taken after the detector, in the speech
/// band — tracks that C/N to within about a dB anywhere from 0 to 20 dB C/N.
/// Without a passband here, that same unit power spreads over the whole ±Fs/2,
/// so less of it lands in the speech band and the receiver reads about 6 dB
/// more sensitive than the budget says — 5.9 dB at 48 kHz.
/// The post-detection bandpass cannot repair that: envelope detection is nonlinear,
/// so the two filters do not commute, and by then nothing downstream can know what
/// bandwidth the power handed to the detector was spread across.
///
/// The old Voss-McCartney generator ran at 1/15 variance per quadrature (five
/// dice uniform on [−1,1], averaged), so E|n|² = 2/15 — 8.75 dB quieter, and
/// measured at −8.73 dB. The same <c>ReceivedSnrDb</c> is therefore 8.75 dB
/// noisier here than it used to be.
///
/// Unit power also puts the noise envelope's RMS at exactly 1, which makes the
/// 2.0 squelch threshold precisely the +6 dB that the squelch comment in
/// RadioPlayback claims to be modelling; the same threshold sat 14.8 dB up under
/// the old generator. That is the operative statistic, since the squelch detector
/// follows mean power. Pick a different one for "the floor" and the margin moves:
/// 8.7 dB over the envelope mean (VHF noise here is impulsive, not Rayleigh, so its
/// mean envelope is 0.73 rather than √π/2), and 5.5 dB over where the AGC settled back
/// when AgcDecay was 3.3 ms — which is where the gate used to sit, and why it was so
/// easily broken. At today's 33 ms decay the AGC rests near 2.1 on this noise, so that
/// margin would now be nil; squelch has its own detector precisely so it doesn't care.
/// </summary>
public class BackgroundNoiseGenerator
{
    // A 25 kHz AM channel gets ~8 kHz of bandwidth, so ±4 kHz at complex baseband.
    // (8.33 kHz European VHF would want ~1.7 kHz here, if it ever matters.)
    //
    // Two things ride on this number. It is the bandwidth ReceivedSnrDb is quoted in
    // (see the unit-power note above), and at roughly twice the 3 kHz audio passband —
    // the double-sideband AM relationship — C/N and detected audio SNR come out equal
    // to within about a dB. It also sets the ringing length T_eff ≈ 1/IF_BW = 125 µs that
    // gives an injected delta its duration, and with λ·T_eff ≈ 0.3 at VHF that is what keeps
    // impulses resolvable as crackle rather than overlapping into a roar (Middleton class A).
    private const double IfBandwidthHz = 8000.0;
    private const int IfFilterOrder = 4;


    /// <summary>
    /// Noise character at one frequency. There is no VHF/UHF switch: the difference
    /// between the bands is that external noise falls off with frequency while the
    /// receiver's own thermal floor does not, which is a continuous law rather than a
    /// cliff at 200 MHz. See <see cref="ForFrequency"/>.
    /// </summary>
    private readonly record struct BandNoise(
        double ImpulseRateHz,        // λ, impulse arrivals per second
        double Gamma,                // thermal / (ambient external + corona)
        double LogNormalSigma,       // s, spread of impulse magnitudes
        double ExternalNoiseGain)    // amplitude of the raised floor; 1 in clear air
    {
        // λ and s belong to the impulse *sources*, not to the frequency. The same spark
        // gap fires at the same rate whoever is listening, and the spread of discharge
        // strengths is a property of the discharge. What frequency decides is how much
        // of that energy lands in the passband, which is what Γ carries.
        private const double SourceImpulseRateHz = 2400.0;
        private const double SourceMagnitudeSpread = 0.5;

        // Anchor: at 120 MHz P.372's median external noise sits ~6 dB over a typical
        // receiver's thermal floor, so external/thermal = 4 and Γ = 0.25.
        private const double AnchorMhz = 120.0;
        private const double AnchorExternalPower = 4.0;

        // External noise falls 27.7 dB/decade (P.372's man-made slope; galactic is 23)
        // while receiver thermal is flat, so their ratio scales as f^2.77.
        private const double ExternalSlope = 2.77;

        // Corona discharge falls off faster than ambient man-made noise. That, plus the
        // thermal floor it has to climb over being relatively larger up there, is why
        // P-static blankets VHF and leaves UHF usable.
        private const double CoronaSlope = 4.0;

        // Severe P-static is worth about +40 dB of external noise power at the anchor.
        private const double CoronaPowerDb = 40.0;

        /// <summary>
        /// Noise parameters for a frequency, optionally in precipitation static
        /// (0 clear, 1 severe). Everything is a noise power relative to the receiver's
        /// own thermal floor, which is 1 by definition and flat with frequency.
        /// </summary>
        public static BandNoise ForFrequency(int frequencyKhz, float precipitation = 0f)
        {
            double ratio = frequencyKhz / 1000.0 / AnchorMhz;

            double external = AnchorExternalPower * Math.Pow(ratio, -ExternalSlope);
            double corona = precipitation <= 0f
                ? 0.0
                : (Math.Pow(10.0, CoronaPowerDb / 10.0 * precipitation) - 1.0)
                  * Math.Pow(ratio, -CoronaSlope);

            double impulsive = external + corona;

            // Corona is a continuous discharge rather than discrete sparks, so as it
            // takes over, the arrival rate climbs until impulses overlap inside the IF
            // filter's response and stop being resolvable. That is the whole mechanism
            // behind P-static being a roar and not a crackle.
            double coronaShare = corona / impulsive;
            double rate = SourceImpulseRateHz * (1.0 + 9.0 * coronaShare);

            // Total noise power goes from 1 + external to 1 + external + corona
            // (thermal is 1 by definition), but the generator normalizes whatever it
            // is handed to unit power, so the extra floor has to be reapplied
            // afterwards as an amplitude — hence the square root.
            //
            // The floor genuinely rises, which is what buries the voice and what lets
            // the roar break squelch. NB: this is invisible to DropoutRate and
            // DeepFadeRate, which FastPathAudioSim computes from ReceivedSnrDb alone.
            // The link budget is this term's right home once weather gets plumbed.
            double gain = Math.Sqrt((1.0 + impulsive) / (1.0 + external));

            return new BandNoise(rate, 1.0 / impulsive, SourceMagnitudeSpread, gain);
        }
    }

    private readonly int _sampleRate;
    private readonly Random _rng;

    // One real lowpass per quadrature makes a complex baseband lowpass.
    private readonly LowPassFilter _ifI;
    private readonly LowPassFilter _ifQ;

    // Σh² for the IF filter, measured from its own impulse response so that changing
    // the design above can't silently move the noise level out from under the scaling.
    private readonly double _noiseGain;

    private readonly int _frequencyKhz;
    private BandNoise _band;

    private double _arrivalProb;    // λ / Fs, impulses per sample
    private double _expNegArrival;  // e^-arrivalProb, for the Poisson draw
    private double _logNormalSigma;
    private float _impulseScale;
    private float _thermalScale;

    private float _precipitation;
    private float _externalGain = 1f;

    private double? _spareGaussian;

    /// <param name="seed">Fixed seed for tests. Production leaves this null.</param>
    public BackgroundNoiseGenerator(int sampleRate, int frequencyKhz, int? seed = null)
    {
        _sampleRate = sampleRate;

        // NB: not Environment.TickCount. Two generators constructed in the same tick
        // would draw identical noise, so every slot would hiss in unison — and if I
        // and Q ever shared a generator again it would be a 100%-depth warble instead
        // of the 43% one this class exists to fix.
        _rng = seed.HasValue ? new Random(seed.Value) : new Random();

        double cutoff = IfBandwidthHz / 2.0 / sampleRate;
        _ifI = new LowPassFilter(cutoff, IfFilterOrder);
        _ifQ = new LowPassFilter(cutoff, IfFilterOrder);
        _noiseGain = MeasureImpulseResponse(cutoff, IfFilterOrder);

        _frequencyKhz = frequencyKhz;
        UpdateScales();
    }

    /// <summary>
    /// Precipitation static, 0 (clear) to 1 (severe). Flying through ice crystals or
    /// snow charges the airframe and the static wicks discharge continuously.
    ///
    /// Both of its characteristic behaviours are emergent rather than special-cased:
    /// it is a roar and not a crackle because the arrival rate climbs until impulses
    /// overlap inside the IF filter's response, and it blankets VHF while leaving UHF
    /// usable because corona falls off faster with frequency than ambient noise does
    /// and has a relatively larger thermal floor to climb over up there.
    ///
    /// Nothing drives this yet — the weather state is in the sim but is not plumbed
    /// through to the receiver.
    /// </summary>
    public float PrecipitationStatic
    {
        get => _precipitation;
        set
        {
            float clamped = Math.Clamp(value, 0f, 1f);
            if (clamped == _precipitation) return;
            _precipitation = clamped;
            UpdateScales();
        }
    }

    /// <summary>
    /// Draw the next complex noise sample. I and Q must be independent.
    /// </summary>
    public void Next(out float i, out float q)
    {
        double xi = 0.0;
        double xq = 0.0;

        if (_impulseScale > 0f)
        {
            int arrivals = NextPoisson();
            for (int j = 0; j < arrivals; ++j)
            {
                // Log-normal magnitude, normalized so E[|a|²] = 1:
                //   |a| = exp(N(0,s) − s²)
                //   E[|a|²] = E[exp(2·N(0,s) − 2s²)] = e^(2s²) · e^(−2s²) = 1
                // using E[e^X] = e^(σ²/2) for X ~ N(0,σ). Note the −s², not the −s²/2
                // that would normalize E[|a|] instead and leave the noise e^(s²) hot.
                double mag = _logNormalSigma > 0.0
                    ? Math.Exp(NextGaussian() * _logNormalSigma - _logNormalSigma * _logNormalSigma)
                    : 1.0;
                double phi = _rng.NextDouble() * 2.0 * Math.PI;
                xi += _impulseScale * mag * Math.Cos(phi);
                xq += _impulseScale * mag * Math.Sin(phi);
            }
        }

        xi += _thermalScale * NextGaussian();
        xq += _thermalScale * NextGaussian();

        i = _externalGain * _ifI.Process((float)xi);
        q = _externalGain * _ifQ.Process((float)xq);
    }

    /// <summary>
    /// Run a unit impulse through a scratch copy of the IF filter to get Σh².
    /// An order-4 Butterworth at Fs/12 is long dead well inside this window.
    /// </summary>
    private static double MeasureImpulseResponse(double cutoff, int order)
    {
        var probe = new LowPassFilter(cutoff, order);
        double sumH2 = 0.0;
        for (int n = 0; n < 4096; ++n)
        {
            double h = probe.Process(n == 0 ? 1.0f : 0.0f);
            sumH2 += h * h;
        }
        return sumH2;
    }

    private void UpdateScales()
    {
        _band = BandNoise.ForFrequency(_frequencyKhz, _precipitation);
        _logNormalSigma = _band.LogNormalSigma;

        double thermalFraction = _band.Gamma / (1.0 + _band.Gamma);
        double impulseFraction = 1.0 / (1.0 + _band.Gamma);

        _arrivalProb = _band.ImpulseRateHz / _sampleRate;
        _expNegArrival = Math.Exp(-_arrivalProb);

        // Solve for the pre-filter scales that land at the wanted post-filter power.
        // A filter multiplies input noise power by Σh² = _noiseGain, so with
        // p = λ/Fs the per-sample arrival probability:
        //
        //   thermal:  2 · _thermalScale² · Σh² = thermalFraction   (two quadratures)
        //   impulses: p · _impulseScale² · E[|a|²] · Σh² = impulseFraction
        //
        // and E[|a|²] = 1 by the normalization in Next. Both invert directly. The
        // factor of 2 on the thermal line is what keeps the two quadratures together
        // at thermalFraction rather than 2·thermalFraction.
        _thermalScale = (float)Math.Sqrt(thermalFraction / (2.0 * _noiseGain));
        _impulseScale = impulseFraction > 0.0 && _arrivalProb > 0.0
            ? (float)Math.Sqrt(impulseFraction / (_arrivalProb * _noiseGain))
            : 0f;

        _externalGain = (float)_band.ExternalNoiseGain;
    }

    private int NextPoisson()
    {
        // Knuth, which is only sane for small λ — and λ here is 0.05 impulses per
        // sample at VHF, so this exits without entering the loop ~95% of the time.
        double p = _rng.NextDouble();
        int k = 0;
        while (p > _expNegArrival)
        {
            ++k;
            p *= _rng.NextDouble();
        }
        return k;
    }

    private double NextGaussian()
    {
        // https://en.wikipedia.org/wiki/Marsaglia_polar_method
        // Produces a pair of normally distributed variables,
        // which is nice since we often need two at a time.
        if (_spareGaussian.HasValue)
        {
            var ret = _spareGaussian.Value;
            _spareGaussian = null;
            return ret;
        }

        double u, v, s;
        do
        {
            u = _rng.NextDouble() * 2.0 - 1.0;
            v = _rng.NextDouble() * 2.0 - 1.0;
            s = u * u + v * v;
        } while (s >= 1.0 || s == 0.0);

        double f = Math.Sqrt(-2.0 * Math.Log(s) / s);
        _spareGaussian = v * f;
        return u * f;
    }
}
