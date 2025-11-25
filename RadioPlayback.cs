using System;
using System.Runtime.InteropServices;
using BMSAudioSim.Models;
using ManagedBass;

namespace BMSAudioSim;

public static class RadioPlayback
{
    private static RadioEffect? _radioEffect1;
    private static RadioEffect? _radioEffect2;
    private static RadioPreFilter? _radioPreFilter1;
    private static RadioPreFilter? _radioPreFilter2;

    private static float[]? _dspScratch = new float[8192];
    private static float[]? _buffer1 = new float[8192];
    private static float[]? _buffer2 = new float[8192];

    private static DSPProcedure? _dspProc;
    private static int _stream1;
    private static int _stream2;

    private static readonly object _lock = new object();
    private static bool _steppedEnabled = false;
    private static int _steppedDiffDbm = 0;

    // Heterodyne and mixing state
    private static double _heterodynePhase = 0;
    private static double _switchPhase = 0;
    private static double _warblePhase = 0;
    private static float _currentHeterodyneFreq = 0;
    private static float _heterodyneDriftTarget = 0;
    private static float _heterodyneDrift = 0;
    private static float _lastRadioFrequencyMHz = 0;
    private static Random _rng = new Random();

    private static AudioParams _currentParams;

    public static void Start(string filePath1, string filePath2, AudioParams initialParams, bool steppedEnabled,
        int steppedDiffDbm)
    {
        // Initialize BASS
        if (!Bass.Init())
            throw new Exception("Failed to initialize BASS.");

        _steppedEnabled = steppedEnabled;
        _steppedDiffDbm = steppedDiffDbm;
        _currentParams = initialParams;

        // Create primary stream
        _stream1 = Bass.CreateStream(filePath1, 0, 0, BassFlags.Loop | BassFlags.Float);
        if (_stream1 == 0)
            throw new Exception($"BASS error creating stream1: {Bass.LastError}");

        var info = Bass.ChannelGetInfo(_stream1);

        // Initialize filters for primary stream
        _radioPreFilter1 ??= new RadioPreFilter(info.Frequency);
        _radioEffect1 ??= new RadioEffect(info.Frequency, info.Channels, initialParams);

        // Create secondary stream
        _stream2 = Bass.CreateStream(filePath2, 0, 0, BassFlags.Loop | BassFlags.Float | BassFlags.Decode);
        if (_stream2 == 0)
            throw new Exception($"BASS error creating stream2: {Bass.LastError}");

        // Initialize filters for secondary stream
        _radioPreFilter2 ??= new RadioPreFilter(info.Frequency);
        _radioEffect2 ??= new RadioEffect(info.Frequency, info.Channels, initialParams);

        // Define DSP callback
        _dspProc = (handle, channel, bufferPtr, length, user) =>
        {
            int samples = length / sizeof(float);
            EnsureBufferSize(samples);

            // Capture snapshot of current params to avoid race conditions
            AudioParams currentParams;
            bool steppedEnabled;
            int steppedDiffDbm;
            lock (_lock)
            {
                currentParams = _currentParams;
                steppedEnabled = _steppedEnabled;
                steppedDiffDbm = _steppedDiffDbm;
            }

            // Copy primary stream to buffer1
            Marshal.Copy(bufferPtr, _buffer1, 0, samples);

            if (steppedEnabled && _stream2 != 0)
            {
                // Read from secondary stream
                int bytesRead = Bass.ChannelGetData(_stream2, _buffer2, length);

                if (bytesRead > 0)
                {
                    int samples2 = bytesRead / sizeof(float);

                    // === CALCULATE ALL GAINS FIRST ===
                    float gainRatio = (float)Math.Pow(10.0, steppedDiffDbm / 20.0);

                    float primaryGain = currentParams.Gain;
                    float secondaryGain = currentParams.Gain * gainRatio;

                    // Scale both down if needed to stay within [0, 1]
                    float maxGain = Math.Max(primaryGain, secondaryGain);
                    if (maxGain > 1.0f)
                    {
                        float scale = 1.0f / maxGain;
                        primaryGain *= scale;
                        secondaryGain *= scale;
                    }

                    primaryGain = Math.Clamp(primaryGain, 0.0f, 1.0f);
                    secondaryGain = Math.Clamp(secondaryGain, 0.0f, 1.0f);

                    // === PROCESS BOTH BUFFERS ===

                    // Process primary
                    _radioPreFilter1.SetNoiseLevel(currentParams.NoiseLevel);
                    _radioPreFilter1.Process(_buffer1, 0, samples, 1);

                    var params1 = new AudioParams
                    {
                        Gain = primaryGain,
                        LowpassHz = currentParams.LowpassHz,
                        NoiseLevel = currentParams.NoiseLevel,
                        DropoutProb = currentParams.DropoutProb,
                        FlutterDepth = currentParams.FlutterDepth
                    };
                    _radioEffect1.Params = params1;
                    _radioEffect1.Process(_buffer1, 0, samples);

                    // Process secondary
                    _radioPreFilter2.SetNoiseLevel(currentParams.NoiseLevel);
                    _radioPreFilter2.Process(_buffer2, 0, samples2, 1);

                    var params2 = new AudioParams
                    {
                        Gain = secondaryGain,
                        LowpassHz = currentParams.LowpassHz,
                        NoiseLevel = currentParams.NoiseLevel,
                        DropoutProb = currentParams.DropoutProb,
                        FlutterDepth = currentParams.FlutterDepth
                    };
                    _radioEffect2.Params = params2;
                    _radioEffect2.Process(_buffer2, 0, samples2);

                    // Mix with FM capture effect
                    MixWithCaptureEffect(_buffer1, _buffer2, _dspScratch,
                        Math.Min(samples, samples2),
                        info.Frequency,
                        -steppedDiffDbm,
                        currentParams.RadioFrequencyMHz,
                        primaryGain,
                        secondaryGain,
                        params1.DropoutProb);

                    // Copy mixed result back
                    Marshal.Copy(_dspScratch, 0, bufferPtr, Math.Min(samples, samples2));
                    return;
                }
            }

            // No stepped-on: process primary normally
            _radioPreFilter1.SetNoiseLevel(currentParams.NoiseLevel);
            _radioPreFilter1.Process(_buffer1, 0, samples, 1);

            _radioEffect1.Params = currentParams;
            _radioEffect1.Process(_buffer1, 0, samples);

            Marshal.Copy(_buffer1, 0, bufferPtr, samples);
        };

        // Attach DSP and start playback
        Bass.ChannelSetDSP(_stream1, _dspProc, IntPtr.Zero, 0);
        Bass.ChannelPlay(_stream1);
    }

