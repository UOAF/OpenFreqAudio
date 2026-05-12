using System.Runtime.InteropServices;
using ManagedBass;
using ManagedBass.Mix;
using Microsoft.Extensions.Logging;
using NWaves.Filters.Butterworth;

// ReSharper disable InconsistentNaming

namespace OpenFreqAudio;

/// <summary>
/// AKA EnvelopeFollower in NWaves,
/// but without a dumb private delay tap.
/// </summary>
public class FirstOrderFilter
{
    private float Attack;
    private float Decay;

    /// <summary>
    /// The delay tap - i.e. the current value of the filter
    /// </summary>
    public float D1;

    public FirstOrderFilter(float a, float d, float init)
    {
        Attack = a;
        Decay = d;
        D1 = init;
    }

    public float Apply(float x)
    {
        float alpha = x > D1 ? Attack : Decay;
        D1 = alpha * x + (1 - alpha) * D1;
        return D1;
    }
}

/// <summary>
/// RadioPlayback
/// - Push (WebRTC) streams use a per-stream circular float ring buffer.
/// - File-based streams are decoded by a background reader task that fills the same ring buffer,
/// - PushAudioData converts incoming bytes (16-bit PCM or 32-bit float) into floats and writes into the ring buffer.
/// </summary>
public class RadioPlayback : IDisposable
{
    private class RadioStream
    {
        private ILogger _logger;
        public string StreamId { get; set; } = "";
        public int FrequencyKHz { get; set; }
        public int BassStreamHandle { get; set; } // 0 for push streams
        public int BassMixerHandle { get; set; } // 0 if no mixer (push streams)
        public bool IsPush { get; set; } // true for WebRTC / pushed audio
        public required RadioEffect RadioEffect { get; set; }
        public required AudioParams CurrentParams { get; set; }

        // Audio to play is pushed here and pulled by playback.
        public SyncRope<float> Buffer { get; } = new();
        // Scratch space for decoding and FX application
        public float[] Scratch { get; set; } = [];
        // A view of Scratch that contains valid samples.
        // (Should we have some method fill scratch and set this?)
        public Memory<float> Samples { get; set; }

        // Automatic level control - boost the signal to unityish.
        // For a time constant tau, if Fs is our sample rate,
        // AGC ramps down each sample at e^(-1/tau * Fs).
        // This means we ramp about 95% of the way in 3 tau,
        // 99% of the way in 4.6 tau, etc.
        // See: https://en.wikipedia.org/wiki/RC_circuit
        //
        // Aggressive attack to avoid clipping, but decay slowly
        // so we don't pump while people think about what to say next.
        private const double AlcAttack = 0.003f / 3;
        private const double AlcDecay = 1f / 3;
        public readonly FirstOrderFilter Alc = MakeFirstOrderFilter(AlcAttack, AlcDecay, SampleRate);

        public RadioStream(ILogger logger)
        {
            _logger = logger;
        }

        // For file-based streams we run a reader task that decodes and pushes into the ring
        public CancellationTokenSource? FileReaderCts { get; set; }
        public Task? FileReaderTask { get; set; }

