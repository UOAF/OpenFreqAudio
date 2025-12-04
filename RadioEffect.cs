using System;
using System.Threading;

namespace BMSAudioSim;

/// <summary>
/// Modern military radio receiver effects (AN/ARC-210/222 style)
/// 
/// Signal chain: Analog FM transmission → Analog FM demodulator → Digital audio processing
/// 
/// Receiver-side effects (AFTER demodulation):
/// - Fast digital squelch (DSP-based)
/// - Sharp brick-wall filtering (digital IIR filters)
/// - RF fading effects (pre-demod phenomena that affect audio)
/// 
/// NOTE: Transmissions are ANALOG FM - no digital vocoder, packets, or bit errors!
/// Digital processing happens ONLY in the receiver's audio backend after demodulation.
/// </summary>
public class RadioEffect
{
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly float[] _prevOut;
    private readonly object _lock = new();
    private AudioParams _params;
    
    // User-configurable squelch threshold
    private float _squelchThreshold = 0.03f;
    
    // Digital filter state (2-stage biquad needs 4 states per channel)
    private readonly float[] _filterState; // [x[n-1], x[n-2], y[n-1], y[n-2]] per channel
    
    // Squelch gate state (digital = much faster)
    private enum SquelchState { Closed, Opening, Open, Closing }
    private SquelchState _squelchState = SquelchState.Closed;
    private int _squelchTransitionSamples = 0;
    private int _squelchTransitionLength = 0;
    private const int SquelchAttackSamples = 12;   // ~0.25ms at 48kHz (very fast digital)
    private const int SquelchReleaseSamples = 240;  // ~5ms at 48kHz (fast digital)
    
    /// <summary>
    /// Check if squelch is currently open (allowing audio through)
    /// </summary>
    public bool IsSquelchOpen
    {
        get
        {
            lock (_lock)
            {
                return _squelchState == SquelchState.Open || _squelchState == SquelchState.Opening;
            }
        }
    }
    
    // Squelch burst state (the characteristic "pop" when gate opens/closes)
    private int _squelchBurstSamplesLeft = 0;
    private const int SquelchBurstDuration = 720; // ~15ms at 48kHz (longer, softer)
    private const float SquelchBurstAmplitude = 0.12f; // Audible but not harsh (increased for filtered version)
    
    // Squelch burst low-pass filter state (removes harsh high frequencies)
    private readonly float[] _squelchBurstFilterHistory = new float[4]; // 4-tap moving average (balanced)
    private int _squelchBurstFilterIndex = 0;
    
    // Dropout state (digital = full muting or corruption)
    private int _dropoutSamplesLeft = 0;
    private int _dropoutFadeSamples = 0;
    private bool _dropoutIsMute = true; // true = mute, false = digital corruption
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
                bool wasWeak = _params.Gain < _squelchThreshold;
                bool isWeak = value.Gain < _squelchThreshold;
                
                // Signal crossed threshold - transition immediately regardless of current state
                if (wasWeak && !isWeak)
                {
                    // Signal came up - open squelch (fast digital)
                    _squelchState = SquelchState.Opening;
                    _squelchTransitionSamples = 0;
                    _squelchTransitionLength = SquelchAttackSamples;
                    
                    // Trigger squelch burst (opening pop)
                    _squelchBurstSamplesLeft = SquelchBurstDuration;
                    Array.Clear(_squelchBurstFilterHistory, 0, _squelchBurstFilterHistory.Length);
                    _squelchBurstFilterIndex = 0;
                }
                else if (!wasWeak && isWeak)
                {
                    // Signal dropped - close squelch (fast digital)
                    _squelchState = SquelchState.Closing;
                    _squelchTransitionSamples = 0;
                    _squelchTransitionLength = SquelchReleaseSamples;
                    
                    // Trigger squelch burst (closing pop)
                    _squelchBurstSamplesLeft = SquelchBurstDuration;
                    Array.Clear(_squelchBurstFilterHistory, 0, _squelchBurstFilterHistory.Length);
                    _squelchBurstFilterIndex = 0;
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
        _filterState = new float[channels * 4]; // 4 states per channel (x[n-1], x[n-2], y[n-1], y[n-2])
        
        // Initialize squelch state based on initial signal strength
        _squelchState = initial.Gain >= _squelchThreshold ? SquelchState.Open : SquelchState.Closed;
    }