    private static void EnsureBufferSize(int samples)
    {
        if (_dspScratch == null || _dspScratch.Length < samples)
            _dspScratch = new float[samples];
        if (_buffer1 == null || _buffer1.Length < samples)
            _buffer1 = new float[samples];
        if (_buffer2 == null || _buffer2.Length < samples)
            _buffer2 = new float[samples];
    }

    private static void MixWithCaptureEffect(float[] primary, float[] secondary, float[] output,
        int samples, int sampleRate, double powerDiffDbm, float radioFrequencyMHz, float primaryGain, float secondaryGain, float dropoutProb)
    {
        double dt = 1.0 / sampleRate;

        // Calculate mixing parameters
        AudioMixerParams mixer = CalculateMixerParams(powerDiffDbm);

        // === MANAGE HETERODYNE FREQUENCY ===
        if (mixer.HeterodyneFreq > 0)
        {
            // Check if frequency changed or first time
            if (_currentHeterodyneFreq == 0 || Math.Abs(radioFrequencyMHz - _lastRadioFrequencyMHz) > 0.001f)
            {
                // Generate new heterodyne frequency based on radio frequency
                // Base on frequency modulo for variation
                float baseHz = 700f + ((radioFrequencyMHz * 10f) % 600f);

                // Add deterministic variation based on frequency
                int seed = (int)(radioFrequencyMHz * 1000);
                Random freqRng = new Random(seed);
                baseHz += (float)(freqRng.NextDouble() * 100 - 50); // ±50 Hz

                _currentHeterodyneFreq = Math.Clamp(baseHz, 600f, 1400f);
                _lastRadioFrequencyMHz = radioFrequencyMHz;

                // Reset drift when frequency changes
                _heterodyneDrift = 0;
                _heterodyneDriftTarget = 0;
            }
        }
        else
        {
            // No interference - reset everything
            _currentHeterodyneFreq = 0;
            _lastRadioFrequencyMHz = 0;
            _heterodyneDrift = 0;
            _heterodyneDriftTarget = 0;
        }

        // Main mixing loop
        for (int i = 0; i < samples; i++)
        {
            float mixed;

            // Strong capture - one signal dominates completely
            if (mixer.CaptureRatio >= 0.98f)
            {
                mixed = primary[i];
            }
            else if (mixer.CaptureRatio <= 0.02f)
            {
                mixed = secondary[i];
            }
            else
            {
                // Interference region

                // Fast switching (10-40 Hz) - creates "buzz" quality
                float fastSwitchRate = 10.0f + (0.5f - Math.Abs(0.5f - mixer.CaptureRatio)) * 60.0f;
                _switchPhase += 2 * Math.PI * fastSwitchRate * dt;
                if (_switchPhase > Math.PI * 2) _switchPhase -= Math.PI * 2;
                float fastMod = (float)Math.Sin(_switchPhase) * 0.15f;

                // Slow warble (1 Hz) - gradual drift in mixing ratio
                _warblePhase += 2 * Math.PI * 1.0 * dt;
                if (_warblePhase > Math.PI * 2) _warblePhase -= Math.PI * 2;
                float slowMod = (float)Math.Sin(_warblePhase) * 0.1f;

                // Combine modulations
                float instantCapture = mixer.CaptureRatio + fastMod + slowMod;
                instantCapture = Math.Clamp(instantCapture, 0.0f, 1.0f);

                // Mix signals
                mixed = primary[i] * instantCapture + secondary[i] * (1 - instantCapture);

                // Add heterodyne whistle with slow frequency drift
                if (_currentHeterodyneFreq > 0 && mixer.InterferenceLevel > 0)
                {
                    // Update drift target every 2 seconds
                    if (i % (sampleRate * 2) == 0)
                    {
                        _heterodyneDriftTarget = (float)(_rng.NextDouble() * 2 - 1) * 10f;
                    }

                    float driftSpeed = 0.0001f;
                    _heterodyneDrift += (_heterodyneDriftTarget - _heterodyneDrift) * driftSpeed;
    
                    // Heterodyne amplitude limited by weaker signal
                    float signalStrength = Math.Min(primaryGain, secondaryGain);
                   
                    // Add phase noise when signals are weak
                    float phaseNoise = 0;
                    if (signalStrength < 0.5f)
                    {
                        // More phase noise when weak (unstable carriers)
                        float noiseAmount = (0.5f - signalStrength) * 2.0f; // 0 to 1
                        phaseNoise = (float)(_rng.NextDouble() * 2 - 1) * noiseAmount * 0.1f;
                    }
    
                    float actualFreq = _currentHeterodyneFreq + _heterodyneDrift + phaseNoise;
    
                    _heterodynePhase += 2 * Math.PI * actualFreq * dt;
                    if (_heterodynePhase > Math.PI * 2) _heterodynePhase -= Math.PI * 2;

                    // Scale amplitude by weaker signal and interference level
                    float heterodyneAmplitude = mixer.InterferenceLevel * 0.2f * signalStrength;
                    
                    if (dropoutProb > 0 && _rng.NextDouble() < dropoutProb * 0.5)
                    {
                        // Heterodyne cuts out (carriers lost in noise burst)
                        heterodyneAmplitude = 0;
                    }
                    
                    float heterodyne = (float)Math.Sin(_heterodynePhase) * heterodyneAmplitude;
    
                    mixed += heterodyne;
                }

                // Add interference distortion
                if (mixer.InterferenceLevel > 0.35f)
                {
                    // Intermodulation distortion
                    float im = mixed * mixed * Math.Sign(mixed) * mixer.InterferenceLevel * 0.2f;
                    mixed += im;

                    // Random noise bursts
                    if (_rng.NextDouble() < mixer.InterferenceLevel * 0.01)
                    {
                        mixed += (float)(_rng.NextDouble() * 2 - 1) * 0.3f;
                    }
                }
            }

            output[i] = Math.Clamp(mixed, -1f, 1f);
        }
    }

