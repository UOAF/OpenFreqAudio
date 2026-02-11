using System;
using System.IO;
using System.Reflection;
using ManagedBass;
using Microsoft.Extensions.Logging;
using OpenFreqAudio.Models;

// ReSharper disable InconsistentNaming

namespace OpenFreqAudio;

/// <summary>
/// Sample-based stepped-on interference mixer.
/// Instead of synthesizing the physics, plays a recorded stepped-on sample
/// with variations based on radio frequency, signal strength, and interference level.
/// </summary>
public class Radiomixer
{
    // Pre-loaded stepped-on interference sample
    private static float[]? _steppedOnSample;
    private static int _sampleRate = 44100;

    // Playback state (use float for smooth playback)
    private float _playbackPosition;
    private bool _isPlaying;
    private float _playbackSpeed = 1.0f;

    // Modulation oscillators for variation
    private double _pitchModPhase;
    private double _ampModPhase;

    private static byte[] ReadStreamToByteArray(Stream stream)
    {
        using (MemoryStream memoryStream = new MemoryStream())
        {
            stream.CopyTo(memoryStream);
            return memoryStream.ToArray();
        }
    }

    /// <summary>
    /// Load the stepped-on interference sample from file using BASS.
    /// Call this once at startup. Supports any format BASS supports (WAV, OGG, MP3, etc).
    /// </summary>
    public static void LoadSteppedOnSample()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string resourceName = "OpenFreqAudio.Assets.stepped-on.ogg";
        byte[] audioData;

        // Access the embedded resource as a stream
        using (var resourceStream = assembly.GetManifestResourceStream(resourceName))
        {
            if (resourceStream != null)
            {
                audioData = ReadStreamToByteArray(resourceStream);
            }
            else
            {
                throw new Exception("Resource not found: " + resourceName);
            }
        }

