namespace OpenFreqAudio.Tests
{
    /// <summary>
    /// Statistical tests for <see cref="BackgroundNoiseGenerator"/>. Unusually for DSP
    /// these all have exact analytic targets — Rayleigh for a thermal envelope,
    /// Campbell's theorem for the impulsive kurtosis — so they check the model rather
    /// than just pinning current behaviour. The derivations they check are written
    /// out in the doc comments on <see cref="BackgroundNoiseGenerator"/>.
    /// </summary>
    public class BackgroundNoiseTests
    {
        private const int Fs = 48000;
        private const int VhfKhz = 127000;
        private const int UhfKhz = 251000;
        // Top of the modelled range, where external noise has fallen away and the
        // receiver's own thermal floor is essentially all that is left.
        private const int ThermalKhz = 900000;
        private const int N = Fs * 4;

        private static (double[] i, double[] q) Draw(int freqKhz, int seed)
        {
            var gen = new BackgroundNoiseGenerator(Fs, freqKhz, seed);
            var i = new double[N];
            var q = new double[N];
            // The IF filter starts from rest; let it settle before we measure.
            for (int n = 0; n < Fs / 10; ++n) gen.Next(out _, out _);
            for (int n = 0; n < N; ++n)
            {
                gen.Next(out float si, out float sq);
                i[n] = si;
                q[n] = sq;
            }
            return (i, q);
        }

        private static double Mean(double[] x)
        {
            double s = 0;
            foreach (var v in x) s += v;
            return s / x.Length;
        }

        private static double Variance(double[] x)
        {
            double m = Mean(x), s = 0;
            foreach (var v in x) s += (v - m) * (v - m);
            return s / x.Length;
        }

        private static double Correlation(double[] a, double[] b)
        {
            double ma = Mean(a), mb = Mean(b), sab = 0, sa = 0, sb = 0;
            for (int n = 0; n < a.Length; ++n)
            {
                double da = a[n] - ma, db = b[n] - mb;
                sab += da * db;
                sa += da * da;
                sb += db * db;
            }
            return sab / Math.Sqrt(sa * sb);
        }

        /// <summary>Amplitude of the spectral line at <paramref name="freqHz"/>, by direct DFT bin.</summary>
        private static double LineAmplitude(double[] x, double freqHz)
        {
            double w = 2.0 * Math.PI * freqHz / Fs, re = 0, im = 0;
            for (int n = 0; n < x.Length; ++n)
            {
                re += x[n] * Math.Cos(w * n);
                im += x[n] * Math.Sin(w * n);
            }
            return 2.0 * Math.Sqrt(re * re + im * im) / x.Length;
        }

        [Fact]
        public void NoiseIsUnitPower()
        {
            foreach (var khz in new[] { VhfKhz, UhfKhz })
            {
                var (i, q) = Draw(khz, seed: 1);
                double power = Variance(i) + Variance(q);
                Assert.InRange(power, 0.92, 1.08);
            }
        }

        /// <summary>
        /// Run the real squelch detector over noise alone: where it settles, and how often
        /// the gate opens with nobody transmitting. The AGC runs alongside it because in the
        /// receive chain it does, and it is the thing whose peaks used to drive the gate.
        /// </summary>
        private static (double SquelchFloor, double Duty, double BreaksPerSecond)
            RunDetectors(int freqKhz, int seed, int seconds, float gate = 2.0f)
        {
            var gen = new BackgroundNoiseGenerator(Fs, freqKhz, seed);
            var agc = AttackDecayFilter.MakeAttackDecayFilter(
                RadioPlayback.AgcAttack, RadioPlayback.AgcDecay, Fs);
            var squelch = FirstOrderFilter.MakeFirstOrderFilter(RadioPlayback.SquelchTau, Fs, 1.0);
            float gatePower = gate * gate;
            long total = 0, open = 0;
            int breaks = 0;
            bool wasOpen = false;
            double squelchSum = 0;
            for (int n = 0; n < Fs * seconds; ++n)
            {
                gen.Next(out float i, out float q);
                float env = MathF.Sqrt(i * i + q * q);
                agc.Apply(env);
                squelch.Apply(env * env);
                bool isOpen = squelch.D1 >= gatePower;
                if (n <= Fs) { wasOpen = isOpen; continue; } // let the detectors settle
                if (isOpen) open++;
                if (isOpen && !wasOpen) breaks++;
                wasOpen = isOpen;
                squelchSum += squelch.D1;
                total++;
            }
            return (squelchSum / total, (double)open / total, breaks / (double)(seconds - 1));
        }

        [Fact]
        public void SquelchGateSitsJustOverTheNoiseFloor()
        {
            // The gate is a level of 2.0 (RadioPlayback: SquelchLevel * 2) on a detector that
            // tracks mean power, and the comment there says it is modelling a +6 dB squelch.
            // Unit-power noise settles that detector on 1.0, so the margin is 6.02 dB by
            // construction and this is really a check that the generator is unit-power where
            // the gate can see it. The old Voss-McCartney generator put it ~14 dB out.
            foreach (var khz in new[] { VhfKhz, UhfKhz })
            {
                var run = RunDetectors(khz, seed: 2, seconds: 6);
                double marginDb = 10.0 * Math.Log10(4.0 / run.SquelchFloor);
                Assert.InRange(marginDb, 5.5, 6.5);
            }
        }

