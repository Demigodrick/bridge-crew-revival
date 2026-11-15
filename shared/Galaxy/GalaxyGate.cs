using System;
using System.Collections.Generic;
using System.Text;

namespace StbcRevival.Galaxy
{
    /// <summary>Whether a galaxy region is on for a session, and if it is off, what to tell the host.</summary>
    public sealed class GateResult
    {
        public bool On;
        /// <summary>Null when there is nothing to tell the host; otherwise why the region is off.</summary>
        public string Reason;
    }

    /// <summary>The crew gate: a region is only on when the mission wants it and every player has the same file.</summary>
    public static class GalaxyGate
    {
        /// <param name="players">Each player other than the host, as name and file hash (null when they have no file).</param>
        public static GateResult Decide(bool missionWantsRegion, string regionName, string hostHash, IList<KeyValuePair<string, string>> players)
        {
            // Nothing to decide, or nothing the host can use: say nothing.
            if (!missionWantsRegion || string.IsNullOrEmpty(hostHash)) return new GateResult { On = false, Reason = null };

            var missing = new List<string>();
            foreach (var p in players)
            {
                if (p.Value != hostHash) missing.Add(p.Key);
            }
            if (missing.Count == 0) return new GateResult { On = true, Reason = null };

            return new GateResult { On = false, Reason = regionName + " is off: " + NameList(missing) + (missing.Count == 1 ? " doesn't have it" : " don't have it") };
        }

        /// <summary>System ids at or above the Trench count are the region's systems, not Trench's.</summary>
        public static bool IsRegionSystem(int systemId, int trenchCount) => systemId >= trenchCount;

        // "Sam", "Sam and Alex", "Sam, Alex and Kim".
        static string NameList(List<string> names)
        {
            if (names.Count == 1) return names[0];
            var sb = new StringBuilder();
            for (int i = 0; i < names.Count - 1; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(names[i]);
            }
            sb.Append(" and ").Append(names[names.Count - 1]);
            return sb.ToString();
        }
    }
}
