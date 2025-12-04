using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ManagedBass;

namespace BMSAudioSim;

/// <summary>
/// Manages multiple concurrent radio transmissions across multiple frequencies
/// with automatic stepped-on interference per frequency and multi-frequency listening
/// </summary>
public class RadioPlayback
{
    // Stream tracking
    private class RadioStream
    {
        public string StreamId { get; set; } = "";
        public float FrequencyMHz { get; set; }
        public int BassStreamHandle { get; set; }
        public int Channels { get; set; }  // Track channel count
        public RadioEffect RadioEffect { get; set; }
        public RadioPreFilter RadioPreFilter { get; set; }
        public AudioParams CurrentParams { get; set; }
        public float[] Buffer { get; set; } = new float[8192];
        public bool IsStopping { get; set; }  // Marked for removal after burst
        public int StoppingBurstSamplesLeft { get; set; }  // Countdown until removal
    }
    
    // Per-frequency configuration
    private class FrequencyConfig
    {
        public float Volume { get; set; } = 1.0f;
        public AudioChannel AudioChannel { get; set; } = AudioChannel.Both;
        public Radiomixer Mixer { get; set; } = new Radiomixer();
        public bool IsTuned { get; set; } = false; // Is user listening to this frequency?
    }
    
    /// <summary>
    /// Audio channel routing options
    /// </summary>
    public enum AudioChannel
    {
        Left,
        Right,
        Both
    }

    private readonly Dictionary<string, RadioStream> _streams = new();
    private readonly Dictionary<float, FrequencyConfig> _frequencies = new(); // Per-frequency config
    private readonly object _lock = new();
    
    // Master output stream (receives DSP processing)
    private int _masterStream;
    private DSPProcedure? _dspProc;
    private bool _dspSetup = false; // Track if DSP callback has been set up
    
    // Processing resources
    private float[] _dspScratch = new float[8192];
    private float[] _mixBuffer1 = new float[8192];
    private float[] _mixBuffer2 = new float[8192];
    private float[] _frequencyMixBuffer = new float[8192];
    private float[] _noiseBuffer = new float[8192];
    private Random _rng = new Random();
    
    // Sample rate (set from first stream)
    private int _sampleRate = 48000;
    private int _channels = 2;
    
    // User-controlled squelch threshold
    private float _squelchThreshold = 0.03f;
    
    // Background noise generator
    private BackgroundNoiseGenerator? _noiseGenerator;
    private float _noiseFadeGain = 0f; // Current fade envelope for noise
    private const int NoiseFadeSamples = 2400; // ~50ms fade at 48kHz
    private const float NoiseBaseLevel = 0.08f; // Base noise level (subtle but audible)
    
    /// <summary>
    /// Copy stream buffer to output buffer, upmixing mono to stereo if needed
    /// </summary>
    private void CopyStreamToBuffer(float[] sourceBuffer, int sourceChannels, float[] destBuffer, int destSamples)
    {
        if (sourceChannels == 1)
        {
            // Mono to stereo - duplicate each sample to both channels
            int frames = destSamples / 2;
            int sourceFrames = Math.Min(frames, sourceBuffer.Length);
            
            for (int frame = 0; frame < sourceFrames; frame++)
            {
                float sample = sourceBuffer[frame];
                destBuffer[frame * 2] = sample;      // Left
                destBuffer[frame * 2 + 1] = sample;  // Right
            }
        }
        else if (sourceChannels == 2)
        {
            // Stereo to stereo - direct copy
            Array.Copy(sourceBuffer, destBuffer, Math.Min(destSamples, sourceBuffer.Length));
        }
        else
        {
            // Multi-channel - just copy what we can
            Array.Copy(sourceBuffer, destBuffer, Math.Min(destSamples, sourceBuffer.Length));
        }
    }
    
    private static bool _bassInitialized = false;
    private static readonly object _bassInitLock = new();

    public RadioPlayback()
    {
        // Initialize BASS (only once globally)
        lock (_bassInitLock)
        {
            if (!_bassInitialized)
            {
                if (!Bass.Init())
                    throw new Exception("Failed to initialize BASS.");
                _bassInitialized = true;
            }
        }
        
        // Load stepped-on sample (shared across all frequencies)
        Radiomixer.LoadSteppedOnSample("stepped-on.ogg");
        
        // Master stream will be created when first stream is added (after we know sample rate)
    }

