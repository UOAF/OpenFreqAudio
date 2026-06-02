// ReSharper disable InconsistentNaming

using System;

namespace OpenFreqAudio;

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
    private readonly Random _rng;

    // Pink noise using Voss-McCartney dice-rolling algorithm
    // Instead of 7 multiplies + 7 adds per sample, averages ~2 operations
    private int _pinkNoiseCounter;
    private float _pinkNoiseSum;
    private readonly float[] _pinkNoiseDice = new float[5]; // 5 dice for good spectral balance

    // VHF crackle generator state
    private int _vhfCrackleSamplesLeft;
    private float _vhfCrackleAmplitude;

    // Low-frequency modulation for more organic feel
    private double _modulationPhase;
    private const double ModulationFrequency = 3.0; // Hz

    public enum RadioType
    {
        VHF_AM,
        UHF_AM,
        UHF_FM
    }

    private RadioType _radioType;

    public BackgroundNoiseGenerator(int sampleRate, int frequencyKhz)
    {
        _sampleRate = sampleRate;
        _rng = new Random(Environment.TickCount);

        // Initialize pink noise dice with random values
        for (int i = 0; i < _pinkNoiseDice.Length; i++)
        {
            _pinkNoiseDice[i] = (float)(_rng.NextDouble() * 2.0 - 1.0);
            _pinkNoiseSum += _pinkNoiseDice[i];
        }

        _radioType = frequencyKhz < 200000 ? RadioType.VHF_AM : RadioType.UHF_AM; // 200 MHz = 200000 kHz
    }

    /// <summary>
    /// Set the radio type to adjust noise characteristics
    /// </summary>
    public void SetRadioType(RadioType radioType)
    {
        _radioType = radioType;
    }

    /// <summary>
    /// Generate the next background-noise sample.
    /// </summary>
    public float NextSample()
    {
        // Generate base noise sample
        float noiseSample = _radioType switch
        {
            RadioType.VHF_AM => GenerateVHFNoise(),
            RadioType.UHF_AM => GenerateUHFNoise(),
            RadioType.UHF_FM => GenerateFMNoise(),
            _ => GenerateUHFNoise()
        };

        // For AM: no modulation, just straight noise
        // For FM: use modulation
        if (_radioType == RadioType.UHF_FM)
        {
            double dt = 1.0 / _sampleRate;
            _modulationPhase += 2.0 * Math.PI * ModulationFrequency * dt;
            if (_modulationPhase > 2.0 * Math.PI)
                _modulationPhase -= 2.0 * Math.PI;

            float modulation = (float)(0.85f + 0.15f * Math.Sin(_modulationPhase));
            noiseSample *= modulation;
        }

        return noiseSample;
    }

    /// <summary>
    /// Generate VHF/AM style noise: crackling static with pops
    /// </summary>
    private float GenerateVHFNoise()
    {
        // Pink noise using Voss-McCartney algorithm
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
            float envelope = _vhfCrackleSamplesLeft / 200f;
            envelope = MathF.Pow(envelope, 2.0f); // Exponential decay

            // Sharp, noisy crackle
            float crackleNoise = (float)(_rng.NextDouble() * 2.0 - 1.0);
            crackle = crackleNoise * _vhfCrackleAmplitude * envelope;

            _vhfCrackleSamplesLeft--;
        }

        // Mix pink noise (continuous) with crackles (intermittent)
        return pinkNoise + crackle;
    }

    /// <summary>
    /// Generate UHF/AM style noise: lighter crackling than VHF
    /// </summary>
    private float GenerateUHFNoise()
    {
        // Pink noise base
        float pinkNoise = GeneratePinkNoiseFast();

        // Lighter, less frequent crackles than VHF
        if (_vhfCrackleSamplesLeft <= 0)
        {
            // Moderate crackle rate
            if (_rng.NextDouble() < 0.0003) // ~14 events/sec
            {
                _vhfCrackleSamplesLeft = _rng.Next(40, 150); // Medium duration
                _vhfCrackleAmplitude = 0.2f + (float)_rng.NextDouble() * 0.3f; // Medium intensity
            }
        }

        float crackle = 0f;
        if (_vhfCrackleSamplesLeft > 0)
        {
            float envelope = _vhfCrackleSamplesLeft / 150f;
            envelope = MathF.Pow(envelope, 2.0f);

            float crackleNoise = (float)(_rng.NextDouble() * 2.0 - 1.0);
            crackle = crackleNoise * _vhfCrackleAmplitude * envelope;

            _vhfCrackleSamplesLeft--;
        }

        // Mix pink noise with lighter crackles - INCREASED base level
        return pinkNoise + crackle; // Was 0.12f, now 0.18f
    }

    /// <summary>
    /// Generate UHF/FM style noise: smooth white noise/hiss (future use)
    /// </summary>
    private float GenerateFMNoise()
    {
        float pinkNoise = GeneratePinkNoiseFast();

        // Add slight high-frequency component for the "hiss" character
        float whiteNoise = (float)(_rng.NextDouble() * 2.0 - 1.0);

        // Blend: mostly pink with some white for FM hiss characteristic
        return pinkNoise * 0.7f + whiteNoise * 0.3f;
    }

    /// <summary>
    /// Fast pink noise using Voss-McCartney dice-rolling algorithm
    /// Algorithm: Maintain N dice, update one die on each call based on counter bits
    /// The dice that change least frequently contribute low frequencies
    /// The dice that change most frequently contribute high frequencies
    /// Sum of all dice gives pink noise (1/f spectrum)
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