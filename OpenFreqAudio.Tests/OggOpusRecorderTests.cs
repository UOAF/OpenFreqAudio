using System;
using System.IO;
using System.Text;
using Concentus.Oggfile;
using Concentus.Structs;
using Microsoft.Extensions.Logging.Abstractions;

namespace OpenFreqAudio.Tests
{
    /// <summary>
    /// Tests for the session recorder's Ogg Opus write path. Unlike the BASSenc encoder it
    /// replaced, OggOpusRecorder needs no audio device, so we can round-trip a real file here.
    /// </summary>
    public class OggOpusRecorderTests
    {
        private const int SampleRate = RadioPlayback.SampleRate;
        private const int Channels = 2;
        private const int SamplesPerChannel = SampleRate * 2; // 2 seconds

        /// <summary>
        /// Push a 2s stereo tone (different pitch per channel) through the recorder in
        /// deliberately non-frame-aligned chunks, so the SyncRope handoff has to reassemble
        /// them into whole 20ms Opus frames.
        /// </summary>
        private static void WriteToneFile(string path)
        {
            using (var recorder = new OggOpusRecorder(path, NullLogger.Instance))
            {
                const int chunkSamplesPerChannel = 240; // a quarter frame
                var chunk = new float[chunkSamplesPerChannel * Channels];
                for (int start = 0; start < SamplesPerChannel; start += chunkSamplesPerChannel)
                {
                    for (int i = 0; i < chunkSamplesPerChannel; i++)
                    {
                        double t = (start + i) / (double)SampleRate;
                        chunk[i * 2] = 0.5f * (float)Math.Sin(2 * Math.PI * 440 * t);
                        chunk[i * 2 + 1] = 0.5f * (float)Math.Sin(2 * Math.PI * 880 * t);
                    }
                    recorder.Write(chunk);
                }
            }
        }

        private static string TempPath() =>
            Path.Combine(Path.GetTempPath(), $"OggOpusRecorderTests_{Guid.NewGuid():N}.ogg");

        [Fact]
        public void Write_ProducesOggOpusContainer()
        {
            var path = TempPath();
            try
            {
                WriteToneFile(path);

                var bytes = File.ReadAllBytes(path);
                Assert.NotEmpty(bytes);

                // Ogg capture pattern, then the RFC 7845 identification and comment headers,
                // which is what makes this an Ogg *Opus* file rather than Ogg anything-else.
                Assert.Equal("OggS", Encoding.ASCII.GetString(bytes, 0, 4));
                var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
                Assert.Contains("OpusHead", head);
                Assert.Contains("OpusTags", head);

                // OpusHead: channel count is one byte at offset 9 of the payload.
                int headOffset = head.IndexOf("OpusHead", StringComparison.Ordinal);
                Assert.Equal(Channels, bytes[headOffset + 9]);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Write_RoundTripsWithoutLosingAudio()
        {
            var path = TempPath();
            try
            {
                WriteToneFile(path);

                long decodedPerChannel = 0;
                short peak = 0;
                using (var file = File.OpenRead(path))
                {
#pragma warning disable CS0618 // Do not use the factory - it does not work with Linux
                    using var decoder = new OpusDecoder(SampleRate, Channels);
#pragma warning restore CS0618
                    var reader = new OpusOggReadStream(decoder, file);
                    while (reader.HasNextPacket)
                    {
                        var packet = reader.DecodeNextPacket();
                        if (packet == null) continue;
                        decodedPerChannel += packet.Length / Channels;
                        foreach (var s in packet) peak = Math.Max(peak, Math.Abs(s));
                    }
                }

                // Allow for Opus pre-skip and the <=20ms of tail we knowingly drop at stop
                // (see the DrainExactly note in OggOpusRecorder). A frame-plumbing bug that
                // dropped or duplicated audio would blow well past this.
                Assert.InRange(decodedPerChannel, SamplesPerChannel - 960, SamplesPerChannel + 960);

                // The tone actually survived encoding — not two seconds of silence.
                Assert.True(peak > short.MaxValue / 8, $"decoded peak was only {peak}");
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
