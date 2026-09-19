namespace OpenFreqAudio;

/// <summary>
/// The audio format every part of OpenFreq shares: the rate the radio DSP, the Opus
/// codec and the sound devices all run at, and the frame everything paces audio in.
/// </summary>
public static class AudioFormat
{
    // Baseband is 8 kHz (4kHz Nyquist)
    // NB: Opus only accepts 8000, 12000, 16000, 24000, or 48000 Hz
    public const int SampleRate = 48000;

    // One Opus frame, which is the unit everything upstream paces audio in.
    // The RTP sender encodes one per packet, the jitter buffer releases one per playout
    // slot and conceals a missing one, and OggOpusRecorder writes one at a time.
    public const int OpusFrameMs = 20;
    public const int OpusSamplesPerFrame = SampleRate * OpusFrameMs / 1000;
}
