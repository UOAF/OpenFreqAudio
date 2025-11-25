using System;
using BMSAudioSim.Models;

namespace BMSAudioSim;

public class RadioMixer
{
    private double _heterodynePhase = 0;
    private double _switchPhase = 0;
    private readonly Random _rng = new Random();
    
    public void ProcessSteppedOn(
        float[] buffer1, float[] buffer2, 
        float[] output,
        AudioMixerParams mixer,
        int sampleRate)
    {
        double dt = 1.0 / sampleRate;
        
        for (int i = 0; i < output.Length; i++)
        {
            float mixed;
            
            if (mixer.CaptureRatio > 0.95f)
            {
                // Strong capture - primary only
                mixed = buffer1[i];
            }
            else
            {
                // Interference region
                
                // Rapid switching for near-equal signals
                float switchRate = (1.0f - mixer.CaptureRatio) * 40.0f; // 0-40 Hz
                _switchPhase += 2 * Math.PI * switchRate * dt;
                if (_switchPhase > Math.PI * 2) _switchPhase -= Math.PI * 2;
                
                float switchMod = (float)Math.Sin(_switchPhase);
                float instantCapture = mixer.CaptureRatio + switchMod * (1 - mixer.CaptureRatio) * 0.3f;
                instantCapture = Math.Clamp(instantCapture, 0.3f, 1.0f);
                
                // Mix signals based on instantaneous capture
                mixed = buffer1[i] * instantCapture + buffer2[i] * (1 - instantCapture);
                
                // Add heterodyne tone
                if (mixer.HeterodyneFreq > 0)
                {
                    _heterodynePhase += 2 * Math.PI * mixer.HeterodyneFreq * dt;
                    if (_heterodynePhase > Math.PI * 2) _heterodynePhase -= Math.PI * 2;
                    
                    float heterodyne = (float)Math.Sin(_heterodynePhase) * mixer.InterferenceLevel * 0.15f;
                    mixed += heterodyne;
                }
                
                // Add interference distortion
                if (mixer.InterferenceLevel > 0.3f)
                {
                    // Intermodulation distortion
                    float im = mixed * mixed * Math.Sign(mixed) * mixer.InterferenceLevel * 0.2f;
                    mixed += im;
                    
                    // Random bursts of noise
                    if (_rng.NextDouble() < mixer.InterferenceLevel * 0.01)
                    {
                        mixed += (float)(_rng.NextDouble() * 2 - 1) * 0.3f;
                    }
                }
            }
            
            output[i] = Math.Clamp(mixed, -1f, 1f);
        }
    }
}