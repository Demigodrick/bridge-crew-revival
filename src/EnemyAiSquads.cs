using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// Stage 6 of the enemy AI work: enemy ships fight in squads (host only, where the AI
    /// runs). Every SquadEvery seconds the hostile AI ships (Klingon, Romulan, pirate; not Borg cubes) of each side, in
    /// each system where a side has at least MinFleet ships, are put in squads: the biggest ships lead (a flagship leads
    /// up to FlagshipWingmen, any other ship up to Wingmen), and every other ship joins the nearest squad with room whose
    /// leader is within JoinDistance, or leads one of its own.
    ///
    /// - No tether: the game's own (AgentShip.SetTether) was tried for keeping wingmen with their leader, but its
    ///   return-to-tether branch outranks attacking, so a wingman more than the tether distance away (as attack runs
    ///   often take it) broke off and flew back without firing: in Patrol's intercept the escort barely fought.
    /// - Focus fire: a wingman's aggro list gives LeaderTargetBonus to its leader's target, and LeaderAttackerBonus to
    ///   whoever last hit its leader; its squad doesn't count as a crowd on its leader's target (EnemyAiCombat).
    /// - When a leader is destroyed its wingmen are let go (their tether off) and join or lead new squads.
    /// A ship a mission has tethered is left out. Turning the setting off breaks the squads up.
    /// </summary>
    internal static class EnemyAiSquads
    {
        private static bool On => true; // part of the mod (no longer a setting)

        private const float SquadEvery = 2f;            // seconds between squad updates
        private const int MinFleet = 4;                 // ships a side needs in a system before it forms squads
        private const int Wingmen = 2, FlagshipWingmen = 4;
        private const double JoinDistance = 12000;      // metres from a leader to join its squad
        internal const float LeaderTargetBonus = 40f, LeaderAttackerBonus = 30f;

        private sealed class Squad
        {
            public AI.AgentShip Leader;
            public readonly List<AI.AgentShip> Wingmen = new List<AI.AgentShip>();
            public int Room;
        }

        private static readonly Dictionary<AI.AgentShip, Squad> squadOf = new Dictionary<AI.AgentShip, Squad>(); // every member, leader included
        private static readonly List<Squad> squads = new List<Squad>();
        private static readonly List<AI.AgentShip> candidates = new List<AI.AgentShip>();
        private static float nextUpdate;
        private static string lastSummary;
        private static float nextSummary;
        private static readonly Func<bool> isMaster = MasterGetter();

        private static Func<bool> MasterGetter()
        {
            var getter = AccessTools.Method(typeof(NDIOGIGMPBH), "get_isMasterClient"); // PhotonNetwork.isMasterClient
            return getter != null ? (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), getter) : () => false;
        }

        /// <summary>Called every frame (Plugin.Update).</summary>
        internal static void Update()
        {
            float now = Time.time;
            if (now < nextUpdate) return;
            nextUpdate = now + SquadEvery;
            bool master;
            try { master = isMaster(); } catch { master = false; }
            if (!On || !master)
            {
                if (squads.Count > 0) ReleaseAll();
                return;
            }
            Prune();
            Form();
        }

        // ---- What the aggro list asks ---------------------------------------------------------------------------------

        /// <summary>The squad bonus a ship's aggro list gives a body: its leader's target, or whoever last hit its leader.</summary>
        internal static float Bonus(AI.AgentShip ship, Sector.HOLJLCLENCA body)
        {
            if (!On || body == null || !squadOf.TryGetValue(ship, out var squad) || ReferenceEquals(squad.Leader, ship)) return 0f;
            var leader = squad.Leader;
            if (leader == null) return 0f;
            float bonus = 0f;
            var bb = leader.NMJIEDJOOCJ;
            if (bb != null && ReferenceEquals(bb.Target, body)) bonus += LeaderTargetBonus;
            if (ReferenceEquals(leader.BEAIJNEDPGA, body)) bonus += LeaderAttackerBonus; // LastToCauseDamage
            return bonus;
        }

        /// <summary>How many of a ship's own squad (not counting itself) are targeting a body: they don't count as a crowd.</summary>
        internal static int MatesOn(AI.AgentShip ship, Sector.HOLJLCLENCA body)
        {
            if (!On || body == null || !squadOf.TryGetValue(ship, out var squad)) return 0;
            int n = 0;
            if (squad.Leader != null && !ReferenceEquals(squad.Leader, ship) && ReferenceEquals(squad.Leader.NMJIEDJOOCJ?.Target, body)) n++;
            foreach (var mate in squad.Wingmen)
                if (mate != null && !ReferenceEquals(mate, ship) && ReferenceEquals(mate.NMJIEDJOOCJ?.Target, body)) n++;
            return n;
        }

        // ---- Squads -------------------------------------------------------------------------------------------------------

        private static bool Fighting(AI.AgentShip ship)
        {
            try
            {
                return ship != null && ship.gameObject.activeInHierarchy && !ship.HAFBKMDMMIO /* IsDestroyed */ && ship.NMJIEDJOOCJ != null
                       && !(ship is AI.BorgCube) && EnemyAiTuning.HostileTeam(ship) && ship.ALPAOOKFIEE != null && !ship.ALPAOOKFIEE.InAttic;
            }
            catch { return false; }
        }

        private static void Prune()
        {
            for (int i = squads.Count - 1; i >= 0; i--)
            {
                var squad = squads[i];
                if (!Fighting(squad.Leader))
                {
                    foreach (var wingman in squad.Wingmen) Release(wingman);
                    if (squad.Leader != null) squadOf.Remove(squad.Leader);
                    squads.RemoveAt(i);
                    continue;
                }
                for (int j = squad.Wingmen.Count - 1; j >= 0; j--)
                {
                    var wingman = squad.Wingmen[j];
                    if (Fighting(wingman) && ReferenceEquals(wingman.ALPAOOKFIEE.System, squad.Leader.ALPAOOKFIEE.System)) continue;
                    Release(wingman);
                    squad.Wingmen.RemoveAt(j);
                }
            }
        }

        private static void Form()
        {
            // Sides: team and system, with how many ships each has.
            var sides = new Dictionary<string, int>();
            candidates.Clear();
            foreach (var ship in EnemyAiFlying.Ships)
            {
                if (!Fighting(ship)) continue;
                string side = Side(ship);
                sides[side] = sides.TryGetValue(side, out int n) ? n + 1 : 1;
                if (!squadOf.ContainsKey(ship)) candidates.Add(ship);
            }
            // A side too small for squads: let its squads go.
            for (int i = squads.Count - 1; i >= 0; i--)
            {
                if (sides.TryGetValue(Side(squads[i].Leader), out int n) && n >= MinFleet) continue;
                Dissolve(squads[i]);
                squads.RemoveAt(i);
            }
            if (candidates.Count == 0) { Summarise(sides); return; }
            candidates.Sort((a, b) => MaxHealth(b).CompareTo(MaxHealth(a))); // biggest first: they lead
            foreach (var ship in candidates)
            {
                if (!sides.TryGetValue(Side(ship), out int count) || count < MinFleet) continue;
                if (ship.NMJIEDJOOCJ.IsTetherEnabled) continue; // a mission's tether: leave it be
                Squad best = null;
                double bestDistance = JoinDistance;
                var me = ship.ALPAOOKFIEE;
                foreach (var squad in squads)
                {
                    if (squad.Wingmen.Count >= squad.Room || Side(squad.Leader) != Side(ship)) continue;
                    double distance = (squad.Leader.ALPAOOKFIEE.Position - me.Position).magnitude;
                    if (distance < bestDistance) { bestDistance = distance; best = squad; }
                }
                if (best != null)
                {
                    best.Wingmen.Add(ship);
                    squadOf[ship] = best;
                }
                else
                {
                    var squad = new Squad { Leader = ship, Room = ship.name.IndexOf("Flagship", StringComparison.OrdinalIgnoreCase) >= 0 ? FlagshipWingmen : Wingmen };
                    squads.Add(squad);
                    squadOf[ship] = squad;
                }
            }
            Summarise(sides);
        }

        private static string Side(AI.AgentShip ship)
        {
            try { return Convert.ToInt32(AccessTools.Field(typeof(AI.AgentShip), "_teamID").GetValue(ship)) + "/" + ship.ALPAOOKFIEE.System?.GetHashCode(); }
            catch { return "?"; }
        }

        private static float MaxHealth(AI.AgentShip ship)
        {
            try { return ship.DCPNJKKALHJ; } catch { return 0f; }
        }

        private static void Release(AI.AgentShip ship)
        {
            if (ship == null) return;
            squadOf.Remove(ship);
        }

        private static void Dissolve(Squad squad)
        {
            foreach (var wingman in squad.Wingmen) Release(wingman);
            if (squad.Leader != null) squadOf.Remove(squad.Leader);
        }

        private static void ReleaseAll()
        {
            foreach (var squad in squads) Dissolve(squad);
            squads.Clear();
            squadOf.Clear();
            lastSummary = null;
        }

        private static void Summarise(Dictionary<string, int> sides)
        {
            int wingmen = 0, escorts = 0;
            foreach (var squad in squads)
            {
                wingmen += squad.Wingmen.Count;
                if (squad.Room == FlagshipWingmen) escorts++;
            }
            string summary = $"{squads.Count} squads ({escorts} led by flagships), {wingmen} wingmen";
            if (summary == lastSummary || squads.Count == 0 || Time.time < nextSummary) return;
            lastSummary = summary;
            nextSummary = Time.time + 30f;
            Plugin.Log.LogInfo("Enemy AI squads: " + summary);
        }
    }
}
