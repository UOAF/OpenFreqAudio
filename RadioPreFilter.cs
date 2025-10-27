using System;

namespace BMSAudioSim;
public class RadioPreFilter
{
    private readonly BiquadFilter _highPass;
    private readonly BiquadFilter _lowPass;
    private readonly BiquadFilter _noiseLowPass; // to bandlimit noise
    private readonly Random _rng = new Random();

    // effective audio bandwidth (Hz) used for noise-floor calculations and filter tuning
    public readonly double BandwidthHz;

    // global scaling for noise: 0..1 (AudioParams.NoiseLevel)
    private volatile float _noiseLevel = 0f;

    public RadioPreFilter(int sampleRate, double bandwidthHz = 3000.0)
    {
        BandwidthHz = bandwidthHz;
        // typical voice band: 300 Hz - (300 + bandwidth)
        float lowCut = 300f;
        float highCut = (float)(300f + Math.Min(bandwidthHz, 3400.0 - 300f)); // cap at 3400

        _highPass = BiquadFilter.HighPass(sampleRate, lowCut, 0.707f);
        _lowPass = BiquadFilter.LowPass(sampleRate, highCut, 0.707f);

        // simple lowpass to bandlimit generated noise to the top of the passband
        _noiseLowPass = BiquadFilter.LowPass(sampleRate, highCut, 0.707f);
    }

    public void SetNoiseLevel(float level) => _noiseLevel = Math.Clamp(level, 0f, 1f);

    // In-place processing on floats interleaved (stereo or mono)
    public void Process(float[] buffer, int offset, int samples, int channels)
    {
        // If stereo, we'll process each channel independently (same filters are used per-channel state)
        for (int i = 0; i < samples; i += channels)
        {
            // Produce band-limited noise sample (one per sample frame)
            float noise = 0f;
            if (_noiseLevel > 0f)
            {
                // Generate white noise sample [-1..1]
                float w = (float)(_rng.NextDouble() * 2.0 - 1.0);

                // lowpass it to bandlimit to radio bandwidth (single-pole-ish via biquad)
                // small optimization: reuse noiseLowPass state across channels; that's ok
                noise = _noiseLowPass.Transform(w) * _noiseLevel;
            }

            for (int c = 0; c < channels; c++)
            {
                int idx = offset + i + c;
                float x = buffer[idx];

                // add band-limited noise BEFORE bandpass (realistic chain)
                x += noise;

                // apply bandpass: highpass then lowpass
                x = _highPass.Transform(x);
                x = _lowPass.Transform(x);

                // mild compression/saturation for radio timbre
                x = (float)Math.Tanh(1.5f * x);

                buffer[idx] = x;
            }
        }
    }

    // Basic biquad (same as earlier; keep per-instance state for each filter)
    private class BiquadFilter
    {
        private readonly float a0, a1, a2, b1, b2;
        private float z1, z2;
        public BiquadFilter(float a0, float a1, float a2, float b1, float b2)
        {
            this.a0 = a0; this.a1 = a1; this.a2 = a2; this.b1 = b1; this.b2 = b2;
        }

        public static BiquadFilter LowPass(int sr, float freq, float q)
        {
            float w0 = 2f * (float)Math.PI * freq / sr;
            float alpha = (float)Math.Sin(w0) / (2f * q);
            float cosw0 = (float)Math.Cos(w0);
            float b0 = (1 - cosw0) / 2f;
            float b1 = 1 - cosw0;
            float b2 = (1 - cosw0) / 2f;
            float A0 = 1 + alpha;
            return new BiquadFilter(b0 / A0, b1 / A0, b2 / A0, -2f * cosw0 / A0, (1 - alpha) / A0);
        }

        public static BiquadFilter HighPass(int sr, float freq, float q)
        {
            float w0 = 2f * (float)Math.PI * freq / sr;
            float alpha = (float)Math.Sin(w0) / (2f * q);
            float cosw0 = (float)Math.Cos(w0);
            float b0 = (1 + cosw0) / 2f;
            float b1 = -(1 + cosw0);
            float b2 = (1 + cosw0) / 2f;
            float A0 = 1 + alpha;
            return new BiquadFilter(b0 / A0, b1 / A0, b2 / A0, -2f * cosw0 / A0, (1 - alpha) / A0);
        }

        public float Transform(float x)
        {
            float y = a0 * x + z1;
            z1 = a1 * x + z2 - b1 * y;
            z2 = a2 * x - b2 * y;
            return y;
        }
    }
}
