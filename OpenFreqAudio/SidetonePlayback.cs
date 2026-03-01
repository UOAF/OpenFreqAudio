using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using ManagedBass;
using Microsoft.Extensions.Logging;

namespace OpenFreqAudio;

/// <summary>
/// Dedicated low-latency sidetone playback on separate audio thread.
/// </summary>
public class SidetonePlayback : IDisposable
{
    private readonly ILogger<SidetonePlayback> _logger;
    private readonly object _lock = new();
    private readonly int _sampleRate;
    private readonly int _channels;

    // BASS audio stream handles
    private int _masterStream;
    private int _masterDspProcHandle;
    private DSPProcedure? _dspProc;

    // DSP buffers
    private float[] _dspScratch = Array.Empty<float>();
    private float[] _monoToStereoBuffer = Array.Empty<float>();
    private const int MaxBufferSize = 8192;

    // Active sidetone streams (typically just one, but supports multiple)
    private readonly Dictionary<string, SidetoneStream> _streams = new();

    // Reference to frequency configs (shared with main RadioPlayback)
    private readonly Func<int, RadioPlayback.RadioConfig?> _getFrequencyConfig;

    /// <summary>
    /// Individual sidetone stream
    /// </summary>
    private class SidetoneStream
    {
        public string StreamId { get; set; } = "";
        public int FrequencyKHz { get; set; }
        public int Channels { get; set; }
        public float Volume { get; set; }

        // Ultra-minimal ring buffer (just for jitter absorption)
        public float[] RingBuffer { get; set; } = Array.Empty<float>();
        private int _ringWritePos;
        private int _ringReadPos;
        private int _ringCount;
        private readonly object _ringLock = new();

        // Working buffer for DSP callback
        public float[] Buffer { get; set; } = new float[MaxBufferSize];

        public bool IsBuffering { get; set; } = true; // Start in buffering mode
        public int MinBufferFrames { get; set; } // Minimum frames before playback
        public DateTime LastAudioReceived { get; set; } = DateTime.UtcNow;
        public bool HasReceivedAudio { get; set; }

        // Logger reference for hot-path logging
        public ILogger? Logger { get; set; }

        public void EnsureRingBufferCapacity(int floats)
        {
            lock (_ringLock)
            {
                if (RingBuffer.Length < floats)
                {
                    RingBuffer = new float[floats];
                    _ringWritePos = _ringReadPos = _ringCount = 0;
                }
            }
        }

        /// <summary>
        /// Push audio into ring buffer (from microphone thread)
        /// </summary>
        public void PushToRing(float[] frames, int frameCount)
        {
            lock (_ringLock)
            {
                if (RingBuffer.Length == 0) return;

                int needed = frameCount * Channels;
                int available = RingBuffer.Length - _ringCount;

                // If incoming is larger than buffer, keep only last portion
                if (needed >= RingBuffer.Length)
                {
                    int start = frames.Length - RingBuffer.Length;
                    Array.Copy(frames, start, RingBuffer, 0, RingBuffer.Length);
                    _ringWritePos = 0;
                    _ringReadPos = 0;
                    _ringCount = RingBuffer.Length;
                    return;
                }

                // Drop oldest frames if needed
                while (available < needed)
                {
                    _ringReadPos = (_ringReadPos + Channels) % RingBuffer.Length;
                    _ringCount -= Channels;
                    available = RingBuffer.Length - _ringCount;
                }

                // Write new frames
                int src = 0;
                for (int f = 0; f < frameCount; f++)
                {
                    for (int c = 0; c < Channels; c++)
                    {
                        RingBuffer[_ringWritePos] = frames[src++];
                        _ringWritePos = (_ringWritePos + 1) % RingBuffer.Length;
                    }

                    _ringCount += Channels;
                }

                HasReceivedAudio = true;
                LastAudioReceived = DateTime.UtcNow;

                // Check if we've buffered enough to start playback
                if (IsBuffering && _ringCount >= MinBufferFrames * Channels)
                {
                    IsBuffering = false;
#if DEBUG
                    Logger?.LogDebug("Prebuffer complete! {FrameCount} frames (StreamId: {StreamId})",
                        _ringCount / Channels, StreamId);
#endif
                }
            }
        }

