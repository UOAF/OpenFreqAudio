// ReSharper disable InconsistentNaming

using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace OpenFreqAudio;

/// <summary>
/// Military radio receiver signal processor.
///
/// Signal chain per buffer:
///   1. <see cref="IAmbientNoiseEffect.ApplyPreFade"/>  — transmitter-side acoustics
///      (cockpit noise, oxygen-mask muffling, etc.)
///   2. <see cref="ApplyFading"/>                       — RF channel fading
///      (fast flutter 20–80 ms and deep fades 400–2000 ms)
///
/// The ambient layer (steps 1 + 3) is swapped out atomically when
/// <see cref="AmbientNoise"/> changes, so it can be updated per-packet.
///
/// MULTI-SCALE FADING MODEL:
/// - Fast flutter (20–80 ms):   Rapid multipath, stays above squelch.
/// - Deep fades  (400–2000 ms): Severe signal loss, can trigger squelch pops.
/// </summary>
public class RadioEffect
{
    private readonly ILogger _logger;
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly Lock _lock = new();

    private AudioParams _params;
    private AmbientNoiseType _ambientNoiseType = AmbientNoiseType.None;
    private IAmbientNoiseEffect _ambientEffect = NullAmbientEffect.Instance;

    // --- RF fading state (fast flutter) ---
    private int _dropoutSamplesLeft;
    private int _dropoutFadeSamples;
    private int _dropoutInitialSamples;

    // --- RF fading state (deep fades) ---
    private int _deepFadeSamplesLeft;
    private int _deepFadeFadeSamples;
    private int _deepFadeInitialSamples;

    private static readonly ThreadLocal<Random> ThreadRng =
        new(() => new Random(Environment.TickCount * Thread.CurrentThread.ManagedThreadId));

    // -----------------------------------------------------------------------
    //  Public properties
    // -----------------------------------------------------------------------

    public AudioParams Params
    {
        get
        {
            lock (_lock) return _params;
        }
        set
        {
            lock (_lock) _params = value;
        }
    }

    /// <summary>
    /// Acoustic environment of the transmitting platform.
    /// Setting this replaces the ambient effect instance via the factory;
    /// changes take effect at the next <see cref="Process"/> call.
    /// Thread-safe.
    /// </summary>
    public AmbientNoiseType AmbientNoise
    {
        get
        {
            lock (_lock) return _ambientNoiseType;
        }
        set
        {
            lock (_lock)
            {
                if (_ambientNoiseType == value) return;
                _ambientNoiseType = value;
                _ambientEffect = AmbientNoiseEffectFactory.Create(value, _sampleRate);
#if DEBUG
                _logger.LogDebug("AmbientNoise changed to {Type} (StreamRate={SampleRate} Hz)", value, _sampleRate);
#endif
            }
        }
    }

    // -----------------------------------------------------------------------
    //  Construction
    // -----------------------------------------------------------------------

    public RadioEffect(int sampleRate, int channels, AudioParams initial, ILogger logger)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _params = initial;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    //  Public API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Process <paramref name="samples"/> samples (frames x channels) starting
    /// at <paramref name="offset"/> in <paramref name="buffer"/> in-place.
    /// <paramref name="ambientNoiseVolume"/> (0..1) wet/dry-blends the ambient
    /// noise layer: 0 bypasses it, 1 applies it at full strength.
    /// </summary>
    public void Process(float[] buffer, int offset, int samples, float ambientNoiseVolume)
    {
        var rng = ThreadRng.Value!;
        AudioParams p;
        IAmbientNoiseEffect ambientEffect;

        lock (_lock)
        {
            p = _params;
            ambientEffect = _ambientEffect;
        }

        int frames = samples / _channels;

        // 1. Transmitter acoustics: mic pickup, mask muffling, cockpit noise.
        ambientEffect.ApplyPreFade(buffer, offset, frames, ambientNoiseVolume);

        // 2. RF channel fading: schedule new events, then apply envelopes.
        ScheduleFadingEvents(rng, frames, p);
        ApplyFading(buffer, offset, frames);

        // Safety clamp — should never fire under normal operation.
        ClampBuffer(buffer, offset, samples);
    }

