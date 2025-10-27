using System;
using System.Diagnostics;
using System.Threading;

namespace BMSAudioSim;

public class RadioEffect
{
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly float[] _prevOut;
    private readonly Lock _lock = new();
    private AudioParams _params;
    private double _flutterPhase;
    private int _dropoutSamplesLeft = 0;
    
    private static readonly ThreadLocal<Random> Random =
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
        this._sampleRate = sampleRate;
        this._channels = channels;
        _params = initial;
        _prevOut = new float[channels];
    }

    public void Process(float[] buffer, int offset, int samples)
    {
        AudioParams p;
        lock (_lock) p = _params;

        double dt = 1.0 / _sampleRate;
        float cutoff = MathF.Max(100, MathF.Min(p.LowpassHz, 0.45f * _sampleRate));
        float rc = 1f / (2f * MathF.PI * cutoff);
        float alpha = (float)(dt / (rc + dt));

        for (int i = 0; i < samples; i += _channels)
        {
            // Start a dropout if none active
            if (_dropoutSamplesLeft <= 0)
            {
                // Probability per block of ~10 ms instead of per sample
                double blockProb = p.DropoutProb * 0.05; // tune sensitivity here
                Debug.Assert(Random.Value != null, "Random.Value != null");
                if (Random.Value.NextDouble() < blockProb)
                {
                    // Random dropout between 10–200 ms
                    _dropoutSamplesLeft = (int)(_sampleRate * (0.01 + 0.19 * Random.Value.NextDouble()));
                }
            }

            bool isDropout = _dropoutSamplesLeft > 0;
            if (isDropout) _dropoutSamplesLeft--;

            for (int c = 0; c < _channels; c++)
            {
                int idx = offset + i + c;
                float x = buffer[idx];
                if (isDropout) x = 0f;

                // Lowpass
                float y = _prevOut[c] + alpha * (x - _prevOut[c]);
                _prevOut[c] = y;

                // Flutter
                float flutter = 1f + 0.05f * p.FlutterDepth * (float)Math.Sin(_flutterPhase);
                float val = y * p.Gain * flutter;

                buffer[idx] = val;
            }

            _flutterPhase += 2 * Math.PI * 10.0 * dt;
            if (_flutterPhase > Math.PI * 2.0) _flutterPhase -= Math.PI * 2.0;
        }
    }
}