using System;

namespace BMSAudioSim;

public class RadioPreFilter
{
    private readonly BiquadFilter _highPass;
    private readonly BiquadFilter _lowPass;
    private readonly BiquadFilter _noiseLowPass;
    private readonly Random _rng = new Random();

    public readonly double BandwidthHz;
    
    // AGC state
    private float _agcEnvelope = 0.1f; // Start with reasonable value
    
    private volatile float _noiseLevel = 0f;
    private readonly int _sampleRate;

    public RadioPreFilter(int sampleRate, double bandwidthHz = 3000.0)
    {
        _sampleRate = sampleRate;
        BandwidthHz = bandwidthHz;
        
        // Standard voice band filters
        float lowCut = 300f;
        float highCut = 2200f; // Narrowed to match F-16 radio (was 3300 Hz)

        _highPass = BiquadFilter.HighPass(sampleRate, lowCut, 0.707f);
        _lowPass = BiquadFilter.LowPass(sampleRate, highCut, 0.707f);
        _noiseLowPass = BiquadFilter.LowPass(sampleRate, highCut, 0.707f);
    }

    public void SetNoiseLevel(float level) => _noiseLevel = Math.Clamp(level, 0f, 1f);

    public void Process(float[] buffer, int offset, int samples, int channels)
    {
        // AGC parameters - moderate settings
        const float attackCoeff = 0.96f;    // ~2ms attack
        const float releaseCoeff = 0.9995f; // ~50ms release
        const float threshold = 0.2f;       // -14dB threshold
        const float ratio = 6.0f;           // 6:1 compression (moderate)
        
        // Calculate number of frames
        int frames = samples / channels;
        
        for (int frame = 0; frame < frames; frame++)
        {
            // Generate noise
            float noise = 0f;
            if (_noiseLevel > 0f)
            {
                float w = (float)(_rng.NextDouble() * 2.0 - 1.0);
                noise = _noiseLowPass.Transform(w) * _noiseLevel;
            }

            for (int c = 0; c < channels; c++)
            {
                int idx = offset + frame * channels + c;
                float x = buffer[idx];

                // Add noise
                x += noise;

                // Bandpass
                x = _highPass.Transform(x);
                x = _lowPass.Transform(x);

                // === Simple AGC ===
                float absInput = MathF.Abs(x);
                
                // Envelope follower
                if (absInput > _agcEnvelope)
                    _agcEnvelope = _agcEnvelope * attackCoeff + absInput * (1f - attackCoeff);
                else
                    _agcEnvelope = _agcEnvelope * releaseCoeff + absInput * (1f - releaseCoeff);
                
                // Gain reduction
                float gainReduction = 1f;
                if (_agcEnvelope > threshold)
                {
                    float excess = _agcEnvelope / threshold;
                    gainReduction = threshold / _agcEnvelope * (1f + (excess - 1f) / ratio);
                }
                
                x *= gainReduction;
                
                // === Mild asymmetric clipping ===
                if (x > 0.8f)
                    x = 0.8f + (x - 0.8f) * 0.3f;
                else if (x < -0.85f)
                    x = -0.85f + (x + 0.85f) * 0.35f;
                
                // Soft saturation
                x = MathF.Tanh(x * 1.5f);

                buffer[idx] = x;
            }
        }
    }

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
            float w0 = 2f * MathF.PI * freq / sr;
            float alpha = MathF.Sin(w0) / (2f * q);
            float cosw0 = MathF.Cos(w0);
            float b0 = (1 - cosw0) / 2f;
            float b1 = 1 - cosw0;
            float b2 = (1 - cosw0) / 2f;
            float A0 = 1 + alpha;
            return new BiquadFilter(b0 / A0, b1 / A0, b2 / A0, -2f * cosw0 / A0, (1 - alpha) / A0);
        }

        public static BiquadFilter HighPass(int sr, float freq, float q)
        {
            float w0 = 2f * MathF.PI * freq / sr;
            float alpha = MathF.Sin(w0) / (2f * q);
            float cosw0 = MathF.Cos(w0);
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