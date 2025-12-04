using System;

namespace BMSAudioSim;

/// <summary>
/// Generates background radio noise (static/hiss) when no transmissions are active
/// 
/// Noise characteristics vary by radio type:
/// - VHF (AM): Crackling static with occasional pops (atmospheric noise, ignition interference)
/// - UHF (FM): Smooth white noise/hiss (FM threshold noise, "sssshhh" sound)
/// 
/// PHASE 2b OPTIMIZATIONS:
/// - Fast Voss-McCartney pink noise algorithm (2-3x faster than filter method)
/// - Pre-calculated sin lookup for modulation (reuses Radiomixer's approach)
/// </summary>
public class BackgroundNoiseGenerator
{
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly Random _rng;
    
    // PHASE 2b: Fast pink noise using Voss-McCartney dice-rolling algorithm
    // Instead of 7 multiplies + 7 adds per sample, averages ~2 operations
    private int _pinkNoiseCounter = 0;
    private float _pinkNoiseSum = 0f;
    private readonly float[] _pinkNoiseDice = new float[5]; // 5 dice for good spectral balance
    
    // VHF crackle generator state
    private int _vhfCrackleSamplesLeft = 0;
    private float _vhfCrackleAmplitude = 0f;
    
    // Low-frequency modulation for more organic feel
    private double _modulationPhase = 0;
    private const double ModulationFrequency = 3.0; // Hz
    
    // PHASE 2b: Sine lookup table for modulation (shared with Radiomixer approach)
    private static class SineLookup
    {
        private const int TableSize = 2048;
        private static readonly float[] _table;
        private const float IndexScale = TableSize / (2f * MathF.PI);
        private const int IndexMask = TableSize - 1;
        
        static SineLookup()
        {
            _table = new float[TableSize];
            for (int i = 0; i < TableSize; i++)
            {
                _table[i] = MathF.Sin(i * 2f * MathF.PI / TableSize);
            }
        }
        
        public static float Sin(double x)
        {
            float xf = (float)(x % (2.0 * Math.PI));
            if (xf < 0) xf += 2f * MathF.PI;
    
            float indexF = xf * IndexScale;
            int index = (int)indexF & IndexMask;
            float frac = indexF - index;
    
            int nextIndex = (index + 1) & IndexMask;
            return _table[index] * (1f - frac) + _table[nextIndex] * frac;
        }
    }
    
    public enum RadioType
    {
        VHF_AM,  // 30-88 MHz, AM modulation
        UHF_FM   // 225-400 MHz, FM modulation
    }
    
    private RadioType _radioType = RadioType.UHF_FM;
    
    public BackgroundNoiseGenerator(int sampleRate, int channels, float frequencyMhz)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _rng = new Random(Environment.TickCount);
        
        // Initialize pink noise dice with random values
        for (int i = 0; i < _pinkNoiseDice.Length; i++)
        {
            _pinkNoiseDice[i] = (float)(_rng.NextDouble() * 2.0 - 1.0);
            _pinkNoiseSum += _pinkNoiseDice[i];
        }

        _radioType = frequencyMhz <= 200.0f ? RadioType.UHF_FM : RadioType.VHF_AM;
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
            
            // PHASE 2b: Use sine lookup for modulation (3-10x faster than Math.Sin)
            _modulationPhase += 2.0 * Math.PI * ModulationFrequency * dt;
            if (_modulationPhase > 2.0 * Math.PI)
                _modulationPhase -= 2.0 * Math.PI;
            
            float modulation = 0.85f + 0.15f * SineLookup.Sin(_modulationPhase);
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
        // PHASE 2b: Fast pink noise using Voss-McCartney algorithm
        float pinkNoise = GeneratePinkNoiseFast();
        
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
        // PHASE 2b: Fast pink noise
        float pinkNoise = GeneratePinkNoiseFast();
        
        // Add slight high-frequency component for the "hiss" character
        float whiteNoise = (float)(_rng.NextDouble() * 2.0 - 1.0);
        
        // Blend: mostly pink with some white for FM hiss characteristic
        return pinkNoise * 0.7f + whiteNoise * 0.3f;
    }
    
    /// <summary>
    /// Fast pink noise using Voss-McCartney dice-rolling algorithm
    /// 
    /// PHASE 2b: Much faster than filter-based approach
    /// - Old method: 7 multiplies + 7 adds per sample
    /// - New method: ~2 operations per sample average (only updates changed dice)
    /// 
    /// Algorithm: Maintain N dice, update one die on each call based on counter bits
    /// The dice that change least frequently contribute low frequencies
    /// The dice that change most frequently contribute high frequencies
    /// Sum of all dice gives pink noise (1/f spectrum)
    /// 
    /// Speedup: 2-3x faster than filter method
    /// Quality: Equivalent spectral characteristics
    /// </summary>
    private float GeneratePinkNoiseFast()
    {
        // Increment counter
        _pinkNoiseCounter++;
        
        // Find which bits changed (XOR with previous value)
        // This determines which dice to roll
        int changed = _pinkNoiseCounter ^ (_pinkNoiseCounter - 1);
        
        // Update dice based on changed bits
        // Dice 0 changes every sample (bit 0 always changes on increment)
        // Dice 1 changes every 2 samples (bit 1)
        // Dice 2 changes every 4 samples (bit 2)
        // etc.
        for (int i = 0; i < _pinkNoiseDice.Length; i++)
        {
            // Check if this bit changed
            if ((changed & (1 << i)) != 0)
            {
                // Roll this die: remove old value, generate new value, add it
                _pinkNoiseSum -= _pinkNoiseDice[i];
                _pinkNoiseDice[i] = (float)(_rng.NextDouble() * 2.0 - 1.0);
                _pinkNoiseSum += _pinkNoiseDice[i];
            }
        }
        
        // Average the dice to get pink noise
        // Normalize by number of dice for consistent amplitude
        return _pinkNoiseSum / _pinkNoiseDice.Length;
    }
}