    // -----------------------------------------------------------------------
    //  RF fading — private
    // -----------------------------------------------------------------------

    /// <summary>
    /// Rolls the Poisson dice for this buffer and arms new fading events
    /// if none are currently active.
    /// </summary>
    private void ScheduleFadingEvents(Random rng, int frames, AudioParams p)
    {
        double bufferSec = (double)frames / _sampleRate;

        // Fast flutter (20–80 ms "picket-fencing")
        if (_dropoutSamplesLeft <= 0 && p.DropoutRate > 0.001f)
        {
            double prob = 1.0 - Math.Exp(-p.DropoutRate * bufferSec);
            if (rng.NextDouble() < prob)
            {
                double durMs = Math.Max(20.0, -Math.Log(1.0 - rng.NextDouble()) * 80.0);
                _dropoutSamplesLeft = (int)(_sampleRate * durMs / 1000.0);
                _dropoutFadeSamples = (int)(_sampleRate * 8.0 / 1000.0);
                _dropoutInitialSamples = _dropoutSamplesLeft;
            }
        }

        // Deep fades (400–2000 ms terrain nulls / severe multipath)
        if (_deepFadeSamplesLeft <= 0 && p.DeepFadeRate > 0.001f)
        {
            double prob = 1.0 - Math.Exp(-p.DeepFadeRate * bufferSec);
            if (rng.NextDouble() < prob)
            {
                double durMs = Math.Max(400.0, -Math.Log(1.0 - rng.NextDouble()) * 1200.0);
                _deepFadeSamplesLeft = (int)(_sampleRate * durMs / 1000.0);
                _deepFadeFadeSamples = (int)(_sampleRate * 50.0 / 1000.0);
                _deepFadeInitialSamples = _deepFadeSamplesLeft;
            }
        }
    }

    /// <summary>
    /// Applies the fading envelopes frame-by-frame. Both counters are
    /// decremented here so they remain in sync with the buffer position.
    /// </summary>
    private void ApplyFading(float[] buffer, int offset, int frames)
    {
        for (int frame = 0; frame < frames; frame++)
        {
            bool inDrop = _dropoutSamplesLeft > 0;
            bool inDeepFade = _deepFadeSamplesLeft > 0;

            if (inDrop || inDeepFade)
            {
                float envelope = 1f;
                if (inDrop) envelope *= ComputeDropoutEnvelope();
                if (inDeepFade) envelope *= ComputeDeepFadeEnvelope();

                for (int c = 0; c < _channels; c++)
                    buffer[offset + frame * _channels + c] *= envelope;
            }

            if (inDrop) _dropoutSamplesLeft--;
            if (inDeepFade) _deepFadeSamplesLeft--;
        }
    }

    private float ComputeDropoutEnvelope()
    {
        int age = _dropoutInitialSamples - _dropoutSamplesLeft;
        if (age < _dropoutFadeSamples) // signal dropping
            return 1f - (float)age / _dropoutFadeSamples;
        if (_dropoutSamplesLeft < _dropoutFadeSamples) // signal returning
            return (float)_dropoutSamplesLeft / _dropoutFadeSamples;
        return 0f; // fully attenuated
    }

    private float ComputeDeepFadeEnvelope()
    {
        int age = _deepFadeInitialSamples - _deepFadeSamplesLeft;
        if (age < _deepFadeFadeSamples)
            return 1f - (float)age / _deepFadeFadeSamples;
        if (_deepFadeSamplesLeft < _deepFadeFadeSamples)
            return (float)_deepFadeSamplesLeft / _deepFadeFadeSamples;
        return 0f;
    }

    // -----------------------------------------------------------------------
    //  Utilities
    // -----------------------------------------------------------------------

    private static void ClampBuffer(float[] buffer, int offset, int samples)
    {
        int end = offset + samples;
        for (int i = offset; i < end; i++)
            buffer[i] = Math.Clamp(buffer[i], -2f, 2f);
    }
}