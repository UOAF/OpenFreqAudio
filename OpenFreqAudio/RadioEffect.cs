// ReSharper disable InconsistentNaming

using Microsoft.Extensions.Logging;

namespace OpenFreqAudio;

/// <summary>
/// Military radio receiver signal processor.
///
/// Applies the transmitter-side acoustic layer
/// (<see cref="IAmbientNoiseEffect.ApplyPreFade"/>) to the clean voice buffer, before
/// the mixer modulates it onto a carrier. Cockpit noise, oxygen-mask muffling, and the
/// like.
///
/// The ambient layer is swapped out atomically when <see cref="AmbientNoise"/> changes,
/// so it can be updated per-packet.
///
/// RF channel effects do not belong here. The received power that
/// <see cref="FastPathAudioSim"/> computes already carries them, and the mixer applies
/// it to the carrier, where the AGC and the squelch can respond to it.
/// </summary>
public class RadioEffect
{
    private readonly ILogger _logger;
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly Lock _lock = new();

    private AmbientNoiseType _ambientNoiseType = AmbientNoiseType.None;
    private IAmbientNoiseEffect _ambientEffect = NullAmbientEffect.Instance;

    // -----------------------------------------------------------------------
    //  Public properties
    // -----------------------------------------------------------------------

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

    public RadioEffect(int sampleRate, int channels, ILogger logger)
    {
        _sampleRate = sampleRate;
        _channels = channels;
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
        IAmbientNoiseEffect ambientEffect;

        lock (_lock)
        {
            ambientEffect = _ambientEffect;
        }

        int frames = samples / _channels;

        // Transmitter acoustics: mic pickup, mask muffling, cockpit noise.
        ambientEffect.ApplyPreFade(buffer, offset, frames, ambientNoiseVolume);

        // Safety clamp — should never fire under normal operation.
        ClampBuffer(buffer, offset, samples);
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