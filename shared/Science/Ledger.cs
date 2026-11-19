using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace StbcRevival.Science
{
    public enum UpgradeArea { Weapons, Shields, Impulse, Repair }
    public enum EnemyArea { Damage, Shields, Speed }

    // What a completed target grants. The host looks this up; the client never says.
    internal sealed class TargetInfo
    {
        public UpgradeArea? Area = null; // set by the targets (Task 5)
        public int Team = 0;
    }

    // The mission-wide upgrades earned by science scans. Published as a room property by the host.
    // Wire: "SCL1;<on 0|1>;<w>,<s>,<i>,<r>;<team>:<v>,<team>:<v>;<enemySteps>"
    internal sealed class Ledger
    {
        const string Tag = "SCL1";
        const int MaxEnemySteps = 10000;
        const int MaxFactions = 16;
        static readonly int AreaCount = Enum.GetValues(typeof(UpgradeArea)).Length;

        readonly float[] crew = new float[AreaCount];
        readonly Dictionary<int, float> faction = new Dictionary<int, float>();

        public bool On;
        public int EnemySteps { get; private set; }

        // The game's Team enum as ints: None 0, Federation 1, Klingon 2, NeutralFriendly 3, NeutralHostile 4, Romulan 5, Borg 6.
        // Pirates are teams 7 and 8. Hostile: every real team except Federation and NeutralFriendly.
        public static bool IsHostileTeam(int team)
        {
            return team > 0 && team != 1 && team != 3;
        }

        public float Crew(UpgradeArea a) { return crew[(int)a]; }

        public float CrewVsFaction(int team)
        {
            float v;
            return faction.TryGetValue(team, out v) ? v : 0f;
        }

        public float Enemy(EnemyArea a)
        {
            int i = (int)a;
            return ((EnemySteps + 2 - i) / 3) * ScienceTuning.EnemyStep;
        }

        public void AddScan(UpgradeArea a)
        {
            int i = (int)a;
            crew[i] = Step(crew[i]);
        }

        public void AddFactionScan(int team)
        {
            float v;
            if (!faction.TryGetValue(team, out v) && faction.Count >= MaxFactions) return;
            faction[team] = Step(v);
        }

        public void AddEnemyStep() { if (EnemySteps < MaxEnemySteps) EnemySteps++; }

        static float Step(float v)
        {
            v += ScienceTuning.CrewStep;
            return v > ScienceTuning.CrewCap ? ScienceTuning.CrewCap : v;
        }

        static string F(float v) { return v.ToString("R", CultureInfo.InvariantCulture); }

        public string ToWire()
        {
            StringBuilder sb = new StringBuilder(Tag);
            sb.Append(';').Append(On ? '1' : '0').Append(';');
            for (int i = 0; i < crew.Length; i++) { if (i > 0) sb.Append(','); sb.Append(F(crew[i])); }
            sb.Append(';');
            List<int> teams = new List<int>(faction.Keys);
            teams.Sort();
            for (int i = 0; i < teams.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(teams[i].ToString(CultureInfo.InvariantCulture)).Append(':').Append(F(faction[teams[i]]));
            }
            sb.Append(';').Append(EnemySteps.ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        // Null or anything malformed gives a fresh, switched-off ledger.
        public static Ledger FromWire(string s)
        {
            Ledger l = new Ledger();
            if (string.IsNullOrEmpty(s)) return l;
            string[] p = s.Split(';');
            if (p.Length != 5 || p[0] != Tag) return l;
            try
            {
                Ledger r = new Ledger();
                if (p[1] == "1") r.On = true; else if (p[1] != "0") return l;
                string[] c = p[2].Split(',');
                if (c.Length != AreaCount) return l;
                for (int i = 0; i < c.Length; i++) if (!ParseShare(c[i], out r.crew[i])) return l;
                if (p[3].Length > 0)
                {
                    string[] f = p[3].Split(',');
                    if (f.Length > MaxFactions) return l;
                    for (int i = 0; i < f.Length; i++)
                    {
                        string[] kv = f[i].Split(':');
                        int team; float v;
                        if (kv.Length != 2 || !int.TryParse(kv[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out team)) return l;
                        if (!ParseShare(kv[1], out v) || r.faction.ContainsKey(team)) return l;
                        r.faction[team] = v;
                    }
                }
                int steps;
                if (!int.TryParse(p[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out steps) || steps < 0 || steps > MaxEnemySteps) return l;
                r.EnemySteps = steps;
                return r;
            }
            catch (Exception) { return l; }
        }

        static bool ParseShare(string s, out float v)
        {
            if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return false;
            return !float.IsNaN(v) && !float.IsInfinity(v) && v >= 0f && v <= ScienceTuning.CrewCap + 0.0001f;
        }
    }
}
