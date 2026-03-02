using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ManagedBass;
using ManagedBass.Mix;
using Microsoft.Extensions.Logging;

// ReSharper disable InconsistentNaming

namespace OpenFreqAudio;

/// <summary>
/// A Direct Form II Transposed biquad filter,
/// used to high-pass output.
/// (Radios pass ~300+ Hz)
/// </summary>
/// <see cref="https://en.wikipedia.org/wiki/Digital_biquad_filter#Direct_form_2"/>
class Biquad
{
    private float B0;
    private float B1;
    private float B2;
    // A0 is always 1
    private float A1;
    private float A2;

    // Delays
    private float D1 = 0;
    private float D2 = 0;

    public Biquad(float b0, float b1, float b2, float a1, float a2)
    {
        B0 = b0;
        B1 = b1;
        B2 = b2;
        A1 = a1;
        A2 = a2;
    }

    public float Apply(float x)
    {
        // y[n] = b0 * x[n] + d1
        // d1 = b1 * x[n] - a1 * y[n] + d2
        // d2 = b2 * x[n] - a2 * y[n]
        float y = B0 * x + D1;
        D1 = B1 * x - A1 * y + D2;
        D2 = B2 * x - A2 * y;
        return y;
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
        public int Channels { get; set; } // 1=mono,2=stereo
        public bool IsPush { get; set; } // true for WebRTC / pushed audio
        public required RadioEffect RadioEffect { get; set; }
        public required AudioParams CurrentParams { get; set; }

        // For decoded or pulled audio we reuse Buffer as a temporary buffer
        public float[] Buffer { get; set; } = new float[8192];

        // Ring buffer used by both push streams and file-reader task
        public float[] RingBuffer { get; set; } = Array.Empty<float>();
        private int _ringWritePos;
        private int _ringReadPos;
        private int _ringCount; // number of floats in buffer
        private readonly object _ringLock = new();

        public RadioStream(ILogger logger)
        {
            _logger = logger;
        }

        // For file-based streams we run a reader task that decodes and pushes into the ring
        public CancellationTokenSource? FileReaderCts { get; set; }
        public Task? FileReaderTask { get; set; }

        public bool HasReceivedAudio { get; set; }
        public DateTime LastAudioReceived { get; set; } = DateTime.MinValue;

        // Transmission state (separate from stream lifecycle)
        public bool IsTransmitting { get; set; }
        public AmbientNoiseType AmbientNoise { get; set; } = AmbientNoiseType.None;
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
#if DEBUG
                if (framesDropped > 1000)
                {
                    float fillPercent = (float)_ringCount / RingBuffer.Length * 100f;
                    _logger.LogWarning(
                        "OVERFLOW! Dropped {DroppedFrames} frames to make room. Buffer was {FillPercent:F1}% full (StreamId: {StreamId})",
                        framesDropped, fillPercent, StreamId);
                }
#endif

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
                    _logger.LogDebug(
                        "Buffering complete! {FrameCount} frames buffered ({FillPercent:F1}% full) (StreamId: {StreamId})",
                        _ringCount / Channels, (float)_ringCount / RingBuffer.Length * 100f, StreamId);
#endif
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
#if DEBUG
                        _logger.LogDebug(
                            "Buffering complete! {AvailableFrames} frames buffered ({FillPercent:F1}% full) (StreamId: {StreamId})",
                            availableFrames, fillPercent, StreamId);
#endif
                    }
                    // Fallback exit condition: stream stopped but we have substantial data (>60% of target)
                    // Wait 200ms after last packet to ensure stream truly stopped
                    else if (HasReceivedAudio &&
                             availableFrames >= (MinBufferFrames * 60) / 100 &&
                             (DateTime.UtcNow - LastAudioReceived).TotalMilliseconds > 200)
                    {
                        IsBuffering = false;
#if DEBUG
                        _logger.LogDebug(
                            "Buffering timeout! Stream inactive, using {AvailableFrames} frames ({FillPercent:F1}% full) (StreamId: {StreamId})",
                            availableFrames, fillPercent, StreamId);
#endif
                    }
                    // Still buffering - log progress and return silence
                    else
                    {
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
                        _logger.LogWarning(
                            "UNDERRUN! Requested {RequestedFrames} frames, only {AvailableFrames} available ({FillPercent:F1}% full, {Count}/{Capacity}) (StreamId: {StreamId})",
                            frameCount, availableFrames, fillPercent, _ringCount, RingBuffer.Length, StreamId);
                    }

