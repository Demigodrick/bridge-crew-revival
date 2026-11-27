namespace StbcRevival.Science
{
    // Numbers the science console is balanced with.
    internal static class ScienceTuning
    {
        // A scan only progresses while both mini games match at least this well.
        public const float LockThreshold = 0.70f;
        // Scan progress per second at the lock threshold, and the extra gained going from there to a perfect match.
        public const float BaseRate = 2.0f;
        public const float BonusRate = 3.6f;
        // Turning a sweep ring turns its neighbour by this share of the change.
        public const float RingNudge = 1f / 3f;
        // What one finished scan adds to its area (or to weapons against a faction), and the most an area can reach.
        public const float CrewStep = 0.05f;
        public const float CrewCap = 0.50f;
        // What each completed assignment adds to the enemy, to one area in turn (damage, shields, speed).
        public const float EnemyStep = 0.03f;
        // The share of destroyed enemy ships that leave debris to scan (the host rolls once per kill).
        public const float DebrisChance = 1f / 3f;
        // How far the console's targets can be scanned from, metres.
        public const float RangeMetres = 20000f;
    }
}
