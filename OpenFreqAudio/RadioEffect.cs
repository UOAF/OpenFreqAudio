// ReSharper disable InconsistentNaming

using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace OpenFreqAudio;

/// <summary>
/// Modern military radio receiver effects (AN/ARC-210/222 style)
/// 
/// Signal chain: Analog AM/FM transmission → Analog AM/FM demodulator → Digital audio processing
/// 
/// Receiver-side effects (AFTER demodulation):
/// - Sharp brick-wall filtering (digital IIR filters)
/// - RF fading effects (pre-demod phenomena that affect audio)
/// 
/// NOTE: Transmissions are ANALOG AM/FM - no digital vocoder, packets, or bit errors!
/// Digital processing happens ONLY in the receiver's audio backend after demodulation.
/// 
/// MULTI-SCALE FADING MODEL:
/// - Fast flutter (20-80ms): Rapid multipath interference, stays above squelch
/// - Deep fades (400-2000ms): Severe signal loss, can trigger squelch pops
/// </summary>
public class RadioEffect
{
    private ILogger _logger;
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly Lock _lock = new();
    private AudioParams _params;

    // Pre-calculated filter coefficients (cached per sample rate)
    private static readonly Dictionary<int, (float b0, float b1, float b2, float a1, float a2)> FilterCache = new();

    private static readonly Lock FilterCacheLock = new();

    // Filter coefficients for this instance
    private readonly float _b0, _b1, _b2, _a1, _a2;

    // Digital filter state (2-stage biquad needs 4 states per channel)
    private readonly float[] _filterState; // [x[n-1], x[n-2], y[n-1], y[n-2]] per channel

    // DC whine state
    // private double _whinePhase;
    private const float WhineFreq = 520f; // typical avionics inverter whine (400–800 Hz)
    private const float WhineLevel = 0.003f; // extremely subtle, like cockpit background

    // Deep rumble state
    // private double _rumblePhase;
    private const float RumbleFreq = 80f;    // Low rumble
    private const float RumbleLevel = 0.03f; // Subtle but noticeable
    
    // Oxygen-mask style muffling
    private const float MuffleCutoff = 900f; // muffled low-pass
    private float _muffleA; // filter coefficient
    private float[] _muffleLPChannels;
    private float[] _muffleLP2Channels;

    // Fast flutter state (rapid multipath fading, 20-80ms)
    private int _dropoutSamplesLeft;
    private int _dropoutFadeSamples;
    private int _dropoutInitialSamples; // Track initial duration for envelope calculation

    // Deep fade state (slow severe fading, 400-2000ms)
    private int _deepFadeSamplesLeft;
    private int _deepFadeFadeSamples;
    private int _deepFadeInitialSamples;

    private static readonly ThreadLocal<Random> ThreadRng =
        new(() => new Random(Environment.TickCount * Thread.CurrentThread.ManagedThreadId));

    public AudioParams Params
    {
        get
        {
            lock (_lock) return _params;
        }
        set
        {
            lock (_lock)
            {
                _params = value;
            }
        }
    }

    public RadioEffect(int sampleRate, int channels, AudioParams initial, ILogger logger)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _params = initial;
        _logger = logger;
        _filterState = new float[channels * 4]; // 4 states per channel (x[n-1], x[n-2], y[n-1], y[n-2])

        // Get or calculate filter coefficients (cached per sample rate)
        lock (FilterCacheLock)
        {
            if (!FilterCache.TryGetValue(sampleRate, out var coeffs))
            {
                coeffs = CalculateFilterCoefficients(sampleRate);
                FilterCache[sampleRate] = coeffs;
                #if DEBUG
                logger.LogDebug($"Calculated and cached filter coefficients for {sampleRate} Hz");
                #endif
            }
            else
            {
                #if DEBUG
                logger.LogDebug($"Using cached filter coefficients for {sampleRate} Hz");
                #endif
            }

            (_b0, _b1, _b2, _a1, _a2) = coeffs;
        }