                    // Enter rebuffering mode if buffer critically low (< 5%)
                    if (fillPercent < 5f && !IsBuffering)
                    {
                        IsBuffering = true;
                        _logger.LogWarning(
                            "Buffer critically low ({FillPercent:F1}%) - entering rebuffering mode (StreamId: {StreamId})",
                            fillPercent, StreamId);
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
        public AudioChannel AudioChannel { get; set; } = AudioChannel.Both;
        public bool IsTuned { get; set; }
        public BackgroundNoiseGenerator? NoiseGenerator { get; set; }
        public bool WasHearableLastFrame { get; set; }

        public bool IsNoiseMuted { get; set; }
        public float SquelchLevel { get; set; } = 1.0f;
        // ReSharper restore UnusedAutoPropertyAccessor.Local

        public bool WasSquelchOpen { get; set; }

        public Func<float, float> HighPass { get; } = MakeHighPass();

        // AGC gain; varies as a low-pass of the received signal
        // according to attack and decay params below.
        public double AgcGain { get; set; } = 0;

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

    public enum AudioChannel
    {
        Left,
        Right,
        Both
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
    public const int SampleRate = 8000;

    private readonly int _channels = 2; // Always use Stereo output

    private const int NoiseFadeSamples = 2400;

    private static readonly Lock _bassInitLock = new();
    private bool _timeoutMonitoringStarted;
    private CancellationTokenSource? _timeoutMonitorCts;
    private Task? _timeoutMonitorTask;

    private int _masterDspProcHandle;
    

    public bool Apply3dEffects { get; set; }

    /// <summary>
    /// Creates a 6th-order Butterworth high-pass filter.
    /// </summary>
    /// <remarks>
    /// Each biquad gives us ~20dB per decade, so a single one has a very gradual dropoff,
    /// but three is a good compromise - if our cutoff is 300Hz,
    /// at 200Hz we have -60dB of attenuation.
    /// </remarks>
    /// <returns>A lambda that contains the filter state and applies it each call.</returns>
    private static Func<float, float> MakeHighPass(double cutoff = 300, double sampleRate = SampleRate)
    {
        double t = 1.0 / sampleRate;
        double k = 2.0 / t;
        double wc = k * Math.Tan(Math.PI * cutoff / sampleRate);

        // 6th-order Butterworth pole angles:
        // For order n, prototype poles are at angles θ_k = π·(2k + n + 1) / (2n)
        // for k = 0..n-1 on the unit circle in the s-plane.
        //
        // For n=6, the 6 poles are at angles:
        //   θ = π·(2k+7)/12  for k = 0..5
        //     = 7π/12, 9π/12, 11π/12, 13π/12, 15π/12, 17π/12
        //
        // Conjugate pairs (sharing the same real part):
        //   Pair 0: k=0,5 → θ = 7π/12, 17π/12  → real part = cos(7π/12)
        //   Pair 1: k=1,4 → θ = 9π/12, 15π/12  → real part = cos(9π/12)
        //   Pair 2: k=2,3 → θ = 11π/12, 13π/12 → real part = cos(11π/12)
        //
        // Each conjugate pair (σ ± jω) gives a 2nd order section:
        //   s² + 2|σ|·s + 1  in the normalized prototype
        //
        // The coefficient 2|σ| = 2·cos(π·(2k+1)/(2n)) for the kth pair.
        int n = 6;
        double k2 = k * k;
        double wc2 = wc * wc;
        double overallGain = 1;

        List<(double, double)> pairs = [];
        for (int pair = 0; pair < n / 2; ++pair)
        {
            // Angle of the pole in the upper half-plane
            double theta = Math.PI * (2 * pair + n + 1) / (2 * n);

            // Prototype 2nd order denominator: s² + alpha·s + 1
            // where alpha = -2·cos(theta) = 2·|real part|
            double alpha = -2.0 * Math.Cos(theta);

            // Low to high-pass transform and bilinear transform
            double aKwc = alpha * k * wc;

            double d0 = k2 + aKwc + wc2;
            double d1 = -2.0 * k2 + 2.0 * wc2;
            double d2 = k2 - aKwc + wc2;

            // Normalize coefficients (a0 = 1);
            double a1 = d1 / d0;
            double a2 = d2 / d0;

            // Accumulate this section's gain: K²/d0
            // All zeros are at z=1 (DC), so b is proportional to [1, -2, 1]
            overallGain *= k2 / d0;

            pairs.Add((a1, a2));
        }

        // Assemble biquad coefficients
        // Standard convention: b = [1, -2, 1] for all sections except the first,
        // which absorbs the overall gain. Sections ordered low-Q to high-Q.
        pairs.Reverse();

        List<Biquad> biquads = [];
        for (int i = 0; i < pairs.Count; ++i)
        {
            var (a1, a2) = pairs[i];
            if (i == 0)
            {
                float og = (float)overallGain;
                // First section carries the overall gain
                biquads.Add(new Biquad(og, -2.0f * og, og, (float)a1, (float)a2));
            }
            else
            {
                biquads.Add(new Biquad(1.0f, -2.0f, 1.0f, (float)a1, (float)a2));
            }
        }

        return x =>
        {
            foreach (var b in biquads)
            {
                x = b.Apply(x);
            }
            return x;
        };
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
            Bass.Configure(Configuration.PlaybackBufferLength, 40);
            Bass.Configure(Configuration.DeviceBufferLength, 10);
            Bass.Configure(Configuration.UpdateThreads, 2);
            
            #if !WINDOWS
            // Use explicit path on non-Windows to avoid strange .NET lib*.so wrangling issues
            // We don't need to free it explicitly, this is covered by BASS 
            NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "libbassmix.so"));
            #endif
        }