        // Transmission state (separate from stream lifecycle)
        public AmbientNoiseType AmbientNoise { get; set; } = AmbientNoiseType.None;

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
            Buffer.Clear();
        }
    }

    /// <summary>
    /// Represents the parameters of an individual radio.
    /// Some clients (e.g. GCI) will have many.
    /// </summary>
    public class RadioConfig
    {
        // ReSharper disable UnusedAutoPropertyAccessor.Local
        public float Volume { get; set; } = 1.0f;

        /// <summary>
        /// Which ears should this radino play into?
        /// </summary>
        /// <summary>Pan position: -100 = full left, 0 = center (both), +100 = full right.</summary>
        public int Pan { get; set; } = 0;

        public bool IsTuned { get; set; }
        public BackgroundNoiseGenerator? NoiseGenerator { get; set; }

        public bool IsNoiseMuted { get; set; }
        public float SquelchLevel { get; set; } = 1.0f;
        // ReSharper restore UnusedAutoPropertyAccessor.Local

        public bool WasSquelchOpen { get; set; }

        // Regardless of our sample rate, we want to band-pass between ~300 and 3000 khz
        // to get our radio sound. Chain two filters
        // (a high-pass to remove low freqs & DC, then a low-pass).
        // This fits our needs better than alternatives:
        //
        // - A single-Butterworth bandpass either has to be relatively low-order
        //   with mediocre rolloff, or else it gets very unstable when we pass
        //   it only positive values from our envelope.
        //
        // - An equivalent FIR filter needs > 512 taps, adding delays of 5ms and up.
        public HighPassFilter HighPass { get; } =
            new HighPassFilter(300.0 / SampleRate, 3);
        public LowPassFilter LowPass { get; } =
            new LowPassFilter(3000.0 / SampleRate, 6);

        // AGC gain; varies as a low-pass of the received signal
        // according to attack and decay params below.
        public FirstOrderFilter Agc = MakeFirstOrderFilter(AgcAttack, AgcDecay, SampleRate);

        // AGC attack and decay are exponential functions -
        // for a time constant tau, if Fs is our sample rate,
        // AGC ramps down each sample at e^(-1/tau * Fs).
        // This means we ramp about 95% of the way in 3 tau,
        // 99% of the way in 4.6 tau, etc.
        // See: https://en.wikipedia.org/wiki/RC_circuit
        //
        // Radio specifications I found suggest AGC should attack
        // (ramp up) in about ~3ms, and decay (ramp down) in ~100ms.
        // So we should pick time constants about a third of those values
        // to get the intended effect. (Feel free to tune these by ear!)
        public const double AgcAttack = 0.003f / 3;
        public const double AgcDecay = 0.1f / 3;
    }


    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RadioPlayback> _logger;

    // All incoming streams
    private readonly Dictionary<string, RadioStream> _streams = new();

    // Frequencies we are tuned to
    // TODO: Lots of our logic below assumes only one radio is tuned to a given frequency.
    private readonly Dictionary<int, RadioConfig> _frequencies = new();

    // Frequencies were we are currently transmitting and which are therefore muted
    private readonly HashSet<int> _transmittingFrequencies = new();

    private readonly object _lock = new();

    private int _masterStream;
    private DSPProcedure? _dspProc;

    private const int MaxBufferSize = 24576;
    private float[] _dspScratch = new float[MaxBufferSize];
    private float[] _dspScratch2 = new float[MaxBufferSize];
    private float[] _stereoBuffer = new float[MaxBufferSize * 2];

    // Phase coherence is good - don't have phase jumps between DSP callbacks.
    private int _sampleNum = 0;

    // Baseband is 8 kHz (4kHz Nyquist)
    // NB: Opus only accepts 8000, 12000, 16000, 24000, or 48000 Hz
    public const int SampleRate = 48000;

    private const int NoiseFadeSamples = 2400;

    private static readonly Lock _bassInitLock = new();

    private int _masterDspProcHandle;

    // Sidetone (own-voice loopback): mic samples pushed here from the recording callback,
    // drained and mixed in the DSP callback with both sides on separate BASS audio threads,
    // so we use a lock-free SPSC ring buffer instead of SyncRope to avoid any lock on the hot path.
    private readonly SidetoneSpscBuffer _sidetoneBuffer = new(8192); // 8192 floats ≈ 170ms @ 48kHz
    private float[] _sidetoneScratch = [];
    public bool SidetoneEnabled { get; set; }
    public float SidetoneVolume { get; set; } = 0.4f;

    public void PushSidetone(ReadOnlySpan<float> samples) => _sidetoneBuffer.Write(samples);
    public void ClearSidetone() => _sidetoneBuffer.Clear();

    public bool Apply3dEffects { get; set; }

    /// <summary>
    /// Create a first-order filter from attack and decay time constants
    /// </summary>
    /// <returns>The filter - not lifted into a closure so that you can query the previous value</returns>
    public static FirstOrderFilter MakeFirstOrderFilter(double attackTau, double decayTau, double sampleRate)
    {
        double attackApha = 1 - Math.Exp(-1 / (sampleRate * attackTau));
        double decayAlpha = 1 - Math.Exp(-1 / (sampleRate * decayTau));
        // Assume we're using this for an AGC or something similar where the initial gain should be 1.
        return new FirstOrderFilter((float)attackApha, (float)decayAlpha, 1.0f);
    }

    public RadioPlayback(ILoggerFactory loggerFactory, int playbackDeviceIndex = -1)
    {
        _logger = loggerFactory.CreateLogger<RadioPlayback>();
        _loggerFactory = loggerFactory;
        lock (_bassInitLock)
        {
            // Just use the default device - BASS doesnt like any checks on that
            if (playbackDeviceIndex == -1)
            {
                Bass.Init(playbackDeviceIndex);
            }
            else
            {
                var deviceInfo = Bass.GetDeviceInfo(playbackDeviceIndex);
                if (!deviceInfo.IsInitialized)
                {
                    // Initialize the new device
                    if (!Bass.Init(playbackDeviceIndex, SampleRate, DeviceInitFlags.Default, IntPtr.Zero))
                    {
                        _logger.LogError($"Failed to initialize device {playbackDeviceIndex}: {Bass.LastError}");
                        return;
                    }
                }

                Bass.CurrentDevice = playbackDeviceIndex;
            }

            // Configure BASS for low-latency operation
            Bass.Configure(Configuration.UpdatePeriod, 5);
            Bass.Configure(Configuration.PlaybackBufferLength, 10);
            Bass.Configure(Configuration.DeviceBufferLength, 10);
            Bass.Configure(Configuration.UpdateThreads, 1);
            
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Use explicit path on non-Windows to avoid strange .NET lib*.so wrangling issues
                // We don't need to free it explicitly, this is covered by BASS
                NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "libbassmix.so"));
            }
        }
    }

    public void StartStream(string streamId, string filePath, AudioParams audioParams,
        AmbientNoiseType ambientNoise = AmbientNoiseType.None)
    {
        _logger.LogInformation($"Starting stream '{streamId} with Ambient {ambientNoise}");
        int bassStream;
        ChannelInfo info;

        lock (_lock)
        {
            if (_streams.ContainsKey(streamId)) StopStreamInternal(streamId);
            if (!_frequencies.ContainsKey(audioParams.RadioFrequencyKHz))
                _frequencies[audioParams.RadioFrequencyKHz] = new RadioConfig();

            // Create BASS decode stream (float)
            bassStream = Bass.CreateStream(filePath, 0, 0, BassFlags.Loop | BassFlags.Float | BassFlags.Decode);
            if (bassStream == 0) throw new Exception($"BASS error creating stream '{streamId}': {Bass.LastError}");

            info = Bass.ChannelGetInfo(bassStream);

            _logger.LogInformation("File info: NativeRate={NativeRate}, Channels={Channels}, ResampledTo={InputRate}",
                info.Frequency, info.Channels, SampleRate);
        }

        lock (_lock)
        {
            // Create a BassMix mixer to resample from native rate to SampleRate
            int mixer = BassMix.CreateMixerStream(SampleRate, info.Channels,
                BassFlags.Decode | BassFlags.Float);
            if (mixer == 0)
                throw new Exception($"BASS error creating mixer for '{streamId}': {Bass.LastError}");
            if (!BassMix.MixerAddChannel(mixer, bassStream, BassFlags.Default))
                throw new Exception($"BASS error adding channel to mixer for '{streamId}': {Bass.LastError}");
            if (info.Channels != 1)
                throw new Exception($"BASS error: expected mono, got {info.Channels} channels");

            var stream = new RadioStream(_logger)
            {
                StreamId = streamId,
                FrequencyKHz = audioParams.RadioFrequencyKHz,
                BassStreamHandle = bassStream,
                BassMixerHandle = mixer,
                IsPush = false,
                RadioEffect = new RadioEffect(SampleRate, info.Channels, audioParams,
                        _loggerFactory.CreateLogger<RadioEffect>())
                    { AmbientNoise = ambientNoise },
                CurrentParams = audioParams,
                AmbientNoise = ambientNoise
            };

            // Start a background task that pulls resampled floats from the mixer and pushes them into the ring buffer.
            stream.FileReaderCts = new CancellationTokenSource();
            var token = stream.FileReaderCts.Token;
            int streamSampleRate = SampleRate;
            stream.FileReaderTask = Task.Run(() =>
            {
                try
                {
                    // Read in larger chunks for better throughput (8192 frames = ~185ms at 44.1kHz)
                    int chunkFrames = 8192;
                    int chunkSamples = chunkFrames;
                    int consecutiveNoData = 0;
                    int totalFramesRead = 0;

                    while (!token.IsCancellationRequested)
                    {
                        float[] readBuffer = new float[chunkSamples];
                        int bytesRequested = chunkSamples * sizeof(float);
                        int bytesRead = Bass.ChannelGetData(mixer, readBuffer, bytesRequested);

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
                                _logger.LogDebug(
                                    "Reached end of stream after reading {TotalFrames} total frames ({Duration:F2}s) at {Time:HH:mm:ss.fff} (StreamId: {StreamId})",
                                    totalFramesRead, (float)totalFramesRead / streamSampleRate, DateTime.Now, streamId);
                            }

                            continue;
                        }

                        consecutiveNoData = 0;

                        int samplesRead = bytesRead / sizeof(float);
                        totalFramesRead += samplesRead;

                        if (samplesRead > 0)
                        {
                            stream.Buffer.Fill(readBuffer.AsMemory()[..samplesRead]);

                            // Throttle reading to prevent flooding the ring buffer
                            // Dynamically adjust sleep time based on how full the buffer is

                            var currentFill = stream.Buffer.Available;
                            // Fill up to a second
                            var fillPercent = (float)currentFill / streamSampleRate;

                            // Calculate base sleep time (80% of audio duration)
                            int baseSleepMs = (int)((float)currentFill / streamSampleRate * 1000 * 0.8);

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

                    _logger.LogDebug("Task cancelled, exiting (StreamId: {StreamId})", streamId);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("Task cancelled (StreamId: {StreamId})", streamId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Exception in FileReader for stream {StreamId}", streamId);
                }
            }, token);

            _streams.Add(streamId, stream);

            _logger.LogInformation(
                "Added file stream {StreamId}: Freq={Frequency:F3}MHz, Power={Power}dBm, Channels={Channels}, NativeRate={NativeRate}Hz, ResampledTo={InputRate}Hz",
                streamId, audioParams.RadioFrequencyKHz / 1000.0, audioParams.ReceivedDb, info.Channels, info.Frequency,
                SampleRate);
        }
    }

    public void StartPushStream(string streamId, int sampleRate, int channels, AudioParams audioParams)
    {
        if (sampleRate != SampleRate)
        {
            throw new ArgumentException($"Push stream had sample rate of {sampleRate}, expected {SampleRate}");
        }
        if (channels != 1)
        {
            throw new ArgumentException($"Expected mono, got {channels} channels");
        }

        lock (_lock)
        {
            if (_streams.ContainsKey(streamId)) return;
            if (!_frequencies.ContainsKey(audioParams.RadioFrequencyKHz))
                _frequencies[audioParams.RadioFrequencyKHz] = new RadioConfig();
        }


        lock (_lock)
        {
            var stream = new RadioStream(_loggerFactory.CreateLogger<RadioStream>())
            {
                StreamId = streamId,
                FrequencyKHz = audioParams.RadioFrequencyKHz,
                BassStreamHandle = 0,
                IsPush = true,
                RadioEffect = new RadioEffect(sampleRate, channels, audioParams,
                    _loggerFactory.CreateLogger<RadioEffect>()),
                CurrentParams = audioParams,
            };

            int ringFrames = (sampleRate * 150) / 1000; // 150ms
            int minBufferFrames = (sampleRate * 120) / 1000; // 120ms
            int ringCapacity = ringFrames * Math.Max(1, channels);

            _logger.LogInformation("Stream '{StreamId}' on {Frequency:F3} MHz", streamId,
                audioParams.RadioFrequencyKHz / 1000.0);
            _logger.LogInformation("  SampleRate={SampleRate}, Channels={Channels}", sampleRate, channels);
            _logger.LogInformation(
                "  RingBuffer: {RingFrames} frames × {Channels} ch = {RingCapacity} samples ({Duration:F1}s)",
                ringFrames, Math.Max(1, channels), ringCapacity, (float)ringFrames / sampleRate);

            _streams.Add(streamId, stream);
        }
    }

    // Accepts raw PCM bytes from WebRTC.
    // ambientNoise describes the acoustic environment of the transmitting platform and
    // is forwarded to RadioEffect so the correct SFX layer is applied post-demodulation.
    public bool PushAudioData(string streamId, Memory<short> audioData, AmbientNoiseType ambientNoise = AmbientNoiseType.None)
    {
        lock (_lock)
        {
            if (!_streams.TryGetValue(streamId, out var stream))
            {
                _logger.LogWarning("Stream '{StreamId}' not found", streamId);
                return false;
            }

            if (!stream.IsPush)
            {
                _logger.LogWarning("Stream '{StreamId}' is not a push stream", streamId);
                return false;
            }

            // Propagate ambient noise type to the effect processor so the correct
            // SFX layer (Air / Ground / Stationary) is applied per packet.
            if (stream.AmbientNoise != ambientNoise)
            {
                stream.AmbientNoise = ambientNoise;
                stream.RadioEffect.AmbientNoise = ambientNoise;
            }

            if (audioData.IsEmpty) return false;

            float[] floatFrames = new float[audioData.Length];

            // 16-bit PCM little-endian
            for (int i = 0; i < audioData.Length; ++i)
            {
                float pcm16 = audioData.Span[i];
                floatFrames[i] = pcm16 / short.MaxValue;
            }

            // Push into ring buffer
            stream.Buffer.Fill(new Memory<float>(floatFrames));
            return true;
        }
    }

    public async Task StopStream(string streamId)
    {
        bool shouldStopMaster = false;

        await Task.Run(() =>
        {
            lock (_lock)
            {
                StopStreamInternal(streamId);
                shouldStopMaster = ShouldStopMasterStream();
            }
        });

        // Must release lock before calling StopMasterStream!
        if (shouldStopMaster)
        {
            StopMasterStream();
        }
    }

    private void StopStreamInternal(string streamId)
    {
        if (!_streams.TryGetValue(streamId, out var stream)) return;

        // Stop file reader if any
        if (!stream.IsPush)
        {
            stream.StopFileReader();
        }

        if (!stream.IsPush && stream.BassMixerHandle != 0)
        {
            Bass.StreamFree(stream.BassMixerHandle);
        }

        if (!stream.IsPush && stream.BassStreamHandle != 0)
        {
            Bass.StreamFree(stream.BassStreamHandle);
        }

        _streams.Remove(streamId);

        // Note: Don't call StopMasterStream here - let the caller handle it
        // since this method is called from within locks
    }

    private bool ShouldStopMasterStream()
    {
        bool hasTuned = _frequencies.Values.Any(f => f.IsTuned);
        return _streams.Count == 0 && !hasTuned && _masterStream != 0;
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

    public void Initialize()
    {
        // Always start master stream during initialization
        // This eliminates race conditions when adding streams later
        // The stream will just output silence until streams are added
        StartMasterStream();
    }

    public void TuneFrequency(int frequencyKHz)
    {
        lock (_lock)
        {
            if (!_frequencies.ContainsKey(frequencyKHz))
                _frequencies[frequencyKHz] = new RadioConfig();

            var freqConfig = _frequencies[frequencyKHz];

            freqConfig.IsTuned = true;
            if (freqConfig.NoiseGenerator == null)
            {
                freqConfig.NoiseGenerator = new BackgroundNoiseGenerator(SampleRate, frequencyKHz);

                var bandConfig = FastPathAudioSim.GetBandConfig(frequencyKHz);

                _logger.LogInformation("TuneFrequency {Frequency:F3} MHz", frequencyKHz / 1000.0);
            }
        }
    }

    public void UntuneFrequency(int frequencyKHz)
    {
        bool shouldStopMaster;

        lock (_lock)
        {
            if (!_frequencies.TryGetValue(frequencyKHz, out var frequency)) return;
            frequency.IsTuned = false;
            shouldStopMaster = ShouldStopMasterStream();
        }

        if (shouldStopMaster)
        {
            StopMasterStream();
        }
    }

    public void SetSquelchLevel(int frequencyKHz, float squelchLevel)
    {
        lock (_lock)
        {
            if (!_frequencies.ContainsKey(frequencyKHz))
                _frequencies[frequencyKHz] = new RadioConfig();

            _frequencies[frequencyKHz].SquelchLevel = squelchLevel;
        }
    }

    public float GetSquelchLevel(int frequencyKHz)
    {
        lock (_lock)
        {
            if (_frequencies.TryGetValue(frequencyKHz, out var config))
                return config.SquelchLevel;
        }

        return 1.0f; // Default
    }

    public void SetFrequencyVolume(int frequencyKHz, float volume)
    {
        lock (_lock)
        {
            if (!_frequencies.ContainsKey(frequencyKHz)) _frequencies[frequencyKHz] = new RadioConfig();
            _frequencies[frequencyKHz].Volume = Math.Clamp(volume, -2f, 2f); // allow for some boost
        }
    }

    public float GetFrequencyVolume(int frequencyKHz)
    {
        lock (_lock)
        {
            return !_frequencies.TryGetValue(frequencyKHz, out var value) ? 0f : value.Volume;
        }
    }

    public void SetFrequencyPan(int frequencyKHz, int pan)
    {
        lock (_lock)
        {
            if (!_frequencies.ContainsKey(frequencyKHz))
                _frequencies[frequencyKHz] = new RadioConfig();
            _frequencies[frequencyKHz].Pan = Math.Clamp(pan, -100, 100);
        }
    }

    public void AddTransmittingFrequencies(IEnumerable<int> frequencies)
    {
        lock (_lock)
        {
            foreach (var frequency in frequencies)
            {
                _transmittingFrequencies.Add(frequency);
            }

            // Log changes
            _logger.LogDebug(_transmittingFrequencies.Count > 0
                    ? "Now blocking: {Frequencies}"
                    : "Not blocking any frequencies",
                string.Join(", ", _transmittingFrequencies.Select(f => $"{f / 1000.0:F3} MHz")));
        }
    }

    public void RemoveTransmittingFrequencies(IEnumerable<int> frequencies)
    {
        lock (_lock)
        {
            foreach (var frequency in frequencies)
            {
                _transmittingFrequencies.Remove(frequency);
            }

            // Log changes
            _logger.LogDebug(_transmittingFrequencies.Count > 0
                    ? "Now blocking: {Frequencies}"
                    : "Not blocking any frequencies",
                string.Join(", ", _transmittingFrequencies.Select(f => $"{f / 1000.0:F3} MHz")));
        }
    }

    public void ClearTransmittingFrequencies()
    {
        lock (_lock)
        {
            _transmittingFrequencies.Clear();
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

        _masterStream = Bass.CreateStream(SampleRate, 2, BassFlags.Float, streamProc, IntPtr.Zero);
        if (_masterStream == 0) throw new Exception($"BASS error creating master stream: {Bass.LastError}");
        SetupDSPAndPlay();
    }

    private void StopMasterStream()
    {
        int streamToStop = 0;
        int dspHandleToRemove = 0;
        DSPProcedure? procToRemove = null;

        // Capture what we need to do while holding the lock
        lock (_lock)
        {
            streamToStop = _masterStream;
            dspHandleToRemove = _masterDspProcHandle;
            procToRemove = _dspProc;

            // Clear state immediately so other threads know we're stopping
            _masterStream = 0;
            _dspProc = null;
            _masterDspProcHandle = 0;
        }


        if (streamToStop != 0)
        {
            if (procToRemove != null)
            {
                Bass.ChannelRemoveDSP(streamToStop, dspHandleToRemove);
            }

            Bass.ChannelStop(streamToStop);
            Bass.StreamFree(streamToStop);
        }
    }

    private void SetupDSPAndPlay()
    {
        // This callback is fired continuously - to avoid stutters,
        // we need to provide length bytes of stereo audio samples.
        _dspProc = (_, _, bufferPtr, length, _) =>
        {
            int stereoOutputSamples = length / sizeof(float);
            int samples = stereoOutputSamples / 2;

            // Ensure buffers are large enough
            if (samples > MaxBufferSize)
            {
                lock (_lock)
                {
                    _dspScratch = new float[samples];
                    _dspScratch2 = new float[samples];
                    _stereoBuffer = new float[stereoOutputSamples];
                }
            }

            // Snapshot current streams and frequencies
            List<RadioStream> streams;
            Dictionary<int, RadioConfig> frequencySnapshot;
            // What should we mute because we're talking on it?
            HashSet<int> transmittingFrequencies;
            lock (_lock)
            {
                if (Apply3dEffects)
                {
                    // Snapshot transmitting frequencies - but only when we are in 3D Mode
                    transmittingFrequencies = new (_transmittingFrequencies);
                }
                else
                {
                    transmittingFrequencies = [];
                }
                streams = _streams.Values.ToList();
                frequencySnapshot = new Dictionary<int, RadioConfig>(_frequencies);
            }

            // Group streams by frequency - we're treating each as its own radio,
            // with its own AGC, squelch, etc.
            var streamsByFrequency = new Dictionary<int, List<RadioStream>>();
            foreach (var stream in streams)
            {
                int freq = stream.FrequencyKHz;
                if (!streamsByFrequency.TryGetValue(freq, out var list))
                {
                    list = new List<RadioStream>();
                    streamsByFrequency[freq] = list;
                }

                list.Add(stream);
            }

            // If anyone has anything to play,
            // limit this round to the shortest length.
            // If all is quiet, just use the provided length.
            int? maxReady = null;
            foreach (var stream in streams)
            {
                var avail = stream.Buffer.Available;
                if (avail > 0)
                {
                    if (!maxReady.HasValue) maxReady = avail;
                    else maxReady = Math.Min(maxReady.Value, avail);
                    // No matter how much we have ready,
                    // we can only handle `samples` at most.
                    maxReady = Math.Min(samples, maxReady.Value);
                }
            }
            // TODO: If we have nothing to play (maxReady is null)
            // we could limit the number of samples returned to a small duration
            // so that we're more responsive as soon as new ones arrive.
            samples = maxReady ?? samples;
            stereoOutputSamples = samples * 2;
            Array.Clear(_stereoBuffer, 0, stereoOutputSamples);

            // No matter what else we do, keep the samples moving.
            foreach (var stream in streams)
            {
                if (maxReady.HasValue)
                {
                    var mr = maxReady.Value;
                    if (stream.Scratch.Length < mr)
                    {
                        stream.Scratch = new float[mr];
                    }
                    int drained = stream.Buffer.DrainTo(stream.Scratch.AsSpan()[..mr])!.Value;
                    if (drained > 0 && drained != mr)
                    {
                        throw new Exception($"Expected {mr} samples, got {drained}");
                    }
                    // Automatic level control (ALC)
                    for (int i = 0; i < drained; ++i)
                    {
                        stream.Alc.Apply(Math.Abs(stream.Scratch[i]));
                        // Avoid ALC asymptotes as measured volume drops to 0
                        // (unlikely for users to give us a perfectly silent signal,
                        // but for file playback/test tones...)
                        stream.Scratch[i] /= Math.Max(stream.Alc.D1, 0.01f);
                    }
                    
                    // Apply radio effects
                    if (Apply3dEffects)
                    {
                        stream.RadioEffect.Process(stream.Scratch, 0, drained);
                    }
                    
                    for (int i = drained; i < samples; ++i) stream.Alc.Apply(0f);
                    stream.Samples = stream.Scratch.AsMemory()[..drained];
                }
                else
                {
                    // Decay ALC
                    for (int i = 0; i < samples; ++i) stream.Alc.Apply(0f);
                    stream.Samples = new Memory<float>();
                }
            }

            // 2: Process each frequency (noise + squelch + mixing), AKA radio,
            //    which should have its own AGC, Squelch, etc.
            foreach (var kvp in frequencySnapshot)
            {
                int freq = kvp.Key;
                var freqConfig = kvp.Value;

                // If we're not tuned or we're muted, we don't need to process anything,
                // but we should make sure to drain any buffered samples.
                if (!freqConfig.IsTuned || freqConfig.IsNoiseMuted ||
                    transmittingFrequencies.Contains(freq))
                {
                    continue;
                }

                // Get pre-grouped streams for this frequency
                // Don't bail early if these are empty;
                // still want to apply squelch sound and other FX.
                var freqStreams = streamsByFrequency.GetValueOrDefault(freq, []);

                // --- PER-FREQUENCY RF PARAMETERS ---
                var bandConfig = FastPathAudioSim.GetBandConfig(freq);

                // True if squelch opened at any point in this set of samples
                bool squelchOpened = false;
                // Typical squelch is at +6 dB, which is a factor of 2x.
                float squelchThreshold = freqConfig.SquelchLevel * 2.0f;

                // Mix transmitting streams
                if (Apply3dEffects)
                {
                    // Noise is always there!
                    // The question is just "how loud compared to the signal?"
                    // (What's the SNR?)
                    freqConfig.NoiseGenerator?.GenerateNoise(_dspScratch, 0, samples, 1.0f);
                    // We need random I *and* Q values - if we use
                    // I_noise[n] = Q_noise[n] = -dspScrach[n],
                    // we wouldn't have random noise,
                    // we'd have a single signal with a fixed phase (45 deg).
                    // TODO: Generate this each sample instead of filling buffers of noise?
                    freqConfig.NoiseGenerator?.GenerateNoise(_dspScratch2, 0, samples, 1.0f);

                    var transmittingStreams = freqStreams.Where(s => s.Samples.Length > 0).ToList();
                    // Sanity check:
                    // By our maxReady logic above, any streams _with_ samples should be the same length,
                    // and that lengh should be `samples`.
                    if (!transmittingStreams.Select(s => s.Samples.Length).All(l => l == samples))
                    {
                        throw new Exception("Active streams have different lengths");
                    }

                    if (transmittingStreams.Count > 0)
                    {
                        // TODO: Factor this out into a function.

                        // AM demodulators are envelope detectors
                        // (https://en.wikipedia.org/wiki/Envelope_detector)
                        // They pull out the modulated voice by extracting
                        // the shape (envelope) of the signal.
                        // This has some nice advantages:
                        //
                        // 1. Receivers don't have to perfectly match the channel frequency
                        //    of the transmitter - as long as a TX is in the passband of an RX,
                        //    we can recover the transmitted voice without any frequency errors
                        //    that would make it sound too high or too low.
                        //    (This is especially nice for fast aircraft, since the Doppler effect
                        //    means the frequencies are changing all the time!)
                        //
                        // 2. The electronics for an envelope detector are pretty cheap and simple.
                        //
                        // All is well when a single transmitter is sending on a frequency,
                        // but when *multiple* transmitters send at once, trouble starts.
                        // IRL radios are never tuned to the exact same frequency, since
                        // making two oscillators moving at several million cycles per second
                        // match perfectly is very hard - and so the carrier frequencies
                        // create beats (https://en.wikipedia.org/wiki/Beat_(acoustics)).
                        // Along with people talking over each other,
                        // the receiver hears some nasty effects:
                        //
                        // 1. The receiver hears tones at each of the beat frequencies.
                        //
                        // 2. The beat frequencies ring modulate the weaker voice -
                        //    each frequency f turns into two: f + beat and f - beat.
                        //    (Find some videos of guitar pedals and vocoders that apply
                        //    ring modulation for an example of what this sounds like.)
                        //
                        // 3. The louder voice amplitude-modulates the weaker one.
                        //
                        // 4. Low beat frequencies that fall inside the AGC's passband
                        //    can cause the volume to "pump" up and down.
                        //
                        // Instead of trying to simulate all this, we can calculate the real thing!
                        // For some signal s[n], its envelope is E[n] = sqrt(I[n]^2 + Q[n]^2)
                        // where (I + jQ) is the representation of the signal as a complex number
                        // (see https://en.wikipedia.org/wiki/In-phase_and_quadrature_components).
                        //
                        // And for each beat frequency k,
                        // I_k = cos(θ_k[n])
                        // Q_k = sin(θ_k[n])
                        // where θ_k[n] = 2π · beat[k] · n / F_s
                        //   and F_s is the sample rate.
                        // We'll multiply those terms by each AM signal A_k + v_k[n],
                        // where A_k is the received power of the carrier and v_k[n] is the modulated voice.
                        // All together, we get
                        //
                        // I[n] = Σ_k (A_k + v_k[n]) · cos(θ_k[n])
                        // Q[n] = Σ_k (A_k + v_k[n]) · sin(θ_k[n])
                        // E[n] = sqrt(I[n]² + Q[n]²)
                        //
                        // which gives us an envelope between 0 and 2.
                        // Shift that back to [-1, 1] and we have ourselves the envelope.

                        var numStreams = transmittingStreams.Count;
                        var relativePowers = new List<float>(numStreams);
                        var beats = new List<float>(numStreams);
                        // We can make any of the frequencies "0" and calculate beats off of it.
                        // Just pick the first transmitter in the list.
                        float zeroFreq = (float)transmittingStreams[0].CurrentParams.RadioFrequencyKHz * 1e3f;
                        zeroFreq += zeroFreq * transmittingStreams[0].CurrentParams.TuneOffsetPPM * 1e-6f;
                        for (int i = 0; i < numStreams; ++i)
                        {
                            // We need to convert from dB to linear power when weighing the signals.
                            var thisSnrLinear = Math.Pow(10, transmittingStreams[i].CurrentParams.ReceivedSnrDb / 20.0);
                            relativePowers.Add((float)thisSnrLinear);
                            if (i == 0)
                            {
                                beats.Add(0);
                            }
                            else
                            {
                                var thisFreq = (float)transmittingStreams[i].CurrentParams.RadioFrequencyKHz * 1e3f;
                                thisFreq += thisFreq * transmittingStreams[i].CurrentParams.TuneOffsetPPM * 1e-6f;
                                beats.Add(Math.Abs(thisFreq - zeroFreq));
                            }
                        }

                        // Calculate E[n] for each sample n.
                        for (int n = 0; n < samples; ++n)
                        {
                            // Start with our noise.
                            double i = _dspScratch[n];
                            double q = _dspScratch2[n];
                            // Real aircraft radios don't have 100% modulation.
                            // A bunch of the standards are paywalled, but those I've found
                            // suggest minimum specs are 85% modulation, with 90-95% being common.
                            // https://www.etsi.org/deliver/etsi_i_ets/300600_300699/300676/01_20_91/ets_300676e01c.pdf
                            // https://avweb.com/avionics/vhf-nav-comm-basics/
                            const double modIndex = 0.9;
                            for (int k = 0; k < numStreams; ++k)
                            {
                                // θ_k is the phasor that rotates around at each beat frequency k.
                                double theta = 2.0f * Math.PI * beats[k] *
                                    (double)(n + _sampleNum) / (double)SampleRate;
                                // Sum IQ components _before_ taking the length of the vector,
                                // as that's a nonlinear operation.
                                float samp = transmittingStreams[k].Samples.Span[n];
                                i += relativePowers[k] * (1 + samp * modIndex) * Math.Cos(theta);
                                q += relativePowers[k] * (1 + samp * modIndex) * Math.Sin(theta);
                            }

                            // Take the envelope.
                            _dspScratch[n] = (float)Math.Sqrt(i * i + q * q);

                            // Update the AGC:
                            freqConfig.Agc.Apply(_dspScratch[n]);

                            // Squelch is driven by the AGC gain.
                            // When it starts attenuating, we know we hear something.
                            // NB: Handle squelch per sample!
                            // We don't want to squelch every sample here (or not!) based on the final AGC value.
                            if (freqConfig.Agc.D1 >= squelchThreshold)
                            {
                                _dspScratch[n] = _dspScratch[n] / freqConfig.Agc.D1;
                                squelchOpened = true;
                            }
                            else
                            {
                                _dspScratch[n] = 0;
                            }
                        }
                    }
                    // Nothing is transmitting except noise, decay AGC back to unity.
                    else
                    {
                        for (int n = 0; n < samples; ++n)
                        {
                            double i = _dspScratch[n];
                            double q = _dspScratch2[n];
                            _dspScratch[n] = (float)Math.Sqrt(i * i + q * q);
                            freqConfig.Agc.Apply(_dspScratch[n]);

                            // See above.
                            if (freqConfig.Agc.D1 >= squelchThreshold)
                            {
                                _dspScratch[n] = _dspScratch[n] / freqConfig.Agc.D1;
                                squelchOpened = true;
                            }
                            else
                            {
                                _dspScratch[n] = 0;
                            }
                        }
                    }
                    for (int n = 0; n < samples; ++n)
                    {
                        // Bandpass the signal, which removes the DC component and centers us around 0
                        _dspScratch[n] = freqConfig.LowPass.Process(
                            freqConfig.HighPass.Process(_dspScratch[n]));
                    }

                    if (squelchOpened != freqConfig.WasSquelchOpen)
                    {
#if DEBUG
                        _logger.LogDebug(
                            "SQUELCH {State} (Freq: {Frequency}, SNR={SNR:F1}dB)",
                            squelchOpened ? "OPEN" : "CLOSED", freq, freqConfig.Agc.D1);
#endif

                        freqConfig.WasSquelchOpen = squelchOpened;
                    }
                }
                // Straight mix when we're not applying any FX
                else
                {
                    Array.Clear(_dspScratch, 0, samples);

                    int numTransmitting = freqStreams.Count;
                    if (numTransmitting > 0)
                    {
                        // Mix all streams with proper normalization
                        foreach (var t in freqStreams)
                        {
                            for (int i = 0; i < t.Samples.Length; ++i)
                            {
                                _dspScratch[i] += t.Samples.Span[i];
                            }
                        }

                        // Normalize by number of streams to prevent clipping
                        float mixGain = 1.0f / (float)Math.Sqrt(numTransmitting);
                        for (int i = 0; i < samples; ++i)
                        {
                            _dspScratch[i] *= mixGain;
                        }
                    }

                    // Set AGC back to unity so there's not sudden jumps
                    // when we turn FX back on.
                    freqConfig.Agc.D1 = 1;
                }

                // Final mix, split to stereo output.
                // Note that we _sum_, not set stereo buffer so that we can combine multiple radios.
                // Pan: -100=full left, 0=center (both at full), +100=full right.
                float volume = freqConfig.Volume;
                float leftGain  = volume * Math.Clamp((100 - freqConfig.Pan) / 100f, 0f, 1f);
                float rightGain = volume * Math.Clamp((100 + freqConfig.Pan) / 100f, 0f, 1f);
                for (int frame = 0; frame < samples; frame++)
                {
                    int leftIdx = frame * 2;
                    int rightIdx = leftIdx + 1;
                    _stereoBuffer[leftIdx]  += leftGain  * _dspScratch[frame];
                    _stereoBuffer[rightIdx] += rightGain * _dspScratch[frame];
                }
            }

            // Sidetone: mix own voice (mono) into stereo output
            // No AGC/ALC
            if (SidetoneEnabled)
            {
                int sidetoneAvail = _sidetoneBuffer.Available;
                if (sidetoneAvail > 0)
                {
                    int sidetoneFrames = Math.Min(sidetoneAvail, samples);
                    if (_sidetoneScratch.Length < sidetoneFrames)
                        _sidetoneScratch = new float[sidetoneFrames];
                    int drained = _sidetoneBuffer.Read(_sidetoneScratch.AsSpan()[..sidetoneFrames]);
                    float vol = SidetoneVolume;
                    for (int i = 0; i < drained; ++i)
                    {
                        float s = _sidetoneScratch[i] * vol;
                        _stereoBuffer[i * 2]     += s;
                        _stereoBuffer[i * 2 + 1] += s;
                    }
                }
            }

            // Peak-limit the mixed output: find the highest absolute sample,
            // and if it would clip, scale the entire buffer down uniformly.
            // Necessary when dealing with multiple incoming channels at once.
            var peak = 0f;
            for (int i = 0; i < stereoOutputSamples; ++i)
            {
                peak = MathF.Max(peak, MathF.Abs(_stereoBuffer[i]));
            }

            var limitGain = peak > 1f ? 1f / peak : 1f;
            for (var i = 0; i < stereoOutputSamples; ++i)
            {
                _stereoBuffer[i] = Math.Clamp(_stereoBuffer[i] * limitGain, -2f, 2f);
            }

            Marshal.Copy(_stereoBuffer, 0, bufferPtr, stereoOutputSamples);
            // Keep sinusoids phase-coherent across callbacks.
            // (At least until we roll over, but that's once every blue moon.)
            _sampleNum += samples;
        };

        _masterDspProcHandle = Bass.ChannelSetDSP(_masterStream, _dspProc, IntPtr.Zero);
        Bass.ChannelPlay(_masterStream);
    }


    public async Task StopAll()
    {
        _logger.LogInformation("Stopping all streams and cleaning up resources...");

        // 2. Stop all streams
        List<string> ids;
        lock (_lock)
        {
            ids = _streams.Keys.ToList();
        }

        foreach (var id in ids)
        {
            await StopStream(id);
        }

        // 3. Stop master stream
        StopMasterStream();

        // 4. Clear frequency configs
        lock (_lock)
        {
            _frequencies.Clear();
        }

        ClearTransmittingFrequencies();

        // 5. Free BASS device
        try
        {
            var currentDevice = Bass.CurrentDevice;
            if (currentDevice != -1)
            {
                Bass.Free();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error freeing BASS device");
        }
    }

    public List<string> GetActiveStreams()
    {
        lock (_lock) return _streams.Keys.ToList();
    }

    public List<string> GetStreamsOnFrequency(int frequencyKHz)
    {
        lock (_lock)
            return _streams.Values.Where(s => s.FrequencyKHz == frequencyKHz).Select(s => s.StreamId).ToList();
    }

    public bool IsStreamActive(string id)
    {
        lock (_lock) return _streams.TryGetValue(id, out _);
    }

    public List<int> GetActiveFrequencies()
    {
        lock (_lock) return _streams.Values.Select(s => s.FrequencyKHz).Distinct().OrderBy(x => x).ToList();
    }

    public AudioParams? GetStreamParams(string id)
    {
        lock (_lock)
        {
            if (_streams.TryGetValue(id, out var s)) return s.CurrentParams;
            return null;
        }
    }

    public int? GetStreamFrequency(string id)
    {
        lock (_lock)
        {
            if (_streams.TryGetValue(id, out var s)) return s.FrequencyKHz;
            return null;
        }
    }

    public void ChangeOutputDevice(int newDeviceIndex)
    {
        bool hadMasterStream = false;

        lock (_lock)
        {
            hadMasterStream = _masterStream != 0;
        }

        if (hadMasterStream)
        {
            StopMasterStream();
        }

        // Initialize the new device if not already initialized
        // Check if device is already initialized
        var deviceInfo = Bass.GetDeviceInfo(newDeviceIndex);
        if (!deviceInfo.IsInitialized)
        {
            // Initialize the new device
            if (!Bass.Init(newDeviceIndex, SampleRate, DeviceInitFlags.Default, IntPtr.Zero))
            {
                _logger.LogError($"Failed to initialize device {newDeviceIndex}: {Bass.LastError}");
                return;
            }
        }

        // Set current device for this thread
        Bass.CurrentDevice = newDeviceIndex;

        if (hadMasterStream)
        {
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
            _logger.LogDebug("PTT pressed - expecting RTP start marker (StreamId: {StreamId})", streamId);
        }
    }

    public void Dispose()
    {
        StopAll().Wait(500);
    }

    // Lock-free SPSC ring buffer for sidetone.
    // Producer = BASS recording thread. Consumer = BASS DSP thread.
    // Uses Volatile.Write (store-release) on the write index so the consumer sees
    // all buffer writes before the updated index, and Volatile.Read (load-acquire)
    // so each side observes the other's latest index across CPU cores / ARM reordering.
    private sealed class SidetoneSpscBuffer(int capacity)
    {
        private readonly float[] _buf = new float[NextPow2(capacity)];
        private readonly int _mask = NextPow2(capacity) - 1;
        private int _writeIdx;
        private int _readIdx;

        private static int NextPow2(int n) { int p = 1; while (p < n) p <<= 1; return p; }

        public int Available => Volatile.Read(ref _writeIdx) - Volatile.Read(ref _readIdx);

        // Called only from the recording thread.
        public void Write(ReadOnlySpan<float> src)
        {
            int wp = _writeIdx; // only writer touches _writeIdx — no Volatile.Read needed
            int free = _buf.Length - (wp - Volatile.Read(ref _readIdx));
            int n = Math.Min(src.Length, free);
            for (int i = 0; i < n; i++)
                _buf[(wp + i) & _mask] = src[i];
            Volatile.Write(ref _writeIdx, wp + n); // release: buffer writes visible before index update
        }

        // Called only from the DSP thread.
        public int Read(Span<float> dst)
        {
            int rp = _readIdx; // only reader touches _readIdx
            int n = Math.Min(dst.Length, Volatile.Read(ref _writeIdx) - rp); // acquire
            for (int i = 0; i < n; i++)
                dst[i] = _buf[(rp + i) & _mask];
            Volatile.Write(ref _readIdx, rp + n);
            return n;
        }

        // Safe to call from any thread — single store, worst case DSP sees stale index once.
        public void Clear() => Volatile.Write(ref _readIdx, Volatile.Read(ref _writeIdx));
    }
}