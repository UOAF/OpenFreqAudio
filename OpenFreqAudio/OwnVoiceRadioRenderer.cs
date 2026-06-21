using Microsoft.Extensions.Logging;
using NWaves.Filters.Butterworth;

// ReSharper disable InconsistentNaming

namespace OpenFreqAudio;

/// <summary>
/// Renders our own microphone audio as it would sound coming back over the radio from the same position (i.e. at zero distance):
/// full signal, no path loss, no fading.
/// This is a port of the single-transmitter signal chain in
/// <see cref="RadioPlayback"/>'s DSP callback
///   1. RadioEffect: transmitter-side ambient SFX (cockpit, mask, …) + RF fading
///   2. AM envelope + background noise + AGC: the radio sound. The AGC is bounded here
///      (unlike a bare-voice AGC) because the carrier + noise floor are always present.
///   3. 300–3000 Hz band-pass
///   4. squelch gate: opens when the AGC-tracked signal level crosses the threshold and
///      tails out (noise swell) when the carrier drops on key-up, exactly like the incoming
///      per-slot gate.
///
/// Driven once per DSP buffer for the WHOLE recording (even when we are not transmitting):
/// the noise/AGC/squelch state machine must run continuously so the gate behaves correctly.
/// When idle the gate is closed and it outputs silence. Single-threaded (DSP thread only).
/// </summary>
public sealed class OwnVoiceRadioRenderer
{
    // Matches the multi-transmitter mixer in RadioPlayback.
    private const double ModIndex = 0.9;

    // Default per-slot squelch gate: slot.SquelchLevel (1.0) * 2f, as in RadioPlayback fan-out.
    private const float SquelchThreshold = 2f;

    private readonly RadioEffect _effect;

    // ALC + AGC time constants are shared with the receive chain — see RadioPlayback.
    private readonly AttackDecayFilter _agc;
    private readonly HighPassFilter _highPass;
    private readonly LowPassFilter _lowPass;
    private readonly int _sampleRate;

    // Per-frequency background noise. Recreated when the tuned frequency changes.
    private BackgroundNoiseGenerator? _noise;
    private int _noiseFreqKhz = -1;

    // Linear received power relative to the noise floor (10^(SNR/20)); cached from params.
    private float _relativePower = 1f;

    /// <summary>
    /// When false, the own voice is passed through clean (level-controlled only) — no ambient
    /// SFX, AM envelope, noise, AGC, squelch or band-pass. When true (default), the full radio
    /// chain is applied so own voice matches the incoming sound.
    /// </summary>
    public bool ApplySfx { get; set; } = true;

    public OwnVoiceRadioRenderer(int sampleRate, AudioParams initial, ILogger logger)
    {
        _sampleRate = sampleRate;
        _effect = new RadioEffect(sampleRate, 1, initial, logger);
        _agc = AttackDecayFilter.MakeAttackDecayFilter(
            RadioPlayback.AgcAttack, RadioPlayback.AgcDecay, sampleRate);
        _highPass = new HighPassFilter(300.0 / sampleRate, 3);
        _lowPass = new LowPassFilter(3000.0 / sampleRate, 6);
        ApplyParams(initial);
    }

    /// <summary>
    /// Update the radio params and transmitter-acoustics SFX layer. Call when a
    /// transmission starts (frequency / ambient may differ per radio).
    /// </summary>
    public void SetParams(AudioParams p, AmbientNoiseType ambient)
    {
        _effect.Params = p;
        _effect.AmbientNoise = ambient;
        ApplyParams(p);
    }

    private void ApplyParams(AudioParams p)
    {
        _relativePower = (float)Math.Pow(10, p.ReceivedSnrDb / 20.0);
        if (p.RadioFrequencyKHz != _noiseFreqKhz && p.RadioFrequencyKHz > 0)
        {
            _noise = new BackgroundNoiseGenerator(_sampleRate, p.RadioFrequencyKHz);
            _noiseFreqKhz = p.RadioFrequencyKHz;
        }
    }

    /// <summary>
    /// Render one DSP buffer of <paramref name="frames"/> samples into <paramref name="buffer"/>
    /// in place. The first <paramref name="voiceCount"/> samples carry real own-voice audio
    /// (carrier on); the remainder are treated as carrier-off (key released) so the squelch
    /// tail develops. <paramref name="ambientVolume"/> (0..1) wet/dry-blends the ambient SFX.
    /// </summary>
    public void Process(float[] buffer, int voiceCount, int frames, float ambientVolume)
    {
        if (!ApplySfx)
        {
            return;
        }

        if (_noise == null)
        {
            // No frequency yet — nothing to render. Keep the channel silent.
            Array.Clear(buffer, 0, frames);
            return;
        }

        // 1. Transmitter acoustics (ambient SFX) + RF fading, on the voice portion only.
        if (voiceCount > 0) _effect.Process(buffer, 0, voiceCount, ambientVolume);

        // 2. AM envelope + noise + AGC (single transmitter → no beats).
        for (int i = 0; i < frames; i++)
        {
            bool carrierOn = i < voiceCount;
            float a = carrierOn ? _relativePower : 0f;
            float samp = carrierOn ? buffer[i] : 0f;

            double iComp = _noise.NextSample() + a * (1 + samp * ModIndex);
            double qComp = _noise.NextSample();
            float env = (float)Math.Sqrt(iComp * iComp + qComp * qComp);

            _agc.Apply(env);
            float norm = env / _agc.D1;

            // 3. Band-pass for the radio tone (run the filters every sample for continuity).
            norm = _lowPass.Process(_highPass.Process(norm));

            // 4. Squelch gate: open while the signal level is above threshold; tails out as
            //    the AGC decays after the carrier drops, then cuts to silence.
            buffer[i] = _agc.D1 >= SquelchThreshold ? norm : 0f;
        }
    }
}
