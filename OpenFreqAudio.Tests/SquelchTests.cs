namespace OpenFreqAudio.Tests
{
    /// <summary>
    /// Tests for the squelch gate in <see cref="RadioPlayback"/>. The gate runs two
    /// detectors on the same mean power. Only the slow one opens it, and only the fast
    /// one closes it. <see cref="BackgroundNoiseTests.NoiseAloneDoesNotBreakSquelch"/>
    /// covers the open side, which is what noise impulses attack. These cover the close
    /// side and the hysteresis between the two thresholds.
    /// </summary>
    public class SquelchTests
    {
        private const int Fs = AudioFormat.SampleRate;
        private const int VhfKhz = 127000;

        /// <summary>
        /// A crude three-tone voice. Its troughs swing the envelope hard, which is what
        /// makes a gate near its threshold chatter.
        /// </summary>
        private static float Voice(int n)
        {
            double t = n / (double)Fs;
            return (float)(0.45 * Math.Sin(2 * Math.PI * 310 * t)
                         + 0.30 * Math.Sin(2 * Math.PI * 780 * t)
                         + 0.15 * Math.Sin(2 * Math.PI * 1900 * t));
        }

        /// <summary>
        /// The envelope a slot sees: one modulated carrier at <paramref name="cnDb"/> over
        /// the noise, and noise alone once <paramref name="carrierSamples"/> have passed.
        /// </summary>
        private static IEnumerable<float> Envelope(double cnDb, int carrierSamples, int total, int seed)
        {
            var noise = new BackgroundNoiseGenerator(Fs, VhfKhz, seed);
            double a = Math.Pow(10, cnDb / 20.0);
            for (int n = 0; n < total; ++n)
            {
                noise.Next(out float ni, out float nq);
                double amp = n < carrierSamples ? a * (1 + Voice(n) * RadioPlayback.ModIndex) : 0.0;
                float i = ni + (float)amp;
                yield return MathF.Sqrt(i * i + nq * nq);
            }
        }

        [Fact]
        public void HysteresisStopsChatterNearTheThreshold()
        {
            // The regression the second threshold exists for. A modulated carrier at 5 dB
            // C/N parks the detector just over the open power, and the voice troughs then
            // drag it back under. With one threshold for both directions that measured 134
            // open/close cycles in five seconds here, and 1472 at 4 dB C/N.
            var slot = new RadioPlayback.RadioConfig();
            var (openPower, closePower) = slot.SquelchPowers();

            int transitions = 0;
            bool wasOpen = false;
            int n = 0;
            foreach (float env in Envelope(5, int.MaxValue, Fs * 6, seed: 7))
            {
                bool isOpen = slot.StepSquelch(env * env, openPower, closePower);
                if (n == Fs) wasOpen = isOpen;         // let the detectors settle
                if (n > Fs && isOpen != wasOpen) transitions++;
                wasOpen = isOpen;
                ++n;
            }

            Assert.Equal(0, transitions);
            Assert.True(slot.SquelchOpen, "a 5 dB C/N carrier should hold the gate open");
        }

        [Theory]
        [InlineData(20)]
        [InlineData(40)]
        [InlineData(60)]
        // What the link budget gives an aircraft a kilometre away.
        [InlineData(78)]
        public void CloseTimeDoesNotGrowWithSignalStrength(double cnDb)
        {
            // One detector and one threshold decay from the received power P down through
            // that threshold, which takes SquelchTau · ln((P - 1) / 3). That measured
            // 35/82/128/171 ms at these four C/N values, so every strong transmitter left
            // a long noise tail. The fast detector holds the same four at 8/18/27/35 ms.
            var slot = new RadioPlayback.RadioConfig();
            var (openPower, closePower) = slot.SquelchPowers();

            int carrierSamples = Fs;
            int closedAt = -1;
            int n = 0;
            foreach (float env in Envelope(cnDb, carrierSamples, Fs * 2, seed: 11))
            {
                bool isOpen = slot.StepSquelch(env * env, openPower, closePower);
                if (n >= carrierSamples && !isOpen && closedAt < 0) closedAt = n;
                ++n;
            }

            Assert.True(closedAt > 0, "the gate never closed after the carrier stopped");
            double closeMs = (closedAt - carrierSamples) * 1000.0 / Fs;
            Assert.InRange(closeMs, 0.0, 45.0);
        }

        [Fact]
        public void SquelchLevelZeroStaysOpenOnNoiseAlone()
        {
            // Squelch off: the pilot hears the static. Both thresholds fall to zero, and
            // mean power is never negative, so the gate opens on the first sample and stays.
            var slot = new RadioPlayback.RadioConfig { SquelchLevel = 0f };
            var (openPower, closePower) = slot.SquelchPowers();

            int closed = 0;
            foreach (float env in Envelope(0, 0, Fs * 2, seed: 3))
            {
                if (!slot.StepSquelch(env * env, openPower, closePower)) closed++;
            }

            Assert.Equal(0, closed);
        }
    }
}
