using System;

namespace SovereignQuantum
{
    public static class QuantumPolicy
    {
        public static bool IsSovereignBody(bool playerShip, string bodyName)
        {
            return playerShip && bodyName != null &&
                bodyName.StartsWith("Body - Sovereign (", StringComparison.Ordinal);
        }

        public static float Multiplier(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 1.5f;
            return Math.Max(1f, Math.Min(3f, value));
        }
    }
}
