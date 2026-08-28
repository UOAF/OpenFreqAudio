using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;
using Concentus.Structs;
using Microsoft.Extensions.Logging;

namespace OpenFreqAudio;

/// <summary>
/// Writes an interleaved stereo float mix to an Ogg Opus file.
/// </summary>
/// <remarks>
/// Samples are handed over on the audio (DSP) thread and encoded on our own thread, so neither
/// the Opus encode nor the file write ever happens in the audio callback. This mirrors what
/// RtpAudioSender does for the network path, and what BASSenc's EncodeFlags.Queue used to do
/// for us before the codec swap from Vorbis.
/// </remarks>
internal sealed class OggOpusRecorder : IDisposable
{
    // 20ms @ 48kHz stereo, interleaved. Matches OpusOggWriteStream's own internal frame size.
    private const int FrameSamplesPerChannel = RadioPlayback.SampleRate / 50;
    private const int FrameFloats = FrameSamplesPerChannel * Channels;

    private const int Channels = 2;

    // Xiph puts fullband stereo speech at 32-48kbps; the mix is band-limited radio audio, so
    // the bottom of that range is plenty. https://wiki.xiph.org/Opus_Recommended_Settings
    private const int Bitrate = 32000;

    // Flush a page roughly every second so an unclean shutdown costs at most that much audio.
    private const int FramesPerFlush = 50;

    private readonly ILogger _logger;
    private readonly SyncRope<float> _queue = new();
    private readonly Thread _encodeThread;
    private readonly FileStream _file;
    private readonly IOpusEncoder _encoder;
    private readonly OpusOggWriteStream _ogg;

    // Set if the encode thread dies; stops us queueing samples nobody will ever drain.
    private volatile bool _faulted;
    private bool _disposed;

    public OggOpusRecorder(string filePath, ILogger logger)
    {
        _logger = logger;
        _file = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        try
        {
#pragma warning disable CS0618 // Do not use the factory - it does not work with Linux
            _encoder = new OpusEncoder(RadioPlayback.SampleRate, Channels,
                OpusApplication.OPUS_APPLICATION_AUDIO);
#pragma warning restore CS0618
            // Do *not* use VOIP mode since we don't want to filter the radio FX
            // through processing designed for voice only.
            // Leave _encoder.SignalType alone
            _encoder.Bitrate = Bitrate;
            _encoder.Complexity = 10; // A whole thread, and 20ms of budget per 20ms of audio.
            _ogg = new OpusOggWriteStream(_encoder, _file);
        }
        catch
        {
            _encoder?.Dispose();
            _file.Dispose();
            throw;
        }

        _encodeThread = new Thread(EncodeThreadProc)
        {
            IsBackground = true,
            Name = "OggOpusRecorder",
        };
        _encodeThread.Start();
    }

    /// <summary>
    /// Hand one interleaved stereo frame to the encode thread. Called on the DSP thread; never blocks.
    /// </summary>
    public void Write(ReadOnlySpan<float> interleavedStereo)
    {
        // SyncRope.Fill takes the memory by reference, and our caller reuses its buffer
        // every callback, so we have to hand over a copy.
        if (!_faulted) _queue.Fill(interleavedStereo.ToArray());
    }

    private void EncodeThreadProc()
    {
        var frame = new float[FrameFloats];
        int sinceFlush = 0;
        try
        {
            // DrainExactly keeps handing out whole frames after Close() and only gives up once
            // fewer than a frame remain, so stopping costs us at most the final partial frame
            // (<20ms) — inconsequential at the end of a session.
            while (_queue.DrainExactly(frame))
            {
                _ogg.WriteSamples(frame, 0, frame.Length);
                if (++sinceFlush >= FramesPerFlush)
                {
                    sinceFlush = 0;
                    _file.Flush();
                }
            }
        }
        catch (Exception ex)
        {
            _faulted = true;
            _queue.Clear();
            _logger.LogError(ex, "Opus encode thread died; recording is truncated");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Closing lets the encode thread finish the backlog and then stop; it does not discard
        // what is still queued, so the recording keeps everything but the final partial frame.
        _queue.Close(); // Sentinel kills the thread
        _encodeThread.Join();

        try
        {
            // Writes the end-of-stream page. Only safe now that the encode thread is done.
            if (!_faulted) _ogg.Finish();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to finalize Ogg stream");
        }

        _encoder.Dispose();
        _file.Dispose();
    }
}