    /// <summary>
    /// Set the squelch threshold for this RadioEffect
    /// </summary>
    public void SetSquelchThreshold(float threshold)
    {
        lock (_lock)
        {
            float oldThreshold = _squelchThreshold;
            _squelchThreshold = Math.Clamp(threshold, 0.001f, 1.0f);
            
            // Re-evaluate squelch state with new threshold
            bool wasWeak = _params.Gain < oldThreshold;
            bool isWeak = _params.Gain < _squelchThreshold;
            
            // Handle all states - signal crossed threshold
            if (wasWeak && !isWeak)
            {
                // Signal is now strong enough - open squelch regardless of current state
                _squelchState = SquelchState.Opening;
                _squelchTransitionSamples = 0;
                _squelchTransitionLength = SquelchAttackSamples;
                
                // Trigger squelch burst
                _squelchBurstSamplesLeft = SquelchBurstDuration;
                Array.Clear(_squelchBurstFilterHistory, 0, _squelchBurstFilterHistory.Length);
                _squelchBurstFilterIndex = 0;
            }
            else if (!wasWeak && isWeak)
            {
                // Signal is now too weak - close squelch regardless of current state
                _squelchState = SquelchState.Closing;
                _squelchTransitionSamples = 0;
                _squelchTransitionLength = SquelchReleaseSamples;
                
                // Trigger squelch burst
                _squelchBurstSamplesLeft = SquelchBurstDuration;
                Array.Clear(_squelchBurstFilterHistory, 0, _squelchBurstFilterHistory.Length);
                _squelchBurstFilterIndex = 0;
            }
        }
    }

    /// <summary>
    /// Manually trigger a squelch burst (for transmission start/stop)
    /// </summary>
    public void TriggerSquelchBurst()
    {
        lock (_lock)
        {
            if (_squelchBurstSamplesLeft != 0) return;
            
            _squelchBurstSamplesLeft = SquelchBurstDuration;
            
            // Reset filter history to avoid artifacts from previous bursts
            Array.Clear(_squelchBurstFilterHistory, 0, _squelchBurstFilterHistory.Length);
            _squelchBurstFilterIndex = 0;
            
            Console.Out.WriteLine($"Triggering Squelch Burst {_squelchBurstSamplesLeft}");
        }
    }

