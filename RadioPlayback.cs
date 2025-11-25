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
        public int Channels { get; set; }
        public RadioEffect RadioEffect { get; set; }
        public RadioPreFilter RadioPreFilter { get; set; }
        public AudioParams CurrentParams { get; set; }
        public float[] Buffer { get; set; } = new float[8192];
    
        // Use int for Interlocked operations (0 = false, 1 = true)
        public int _transmissionActiveFlag = 0;
        public bool TransmissionActive 
        { 
            get => Interlocked.CompareExchange(ref _transmissionActiveFlag, 0, 0) == 1;
            set => Interlocked.Exchange(ref _transmissionActiveFlag, value ? 1 : 0);
        }

        public int _isStoppingFlag = 0;
        public bool IsStopping 
        { 
            get => Interlocked.CompareExchange(ref _isStoppingFlag, 0, 0) == 1;
            set => Interlocked.Exchange(ref _isStoppingFlag, value ? 1 : 0);
        }

        public int StoppingBurstSamplesLeft { get; set; }
    }

    // Per-frequency configuration
    private class FrequencyConfig
    {
        public float Volume { get; set; } = 1.0f;
        public AudioChannel AudioChannel { get; set; } = AudioChannel.Both;
        public Radiomixer Mixer { get; set; } = new Radiomixer();
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

    // Processing resources
    private float[] _dspScratch = new float[8192];
    private float[] _mixBuffer1 = new float[8192];
    private float[] _mixBuffer2 = new float[8192];
    private float[] _frequencyMixBuffer = new float[8192];
    private Random _rng = new Random();

    // Sample rate (set from first stream)
    private int _sampleRate = 48000;
    private int _channels = 2;

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
        Console.Out.WriteLine($"Starting stream with id {streamId}");
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

            // Update sample rate from first stream and create master output
            if (_streams.Count == 0)
            {
                _sampleRate = info.Frequency;
                _channels = info.Channels;

                // Now create master stream with correct sample rate
                CreateMasterStream();
            }

            // Create stream object
            var stream = new RadioStream
            {
                StreamId = streamId,
                FrequencyMHz = audioParams.RadioFrequencyMHz,
                BassStreamHandle = bassStream,
                Channels = info.Channels, // Store actual channel count
                RadioEffect = new RadioEffect(info.Frequency, info.Channels, audioParams),
                RadioPreFilter = new RadioPreFilter(info.Frequency),
                CurrentParams = audioParams,
                Buffer = new float[8192],
                TransmissionActive = false
            };

            _streams.Add(streamId, stream);

            // If this is the first stream, set up DSP and start playback
            if (_streams.Count == 1)
            {
                SetupDSPAndPlay();
            }

            // Don't trigger burst here - DSP callback will detect transmission start
            // Mark as inactive initially, DSP will detect when transmission becomes active
            stream.TransmissionActive = false;
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
            if (stream.CurrentParams.Gain > 0.03f || stream.TransmissionActive)
            {
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

        // If no more streams, stop and free master playback
        if (_streams.Count == 0 && _masterStream != 0)
        {
            Bass.ChannelStop(_masterStream);
            Bass.StreamFree(_masterStream);
            _masterStream = 0;
        }
    }

    /// <summary>
    /// Update RF parameters for a specific stream while it's playing
    /// DSP callback will automatically detect gain changes and trigger squelch bursts
    /// </summary>
    /// <param name="streamId">Stream identifier</param>
    /// <param name="newParams">New RF parameters (gain, distance, SNR, etc.)</param>
    public void UpdateStreamParams(string streamId, AudioParams newParams)
    {
        lock (_lock)
        {
            if (!_streams.TryGetValue(streamId, out var stream))
                return;

            // Update parameters - DSP callback will detect gain changes and trigger squelch bursts
            stream.CurrentParams = newParams;
            stream.RadioEffect.Params = newParams;
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

    private void CreateMasterStream()
    {
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
    }

    private void SetupDSPAndPlay()
    {
        Console.Out.WriteLine("SetupDSPAndPlay");
        
        int dspCallbackCount = 0; // Counter to track callbacks
        
        _dspProc = (handle, channel, bufferPtr, length, user) =>
        {
            int callbackId = Interlocked.Increment(ref dspCallbackCount);
            int threadId = Thread.CurrentThread.ManagedThreadId;
            Console.Out.WriteLine($"[DSP #{callbackId} Thread {threadId}] Callback START");
            
            int samples = length / sizeof(float);
            EnsureBufferSize(samples);

            // Get snapshot of all active streams
            List<RadioStream> activeStreams;
            Dictionary<float, FrequencyConfig> frequencySnapshot;

            lock (_lock)
            {
                activeStreams = _streams.Values.ToList();
                frequencySnapshot = new Dictionary<float, FrequencyConfig>(_frequencies);
                Console.Out.WriteLine($"[DSP #{callbackId}] Got {activeStreams.Count} streams");
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

            if (activeStreams.Count == 0)
            {
                Marshal.Copy(_dspScratch, 0, bufferPtr, samples);
                return;
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
                        streamsToRemove.Add(stream.StreamId);
                        continue;
                    }
                }
    
                bool isActive = stream.CurrentParams.Gain > 0.03f;
    
                // Atomic check-and-set: ONLY returns 0 once across all threads
                if (isActive && !stream.IsStopping)
                {
                    if (Interlocked.CompareExchange(ref stream._transmissionActiveFlag, 1, 0) == 0)
                    {
                        Console.Out.WriteLine($"[DSP #{callbackId}] Triggering burst on {stream.StreamId}");
                        stream.RadioEffect.TriggerSquelchBurst();
                    }
                }
    
                int bytesRead = Bass.ChannelGetData(stream.BassStreamHandle, stream.Buffer, length);
                if (bytesRead <= 0)
                    continue;

                int streamSamples = bytesRead / sizeof(float);

                if (isActive)
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

            // Group streams by frequency
            var streamsByFrequency = activeStreams
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
                    freqConfig = new FrequencyConfig();
                }

                // Skip if volume is zero (muted)
                if (freqConfig.Volume <= 0f)
                    continue;

                Array.Clear(_frequencyMixBuffer, 0, samples);

                if (streamsOnFreq.Count == 1)
                {
                    // Single stream on this frequency - direct copy
                    var stream = streamsOnFreq[0];
                    Array.Copy(stream.Buffer, _frequencyMixBuffer, Math.Min(samples, stream.Buffer.Length));
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
                        Array.Copy(sortedByGain[0].Buffer, _frequencyMixBuffer,
                            Math.Min(samples, sortedByGain[0].Buffer.Length));
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

                        // Copy to mixing buffers
                        int mixSamples = Math.Min(samples, Math.Min(primary.Buffer.Length, secondary.Buffer.Length));
                        Array.Copy(primary.Buffer, _mixBuffer1, mixSamples);
                        Array.Copy(secondary.Buffer, _mixBuffer2, mixSamples);

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

                // Assumes stereo output (2 channels)
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
                            _dspScratch[leftIdx] += leftSample;
                            // Right channel gets silence
                            break;

                        case AudioChannel.Right:
                            // Left channel gets silence
                            _dspScratch[rightIdx] += rightSample;
                            break;

                        case AudioChannel.Both:
                            _dspScratch[leftIdx] += leftSample;
                            _dspScratch[rightIdx] += rightSample;
                            break;
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
            Console.Out.WriteLine($"[DSP #{callbackId}] Callback END");
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