using System;

namespace BMSAudioSim;

/// <summary>
/// Generates background radio noise (static/hiss) when no transmissions are active
/// 
/// Noise characteristics vary by radio type:
/// - VHF (AM): Crackling static with occasional pops (atmospheric noise, ignition interference)
/// - UHF (FM): Smooth white noise/hiss (FM threshold noise, "sssshhh" sound)
/// </summary>
public class BackgroundNoiseGenerator
{
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly Random _rng;
    
    // Pink noise filter state (for more natural sounding noise)
    private readonly float[] _pinkNoiseB = new float[7];
    private int _pinkNoiseIndex = 0;
    
    // VHF crackle generator state
    private int _vhfCrackleSamplesLeft = 0;
    private float _vhfCrackleAmplitude = 0f;
    
    // Low-frequency modulation for more organic feel
    private double _modulationPhase = 0;
    private const double ModulationFrequency = 3.0; // Hz
    
    public enum RadioType
    {
        VHF_AM,  // 30-88 MHz, AM modulation
        UHF_FM   // 225-400 MHz, FM modulation
    }
    
    private RadioType _radioType = RadioType.UHF_FM;
    
    public BackgroundNoiseGenerator(int sampleRate, int channels)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _rng = new Random(Environment.TickCount);
    }
    
    /// <summary>
    /// Set the radio type to adjust noise characteristics
    /// </summary>
    public void SetRadioType(RadioType radioType)
    {
        _radioType = radioType;
    }
    
    /// <summary>
    /// Generate background noise into the provided buffer
    /// </summary>
    /// <param name="buffer">Output buffer (interleaved samples)</param>
    /// <param name="offset">Starting offset in buffer</param>
    /// <param name="samples">Total samples to generate (frames * channels)</param>
    /// <param name="gain">Overall noise level (0.0 to 1.0)</param>
    public void GenerateNoise(float[] buffer, int offset, int samples, float gain)
    {
        int frames = samples / _channels;
        double dt = 1.0 / _sampleRate;
        
        for (int frame = 0; frame < frames; frame++)
        {
            // Generate base noise sample
            float noiseSample = _radioType switch
            {
                RadioType.VHF_AM => GenerateVHFNoise(),
                RadioType.UHF_FM => GenerateUHFNoise(),
                _ => GenerateUHFNoise()
            };
            
            // Apply slow modulation for organic feel
            _modulationPhase += 2.0 * Math.PI * ModulationFrequency * dt;
            if (_modulationPhase > 2.0 * Math.PI)
                _modulationPhase -= 2.0 * Math.PI;
            
            float modulation = 0.85f + 0.15f * (float)Math.Sin(_modulationPhase);
            noiseSample *= modulation;
            
            // Apply gain
            noiseSample *= gain;
            
            // Write to all channels
            for (int c = 0; c < _channels; c++)
            {
                int idx = offset + frame * _channels + c;
                buffer[idx] = Math.Clamp(noiseSample, -1f, 1f);
            }
        }
    }
    
    /// <summary>
    /// Generate VHF/AM style noise: crackling static with pops
    /// </summary>
    private float GenerateVHFNoise()
    {
        // Base pink noise (more natural than white noise)
        float pinkNoise = GeneratePinkNoise();
        
        // Occasional crackles/pops (atmospheric noise, ignition interference)
        if (_vhfCrackleSamplesLeft <= 0)
        {
            // Random chance of crackle (about 10-20 per second)
            if (_rng.NextDouble() < 0.0004) // At 48kHz, ~19 events/sec
            {
                _vhfCrackleSamplesLeft = _rng.Next(50, 200); // 1-4ms duration
                _vhfCrackleAmplitude = 0.3f + (float)_rng.NextDouble() * 0.4f; // Variable intensity
            }
        }
        
        float crackle = 0f;
        if (_vhfCrackleSamplesLeft > 0)
        {
            // Decaying crackle envelope
            float envelope = (float)_vhfCrackleSamplesLeft / 200f;
            envelope = MathF.Pow(envelope, 2.0f); // Exponential decay
            
            // Sharp, noisy crackle
            float crackleNoise = (float)(_rng.NextDouble() * 2.0 - 1.0);
            crackle = crackleNoise * _vhfCrackleAmplitude * envelope;
            
            _vhfCrackleSamplesLeft--;
        }
        
        // Mix pink noise (continuous) with crackles (intermittent)
        return pinkNoise * 0.15f + crackle;
    }
    
    /// <summary>
    /// Generate UHF/FM style noise: smooth white noise/hiss
    /// </summary>
    private float GenerateUHFNoise()
    {
        // Pink noise is softer and more pleasant than pure white noise
        float pinkNoise = GeneratePinkNoise();
        
        // Add slight high-frequency component for the "hiss" character
        float whiteNoise = (float)(_rng.NextDouble() * 2.0 - 1.0);
        
        // Blend: mostly pink with some white for FM hiss characteristic
        return pinkNoise * 0.7f + whiteNoise * 0.3f;
    }
    
    /// <summary>
    /// Generate pink noise using Paul Kellet's refined method
    /// Pink noise has equal energy per octave (sounds more natural than white noise)
    /// </summary>
    private float GeneratePinkNoise()
    {
        // Generate white noise
        float white = (float)(_rng.NextDouble() * 2.0 - 1.0);
        
        // Apply pink noise filter (accumulator method)
        _pinkNoiseB[0] = 0.99886f * _pinkNoiseB[0] + white * 0.0555179f;
        _pinkNoiseB[1] = 0.99332f * _pinkNoiseB[1] + white * 0.0750759f;
        _pinkNoiseB[2] = 0.96900f * _pinkNoiseB[2] + white * 0.1538520f;
        _pinkNoiseB[3] = 0.86650f * _pinkNoiseB[3] + white * 0.3104856f;
        _pinkNoiseB[4] = 0.55000f * _pinkNoiseB[4] + white * 0.5329522f;
        _pinkNoiseB[5] = -0.7616f * _pinkNoiseB[5] - white * 0.0168980f;
        
        float pink = _pinkNoiseB[0] + _pinkNoiseB[1] + _pinkNoiseB[2] + 
                     _pinkNoiseB[3] + _pinkNoiseB[4] + _pinkNoiseB[5] + 
                     _pinkNoiseB[6] + white * 0.5362f;
        
        _pinkNoiseB[6] = white * 0.115926f;
        
        // Normalize
        return pink * 0.11f;
    }
}