    public void Process(float[] buffer, int offset, int samples)
    {
        AudioParams p;
        lock (_lock) p = _params;

        double dt = 1.0 / _sampleRate;
        int frames = samples / _channels;

        // Digital brick-wall filter coefficients
        // Using cascaded biquad for sharper rolloff
        float cutoff = MathF.Max(300, MathF.Min(p.LowpassHz, 0.45f * _sampleRate));
        float omega = 2f * MathF.PI * cutoff / _sampleRate;
        float cosOmega = MathF.Cos(omega);
        float Q = 0.707f; // Butterworth
        float alpha = MathF.Sin(omega) / (2f * Q);
        
        float b0 = (1f - cosOmega) / 2f;
        float b1 = 1f - cosOmega;
        float b2 = b0;
        float a0 = 1f + alpha;
        float a1 = -2f * cosOmega;
        float a2 = 1f - alpha;
        
        // Normalize
        b0 /= a0; b1 /= a0; b2 /= a0;
        a1 /= a0; a2 /= a0;

        // Dropout parameters (RF fading/multipath, not digital packet loss)
        // In analog FM, dropouts come from:
        // - Multipath fading (Rayleigh/Rician fading)
        // - Terrain shadowing
        // - Atmospheric effects
        double eventsPerSec = Math.Clamp(p.DropoutProb, 0.0, 2.0); // Max 2/sec
        double blockDurationSec = (double)frames / _sampleRate;
        double startProbThisBlock = eventsPerSec * blockDurationSec;
        const double meanDropMs = 75.0; // Average fade duration
        const double maxDropMs = 300.0; // Max fade duration
        const double minDropMs = 20.0;  // Min fade duration
        const double fadeMs = 0.5; // Fast fade in/out (squelch response)

        var rng = ThreadRng.Value ?? _rng;

        // Maybe start a dropout (RF fading event)
        if (_dropoutSamplesLeft <= 0 && rng.NextDouble() < startProbThisBlock)
        {
            double u = rng.NextDouble();
            double durMs = Math.Max(minDropMs, -Math.Log(1.0 - u) * meanDropMs);
            _dropoutSamplesLeft = (int)(_sampleRate * durMs / 1000.0);
            _dropoutFadeSamples = (int)(_sampleRate * fadeMs / 1000.0);
            
            // Analog FM fading: Just signal loss, no corruption
            // (corruption would require digital codec, which we don't have)
            _dropoutIsMute = true; // Always mute for analog fading
        }

        for (int frame = 0; frame < frames; frame++)
        {
            // === Digital squelch gate (fast, no burst) ===
            float squelchGain = 1f;
            
            switch (_squelchState)
            {
                case SquelchState.Closed:
                    squelchGain = 0f;
                    break;
                    
                case SquelchState.Opening:
                    // Linear ramp (digital is clean and fast)
                    float openProgress = (float)_squelchTransitionSamples / _squelchTransitionLength;
                    squelchGain = openProgress;
                    
                    _squelchTransitionSamples++;
                    if (_squelchTransitionSamples >= _squelchTransitionLength)
                        _squelchState = SquelchState.Open;
                    break;
                    
                case SquelchState.Open:
                    squelchGain = 1f;
                    break;
                    
                case SquelchState.Closing:
                    // Linear ramp down (fast)
                    float closeProgress = (float)_squelchTransitionSamples / _squelchTransitionLength;
                    squelchGain = 1f - closeProgress;
                    
                    _squelchTransitionSamples++;
                    if (_squelchTransitionSamples >= _squelchTransitionLength)
                        _squelchState = SquelchState.Closed;
                    break;
            }

            // === Squelch burst generation (the "pop" sound) ===
            float squelchBurstSample = 0f;
            if (_squelchBurstSamplesLeft > 0)
            {
                // Generate analog-style bandlimited noise (not harsh white noise)
                int age = SquelchBurstDuration - _squelchBurstSamplesLeft;
                
                // Gentler envelope: slower attack, slower exponential decay
                float envelope;
                if (age < 120) // ~2.5ms attack at 48kHz (even slower for less click)
                {
                    // Cubic ease-in curve for very gentle attack
                    float attackProgress = (float)age / 120f;
                    envelope = attackProgress * attackProgress * attackProgress; // Cubic ease-in
                }
                else
                {
                    // Slower exponential decay over remaining duration
                    float decayProgress = (float)(age - 120) / (SquelchBurstDuration - 120);
                    envelope = MathF.Exp(-4.0f * decayProgress); // Slightly faster decay
                }
                
                // Generate raw noise
                float noise1 = (float)(rng.NextDouble() * 2.0 - 1.0);
                float noise2 = (float)(rng.NextDouble() * 2.0 - 1.0);
                float noise3 = (float)(rng.NextDouble() * 2.0 - 1.0);
                
                // Pre-average to reduce initial harshness
                float rawNoise = (noise1 + noise2 + noise3) / 3.0f;
                
                // Apply 4-tap moving average low-pass filter to remove high frequencies
                // Balanced between smoothness and audibility
                _squelchBurstFilterHistory[_squelchBurstFilterIndex] = rawNoise;
                _squelchBurstFilterIndex = (_squelchBurstFilterIndex + 1) % 4;
                
                // Calculate filtered output (average of last 4 samples)
                float filteredNoise = 0f;
                for (int i = 0; i < 4; i++)
                {
                    filteredNoise += _squelchBurstFilterHistory[i];
                }
                filteredNoise /= 4.0f;
                
                // Apply envelope and reduced amplitude for subtler, less clicky effect
                squelchBurstSample = filteredNoise * envelope * SquelchBurstAmplitude;
                
                _squelchBurstSamplesLeft--;
            }

            // === Dropout envelope (RF fading) ===
            bool inDrop = _dropoutSamplesLeft > 0;
            float dropoutEnvelope = 1f;

            if (inDrop)
            {
                int fadeIn = _dropoutFadeSamples;
                int fadeOut = _dropoutFadeSamples;
                int totalDrop = _dropoutSamplesLeft + fadeIn + fadeOut;
                int age = totalDrop - _dropoutSamplesLeft;
                
                if (age < fadeIn)
                {
                    // Fast fade to mute (squelch closing)
                    dropoutEnvelope = 1f - (float)age / fadeIn;
                }
                else if (_dropoutSamplesLeft < fadeOut)
                {
                    // Fast fade back (squelch opening)
                    dropoutEnvelope = (float)(_dropoutFadeSamples - _dropoutSamplesLeft) / fadeOut;
                }
                else
                {
                    // Full dropout (signal loss)
                    dropoutEnvelope = 0f;
                }
            }

            for (int c = 0; c < _channels; c++)
            {
                int idx = offset + frame * _channels + c;
                float x = buffer[idx];

                // Apply squelch gate (clean digital, no burst)
                x *= squelchGain;

                // === RF Fading (analog signal loss) ===
                if (inDrop)
                {
                    // Analog FM fading: Just signal attenuation
                    // No digital corruption artifacts (no codec to corrupt!)
                    x *= dropoutEnvelope;
                }

                // === Digital brick-wall filter (biquad) ===
                // State indices for this channel: [x[n-1], x[n-2], y[n-1], y[n-2]]
                int stateBase = c * 4;
                
                // Direct Form II biquad implementation
                float xn1 = _filterState[stateBase];     // x[n-1]
                float xn2 = _filterState[stateBase + 1]; // x[n-2]
                float yn1 = _filterState[stateBase + 2]; // y[n-1]
                float yn2 = _filterState[stateBase + 3]; // y[n-2]
                
                // Compute output
                float y = b0 * x + b1 * xn1 + b2 * xn2 - a1 * yn1 - a2 * yn2;
                
                // Update state
                _filterState[stateBase + 1] = xn1; // x[n-2] = x[n-1]
                _filterState[stateBase] = x;       // x[n-1] = x[n]
                _filterState[stateBase + 3] = yn1; // y[n-2] = y[n-1]
                _filterState[stateBase + 2] = y;   // y[n-1] = y[n]
                
                _prevOut[c] = y;
                
                // Apply gain
                float val = y * p.Gain;

                // === Analog receiver noise (optional) ===
                // In weak signal conditions, analog FM receivers exhibit:
                // - Thermal noise (becomes dominant below FM threshold)
                // - Background hiss
                // This is different from digital quantization noise!
                if (p.NoiseLevel > 0.1f && squelchGain > 0f)
                {
                    // Thermal/background noise (analog characteristic)
                    // Only when signal is weak and squelch is open
                    float thermalNoise = (float)(rng.NextDouble() * 2.0 - 1.0) * p.NoiseLevel * 0.02f;
                    val += thermalNoise;
                }

                // Clamp to safe range
                val = Math.Clamp(val, -1f, 1f);
                
                // === Mix in squelch burst INDEPENDENTLY ===
                // This happens outside the signal chain so it's always audible
                // regardless of gain or squelch state
                val += squelchBurstSample;
                
                buffer[idx] = Math.Clamp(val, -1f, 1f);
            }

            if (inDrop) _dropoutSamplesLeft--;
        }
    }
}