        /// <summary>
        /// Read frames from ring buffer (called by DSP thread)
        /// Returns frames read (not samples)
        /// </summary>
        public int ReadFromRing(float[] dest, int frameCount)
        {
            lock (_ringLock)
            {
                int availableFrames = _ringCount / Channels;
                float fillPercent = RingBuffer.Length > 0 ? (float)_ringCount / RingBuffer.Length * 100f : 0f;

                // PREBUFFERING LOGIC (similar to RadioPlayback)
                if (IsBuffering)
                {
                    // Exit buffering when we have enough frames
                    if (availableFrames >= MinBufferFrames)
                    {
                        IsBuffering = false;
#if DEBUG
                        Logger?.LogDebug(
                            "Buffering complete! {AvailableFrames} frames ({FillPercent:F1}% full) (StreamId: {StreamId})",
                            availableFrames, fillPercent, StreamId);
#endif
                    }
                    // Fallback: Exit buffering if stream stopped but we have substantial data
                    else if (HasReceivedAudio &&
                             availableFrames >= (MinBufferFrames * 60) / 100 &&
                             (DateTime.UtcNow - LastAudioReceived).TotalMilliseconds > 200)
                    {
                        IsBuffering = false;
#if DEBUG
                        Logger?.LogDebug(
                            "Buffering timeout! Using {AvailableFrames} frames ({FillPercent:F1}% full) (StreamId: {StreamId})",
                            availableFrames, fillPercent, StreamId);
#endif
                    }
                    else
                    {
                        // Still buffering - return silence
                        Array.Clear(dest, 0, frameCount * Channels);
                        return 0;
                    }
                }

                // Check for underrun
                if (availableFrames < frameCount)
                {
                    if (availableFrames == 0)
                    {
                        // Complete underrun - enter rebuffering if buffer is very low
                        if (fillPercent < 10f && HasReceivedAudio)
                        {
#if DEBUG
                            Logger?.LogWarning(
                                "UNDERRUN! Rebuffering... ({FillPercent:F1}% full) (StreamId: {StreamId})",
                                fillPercent, StreamId);
#endif
                            IsBuffering = true;
                            Array.Clear(dest, 0, frameCount * Channels);
                            return 0;
                        }

                        // No data available - return silence
                        Array.Clear(dest, 0, frameCount * Channels);
                        return 0;
                    }

                    // Partial underrun - log if severe
                    if (fillPercent < 25f)
                    {
#if DEBUG
                        Logger?.LogWarning(
                            "Low buffer: {AvailableFrames}/{RequestedFrames} frames ({FillPercent:F1}% full) (StreamId: {StreamId})",
                            availableFrames, frameCount, fillPercent, StreamId);
#endif
                    }
                }

                int framesToRead = Math.Min(frameCount, availableFrames);

                // Read frames
                int samplesRead = 0;
                for (int f = 0; f < framesToRead; f++)
                {
                    for (int c = 0; c < Channels; c++)
                    {
                        dest[samplesRead++] = RingBuffer[_ringReadPos];
                        _ringReadPos = (_ringReadPos + 1) % RingBuffer.Length;
                    }

                    _ringCount -= Channels;
                }

                // Fill remainder with silence if needed
                if (framesToRead < frameCount)
                {
                    int remainingSamples = (frameCount - framesToRead) * Channels;
                    Array.Clear(dest, samplesRead, remainingSamples);
                }

                return framesToRead;
            }
        }

        public void ClearRingBuffer()
        {
            lock (_ringLock)
            {
                _ringWritePos = _ringReadPos = _ringCount = 0;
                Array.Clear(RingBuffer, 0, RingBuffer.Length);
            }
        }
    }

