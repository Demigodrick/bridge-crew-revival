using System;

namespace StbcRevival.Science
{
    public enum MiniGameKind { Signal, Sweep, Tuning }

    // A walk-up mini game. Controls are 0..1; the target drifts slowly with time.
    internal abstract class MiniGame
    {
        public const int Controls = 3;
        protected readonly float[] c = new float[Controls];
        protected float time;

        public abstract MiniGameKind Kind { get; }
        public int ControlCount { get { return Controls; } }
        public float GetControl(int i) { return c[i]; }
        public virtual void SetControl(int i, float v) { c[i] = Clamp01(v); }
        // A steady game's target holds still: a scan drifts only one of its two games, so the player has one to chase.
        public bool Steady { get; set; }
        public void Tick(float t) { time = Steady ? 0f : t; }
        public abstract float Match { get; }
        // Where control i has to be right now (tests and hints).
        internal abstract float Target(int i);

        public static MiniGame Create(MiniGameKind k, int seed)
        {
            Rng r = new Rng(seed);
            switch (k)
            {
                case MiniGameKind.Signal: return new SignalGame(r);
                case MiniGameKind.Sweep: return new SweepGame(r);
                default: return new TuningGame(r);
            }
        }

        internal static float Clamp(float x, float a, float b) { return x < a ? a : (x > b ? b : x); }
        internal static float Clamp01(float x) { return Clamp(x, 0f, 1f); }
        internal static float Wrap01(float x) { x -= (float)Math.Floor(x); return x >= 1f ? 0f : x; }

        // Small deterministic generator so every machine draws the same targets.
        internal sealed class Rng
        {
            uint s;
            public Rng(int seed) { s = (uint)seed * 2654435761u + 0x9E3779B9u; if (s == 0) s = 1; Next(); Next(); }
            uint Next() { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return s; }
            public float Float() { return (Next() >> 8) / 16777216f; }
            // Away from the middle, so the opposite setting is clearly wrong.
            public float OffCentre() { float f = Float() * 0.2f + 0.1f; return Next() % 2 == 0 ? f : 1f - f; }
        }
    }

    // Match a wave: frequency, amplitude, phase.
    internal sealed class SignalGame : MiniGame
    {
        readonly float f0, a0, p0;
        internal SignalGame(Rng r) { f0 = r.OffCentre(); a0 = r.OffCentre(); p0 = r.OffCentre(); }
        public override MiniGameKind Kind { get { return MiniGameKind.Signal; } }

        float TargetFreq { get { return 1f + 7f * f0 + (float)Math.Sin(time * 0.13f) * 0.25f; } }
        float TargetAmp { get { return 0.1f + 0.9f * a0; } }
        float TargetPhase { get { return 6.28f * p0 + time * 0.05f; } }

        internal override float Target(int i)
        {
            if (i == 0) return Clamp01((TargetFreq - 1f) / 7f);
            if (i == 1) return a0;
            return Wrap01(TargetPhase / 6.28f);
        }

        public override float Match
        {
            get
            {
                float f = 1f + 7f * c[0], a = 0.1f + 0.9f * c[1], p = 6.28f * c[2];
                float dp = Math.Abs((((p - TargetPhase) % 6.283f) + 6.283f) % 6.283f);
                dp = Math.Min(dp, 6.283f - dp);
                return Clamp(1f - Math.Abs(f - TargetFreq) / 1.5f, 0f, 1f)
                     * Clamp(1f - Math.Abs(a - TargetAmp) / 0.5f, 0f, 1f)
                     * Clamp(1f - dp / 1.6f, 0f, 1f);
            }
        }
    }

    // Turn three rings until their gaps line up. Turning a ring drags its neighbour a little.
    internal sealed class SweepGame : MiniGame
    {
        readonly float[] t0 = new float[3];
        internal SweepGame(Rng r) { for (int i = 0; i < 3; i++) t0[i] = r.Float(); }
        public override MiniGameKind Kind { get { return MiniGameKind.Sweep; } }

        float Drift(int i) { return (float)Math.Sin(time * 0.1f) * 12f * (i + 1) / 2f; }

        // Ring angle is control*360 plus its offset; aligned when that is 0 (mod 360).
        internal override float Target(int i) { return Wrap01(-t0[i] - Drift(i) / 360f); }

        public override void SetControl(int i, float v)
        {
            v = Wrap01(v);
            float d = v - c[i];
            if (d > 0.5f) d -= 1f; else if (d < -0.5f) d += 1f;
            c[i] = v;
            int n = i == 1 ? 0 : 1;
            c[n] = Wrap01(c[n] + d * ScienceTuning.RingNudge);
        }

        internal float RingOff(int i)
        {
            float off = Wrap01(c[i] + t0[i] + Drift(i) / 360f) * 360f;
            return Math.Min(off, 360f - off);
        }

        public int Aligned { get { int n = 0; for (int i = 0; i < 3; i++) if (RingOff(i) < 10f) n++; return n; } }

        public override float Match
        {
            get
            {
                float q = 1f;
                for (int i = 0; i < 3; i++) q *= Clamp(1f - RingOff(i) / 60f, 0f, 1f);
                return (float)Math.Pow(q, 0.6);
            }
        }
    }

    // Three bands to centre; the target wanders a little.
    internal sealed class TuningGame : MiniGame
    {
        readonly float[] t0 = new float[3];
        internal TuningGame(Rng r) { for (int i = 0; i < 3; i++) t0[i] = r.OffCentre(); }
        public override MiniGameKind Kind { get { return MiniGameKind.Tuning; } }

        float Drift(int i) { return (float)Math.Sin(time * (0.09f + 0.02f * i) + i) * 0.12f; }
        internal override float Target(int i) { return Clamp01(t0[i] + Drift(i)); }

        public override float Match
        {
            get
            {
                float q = 1f;
                for (int i = 0; i < 3; i++) { float d = (c[i] - Target(i)) / 0.22f; q *= (float)Math.Exp(-d * d); }
                return q;
            }
        }
    }

    internal static class MiniGamePick
    {
        static readonly MiniGameKind[][] Pairs =
        {
            new[] { MiniGameKind.Signal, MiniGameKind.Sweep },
            new[] { MiniGameKind.Signal, MiniGameKind.Tuning },
            new[] { MiniGameKind.Sweep, MiniGameKind.Tuning },
        };

        public static MiniGameKind[] For(int targetSeed)
        {
            uint h = (uint)targetSeed * 2654435761u;
            return (MiniGameKind[])Pairs[(h >> 16) % 3].Clone();
        }

        // Same on every machine for a given target id.
        public static int SeedOf(string id)
        {
            unchecked
            {
                uint h = 2166136261;
                for (int i = 0; i < id.Length; i++) { h ^= id[i]; h *= 16777619; }
                return (int)h;
            }
        }
    }
}