        [Fact]
        public void NoiseAloneDoesNotBreakSquelch()
        {
            // The regression this detector exists for. VHF noise here is impulsive — envelope
            // excess kurtosis ~11 at 127 MHz, against 0.245 for the Rayleigh envelope of pure
            // thermal noise — and its envelope reaches 12x its own RMS, so gating on the AGC
            // (1 ms attack, a peak tracker) let single impulses open the gate about once a
            // second with nobody transmitting. Each break was a ~0.3 ms burst that peaked
            // above fully-quieting speech, because an impulse rings for only ~125 us and the
            // AGC cannot divide out a peak it has not finished tracking.
            //
            // SquelchTau averages over 10 ms instead, where such an impulse is worth about 1%
            // of the window. Measured over 295 s at 127 MHz: 1.01 breaks/s off the AGC,
            // 0.25/s at tau = 3 ms, none at 10 ms.
            foreach (var khz in new[] { VhfKhz, UhfKhz, ThermalKhz })
            {
                var run = RunDetectors(khz, seed: 2, seconds: 30);
                Assert.Equal(0.0, run.BreaksPerSecond);
                Assert.Equal(0.0, run.Duty);
            }
        }

        [Fact]
        public void QuadraturesAreIndependent()
        {
            // The regression this class was rewritten for: the old generator drew I and Q
            // from consecutive samples of one pink process and landed at rho = 0.43.
            foreach (var khz in new[] { VhfKhz, UhfKhz })
            {
                var (i, q) = Draw(khz, seed: 3);
                Assert.InRange(Correlation(i, q), -0.02, 0.02);
            }
        }

        [Fact]
        public void EnvelopeApproachesRayleighWhereThermalDominates()
        {
            var (i, q) = Draw(ThermalKhz, seed: 4);
            var env = new double[N];
            for (int n = 0; n < N; ++n) env[n] = Math.Sqrt(i[n] * i[n] + q[n] * q[n]);

            // Rayleigh: mean/sigma = sqrt(pi/2) / sqrt(2 - pi/2) = 1.913.
            double ratio = Mean(env) / Math.Sqrt(Variance(env));
            Assert.InRange(ratio, 1.87, 1.96);
        }

        [Fact]
        public void DetectedNoisePowerIsInvariantToCarrierOffset()
        {
            var (i, q) = Draw(UhfKhz, seed: 8);
            double reference = 0;
            foreach (double offset in new[] { 0.0, 10.0, 50.0, 250.0, 1000.0 })
            {
                double power = Variance(Envelope(i, q, offset, carrier: 10.0));
                if (offset == 0.0) reference = power;
                else Assert.InRange(power / reference, 0.9, 1.1);
            }
        }

        [Fact]
        public void NoAmSidebandAtTwiceTheCarrierOffset()
        {
            const double offset = 250.0;
            const double control = 1277.0; // an arbitrary band with nothing in it

            var (i, q) = Draw(UhfKhz, seed: 9);
            double[] clean = Envelope(i, q, offset, carrier: 10.0);
            double cleanLine = LineAmplitude(Square(clean), 2 * offset);
            double cleanFloor = LineAmplitude(Square(clean), control);

            // Same noise, but with the quadratures deliberately shared the way the old
            // generator effectively shared them. Proves the measurement can see the fault.
            double[] broken = Envelope(i, i, offset, carrier: 10.0);
            double brokenLine = LineAmplitude(Square(broken), 2 * offset);
            double brokenFloor = LineAmplitude(Square(broken), control);

            Assert.True(brokenLine > 20 * brokenFloor,
                $"correlated I/Q should show a line at 2*offset: {brokenLine:E3} vs floor {brokenFloor:E3}");
            Assert.True(cleanLine < 4 * cleanFloor,
                $"independent I/Q should not: {cleanLine:E3} vs floor {cleanFloor:E3}");
        }

        private static double[] Envelope(double[] i, double[] q, double offsetHz, double carrier)
        {
            var env = new double[i.Length];
            double w = 2.0 * Math.PI * offsetHz / Fs;
            for (int n = 0; n < i.Length; ++n)
            {
                double ci = carrier * Math.Cos(w * n) + i[n];
                double cq = carrier * Math.Sin(w * n) + q[n];
                env[n] = Math.Sqrt(ci * ci + cq * cq);
            }
            return env;
        }

        private static double[] Square(double[] x)
        {
            double m = Mean(x);
            var p = new double[x.Length];
            for (int n = 0; n < x.Length; ++n) p[n] = (x[n] - m) * (x[n] - m);
            return p;
        }
    }
}
