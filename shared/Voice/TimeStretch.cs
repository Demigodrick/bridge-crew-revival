#nullable disable
// Revival voice time stretching, as WebRTC's NetEq (Discord's jitter buffer) does it: speech is sped up by cutting
// one pitch period out of it, or slowed down by repeating one, cross-fading across the cut so the waveform stays
// continuous. Voiced speech repeats itself every pitch period, so one period less or more isn't heard; quiet audio
// (pauses inside a sentence) can be cut or repeated anywhere. Audio that isn't periodic enough is left alone.
// Compiled by the mod (net35) and the voice tests (net8): plain arrays only.
using System;

namespace StbcRevival.Voice
{
    public static class TimeStretch
    {
        public const float MinCorrelation = 0.85f;   // how alike two periods must be to cut or repeat one
        public const float QuietRms = 0.003f;        // about -50 dBFS: anything goes
        const float MinPitchMs = 2.5f, MaxPitchMs = 15f, MatchMs = 5f;

        // The best period (samples) to cut or repeat in x[0 .. length): the lag at which x[0 .. match) best matches
        // x[lag .. lag + match), searched from 2.5 ms up to maxPeriod; 0 if no lag is alike enough (and it isn't quiet).
        public static int FindPeriod(float[] x, int length, int rate, int maxPeriod)
        {
            int minP = (int)(rate * MinPitchMs / 1000f);
            int maxP = Math.Min(maxPeriod, (int)(rate * MaxPitchMs / 1000f));
            int match = (int)(rate * MatchMs / 1000f);
            if (maxP < minP || maxP + match > length) return 0;

            double energy = 0;
            for (int i = 0; i < length; i++) energy += x[i] * x[i];
            if (Math.Sqrt(energy / length) < QuietRms) return maxP; // quiet: the most that fits

            double e0 = 0;
            for (int i = 0; i < match; i++) e0 += x[i] * x[i];
            int best = 0;
            double bestCorr = MinCorrelation;
            for (int lag = minP; lag <= maxP; lag++)
            {
                double cross = 0, e1 = 0;
                for (int i = 0; i < match; i++)
                {
                    float b = x[lag + i];
                    cross += x[i] * b;
                    e1 += b * b;
                }
                if (cross <= 0 || e0 <= 0 || e1 <= 0) continue;
                double corr = cross / Math.Sqrt(e0 * e1);
                if (corr > bestCorr) { bestCorr = corr; best = lag; }
            }
            return best;
        }

        // Speeds x[0 .. length) up by one period: periods A = x[0 .. p) and B = x[p .. 2p) become one that starts
        // as A and ends as B. Writes length - p samples to output; returns how many were cut (0: nothing done, the
        // input copied as it is).
        public static int Compress(float[] x, int length, int rate, float[] output)
        {
            int p = FindPeriod(x, length, rate, length / 2);
            if (p <= 0 || 2 * p > length)
            {
                Array.Copy(x, output, length);
                return 0;
            }
            for (int i = 0; i < p; i++)
            {
                float w = (i + 0.5f) / p;
                output[i] = x[i] * (1f - w) + x[p + i] * w;
            }
            Array.Copy(x, 2 * p, output, p, length - 2 * p);
            return p;
        }

        // Slows x[0 .. length) down by one period: after A = x[0 .. p) comes a period that starts as B = x[p .. 2p)
        // (what follows A) and ends as A (what precedes B), then B and the rest. Writes length + p samples to output
        // (which must have room); returns how many were added (0: nothing done, the input copied as it is).
        public static int Expand(float[] x, int length, int rate, float[] output)
        {
            int p = FindPeriod(x, length, rate, length / 2);
            if (p <= 0 || 2 * p > length)
            {
                Array.Copy(x, output, length);
                return 0;
            }
            Array.Copy(x, 0, output, 0, p);
            for (int i = 0; i < p; i++)
            {
                float w = (i + 0.5f) / p;
                output[p + i] = x[p + i] * (1f - w) + x[i] * w;
            }
            Array.Copy(x, p, output, 2 * p, length - p);
            return p;
        }
    }
}