    /// <summary>
    /// Start a new transmission stream on a specific frequency
    /// </summary>
    /// <param name="streamId">Unique identifier for this stream</param>
    /// <param name="filePath">Audio file path</param>
    /// <param name="audioParams">RF parameters for this transmission</param>
    public void StartStream(string streamId, string filePath, AudioParams audioParams)
    {
        lock (_lock)
        {
            // If stream already exists, stop it first
            if (_streams.ContainsKey(streamId))
            {
                StopStreamInternal(streamId);
            }

            // Ensure frequency config exists
            if (!_frequencies.ContainsKey(audioParams.RadioFrequencyMHz))
            {
                _frequencies[audioParams.RadioFrequencyMHz] = new FrequencyConfig();
            }

            // Create BASS stream
            int bassStream = Bass.CreateStream(filePath, 0, 0, BassFlags.Loop | BassFlags.Float | BassFlags.Decode);
            if (bassStream == 0)
                throw new Exception($"BASS error creating stream '{streamId}': {Bass.LastError}");

            var info = Bass.ChannelGetInfo(bassStream);
            
            Console.Out.WriteLine($"[StartStream] Stream info: sampleRate={info.Frequency}, channels={info.Channels}");
            Console.Out.WriteLine($"[StartStream] Current state: _masterStream={_masterStream}, _sampleRate={_sampleRate}, _dspSetup={_dspSetup}");
            
            // Check if master stream needs to be (re)created
            if (_masterStream == 0)
            {
                Console.Out.WriteLine($"[StartStream] No master stream, creating one...");
                // No master stream yet - create with stream's sample rate
                _sampleRate = info.Frequency;
                _channels = 2;  // Always stereo for channel routing
                StartMasterStream();
            }
            else if (_sampleRate != info.Frequency)
            {
                // Master stream exists but sample rate mismatch - recreate it
                Console.Out.WriteLine($"[RadioPlayback] Sample rate mismatch: Master={_sampleRate}Hz, Stream={info.Frequency}Hz - recreating master stream");
                StopMasterStream();
                _sampleRate = info.Frequency;
                _channels = 2;  // Always stereo for channel routing
                StartMasterStream();
            }
            else
            {
                Console.Out.WriteLine($"[StartStream] Master stream exists and sample rate matches, reusing");
            }

            // Create stream object
            var stream = new RadioStream
            {
                StreamId = streamId,
                FrequencyMHz = audioParams.RadioFrequencyMHz,
                BassStreamHandle = bassStream,
                Channels = info.Channels,
                RadioEffect = new RadioEffect(info.Frequency, info.Channels, audioParams),
                RadioPreFilter = new RadioPreFilter(info.Frequency),
                CurrentParams = audioParams,
                Buffer = new float[8192],
                IsStopping = false
            };
            
            // Set the current squelch threshold on the new RadioEffect
            stream.RadioEffect.SetSquelchThreshold(_squelchThreshold);

            _streams.Add(streamId, stream);
            
            // Trigger initial squelch burst if stream is hearable
            if (audioParams.Gain >= _squelchThreshold)
            {
                Console.Out.WriteLine($"Stream {streamId} starting - triggering squelch burst");
                stream.RadioEffect.TriggerSquelchBurst();
            }
        }
    }

    /// <summary>
    /// Stop a transmission stream
    /// </summary>
    public async Task StopStream(string streamId)
    {
        lock (_lock)
        {
            if (!_streams.TryGetValue(streamId, out var stream))
                return; // Stream doesn't exist
            
            // Mark stream as stopping - DSP callback will handle the burst and cleanup
            if (stream.CurrentParams.Gain >= _squelchThreshold)
            {
                Console.Out.WriteLine($"Stream {streamId} stopping - triggering squelch burst");
                stream.IsStopping = true;
                stream.StoppingBurstSamplesLeft = 720; // 15ms at 48kHz (burst duration)
                stream.RadioEffect.TriggerSquelchBurst();
            }
            else
            {
                // No burst needed, remove immediately
                StopStreamInternal(streamId);
            }
        }
        
        // No need to wait here - DSP callback will handle timing
    }

    private void StopStreamInternal(string streamId)
    {
        if (!_streams.TryGetValue(streamId, out var stream))
            return;

        if (stream.BassStreamHandle != 0)
        {
            Bass.StreamFree(stream.BassStreamHandle);
        }

        _streams.Remove(streamId);
        
        // Only stop master stream if no streams AND no tuned frequencies
        bool hasTunedFrequencies = _frequencies.Values.Any(f => f.IsTuned);
        if (_streams.Count == 0 && !hasTunedFrequencies && _masterStream != 0)
        {
            StopMasterStream();
        }
    }

