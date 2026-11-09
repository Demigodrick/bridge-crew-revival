#nullable disable
// Debug network simulator for Revival voice: injects loss, burst loss, reordering and jitter into the receive path.
// Spec string like "loss=5,burst=2,reorder=2,jitter=50" (loss and reorder in %, burst in packets, jitter in ms).
// Compiled by the mod (net35) and the voice tests (net8): plain arrays and collections only.
using System;
using System.Collections.Generic;

namespace StbcRevival.Voice
{
    public sealed class NetSim
    {
        public const double SlotMs = 20.0;

        sealed class Held
        {
            public byte[] Data;
            public int Length;
            public double ReleaseMs;
        }

        readonly int lossPercent, burst, reorderPercent, jitterMs;
        readonly Random rng;
        readonly List<Held> queue = new List<Held>();   // sorted by ReleaseMs, equal times in arrival order
        int burstLeft;

        NetSim(int loss, int burst, int reorder, int jitter, int seed)
        {
            lossPercent = loss; this.burst = burst; reorderPercent = reorder; jitterMs = jitter;
            rng = new Random(seed);
        }

        // "" or null, or every value 0 -> null (off). Unknown keys are ignored; a bad number counts as 0.
        public static NetSim Parse(string spec, int seed)
        {
            if (string.IsNullOrEmpty(spec)) return null;
            int loss = 0, burst = 0, reorder = 0, jitter = 0;
            string[] parts = spec.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                int eq = parts[i].IndexOf('=');
                if (eq < 0) continue;
                string key = parts[i].Substring(0, eq).Trim().ToLowerInvariant();
                int v;
                if (!int.TryParse(parts[i].Substring(eq + 1).Trim(), out v) || v < 0) v = 0;
                if (key == "loss") loss = v;
                else if (key == "burst") burst = v;
                else if (key == "reorder") reorder = v;
                else if (key == "jitter") jitter = v;
            }
            if (loss == 0 && burst == 0 && reorder == 0 && jitter == 0) return null;
            return new NetSim(loss, burst, reorder, jitter, seed);
        }

        public void Offer(byte[] packet, int length, double nowMs)
        {
            if (burstLeft > 0) { burstLeft--; return; }
            if (lossPercent > 0 && rng.Next(100) < lossPercent) { burstLeft = burst; return; }

            double release = nowMs;
            // A reordered packet is held one slot plus 1 ms, so it lands behind the next one at the real 20 ms spacing.
            if (reorderPercent > 0 && rng.Next(100) < reorderPercent) release += SlotMs + 1;
            if (jitterMs > 0) release += rng.NextDouble() * jitterMs;

            var h = new Held { Data = new byte[length], Length = length, ReleaseMs = release };
            Buffer.BlockCopy(packet, 0, h.Data, 0, length);
            int at = queue.Count;
            while (at > 0 && queue[at - 1].ReleaseMs > release) at--;   // after any equal time: arrival order
            queue.Insert(at, h);
        }

        public bool TryTake(double nowMs, out byte[] packet, out int length)
        {
            if (queue.Count == 0 || queue[0].ReleaseMs > nowMs)
            {
                packet = null; length = 0;
                return false;
            }
            Held h = queue[0];
            queue.RemoveAt(0);
            packet = h.Data; length = h.Length;
            return true;
        }
    }
}