        // Precompute one-pole LPF coefficient for muffling (oxygen mask effect)
        // Lower cutoff = more muffled (typical military masks: 600-700 Hz)
        _muffleA = MathF.Exp(-2f * MathF.PI * MuffleCutoff / _sampleRate);
        _muffleLPChannels = new float[_channels];
        _muffleLP2Channels = new float[_channels];
    }

    /// <summary>
    /// Calculate digital brick-wall filter coefficients (300Hz - 2700Hz bandpass)
    /// </summary>
    private static (float b0, float b1, float b2, float a1, float a2) CalculateFilterCoefficients(int sampleRate)
    {
        // Digital brick-wall filter: 300Hz - 2700Hz bandpass
        // Military radio voice frequency response
        float lowFreq = 300f;
        float highFreq = 2700f;

        // Design a 2nd-order Butterworth bandpass filter
        // Center frequency and bandwidth
        float centerFreq = (lowFreq + highFreq) / 2f; // 1500 Hz
        float bandwidth = highFreq - lowFreq; // 2400 Hz

        float w0 = 2f * MathF.PI * centerFreq / sampleRate;

        // Calculate Q from bandwidth
        // For bandpass: Q = f0 / bandwidth
        float Q = centerFreq / bandwidth; // ~0.625

        // Biquad bandpass coefficients
        float alpha = MathF.Sin(w0) / (2f * Q);
        float cosw0 = MathF.Cos(w0);

        float b0 = alpha;
        float b1 = 0f;
        float b2 = -alpha;
        float a0 = 1f + alpha;
        float a1 = -2f * cosw0;
        float a2 = 1f - alpha;

        // Normalize by a0
        return (b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    /// <summary>
    /// Process buffer through military radio receiver effects
    /// </summary>
    public void Process(float[] buffer, int offset, int samples)
    {
        var rng = ThreadRng.Value!;
        AudioParams p;

        lock (_lock)
        {
            p = _params;
        }

        int frames = samples / _channels;

        // === FAST FLUTTER: Rapid multipath fading (20-80ms) ===
        // This is Poisson process for "picket-fencing" effect
        // DropoutProb is the rate (events per second)
        if (_dropoutSamplesLeft <= 0 && p.DropoutRate > 0.001f)
        {
            double bufferDurationSec = (double)frames / _sampleRate;
            double expectedEvents = p.DropoutRate * bufferDurationSec;
            double dropoutProbability = 1.0 - Math.Exp(-expectedEvents);

            if (rng.NextDouble() < dropoutProbability)
            {
                double u = rng.NextDouble();
                double meanDropMs = 80.0; // Average 80ms (fast flutter)
                double minDropMs = 20.0; // Minimum 20ms
                double fadeMs = 8.0; // Fast fade
                double durMs = Math.Max(minDropMs, -Math.Log(1.0 - u) * meanDropMs);
                _dropoutSamplesLeft = (int)(_sampleRate * durMs / 1000.0);
                _dropoutFadeSamples = (int)(_sampleRate * fadeMs / 1000.0);
                _dropoutInitialSamples = _dropoutSamplesLeft;
            }
        }

        // === DEEP FADES: Slow severe dropouts (400-2000ms) ===
        // Independent Poisson process for terrain nulls, severe multipath
        // These CAN drop signal below squelch threshold → trigger pops
        if (_deepFadeSamplesLeft <= 0 && p.DeepFadeRate > 0.001f)
        {
            double bufferDurationSec = (double)frames / _sampleRate;
            double expectedEvents = p.DeepFadeRate * bufferDurationSec;
            double fadeProbability = 1.0 - Math.Exp(-expectedEvents);

            if (rng.NextDouble() < fadeProbability)
            {
                double u = rng.NextDouble();
                double meanDropMs = 1200.0; // Average 1.2 seconds (deep fade)
                double minDropMs = 400.0; // Minimum 400ms
                double fadeMs = 50.0; // Slower fade (50ms)
                double durMs = Math.Max(minDropMs, -Math.Log(1.0 - u) * meanDropMs);
                _deepFadeSamplesLeft = (int)(_sampleRate * durMs / 1000.0);
                _deepFadeFadeSamples = (int)(_sampleRate * fadeMs / 1000.0);
                _deepFadeInitialSamples = _deepFadeSamplesLeft;
            }
        }

        for (int frame = 0; frame < frames; frame++)
        {
            bool inDrop = _dropoutSamplesLeft > 0;
            bool inDeepFade = _deepFadeSamplesLeft > 0;

            for (int c = 0; c < _channels; c++)
            {
                int idx = offset + frame * _channels + c;
                float x = buffer[idx];

                // === RF Fading (both fast flutter and deep fades) ===
                if (inDrop)
                {
                    int fadeIn = _dropoutFadeSamples;
                    int fadeOut = _dropoutFadeSamples;
                    int age = _dropoutInitialSamples - _dropoutSamplesLeft;

                    float dropoutEnvelope;
                    if (age < fadeIn)
                        dropoutEnvelope = 1f - (float)age / fadeIn;
                    else if (_dropoutSamplesLeft < fadeOut)
                        dropoutEnvelope = (float)(fadeOut - _dropoutSamplesLeft) / fadeOut;
                    else
                        dropoutEnvelope = 0f;

                    x *= dropoutEnvelope;
                }

                if (inDeepFade)
                {
                    int fadeIn = _deepFadeFadeSamples;
                    int fadeOut = _deepFadeFadeSamples;
                    int age = _deepFadeInitialSamples - _deepFadeSamplesLeft;

                    float deepFadeEnvelope;
                    if (age < fadeIn)
                        deepFadeEnvelope = 1f - (float)age / fadeIn;
                    else if (_deepFadeSamplesLeft < fadeOut)
                        deepFadeEnvelope = (float)(fadeOut - _deepFadeSamplesLeft) / fadeOut;
                    else
                        deepFadeEnvelope = 0f;

                    x *= deepFadeEnvelope;
                }
                // TODO: Add back in, DOWNRANGE OF DEMODULATION AND MIXING
                /*
                // Generate subtle DC whine (actually AC tone)
                double whineIncrement = 2.0 * Math.PI * WhineFreq / _sampleRate;
                float whineSample = (float)Math.Sin(_whinePhase) * WhineLevel;
                _whinePhase += whineIncrement;
                if (_whinePhase > Math.PI * 2) _whinePhase -= Math.PI * 2;
                
                // Generate subtle deep rumble
                double rumbleIncrement = 2.0 * Math.PI * RumbleFreq / _sampleRate;
                float rumbleSample = (float)Math.Sin(_rumblePhase) * RumbleLevel;
                _rumblePhase += rumbleIncrement;
                if (_rumblePhase > Math.PI * 2) _rumblePhase -= Math.PI * 2;

                // === Oxygen mask (two-pole strong LPF + nasal boost) ===
                float lp1 = _muffleLPChannels[c];
                lp1 = _muffleA * lp1 + (1f - _muffleA) * x;
                _muffleLPChannels[c] = lp1;

                float lp2 = _muffleLP2Channels[c];
                lp2 = _muffleA * lp2 + (1f - _muffleA) * lp1;
                _muffleLP2Channels[c] = lp2;


                // Stronger nasal boost for helmet/mask resonance
                float nasal = x * 0.55f;

                // Blend to final mask sound
                x = (lp2 * 0.75f) + (nasal * 0.25f);

                // === Digital brick-wall filter (biquad) ===
                // State indices for this channel: [x[n-1], x[n-2], y[n-1], y[n-2]]
                int stateBase = c * 4;

                // Direct Form II biquad implementation
                float xn1 = _filterState[stateBase]; // x[n-1]
                float xn2 = _filterState[stateBase + 1]; // x[n-2]
                float yn1 = _filterState[stateBase + 2]; // y[n-1]
                float yn2 = _filterState[stateBase + 3]; // y[n-2]

                // Compute output
                float y = _b0 * x + _b1 * xn1 + _b2 * xn2 - _a1 * yn1 - _a2 * yn2;

                // Update state
                _filterState[stateBase + 1] = xn1; // x[n-2] = x[n-1]
                _filterState[stateBase] = x; // x[n-1] = x[n]
                _filterState[stateBase + 3] = yn1; // y[n-2] = y[n-1]
                _filterState[stateBase + 2] = y; // y[n-1] = y[n]
                */
                buffer[idx] = Math.Clamp(x, -2f, 2f);
            }

            if (inDrop) _dropoutSamplesLeft--;
            if (inDeepFade) _deepFadeSamplesLeft--;
        }
    }
}