    /// <summary>
    /// Create sidetone playback engine
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="sampleRate">Audio sample rate (typically 48000)</param>
    /// <param name="channels">Output channels (2 for stereo)</param>
    /// <param name="getFrequencyConfig">Callback to get frequency volume/channel settings</param>
    /// <param name="deviceIndex">BASS device index (-1 for default)</param>
    public SidetonePlayback(
        ILogger<SidetonePlayback> logger,
        int sampleRate,
        int channels,
        Func<int, RadioPlayback.RadioConfig?> getFrequencyConfig,
        int deviceIndex = -1)
    {
        _logger = logger;
        _sampleRate = sampleRate;
        _channels = channels;
        _getFrequencyConfig = getFrequencyConfig;

        // Initialize BASS device for sidetone
        // Use small update period for low latency
        if (!Bass.Init(deviceIndex, sampleRate, DeviceInitFlags.Default | DeviceInitFlags.Latency, IntPtr.Zero))
        {
            var error = Bass.LastError;
            if (error != Errors.Already) // OK if already initialized
            {
                throw new Exception($"Failed to initialize BASS for sidetone: {error}");
            }
        }

        // Set device for this thread
        if (deviceIndex != -1)
        {
            Bass.CurrentDevice = deviceIndex;
        }

        // Configure for low latency
        Bass.Configure(Configuration.UpdatePeriod, 10); // Was 5ms, now 10ms
        Bass.Configure(Configuration.UpdateThreads, 1);
        Bass.Configure(Configuration.PlaybackBufferLength, 20); // Was 10ms, now 20ms

        Bass.Configure(Configuration.RecordingBufferLength, 20); // ADD THIS LINE

        _logger.LogInformation("Initialized: {SampleRate}Hz, {Channels}ch, device={DeviceIndex}",
            sampleRate, channels, Bass.CurrentDevice);
        StartMasterStream();
    }

    /// <summary>
    /// Create a sidetone stream for a specific frequency
    /// </summary>
    /// <param name="streamId">Unique identifier</param>
    /// <param name="frequencyKHz">Radio frequency (for volume/channel routing)</param>
    /// <param name="channels">Audio channels (1=mono, 2=stereo)</param>
    /// <param name="volume">Base volume multiplier (0.0-1.0)</param>
    /// <param name="bufferMs">Ring buffer size in milliseconds (0 for no buffering)</param>
    public void CreateStream(
        string streamId,
        int frequencyKHz,
        int channels,
        float volume = 0.4f,
        int bufferMs = 10)
    {
        lock (_lock)
        {
            if (_streams.ContainsKey(streamId))
            {
                _logger.LogWarning("Stream '{StreamId}' already exists", streamId);
                return;
            }

            var stream = new SidetoneStream
            {
                StreamId = streamId,
                FrequencyKHz = frequencyKHz,
                Channels = channels,
                Volume = volume,
                Buffer = new float[MaxBufferSize],
                Logger = _logger
            };

            // Setup ring buffer
            if (bufferMs > 0)
            {
                int ringFrames = (_sampleRate * bufferMs) / 1000;
                int ringCapacity = ringFrames * Math.Max(1, channels);
                stream.EnsureRingBufferCapacity(ringCapacity);

                // Set prebuffer target: Fill 50% of buffer before starting playback
                // This prevents immediate underruns while still keeping latency low
                stream.MinBufferFrames = ringFrames / 4;
                stream.IsBuffering = true;

                _logger.LogInformation("Stream '{StreamId}' on {Frequency:F3} MHz", streamId, frequencyKHz / 1000.0);
                _logger.LogInformation("  Buffer: {BufferMs}ms ({RingFrames} frames)", bufferMs, ringFrames);
                _logger.LogInformation("  Prebuffer: {MinBufferFrames} frames ({PrebufferMs}ms)",
                    stream.MinBufferFrames, bufferMs / 2);
            }
            else
            {
                // Zero buffer mode - no prebuffering
                stream.EnsureRingBufferCapacity(MaxBufferSize);
                stream.MinBufferFrames = 0;
                stream.IsBuffering = false;
                _logger.LogInformation("Stream '{StreamId}' - ZERO BUFFER MODE", streamId);
            }

            _streams.Add(streamId, stream);
        }
    }

