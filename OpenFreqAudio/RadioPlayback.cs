using System.Runtime.InteropServices;
using ManagedBass;
// ReSharper disable InconsistentNaming

namespace OpenFreqAudio;

/// <summary>
/// Rewritten RadioPlayback with corrected mixer architecture.
/// Key changes:
/// - Push (WebRTC) streams use a per-stream circular float ring buffer.
/// - File-based streams are decoded by a background reader task that fills the same ring buffer,
///   so both push and file streams are mixed identically by the DSP.
/// - This avoids using ChannelGetData() inside the DSP and prevents NotAvailable errors.
/// - Stream objects track whether they are push streams (IsPush). Push audio is pushed with PushAudioData(byte[]).
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

        public bool IsStopping { get; set; }
        public int StoppingBurstSamplesLeft { get; set; }

        public bool HasReceivedAudio { get; set; }
        public DateTime LastAudioReceived { get; set; } = DateTime.MinValue;
        public int ValidSamples { get; set; }

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

                // Only log underruns if we've already started playback (buffer should be reasonably full)
                // and the underrun is significant
                if (availableFrames < frameCount && HasReceivedAudio && fillPercent < 20f)
                {
                    Console.WriteLine(
                        $"[ReadFromRing:{StreamId}] UNDERRUN! Requested {frameCount} frames, only {availableFrames} available ({fillPercent:F1}% full, {_ringCount}/{RingBuffer.Length})");
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
                int destSamples = frameCount * Channels;
                if (samplesToRead < destSamples)
                    Array.Clear(dest, samplesToRead, destSamples - samplesToRead);

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
        }

        Radiomixer.LoadSteppedOnSample();
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
                IsStopping = false
            };
            

            // Allocate a ring buffer (3 seconds worth of audio)
            int ringFrames = info.Frequency * 3;
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
                IsStopping = false
            };

            // Ring buffer capacity: 3 seconds (same as file streams) to handle jitter + processing overhead
            int ringFrames = sampleRate / 4;
            int ringCapacity = ringFrames * Math.Max(1, channels);
            stream.EnsureRingBufferCapacity(ringCapacity);

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
    public bool PushAudioData(string streamId, byte[] audioData)
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
            float fillPercent = (float)fillCount / capacity * 100f;
            Console.WriteLine(
                $"[PushAudioData:{streamId}] Pushed {frames} frames ({audioData.Length} bytes, {bytesPerSample * 8}-bit), buffer now {fillPercent:F1}% full ({fillCount}/{capacity})");

            return true;
        }
    }

    public async Task StopStream(string streamId)
    {
        await Task.Run(() =>
        {
            lock (_lock)
            {
                if (!_streams.TryGetValue(streamId, out var stream)) return;

                float threshold = stream.RadioEffect.GetSquelchThreshold();

                if (stream.CurrentParams.Gain >= threshold && threshold > 0.01f)
                {
                    stream.IsStopping = true;
                    stream.StoppingBurstSamplesLeft = 720;
                    stream.RadioEffect.TriggerSquelchBurst();
                }
                else
                {
                    StopStreamInternal(streamId);
                }
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
            
                Console.WriteLine($"[TuneFrequency] {frequencyMHz} MHz: MinimumGain={freqConfig.MinimumGain:F3}, NoiseFloor={noiseFloor:F3}");
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
                    (!s.IsStopping || s.StoppingBurstSamplesLeft > 0) && s.CurrentParams.Gain > 0 &&
                    s.CurrentParams.Gain >= _frequencies[s.FrequencyMHz].MinimumGain).ToList();
                frequencySnapshot = new Dictionary<double, FrequencyConfig>(_frequencies);
            }

            int outputFrames = samples / _channels;
            Array.Clear(_dspScratch, 0, samples);

            // Update stream activity BEFORE processing anything else
            // RadioEffect will automatically trigger squelch bursts when activity changes
            foreach (var stream in activeStreams)
            {
                bool isActive = stream.HasReceivedAudio && 
                               (DateTime.UtcNow - stream.LastAudioReceived).TotalMilliseconds < 500 &&
                               !stream.IsStopping;
                
                // This will trigger squelch bursts automatically if activity state changed
                stream.RadioEffect.SetStreamActive(isActive);
            }
            
            // Generate frequency noise where applicable
            foreach (var kvp in frequencySnapshot)
            {
                double freq = kvp.Key;
                var freqConfig = kvp.Value;
                if (!freqConfig.IsTuned || freqConfig.NoiseGenerator == null) continue;
    
                var freqStreams = activeStreams.Where(s => Math.Abs(s.FrequencyMHz - freq) < 0.01d).ToList();

                // Check if any stream on this frequency has squelch open
                bool freqHasHearableStreams = freqStreams.Any(s => s.RadioEffect.IsSquelchOpen);
    
                // Get squelch threshold from any stream on this frequency (or use default)
                float squelchThreshold = freqStreams.FirstOrDefault()?.RadioEffect.GetSquelchThreshold() 
                                         ?? freqConfig.DefaultSquelchThreshold;
    
                // Background noise is squelched if:
                // 1. Its amplitude is below the squelch threshold, OR
                // 2. There are active transmissions
                bool noiseIsSquelched = (float)freqConfig.MinimumGain <= squelchThreshold || freqHasHearableStreams || freqConfig.IsNoiseMuted;
    
                lock (_lock)
                {
                    if (_frequencies.TryGetValue(freq, out var fc))
                        fc.WasHearableLastFrame = freqHasHearableStreams;
                }

                // Generate background noise only if not squelched
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
                    // Fade out noise
                    float fadeStep = (float)freqConfig.MinimumGain / NoiseFadeSamples;
                    if (freqConfig.NoiseFadeGain > 0f)
                        freqConfig.NoiseFadeGain = Math.Max(freqConfig.NoiseFadeGain - fadeStep, 0f);
                }

            }

            // Read/process streams in their native channel format.
            // Channel conversion to output format happens during mixing.
            List<string> streamsToRemove = new();

            foreach (var stream in activeStreams)
            {
                // Fill stream.Buffer with native-channel-interleaved samples
                if (stream.IsPush)
                {
                    // For push streams (WebRTC), implement jitter buffer:
                    // Only start playing once we have enough data buffered
                    var (fillCount, capacity) = stream.GetRingBufferFillLevel();
                    float fillPercent = (float)fillCount / capacity;
                    int fillFrames = fillCount / stream.Channels;

                    // Minimum threshold: either 50% of requested frames, or 100ms worth of audio, whichever is larger
                    int minThresholdFrames = _sampleRate / 100; // 25ms

                    // If we don't have enough data yet, skip this stream (let buffer fill up)
                    if (fillFrames < minThresholdFrames)
                    {
                        // If we cannot supply a full DSP block, output silence
                        stream.ValidSamples = 0;
                        Array.Clear(stream.Buffer, 0, stream.Buffer.Length);

                        // Log only once when transitioning to low-buffer state
                        Console.WriteLine(
                            $"[DSP:{stream.StreamId}] Buffering... {fillFrames}/{minThresholdFrames} frames ({fillPercent:F1}%)");

                        // We still continue, but now silence will be mixed
                        continue;
                    }

                    // Read from ring buffer in native channel format
                    int framesRead = stream.ReadFromRing(stream.Buffer, outputFrames);
                    if (framesRead == 0)
                    {
                        // Clear the old audio — prevent endless repetition
                        stream.ValidSamples = 0;
                        Array.Clear(stream.Buffer, 0, stream.Buffer.Length);
                        Console.WriteLine("CLEARING OLD BUFFER");

                        // We still continue, but now silence will be mixed
                        continue;
                    }

                    int samplesRead = framesRead * stream.Channels; // Native channel count
                    stream.ValidSamples = samplesRead;
                    stream.RadioEffect.Process(stream.Buffer, 0, samplesRead);
                    stream.RadioPreFilter.Process(stream.Buffer, 0, samplesRead, stream.Channels);
                }
                else
                {
                    // File streams: read in native channel format, process, then convert during mixing
                    int framesRead = stream.ReadFromRing(stream.Buffer, outputFrames);
                    if (framesRead == 0)
                    {
                        // Clear the old audio — prevent endless repetition
                        stream.ValidSamples = 0;
                        Array.Clear(stream.Buffer, 0, stream.Buffer.Length);

                        // We still continue, but now silence will be mixed
                        continue;
                    }

                    int samplesRead = framesRead * stream.Channels; // Native channel count
                    stream.ValidSamples = samplesRead;
                    stream.RadioEffect.Process(stream.Buffer, 0, samplesRead);
                    stream.RadioPreFilter.Process(stream.Buffer, 0, samplesRead, stream.Channels);
                }

                if (stream.IsStopping)
                {
                    stream.StoppingBurstSamplesLeft -= outputFrames;
                    if (stream.StoppingBurstSamplesLeft <= 0)
                    {
                        streamsToRemove.Add(stream.StreamId);
                    }
                }
            }

            // Mix by frequency (unchanged semantics)
            foreach (var kvp in frequencySnapshot)
            {
                double freq = kvp.Key;
                var freqConfig = kvp.Value;
                if (!freqConfig.IsTuned) continue;

                var freqStreams = activeStreams.Where(s => Math.Abs(s.FrequencyMHz - freq) < 0.01d).ToList();
                if (freqStreams.Count == 0) continue;

                Array.Clear(_frequencyMixBuffer, 0, samples);

                if (freqStreams.Count == 1)
                {
                    // Single stream - convert from native format to output format
                    var s = freqStreams[0];
                    ConvertToOutputFormat(s, _frequencyMixBuffer, samples);
                }
                else
                {
                    // Multiple streams - stepped-on interference
                    var sorted = freqStreams.OrderByDescending(s => s.CurrentParams.Gain).ToList();
                    var primary = sorted[0];
                    var secondary = sorted[1];

                    // Convert both streams to output format
                    ConvertToOutputFormat(primary, _mixBuffer1, samples);
                    ConvertToOutputFormat(secondary, _mixBuffer2, samples);

                    var steppedParams = Radiomixer.CalculateSteppedOnParams(primary.CurrentParams,
                        secondary.CurrentParams, primary.CurrentParams.Distance_km, secondary.CurrentParams.Distance_km,
                        primary.CurrentParams.SNR_dB, secondary.CurrentParams.SNR_dB);
                    
                    freqConfig.Mixer.ProcessSteppedOn(_mixBuffer1, _mixBuffer2, _frequencyMixBuffer, steppedParams,
                        _sampleRate, primary.RadioEffect.GetSquelchThreshold(), primary.CurrentParams.Gain,
                        secondary.CurrentParams.Gain);
                }

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
                    for (int i = 0; i < samples; i++) _dspScratch[i] += _frequencyMixBuffer[i] * volume;
                }
            }

            for (int i = 0; i < samples; i++) _dspScratch[i] = Math.Clamp(_dspScratch[i], -1f, 1f);

            Marshal.Copy(_dspScratch, 0, bufferPtr, samples);

            if (streamsToRemove.Count > 0)
            {
                lock (_lock)
                {
                    foreach (var id in streamsToRemove) StopStreamInternal(id);
                }
            }
        };

        Bass.ChannelSetDSP(_masterStream, _dspProc, IntPtr.Zero);
        Bass.ChannelPlay(_masterStream);
    }

    public async Task StopAll()
    {
        List<string> ids;
        lock (_lock) ids = _streams.Keys.ToList();
        foreach (var id in ids) await StopStream(id);
        if (_masterStream != 0)
        {
            Bass.ChannelStop(_masterStream);
            Bass.StreamFree(_masterStream);
            _masterStream = 0;
        }
    }

    public List<string> GetActiveStreams()
    {
        lock (_lock) return _streams.Keys.ToList();
    }

    public List<string> GetStreamsOnFrequency(double f)
    {
        lock (_lock) return _streams.Values.Where(s => Math.Abs(s.FrequencyMHz - f) < 0.001).Select(s => s.StreamId).ToList();
    }

    public bool IsStreamActive(string id)
    {
        lock (_lock) return _streams.TryGetValue(id, out var s) && !s.IsStopping;
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

    public void MarkStreamSilent(string streamId)
    {
        lock (_lock)
        {
            if (_streams.TryGetValue(streamId, out var s)) s.HasReceivedAudio = false;
        }
    }
}