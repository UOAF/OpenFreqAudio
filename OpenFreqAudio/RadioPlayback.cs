using System.Runtime.InteropServices;
using ManagedBass;

// ReSharper disable InconsistentNaming

namespace OpenFreqAudio;

/// <summary>
/// RadioPlayback
/// - Push (WebRTC) streams use a per-stream circular float ring buffer.
/// - File-based streams are decoded by a background reader task that fills the same ring buffer,
/// - PushAudioData converts incoming bytes (16-bit PCM or 32-bit float) into floats and writes into the ring buffer.
/// </summary>
public class RadioPlayback
{
    private class RadioStream
    {
        public string StreamId { get; set; } = "";
        public double FrequencyMHz { get; set; }
        public int BassStreamHandle { get; set; } // 0 for push streams
        public int Channels { get; set; } // 1=mono,2=stereo
        public bool IsPush { get; set; } // true for WebRTC / pushed audio
        public required RadioEffect RadioEffect { get; set; }
        public required RadioPreFilter RadioPreFilter { get; set; }
        public required AudioParams CurrentParams { get; set; }

        // For decoded or pulled audio we reuse Buffer as a temporary buffer
        public float[] Buffer { get; set; } = new float[8192];

        // Ring buffer used by both push streams and file-reader task
        public float[] RingBuffer { get; set; } = Array.Empty<float>();
        private int _ringWritePos;
        private int _ringReadPos;
        private int _ringCount; // number of floats in buffer
        private readonly object _ringLock = new();

        // For file-based streams we run a reader task that decodes and pushes into the ring
        public CancellationTokenSource? FileReaderCts { get; set; }
        public Task? FileReaderTask { get; set; }

        public bool HasReceivedAudio { get; set; }
        public DateTime LastAudioReceived { get; set; } = DateTime.MinValue;
        public int ValidSamples { get; set; }

        // Transmission state (separate from stream lifecycle)
        public bool IsTransmitting { get; set; }
        public DateTime TransmissionStartTime { get; set; }
        public DateTime TransmissionEndTime { get; set; }
        public DateTime LastPacketReceived { get; set; } = DateTime.UtcNow;
        public TimeSpan PeerTimeoutThreshold { get; set; } = TimeSpan.FromSeconds(5);

        // Prebuffering state
        public bool IsBuffering { get; set; } = true;
        public int MinBufferFrames { get; set; } // Minimum frames before playback starts

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

        // Get current ring buffer fill level (thread-safe)
        public (int count, int capacity) GetRingBufferFillLevel()
        {
            lock (_ringLock)
            {
                return (_ringCount, RingBuffer.Length);
            }
        }

        // Push raw interleaved float frames into ring buffer.
        // frames.Length == frameCount * Channels
        public void PushToRing(float[] frames, int frameCount)
        {
            lock (_ringLock)
            {
                if (RingBuffer.Length == 0)
                    return;

                int needed = frameCount * Channels; // floats
                int available = RingBuffer.Length - _ringCount;

                // If incoming chunk is larger than ring buffer, truncate oldest entirely
                if (needed >= RingBuffer.Length)
                {
                    // keep only the last portion that fits
                    int start = frames.Length - RingBuffer.Length;
                    Array.Copy(frames, start, RingBuffer, 0, RingBuffer.Length);
                    _ringWritePos = 0;
                    _ringReadPos = 0;
                    _ringCount = RingBuffer.Length;
                    HasReceivedAudio = true;
                    LastAudioReceived = DateTime.UtcNow;
                    return;
                }

                // If not enough space, drop oldest frames until there's room
                int framesDropped = 0;
                while (available < needed)
                {
                    // drop one frame (Channels floats)
                    _ringReadPos = (_ringReadPos + Channels) % RingBuffer.Length;
                    _ringCount -= Channels;
                    available = RingBuffer.Length - _ringCount;
                    framesDropped++;
                }

                // Only log significant overflows (>1000 frames = ~23ms at 44.1kHz)
                // Small overflows during RadioEffect processing are normal
                if (framesDropped > 1000)
                {
                    float fillPercent = (float)_ringCount / RingBuffer.Length * 100f;
                    Console.WriteLine(
                        $"[PushToRing:{StreamId}] OVERFLOW! Dropped {framesDropped} frames to make room. Buffer was {fillPercent:F1}% full");
                }

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
                    Console.WriteLine(
                        $"[PushToRing:{StreamId}] Buffering complete! {_ringCount / Channels} frames buffered ({(float)_ringCount / RingBuffer.Length * 100f:F1}% full)");
                }
            }
        }

