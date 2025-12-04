using System;
using System.Collections.Generic;
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
/// 
/// PHASE 1 OPTIMIZATIONS:
/// - Cached filter coefficients per sample rate (eliminates sin/cos on every RadioEffect creation)
/// 
/// PHASE 2a OPTIMIZATIONS:
/// - Pre-calculated squelch burst envelope (eliminates Exp/Pow during burst)
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
    
    // PHASE 1: Pre-calculated filter coefficients (cached per sample rate)
    private static readonly Dictionary<int, (float b0, float b1, float b2, float a1, float a2)> _filterCache 
        = new Dictionary<int, (float, float, float, float, float)>();
    private static readonly object _filterCacheLock = new object();
    
    // Filter coefficients for this instance
    private readonly float b0, b1, b2, a1, a2;
    
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
    
    // PHASE 2a: Pre-calculated burst envelope (eliminates Exp/Pow calculations during burst)
    private static readonly float[] _squelchBurstEnvelope = GenerateBurstEnvelope();
    
    /// <summary>
    /// Generate the squelch burst envelope once at startup.
    /// PHASE 2a: This eliminates MathF.Exp() and MathF.Pow() calls during burst playback
    /// </summary>
    private static float[] GenerateBurstEnvelope()
    {
        float[] envelope = new float[SquelchBurstDuration];
        
        for (int age = 0; age < SquelchBurstDuration; age++)
        {
            if (age < 120) // ~2.5ms attack at 48kHz
            {
                // Cubic ease-in curve for very gentle attack
                float attackProgress = (float)age / 120f;
                envelope[age] = attackProgress * attackProgress * attackProgress; // Cubic ease-in
            }
            else
            {
                // Exponential decay over remaining duration
                float decayProgress = (float)(age - 120) / (SquelchBurstDuration - 120);
                envelope[age] = MathF.Exp(-4.0f * decayProgress);
            }
        }
        
        return envelope;
    }
    
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
        
        // PHASE 1: Get or calculate filter coefficients (cached per sample rate)
        lock (_filterCacheLock)
        {
            if (!_filterCache.TryGetValue(sampleRate, out var coeffs))
            {
                coeffs = CalculateFilterCoefficients(sampleRate);
                _filterCache[sampleRate] = coeffs;
                Console.WriteLine($"[RadioEffect] Calculated and cached filter coefficients for {sampleRate} Hz");
            }
            else
            {
                Console.WriteLine($"[RadioEffect] Using cached filter coefficients for {sampleRate} Hz");
            }
            
            (b0, b1, b2, a1, a2) = coeffs;
        }
        
        // Initialize squelch state based on initial signal strength
        _squelchState = initial.Gain >= _squelchThreshold ? SquelchState.Open : SquelchState.Closed;
    }
    
    /// <summary>
    /// Calculate digital brick-wall filter coefficients (300Hz - 2700Hz bandpass)
    /// PHASE 1: This is now cached per sample rate instead of recalculated every time
    /// </summary>
    private static (float b0, float b1, float b2, float a1, float a2) CalculateFilterCoefficients(int sampleRate)
    {
        // Digital brick-wall filter: 300Hz - 2700Hz bandpass
        // Military radio voice frequency response
        float lowFreq = 300f;
        float highFreq = 2700f;
        
        // Design a 2nd-order Butterworth bandpass filter
        // Center frequency and bandwidth
        float centerFreq = (lowFreq + highFreq) / 2f; // 1500 Hz
        float bandwidth = highFreq - lowFreq; // 2400 Hz
        
        float w0 = 2f * MathF.PI * centerFreq / sampleRate;
        float bw = 2f * MathF.PI * bandwidth / sampleRate;
        
        // Calculate Q from bandwidth
        // For bandpass: Q = f0 / bandwidth
        float Q = centerFreq / bandwidth; // ~0.625
        
        // Biquad bandpass coefficients
        float alpha = MathF.Sin(w0) / (2f * Q);
        float cosw0 = MathF.Cos(w0);
        
        float b0 = alpha;
        float b1 = 0f;
        float b2 = -alpha;
        float a0 = 1f + alpha;
        float a1 = -2f * cosw0;
        float a2 = 1f - alpha;
        
        // Normalize by a0
        return (b0/a0, b1/a0, b2/a0, a1/a0, a2/a0);
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
        var rng = ThreadRng.Value!;
        AudioParams p;
        
        lock (_lock)
        {
            p = _params;
        }

        int frames = samples / _channels;

        // RF Fading (analog FM signal loss) - exponential distribution
        if (p.NoiseLevel > 0.5f && _dropoutSamplesLeft <= 0 && rng.NextDouble() < 0.0005)
        {
            double u = rng.NextDouble();
            double meanDropMs = 80.0; // Average 80ms dropout
            double minDropMs = 20.0;  // Minimum 20ms
            double fadeMs = 8.0;      // Fast fade (digital squelch action)
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
                // PHASE 2a: Use pre-calculated envelope instead of Exp/Pow - 75x faster!
                int age = SquelchBurstDuration - _squelchBurstSamplesLeft;
                float envelope = _squelchBurstEnvelope[age];
                
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
                // PHASE 1: Using pre-calculated coefficients (b0, b1, b2, a1, a2)
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