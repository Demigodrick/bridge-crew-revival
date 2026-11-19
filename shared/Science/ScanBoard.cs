using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace StbcRevival.Science
{
    // Host-owned state of the scan targets: progress, who holds the console, what is done.
    // Wire: "SCB1;<id>,<progress>,<done 0|1>;<id>,..."
    internal sealed class ScanBoard
    {
        const string Tag = "SCB1";
        public const int MaxIdLength = 64;
        public const int MaxTargets = 64;
        public const float ClaimSeconds = 3f;

        sealed class Entry { public float Progress; public bool Done; }

        readonly Dictionary<string, Entry> targets = new Dictionary<string, Entry>();
        readonly List<string> order = new List<string>();
        int claimant;
        float lastBeat;

        public int Count { get { return order.Count; } }

        // The listed ids, in order (a copy).
        public string[] Ids() { return order.ToArray(); }

        public static bool ValidId(string id)
        {
            return id != null && id.Length > 0 && id.Length <= MaxIdLength && id.IndexOf(';') < 0 && id.IndexOf(',') < 0;
        }

        // Keeps the progress of ids still listed and forgets the rest. Unusable ids are skipped.
        public void SetTargets(IEnumerable<string> ids)
        {
            Dictionary<string, Entry> next = new Dictionary<string, Entry>();
            List<string> nextOrder = new List<string>();
            if (ids != null)
                foreach (string id in ids)
                {
                    if (!ValidId(id) || next.ContainsKey(id) || nextOrder.Count >= MaxTargets) continue;
                    Entry e;
                    next[id] = targets.TryGetValue(id, out e) ? e : new Entry();
                    nextOrder.Add(id);
                }
            targets.Clear();
            order.Clear();
            foreach (string id in nextOrder) { targets[id] = next[id]; order.Add(id); }
        }

        public float Progress(string id)
        {
            Entry e;
            return id != null && targets.TryGetValue(id, out e) ? e.Progress : 0f;
        }

        public bool IsDone(string id)
        {
            Entry e;
            return id != null && targets.TryGetValue(id, out e) && e.Done;
        }

        public int Claimant(float now)
        {
            return claimant != 0 && now - lastBeat <= ClaimSeconds ? claimant : 0;
        }

        public bool TryClaim(int actor, float now)
        {
            if (actor == 0) return false;
            int c = Claimant(now);
            if (c != 0 && c != actor) return false;
            claimant = actor;
            lastBeat = now;
            return true;
        }

        public void Heartbeat(int actor, float now)
        {
            if (actor != 0 && Claimant(now) == actor) lastBeat = now;
        }

        public void Release(int actor)
        {
            if (actor != 0 && claimant == actor) claimant = 0;
        }

        public bool Report(int actor, string id, float progress, float now)
        {
            if (actor == 0 || Claimant(now) != actor) return false;
            if (float.IsNaN(progress) || float.IsInfinity(progress)) return false;
            Entry e;
            if (id == null || !targets.TryGetValue(id, out e)) return false;
            if (progress > 100f) progress = 100f;
            if (progress < 0f) progress = 0f;
            if (progress < e.Progress) return false;
            e.Progress = progress;
            lastBeat = now;
            return true;
        }

        public bool Complete(int actor, string id, float now)
        {
            if (actor == 0 || Claimant(now) != actor) return false;
            Entry e;
            if (id == null || !targets.TryGetValue(id, out e) || e.Done) return false;
            e.Done = true;
            e.Progress = 100f;
            lastBeat = now;
            return true;
        }

        public string ToWire()
        {
            StringBuilder sb = new StringBuilder(Tag);
            foreach (string id in order)
            {
                Entry e = targets[id];
                sb.Append(';').Append(id).Append(',')
                  .Append(e.Progress.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(e.Done ? '1' : '0');
            }
            return sb.ToString();
        }

        public static ScanBoard FromWire(string s)
        {
            ScanBoard b = new ScanBoard();
            if (string.IsNullOrEmpty(s)) return b;
            string[] p = s.Split(';');
            if (p[0] != Tag || p.Length - 1 > MaxTargets) return b;
            ScanBoard r = new ScanBoard();
            for (int i = 1; i < p.Length; i++)
            {
                string[] f = p[i].Split(',');
                float v;
                if (f.Length != 3 || !ValidId(f[0]) || r.targets.ContainsKey(f[0])) return b;
                if (!float.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out v)
                    || float.IsNaN(v) || float.IsInfinity(v) || v < 0f || v > 100f) return b;
                if (f[2] != "0" && f[2] != "1") return b;
                r.targets[f[0]] = new Entry { Progress = v, Done = f[2] == "1" };
                r.order.Add(f[0]);
            }
            return r;
        }
    }
}
