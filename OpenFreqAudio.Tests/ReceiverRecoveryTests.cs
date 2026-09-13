using System;

namespace OpenFreqAudio.Tests
{
    /// <summary>
    /// Tests for how a <see cref="RadioPlayback"/> slot notices NaN or infinite receiver state and recovers.
    /// In 1.1.0 a single NaN SNR latched a slot's squelch detector, and that radio stayed silent until reconnect.
    /// </summary>
    public class ReceiverRecoveryTests
    {
        [Fact]
        public void NewSlot_IsFinite()
        {
            var slot = new RadioPlayback.RadioConfig();
            Assert.True(slot.IsFinite(new float[480]));
            Assert.True(slot.IsFinite(ReadOnlySpan<float>.Empty));
        }

        [Theory]
        [InlineData("agc", float.NaN)]
        [InlineData("squelch", float.NaN)]
        [InlineData("squelch", float.PositiveInfinity)]
        [InlineData("highpass", float.NaN)]
        [InlineData("lowpass", float.PositiveInfinity)]
        public void NonFiniteState_IsNoticed(string where, float bad)
        {
            var slot = new RadioPlayback.RadioConfig();
            var output = new float[480];
            switch (where)
            {
                case "agc": slot.Agc.D1 = bad; break;
                case "squelch": slot.SquelchDetector.D1 = bad; break;
                // The band-pass filters' state isn't visible, but it shows in their output.
                case "highpass":
                    slot.HighPass.Process(bad);
                    output[^1] = slot.LowPass.Process(slot.HighPass.Process(0f));
                    break;
                case "lowpass":
                    slot.LowPass.Process(bad);
                    output[^1] = slot.LowPass.Process(slot.HighPass.Process(0f));
                    break;
                default: throw new ArgumentException(where);
            }

            Assert.False(slot.IsFinite(output));
        }

        [Fact]
        public void ResetReceiver_RecoversFromLatchedNaN()
        {
            var slot = new RadioPlayback.RadioConfig();

            // What 1.1.0 did: one NaN envelope sample through the detectors and band-pass...
            slot.Agc.Apply(float.NaN);
            slot.SquelchDetector.Apply(float.NaN);
            slot.LowPass.Process(slot.HighPass.Process(float.NaN));

            // ...and no amount of good signal afterwards brings them back.
            float lastOut = 0f;
            for (int n = 0; n < 48000; n++)
            {
                slot.Agc.Apply(1f);
                slot.SquelchDetector.Apply(1f);
                lastOut = slot.LowPass.Process(slot.HighPass.Process(1f));
            }
            Assert.True(float.IsNaN(slot.Agc.D1));
            Assert.True(float.IsNaN(slot.SquelchDetector.D1));
            Assert.True(float.IsNaN(lastOut));

            slot.ResetReceiver();

            // Back where a new slot starts, and tracking signal again.
            Assert.Equal(1f, slot.Agc.D1);
            Assert.Equal(1f, slot.SquelchDetector.D1);
            var output = new float[480];
            for (int n = 0; n < output.Length; n++)
            {
                float envelope = 1f + 0.5f * MathF.Sin(2f * MathF.PI * 1000f * n / RadioPlayback.SampleRate);
                slot.Agc.Apply(envelope);
                slot.SquelchDetector.Apply(envelope * envelope);
                output[n] = slot.LowPass.Process(slot.HighPass.Process(envelope / slot.Agc.D1));
            }
            Assert.True(slot.IsFinite(output));
            Assert.All(output, s => Assert.True(float.IsFinite(s)));
        }
    }
}
