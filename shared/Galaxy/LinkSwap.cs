using System;
using System.Collections.Generic;

namespace StbcRevival.Galaxy
{
    /// <summary>
    /// Trench systems with additions: for Revival Patrol and Free Roam each such Trench link points at a copy of its
    /// system (the original's content plus the new families); for every other mission it points at the original again.
    /// The Trench asset stays loaded between missions, so this is set before every mission on it, like RegionLinks.
    /// </summary>
    public static class LinkSwap
    {
        /// <summary>On: each link whose system is a key of <paramref name="originalToCopy"/> gets its copy. Off: each link
        /// whose system is one of the copies gets its original back. Other links are left alone; applying twice changes
        /// nothing more. A null map changes nothing.</summary>
        public static void Apply<TLink, TSys>(IList<TLink> links, Func<TLink, TSys> get, Action<TLink, TSys> set,
            IDictionary<TSys, TSys> originalToCopy, bool on)
        {
            if (links == null || originalToCopy == null || originalToCopy.Count == 0) return;
            var from = originalToCopy;
            if (!on)
            {
                from = new Dictionary<TSys, TSys>();
                foreach (var kv in originalToCopy)
                    if (kv.Value != null && !from.ContainsKey(kv.Value)) from[kv.Value] = kv.Key;
            }
            foreach (var link in links)
            {
                if (link == null) continue;
                var now = get(link);
                if (now == null) continue;
                if (from.TryGetValue(now, out var to)) set(link, to);
            }
        }
    }
}
