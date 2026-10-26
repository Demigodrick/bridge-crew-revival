using System;
using System.Collections.Generic;
using HarmonyLib;
using Sector;
using Sector.Spacecraft;
using BodyRef = Mission.Scripting.KOOLIJPNDOH;          // a reference to a sector body (ship), filled by spawn tasks
using MissionSequence = Mission.Scripting.MNFBGALLEAJ;  // base class of mission sequences
using MissionObjective = Mission.Scripting.MJHDDKANLEC; // base class of a sequence's objectives
using Step = Mission.Scripting.Framework.Tasks.KAPOBAJDOEJ; // a scripting task, yielded from objective coroutines
using StartSystem = BHNEPPPPMKH;                         // starting-system choice
using Tag = BLECEHNPNBH;
using Team = Sector.DGGJOECIFAF;

namespace StbcRevival.Missions
{
    /// <summary>
    /// PvP test (phase 1 of player-vs-player, a debug mission: [Debug] PvpTest shows it in the briefing): a second
    /// player ship, "PvP B", spawns ahead of yours on the Klingon team, both ships targetable and attackable by
    /// phasers and torpedoes. Each player's game stays on its own ship (PvpPrototype); [Debug] PvpTestShip = B
    /// puts you on the second one instead. Won when PvP B is destroyed; lost, as always, if the first ship is.
    /// </summary>
    public sealed class PvpTestSequence : MissionSequence
    {
        public override void CreateSequence()
        {
            var fight = Sequence.MakeNode<PvpTestObjective>();
            Sequence.OnFail(fight).MissionFails();
            Sequence.OnSucceed(fight).MissionSucceeds();
            Sequence.StartObjective(fight);
        }

        public override void StartingSystemSelection(StartSystem selection)
        {
            selection.ForceRandomSystemSelection = true;
            selection.Choice = StartSystem.LPCLBKKEBMK.Random;
        }
    }

    public sealed class PvpTestObjective : MissionObjective
    {
        internal const string Title = "PVP TEST";

        private BodyRef player;
        private Map.SystemRoot system;

        public override void StartScript()
        {
            player = Objective.FFKKADIEOGP;
            system = Objective.KALBLMGFNIN;
            Objective.AddStep("pvp", Task.MFLJBKCDBMC.WithSubtaskCoroutine(Fight), Objective.Success());
        }

        private IEnumerator<Step> Fight()
        {
            const string directive = "PvP test", target = "Destroy PvP B, the other player ship";
            yield return Task.NECMCDDOFJB.WithDescriptionString(directive);
            yield return Task.NECMCDDOFJB.AddSubDirectives(directive, target);
            yield return Task.HHNFJKHGEPH.WithMissionString(Title);
            yield return Task.OGJONIFINCB.DisableWarping(); // the fight is here (and LastStandLocks)

            // The second ship: the same ship as yours, 3 km ahead and facing you.
            var other = new BodyRef();
            var spawner = Objective.MNKGANNGIKK.WithPrefab(PlayerShipPrefab()).InSystem(system)
                .AssignName(PvpPrototype.ShipBName).AddTags(Tag.Enemy, Tag.Ship);
            yield return Task.BFMBGDIDKEI.UsingSpawner(spawner).AtAngleAndDistanceFromBody(player, 0f, 3000f).FaceOtherBody(player).OutputTo(other);
            float spawned = UnityEngine.Time.time;
            yield return Task.KNMMMJOOBHC.WithTime(2f);

            var a = player.AHMPFHDGMKJ as PlayerSpacecraft;
            var b = other.AHMPFHDGMKJ as PlayerSpacecraft;
            if (b == null)
            {
                Plugin.Log.LogWarning($"PvP test: the second ship didn't spawn as a player ship ({other.AHMPFHDGMKJ?.GetType().Name ?? "nothing"})");
                yield return Task.HHNFJKHGEPH.WithMissionResult("PvP test: the second ship didn't spawn", false);
                yield break;
            }
            // Teams: the first ship stays Federation, the second is Klingon (players on it see it the other way
            // round: PvpPrototype). Both can be targeted and hit by the other's weapons.
            if (b.GetTargetInfo() is TargetInfoComponent info) info.DGGJOECIFAF = Team.Klingon;
            foreach (var ship in new[] { a, b })
            {
                if (ship == null || !(ship.GetDetectable() is TargetableComponent targetable)) continue;
                targetable.SetAttackable(true);
                targetable.SetTargetableByTactical(true);
            }
            Plugin.Log.LogInfo($"PvP test: {Describe(a)} and {Describe(b)} spawned");

            int ticks = 0;
            while (LastStandWaves.Alive(other, UnityEngine.Time.time - spawned < 10f))
            {
                yield return Task.KNMMMJOOBHC.WithTime(2f);
                if (++ticks % 10 == 0) Plugin.Log.LogInfo($"PvP test: {Describe(a)}; {Describe(b)}; bound to {PvpPrototype.Bound?.name ?? "nothing"}");
            }

            Plugin.Log.LogInfo("PvP test: PvP B destroyed");
            yield return Task.NECMCDDOFJB.CompleteSubDirective(directive, target);
            yield return Task.HHNFJKHGEPH.WithMissionResult("PvP B destroyed", true);
            yield return Task.KNMMMJOOBHC.WithTime(10f);
        }

        // The ship the game gave the crew (MissionManager.NJHADEBHMNN's choice): retro, TNG or the Aegis.
        private static string PlayerShipPrefab()
        {
            string field = Loader.JCEFNOJHCKO ? "_retroPlayerCraftPrefabPath" : Loader.IOMGCNMDDCK ? "_TNGPlayerCraftPrefabPath" : "_playerCraftPrefabPath";
            var path = AccessTools.Field(typeof(Mission.MissionManager), field)?.GetValue(Mission.MissionManager.IEFBKOMBFGO) as string;
            return string.IsNullOrEmpty(path) ? "Sector/Ships/Ship - Aegis" : path;
        }

        // "PvP B (Klingon, hull 100%, attackable)", for the log.
        private static string Describe(PlayerSpacecraft ship)
        {
            if (ship == null) return "a missing ship";
            try
            {
                var info = ship.GetTargetInfo();
                var target = ship.GetDetectable() as TargetableComponent;
                var team = info is TargetInfoComponent t ? PvpPrototype.RawTeam(t).ToString() : "?";
                return $"{ship.ALPAOOKFIEE?.BaseName ?? ship.name} ({team}, hull {info?.GBNCJMFFIDI:F0}, {(target != null && target.KODEOPDPGJH ? "attackable" : "not attackable")})";
            }
            catch (Exception e)
            {
                return $"{ship.name} ({e.GetType().Name})";
            }
        }
    }
}
