using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace StbcRevival.Science
{
    // What the science console's screen shows.
    public enum ScreenMode { Targets, Scan, OutOfRange, Busy }

    // Where the scanned target is now: listed and within range, listed but too far, or not listed at all.
    internal enum TargetState { InRange, OutOfRange, Missing }

    // The messages the console sends the host (ScienceSession in the mod, a recorder in the tests).
    internal interface IScienceLink
    {
        void Claim();
        void Release();
        void Heartbeat();
        void Report(string id, float progress);
        void Complete(string id);
    }

    /// <summary>
    /// The science console's screen without the screen: which mode it is in, the two mini games and the scan's progress,
    /// and when to claim, heartbeat, report, complete and release. The screen feeds it the board's claimant, where the
    /// target is and the time; it renders what this says and passes taps and slider moves on.
    ///   - Targets: the list. Tapping one (Start) claims the console and opens Scan, with the target's two mini games
    ///     (the same on every machine for that target) and the progress the board already has for it.
    ///   - Scan: the games run and the bar fills while both hold a lock; reports 4 times a second while locked, a
    ///     heartbeat once a second, and a claim again once a second while the board doesn't show us as the claimant
    ///     (a new host forgets the claim; a claim lapses 3 s after the last beat). At 100: report, complete, release,
    ///     back to Targets, but only while the board shows us as the claimant (the host drops them from anyone else);
    ///     until then it waits at 100, claiming again.
    ///   - OutOfRange: the target is listed but too far: paused, one report of where it got to; the heartbeat goes on, so
    ///     the console stays ours. Back to Scan when it's in range again.
    ///   - Busy: someone else holds the console. Whatever we were scanning is dropped (its progress is on the board); back
    ///     to Targets when it's free.
    /// A target that's done on the board, or gone from the list for longer than MissingGrace, ends the scan; one at 100 but
    /// not done can be started again, to complete it.
    /// </summary>
    internal sealed class ConsoleFlow
    {
        public const float ReportEvery = 0.25f, BeatEvery = 1f, ClaimEvery = 1f, MissingGrace = 3f;

        readonly IScienceLink link;
        float nextReport, nextBeat, nextClaim, missingSince = -1f;

        public ConsoleFlow(IScienceLink link) { this.link = link; }

        public ScreenMode Mode { get; private set; }
        public string TargetId { get; private set; }
        public MiniGame GameA { get; private set; }
        public MiniGame GameB { get; private set; }
        public ScanProgress Progress { get; private set; }
        // Who holds the console while Busy (0 otherwise).
        public int BusyActor { get; private set; }

        // A scan of ours is open (running or paused out of range).
        public bool Holding { get { return Mode == ScreenMode.Scan || Mode == ScreenMode.OutOfRange; } }

        // The local player holds the claim.
        public bool InUse(int claimant, int localActor) { return Holding && claimant != 0 && claimant == localActor; }

        // The two games a target gets: from its id, the same on every machine.
        public static MiniGame[] GamesFor(string id)
        {
            int seed = MiniGamePick.SeedOf(id);
            MiniGameKind[] kinds = MiniGamePick.For(seed);
            var games = new[] { MiniGame.Create(kinds[0], seed), MiniGame.Create(kinds[1], unchecked(seed * 31 + 7)) };
            games[(seed >> 3) & 1].Steady = true; // only one of the two drifts (which one: from the id too)
            return games;
        }

        /// <summary>
        /// A target row was tapped: claim and open its scan from the board's progress. False if it can't start (it's done).
        /// A target the board has at 100 but not done (its scanner left, or dropped, before the claim let it complete)
        /// opens at 100 and finishes as soon as the board shows us as the claimant.
        /// </summary>
        public bool Start(string id, float boardProgress, bool boardDone, float now)
        {
            if (Mode != ScreenMode.Targets || string.IsNullOrEmpty(id) || boardDone) return false;
            MiniGame[] g = GamesFor(id);
            TargetId = id;
            GameA = g[0];
            GameB = g[1];
            GameA.Tick(now);
            GameB.Tick(now);
            Progress = new ScanProgress(boardProgress);
            Mode = ScreenMode.Scan;
            missingSince = -1f;
            link.Claim();
            nextClaim = now + ClaimEvery;
            nextBeat = now + BeatEvery;
            nextReport = now + ReportEvery;
            return true;
        }

        /// <summary>Back to the list by choice (or the player left): what's done so far is reported, the console freed.</summary>
        public void Leave()
        {
            if (!Holding) return;
            if (Progress.Value > 0f) link.Report(TargetId, Progress.Value);
            link.Release();
            ToTargets();
        }

        public void Tick(float now, float dt, int claimant, int localActor, TargetState target, bool boardDone)
        {
            if (claimant != 0 && claimant != localActor)
            {
                if (Mode != ScreenMode.Busy) { Clear(); Mode = ScreenMode.Busy; }
                BusyActor = claimant;
                return;
            }
            if (Mode == ScreenMode.Busy) { BusyActor = 0; Mode = ScreenMode.Targets; }
            if (!Holding) return;

            if (target == TargetState.Missing)
            {
                if (missingSince < 0f) missingSince = now;
            }
            else missingSince = -1f;
            if (boardDone || (missingSince >= 0f && now - missingSince >= MissingGrace))
            {
                link.Release();
                ToTargets();
                return;
            }

            if (claimant != localActor && now >= nextClaim) { link.Claim(); nextClaim = now + ClaimEvery; }
            if (now >= nextBeat) { link.Heartbeat(); nextBeat = now + BeatEvery; }

            // At 100 but not (yet) shown as the claimant: the host would drop the report and the complete. Wait at 100,
            // claiming again, and finish once the board shows us.
            if (Progress.Done)
            {
                if (claimant == localActor) Finish();
                return;
            }

            if (target != TargetState.InRange)
            {
                if (Mode == ScreenMode.Scan)
                {
                    Mode = ScreenMode.OutOfRange;
                    link.Report(TargetId, Progress.Value);
                }
                return;
            }
            if (Mode == ScreenMode.OutOfRange) Mode = ScreenMode.Scan;

            GameA.Tick(now);
            GameB.Tick(now);
            Progress.Advance(GameA.Match, GameB.Match, dt);
            if (Progress.Done)
            {
                if (claimant == localActor) Finish();
                return;
            }
            if (Progress.Locked && now >= nextReport)
            {
                link.Report(TargetId, Progress.Value);
                nextReport = now + ReportEvery;
            }
        }

        void Finish()
        {
            link.Report(TargetId, 100f);
            link.Complete(TargetId);
            link.Release();
            ToTargets();
        }

        void ToTargets() { Clear(); Mode = ScreenMode.Targets; }

        void Clear()
        {
            TargetId = null;
            GameA = GameB = null;
            Progress = null;
            missingSince = -1f;
        }
    }

    // The console's lines that come from numbers. tr translates an English template (identity in tests).
    internal static class ConsoleText
    {
        static readonly string[] AreaNames = { "Weapons", "Shields", "Impulse", "Repair" };

        // "Upgrades: Weapons +10%  Shields +5%  vs Klingon +5%", or "Upgrades: none".
        public static string Upgrades(Ledger l, Func<string, string> tr)
        {
            StringBuilder sb = new StringBuilder();
            foreach (UpgradeArea a in Enum.GetValues(typeof(UpgradeArea)))
            {
                int pct = Pct(l.Crew(a));
                if (pct <= 0) continue;
                if (sb.Length > 0) sb.Append("  ");
                sb.Append(tr(AreaNames[(int)a])).Append(" +").Append(pct.ToString(CultureInfo.InvariantCulture)).Append('%');
            }
            // Teams with one name (the two pirate teams) show once, at the better share.
            List<string> names = new List<string>();
            List<int> best = new List<int>();
            foreach (int team in l.FactionTeams())
            {
                int pct = Pct(l.CrewVsFaction(team));
                if (pct <= 0) continue;
                string name = ScienceKinds.Faction(team);
                int i = names.IndexOf(name);
                if (i < 0) { names.Add(name); best.Add(pct); }
                else if (pct > best[i]) best[i] = pct;
            }
            for (int i = 0; i < names.Count; i++)
            {
                if (sb.Length > 0) sb.Append("  ");
                sb.Append(tr("vs " + names[i])).Append(" +").Append(best[i].ToString(CultureInfo.InvariantCulture)).Append('%');
            }
            return string.Format(tr("Upgrades: {0}"), sb.Length > 0 ? sb.ToString() : tr("none"));
        }

        // Kilometres: one decimal under 10 km, whole above. comma: German decimal comma.
        public static string Km(double metres, bool comma)
        {
            double km = metres / 1000.0;
            string s = km < 10.0 ? km.ToString("0.0", CultureInfo.InvariantCulture) : Math.Round(km).ToString("0", CultureInfo.InvariantCulture);
            return comma ? s.Replace('.', ',') : s;
        }

        static int Pct(float share) { return (int)Math.Round(share * 100f); }
    }
}
