using System;
using System.Threading;

namespace BMSAudioSim;

public class RadioEffect
{
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly float[] _prevOut;
    private readonly object _lock = new();
    private AudioParams _params;
    
    // Flutter state (variable)
    private double _flutterPhase;
    private float _flutterFreq = 10f;
    private float _flutterFreqTarget = 10f;
    private float _flutterDepthMod = 1f;
    
    // Squelch gate state
    private enum SquelchState { Closed, Opening, Open, Closing }
    private SquelchState _squelchState = SquelchState.Closed;
    private int _squelchTransitionSamples = 0;
    private int _squelchTransitionLength = 0;
    private const int SquelchAttackSamples = 48;  // ~1ms at 48kHz
    private const int SquelchReleaseSamples = 2400; // ~50ms at 48kHz
    private bool _squelchNoiseAdded = false;
    
    // Dropout state
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
            lock (_lock)
            {
                // Detect signal strength changes for squelch
                bool wasWeak = _params.Gain < 0.05f;
                bool isWeak = value.Gain < 0.05f;
                
                if (wasWeak && !isWeak && _squelchState == SquelchState.Closed)
                {
                    // Signal came up - open squelch
                    _squelchState = SquelchState.Opening;
                    _squelchTransitionSamples = 0;
                    _squelchTransitionLength = SquelchAttackSamples;
                    _squelchNoiseAdded = false;
                }
                else if (!wasWeak && isWeak && _squelchState == SquelchState.Open)
                {
                    // Signal dropped - close squelch
                    _squelchState = SquelchState.Closing;
                    _squelchTransitionSamples = 0;
                    _squelchTransitionLength = SquelchReleaseSamples;
                }
                
                _params = value;
            }
        }
    }

    public RadioEffect(int sampleRate, int channels, AudioParams initial)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _params = initial;
        _prevOut = new float[channels];
        
        // Initialize squelch state based on initial signal strength
        _squelchState = initial.Gain >= 0.05f ? SquelchState.Open : SquelchState.Closed;
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

        // Dropout parameters
        double eventsPerSec = Math.Clamp(p.DropoutProb, 0.0, 5.0);
        double blockDurationSec = (double)frames / _sampleRate;
        double startProbThisBlock = eventsPerSec * blockDurationSec;
        const double meanDropMs = 120.0;
        const double minDropMs = 30.0;
        const double fadeMs = 20.0;
        const float minAttenuation = 0.05f; // More severe dropout minimum

        var rng = ThreadRng.Value ?? _rng;

        // Maybe start a dropout
        if (_dropoutSamplesLeft <= 0 && rng.NextDouble() < startProbThisBlock)
        {
            double u = rng.NextDouble();
            double durMs = Math.Max(minDropMs, -Math.Log(1.0 - u) * meanDropMs);
            _dropoutSamplesLeft = (int)(_sampleRate * durMs / 1000.0);
            _dropoutFadeSamples = (int)(_sampleRate * fadeMs / 1000.0);
            _dropoutAttenuation = minAttenuation + (float)(rng.NextDouble() * 0.08);
        }
        
        // Variable flutter frequency (random walk)
        if (rng.NextDouble() < 0.001) // Occasionally change target
        {
            _flutterFreqTarget = 8f + (float)(rng.NextDouble() * 4.0); // 8-12 Hz
        }
        _flutterFreq += (_flutterFreqTarget - _flutterFreq) * 0.001f; // Smooth transition
        
        // Flutter depth modulation (varies with signal quality)
        _flutterDepthMod = 0.6f + 0.4f * (float)Math.Sin(_flutterPhase * 0.03);

        for (int frame = 0; frame < frames; frame++)
        {
            // === Squelch gate processing ===
            float squelchGain = 1f;
            
            switch (_squelchState)
            {
                case SquelchState.Closed:
                    squelchGain = 0f;
                    break;
                    
                case SquelchState.Opening:
                    float openProgress = (float)_squelchTransitionSamples / _squelchTransitionLength;
                    squelchGain = openProgress * openProgress; // Exponential curve
                    
                    // Add characteristic squelch opening burst
                    if (!_squelchNoiseAdded && openProgress > 0.1f)
                    {
                        _squelchNoiseAdded = true;
                        // Burst added per-channel below
                    }
                    
                    _squelchTransitionSamples++;
                    if (_squelchTransitionSamples >= _squelchTransitionLength)
                        _squelchState = SquelchState.Open;
                    break;
                    
                case SquelchState.Open:
                    squelchGain = 1f;
                    break;
                    
                case SquelchState.Closing:
                    float closeProgress = (float)_squelchTransitionSamples / _squelchTransitionLength;
                    squelchGain = 1f - closeProgress;
                    
                    _squelchTransitionSamples++;
                    if (_squelchTransitionSamples >= _squelchTransitionLength)
                        _squelchState = SquelchState.Closed;
                    break;
            }

            // === Dropout envelope ===
            bool inDrop = _dropoutSamplesLeft > 0;
            float dropoutEnvelope = 1f;

            if (inDrop)
            {
                int total = _dropoutFadeSamples * 2;
                int age = Math.Max(0, total - _dropoutSamplesLeft);
                if (age < _dropoutFadeSamples)
                {
                    dropoutEnvelope = 1f - (1f - _dropoutAttenuation) * (age / (float)_dropoutFadeSamples);
                }
                else if (_dropoutSamplesLeft < _dropoutFadeSamples)
                {
                    float t = (_dropoutFadeSamples - _dropoutSamplesLeft) / (float)_dropoutFadeSamples;
                    dropoutEnvelope = _dropoutAttenuation + (1f - _dropoutAttenuation) * t;
                }
                else
                {
                    dropoutEnvelope = _dropoutAttenuation;
                }
            }

            for (int c = 0; c < _channels; c++)
            {
                int idx = offset + frame * _channels + c;
                float x = buffer[idx];
                
                // Add squelch opening burst
                if (_squelchState == SquelchState.Opening && !_squelchNoiseAdded)
                {
                    x += (float)(rng.NextDouble() * 2.0 - 1.0) * 0.15f;
                }

                // Apply squelch gate
                x *= squelchGain;

                // Dropout processing
                if (inDrop)
                {
                    // Mix with harsh static during dropout
                    float dropNoise = (float)(rng.NextDouble() * 2.0 - 1.0);
                    x = dropoutEnvelope * x + (1f - dropoutEnvelope) * dropNoise * 0.12f;
                }

                // Lowpass filter (after dropout for smoothness)
                float y = _prevOut[c] + alpha * (x - _prevOut[c]);
                _prevOut[c] = y;

                // === Variable flutter modulation ===
                float flutterAmount = 0.05f * p.FlutterDepth * _flutterDepthMod;
                float flutter = 1f + flutterAmount * (float)Math.Sin(_flutterPhase);
                
                // Add occasional "warble" (fast AM modulation)
                if (rng.NextDouble() < 0.0005) // Rare warble events
                {
                    float warble = (float)Math.Sin(_flutterPhase * 40.0) * 0.02f;
                    flutter += warble;
                }
                
                float val = y * p.Gain * flutter;

                // Clamp to safe range
                buffer[idx] = Math.Clamp(val, -1f, 1f);
            }

            if (inDrop) _dropoutSamplesLeft--;

            // Update flutter phase with variable frequency
            _flutterPhase += 2 * Math.PI * _flutterFreq * dt;
            if (_flutterPhase > Math.PI * 2.0)
                _flutterPhase -= Math.PI * 2.0;
        }
    }
}