    /// <summary>
    /// Push audio data to a sidetone stream (typically from microphone)
    /// </summary>
    /// <param name="streamId">Stream identifier</param>
    /// <param name="audioData">PCM audio data (16-bit little-endian)</param>
    public void PushAudioData(string streamId, byte[] audioData)
    {
        lock (_lock)
        {
            if (!_streams.TryGetValue(streamId, out var stream))
            {
                _logger.LogWarning("Stream '{StreamId}' not found", streamId);
                return;
            }

            // Convert 16-bit PCM to float
            int bytesPerSample = 2;
            int frameBytes = bytesPerSample * stream.Channels;
            if (frameBytes == 0) return;

            int frames = audioData.Length / frameBytes;
            if (frames == 0) return;

            float[] floatFrames = new float[frames * stream.Channels];

            for (int i = 0, o = 0; i < audioData.Length; i += 2)
            {
                short s = (short)(audioData[i] | (audioData[i + 1] << 8));
                floatFrames[o++] = s / 32768f;
            }

            // Push to ring buffer
            stream.PushToRing(floatFrames, frames);
        }
    }

    /// <summary>
    /// Stop and remove a sidetone stream
    /// </summary>
    public void StopStream(string streamId)
    {
        lock (_lock)
        {
            if (_streams.TryGetValue(streamId, out var stream))
            {
                stream.ClearRingBuffer();
                _streams.Remove(streamId);
                _logger.LogInformation("Stopped stream '{StreamId}'", streamId);
            }
        }
    }

    /// <summary>
    /// Get active sidetone stream IDs
    /// </summary>
    public List<string> GetActiveStreams()
    {
        lock (_lock)
        {
            return _streams.Keys.ToList();
        }
    }

    private void StartMasterStream()
    {
        // Create dummy stream (BASS requirement)
        StreamProcedure streamProc = (_, buffer, length, _) =>
        {
            if (buffer != IntPtr.Zero)
            {
                unsafe
                {
                    float* ptr = (float*)buffer;
                    int samples = length / sizeof(float);
                    for (int i = 0; i < samples; i++) ptr[i] = 0f;
                }
            }

            return length;
        };

        _masterStream = Bass.CreateStream(_sampleRate, _channels, BassFlags.Float, streamProc, IntPtr.Zero);
        if (_masterStream == 0)
            throw new Exception($"BASS error creating sidetone master stream: {Bass.LastError}");

        SetupDSPAndPlay();
    }

