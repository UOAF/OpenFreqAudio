using System;
using System.Diagnostics;
using System.Threading;

namespace BMSAudioSim;

public class RadioEffect
{
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly float[] _prevOut;
    private readonly object _lock = new();
    private AudioParams _params;
    private double _flutterPhase;

    // === Dropout state ===
    private int _dropoutSamplesLeft = 0;
    private int _dropoutFadeSamples = 0;
    private float _dropoutAttenuation = 1f;
    private readonly Random _rng = new(Environment.TickCount);

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
            lock (_lock) _params = value;
        }
    }

    public RadioEffect(int sampleRate, int channels, AudioParams initial)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _params = initial;
        _prevOut = new float[channels];
    }

    public void Process(float[] buffer, int offset, int samples)
    {
        AudioParams p;
        lock (_lock) p = _params;

        double dt = 1.0 / _sampleRate;
        int frames = samples / _channels;

        float cutoff = MathF.Max(100, MathF.Min(p.LowpassHz, 0.45f * _sampleRate));
        float rc = 1f / (2f * MathF.PI * cutoff);
        float alpha = (float)(dt / (rc + dt));

        // === Dropout parameters ===
        double eventsPerSec = Math.Clamp(p.DropoutProb, 0.0, 5.0);
        double blockDurationSec = (double)frames / _sampleRate;
        double startProbThisBlock = eventsPerSec * blockDurationSec;
        const double meanDropMs = 120.0;
        const double minDropMs = 30.0;
        const double fadeMs = 20.0;
        const float minAttenuation = 0.1f;

        var rng = ThreadRng.Value ?? _rng;

        // maybe start a dropout
        if (_dropoutSamplesLeft <= 0 && rng.NextDouble() < startProbThisBlock)
        {
            double u = rng.NextDouble();
            double durMs = Math.Max(minDropMs, -Math.Log(1.0 - u) * meanDropMs);
            _dropoutSamplesLeft = (int)(_sampleRate * durMs / 1000.0);
            _dropoutFadeSamples = (int)(_sampleRate * fadeMs / 1000.0);
            _dropoutAttenuation = minAttenuation + (float)(rng.NextDouble() * 0.03);
        }

        for (int frame = 0; frame < frames; frame++)
        {
            bool inDrop = _dropoutSamplesLeft > 0;
            float envelope = 1f;

            if (inDrop)
            {
                int total = _dropoutFadeSamples * 2;
                int age = Math.Max(0, total - _dropoutSamplesLeft);
                if (age < _dropoutFadeSamples)
                {
                    envelope = 1f - (1f - _dropoutAttenuation) * (age / (float)_dropoutFadeSamples);
                }
                else if (_dropoutSamplesLeft < _dropoutFadeSamples)
                {
                    float t = (_dropoutFadeSamples - _dropoutSamplesLeft) / (float)_dropoutFadeSamples;
                    envelope = _dropoutAttenuation + (1f - _dropoutAttenuation) * t;
                }
                else
                {
                    envelope = _dropoutAttenuation;
                }
            }

            for (int c = 0; c < _channels; c++)
            {
                int idx = offset + frame * _channels + c;
                float x = buffer[idx];

                if (inDrop)
                {
                    // Crossfade with low-level white noise
                    float white = (float)(rng.NextDouble() * 2.0 - 1.0);
                    x = envelope * x + (1f - envelope) * white * 0.05f;
                }

                // --- Lowpass after dropout envelope ---
                float y = _prevOut[c] + alpha * (x - _prevOut[c]);
                _prevOut[c] = y;

                // --- Flutter modulation ---
                float flutter = 1f + 0.05f * p.FlutterDepth * (float)Math.Sin(_flutterPhase);
                float val = y * p.Gain * flutter;

                // --- Clamp to safe range to avoid bangs ---
                buffer[idx] = Math.Clamp(val, -1f, 1f);
            }

            if (inDrop) _dropoutSamplesLeft--;

            _flutterPhase += 2 * Math.PI * 10.0 * dt;
            if (_flutterPhase > Math.PI * 2.0)
                _flutterPhase -= Math.PI * 2.0;
        }
    }
}