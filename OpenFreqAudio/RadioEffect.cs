namespace OpenFreqAudio;

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
/// MULTI-SCALE FADING MODEL:
/// - Fast flutter (20-80ms): Rapid multipath interference, stays above squelch
/// - Deep fades (400-2000ms): Severe signal loss, can trigger squelch pops
/// </summary>
public class RadioEffect
{
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly float[] _prevOut;
    private readonly object _lock = new();
    private AudioParams _params;
    
    private float _squelchThreshold = 0.1f;
    
    // A stream can have good signal but no audio data flowing (i.e. WebRTC streams)
    private bool _isStreamActive = false;
    
    // Pre-calculated filter coefficients (cached per sample rate)
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
    
    // DC whine state
    private double _whinePhase = 0.0;
    private const float WhineFreq = 520f;    // typical avionics inverter whine (400–800 Hz)
    private const float WhineLevel = 0.003f; // extremely subtle, like cockpit background
    
    // Oxygen-mask style muffling
    private float _muffleLP;        // simple one-pole low-pass history
    private const float MuffleCutoff = 900f;   // muffled low-pass
    private float _muffleA;         // filter coefficient
    private float[] _muffleLPChannels;
    private float[] _muffleLP2Channels;
    
    /// <summary>
    /// Check if squelch is currently open (allowing audio through).
    /// Squelch is open only when we have BOTH:
    /// 1. Good RF signal strength (based on effectiveGain vs threshold)
    /// 2. Active audio data flow (based on SetStreamActive calls from RadioPlayback)
    /// 
    /// This matches real radio behavior - you need carrier AND modulation.
    /// </summary>
    public bool IsSquelchOpen
    {
        get
        {
            lock (_lock)
            {
                bool signalSquelchOpen = _squelchState == SquelchState.Open || _squelchState == SquelchState.Opening;
                
                // True squelch requires BOTH good signal AND active stream
                return signalSquelchOpen && _isStreamActive;
            }
        }
    }
    
    /// <summary>
    /// Get whether RF signal squelch is open (independent of stream activity).
    /// Useful for debugging or UI display.
    /// </summary>
    public bool IsSignalSquelchOpen
    {
        get
        {
            lock (_lock)
            {
                return _squelchState == SquelchState.Open || _squelchState == SquelchState.Opening;
            }
        }
    }
    
    /// <summary>
    /// Get current stream activity state
    /// </summary>
    public bool IsStreamActive
    {
        get
        {
            lock (_lock)
            {
                return _isStreamActive;
            }
        }
    }
    
    /// <summary>
    /// Notify RadioEffect whether the stream is actively receiving audio data.
    /// This is separate from RF signal strength - you can have good signal but no data flow.
    /// Automatically triggers squelch bursts when stream activity changes.
    /// 
    /// Call this from RadioPlayback based on:
    /// - HasReceivedAudio
    /// - LastAudioReceived timestamp
    /// - IsStopping flag
    /// </summary>
    public void SetStreamActive(bool isActive)
    {
        lock (_lock)
        {
            // Only trigger burst if state actually changed
            if (isActive != _isStreamActive)
            {
                bool wasActive = _isStreamActive;
                _isStreamActive = isActive;
                
                // Trigger burst for stream activity change
                // But only if RF signal squelch is also open (avoids double-burst during fades)
                bool signalSquelchOpen = _squelchState == SquelchState.Open || _squelchState == SquelchState.Opening;
                
                if (signalSquelchOpen)
                {
                    _squelchBurstSamplesLeft = SquelchBurstDuration;
                    Array.Clear(_squelchBurstFilterHistory, 0, _squelchBurstFilterHistory.Length);
                    _squelchBurstFilterIndex = 0;
                    Console.WriteLine($"[RadioEffect] Stream activity: {(wasActive ? "ACTIVE" : "INACTIVE")} → {(isActive ? "ACTIVE" : "INACTIVE")} (burst triggered)");
                }
                else
                {
                    Console.WriteLine($"[RadioEffect] Stream activity: {(wasActive ? "ACTIVE" : "INACTIVE")} → {(isActive ? "ACTIVE" : "INACTIVE")} (no burst - signal squelch closed)");
                }
            }
        }
    }
    