    private void SetupDSPAndPlay()
    {
        _dspProc = (_, _, bufferPtr, length, _) =>
        {
            int samples = length / sizeof(float);

            // Ensure scratch buffer is large enough
            if (samples > _dspScratch.Length)
            {
                lock (_lock)
                {
                    _dspScratch = new float[samples];
                    _monoToStereoBuffer = new float[samples];
                }
            }

            // Clear output buffer
            Array.Clear(_dspScratch, 0, samples);
            int outputFrames = samples / _channels;

            // Snapshot active streams
            List<SidetoneStream> activeStreams;
            lock (_lock)
            {
                activeStreams = _streams.Values.ToList();
            }

            // Process each sidetone stream
            foreach (var stream in activeStreams)
            {
                // Read audio from ring buffer
                int framesRead = stream.ReadFromRing(stream.Buffer, outputFrames);
                if (framesRead == 0)
                {
                    continue;
                }

                // Get frequency configuration for volume and channel routing
                var freqConfig = _getFrequencyConfig(stream.FrequencyKHz);
                if (freqConfig == null) continue;

                // Calculate effective volume
                float effectiveVolume = stream.Volume * freqConfig.Volume;
                var audioChannel = freqConfig.AudioChannel;

                // Convert stream format to output format and mix
                if (stream.Channels == 1 && _channels == 2)
                {
                    // Mono → Stereo with volume and channel routing
                    for (int frame = 0; frame < framesRead; frame++)
                    {
                        float sample = stream.Buffer[frame] * effectiveVolume;
                        int leftIdx = frame * 2;
                        int rightIdx = leftIdx + 1;

                        switch (audioChannel)
                        {
                            case RadioPlayback.AudioChannel.Left:
                                _dspScratch[leftIdx] += sample;
                                break;
                            case RadioPlayback.AudioChannel.Right:
                                _dspScratch[rightIdx] += sample;
                                break;
                            case RadioPlayback.AudioChannel.Both:
                                _dspScratch[leftIdx] += sample;
                                _dspScratch[rightIdx] += sample;
                                break;
                        }
                    }
                }
                else if (stream.Channels == 2 && _channels == 2)
                {
                    // Stereo → Stereo with volume and channel routing
                    for (int frame = 0; frame < framesRead; frame++)
                    {
                        int srcIdx = frame * 2;
                        float left = stream.Buffer[srcIdx] * effectiveVolume;
                        float right = stream.Buffer[srcIdx + 1] * effectiveVolume;

                        int leftIdx = frame * 2;
                        int rightIdx = leftIdx + 1;

                        switch (audioChannel)
                        {
                            case RadioPlayback.AudioChannel.Left:
                                _dspScratch[leftIdx] += left + right;
                                break;
                            case RadioPlayback.AudioChannel.Right:
                                _dspScratch[rightIdx] += left + right;
                                break;
                            case RadioPlayback.AudioChannel.Both:
                                _dspScratch[leftIdx] += left;
                                _dspScratch[rightIdx] += right;
                                break;
                        }
                    }
                }
                else if (stream.Channels == 1 && _channels == 1)
                {
                    // Mono → Mono
                    for (int i = 0; i < framesRead; i++)
                    {
                        _dspScratch[i] += stream.Buffer[i] * effectiveVolume;
                    }
                }
            }

            // Clamp and copy to output
            for (int i = 0; i < samples; i++)
            {
                _dspScratch[i] = Math.Clamp(_dspScratch[i], -1f, 1f);
            }

            Marshal.Copy(_dspScratch, 0, bufferPtr, samples);
        };

        _masterDspProcHandle = Bass.ChannelSetDSP(_masterStream, _dspProc, IntPtr.Zero, 0);
        Bass.ChannelPlay(_masterStream);

        _logger.LogInformation("DSP callback started, playback active");
    }

    private void StopMasterStream()
    {
        int streamToStop = 0;
        int dspHandleToRemove = 0;

        lock (_lock)
        {
            streamToStop = _masterStream;
            dspHandleToRemove = _masterDspProcHandle;
            _masterStream = 0;
            _masterDspProcHandle = 0;
        }

        if (streamToStop != 0)
        {
            if (dspHandleToRemove != 0)
            {
                Bass.ChannelRemoveDSP(streamToStop, dspHandleToRemove);
            }

            Bass.ChannelStop(streamToStop);
            Bass.StreamFree(streamToStop);
        }
    }

    public void Dispose()
    {
        _logger.LogInformation("Disposing...");

        // Stop all streams
        lock (_lock)
        {
            foreach (var stream in _streams.Values)
            {
                stream.ClearRingBuffer();
            }

            _streams.Clear();
        }

        // Stop master stream
        StopMasterStream();

        // Free BASS device (if we initialized it)
        try
        {
            var currentDevice = Bass.CurrentDevice;
            if (currentDevice != -1)
            {
                Bass.Free();
                _logger.LogInformation("BASS device freed");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error freeing BASS");
        }

        _logger.LogInformation("Disposed");
    }
}