    /// <summary>
    /// Update RF parameters for a specific stream while it's playing
    /// RadioEffect will automatically detect gain changes and trigger squelch bursts
    /// </summary>
    /// <param name="streamId">Stream identifier</param>
    /// <param name="newParams">New RF parameters (gain, distance, SNR, etc.)</param>
    public void UpdateStreamParams(string streamId, AudioParams newParams)
    {
        lock (_lock)
        {
            if (!_streams.TryGetValue(streamId, out var stream))
                return;

            // Update parameters - RadioEffect.Params setter will automatically handle squelch transitions
            stream.CurrentParams = newParams;
            stream.RadioEffect.Params = newParams;
        }
    }

    /// <summary>
    /// Initialize the audio system with default settings (48kHz, stereo).
    /// Call this before TuneFrequency() if you want background noise before any WebRTC streams.
    /// Optional - system auto-initializes when first stream arrives if not called.
    /// </summary>
    public void Initialize()
    {
        lock (_lock)
        {
            if (_masterStream == 0)
            {
                StartMasterStream();
            }
        }
    }

    /// <summary>
    /// Tune to a frequency (start listening). Background noise will play if squelch is low.
    /// Master stream will auto-start if not already running.
    /// </summary>
    /// <param name="frequencyMHz">Frequency in MHz</param>
    public void TuneFrequency(float frequencyMHz)
    {
        lock (_lock)
        {
            Console.Out.WriteLine($"[TuneFrequency] Tuning to {frequencyMHz:F2} MHz, _masterStream={_masterStream}, _dspSetup={_dspSetup}");
            
            if (!_frequencies.ContainsKey(frequencyMHz))
            {
                _frequencies[frequencyMHz] = new FrequencyConfig();
            }

            _frequencies[frequencyMHz].IsTuned = true;

            // Start master stream if not already started
            if (_masterStream == 0)
            {
                Console.Out.WriteLine($"[TuneFrequency] Starting master stream");
                StartMasterStream();
                Console.Out.WriteLine($"[TuneFrequency] Master stream started: _masterStream={_masterStream}, _dspSetup={_dspSetup}");
            }
        }
    }

    /// <summary>
    /// Untune from a frequency (stop listening). Background noise will stop for this frequency.
    /// </summary>
    /// <param name="frequencyMHz">Frequency in MHz</param>
    public void UntuneFrequency(float frequencyMHz)
    {
        lock (_lock)
        {
            Console.Out.WriteLine($"[UntuneFrequency] Untuning {frequencyMHz:F2} MHz, _masterStream={_masterStream}, _streams.Count={_streams.Count}");
            
            if (_frequencies.ContainsKey(frequencyMHz))
            {
                _frequencies[frequencyMHz].IsTuned = false;

                // Stop master stream if no frequencies tuned and no streams
                bool hasTunedFrequencies = _frequencies.Values.Any(f => f.IsTuned);
                Console.Out.WriteLine($"[UntuneFrequency] hasTunedFrequencies={hasTunedFrequencies}, _streams.Count={_streams.Count}");
                
                if (!hasTunedFrequencies && _streams.Count == 0 && _masterStream != 0)
                {
                    Console.Out.WriteLine($"[UntuneFrequency] Stopping master stream");
                    StopMasterStream();
                }
            }
        }
    }

    /// <summary>
    /// Check if any frequencies are currently tuned
    /// </summary>
    public bool HasTunedFrequencies()
    {
        lock (_lock)
        {
            return _frequencies.Values.Any(f => f.IsTuned);
        }
    }
    /// When threshold changes, all RadioEffect instances are updated
    /// </summary>
    /// <param name="threshold">New squelch threshold (typically 0.01 to 0.1)</param>
    public void SetSquelchThreshold(float threshold)
    {
        lock (_lock)
        {
            _squelchThreshold = Math.Clamp(threshold, 0.001f, 1.0f);
            
            // Update threshold on all RadioEffect instances
            foreach (var stream in _streams.Values)
            {
                stream.RadioEffect.SetSquelchThreshold(_squelchThreshold);
            }
        }
    }

