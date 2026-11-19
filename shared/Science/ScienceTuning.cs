namespace StbcRevival.Science
{
    // Numbers the science console is balanced with.
    internal static class ScienceTuning
    {
        // A scan only progresses while both mini games match at least this well.
        public const float LockThreshold = 0.70f;
        // Scan progress per second at the lock threshold, and the extra gained going from there to a perfect match.
        public const float BaseRate = 1.0f;
        public const float BonusRate = 1.8f;
        // Turning a sweep ring turns its neighbour by this share of the change.
        public const float RingNudge = 1f / 3f;
        // Crew sharing a scan: each extra crew member's step, and its cap.
        public const float CrewStep = 0.05f;
        public const float CrewCap = 0.50f;
        // Enemy ships present add this much each.
        public const float EnemyStep = 0.03f;
        public const float RangeMetres = 20000f;
    }
}
