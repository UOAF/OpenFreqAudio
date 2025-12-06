using System;
using BMSAudioSim.Models;
using ManagedBass;

namespace BMSAudioSim;

/// <summary>
/// Sample-based stepped-on interference mixer.
/// Instead of synthesizing the physics, plays a recorded stepped-on sample
/// with variations based on radio frequency, signal strength, and interference level.
/// </summary>
public class Radiomixer
{
    // Pre-loaded stepped-on interference sample
    private static float[]? _steppedOnSample = null;
    private static int _sampleRate = 44100;
    
    // Playback state (use float for smooth playback)
    private float _playbackPosition = 0;
    private bool _isPlaying = false;
    private float _playbackSpeed = 1.0f;
    
    // Modulation oscillators for variation
    private double _pitchModPhase = 0;
    private double _ampModPhase = 0;
    private readonly Random _rng = new Random();
    
    /// <summary>
    /// Load the stepped-on interference sample from file using BASS.
    /// Call this once at startup. Supports any format BASS supports (WAV, OGG, MP3, etc).
    /// </summary>
    public static void LoadSteppedOnSample(string filePath)
    {
        Console.WriteLine($"[RadioMixer] Loading stepped-on sample from: {filePath}");
        
        // Create a decode stream (no playback, just for reading data)
        int stream = Bass.CreateStream(filePath, 0, 0, BassFlags.Decode | BassFlags.Float);
        
        if (stream == 0)
        {
            var error = Bass.LastError;
            throw new Exception($"Failed to load stepped-on sample: {error}");
        }
        
        try
        {
            // Get stream info
            var info = Bass.ChannelGetInfo(stream);
            _sampleRate = info.Frequency;
            int channels = info.Channels;
            
            // Get length in bytes
            long lengthBytes = Bass.ChannelGetLength(stream);
            double lengthSamples = Bass.ChannelBytes2Seconds(stream, lengthBytes) * _sampleRate;
            
            // Read all samples
            float[] buffer = new float[(int)(lengthSamples * channels)];
            int bytesRead = Bass.ChannelGetData(stream, buffer, (int)(lengthSamples * channels * sizeof(float)));
            int samplesRead = bytesRead / sizeof(float);
            
            // Convert to mono if needed
            if (channels == 1)
            {
                _steppedOnSample = new float[samplesRead];
                Array.Copy(buffer, _steppedOnSample, samplesRead);
            }
            else
            {
                // Average channels to mono
                _steppedOnSample = new float[samplesRead / channels];
                for (int i = 0; i < _steppedOnSample.Length; i++)
                {
                    float sum = 0;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        sum += buffer[i * channels + ch];
                    }
                    _steppedOnSample[i] = sum / channels;
                }
            }
            
            Console.WriteLine($"[RadioMixer] Loaded stepped-on sample:");
            Console.WriteLine($"  Sample rate: {_sampleRate} Hz");
            Console.WriteLine($"  Channels: {channels} -> 1 (mono)");
            Console.WriteLine($"  Samples: {_steppedOnSample.Length}");
            Console.WriteLine($"  Duration: {_steppedOnSample.Length / (float)_sampleRate:F2} seconds");
        }
        finally
        {
            // Free the stream
            Bass.StreamFree(stream);
        }
    }
    
    /// <summary>
    /// Calculate physics-based stepped-on parameters.
    /// </summary>
    public static SteppedOnParams CalculateSteppedOnParams(
        AudioParams tx1,
        AudioParams tx2,
        float distance1_km,
        float distance2_km,
        float snr1_dB,
        float snr2_dB)
    {
        var result = new SteppedOnParams();
        
        // === POWER RELATIONSHIP ===
        float gain1_dB = 20 * MathF.Log10(Math.Max(tx1.Gain, 1e-6f));
        float gain2_dB = 20 * MathF.Log10(Math.Max(tx2.Gain, 1e-6f));
        result.PowerDiff_dBm = gain1_dB - gain2_dB;
        
        // === FM CAPTURE RATIO ===
        float absDiff = MathF.Abs(result.PowerDiff_dBm);
        
        if (absDiff >= 8.0f)
        {
            result.CaptureRatio = result.PowerDiff_dBm > 0 ? 1.0f : 0.0f;
            result.InterferenceLevel = 0.0f;
            return result;
        }
        else if (absDiff >= 6.0f)
        {
            float t = (absDiff - 6.0f) / 2.0f;
            result.CaptureRatio = result.PowerDiff_dBm > 0 ? 
                0.92f + t * 0.08f : 0.08f - t * 0.08f;
            result.InterferenceLevel = 0.12f * (1 - t);
        }
        else if (absDiff >= 3.0f)
        {
            float t = (absDiff - 3.0f) / 3.0f;
            result.CaptureRatio = result.PowerDiff_dBm > 0 ?
                0.70f + t * 0.22f : 0.30f - t * 0.22f;
            result.InterferenceLevel = 0.30f + (1 - t) * 0.25f;
        }
        else if (absDiff >= 1.5f)
        {
            float t = (absDiff - 1.5f) / 1.5f;
            result.CaptureRatio = result.PowerDiff_dBm > 0 ?
                0.58f + t * 0.12f : 0.42f - t * 0.12f;
            result.InterferenceLevel = 0.55f + (1 - t) * 0.20f;
        }
        else
        {
            result.CaptureRatio = 0.5f + result.PowerDiff_dBm / 3.0f;
            result.InterferenceLevel = 0.75f + (1.5f - absDiff) / 1.5f * 0.20f;
        }
        
        // === RADIO FREQUENCY ===
        float avgFreqMHz = (tx1.RadioFrequencyMHz + tx2.RadioFrequencyMHz) / 2.0f;
        result.IsVHF = avgFreqMHz < 200.0f;
        
        if (result.IsVHF)
            result.InterferenceLevel *= 0.85f;
        
        // === CALCULATE VARIATIONS FOR SAMPLE PLAYBACK ===
        
        // Beat frequency influences playback speed
        // Reference sample is UHF at ~387 Hz beat tone
        // Scale based on actual radio frequency to create variation
        
        // VHF (30-174 MHz): Lower beat frequencies (150-300 Hz typical)
        // UHF (225-512 MHz): Higher beat frequencies (250-450 Hz typical)
        
        float referenceBeatHz = 387.0f; // Our reference sample
        float referenceFreqMHz = 300.0f; // Assume reference is 300 MHz UHF
        
        // Scale beat frequency proportionally to radio frequency
        // This creates natural pitch variation across the frequency spectrum
        float beatHz = referenceBeatHz * (avgFreqMHz / referenceFreqMHz);
        
        // Clamp to realistic ranges
        if (result.IsVHF)
            beatHz = Math.Clamp(beatHz, 150.0f, 300.0f);
        else
            beatHz = Math.Clamp(beatHz, 250.0f, 450.0f);
        
        result.BeatFrequency_Hz = beatHz;
        
        // Frequency instability influences pitch modulation depth
        float avgSNR = (snr1_dB + snr2_dB) / 2.0f;
        float snrFactor = MathF.Max(0, (10.0f - avgSNR) / 20.0f);
        result.FreqInstability_Hz = 30.0f + snrFactor * 40.0f;
        
        // Amplitude variations
        float equalityFactor = 1.0f - MathF.Abs(result.CaptureRatio - 0.5f) * 2.0f;
        result.FastFadingRate_Hz = 45.0f + equalityFactor * 15.0f;
        
        // === SIGNAL QUALITY ===
        result.SignalQuality = Math.Clamp((avgSNR + 10.0f) / 30.0f, 0.0f, 1.0f);
        result.SignalQuality *= (1.0f - result.InterferenceLevel * 0.6f);
        
        return result;
    }
    
    /// <summary>
    /// Process stepped-on interference by playing the reference sample with variations.
    /// 
    /// APPROACH:
    /// 1. Play the pre-recorded stepped-on sample (your reference audio)
    /// 2. Vary playback speed based on radio frequency (pitch shift)
    /// 3. Vary amplitude based on signal strength and interference level
    /// 4. Add subtle pitch/amplitude modulation for variation
    /// 5. Mix with suppressed audio from both transmitters
    /// </summary>
    public void ProcessSteppedOn(
        float[] buffer1,
        float[] buffer2,
        float[] output,
        SteppedOnParams stepped,
        int sampleRate,
        float gain1 = 1.0f,
        float gain2 = 1.0f)
    {
        // Validate buffer sizes to prevent IndexOutOfRangeException
        // This can happen during channel tuning/untuning when buffers are being reallocated
        int minLength = Math.Min(Math.Min(buffer1.Length, buffer2.Length), output.Length);
        if (minLength == 0)
        {
            Array.Clear(output);
            return;
        }
        
        // Limit processing to the smallest buffer size
        int processLength = minLength;
        
        const float SQUELCH_THRESHOLD = 0.03f;

        // No signals → silence
        if (gain1 < SQUELCH_THRESHOLD && gain2 < SQUELCH_THRESHOLD)
        {
            Array.Clear(output);
            _isPlaying = false;
            return;
        }

        // Only one signal → no interference
        if (gain1 < SQUELCH_THRESHOLD)
        {
            Array.Copy(buffer2, output, processLength);
            _isPlaying = false;
            return;
        }
        if (gain2 < SQUELCH_THRESHOLD)
        {
            Array.Copy(buffer1, output, processLength);
            _isPlaying = false;
            return;
        }

        // Clean capture → pass through stronger signal
        if (stepped.InterferenceLevel < 0.05f)
        {
            // Use the stronger signal
            if (gain1 >= gain2)
                Array.Copy(buffer1, output, processLength);
            else
                Array.Copy(buffer2, output, processLength);
            _isPlaying = false;
            return;
        }
        
        // Check if sample is loaded
        if (_steppedOnSample == null || _steppedOnSample.Length == 0)
        {
            // Fallback: simple mix if no sample loaded
            for (int i = 0; i < processLength; i++)
            {
                output[i] = buffer1[i] * stepped.CaptureRatio + 
                           buffer2[i] * (1.0f - stepped.CaptureRatio);
            }
            return;
        }
        
        // === DETERMINE WHICH SIGNAL IS STRONGER ===
        // The capture ratio is calculated assuming buffer1 is stronger (positive PowerDiff)
        // If buffer2 is actually stronger, we need to swap and invert the ratio
        
        float strongerGain, weakerGain;
        float[] strongerBuffer, weakerBuffer;
        float actualCaptureRatio;
        
        if (gain1 >= gain2)
        {
            // Buffer1 is stronger - use capture ratio as-is
            strongerBuffer = buffer1;
            weakerBuffer = buffer2;
            strongerGain = gain1;
            weakerGain = gain2;
            actualCaptureRatio = stepped.CaptureRatio;
        }
        else
        {
            // Buffer2 is stronger - swap and invert capture ratio
            strongerBuffer = buffer2;
            weakerBuffer = buffer1;
            strongerGain = gain2;
            weakerGain = gain1;
            actualCaptureRatio = 1.0f - stepped.CaptureRatio;
        }
        
        // Start playback if not already playing
        if (!_isPlaying)
        {
            _playbackPosition = 0;
            _isPlaying = true;
        }
        
        // Update playback speed based on current radio frequency
        // (recalculate every time to respond to frequency changes)
        float referenceFreq = 387.0f;
        _playbackSpeed = stepped.BeatFrequency_Hz / referenceFreq;
        _playbackSpeed = Math.Clamp(_playbackSpeed, 0.7f, 1.3f);
        
        double dt = 1.0 / sampleRate;
        
        // === PARAMETERS ===
        float overallSignal = Math.Min(strongerGain, weakerGain);
        
        // Audio suppression
        float audioSuppression;
        if (stepped.InterferenceLevel < 0.3f)
            audioSuppression = 0.25f;
        else if (stepped.InterferenceLevel < 0.6f)
            audioSuppression = 0.08f;
        else
            audioSuppression = 0.04f;
        
        // Stepped-on sample amplitude
        float steppedOnAmplitude = 0.9f * overallSignal * Math.Min(1.0f, stepped.InterferenceLevel * 1.5f);
        
        // Modulation rates
        float pitchModRate = 5.0f; // Hz - subtle pitch wobble
        float pitchModDepth = stepped.FreqInstability_Hz / 400.0f; // 0-0.2 range
        
        float ampModRate = stepped.FastFadingRate_Hz; // ~50 Hz
        float ampModDepth = 0.15f * stepped.InterferenceLevel;
        
        // Use floating-point position for smooth playback
        float playbackPos = _playbackPosition;
        
        for (int i = 0; i < processLength; i++)
        {
            // === SUPPRESSED AUDIO ===
            // Mix stronger and weaker signals according to capture effect
            float mixed = strongerBuffer[i] * actualCaptureRatio + 
                          weakerBuffer[i] * (1.0f - actualCaptureRatio);
            float suppressedAudio = mixed * audioSuppression * overallSignal;
            
            // === STEPPED-ON SAMPLE PLAYBACK ===
            
            // Pitch modulation (makes it warble slightly)
            _pitchModPhase += 2 * Math.PI * pitchModRate * dt;
            if (_pitchModPhase > Math.PI * 2) _pitchModPhase -= Math.PI * 2;
            float pitchMod = (float)Math.Sin(_pitchModPhase) * pitchModDepth;
            
            // Instantaneous playback speed (adjusted for sample rate difference)
            float sampleRateRatio = (float)_sampleRate / sampleRate;
            float instantSpeed = _playbackSpeed * (1.0f + pitchMod) * sampleRateRatio;
            
            // Read sample with linear interpolation
            int sampleIndex = (int)playbackPos;
            float frac = playbackPos - sampleIndex;
            
            // Loop the sample
            sampleIndex = sampleIndex % _steppedOnSample.Length;
            int nextIndex = (sampleIndex + 1) % _steppedOnSample.Length;
            
            // Linear interpolation
            float sampleValue = _steppedOnSample[sampleIndex] * (1.0f - frac) + 
                               _steppedOnSample[nextIndex] * frac;
            
            // Amplitude modulation (makes it beat/pulse)
            _ampModPhase += 2 * Math.PI * ampModRate * dt;
            if (_ampModPhase > Math.PI * 2) _ampModPhase -= Math.PI * 2;
            float ampMod = (float)Math.Sin(_ampModPhase);
            
            float amplitude = steppedOnAmplitude * (1.0f - ampModDepth + ampModDepth * (ampMod * 0.5f + 0.5f));
            
            // Apply amplitude
            float steppedOnSound = sampleValue * amplitude;
            
            // === COMBINE ===
            float combined = suppressedAudio + steppedOnSound;
            
            output[i] = Math.Clamp(combined, -1.0f, 1.0f);
            
            // Advance playback position
            playbackPos += instantSpeed;
            if (playbackPos >= _steppedOnSample.Length)
                playbackPos -= _steppedOnSample.Length;
        }
        
        // Store position for next call
        _playbackPosition = playbackPos;
        
        // Clear any remaining samples in output if we didn't fill the entire buffer
        if (processLength < output.Length)
        {
            Array.Clear(output, processLength, output.Length - processLength);
        }
    }
    
    /// <summary>
    /// Reset playback state.
    /// </summary>
    public void Reset()
    {
        _playbackPosition = 0;
        _isPlaying = false;
        _pitchModPhase = 0;
        _ampModPhase = 0;
    }
}