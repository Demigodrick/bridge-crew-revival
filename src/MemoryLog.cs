using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// Where the game's memory goes, to tell a leak from churn. The performance log's "memory" is the managed heap
    /// in use (GC.GetTotalMemory), which climbs between garbage collections whether or not anything leaks. So, every
    /// 30 s:
    ///   - allocated: what the heap grew by, frame to frame, between collections (the allocation rate);
    ///   - floor: the heap right after each collection, and its lowest and latest since the last report. A floor
    ///     that keeps rising is a leak; a high rate with a flat floor is only churn;
    ///   - the mod's own share: the heap's growth across each of the mod's per-frame updates (Plugin.Safely),
    ///     the biggest named. Whatever's left is the game's (or the mod's Harmony patches, which run inside it).
    /// And every 2 minutes, a count of the Unity objects by type, the types that grew the most since the last count
    /// (a leak of objects: copies of meshes, materials, textures, game objects never destroyed).
    /// </summary>
    internal static class MemoryLog
    {
        private const float ReportSeconds = 30f, CensusSeconds = 120f;

        private static long lastHeap = -1, allocated, lowestFloor = long.MaxValue, latestFloor = -1;
        private static int collections;
        private static float nextReport, nextCensus;
        private static readonly Dictionary<string, long> modAllocated = new Dictionary<string, long>();
        private static Dictionary<string, int> lastCensus;

        /// <summary>Each frame, before the mod's updates.</summary>
        internal static void Frame()
        {
            long heap = GC.GetTotalMemory(false);
            if (lastHeap >= 0)
            {
                if (heap >= lastHeap) allocated += heap - lastHeap;
                else
                {
                    // A collection (the heap shrank): what's left is the floor.
                    collections++;
                    latestFloor = heap;
                    if (heap < lowestFloor) lowestFloor = heap;
                }
            }
            lastHeap = heap;

            float now = Time.realtimeSinceStartup;
            SettleCheck(now);
            if (nextReport == 0f) { nextReport = now + ReportSeconds; nextCensus = now + CensusSeconds; }
            if (now >= nextReport)
            {
                nextReport = now + ReportSeconds;
                Report(heap);
            }
            if (now >= nextCensus)
            {
                nextCensus = now + CensusSeconds;
                Census();
            }
        }

        // Ten seconds into the briefing room or the main menu (where a short hitch doesn't matter), a full
        // collection: the true floor there, logged, so visits can be compared (a floor that's higher after every
        // mission is memory the last one kept). It also stops the heap growing between collections there: the
        // game's garbage collector lets it grow rather than collect, and the game crashed in Mono at the main menu
        // with the heap past 500 MB and climbing (most likely the collector running out of heap sections).
        private static Type lastPhase;
        private static float settleAt = -1f;

        private static void SettleCheck(float now)
        {
            var manager = GamePhaseManager.IEFBKOMBFGO;
            var phase = manager == null ? null : manager.GetCurrentGamePhase();
            if (phase != lastPhase)
            {
                lastPhase = phase;
                settleAt = phase == typeof(GamePhase_BriefingRoom) || phase == typeof(GamePhase_MainMenu) ? now + 10f : -1f;
            }
            if (settleAt < 0f || now < settleAt) return;
            settleAt = -1f;
            long before = GC.GetTotalMemory(false);
            float started = Time.realtimeSinceStartup;
            long after = GC.GetTotalMemory(true);
            lastHeap = after;
            Plugin.Log.LogInfo($"Memory: settled in {phase.Name}: full collection, heap {Mb(before)} to {Mb(after)} (the floor here) in {(Time.realtimeSinceStartup - started) * 1000f:0} ms");
        }

        /// <summary>Around one of the mod's per-frame updates: the heap before it.</summary>
        internal static long Before() => GC.GetTotalMemory(false);

        /// <summary>After it: its growth, unless a collection ran during it.</summary>
        internal static void After(string what, long before)
        {
            long grew = GC.GetTotalMemory(false) - before;
            if (grew <= 0) return;
            modAllocated.TryGetValue(what, out var sum);
            modAllocated[what] = sum + grew;
        }

        private static void Report(long heap)
        {
            long mod = modAllocated.Values.Sum();
            string top = string.Join(", ", modAllocated.OrderByDescending(kv => kv.Value).Take(5)
                .Where(kv => kv.Value >= 64 * 1024).Select(kv => $"{kv.Key} {Mb(kv.Value)}").ToArray());
            string floor = collections == 0
                ? "no collection in it"
                : $"{collections} collections, floor after them {Mb(latestFloor)} (lowest {Mb(lowestFloor)})";
            string phase;
            try { phase = GameState.Phase() ?? "no phase"; }
            catch { phase = "unknown phase"; }
            Plugin.Log.LogInfo($"Memory: last {ReportSeconds:0} s in {phase}, heap {Mb(heap)}; allocated {Mb(allocated)} ({Mb((long)(allocated / ReportSeconds))}/s); "
                               + $"{floor}; the mod's per-frame updates {Mb(mod)}{(top.Length > 0 ? ": " + top : "")}");
            allocated = 0;
            collections = 0;
            lowestFloor = long.MaxValue;
            modAllocated.Clear();
        }

        // The Unity objects by type, and the types that grew since the last count.
        private static void Census()
        {
            var counts = new Dictionary<string, int>();
            foreach (var o in Resources.FindObjectsOfTypeAll(typeof(UnityEngine.Object)))
            {
                if (o == null) continue;
                string type = o.GetType().Name;
                counts.TryGetValue(type, out var n);
                counts[type] = n + 1;
            }
            int total = counts.Values.Sum();
            if (lastCensus == null)
            {
                Plugin.Log.LogInfo($"Memory: {total} Unity objects; most: "
                                   + string.Join(", ", counts.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key} {kv.Value}").ToArray()));
            }
            else
            {
                int lastTotal = lastCensus.Values.Sum();
                var grew = counts.Select(kv => new { kv.Key, Change = kv.Value - (lastCensus.TryGetValue(kv.Key, out var was) ? was : 0), Now = kv.Value })
                    .Where(x => x.Change > 0).OrderByDescending(x => x.Change).Take(8).ToArray();
                Plugin.Log.LogInfo($"Memory: {total} Unity objects ({(total >= lastTotal ? "+" : "")}{total - lastTotal} in {CensusSeconds:0} s); "
                                   + (grew.Length == 0 ? "no type grew" : "grew most: " + string.Join(", ", grew.Select(x => $"{x.Key} +{x.Change} (now {x.Now})").ToArray())));
            }
            lastCensus = counts;
        }

        private static string Mb(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024f * 1024f):0.0} MB" : $"{bytes / 1024} KB";
    }
}