    /// <summary>
    /// Get current squelch threshold
    /// </summary>
    public float GetSquelchThreshold()
    {
        lock (_lock)
        {
            return _squelchThreshold;
        }
    }

    /// <summary>
    /// Set volume for a specific frequency
    /// </summary>
    /// <param name="frequencyMHz">Frequency in MHz</param>
    /// <param name="volume">Volume (0.0 to 1.0)</param>
    public void SetFrequencyVolume(float frequencyMHz, float volume)
    {
        lock (_lock)
        {
            if (!_frequencies.ContainsKey(frequencyMHz))
            {
                _frequencies[frequencyMHz] = new FrequencyConfig();
            }
            _frequencies[frequencyMHz].Volume = Math.Clamp(volume, 0f, 1f);
        }
    }

    /// <summary>
    /// Set audio channel routing for a specific frequency
    /// </summary>
    /// <param name="frequencyMHz">Frequency in MHz</param>
    /// <param name="audioChannel">Channel routing (Left/Right/Both)</param>
    public void SetFrequencyAudioChannel(float frequencyMHz, AudioChannel audioChannel)
    {
        lock (_lock)
        {
            if (!_frequencies.ContainsKey(frequencyMHz))
            {
                _frequencies[frequencyMHz] = new FrequencyConfig();
            }
            _frequencies[frequencyMHz].AudioChannel = audioChannel;
        }
    }

    /// <summary>
    /// Get volume for a specific frequency
    /// </summary>
    public float GetFrequencyVolume(float frequencyMHz)
    {
        lock (_lock)
        {
            if (_frequencies.TryGetValue(frequencyMHz, out var freq))
                return freq.Volume;
            return 1.0f;
        }
    }

    /// <summary>
    /// Get audio channel routing for a specific frequency
    /// </summary>
    public AudioChannel GetFrequencyAudioChannel(float frequencyMHz)
    {
        lock (_lock)
        {
            if (_frequencies.TryGetValue(frequencyMHz, out var freq))
                return freq.AudioChannel;
            return AudioChannel.Both;
        }
    }

    /// <summary>
    /// Set the radio type for background noise generation
    /// VHF (AM): Crackling static with pops
    /// UHF (FM): Smooth white noise/hiss
    /// </summary>
    /// <param name="radioType">Radio type (VHF_AM or UHF_FM)</param>
    public void SetRadioType(BackgroundNoiseGenerator.RadioType radioType)
    {
        _noiseGenerator?.SetRadioType(radioType);
    }

    /// <summary>
    /// Set radio type based on frequency
    /// </summary>
    /// <param name="frequencyMHz">Frequency in MHz</param>
    public void SetRadioTypeFromFrequency(float frequencyMHz)
    {
        // VHF: 30-88 MHz (AM)
        // UHF: 225-400 MHz (FM)
        var radioType = frequencyMHz < 100 
            ? BackgroundNoiseGenerator.RadioType.VHF_AM 
            : BackgroundNoiseGenerator.RadioType.UHF_FM;
        
        _noiseGenerator?.SetRadioType(radioType);
    }

    private void StartMasterStream()
    {
        Console.Out.WriteLine($"[StartMasterStream] Called: _sampleRate={_sampleRate}, _channels={_channels}, _dspSetup={_dspSetup}");
        
        // Create master output stream with streaming callback
        // The callback generates silence, DSP will fill it with audio
        StreamProcedure streamProc = (handle, buffer, length, user) =>
        {
            // Generate silence - DSP will fill this with actual audio
            if (buffer != IntPtr.Zero)
            {
                // Fill with zeros (silence)
                unsafe
                {
                    float* ptr = (float*)buffer;
                    int samples = length / sizeof(float);
                    for (int i = 0; i < samples; i++)
                        ptr[i] = 0f;
                }
            }
            return length;
        };
        
        _masterStream = Bass.CreateStream(_sampleRate, _channels, BassFlags.Float, streamProc, IntPtr.Zero);
        if (_masterStream == 0)
            throw new Exception($"BASS error creating master stream: {Bass.LastError}");
        
        Console.Out.WriteLine($"[StartMasterStream] Master stream created: handle={_masterStream}");
        
        // Initialize background noise generator
        _noiseGenerator = new BackgroundNoiseGenerator(_sampleRate, _channels);
        _noiseGenerator.SetRadioType(BackgroundNoiseGenerator.RadioType.UHF_FM); // Default to UHF
        
        // IMPORTANT: Always set up DSP callback for each new master stream
        // The DSP callback must be attached to THIS specific master stream handle
        // Even if _dspSetup is true from a previous master stream, we need to reattach
        Console.Out.WriteLine($"[StartMasterStream] Setting up DSP for new master stream...");
        SetupDSPAndPlay();
        _dspSetup = true;
        Console.Out.WriteLine($"[StartMasterStream] DSP setup complete, _dspSetup={_dspSetup}");
    }

