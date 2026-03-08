namespace OpenFreqAudio;

public static class AudioUtil
{
    /// <summary>
    /// Applies compression and normalization to audio samples
    /// </summary>
    public static void CompressAudio(byte[] audioData, float targetLevel = 0.5f, float ratio = 3.0f, float threshold = 0.3f)
    {
        if (audioData.Length % 2 != 0)
            return;
    
        var sampleCount = audioData.Length / 2;
    
        // Convert to float samples
        var samples = new float[sampleCount];
        var peak = 0f;
    
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = (short)(audioData[i * 2] | (audioData[i * 2 + 1] << 8));
            samples[i] = sample / 32768f;
            peak = Math.Max(peak, Math.Abs(samples[i]));
        }
    
        // Calculate gain needed to reach target level
        var gain = peak > 0.001f ? targetLevel / peak : 1.0f;
    
        // Limit gain to avoid amplifying noise too much (max +12dB)
        gain = Math.Min(gain, 4.0f);
    
        // Apply compression and convert back
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = samples[i] * gain;
        
            // Apply soft-knee compression above threshold
            var absSample = Math.Abs(sample);
            if (absSample > threshold)
            {
                var excess = absSample - threshold;
                var compressed = threshold + excess / ratio;
                sample = Math.Sign(sample) * compressed;
            }
        
            // Clamp to valid range
            sample = Math.Max(-1.0f, Math.Min(1.0f, sample));
        
            // Convert back to 16-bit
            var outputSample = (short)(sample * 32767f);
            audioData[i * 2] = (byte)(outputSample & 0xFF);
            audioData[i * 2 + 1] = (byte)((outputSample >> 8) & 0xFF);
        }
    }
    
    /// <summary>
    /// Applies compression and normalization to audio samples
    /// </summary>
    public static void CompressAudio(float[] samples, float targetLevel = 0.5f, float ratio = 3.0f, float threshold = 0.3f)
    {
        if (samples.Length == 0)
            return;
    
        // Find peak level
        float peak = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            peak = Math.Max(peak, Math.Abs(samples[i]));
        }
    
        // Calculate gain needed to reach target level
        float gain = peak > 0.001f ? targetLevel / peak : 1.0f;
    
        // Limit gain to avoid amplifying noise too much (max +12dB)
        gain = Math.Min(gain, 4.0f);
    
        // Apply compression in-place
        for (int i = 0; i < samples.Length; i++)
        {
            float sample = samples[i] * gain;
        
            // Apply soft-knee compression above threshold
            float absSample = Math.Abs(sample);
            if (absSample > threshold)
            {
                float excess = absSample - threshold;
                float compressed = threshold + excess / ratio;
                sample = Math.Sign(sample) * compressed;
            }
        
            // Clamp to valid range
            samples[i] = Math.Max(-1.0f, Math.Min(1.0f, sample));
        }
    }
}