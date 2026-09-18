using Microsoft.Extensions.Logging;
using NWaves.Filters.Butterworth;

// ReSharper disable InconsistentNaming

namespace OpenFreqAudio;

/// <summary>
/// Renders our own microphone audio for the session recording with the same radio tone as the
/// incoming audio, but without a simulated receiver:
///   1. RadioEffect: transmitter-side ambient SFX (cockpit, mask, …)
///      RadioPlayback creates it with zero-distance params, so there is no fading.
///   2. The 300–3000 Hz band-pass of each receiver in <see cref="RadioPlayback"/>.
///
/// There is no AM envelope, background noise, AGC or squelch. At zero distance, a receiver
/// adds no audible noise to our voice. It only adds a loud pop at key-down (while the AGC
/// catches the carrier) and a noise burst at key-up (before the squelch closes).
///
/// Driven once per DSP buffer for the WHOLE recording (even when we are not transmitting).
/// The ambient SFX run continuously, so the recording has the cockpit sound between
/// transmissions too. Single-threaded (DSP thread only).
/// </summary>
public sealed class OwnVoiceRadioRenderer
{
    private readonly RadioEffect _effect;
    private readonly HighPassFilter _highPass;
    private readonly LowPassFilter _lowPass;

    public OwnVoiceRadioRenderer(int sampleRate, AudioParams initial, ILogger logger)
    {
        _effect = new RadioEffect(sampleRate, 1, initial, logger);
        _highPass = new HighPassFilter(300.0 / sampleRate, 3);
        _lowPass = new LowPassFilter(3000.0 / sampleRate, 6);
    }

    /// <summary>Transmitter-acoustics SFX layer (cockpit, mask, …). Thread-safe.</summary>
    public AmbientNoiseType AmbientNoise
    {
        get => _effect.AmbientNoise;
        set => _effect.AmbientNoise = value;
    }

    /// <summary>
    /// Render one DSP buffer of <paramref name="frames"/> samples of own-voice audio (zero where
    /// we have none) into <paramref name="buffer"/> in place.
    /// <paramref name="sfxVolume"/> (0..1) wet/dry-blends the ambient SFX. The band-pass always
    /// runs, so own voice keeps the radio tone even with no SFX.
    /// </summary>
    public void Process(float[] buffer, int frames, float sfxVolume)
    {
        // Transmitter acoustics (ambient SFX)
        _effect.Process(buffer, 0, frames, sfxVolume);

        // Band-pass for the radio tone (run the filters every sample for continuity).
        for (int i = 0; i < frames; i++)
        {
            buffer[i] = _lowPass.Process(_highPass.Process(buffer[i]));
        }
    }
}
