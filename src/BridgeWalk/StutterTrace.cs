using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace StbcRevival.Walking
{
    /// <summary>
    /// While you stand and walk (Bridge Walk), a trace of what makes the view stutter, in the log ("Stutter trace:"):
    ///   - each frame over 20 ms: how long it took, whether memory was cleaned up in it (garbage collection), the
    ///     mod's slowest updates in it and the mod's total, and how far your floor height jumped that frame;
    ///   - every 10 s standing: frames over 20 ms and 33 ms, the longest, the mod's slowest updates on average, and how
    ///     often and how far the floor height changed under you.
    /// The mod's updates are timed in Plugin.Safely and the walk's animation pass; nothing is timed or written while
    /// seated. At most 300 slow-frame lines a session, so a slow PC doesn't fill the log.
    /// </summary>
    internal static class StutterTrace
    {
        private const float Slow = 0.020f, VerySlow = 0.033f, SummarySeconds = 10f;
        private const int MaxLines = 300;

        private static readonly Dictionary<string, long> frame = new Dictionary<string, long>();   // this frame's mod updates (ticks)
        private static readonly Dictionary<string, long> window = new Dictionary<string, long>();  // the summary window's
        private static bool on;
        private static int gcLast = -1, lines, frames, slow, verySlow, floorMoves, collections;
        private static float longest, summaryAt, bigStep, stepSum;
        private static long modTicks;

        /// <summary>One of the mod's updates took this long (Stopwatch ticks); only counted while standing.</summary>
        internal static void Add(string what, long ticks)
        {
            if (!on) return;
            frame.TryGetValue(what, out var t);
            frame[what] = t + ticks;
        }

        /// <summary>First thing each frame: the frame that just ended, looked at.</summary>
        internal static void FrameStart()
        {
            bool standing = Walker.Standing;
            if (!standing)
            {
                if (on) { Summary(); on = false; }
                frame.Clear();
                return;
            }
            int gc = GC.CollectionCount(0);
            if (!on)
            {
                // Just stood up: start counting from the next frame.
                on = true; gcLast = gc; summaryAt = Time.unscaledTime + SummarySeconds;
                frame.Clear(); window.Clear();
                frames = slow = verySlow = floorMoves = collections = 0; longest = bigStep = stepSum = 0f; modTicks = 0;
                return;
            }
            float dt = Time.unscaledDeltaTime;
            float step = Walker.LastStep;
            bool cleaned = gc != gcLast;
            gcLast = gc;
            long sum = 0;
            foreach (var kv in frame)
            {
                sum += kv.Value;
                window.TryGetValue(kv.Key, out var w);
                window[kv.Key] = w + kv.Value;
            }
            frames++; modTicks += sum;
            if (cleaned) collections++;
            if (dt > longest) longest = dt;
            if (Mathf.Abs(step) > 0.002f) { floorMoves++; stepSum += Mathf.Abs(step); if (Mathf.Abs(step) > Mathf.Abs(bigStep)) bigStep = step; }
            if (dt > Slow)
            {
                slow++;
                if (dt > VerySlow) verySlow++;
                if (lines < MaxLines)
                {
                    lines++;
                    Plugin.Log.LogInfo($"Stutter trace: frame {dt * 1000f:F0} ms{(cleaned ? ", memory cleaned up in it" : "")}; the mod {Ms(sum):F1} ms ({Top(frame, 3, 1)}); "
                        + $"floor height {step * 100f:+0.0;-0.0;0} cm; walking {Walker.Speed:F1} m/s"
                        + (lines == MaxLines ? " (no more slow-frame lines this session)" : ""));
                }
            }
            frame.Clear();
            if (Time.unscaledTime >= summaryAt) Summary();
        }

        private static void Summary()
        {
            summaryAt = Time.unscaledTime + SummarySeconds;
            if (frames == 0) return;
            Plugin.Log.LogInfo($"Stutter trace: last {frames} frames standing: {slow} over 20 ms, {verySlow} over 33 ms, longest {longest * 1000f:F0} ms, {collections} memory clean-up(s); "
                + $"the mod {Ms(modTicks) / frames:F2} ms a frame on average ({Top(window, 4, frames)}); "
                + $"floor height changed on {floorMoves} frames ({(floorMoves > 0 ? $"{stepSum * 100f / floorMoves:F1} cm on average, biggest {bigStep * 100f:+0.0;-0.0} cm" : "never")})");
            window.Clear();
            frames = slow = verySlow = floorMoves = collections = 0; longest = bigStep = stepSum = 0f; modTicks = 0;
        }

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        // The slowest few, per frame (divided by frames), as "name 1.2 ms, ...".
        private static string Top(Dictionary<string, long> d, int count, int per)
        {
            var list = new List<KeyValuePair<string, long>>(d);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb = new StringBuilder();
            for (int i = 0; i < list.Count && i < count; i++)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(list[i].Key).Append(' ').Append((Ms(list[i].Value) / per).ToString("F2")).Append(" ms");
            }
            return sb.Length > 0 ? sb.ToString() : "nothing";
        }
    }
}
