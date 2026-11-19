namespace StbcRevival.Science
{
    // How far a scan has got. Only moves forward, and only while both mini games hold a lock.
    internal sealed class ScanProgress
    {
        public float Value { get; private set; }
        public bool Locked { get; private set; }
        public bool Done { get { return Value >= 100f; } }

        public ScanProgress() { }

        // Carrying on from where the board has a target (0..100).
        public ScanProgress(float start)
        {
            if (float.IsNaN(start) || start < 0f) start = 0f;
            Value = start > 100f ? 100f : start;
        }

        public void Advance(float matchA, float matchB, float dt)
        {
            float worst = matchA < matchB ? matchA : matchB;
            Locked = worst >= ScienceTuning.LockThreshold;
            if (!Locked || Done || dt <= 0f) return;
            float rate = ScienceTuning.BaseRate
                + (worst - ScienceTuning.LockThreshold) / (1f - ScienceTuning.LockThreshold) * ScienceTuning.BonusRate;
            Value += rate * dt;
            if (Value > 100f) Value = 100f;
        }
    }
}