    private void StopMasterStream()
    {
        Console.Out.WriteLine($"[StopMasterStream] Called: _masterStream={_masterStream}");
        
        if (_masterStream != 0)
        {
            Bass.ChannelStop(_masterStream);
            Bass.StreamFree(_masterStream);
            _masterStream = 0;
            _dspSetup = false;
            Console.Out.WriteLine($"[StopMasterStream] Master stream stopped and freed");
        }
    }

    private void SetupDSPAndPlay()
    {
        Console.Out.WriteLine($"[SetupDSPAndPlay] Called: _masterStream={_masterStream}");
        
        _dspProc = (handle, channel, bufferPtr, length, user) =>
        {
            int samples = length / sizeof(float);
            EnsureBufferSize(samples);

            // Get snapshot of all active streams
            List<RadioStream> activeStreams;
            Dictionary<float, FrequencyConfig> frequencySnapshot;
            
            lock (_lock)
            {
                activeStreams = _streams.Values.ToList();
                frequencySnapshot = new Dictionary<float, FrequencyConfig>(_frequencies);
            }

            // Ensure all buffers are large enough for this callback
            foreach (var stream in activeStreams)
            {
                if (stream.Buffer.Length < samples)
                {
                    stream.Buffer = new float[samples];
                }
            }

            // Clear output buffer
            Array.Clear(_dspScratch, 0, samples);

            // Check if we have any hearable streams (squelch open, not stopping)
            // Use RadioEffect's squelch state instead of direct threshold checking
            // to avoid race conditions with BASS's synchronous DSP callback invocation
            bool hasHearableStreams = activeStreams.Any(s => 
                !s.IsStopping && s.RadioEffect.IsSquelchOpen);

            // Check if any frequencies are tuned (user is listening)
            bool hasTunedFrequencies = _frequencies.Values.Any(f => f.IsTuned);

            // Background noise should only play when:
            // 1. (No hearable streams AND at least one frequency is tuned) AND
            // 2. Squelch threshold is LOW enough (user wants to hear weak signals/noise)
            // The background noise itself is a weak signal (~0.08 effective gain)
            // So it should be squelched when threshold is above that level
            float currentSquelchThreshold;
            lock (_lock)
            {
                currentSquelchThreshold = _squelchThreshold;
            }
            
            const float BackgroundNoiseEffectiveGain = NoiseBaseLevel; // 0.08
            bool noiseIsSquelched = BackgroundNoiseEffectiveGain < currentSquelchThreshold;

            if (!hasHearableStreams && hasTunedFrequencies && !noiseIsSquelched)
            {
                // No hearable streams - generate background noise
                // Fade in noise smoothly
                float targetGain = NoiseBaseLevel;
                float fadeStep = targetGain / NoiseFadeSamples;
                
                if (_noiseBuffer.Length < samples)
                    _noiseBuffer = new float[samples];
                
                // Generate noise
                _noiseGenerator?.GenerateNoise(_noiseBuffer, 0, samples, 1.0f);
                
                // Determine which channels to play noise on based on tuned frequencies
                bool playLeft = false;
                bool playRight = false;
                foreach (var freq in frequencySnapshot)
                {
                    if (freq.Value.IsTuned)
                    {
                        switch (freq.Value.AudioChannel)
                        {
                            case AudioChannel.Left:
                                playLeft = true;
                                break;
                            case AudioChannel.Right:
                                playRight = true;
                                break;
                            case AudioChannel.Both:
                                playLeft = true;
                                playRight = true;
                                break;
                        }
                    }
                }
                
                // Apply fade and mix with channel routing
                int frames = samples / _channels;
                
                if (_channels == 1)
                {
                    // Mono - ignore channel routing, always play
                    for (int frame = 0; frame < frames; frame++)
                    {
                        // Update fade envelope
                        if (_noiseFadeGain < targetGain)
                        {
                            _noiseFadeGain = Math.Min(_noiseFadeGain + fadeStep, targetGain);
                        }
                        
                        _dspScratch[frame] = _noiseBuffer[frame] * _noiseFadeGain;
                    }
                }
                else if (_channels == 2)
                {
                    // Stereo - respect channel routing
                    for (int frame = 0; frame < frames; frame++)
                    {
                        // Update fade envelope
                        if (_noiseFadeGain < targetGain)
                        {
                            _noiseFadeGain = Math.Min(_noiseFadeGain + fadeStep, targetGain);
                        }
                        
                        int leftIdx = frame * 2;
                        int rightIdx = frame * 2 + 1;
                        
                        float leftNoise = _noiseBuffer[leftIdx] * _noiseFadeGain;
                        float rightNoise = _noiseBuffer[rightIdx] * _noiseFadeGain;
                        
                        // Route noise to selected channels
                        if (playLeft && !playRight)
                        {
                            // Left only - route both channels to left
                            _dspScratch[leftIdx] = leftNoise + rightNoise;
                        }
                        else if (!playLeft && playRight)
                        {
                            // Right only - route both channels to right
                            _dspScratch[rightIdx] = leftNoise + rightNoise;
                        }
                        else if (playLeft && playRight)
                        {
                            // Both - normal stereo
                            _dspScratch[leftIdx] = leftNoise;
                            _dspScratch[rightIdx] = rightNoise;
                        }
                        // else: neither playLeft nor playRight - silence (shouldn't happen)
                    }
                }
                else
                {
                    // Multi-channel (>2) - play on all channels
                    for (int frame = 0; frame < frames; frame++)
                    {
                        // Update fade envelope
                        if (_noiseFadeGain < targetGain)
                        {
                            _noiseFadeGain = Math.Min(_noiseFadeGain + fadeStep, targetGain);
                        }
                        
                        for (int c = 0; c < _channels; c++)
                        {
                            int idx = frame * _channels + c;
                            _dspScratch[idx] = _noiseBuffer[idx] * _noiseFadeGain;
                        }
                    }
                }
                
                // Clamp and output
                for (int i = 0; i < samples; i++)
                {
                    _dspScratch[i] = Math.Clamp(_dspScratch[i], -1f, 1f);
                }
                
                Marshal.Copy(_dspScratch, 0, bufferPtr, samples);
                return;
            }
            
            // We have hearable streams OR noise is squelched - fade out background noise if it was playing
            if (_noiseFadeGain > 0f)
            {
                float fadeStep = NoiseBaseLevel / NoiseFadeSamples;
                _noiseFadeGain = Math.Max(_noiseFadeGain - fadeStep * (samples / _channels), 0f);
            }

            // Read and process all streams
            List<string> streamsToRemove = new List<string>();
            
            foreach (var stream in activeStreams)
            {
                // Handle stopping streams
                if (stream.IsStopping)
                {
                    stream.StoppingBurstSamplesLeft -= samples;
                    if (stream.StoppingBurstSamplesLeft <= 0)
                    {
                        // Burst complete, mark for removal
                        streamsToRemove.Add(stream.StreamId);
                        continue;
                    }
                    
                    // During burst: Check if stream should be squelched
                    if (stream.CurrentParams.Gain < _squelchThreshold)
                    {
                        // Stream is squelched - skip burst, just mark for removal
                        streamsToRemove.Add(stream.StreamId);
                        continue;
                    }
                    
                    
                    // Burst is hearable: Clear buffer (don't read new data from file)
                    // The RadioEffect will generate the burst sound on silence
                    int clearSamples = Math.Min(stream.Buffer.Length, samples);
                    Array.Clear(stream.Buffer, 0, clearSamples);
                    
                    // Process with RadioEffect to generate burst
                    stream.RadioEffect.Process(stream.Buffer, 0, clearSamples);
                    continue;  // Skip reading from BASS
                }
                
                // Calculate correct number of bytes to read based on stream's channel count
                // Master stream is stereo (2 channels), but individual streams may be mono
                int frames = samples / _channels;  // Number of frames in master output
                int bytesToRead = frames * stream.Channels * sizeof(float);  // Bytes needed from this stream
                
                int bytesRead = Bass.ChannelGetData(stream.BassStreamHandle, stream.Buffer, bytesToRead);
                if (bytesRead <= 0)
                    continue;

                int streamSamples = bytesRead / sizeof(float);
                
                // Process with pre-filter and effects
                if (stream.CurrentParams.Gain >= _squelchThreshold)
                {
                    stream.RadioPreFilter.SetNoiseLevel(stream.CurrentParams.NoiseLevel);
                    stream.RadioPreFilter.Process(stream.Buffer, 0, streamSamples, stream.Channels);
                }
                else
                {
                    Array.Clear(stream.Buffer, 0, streamSamples);
                }
                
                stream.RadioEffect.Process(stream.Buffer, 0, streamSamples);
            }

            // Group streams by frequency (exclude stopping streams - they're just playing burst)
            var streamsByFrequency = activeStreams
                .Where(s => !s.IsStopping)  // Don't include stopping streams in interference logic
                .GroupBy(s => s.FrequencyMHz)
                .ToDictionary(g => g.Key, g => g.ToList());

            // Process each frequency separately
            foreach (var kvp in streamsByFrequency)
            {
                float frequency = kvp.Key;
                var streamsOnFreq = kvp.Value;
                
                // Get config for this frequency
                if (!frequencySnapshot.TryGetValue(frequency, out var freqConfig))
                {
                    // No config yet, use defaults
                    Console.Out.WriteLine($"[DSP] WARNING: No config for frequency {frequency:F6}MHz, using defaults (Both channels)");
                    Console.Out.WriteLine($"[DSP] Available frequencies: {string.Join(", ", frequencySnapshot.Keys.Select(k => k.ToString("F6")))}");
                    freqConfig = new FrequencyConfig();
                }
                
                // Skip if volume is zero (muted)
                if (freqConfig.Volume <= 0f)
                    continue;

                Array.Clear(_frequencyMixBuffer, 0, samples);

                if (streamsOnFreq.Count == 1)
                {
                    // Single stream on this frequency - copy with upmix if needed
                    var stream = streamsOnFreq[0];
                    CopyStreamToBuffer(stream.Buffer, stream.Channels, _frequencyMixBuffer, samples);
                }
                else
                {
                    // Multiple streams on same frequency - find two strongest and apply stepped-on
                    var sortedByGain = streamsOnFreq
                        .OrderByDescending(s => s.CurrentParams.Gain)
                        .Take(2)
                        .ToList();

                    if (sortedByGain.Count == 1)
                    {
                        CopyStreamToBuffer(sortedByGain[0].Buffer, sortedByGain[0].Channels, _frequencyMixBuffer, samples);
                    }
                    else
                    {
                        // Two strongest streams - apply stepped-on interference
                        var primary = sortedByGain[0];
                        var secondary = sortedByGain[1];
                        
                        float primaryGain = primary.CurrentParams.Gain;
                        float secondaryGain = secondary.CurrentParams.Gain;
                        
                        // Calculate power difference
                        double gainRatio = secondaryGain / Math.Max(primaryGain, 0.001f);
                        int powerDiffDbm = (int)(20.0 * Math.Log10(gainRatio));
                        
                        // Copy to mixing buffers with upmix if needed
                        CopyStreamToBuffer(primary.Buffer, primary.Channels, _mixBuffer1, samples);
                        CopyStreamToBuffer(secondary.Buffer, secondary.Channels, _mixBuffer2, samples);
                        
                        int mixSamples = samples; // Use full buffer size now that we've upmixed
                        
                        // Calculate stepped-on parameters
                        var steppedParams = Radiomixer.CalculateSteppedOnParams(
                            primary.CurrentParams, secondary.CurrentParams,
                            primary.CurrentParams.Distance_km, secondary.CurrentParams.Distance_km,
                            primary.CurrentParams.SNR_dB, secondary.CurrentParams.SNR_dB
                        );
                        
                        // Apply physics-based interference
                        freqConfig.Mixer.ProcessSteppedOn(
                            _mixBuffer1,
                            _mixBuffer2,
                            _frequencyMixBuffer,
                            steppedParams,
                            _sampleRate,
                            primaryGain,
                            secondaryGain
                        );
                    }
                }

                // Mix this frequency into the main output with volume and channel routing
                float volume = freqConfig.Volume;
                AudioChannel audioChannel = freqConfig.AudioChannel;
                
                if (_channels == 1)
                {
                    // Mono - ignore channel routing, always mix
                    for (int i = 0; i < samples; i++)
                    {
                        _dspScratch[i] += _frequencyMixBuffer[i] * volume;
                    }
                }
                else if (_channels == 2)
                {
                    // Stereo - respect channel routing
                    int frames = samples / 2;
                    
                    for (int frame = 0; frame < frames; frame++)
                    {
                        int leftIdx = frame * 2;
                        int rightIdx = frame * 2 + 1;
                        
                        float leftSample = _frequencyMixBuffer[leftIdx] * volume;
                        float rightSample = _frequencyMixBuffer[rightIdx] * volume;
                        
                        // Apply channel routing
                        switch (audioChannel)
                        {
                            case AudioChannel.Left:
                                // Route both source channels to left output
                                _dspScratch[leftIdx] += leftSample + rightSample;
                                // Right output gets silence
                                break;
                                
                            case AudioChannel.Right:
                                // Route both source channels to right output
                                // Left output gets silence
                                _dspScratch[rightIdx] += leftSample + rightSample;
                                break;
                                
                            case AudioChannel.Both:
                                // Normal stereo output
                                _dspScratch[leftIdx] += leftSample;
                                _dspScratch[rightIdx] += rightSample;
                                break;
                        }
                    }
                }
                else
                {
                    // Multi-channel (>2) - mix to all channels
                    for (int i = 0; i < samples; i++)
                    {
                        _dspScratch[i] += _frequencyMixBuffer[i] * volume;
                    }
                }
            }

            // Clamp final output to prevent clipping
            for (int i = 0; i < samples; i++)
            {
                _dspScratch[i] = Math.Clamp(_dspScratch[i], -1f, 1f);
            }

            // Copy result to output
            Marshal.Copy(_dspScratch, 0, bufferPtr, samples);
            
            // Cleanup stopped streams (outside the main loop to avoid modifying during iteration)
            if (streamsToRemove.Count > 0)
            {
                lock (_lock)
                {
                    foreach (var streamId in streamsToRemove)
                    {
                        StopStreamInternal(streamId);
                    }
                }
            }
        };

        // Attach DSP to master stream
        Bass.ChannelSetDSP(_masterStream, _dspProc, IntPtr.Zero, 0);
        Bass.ChannelPlay(_masterStream);
    }