    private static AudioMixerParams CalculateMixerParams(double powerDiffDbm)
    {
        var mixer = new AudioMixerParams();
        double absDiff = Math.Abs(powerDiffDbm);

        if (absDiff >= 10.0)
        {
            // Complete capture
            mixer.CaptureRatio = powerDiffDbm > 0 ? 1.0f : 0.0f;
            mixer.InterferenceLevel = 0.0f;
            mixer.HeterodyneFreq = 0.0f;
        }
        else if (absDiff >= 6.0)
        {
            // Very strong capture: 90-100%
            if (powerDiffDbm > 0)
                mixer.CaptureRatio = (float)(0.9f + (absDiff - 6.0) / 4.0 * 0.1f);
            else
                mixer.CaptureRatio = (float)(0.1f - (absDiff - 6.0) / 4.0 * 0.1f);

            mixer.InterferenceLevel = 0.15f;
            mixer.HeterodyneFreq = 1.0f;
        }
        else if (absDiff >= 4.0)
        {
            // Strong capture: 80-90%
            if (powerDiffDbm > 0)
                mixer.CaptureRatio = (float)(0.8f + (absDiff - 4.0) / 2.0 * 0.1f);
            else
                mixer.CaptureRatio = (float)(0.2f - (absDiff - 4.0) / 2.0 * 0.1f);

            mixer.InterferenceLevel = 0.25f;
            mixer.HeterodyneFreq = 1.0f;
        }
        else if (absDiff >= 2.0)
        {
            // Moderate capture: 65-80%
            if (powerDiffDbm > 0)
                mixer.CaptureRatio = (float)(0.65f + (absDiff - 2.0) / 2.0 * 0.15f);
            else
                mixer.CaptureRatio = (float)(0.35f - (absDiff - 2.0) / 2.0 * 0.15f);

            mixer.InterferenceLevel = 0.35f;
            mixer.HeterodyneFreq = 1.0f;
        }
        else if (absDiff >= 1.0)
        {
            // Light capture: 55-65%
            if (powerDiffDbm > 0)
                mixer.CaptureRatio = (float)(0.55f + (absDiff - 1.0) / 1.0 * 0.1f);
            else
                mixer.CaptureRatio = (float)(0.45f - (absDiff - 1.0) / 1.0 * 0.1f);

            mixer.InterferenceLevel = 0.5f;
            mixer.HeterodyneFreq = 1.0f;
        }
        else
        {
            // Very close: 50-55%
            mixer.CaptureRatio = 0.5f + (float)(powerDiffDbm / 1.0) * 0.05f;
            mixer.InterferenceLevel = (float)(0.65f - absDiff * 0.15f);
            mixer.HeterodyneFreq = 1.0f;
        }

        return mixer;
    }

    public static void UpdateParams(AudioParams p, bool steppedEnabled, int steppedDiffDbm)
    {
        lock (_lock)
        {
            _currentParams = p;
            _steppedEnabled = steppedEnabled;
            _steppedDiffDbm = steppedDiffDbm;
        }
    }

    public static void Stop()
    {
        if (_stream1 != 0)
        {
            Bass.ChannelStop(_stream1);
            Bass.StreamFree(_stream1);
            _stream1 = 0;
        }

        if (_stream2 != 0)
        {
            Bass.ChannelStop(_stream2);
            Bass.StreamFree(_stream2);
            _stream2 = 0;
        }

        Bass.Free();
    }
}