        // Read up to frameCount frames from ring into dest in the stream's NATIVE channel format
        // Channel conversion will happen later during mixing
        // Returns frames read (in frames, not samples)
        public int ReadFromRing(float[] dest, int frameCount)
        {
            lock (_ringLock)
            {
                int availableFrames = _ringCount / Channels;
                float fillPercent = (float)_ringCount / RingBuffer.Length * 100f;

                // If still buffering, check if we should exit buffering mode
                if (IsBuffering)
                {
                    // Primary exit condition: reached target buffer level
                    if (availableFrames >= MinBufferFrames)
                    {
                        IsBuffering = false;
                        Console.WriteLine(
                            $"[ReadFromRing:{StreamId}] Buffering complete! {availableFrames} frames buffered ({fillPercent:F1}% full)");
                    }
                    // Fallback exit condition: stream stopped but we have substantial data (>60% of target)
                    // Wait 200ms after last packet to ensure stream truly stopped
                    else if (HasReceivedAudio &&
                             availableFrames >= (MinBufferFrames * 60) / 100 &&
                             (DateTime.UtcNow - LastAudioReceived).TotalMilliseconds > 200)
                    {
                        IsBuffering = false;
                        Console.WriteLine(
                            $"[ReadFromRing:{StreamId}] Buffering timeout! Stream inactive, using {availableFrames} frames ({fillPercent:F1}% full)");
                    }
                    // Still buffering - log progress and return silence
                    else
                    {
                        // Log buffering progress periodically (not every call to avoid spam)
                        if (availableFrames % 480 == 0 || availableFrames == 0)
                        {
                            // Console.WriteLine($"[DSP:{StreamId}] Buffering... {availableFrames}/{MinBufferFrames} frames ({fillPercent:F1}%)");
                        }

                        // Return silence while buffering
                        int destSamples = frameCount * Channels;
                        Array.Clear(dest, 0, destSamples);
                        return 0;
                    }
                }

                // Check buffer health and enter rebuffering if critically low
                // With 85% target and ~80% DSP consumption, steady state is 75-85%
                // After one DSP call: 85% + packets - 80% = ~5-15% depending on timing
                // Rebuffer only at < 5% to avoid false triggers during normal operation
                if (availableFrames < frameCount && HasReceivedAudio)
                {
                    if (fillPercent < 25f)
                    {
                        Console.WriteLine(
                            $"[ReadFromRing:{StreamId}] UNDERRUN! Requested {frameCount} frames, only {availableFrames} available ({fillPercent:F1}% full, {_ringCount}/{RingBuffer.Length})");
                    }

                    // Enter rebuffering mode if buffer critically low (< 5%)
                    if (fillPercent < 5f && !IsBuffering)
                    {
                        IsBuffering = true;
                        Console.WriteLine(
                            $"[ReadFromRing:{StreamId}] Buffer critically low ({fillPercent:F1}%) - entering rebuffering mode");
                    }
                }

                int toReadFrames = Math.Min(availableFrames, frameCount);
                int samplesToRead = toReadFrames * Channels;

                // Simple copy - keep native channel format
                for (int i = 0; i < samplesToRead; i++)
                {
                    dest[i] = RingBuffer[_ringReadPos];
                    _ringReadPos = (_ringReadPos + 1) % RingBuffer.Length;
                }

                _ringCount -= samplesToRead;

                // Clear remainder
                int destSamples2 = frameCount * Channels;
                if (samplesToRead < destSamples2)
                    Array.Clear(dest, samplesToRead, destSamples2 - samplesToRead);

                return toReadFrames;
            }
        }

        public void StopFileReader()
        {
            try
            {
                FileReaderCts?.Cancel();
                FileReaderTask?.Wait(100);
            }
            catch
            {
                // we don't care for any errors here
            }
            finally
            {
                FileReaderCts = null;
                FileReaderTask = null;
            }
        }