    private void EnsureBufferSize(int samples)
    {
        if (_dspScratch.Length < samples)
        {
            _dspScratch = new float[samples];
            _mixBuffer1 = new float[samples];
            _mixBuffer2 = new float[samples];
            _frequencyMixBuffer = new float[samples];
            _noiseBuffer = new float[samples];
        }
    }

    /// <summary>
    /// Stop all streams and cleanup
    /// </summary>
    public async Task StopAll()
    {
        List<string> streamIds;
        lock (_lock)
        {
            streamIds = _streams.Keys.ToList();
        }

        // Stop all streams (with their closing bursts)
        foreach (var streamId in streamIds)
        {
            await StopStream(streamId);
        }

        // Cleanup master stream
        if (_masterStream != 0)
        {
            Bass.ChannelStop(_masterStream);
            Bass.StreamFree(_masterStream);
            _masterStream = 0;
        }
    }

    /// <summary>
    /// Get list of currently active stream IDs
    /// </summary>
    public List<string> GetActiveStreams()
    {
        lock (_lock)
        {
            return _streams.Keys.ToList();
        }
    }

    /// <summary>
    /// Get streams on a specific frequency
    /// </summary>
    public List<string> GetStreamsOnFrequency(float frequencyMHz)
    {
        lock (_lock)
        {
            return _streams.Values
                .Where(s => Math.Abs(s.FrequencyMHz - frequencyMHz) < 0.001f)
                .Select(s => s.StreamId)
                .ToList();
        }
    }

    /// <summary>
    /// Get all active frequencies with transmissions
    /// </summary>
    public List<float> GetActiveFrequencies()
    {
        lock (_lock)
        {
            return _streams.Values
                .Select(s => s.FrequencyMHz)
                .Distinct()
                .OrderBy(f => f)
                .ToList();
        }
    }

    /// <summary>
    /// Get current parameters for a stream
    /// </summary>
    public AudioParams? GetStreamParams(string streamId)
    {
        lock (_lock)
        {
            if (_streams.TryGetValue(streamId, out var stream))
                return stream.CurrentParams;
            return null;
        }
    }

    /// <summary>
    /// Get the frequency a stream is transmitting on
    /// </summary>
    public float? GetStreamFrequency(string streamId)
    {
        lock (_lock)
        {
            if (_streams.TryGetValue(streamId, out var stream))
                return stream.FrequencyMHz;
            return null;
        }
    }
}