        // Start peer timeout monitoring
        StartPeerTimeoutMonitoring();
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

            var stream = new RadioStream(_logger)
            {
                StreamId = streamId,
                FrequencyKHz = audioParams.RadioFrequencyKHz,
                BassStreamHandle = bassStream,
                BassMixerHandle = mixer,
                Channels = info.Channels,
                IsPush = false,
                RadioEffect = new RadioEffect(SampleRate, info.Channels, audioParams,
                    _loggerFactory.CreateLogger<RadioEffect>())
                    { AmbientNoise = ambientNoise },
                CurrentParams = audioParams,
                Buffer = new float[MaxBufferSize],
                IsTransmitting = true,
                AmbientNoise = ambientNoise
            };

            // Allocate a ring buffer (5 seconds worth of audio)
            int ringFrames = SampleRate * 5;
            stream.EnsureRingBufferCapacity(ringFrames * Math.Max(1, stream.Channels));

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
                    int chunkSamples = chunkFrames * stream.Channels;
                    float[] readBuffer = new float[chunkSamples];
                    int consecutiveNoData = 0;
                    int totalFramesRead = 0;

                    while (!token.IsCancellationRequested)
                    {
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
                "Added file stream {StreamId}: Freq={Frequency:F3}MHz, Power={Power}dBm, Channels={Channels}, NativeRate={NativeRate}Hz, ResampledTo={InputRate}Hz, RingBuffer={RingBufferSize} floats",
                streamId, audioParams.RadioFrequencyKHz / 1000.0, audioParams.ReceivedDb, info.Channels, info.Frequency,
                SampleRate, stream.RingBuffer.Length);
        }
    }

    public void StartPushStream(string streamId, int sampleRate, int channels, AudioParams audioParams)
    {
        if (sampleRate != SampleRate)
        {
            throw new ArgumentException($"Push stream had sample rate of {sampleRate}, expected {SampleRate}");
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
                Channels = channels,
                IsPush = true,
                RadioEffect = new RadioEffect(sampleRate, channels, audioParams,
                    _loggerFactory.CreateLogger<RadioEffect>()),
                CurrentParams = audioParams,
                Buffer = new float[MaxBufferSize],
            };

            int ringFrames = (sampleRate * 150) / 1000; // 150ms
            int minBufferFrames = (sampleRate * 120) / 1000; // 120ms
            int ringCapacity = ringFrames * Math.Max(1, channels);
            stream.EnsureRingBufferCapacity(ringCapacity);
            stream.MinBufferFrames = minBufferFrames;
            stream.IsBuffering = true;

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
    public bool PushAudioData(string streamId, byte[] audioData, bool startMarker = false, bool endMarker = false,
        AmbientNoiseType ambientNoise = AmbientNoiseType.None)
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

            // Update packet receipt timestamp for timeout detection
            stream.LastPacketReceived = DateTime.UtcNow;

            // Propagate ambient noise type to the effect processor so the correct
            // SFX layer (Air / Ground / Stationary) is applied per packet.
            if (stream.AmbientNoise != ambientNoise)
            {
                stream.AmbientNoise = ambientNoise;
                stream.RadioEffect.AmbientNoise = ambientNoise;
            }

            // Detect implicit transmission start:
            // 1. First audio ever (stream just created)
            // 2. Transmission gap (>500ms since last audio and not currently transmitting)
            bool isFirstAudio = !stream.HasReceivedAudio;
            bool hasGapAfterEnd = stream.HasReceivedAudio &&
                                  !stream.IsTransmitting &&
                                  (DateTime.UtcNow - stream.LastAudioReceived).TotalMilliseconds > 500;
            bool implicitStart = isFirstAudio || hasGapAfterEnd;

            // Handle transmission start marker (explicit or implicit)
            if ((startMarker || implicitStart) && !stream.IsTransmitting)
            {
                stream.IsTransmitting = true;
                stream.TransmissionStartTime = DateTime.UtcNow;
                stream.IsBuffering = true;

                string reason = startMarker ? "marker" : (isFirstAudio ? "first-audio" : "gap-restart");
                _logger.LogDebug("Transmission START ({Reason}) (StreamId: {StreamId})", reason, streamId);
            }

            // Sanity check: both markers set (shouldn't happen but handle gracefully)
            if (startMarker && endMarker)
            {
                _logger.LogWarning("Both start and end markers set! (StreamId: {StreamId})", streamId);
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
            _logger.LogDebug(
                "Pushed {Frames} frames ({Bytes} bytes, {BitsPerSample}-bit), buffer now {FillPercent:F1}% full ({FillCount}/{Capacity}) (StreamId: {StreamId})",
                frames, audioData.Length, bytesPerSample * 8, fillPercent, fillCount, capacity, streamId);
            #endif

            // Handle transmission end marker
            // Process AFTER pushing audio so this final packet's audio is included
            if (endMarker && stream.IsTransmitting)
            {
                var duration = DateTime.UtcNow - stream.TransmissionStartTime;
                stream.IsTransmitting = false;
                stream.TransmissionEndTime = DateTime.UtcNow;
                _logger.LogInformation("Transmission END (marker) - duration: {Duration:F2}s (StreamId: {StreamId})",
                    duration.TotalSeconds, streamId);
            }

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
        bool needsStart = false;

        lock (_lock)
        {
            if (!_frequencies.ContainsKey(frequencyKHz))
                _frequencies[frequencyKHz] = new RadioConfig();

            var freqConfig = _frequencies[frequencyKHz];

            freqConfig.IsTuned = true;
            if (freqConfig.NoiseGenerator == null)
            {
                freqConfig.NoiseGenerator = new BackgroundNoiseGenerator(SampleRate, _channels, frequencyKHz);

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

    public void SetFrequencyAudioChannel(int frequencyKHz, AudioChannel channel)
    {
        lock (_lock)
        {
            if (!_frequencies.ContainsKey(frequencyKHz))
                _frequencies[frequencyKHz] = new RadioConfig();
            _frequencies[frequencyKHz].AudioChannel = channel;
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

        _masterStream = Bass.CreateStream(SampleRate, _channels, BassFlags.Float, streamProc, IntPtr.Zero);
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
            // Output is always stereo, so we can mix left & right ear outputs.
            if (_channels != 2)
            {
                throw new ArgumentException("Only stereo playback supported");
            }
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

            // Snapshot state for UI consumption
            List<RadioStream> activeStreams;
            Dictionary<int, RadioConfig> frequencySnapshot;
            HashSet<int>? transmittingFrequencies = null;
            lock (_lock)
            {
                if (Apply3dEffects)
                {
                    activeStreams = _streams.Values.Where(s =>
                    {
                        var bc = FastPathAudioSim.GetBandConfig(s.FrequencyKHz);
                        return s.CurrentParams.ReceivedSnrDb > 0;
                    }).ToList();

                    // Snapshot transmitting frequencies - but only when we are in 3D Mode
                    transmittingFrequencies = new HashSet<int>(_transmittingFrequencies);
                }
                else
                {
                    activeStreams = _streams.Values.ToList();
                }

                frequencySnapshot = new Dictionary<int, RadioConfig>(_frequencies);
            }

            // Pre-group streams by frequency
            // TODO: Process each frequency in-line here,
            //       instead of creating the intermediate dict?
            var streamsByFrequency = new Dictionary<int, List<RadioStream>>();
            foreach (var stream in activeStreams)
            {
                if (stream.Channels != 1)
                {
                    throw new ArgumentException("Only mono streams supported");
                }

                int freq = stream.FrequencyKHz;
                if (!streamsByFrequency.TryGetValue(freq, out var list))
                {
                    list = new List<RadioStream>();
                    streamsByFrequency[freq] = list;
                }

                list.Add(stream);
            }

            // Important: Don't process more samples than we can read from all streams
            //            (desync is very bad)
            int maxReady = samples;
            foreach (var stream in activeStreams.Where(s => s.IsTransmitting && !s.IsBuffering))
            {
                var (count, capacity) = stream.GetRingBufferFillLevel();
    
                // Ignore streams with critically low buffers (<10%)
                // They're finishing transmission and shouldn't throttle other streams
                float fillPercent = (float)count / capacity * 100f;
                if (fillPercent < 10f)
                {
                    continue;  // Skip this stream - don't let it throttle others
                }
    
                maxReady = Math.Min(maxReady, count);
            }

            if (maxReady > 0)
            {
                samples = maxReady;
                stereoOutputSamples = samples * 2;

                foreach (var stream in activeStreams)
                {
                    int framesRead = stream.ReadFromRing(stream.Buffer, samples);
                    if (framesRead == 0)
                    {
                        Array.Clear(stream.Buffer, 0, stream.Buffer.Length);
                        continue;
                    }

                    if (Apply3dEffects)
                    {
                        stream.RadioEffect.Process(stream.Buffer, 0, samples);
                    }
                }
            }
            Array.Clear(_stereoBuffer, 0, stereoOutputSamples);

            // 2: Process each frequency (noise + squelch + mixing), AKA radio,
            //    which should have its own AGC, Squelch, etc.
            foreach (var kvp in frequencySnapshot)
            {
                int freq = kvp.Key;
                var freqConfig = kvp.Value;
                if (!freqConfig.IsTuned || freqConfig.IsNoiseMuted) continue;

                // Get pre-grouped streams for this frequency
                // Don't bail early if these are empty;
                // still want to apply squelch sound and other FX.
                var freqStreams = streamsByFrequency.TryGetValue(freq, out var streams)
                    ? streams
                    : new List<RadioStream>();

                // --- PER-FREQUENCY RF PARAMETERS ---
                var bandConfig = FastPathAudioSim.GetBandConfig(freq);

                var transmittingStreams = freqStreams.Where(s => s.IsTransmitting).ToList();

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
                                i += relativePowers[k] * (1 + transmittingStreams[k].Buffer[n] * modIndex) *
                                    Math.Cos(theta);
                                q += relativePowers[k] * (1 + transmittingStreams[k].Buffer[n] * modIndex) *
                                    Math.Sin(theta);
                            }

                            // Take the envelope.
                            _dspScratch[n] = (float)Math.Sqrt(i * i + q * q);
                            // AGC time: are we attacking or decaying?
                            // See a discussion of the given time constants _agcAttack and _agcDecay
                            // at their declaration.
                            double tau = _dspScratch[n] > freqConfig.AgcGain ?
                                RadioConfig.AgcAttack : RadioConfig.AgcDecay;
                            double alpha = 1 - Math.Exp(-1 / (SampleRate * tau));
                            // Update the AGC:
                            freqConfig.AgcGain = alpha * _dspScratch[n] + (1 - alpha) * freqConfig.AgcGain;

                            // High-pass the signal, which removes the DC component and centers us around 0
                            _dspScratch[n] = freqConfig.HighPass(_dspScratch[n]);

                            // Squelch is driven by the AGC gain.
                            // When it starts attenuating, we know we hear something.
                            // NB: Handle squelch per sample!
                            // We don't want to squelch every sample here (or not!) based on the final AGC value.
                            if (freqConfig.AgcGain >= squelchThreshold)
                            {
                                // Apply AGC, then remove our DC component, i.e.,
                                // shift our envelope from [0, 2] back to [-1, 1].
                                // Real electronics would use some high-pass filter that notches out 0 Hz.
                                _dspScratch[n] = _dspScratch[n] / (float)freqConfig.AgcGain;
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
                        var alpha = 1 - Math.Exp(-1 / (SampleRate * RadioConfig.AgcDecay));
                        for (int n = 0; n < samples; ++n)
                        {
                            double i = _dspScratch[n];
                            double q = _dspScratch2[n];
                            _dspScratch[n] = (float)Math.Sqrt(i * i + q * q);
                            freqConfig.AgcGain = alpha * _dspScratch[n] + (1 - alpha) * freqConfig.AgcGain;

                            _dspScratch[n] = freqConfig.HighPass(_dspScratch[n]);

                            // See above.
                            if (freqConfig.AgcGain >= squelchThreshold)
                            {
                                _dspScratch[n] = _dspScratch[n] / (float)freqConfig.AgcGain;
                                squelchOpened = true;
                            }
                            else
                            {
                                _dspScratch[n] = 0;
                            }
                        }
                    }

                    // Was previously above, but is all downstream of squelch, so:
                    lock (_lock)
                    {
                        if (_frequencies.TryGetValue(freq, out var fc))
                            fc.WasHearableLastFrame = squelchOpened;
                    }

                    // Use explicit transmission state from RTP markers
                    bool hasActiveTransmission = freqStreams.Any(s => s.IsTransmitting);

                    // Check packet freshness ONLY for push streams (network-based)
                    bool hasRecentPackets = freqStreams.Any(s =>
                        s.IsTransmitting &&
                        (s.IsPush == false || (DateTime.UtcNow - s.LastPacketReceived).TotalMilliseconds < 200));

                    // Force-end stale transmissions (ONLY for push streams)
                    foreach (var s in freqStreams.Where(s =>
                                s.IsPush &&
                                s.IsTransmitting &&
                                (DateTime.UtcNow - s.LastPacketReceived).TotalMilliseconds >= 200))
                    {
#if DEBUG
                        _logger.LogDebug("Force-ending stale transmission for {StreamId}", s.StreamId);
#endif

                        s.IsTransmitting = false;
                        s.TransmissionEndTime = DateTime.UtcNow;
                    }

                    bool isActiveTransmission = hasActiveTransmission && hasRecentPackets;

                    if (squelchOpened != freqConfig.WasSquelchOpen)
                    {
#if DEBUG
                        _logger.LogDebug(
                            "SQUELCH {State} (Freq: {Frequency}, SNR={SNR:F1}dB)",
                            squelchOpened ? "OPEN" : "CLOSED", freq, freqConfig.AgcGain);
#endif

                        freqConfig.WasSquelchOpen = squelchOpened;
                    }
                }
                // Straight mix when we're not applying any FX
                else
                {
                    Array.Clear(_dspScratch, 0, samples);
    
                    int numTransmitting = transmittingStreams.Count;
                    if (numTransmitting > 0)
                    {
                        // Mix all streams with proper normalization
                        foreach (var t in transmittingStreams)
                        {
                            for (int i = 0; i < samples; ++i)
                            {
                                _dspScratch[i] += t.Buffer[i];
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
                    freqConfig.AgcGain = 1;
                }

                // Final mix, split to stereo output.
                // Note that we _sum_, not set stereo buffer so that we can combine multiple radios.
                float volume = freqConfig.Volume;
                for (int frame = 0; frame < samples; frame++)
                {
                    int leftIdx = frame * 2;
                    int rightIdx = leftIdx + 1;
                    switch (freqConfig.AudioChannel)
                    {
                        case AudioChannel.Left: _stereoBuffer[leftIdx] += volume * _dspScratch[frame]; break;
                        case AudioChannel.Right: _stereoBuffer[rightIdx] += volume * _dspScratch[frame]; break;
                        case AudioChannel.Both:
                            _stereoBuffer[leftIdx] += volume * _dspScratch[frame];
                            _stereoBuffer[rightIdx] += volume * _dspScratch[frame];
                            break;
                    }
                }
            }

            // Final output clamping
            // TODO: We could add peak limiting to prevent hard clipping.
            for (int i = 0; i < stereoOutputSamples; ++i)
            {
                _stereoBuffer[i] = Math.Clamp(_stereoBuffer[i], -2f, 2f);
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
        // 1. Stop timeout monitor first
        try
        {
            _timeoutMonitorCts?.Cancel();
            if (_timeoutMonitorTask != null)
            {
                await _timeoutMonitorTask.WaitAsync(TimeSpan.FromSeconds(1));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping timeout monitor");
        }
        finally
        {
            _timeoutMonitorCts?.Dispose();
            _timeoutMonitorCts = null;
            _timeoutMonitorTask = null;

            lock (_lock)
            {
                _timeoutMonitoringStarted = false;
            }
        }

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
                            _logger.LogWarning(
                                "Force-ending transmission - RTP end marker missing (last packet {StallTime:F0}ms ago) (StreamId: {StreamId})",
                                stallTime.TotalMilliseconds, streamId);
                            s.IsTransmitting = false;
                            s.TransmissionEndTime = DateTime.UtcNow;
                        }
                    }
                });
            }

            _logger.LogDebug("PTT released - expecting RTP end marker (StreamId: {StreamId})", streamId);
        }
    }

    /// <summary>
    /// Monitors push streams for peer timeouts during active transmissions
    /// </summary>
    public void StartPeerTimeoutMonitoring()
    {
        lock (_lock)
        {
            if (_timeoutMonitoringStarted)
            {
                _logger.LogWarning("Already started");
                return;
            }

            _timeoutMonitoringStarted = true;
            _timeoutMonitorCts = new CancellationTokenSource();
        }

        _timeoutMonitorTask = Task.Run(async () =>
        {
            _logger.LogInformation("Starting peer timeout monitoring");

            try
            {
                while (!_timeoutMonitorCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(500, _timeoutMonitorCts.Token);

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
                            _logger.LogWarning(
                                "Peer timeout during transmission (no packets for {StallTime:F1}s) (StreamId: {StreamId})",
                                stallTime.TotalSeconds, stream.StreamId);

                            stream.IsTransmitting = false;
                            stream.TransmissionEndTime = now;
                            stream.ClearRingBuffer();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Stopped");
            }
        }, _timeoutMonitorCts.Token);
    }

    public void Dispose()
    {
        StopAll().Wait(500);
    }
}