        // Clear ring buffer (used for timeout/crash cleanup)
        public void ClearRingBuffer()
        {
            lock (_ringLock)
            {
                _ringCount = 0;
                _ringReadPos = 0;
                _ringWritePos = 0;
            }
        }
    }

    private class FrequencyConfig
    {
        // ReSharper disable UnusedAutoPropertyAccessor.Local
        public float Volume { get; set; } = 1.0f;
        public AudioChannel AudioChannel { get; set; } = AudioChannel.Both;
        public Radiomixer Mixer { get; set; } = new();
        public bool IsTuned { get; set; }
        public BackgroundNoiseGenerator? NoiseGenerator { get; set; }
        public float NoiseFadeGain { get; set; }
        public double MinimumGain { get; set; }
        public bool WasHearableLastFrame { get; set; }

        // Physics-based default - will be set to noise floor when frequency is tuned
        public float DefaultSquelchThreshold { get; set; } = 0.1f; // Fallback if not yet calculated
        public bool IsNoiseMuted { get; set; }
        public float SquelchLevel { get; set; }
        // ReSharper restore UnusedAutoPropertyAccessor.Local

        public SquelchBurstGenerator? SquelchBurst { get; set; }
        public bool WasSquelchOpen { get; set; }
    }

    public enum AudioChannel
    {
        Left,
        Right,
        Both
    }

    private readonly Dictionary<string, RadioStream> _streams = new();
    private readonly Dictionary<double, FrequencyConfig> _frequencies = new();
    private readonly object _lock = new();

    private int _masterStream;
    private DSPProcedure? _dspProc;

    private const int MaxBufferSize = 24576;
    private float[] _dspScratch = new float[MaxBufferSize];
    private float[] _mixBuffer1 = new float[MaxBufferSize];
    private float[] _mixBuffer2 = new float[MaxBufferSize];
    private float[] _frequencyMixBuffer = new float[MaxBufferSize];
    private float[] _noiseBuffer = new float[MaxBufferSize];

    private int _sampleRate = 48000;

    private int _channels = 2;

    private const int NoiseFadeSamples = 2400;

    private static bool _bassInitialized;
    private static readonly object _bassInitLock = new();
    private bool _timeoutMonitoringStarted;
    private int _masterDspProcHandle;

    public RadioPlayback(bool skipBassInitialization)
    {
        lock (_bassInitLock)
        {
            if (skipBassInitialization)
                _bassInitialized = true;
            else if (!_bassInitialized)
            {
                if (!Bass.Init())
                    throw new Exception("Failed to initialize BASS.");
                _bassInitialized = true;
            }

            // Configure BASS for low-latency operation
            Bass.Configure(Configuration.UpdatePeriod, 5);
            Bass.Configure(Configuration.PlaybackBufferLength, 40);
            Bass.Configure(Configuration.DeviceBufferLength, 10);
            Bass.Configure(Configuration.UpdateThreads, 2);
        }

        Radiomixer.LoadSteppedOnSample();

        // Start peer timeout monitoring
        StartPeerTimeoutMonitoring();
    }

    public void StartStream(string streamId, string filePath, AudioParams audioParams)
    {
        lock (_lock)
        {
            if (_streams.ContainsKey(streamId)) StopStreamInternal(streamId);
            if (!_frequencies.ContainsKey(audioParams.RadioFrequencyMHz))
                _frequencies[audioParams.RadioFrequencyMHz] = new FrequencyConfig();

            // Create BASS decode stream (float)
            int bassStream = Bass.CreateStream(filePath, 0, 0, BassFlags.Loop | BassFlags.Float | BassFlags.Decode);
            if (bassStream == 0) throw new Exception($"BASS error creating stream '{streamId}': {Bass.LastError}");

            var info = Bass.ChannelGetInfo(bassStream);

            Console.WriteLine($"[StartStream] File info: SampleRate={info.Frequency}, Channels={info.Channels}");

            if (_masterStream == 0)
            {
                _sampleRate = info.Frequency;
                _channels = 2;
                Console.WriteLine(
                    $"[StartStream] Creating master stream: SampleRate={_sampleRate}, Channels={_channels}");
                StartMasterStream();
            }
            else if (_sampleRate != info.Frequency)
            {
                Console.WriteLine(
                    $"[StartStream] Sample rate mismatch! File={info.Frequency}, Master={_sampleRate}. Recreating master stream.");
                StopMasterStream();
                _sampleRate = info.Frequency;
                _channels = 2;
                StartMasterStream();
            }
            else
            {
                Console.WriteLine(
                    $"[StartStream] Using existing master stream: SampleRate={_sampleRate}, Channels={_channels}");
            }

            var stream = new RadioStream
            {
                StreamId = streamId,
                FrequencyMHz = audioParams.RadioFrequencyMHz,
                BassStreamHandle = bassStream,
                Channels = info.Channels,
                IsPush = false,
                RadioEffect = new RadioEffect(info.Frequency, info.Channels, audioParams),
                RadioPreFilter = new RadioPreFilter(info.Frequency),
                CurrentParams = audioParams,
                Buffer = new float[MaxBufferSize],
                IsTransmitting = true
            };


            // Allocate a ring buffer (5 seconds worth of audio)
            int ringFrames = info.Frequency * 5;
            stream.EnsureRingBufferCapacity(ringFrames * Math.Max(1, stream.Channels));

            // Start a background task that pulls decoded floats from the BASS decode stream and pushes them into the ring buffer.
            stream.FileReaderCts = new CancellationTokenSource();
            var token = stream.FileReaderCts.Token;
            int streamSampleRate = info.Frequency; // Capture for use in lambda
            stream.FileReaderTask = Task.Run(() =>
            {
                try
                {
                    // Read in larger chunks for better throughput (8192 frames = ~185ms at 44.1kHz)
                    int chunkFrames = 8192;
                    int chunkSamples = chunkFrames * stream.Channels;
                    float[] readBuffer = new float[chunkSamples];
                    int consecutiveNoData = 0;
                    int totalFramesRead = 0;

                    while (!token.IsCancellationRequested)
                    {
                        int bytesRequested = chunkSamples * sizeof(float);
                        int bytesRead = Bass.ChannelGetData(bassStream, readBuffer, bytesRequested);

                        if (bytesRead <= 0)
                        {
                            // If we consistently get no data, sleep briefly to avoid CPU spin
                            consecutiveNoData++;
                            if (consecutiveNoData > 10)
                            {
                                Thread.Sleep(1);
                            }

                            if (consecutiveNoData == 1)
                            {
                                Console.WriteLine(
                                    $"[FileReader:{streamId}] Reached end of stream after reading {totalFramesRead} total frames ({(float)totalFramesRead / streamSampleRate:F2}s) at {DateTime.Now:HH:mm:ss.fff}");
                            }

                            continue;
                        }

                        consecutiveNoData = 0;

                        int samplesRead = bytesRead / sizeof(float);
                        int framesRead = samplesRead / stream.Channels;
                        totalFramesRead += framesRead;

                        if (framesRead > 0)
                        {
                            // If fewer samples returned than buffer, copy to a trimmed array
                            if (samplesRead != readBuffer.Length)
                            {
                                float[] tmp = new float[samplesRead];
                                Array.Copy(readBuffer, tmp, samplesRead);
                                stream.PushToRing(tmp, framesRead);
                            }
                            else
                            {
                                stream.PushToRing(readBuffer, framesRead);
                            }

                            // Throttle reading to prevent flooding the ring buffer
                            // Dynamically adjust sleep time based on how full the buffer is

                            // Check current buffer fill level
                            var (currentFill, bufferCapacity) = stream.GetRingBufferFillLevel();

                            float fillPercent = (float)currentFill / bufferCapacity;

                            // Calculate base sleep time (80% of audio duration)
                            int baseSleepMs = (int)((float)framesRead / streamSampleRate * 1000 * 0.8);

                            // Adjust sleep based on buffer fill level:
                            // - If buffer is >70% full, sleep extra to let DSP catch up
                            // - If buffer is <30% full, skip sleep to fill faster
                            // - Otherwise, use base sleep time
                            int adjustedSleepMs;
                            if (fillPercent > 0.70f)
                            {
                                // Buffer getting full - sleep longer (up to 2x base)
                                float multiplier = 1.0f + (fillPercent - 0.70f) / 0.30f; // 1.0 to 2.0
                                adjustedSleepMs = (int)(baseSleepMs * multiplier);
                            }
                            else if (fillPercent < 0.30f)
                            {
                                // Buffer running low - sleep less (down to 50% of base)
                                float multiplier = 0.5f + (fillPercent / 0.30f) * 0.5f; // 0.5 to 1.0
                                adjustedSleepMs = (int)(baseSleepMs * multiplier);
                            }
                            else
                            {
                                // Buffer in good range - use base sleep
                                adjustedSleepMs = baseSleepMs;
                            }

                            if (adjustedSleepMs > 0)
                            {
                                Thread.Sleep(adjustedSleepMs);
                            }
                        }
                    }

                    Console.WriteLine($"[FileReader:{streamId}] Task cancelled, exiting");
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine($"[FileReader:{streamId}] Task cancelled");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FileReader] Exception for stream {streamId}: {ex}");
                }
            }, token);

            // Apply frequency's default squelch threshold to the new stream
            if (_frequencies.TryGetValue(audioParams.RadioFrequencyMHz, out var freqConfig))
            {
                stream.RadioEffect.SetSquelchThreshold(freqConfig.DefaultSquelchThreshold);
            }
            else
            {
                // Frequency not tuned yet - calculate physics-based default
                float noiseFloor = FastPathAudioSim.CalculateNoiseFloorAmplitude(audioParams.RadioFrequencyMHz);
                stream.RadioEffect.SetSquelchThreshold(noiseFloor);
            }

            _streams.Add(streamId, stream);

            Console.WriteLine(
                $"[StartStream] Added file stream {streamId}: Freq={audioParams.RadioFrequencyMHz}, Gain={audioParams.Gain}, Channels={info.Channels}, FileRate={info.Frequency}Hz, MasterRate={_sampleRate}Hz, RingBuffer={stream.RingBuffer.Length} floats");

            if (info.Frequency != _sampleRate)
            {
                Console.WriteLine(
                    $"[StartStream] WARNING: Sample rate mismatch! File={info.Frequency}Hz, Master={_sampleRate}Hz - this will cause timing issues!");
            }
        }
    }

    public void StartPushStream(string streamId, int sampleRate, int channels, AudioParams audioParams)
    {
        lock (_lock)
        {
            if (_streams.ContainsKey(streamId)) return;
            if (!_frequencies.ContainsKey(audioParams.RadioFrequencyMHz))
                _frequencies[audioParams.RadioFrequencyMHz] = new FrequencyConfig();

            if (_masterStream == 0)
            {
                _sampleRate = sampleRate;
                _channels = 2;
                StartMasterStream();
            }
            else if (_sampleRate != sampleRate)
            {
                StopMasterStream();
                _sampleRate = sampleRate;
                _channels = 2;
                StartMasterStream();
            }

            var stream = new RadioStream
            {
                StreamId = streamId,
                FrequencyMHz = audioParams.RadioFrequencyMHz,
                BassStreamHandle = 0,
                Channels = channels,
                IsPush = true,
                RadioEffect = new RadioEffect(sampleRate, channels, audioParams),
                RadioPreFilter = new RadioPreFilter(sampleRate),
                CurrentParams = audioParams,
                Buffer = new float[MaxBufferSize],
            };

            int ringFrames = (sampleRate * 60) / 1000; // 60ms
            int minBufferFrames = (sampleRate * 20) / 1000; // 20ms
            int ringCapacity = ringFrames * Math.Max(1, channels);
            stream.EnsureRingBufferCapacity(ringCapacity);
            stream.MinBufferFrames = minBufferFrames;
            stream.IsBuffering = true;

            if (_frequencies.TryGetValue(audioParams.RadioFrequencyMHz, out var freqConfig))
            {
                stream.RadioEffect.SetSquelchThreshold(freqConfig.DefaultSquelchThreshold);
            }
            else
            {
                // Frequency not tuned yet - calculate physics-based default
                float noiseFloor = FastPathAudioSim.CalculateNoiseFloorAmplitude(audioParams.RadioFrequencyMHz);
                stream.RadioEffect.SetSquelchThreshold(noiseFloor);
            }

            Console.WriteLine($"[StartPushStream] Stream '{streamId}' on {audioParams.RadioFrequencyMHz} MHz");
            Console.WriteLine($"[StartPushStream]   SampleRate={sampleRate}, Channels={channels}");
            Console.WriteLine(
                $"[StartPushStream]   RingBuffer: {ringFrames} frames × {Math.Max(1, channels)} ch = {ringCapacity} samples ({(float)ringFrames / sampleRate:F1}s)");

            _streams.Add(streamId, stream);
        }
    }

    // Accepts raw PCM bytes from WebRTC
    public bool PushAudioData(string streamId, byte[] audioData, bool startMarker = false, bool endMarker = false)
    {
        lock (_lock)
        {
            if (!_streams.TryGetValue(streamId, out var stream))
            {
                Console.WriteLine($"[PushAudioData] Stream '{streamId}' not found");
                return false;
            }

            if (!stream.IsPush)
            {
                Console.WriteLine($"[PushAudioData] Stream '{streamId}' is not a push stream");
                return false;
            }

            // Update packet receipt timestamp for timeout detection
            stream.LastPacketReceived = DateTime.UtcNow;

            // Handle transmission start marker
            if (startMarker && !stream.IsTransmitting)
            {
                stream.IsTransmitting = true;
                stream.TransmissionStartTime = DateTime.UtcNow;
                stream.IsBuffering = true; // Start buffering for this transmission
                Console.WriteLine($"[PushAudioData:{streamId}] Transmission START (marker)");
            }

            // Sanity check: both markers set (shouldn't happen but handle gracefully)
            if (startMarker && endMarker)
            {
                Console.WriteLine($"[PushAudioData:{streamId}] WARNING: Both start and end markers set!");
            }

            int bytesPerSample = 2; // assume 16-bit PCM
            int frameBytes = bytesPerSample * stream.Channels;
            if (frameBytes == 0) return false;
            int frames = audioData.Length / frameBytes;
            if (frames == 0) return false;

            float[] floatFrames = new float[frames * stream.Channels];

            // 16-bit PCM little-endian
            for (int i = 0, o = 0; i < audioData.Length; i += 2)
            {
                short s = (short)(audioData[i] | (audioData[i + 1] << 8));
                floatFrames[o++] = s / 32768f;
            }


            // Push into ring buffer
            stream.PushToRing(floatFrames, frames);

            // Get buffer status after push
            var (fillCount, capacity) = stream.GetRingBufferFillLevel();
#if DEBUG
            float fillPercent = (float)fillCount / capacity * 100f;
            Console.WriteLine(
                $"[PushAudioData:{streamId}] Pushed {frames} frames ({audioData.Length} bytes, {bytesPerSample * 8}-bit), buffer now {fillPercent:F1}% full ({fillCount}/{capacity})");
#endif

            // Handle transmission end marker
            // Process AFTER pushing audio so this final packet's audio is included
            if (endMarker && stream.IsTransmitting)
            {
                var duration = DateTime.UtcNow - stream.TransmissionStartTime;
                stream.IsTransmitting = false;
                stream.TransmissionEndTime = DateTime.UtcNow;
                Console.WriteLine(
                    $"[PushAudioData:{streamId}] Transmission END (marker) - duration: {duration.TotalSeconds:F2}s");
            }

            return true;
        }
    }

    public async Task StopStream(string streamId)
    {
        await Task.Run(() =>
        {
            lock (_lock)
            {
                StopStreamInternal(streamId);
            }
        });
    }

    private void StopStreamInternal(string streamId)
    {
        if (!_streams.TryGetValue(streamId, out var stream)) return;

        // Stop file reader if any
        if (!stream.IsPush)
        {
            stream.StopFileReader();
        }

        if (!stream.IsPush && stream.BassStreamHandle != 0)
        {
            Bass.StreamFree(stream.BassStreamHandle);
        }

        _streams.Remove(streamId);

        bool hasTuned = _frequencies.Values.Any(f => f.IsTuned);
        if (_streams.Count == 0 && !hasTuned && _masterStream != 0) StopMasterStream();
    }

    public void UpdateStreamParams(string streamId, AudioParams newParams)
    {
        lock (_lock)
        {
            if (!_streams.TryGetValue(streamId, out var stream)) return;
            stream.CurrentParams = newParams;
            stream.RadioEffect.Params = newParams;
        }
    }

    public void Initialize(int deviceIndex)
    {
        lock (_lock)
        {
            if (_masterStream == 0) StartMasterStream();
        }
    }

    public void TuneFrequency(double frequencyMHz)
    {
        lock (_lock)
        {
            if (!_frequencies.ContainsKey(frequencyMHz))
                _frequencies[frequencyMHz] = new FrequencyConfig();

            var freqConfig = _frequencies[frequencyMHz];
            if (freqConfig.SquelchBurst == null)
            {
                freqConfig.SquelchBurst = new SquelchBurstGenerator(_sampleRate, _channels);
            }

            freqConfig.IsTuned = true;
            if (freqConfig.NoiseGenerator == null)
            {
                freqConfig.NoiseGenerator = new BackgroundNoiseGenerator(_sampleRate, _channels, frequencyMHz);

                // Background noise amplitude when no one is transmitting
                freqConfig.MinimumGain = FastPathAudioSim.CalculateBackgroundNoiseAmplitude(frequencyMHz);

                // Set physics-based default squelch threshold based on noise floor
                // This is the minimum detectable signal level - signals below this are unintelligible
                float noiseFloor = FastPathAudioSim.CalculateNoiseFloorAmplitude(frequencyMHz);
                freqConfig.DefaultSquelchThreshold = noiseFloor;

                // Apply to any existing streams on this frequency
                foreach (var stream in _streams.Values.Where(s => Math.Abs(s.FrequencyMHz - frequencyMHz) < 0.01))
                {
                    stream.RadioEffect.SetSquelchThreshold(noiseFloor);
                }

                Console.WriteLine(
                    $"[TuneFrequency] {frequencyMHz} MHz: MinimumGain={freqConfig.MinimumGain:F3}, NoiseFloor={noiseFloor:F3}");
            }

            if (_masterStream == 0) StartMasterStream();
        }
    }

    public void UntuneFrequency(double frequencyMHz)
    {
        lock (_lock)
        {
            if (!_frequencies.TryGetValue(frequencyMHz, out var frequency)) return;
            frequency.IsTuned = false;
            bool hasTuned = _frequencies.Values.Any(f => f.IsTuned);
            if (!hasTuned && _streams.Count == 0 && _masterStream != 0) StopMasterStream();
        }
    }

    public void SetSquelchLevel(double frequencyMHz, float squelchLevel)
    {
        lock (_lock)
        {
            if (!_frequencies.ContainsKey(frequencyMHz))
                _frequencies[frequencyMHz] = new FrequencyConfig();

            var freqConfig = _frequencies[frequencyMHz];

            // Calculate effective threshold based on background noise amplitude
            // squelchLevel = 1.0 means threshold equals background noise (cuts it out)
            // squelchLevel = 1.5 means threshold is 50% higher (more aggressive)
            float effectiveThreshold = (float)freqConfig.MinimumGain * squelchLevel;

            // Store the relative squelch level (user-friendly)
            freqConfig.SquelchLevel = squelchLevel;

            // Apply absolute threshold to all streams on this frequency
            foreach (var s in _streams.Values.Where(s => Math.Abs(s.FrequencyMHz - frequencyMHz) < 0.01))
            {
                s.RadioEffect.SetSquelchThreshold(effectiveThreshold);
            }

            // Store for new streams
            freqConfig.DefaultSquelchThreshold = effectiveThreshold;
        }
    }

    public float GetSquelchLevel(double frequencyMHz)
    {
        lock (_lock)
        {
            if (_frequencies.TryGetValue(frequencyMHz, out var config))
                return config.SquelchLevel;
        }

        return 1.0f; // Default
    }

    public float? GetSquelchThreshold(double frequencyMHz)
    {
        lock (_lock)
        {
            // Get threshold from any stream on this frequency
            var stream = _streams.Values.FirstOrDefault(s => Math.Abs(s.FrequencyMHz - frequencyMHz) < 0.01);
            if (stream != null)
                return stream.RadioEffect.GetSquelchThreshold();

            // If no active streams, return the default for this frequency
            if (_frequencies.TryGetValue(frequencyMHz, out var config))
                return config.DefaultSquelchThreshold;
        }

        return null;
    }

    public void SetFrequencyVolume(float frequencyMHz, float volume)
    {
        lock (_lock)
        {
            if (!_frequencies.ContainsKey(frequencyMHz)) _frequencies[frequencyMHz] = new FrequencyConfig();
            _frequencies[frequencyMHz].Volume = Math.Clamp(volume, 0f, 1f);
        }
    }

    public void SetFrequencyAudioChannel(float frequencyMHz, AudioChannel channel)
    {
        lock (_lock)
        {
            if (!_frequencies.ContainsKey(frequencyMHz))
                _frequencies[frequencyMHz] = new FrequencyConfig();
            _frequencies[frequencyMHz].AudioChannel = channel;
        }
    }

    // Convert stream from native channel format to output channel format
    private void ConvertToOutputFormat(RadioStream stream, float[] dest, int destSamples)
    {
        int validSamples = stream.ValidSamples;
        int validFrames = validSamples / stream.Channels;
        int outputFrames = destSamples / _channels;

        if (stream.Channels == 1 && _channels == 2)
        {
            // Mono → Stereo: duplicate each sample
            for (int frame = 0; frame < validFrames && frame < outputFrames; frame++)
            {
                float sample = stream.Buffer[frame];
                dest[frame * 2] = sample;
                dest[frame * 2 + 1] = sample;
            }

            // Clear remainder
            if (validFrames < outputFrames)
                Array.Clear(dest, validFrames * 2, (outputFrames - validFrames) * 2);
        }
        else if (stream.Channels == 2 && _channels == 2)
        {
            // Stereo → Stereo: direct copy
            Array.Copy(stream.Buffer, dest, Math.Min(validSamples, destSamples));
            if (validSamples < destSamples)
                Array.Clear(dest, validSamples, destSamples - validSamples);
        }
        else if (stream.Channels == 1 && _channels == 1)
        {
            // Mono → Mono: direct copy
            Array.Copy(stream.Buffer, dest, Math.Min(validSamples, destSamples));
            if (validSamples < destSamples)
                Array.Clear(dest, validSamples, destSamples - validSamples);
        }
        else
        {
            // Generic fallback (shouldn't happen normally)
            Array.Clear(dest, 0, destSamples);
        }
    }

    private void StartMasterStream()
    {
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
        if (_masterStream == 0) throw new Exception($"BASS error creating master stream: {Bass.LastError}");
        SetupDSPAndPlay();
    }

    private void StopMasterStream()
    {
        if (_masterStream != 0)
        {
            Bass.ChannelStop(_masterStream);
        
            // Remove DSP callback before freeing the stream
            if (_dspProc != null)
            {
                Bass.ChannelRemoveDSP(_masterStream, _masterDspProcHandle);
            }
        
            Bass.StreamFree(_masterStream);
            _masterStream = 0;
        }
    }

    private void SetupDSPAndPlay()
    {
        _dspProc = (_, _, bufferPtr, length, _) =>
        {
            int samples = length / sizeof(float);

            if (samples > MaxBufferSize)
            {
                lock (_lock)
                {
                    _dspScratch = new float[samples];
                    _mixBuffer1 = new float[samples];
                    _mixBuffer2 = new float[samples];
                    _frequencyMixBuffer = new float[samples];
                    _noiseBuffer = new float[samples];
                }
            }

            List<RadioStream> activeStreams;
            Dictionary<double, FrequencyConfig> frequencySnapshot;
            lock (_lock)
            {
                activeStreams = _streams.Values.Where(s =>
                    s.CurrentParams.Gain > 0 &&
                    s.CurrentParams.Gain >= _frequencies[s.FrequencyMHz].MinimumGain).ToList();
                frequencySnapshot = new Dictionary<double, FrequencyConfig>(_frequencies);
            }

            int outputFrames = samples / _channels;
            Array.Clear(_dspScratch, 0, samples);

            // Generate background noise per frequency
            foreach (var kvp in frequencySnapshot)
            {
                double freq = kvp.Key;
                var freqConfig = kvp.Value;
                if (!freqConfig.IsTuned || freqConfig.NoiseGenerator == null) continue;

                var freqStreams = activeStreams.Where(s => Math.Abs(s.FrequencyMHz - freq) < 0.01d).ToList();
                bool freqHasHearableStreams = freqStreams.Any(s => s.RadioEffect.IsSquelchOpen);

                float squelchThreshold = freqStreams.FirstOrDefault()?.RadioEffect.GetSquelchThreshold()
                                         ?? freqConfig.DefaultSquelchThreshold;

                bool noiseIsSquelched = (float)freqConfig.MinimumGain <= squelchThreshold ||
                                        freqHasHearableStreams ||
                                        freqConfig.IsNoiseMuted;

                lock (_lock)
                {
                    if (_frequencies.TryGetValue(freq, out var fc))
                        fc.WasHearableLastFrame = freqHasHearableStreams;
                }

                if (!noiseIsSquelched && (float)freqConfig.MinimumGain > 0.001f)
                {
                    float targetGain = (float)freqConfig.MinimumGain * freqConfig.Volume;
                    float fadeStep = targetGain / NoiseFadeSamples;
                    freqConfig.NoiseGenerator.GenerateNoise(_noiseBuffer, 0, samples, 1.0f);

                    if (_channels == 2)
                    {
                        for (int frame = 0; frame < outputFrames; frame++)
                        {
                            if (freqConfig.NoiseFadeGain < targetGain)
                                freqConfig.NoiseFadeGain = Math.Min(freqConfig.NoiseFadeGain + fadeStep, targetGain);
                            int leftIdx = frame * 2;
                            int rightIdx = leftIdx + 1;
                            float noiseSample = _noiseBuffer[leftIdx] * freqConfig.NoiseFadeGain;
                            switch (freqConfig.AudioChannel)
                            {
                                case AudioChannel.Left: _dspScratch[leftIdx] += noiseSample; break;
                                case AudioChannel.Right: _dspScratch[rightIdx] += noiseSample; break;
                                case AudioChannel.Both:
                                    _dspScratch[leftIdx] += noiseSample;
                                    _dspScratch[rightIdx] += noiseSample;
                                    break;
                            }
                        }
                    }
                    else
                    {
                        for (int i = 0; i < samples; i++)
                        {
                            if (freqConfig.NoiseFadeGain < targetGain)
                                freqConfig.NoiseFadeGain = Math.Min(freqConfig.NoiseFadeGain + fadeStep, targetGain);
                            _dspScratch[i] += _noiseBuffer[i] * freqConfig.NoiseFadeGain;
                        }
                    }
                }
                else
                {
                    float fadeStep = (float)freqConfig.MinimumGain / NoiseFadeSamples;
                    if (freqConfig.NoiseFadeGain > 0f)
                        freqConfig.NoiseFadeGain = Math.Max(freqConfig.NoiseFadeGain - fadeStep, 0f);
                }
            }

            // Process streams
            foreach (var stream in activeStreams)
            {
                if (stream.IsPush)
                {
                    int framesRead = stream.ReadFromRing(stream.Buffer, outputFrames);
                    if (framesRead == 0)
                    {
                        stream.ValidSamples = 0;
                        Array.Clear(stream.Buffer, 0, stream.Buffer.Length);
                        continue;
                    }

                    int samplesRead = framesRead * stream.Channels;
                    stream.ValidSamples = samplesRead;
                    stream.RadioEffect.Process(stream.Buffer, 0, samplesRead);
                    stream.RadioPreFilter.Process(stream.Buffer, 0, samplesRead, stream.Channels);
                }
                else
                {
                    int framesRead = stream.ReadFromRing(stream.Buffer, outputFrames);
                    if (framesRead == 0)
                    {
                        stream.ValidSamples = 0;
                        Array.Clear(stream.Buffer, 0, stream.Buffer.Length);
                        continue;
                    }

                    int samplesRead = framesRead * stream.Channels;
                    stream.ValidSamples = samplesRead;
                    stream.RadioEffect.Process(stream.Buffer, 0, samplesRead);
                    stream.RadioPreFilter.Process(stream.Buffer, 0, samplesRead, stream.Channels);
                }
            }

            // Mix frequencies and process squelch bursts
            foreach (var kvp in frequencySnapshot)
            {
                double freq = kvp.Key;
                var freqConfig = kvp.Value;
                if (!freqConfig.IsTuned) continue;

                // Get ALL streams on this frequency (for squelch detection)
                var freqStreams = activeStreams.Where(s => Math.Abs(s.FrequencyMHz - freq) < 0.01d).ToList();

                // Determine squelch state (uses ALL streams, checks IsTransmitting flag)
                if (freqConfig.SquelchBurst != null)
                {
                    // Use explicit transmission state from RTP markers
                    bool hasActiveTransmission = freqStreams.Any(s => s.IsTransmitting);

                    // Check packet freshness ONLY for push streams (network-based)
                    // File-based streams continuously provide data, no "packets" to check
                    bool hasRecentPackets = freqStreams.Any(s =>
                        s.IsTransmitting &&
                        (s.IsPush == false || (DateTime.UtcNow - s.LastPacketReceived).TotalMilliseconds < 200));

                    // Force-end stale transmissions (ONLY for push streams)
                    foreach (var s in freqStreams.Where(s =>
                                 s.IsPush &&
                                 s.IsTransmitting &&
                                 (DateTime.UtcNow - s.LastPacketReceived).TotalMilliseconds >= 200))
                    {
                        Console.WriteLine($"[DSP] Force-ending stale transmission for {s.StreamId}");
                        s.IsTransmitting = false;
                        s.TransmissionEndTime = DateTime.UtcNow;
                    }

                    bool isActiveTransmission = hasActiveTransmission && hasRecentPackets;

                    float squelchThreshold = freqStreams.FirstOrDefault()?.RadioEffect.GetSquelchThreshold()
                                             ?? freqConfig.DefaultSquelchThreshold;

                    bool noiseAboveSquelch = (float)freqConfig.MinimumGain > squelchThreshold;
                    bool isSquelchOpen = isActiveTransmission || noiseAboveSquelch;

                    if (isSquelchOpen != freqConfig.WasSquelchOpen)
                    {
                        Console.WriteLine($"[DSP:{freq}] SQUELCH {(isSquelchOpen ? "OPEN" : "CLOSED")}");
                        if (isSquelchOpen)
                            freqConfig.SquelchBurst.TriggerOpening();
                        else
                            freqConfig.SquelchBurst.TriggerClosing();

                        freqConfig.WasSquelchOpen = isSquelchOpen;
                    }
                }

                // Get ONLY transmitting streams for audio mixing
                var transmittingStreams = freqStreams.Where(s => s.IsTransmitting).ToList();

                // Skip if no transmitting streams and no burst playing
                bool hasBurstPlaying = freqConfig.SquelchBurst?.IsPlaying ?? false;
                if (transmittingStreams.Count == 0 && !hasBurstPlaying) continue;

                Array.Clear(_frequencyMixBuffer, 0, samples);

                // Mix transmitting streams
                if (transmittingStreams.Count > 0)
                {
                    if (transmittingStreams.Count == 1)
                    {
                        ConvertToOutputFormat(transmittingStreams[0], _frequencyMixBuffer, samples);
                    }
                    else // 2+ transmitting streams = stepped-on
                    {
                        var sorted = transmittingStreams.OrderByDescending(s => s.CurrentParams.Gain).ToList();
                        var primary = sorted[0];
                        var secondary = sorted[1];

                        ConvertToOutputFormat(primary, _mixBuffer1, samples);
                        ConvertToOutputFormat(secondary, _mixBuffer2, samples);

                        var steppedParams = Radiomixer.CalculateSteppedOnParams(
                            primary.CurrentParams, secondary.CurrentParams,
                            primary.CurrentParams.Distance_km, secondary.CurrentParams.Distance_km,
                            primary.CurrentParams.SNR_dB, secondary.CurrentParams.SNR_dB);

                        freqConfig.Mixer.ProcessSteppedOn(_mixBuffer1, _mixBuffer2, _frequencyMixBuffer, samples,
                            steppedParams, _sampleRate, primary.RadioEffect.GetSquelchThreshold(),
                            primary.CurrentParams.Gain, secondary.CurrentParams.Gain);
                    }
                }

                // Process squelch burst (happens even if no transmitting streams)
                if (freqConfig.SquelchBurst != null)
                    freqConfig.SquelchBurst.Process(_frequencyMixBuffer, 0, samples);

                // Mix to output
                float volume = freqConfig.Volume;
                var audioChannel = freqConfig.AudioChannel;

                if (_channels == 2)
                {
                    int frames = samples / 2;
                    for (int frame = 0; frame < frames; frame++)
                    {
                        int li = frame * 2;
                        int ri = li + 1;
                        float l = _frequencyMixBuffer[li] * volume;
                        float r = _frequencyMixBuffer[ri] * volume;
                        switch (audioChannel)
                        {
                            case AudioChannel.Left: _dspScratch[li] += l + r; break;
                            case AudioChannel.Right: _dspScratch[ri] += l + r; break;
                            case AudioChannel.Both:
                                _dspScratch[li] += l;
                                _dspScratch[ri] += r;
                                break;
                        }
                    }
                }
                else
                {
                    for (int i = 0; i < samples; i++)
                        _dspScratch[i] += _frequencyMixBuffer[i] * volume;
                }
            }

            for (int i = 0; i < samples; i++)
                _dspScratch[i] = Math.Clamp(_dspScratch[i], -1f, 1f);

            Marshal.Copy(_dspScratch, 0, bufferPtr, samples);
        };

        _masterDspProcHandle = Bass.ChannelSetDSP(_masterStream, _dspProc, IntPtr.Zero);
        Bass.ChannelPlay(_masterStream);
    }


    public async Task StopAll()
    {
        List<string> ids;
        lock (_lock) ids = _streams.Keys.ToList();
        foreach (var id in ids) await StopStream(id);
        StopMasterStream();
    }

    public List<string> GetActiveStreams()
    {
        lock (_lock) return _streams.Keys.ToList();
    }

    public List<string> GetStreamsOnFrequency(double f)
    {
        lock (_lock)
            return _streams.Values.Where(s => Math.Abs(s.FrequencyMHz - f) < 0.001).Select(s => s.StreamId).ToList();
    }

    public bool IsStreamActive(string id)
    {
        lock (_lock) return _streams.TryGetValue(id, out _);
    }

    public List<double> GetActiveFrequencies()
    {
        lock (_lock) return _streams.Values.Select(s => s.FrequencyMHz).Distinct().OrderBy(x => x).ToList();
    }

    public AudioParams? GetStreamParams(string id)
    {
        lock (_lock)
        {
            if (_streams.TryGetValue(id, out var s)) return s.CurrentParams;
            return null;
        }
    }

    public double? GetStreamFrequency(string id)
    {
        lock (_lock)
        {
            if (_streams.TryGetValue(id, out var s)) return s.FrequencyMHz;
            return null;
        }
    }

    public void ChangeOutputDevice(int newDeviceIndex)
    {
        lock (_lock)
        {
            if (_masterStream != 0)
            {
                Bass.ChannelStop(_masterStream);
                Bass.StreamFree(_masterStream);
                _masterStream = 0;
            }

            Bass.CurrentDevice = newDeviceIndex;
            StartMasterStream();
        }
    }


    /// <summary>
    /// Called when WebSocket signals PTT press (optional - RTP markers are primary)
    /// </summary>
    public void OnWebSocketPTTPress(string streamId)
    {
        lock (_lock)
        {
            if (!_streams.TryGetValue(streamId, out var stream)) return;

            // WebSocket arrives before RTP packets typically
            // Just log for validation - RTP start marker will trigger actual transmission start
            Console.WriteLine($"[WebSocket:{streamId}] PTT pressed - expecting RTP start marker");
        }
    }

    /// <summary>
    /// Called when WebSocket signals PTT release (optional - RTP markers are primary)
    /// Acts as a backup timeout mechanism if RTP end marker is lost
    /// </summary>
    public void OnWebSocketPTTRelease(string streamId)
    {
        lock (_lock)
        {
            if (!_streams.TryGetValue(streamId, out var stream)) return;

            if (stream.IsTransmitting)
            {
                // Start a sanity check timer - if RTP end marker doesn't arrive within 1s, force end
                Task.Delay(1000).ContinueWith(_ =>
                {
                    lock (_lock)
                    {
                        if (_streams.TryGetValue(streamId, out var s) && s.IsTransmitting)
                        {
                            var stallTime = DateTime.UtcNow - s.LastPacketReceived;
                            Console.WriteLine(
                                $"[WebSocket:{streamId}] Force-ending transmission - RTP end marker missing (last packet {stallTime.TotalMilliseconds:F0}ms ago)");
                            s.IsTransmitting = false;
                            s.TransmissionEndTime = DateTime.UtcNow;
                        }
                    }
                });
            }

            Console.WriteLine($"[WebSocket:{streamId}] PTT released - expecting RTP end marker");
        }
    }

    /// <summary>
    /// Monitors push streams for peer timeouts during active transmissions
    /// Should be called once during initialization
    /// </summary>
    public void StartPeerTimeoutMonitoring()
    {
        lock (_lock)
        {
            if (_timeoutMonitoringStarted)
            {
                Console.WriteLine("[TimeoutMonitor] Already started");
                return;
            }

            _timeoutMonitoringStarted = true;
        }

        Task.Run(async () =>
        {
            Console.WriteLine("[TimeoutMonitor] Starting peer timeout monitoring");

            while (true)
            {
                await Task.Delay(1000); // Check every second

                lock (_lock)
                {
                    var now = DateTime.UtcNow;
                    var timedOutStreams = _streams.Values
                        .Where(s => s.IsPush &&
                                    s.IsTransmitting &&
                                    (now - s.LastPacketReceived) > s.PeerTimeoutThreshold)
                        .ToList();

                    foreach (var stream in timedOutStreams)
                    {
                        var stallTime = now - stream.LastPacketReceived;
                        Console.WriteLine(
                            $"[TimeoutMonitor:{stream.StreamId}] Peer timeout during transmission (no packets for {stallTime.TotalSeconds:F1}s)");

                        stream.IsTransmitting = false;
                        stream.TransmissionEndTime = now;

                        // Clear ring buffer for crashed peer to avoid stale audio
                        stream.ClearRingBuffer();
                    }
                }
            }
        });
    }
}