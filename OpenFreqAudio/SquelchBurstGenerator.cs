using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace OpenFreqAudio;

/// <summary>
/// Generates squelch burst sounds.
/// </summary>
public class SquelchBurstGenerator
{
    private readonly ILogger _logger;
    private readonly int _sampleRate;
    private readonly int _channels;
    
    // TUNING PARAMETERS - adjust these to change squelch burst characteristics
    // Opening burst (the "click" sound when squelch opens - carrier detected)
    private const int OpenBurstDuration = 5760;
    private const float OpenBurstAmplitude = 0.3f;
    
    // Closing burst (the "ksssh" sound when squelch closes - carrier lost)  
    private const int CloseBurstDuration = 9000;
    private const float CloseBurstAmplitude = 0.10f;
    
    // Pre-calculated burst envelopes
    private static readonly float[] OpenBurstEnvelope = GenerateOpenBurstEnvelope();
    private static readonly float[] CloseBurstEnvelope = GenerateCloseBurstEnvelope();
    
    // Burst state
    private int _samplesLeft;
    private bool _isOpening;
    private readonly float[] _filterHistory = new float[8]; // 8-tap moving average (4-tap for opening, 8-tap for closing)
    private int _filterIndex;
    
    private static readonly ThreadLocal<Random> ThreadRng =
        new(() => new Random(Environment.TickCount * Thread.CurrentThread.ManagedThreadId));
    
    public SquelchBurstGenerator(int sampleRate, int channels, ILogger logger)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _logger = logger;
    }
    
    /// <summary>
    /// Check if a burst is currently playing
    /// </summary>
    public bool IsPlaying => _samplesLeft > 0;
    
    /// <summary>
    /// Trigger opening burst ("click" sound)
    /// </summary>
    public void TriggerOpening()
    {
        if (_samplesLeft > 0) return; // Don't interrupt existing burst
        
        _isOpening = true;
        _samplesLeft = OpenBurstDuration;
        Array.Clear(_filterHistory, 0, _filterHistory.Length);
        _filterIndex = 0;
        
        _logger.LogDebug("Opening burst triggered (click)");
    }
    
    /// <summary>
    /// Trigger closing burst ("ksssh" sound)
    /// </summary>
    public void TriggerClosing()
    {
        if (_samplesLeft > 0) return; // Don't interrupt existing burst
        
        _isOpening = false;
        _samplesLeft = CloseBurstDuration;
        Array.Clear(_filterHistory, 0, _filterHistory.Length);
        _filterIndex = 0;
        
        _logger.LogDebug("Closing burst triggered (ksssh)");
    }
    
    /// <summary>
    /// Process burst into the provided buffer (interleaved samples).
    /// Call this each DSP iteration. Burst is ADDED to existing buffer content.
    /// </summary>
    /// <param name="buffer">Interleaved audio buffer (modified in place)</param>
    /// <param name="offset">Starting offset in buffer</param>
    /// <param name="samples">Number of samples to process (total, not per channel)</param>
    public void Process(float[] buffer, int offset, int samples)
    {
        if (_samplesLeft <= 0) return; // No burst playing
        
        var rng = ThreadRng.Value!;
        int frames = samples / _channels;
        
        // Get burst parameters based on type
        float[] envelope = _isOpening ? OpenBurstEnvelope : CloseBurstEnvelope;
        float amplitude = _isOpening ? OpenBurstAmplitude : CloseBurstAmplitude;
        int duration = _isOpening ? OpenBurstDuration : CloseBurstDuration;
        
        for (int frame = 0; frame < frames && _samplesLeft > 0; frame++)
        {
            int age = duration - _samplesLeft;
            float envelopeValue = envelope[age];
            
            float burstSample;
            
            if (_isOpening)
            {
                // Opening burst (click): Moderate filtering for mechanical "tch" character
                // Mid-frequency emphasis - not harsh highs, not low rumble
                float noise1 = (float)(rng.NextDouble() * 2.0 - 1.0);
                float noise2 = (float)(rng.NextDouble() * 2.0 - 1.0);
                float noise3 = (float)(rng.NextDouble() * 2.0 - 1.0);
                
                // Pre-average to remove harsh highs
                float rawNoise = (noise1 + noise2 + noise3) / 3.0f;
                
                // Use 4-tap filter for mechanical click character (mid-range)
                _filterHistory[_filterIndex] = rawNoise;
                _filterIndex = (_filterIndex + 1) % 8;
                
                float filteredNoise = 0f;
                for (int i = 0; i < 4; i++)
                    filteredNoise += _filterHistory[(_filterIndex - 4 + i + 8) % 8];
                filteredNoise /= 4.0f;
                
                burstSample = filteredNoise * envelopeValue * amplitude;
            }
            else
            {
                // Closing burst (ksssh): Heavy filtering for gritty, low-mid character
                // Use 4-sample pre-average + 8-tap filter for maximum low-frequency emphasis
                float noise1 = (float)(rng.NextDouble() * 2.0 - 1.0);
                float noise2 = (float)(rng.NextDouble() * 2.0 - 1.0);
                float noise3 = (float)(rng.NextDouble() * 2.0 - 1.0);
                float noise4 = (float)(rng.NextDouble() * 2.0 - 1.0);
                float rawNoise = (noise1 + noise2 + noise3 + noise4) / 4.0f;
                
                // Apply 8-tap moving average for heavy low-pass (gritty, rumbling character)
                _filterHistory[_filterIndex] = rawNoise;
                _filterIndex = (_filterIndex + 1) % 8;
                
                float filteredNoise = 0f;
                for (int i = 0; i < 8; i++) // Use all 8 taps
                    filteredNoise += _filterHistory[i];
                filteredNoise /= 8.0f;
                
                burstSample = filteredNoise * envelopeValue * amplitude;
            }
            
            // Add burst to all channels
            for (int c = 0; c < _channels; c++)
            {
                int idx = offset + frame * _channels + c;
                buffer[idx] = burstSample;
                buffer[idx] = Math.Clamp(buffer[idx], -1f, 1f);
            }
            
            _samplesLeft--;
        }
    }
    
    /// <summary>
    /// Generate the squelch OPENING burst envelope (brief "click" when carrier detected).
    /// </summary>
    private static float[] GenerateOpenBurstEnvelope()
    {
        float[] envelope = new float[OpenBurstDuration];
    
        // Make attack duration proportional to total duration
        int attackSamples = Math.Max(24, OpenBurstDuration / 10); // At least 24 samples, or 10% of duration

        for (int age = 0; age < OpenBurstDuration; age++)
        {
            if (age < attackSamples)
            {
                float attackProgress = (float)age / attackSamples;
                envelope[age] = attackProgress;
            }
            else
            {
                float decayProgress = (float)(age - attackSamples) / (OpenBurstDuration - attackSamples);
                envelope[age] = MathF.Exp(-6.0f * decayProgress);
            }
        }

        return envelope;
    }

    /// <summary>
    /// Generate the squelch CLOSING burst envelope (longer "ksssh" when carrier lost).
    /// No fade - constant amplitude
    /// </summary>
    private static float[] GenerateCloseBurstEnvelope()
    {
        float[] envelope = new float[CloseBurstDuration];

        for (int age = 0; age < CloseBurstDuration; age++)
        {
            envelope[age] = 1.0f; // Constant amplitude, no fade
        }

        return envelope;
    }
}