    /// <summary>
    /// Get the current squelch threshold value
    /// </summary>
    public float GetSquelchThreshold()
    {
        lock (_lock)
        {
            return _squelchThreshold;
        }
    }
    
    /// <summary>
    /// Set the squelch threshold. Values are clamped to 0.001-1.0 range.
    /// </summary>
    public void SetSquelchThreshold(float threshold)
    {
        lock (_lock)
        {
            float oldThreshold = _squelchThreshold;
            _squelchThreshold = Math.Clamp(threshold, 0.001f, 1.0f);
            
            // Re-evaluate squelch state with new threshold
            bool wasWeak = _prevEffectiveGain < oldThreshold;
            bool isWeak = _prevEffectiveGain < _squelchThreshold;
            
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
    
    // Squelch burst state (the characteristic "pop" when gate opens/closes)
    private int _squelchBurstSamplesLeft = 0;
    private const int SquelchBurstDuration = 720; // ~15ms at 48kHz (longer, softer)
    private const float SquelchBurstAmplitude = 0.12f; // Audible but not harsh (increased for filtered version)
    
    // Pre-calculated burst envelope
    private static readonly float[] _squelchBurstEnvelope = GenerateBurstEnvelope();
    
    /// <summary>
    /// Generate the squelch burst envelope once at startup.
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
    
    // Fast flutter state (rapid multipath fading, 20-80ms)
    private int _dropoutSamplesLeft = 0;
    private int _dropoutFadeSamples = 0;
    private int _dropoutInitialSamples = 0; // Track initial duration for envelope calculation
    private bool _dropoutIsMute = true; // true = mute, false = digital corruption
    
    // Deep fade state (slow severe fading, 400-2000ms, can trigger squelch)
    private int _deepFadeSamplesLeft = 0;
    private int _deepFadeFadeSamples = 0;
    private int _deepFadeInitialSamples = 0;
    
    // Track effective gain for squelch decisions (includes fade effects)
    private float _prevEffectiveGain = 1.0f;
    
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
                _params = value;
                // Note: Squelch decisions now based on effective gain (calculated in Process)
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
        
        // Get or calculate filter coefficients (cached per sample rate)
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
        _prevEffectiveGain = initial.Gain;
        
        // Precompute one-pole LPF coefficient for muffling (oxygen mask effect)
        // Lower cutoff = more muffled (typical military masks: 600-700 Hz)
        _muffleA = MathF.Exp(-2f * MathF.PI * MuffleCutoff / _sampleRate);
        _muffleLPChannels = new float[_channels];
        _muffleLP2Channels = new float[_channels];
    }
    
    /// <summary>
    /// Calculate digital brick-wall filter coefficients (300Hz - 2700Hz bandpass)
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
    /// Manually trigger a squelch burst (for external events like frequency changes).
    /// NOTE: Normal squelch transitions and stream activity changes trigger bursts automatically.
    /// This should rarely be needed.
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
            
            Console.Out.WriteLine($"Triggering Squelch Burst");
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

        // === FAST FLUTTER: Rapid multipath fading (20-80ms) ===
        // This is Poisson process for "picket-fencing" effect
        // DropoutProb is the rate (events per second)
        if (_dropoutSamplesLeft <= 0 && p.DropoutRate > 0.001f)
        {
            double bufferDurationSec = (double)frames / _sampleRate;
            double expectedEvents = p.DropoutRate * bufferDurationSec;
            double dropoutProbability = 1.0 - Math.Exp(-expectedEvents);
            
            if (rng.NextDouble() < dropoutProbability)
            {
                double u = rng.NextDouble();
                double meanDropMs = 80.0; // Average 80ms (fast flutter)
                double minDropMs = 20.0;  // Minimum 20ms
                double fadeMs = 8.0;      // Fast fade
                double durMs = Math.Max(minDropMs, -Math.Log(1.0 - u) * meanDropMs);
                _dropoutSamplesLeft = (int)(_sampleRate * durMs / 1000.0);
                _dropoutFadeSamples = (int)(_sampleRate * fadeMs / 1000.0);
                _dropoutInitialSamples = _dropoutSamplesLeft;
                _dropoutIsMute = true; // Analog FM fading
            }
        }

        // === DEEP FADES: Slow severe dropouts (400-2000ms) ===
        // Independent Poisson process for terrain nulls, severe multipath
        // These CAN drop signal below squelch threshold → trigger pops
        if (_deepFadeSamplesLeft <= 0 && p.DeepFadeRate > 0.001f)
        {
            double bufferDurationSec = (double)frames / _sampleRate;
            double expectedEvents = p.DeepFadeRate * bufferDurationSec;
            double fadeProbability = 1.0 - Math.Exp(-expectedEvents);
            
            if (rng.NextDouble() < fadeProbability)
            {
                double u = rng.NextDouble();
                double meanDropMs = 1200.0;  // Average 1.2 seconds (deep fade)
                double minDropMs = 400.0;    // Minimum 400ms
                double fadeMs = 50.0;        // Slower fade (50ms)
                double durMs = Math.Max(minDropMs, -Math.Log(1.0 - u) * meanDropMs);
                _deepFadeSamplesLeft = (int)(_sampleRate * durMs / 1000.0);
                _deepFadeFadeSamples = (int)(_sampleRate * fadeMs / 1000.0);
                _deepFadeInitialSamples = _deepFadeSamplesLeft;
            }
        }

        for (int frame = 0; frame < frames; frame++)
        {
            // === Calculate effective gain (includes fade effects) ===
            float effectiveGain = p.Gain;
            
            // Apply fast flutter envelope
            bool inDrop = _dropoutSamplesLeft > 0;
            if (inDrop)
            {
                int fadeIn = _dropoutFadeSamples;
                int fadeOut = _dropoutFadeSamples;
                int age = _dropoutInitialSamples - _dropoutSamplesLeft;
                
                float dropoutEnvelope;
                if (age < fadeIn)
                    dropoutEnvelope = 1f - (float)age / fadeIn;
                else if (_dropoutSamplesLeft < fadeOut)
                    dropoutEnvelope = (float)(fadeOut - _dropoutSamplesLeft) / fadeOut;
                else
                    dropoutEnvelope = 0f;
                
                effectiveGain *= dropoutEnvelope;
            }
            
            // Apply deep fade envelope
            bool inDeepFade = _deepFadeSamplesLeft > 0;
            if (inDeepFade)
            {
                int fadeIn = _deepFadeFadeSamples;
                int fadeOut = _deepFadeFadeSamples;
                int age = _deepFadeInitialSamples - _deepFadeSamplesLeft;
                
                float deepFadeEnvelope;
                if (age < fadeIn)
                    deepFadeEnvelope = 1f - (float)age / fadeIn;
                else if (_deepFadeSamplesLeft < fadeOut)
                    deepFadeEnvelope = (float)(fadeOut - _deepFadeSamplesLeft) / fadeOut;
                else
                    deepFadeEnvelope = 0f;
                
                effectiveGain *= deepFadeEnvelope;
            }
            
            // === Digital squelch gate (responds to effective gain) ===
            // Check if effective gain crossed squelch threshold
            bool wasWeak = _prevEffectiveGain < _squelchThreshold;
            bool isWeak = effectiveGain < _squelchThreshold;
            
            if (wasWeak && !isWeak)
            {
                // Effective gain came up - open squelch (deep fade ended)
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
                // Effective gain dropped - close squelch (deep fade started)
                _squelchState = SquelchState.Closing;
                _squelchTransitionSamples = 0;
                _squelchTransitionLength = SquelchReleaseSamples;
                
                // Trigger squelch burst (closing pop)
                _squelchBurstSamplesLeft = SquelchBurstDuration;
                Array.Clear(_squelchBurstFilterHistory, 0, _squelchBurstFilterHistory.Length);
                _squelchBurstFilterIndex = 0;
            }
            
            _prevEffectiveGain = effectiveGain;
            
            // Calculate squelch gate gain
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
            
            // Generate subtle DC whine (actually AC tone)
            double whineIncrement = 2.0 * Math.PI * WhineFreq / _sampleRate;
            float whineSample = (float)Math.Sin(_whinePhase) * WhineLevel;
            _whinePhase += whineIncrement;
            if (_whinePhase > Math.PI * 2) _whinePhase -= Math.PI * 2;

            for (int c = 0; c < _channels; c++)
            {
                int idx = offset + frame * _channels + c;
                float x = buffer[idx];

                // Apply squelch gate (clean digital, no burst)
                x *= squelchGain;

                // === RF Fading (both fast flutter and deep fades) ===
                // Apply fades to audio signal (these already affected effectiveGain for squelch)
                if (inDrop)
                {
                    int fadeIn = _dropoutFadeSamples;
                    int fadeOut = _dropoutFadeSamples;
                    int age = _dropoutInitialSamples - _dropoutSamplesLeft;
                    
                    float dropoutEnvelope;
                    if (age < fadeIn)
                        dropoutEnvelope = 1f - (float)age / fadeIn;
                    else if (_dropoutSamplesLeft < fadeOut)
                        dropoutEnvelope = (float)(fadeOut - _dropoutSamplesLeft) / fadeOut;
                    else
                        dropoutEnvelope = 0f;
                    
                    x *= dropoutEnvelope;
                }
                
                if (inDeepFade)
                {
                    int fadeIn = _deepFadeFadeSamples;
                    int fadeOut = _deepFadeFadeSamples;
                    int age = _deepFadeInitialSamples - _deepFadeSamplesLeft;
                    
                    float deepFadeEnvelope;
                    if (age < fadeIn)
                        deepFadeEnvelope = 1f - (float)age / fadeIn;
                    else if (_deepFadeSamplesLeft < fadeOut)
                        deepFadeEnvelope = (float)(fadeOut - _deepFadeSamplesLeft) / fadeOut;
                    else
                        deepFadeEnvelope = 0f;
                    
                    x *= deepFadeEnvelope;
                }
                
                
                // === Oxygen mask (two-pole strong LPF + nasal boost) ===
                float lp1 = _muffleLPChannels[c];
                lp1 = _muffleA * lp1 + (1f - _muffleA) * x;
                _muffleLPChannels[c] = lp1;

                float lp2 = _muffleLP2Channels[c];
                lp2 = _muffleA * lp2 + (1f - _muffleA) * lp1;
                _muffleLP2Channels[c] = lp2;

// Stronger nasal boost for helmet/mask resonance
                float nasal = x * 0.55f;

// Blend to final mask sound
                x = (lp2 * 0.75f) + (nasal * 0.25f);

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
                
                // Apply gain (using original p.Gain, not effectiveGain - fades already applied)
                float val = y * p.Gain;

                // === Noise ===
                // Apply physics-calculated noise when signal is transmitting
                // Noise is based on RF propagation conditions (SNR, distance, terrain)
                if (p.NoiseLevel > 0.001f && squelchGain > 0f)
                {
                    // Use pink-ish noise (more natural than pure white)
                    // Average multiple samples for spectral shaping
                    float noise1 = (float)(rng.NextDouble() * 2.0 - 1.0);
                    float noise2 = (float)(rng.NextDouble() * 2.0 - 1.0);
                    float noise3 = (float)(rng.NextDouble() * 2.0 - 1.0);
                    float noise4 = (float)(rng.NextDouble() * 2.0 - 1.0);
    
                    // 4-sample average creates ~6dB/octave rolloff (pink-ish)
                    float radioNoise = (noise1 + noise2 + noise3 + noise4) / 4.0f;
    
                    // Map physics noise level to audible amplitude
                    float noiseGain = MathF.Sqrt(p.NoiseLevel) * 0.3f;
    
                    val += radioNoise * noiseGain;
                }
                
                // Clamp to safe range
                val = Math.Clamp(val, -1f, 1f);
                
                // === Mix in squelch burst INDEPENDENTLY ===
                // This happens outside the signal chain so it's always audible
                // regardless of gain or squelch state
                val += squelchBurstSample;

                if (!inDrop && _squelchState != SquelchState.Closed)
                {
                    val += whineSample;
                } 
                
                buffer[idx] = Math.Clamp(val, -1f, 1f);
            }

            if (inDrop) _dropoutSamplesLeft--;
            if (inDeepFade) _deepFadeSamplesLeft--;
        }
    }
}