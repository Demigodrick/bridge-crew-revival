#nullable disable
// Revival voice streaming resampler: polyphase windowed sinc (Kaiser), 32 taps per phase.
namespace StbcRevival.Voice
{
    public sealed class Resampler
    {
        const int Taps = 32, Half = Taps / 2;
        readonly bool copy;
        readonly int l, m;          // output phases, input step in 1/l input samples
        readonly float[][] table;
        float[] work = new float[Taps];   // saved tail of the previous input, then the new input
        long baseIndex;             // absolute index of the first input sample of the current call
        long nextNum;               // next output position in 1/l input samples

        public Resampler(int fromRate, int toRate)
        {
            copy = fromRate == toRate;
            if (copy) return;
            int g = Gcd(fromRate, toRate);
            l = toRate / g;
            m = fromRate / g;
            double cutoff = System.Math.Min(1.0, (double)toRate / fromRate) * 0.95;
            const double beta = 8.0;
            double i0Beta = Bessel0(beta);
            table = new float[l][];
            for (int p = 0; p < l; p++)
            {
                var h = new float[Taps];
                double sum = 0;
                double frac = (double)p / l;
                var tmp = new double[Taps];
                for (int j = 0; j < Taps; j++)
                {
                    double x = (j - (Half - 1)) - frac;
                    double w = 1.0 - (x / Half) * (x / Half);
                    double win = w > 0 ? Bessel0(beta * System.Math.Sqrt(w)) / i0Beta : 0;
                    double px = System.Math.PI * cutoff * x;
                    double s = System.Math.Abs(px) < 1e-12 ? 1.0 : System.Math.Sin(px) / px;
                    tmp[j] = cutoff * s * win;
                    sum += tmp[j];
                }
                for (int j = 0; j < Taps; j++) h[j] = (float)(tmp[j] / sum);
                table[p] = h;
            }
        }

        static int Gcd(int a, int b) { while (b != 0) { int t = a % b; a = b; b = t; } return a; }

        static double Bessel0(double x)
        {
            double sum = 1, term = 1, q = x * x / 4;
            for (int k = 1; k < 60; k++)
            {
                term *= q / ((double)k * k);
                sum += term;
                if (term < 1e-12 * sum) break;
            }
            return sum;
        }

        // Returns the samples written. The caller sizes output as count * to / from + 64.
        public int Process(float[] input, int count, float[] output)
        {
            if (copy)
            {
                System.Array.Copy(input, 0, output, 0, count);
                return count;
            }
            if (work.Length < Taps + count) System.Array.Resize(ref work, Taps + count);
            System.Array.Copy(input, 0, work, Taps, count);
            int written = 0;
            long end = baseIndex + count;
            while (true)
            {
                long idx = nextNum / l;
                if (idx + Half >= end) break;
                float[] h = table[(int)(nextNum % l)];
                // Taps cover absolute samples idx - (Half-1) .. idx + Half.
                int start = (int)(idx - (Half - 1) - baseIndex) + Taps;
                float acc = 0f;
                for (int j = 0; j < Taps; j++) acc += h[j] * work[start + j];
                output[written++] = acc;
                nextNum += m;
            }
            // Keep the last Taps samples of (old tail + input) as the next tail.
            System.Array.Copy(work, count, work, 0, Taps);
            baseIndex = end;
            return written;
        }
    }
}