        // Create a decode stream (no playback, just for reading data)
        int stream = Bass.CreateStream(audioData, 0, audioData.Length, BassFlags.Decode | BassFlags.Float);

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
        }
        finally
        {
            // Free the stream
            Bass.StreamFree(stream);
        }
    }

    public enum ModulationType
    {
        AM,
        FM
    }

    public static SteppedOnParams CalculateSteppedOnParams(
        AudioParams tx1,
        AudioParams tx2,
        ModulationType modType = ModulationType.AM) // Add modulation type
    {
        var result = new SteppedOnParams();

        // === POWER RELATIONSHIP ===
        float gain1_dB = 20 * MathF.Log10(Math.Max(tx1.Gain, 1e-6f));
        float gain2_dB = 20 * MathF.Log10(Math.Max(tx2.Gain, 1e-6f));
        result.PowerDiff_dBm = gain1_dB - gain2_dB;

        float absDiff = MathF.Abs(result.PowerDiff_dBm);

        if (modType == ModulationType.FM)
        {
            // === FM CAPTURE RATIO (original logic) ===
            if (absDiff >= 8.0f)
            {
                result.CaptureRatio = result.PowerDiff_dBm > 0 ? 1.0f : 0.0f;
                result.InterferenceLevel = 0.0f;
                return result;
            }

            if (absDiff >= 6.0f)
            {
                float t = (absDiff - 6.0f) / 2.0f;
                result.CaptureRatio = result.PowerDiff_dBm > 0 ? 0.92f + t * 0.08f : 0.08f - t * 0.08f;
                result.InterferenceLevel = 0.12f * (1 - t);
            }
            else if (absDiff >= 3.0f)
            {
                float t = (absDiff - 3.0f) / 3.0f;
                result.CaptureRatio = result.PowerDiff_dBm > 0 ? 0.70f + t * 0.22f : 0.30f - t * 0.22f;
                result.InterferenceLevel = 0.30f + (1 - t) * 0.25f;
            }
            else if (absDiff >= 1.5f)
            {
                float t = (absDiff - 1.5f) / 1.5f;
                result.CaptureRatio = result.PowerDiff_dBm > 0 ? 0.58f + t * 0.12f : 0.42f - t * 0.12f;
                result.InterferenceLevel = 0.55f + (1 - t) * 0.20f;
            }
            else
            {
                result.CaptureRatio = 0.5f + result.PowerDiff_dBm / 3.0f;
                result.InterferenceLevel = 0.75f + (1.5f - absDiff) / 1.5f * 0.20f;
            }
        }
        else // AM modulation
        {
            // === AM LINEAR MIXING ===
            // AM doesn't have capture effect - signals mix more linearly

            if (absDiff >= 30.0f)
            {
                // Very large difference: almost complete dominance
                result.CaptureRatio = result.PowerDiff_dBm > 0 ? 0.98f : 0.02f;
                result.InterferenceLevel = 0.05f;
            }
            else if (absDiff >= 20.0f)
            {
                // Strong dominance but still audible interference
                float t = (absDiff - 20.0f) / 10.0f;
                result.CaptureRatio = result.PowerDiff_dBm > 0 ? 0.90f + t * 0.08f : 0.10f - t * 0.08f;
                result.InterferenceLevel = 0.15f - t * 0.10f;
            }
            else if (absDiff >= 10.0f)
            {
                // Clear dominance with noticeable stepped-on effect
                float t = (absDiff - 10.0f) / 10.0f;
                result.CaptureRatio = result.PowerDiff_dBm > 0 ? 0.75f + t * 0.15f : 0.25f - t * 0.15f;
                result.InterferenceLevel = 0.40f - t * 0.25f;
            }
            else if (absDiff >= 6.0f)
            {
                // Moderate dominance, strong interference
                float t = (absDiff - 6.0f) / 4.0f;
                result.CaptureRatio = result.PowerDiff_dBm > 0 ? 0.65f + t * 0.10f : 0.35f - t * 0.10f;
                result.InterferenceLevel = 0.60f - t * 0.20f;
            }
            else if (absDiff >= 3.0f)
            {
                // Slight dominance, heavy garbling
                float t = (absDiff - 3.0f) / 3.0f;
                result.CaptureRatio = result.PowerDiff_dBm > 0 ? 0.58f + t * 0.07f : 0.42f - t * 0.07f;
                result.InterferenceLevel = 0.75f - t * 0.15f;
            }
            else
            {
                // Nearly equal power: maximum garbling
                result.CaptureRatio = 0.5f + result.PowerDiff_dBm / 6.0f; // More gradual than FM
                result.InterferenceLevel = 0.85f + (3.0f - absDiff) / 3.0f * 0.10f; // Peak at 0.95
            }
        }

        // === RADIO FREQUENCY ===
        int avgFreqKHz = (tx1.RadioFrequencyKHz + tx2.RadioFrequencyKHz) / 2;
        result.IsVHF = avgFreqKHz < 200000; // 200 MHz = 200000 kHz

        // For AM: VHF might have MORE interference due to atmospheric noise
        // (opposite of FM assumption)
        if (modType == ModulationType.AM && result.IsVHF)
            result.InterferenceLevel *= 1.08f; // Slight increase for VHF AM
        else if (modType == ModulationType.FM && result.IsVHF)
            result.InterferenceLevel *= 0.85f; // Original FM behavior

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
        float beatHz = referenceBeatHz * ((avgFreqKHz / 1000.0f) / referenceFreqMHz) * 0.6f; // Convert kHz to MHz

        // Clamp to realistic ranges
        if (result.IsVHF)
            beatHz = Math.Clamp(beatHz, 150.0f, 250.0f);
        else
            beatHz = Math.Clamp(beatHz, 180.0f, 320.0f);

        result.BeatFrequency_Hz = beatHz;

        // Frequency instability influences pitch modulation depth
        float avgSNR = (tx1.SNR_dB + tx2.SNR_dB) / 2.0f;
        float snrFactor = MathF.Max(0, (10.0f - avgSNR) / 20.0f);
        result.FreqInstability_Hz = 30.0f + snrFactor * 40.0f;

        // Amplitude variations
        float equalityFactor = 1.0f - MathF.Abs(result.CaptureRatio - 0.5f) * 2.0f;
        result.FastFadingRate_Hz = 45.0f + equalityFactor * 15.0f;

        return result;
    }

    /// <summary>
    /// Process stepped-on interference by playing the reference sample with variations.
    /// 
    /// APPROACH:
    /// 1. Play the pre-recorded stepped-on sample
    /// 2. Vary playback speed based on radio frequency (pitch shift)
    /// 3. Vary amplitude based on signal strength and interference level
    /// 4. Add subtle pitch/amplitude modulation for variation
    /// 5. Mix with suppressed audio from both transmitters
    /// </summary>
    public void ProcessSteppedOn(float[] buffer1, float[] buffer2, float[] output, int length, SteppedOnParams stepped,
        int sampleRate, float squelchThreshold, float gain1, float gain2)
    {
        // No signals → silence
        if (gain1 < squelchThreshold && gain2 < squelchThreshold)
        {
            Array.Clear(output);
            _isPlaying = false;
            return;
        }

        // Only one signal → no interference
        if (gain1 < squelchThreshold)
        {
            int copyLength = Math.Min(buffer2.Length, output.Length);
            Array.Copy(buffer2, output, copyLength);
            _isPlaying = false;
            return;
        }

        if (gain2 < squelchThreshold)
        {
            int copyLength = Math.Min(buffer1.Length, output.Length);
            Array.Copy(buffer1, output, copyLength);
            _isPlaying = false;
            return;
        }

        // Clean capture → pass through stronger signal
        if (stepped.InterferenceLevel < 0.05f)
        {
            // Use the stronger signal
            if (gain1 >= gain2)
            {
                int copyLength = Math.Min(buffer1.Length, output.Length);
                Array.Copy(buffer1, output, copyLength);
            }
            else
            {
                int copyLength = Math.Min(buffer2.Length, output.Length);
                Array.Copy(buffer2, output, copyLength);
            }

            _isPlaying = false;
            return;
        }

        // Cache the sample reference to prevent race conditions
        // (_steppedOnSample is static and could be replaced during processing)
        float[]? sample = _steppedOnSample;
        int sampleLength = sample?.Length ?? 0;

        // Check if sample is loaded
        if (sample == null || sampleLength == 0)
        {
            // Fallback: simple mix if no sample loaded
            int mixLength = Math.Min(Math.Min(buffer1.Length, buffer2.Length), output.Length);
            for (int i = 0; i < mixLength; i++)
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

        // Validate and clamp playback position
        if (!float.IsFinite(_playbackPosition) || _playbackPosition < 0 || _playbackPosition >= sampleLength)
        {
            _playbackPosition = 0;
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

        for (int i = 0; i < length; i++)
        {
            // === SUPPRESSED AUDIO ===
            // Mix stronger and weaker signals according to capture effect
            // Bounds-check buffer accesses (they might be shorter than output during reallocation)
            float mixed;
            if (i < strongerBuffer.Length && i < weakerBuffer.Length)
            {
                mixed = strongerBuffer[i] * actualCaptureRatio +
                        weakerBuffer[i] * (1.0f - actualCaptureRatio);
            }
            else
            {
                mixed = 0; // Safety fallback if buffers are too short
            }

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

            // Ensure indices are in valid range
            // The modulo handles wrapping at boundaries
            sampleIndex = sampleIndex % sampleLength;

            // Handle the rare case where modulo returns negative (shouldn't happen with our wrapping, but defensive)
            if (sampleIndex < 0)
                sampleIndex += sampleLength;

            int nextIndex = (sampleIndex + 1) % sampleLength;

            // Linear interpolation
            float sampleValue = sample[sampleIndex] * (1.0f - frac) +
                                sample[nextIndex] * frac;

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

            // Wrap position using simple modulo-based approach
            // This maintains the original audio characteristics
            if (playbackPos >= sampleLength)
                playbackPos -= sampleLength;
            else if (playbackPos < 0)
                playbackPos += sampleLength;
        }

        // Store position for next call
        _playbackPosition = playbackPos;
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