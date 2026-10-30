using System;
using System.Collections.Generic;
using System.Linq;
using BodyRef = Mission.Scripting.KOOLIJPNDOH;          // a reference to a sector body (ship), filled by spawn tasks
using MissionSequence = Mission.Scripting.MNFBGALLEAJ;  // base class of mission sequences
using MissionObjective = Mission.Scripting.MJHDDKANLEC; // base class of a sequence's objectives
using Spawner = Mission.Scripting.FFPNMKADDPK;          // a spawn definition (prefab, system, name, tags)
using Step = Mission.Scripting.Framework.Tasks.KAPOBAJDOEJ; // a scripting task, yielded from objective coroutines
using StartSystem = BHNEPPPPMKH;                         // starting-system choice
using Tag = BLECEHNPNBH;
using AIObjective = AI.CPLIILIDJKG;                      // AI objective kinds (Attack, ...)

namespace StbcRevival.Missions
{
    /// <summary>
    /// Fleet Battle (Revival Missions): a Starfleet fleet and yours against a Klingon or Romulan fleet,
    /// [Revival] FleetBattleSize ships a side (30 by default). Allies form up around you, the enemy arrives
    /// ahead: every ally is ordered to attack an enemy ship, and the enemy hunts in packs of five (one ship in
    /// six comes for you), sending a third as many again as reinforcements when it's down to half, up to twice.
    /// The allied fleet arrives a little battle-worn (85% hull). Won when
    /// the enemy fleet is destroyed; lost if your ship is. No warp or impulse travel: the battle is here.
    /// </summary>
    public sealed class FleetBattleSequence : MissionSequence
    {
        public override void CreateSequence()
        {
            var battle = Sequence.MakeNode<FleetBattleObjective>();
            Sequence.OnFail(battle).MissionFails();
            Sequence.OnSucceed(battle).MissionSucceeds();
            Sequence.StartObjective(battle);
        }

        public override void StartingSystemSelection(StartSystem selection)
        {
            selection.ForceRandomSystemSelection = true;
            selection.Choice = StartSystem.LPCLBKKEBMK.Random;
        }
    }

    public sealed class FleetBattleObjective : MissionObjective
    {
        internal const string Title = "FLEET BATTLE";

        // Mostly light ships, so the fight isn't one-sided (the cruisers outclass the raiders they meet).
        private static readonly string[] Starfleet =
        {
            "Sector/Ships/Ship - Saladin", "Sector/Ships/Ship - Saladin", "Sector/Ships/Ship - Raleigh", "Sector/Ships/Ship - Huxley",
        };
        // One empire per battle: a mixed fleet might turn on itself.
        private static readonly string[][] Enemies =
        {
            new[] { "Sector/Ships/Ship - Bird Of Prey", "Sector/Ships/Ship - D5 Cruiser", "Sector/Ships/Ship - D5 Cruiser" },
            new[] { "Sector/Ships/Ship - Romulan Scout", "Sector/Ships/Ship - Romulan Medium", "Sector/Ships/Ship - Romulan Medium" },
        };
        private static readonly string[] EnemyFlagships = { "Sector/Ships/Ship - Klingon Flagship", "Sector/Ships/Ship - Romulan DDeridex" };
        private static readonly string[] EnemyNames = { "Klingon", "Romulan" };

        private readonly System.Random random = new System.Random();
        private BodyRef player;
        private Map.SystemRoot system;
        private int enemyCount;

        public override void StartScript()
        {
            player = Objective.FFKKADIEOGP;
            system = Objective.KALBLMGFNIN;
            // Success after the script, as the game's own missions do: an objective can't be completed from
            // inside a subtask coroutine (the framework throws an InvalidCastException and the mission never ends).
            Objective.AddStep("battle", Task.MFLJBKCDBMC.WithSubtaskCoroutine(Battle), Objective.Success());
        }

        private IEnumerator<Step> Battle()
        {
            foreach (var step in MissionAudio.Start(Task)) yield return step;
            yield return Task.OGJONIFINCB.DisableWarping(); // and the helm's warp/impulse travel: see LastStandLocks
            int size = Math.Max(1, Math.Min(Plugin.FleetBattleSize.Value, 60));
            int empire = random.Next(Enemies.Length);
            string directive = $"Fleet battle: destroy the {EnemyNames[empire]} fleet";
            yield return Task.NECMCDDOFJB.WithDescriptionString(directive);
            yield return Task.HHNFJKHGEPH.WithMissionString(Title);
            yield return Task.OKLDNLKLLFJ.WithGroup("State_Group_Mus_Dyn").WithState("Moderate_Tension");
            // Nine allies for every ten enemies: at one for one the allied fleet won by itself (Romulans: 4 allies
            // lost to about 46 enemies); two for three was too hard, and four for five (with bigger reinforcements
            // and a more worn fleet) as well.
            int allyCount = Math.Max(1, size * 9 / 10);
            Plugin.Log.LogInfo($"Fleet battle: {allyCount} Starfleet ships against {size} {EnemyNames[empire]} ships");

            // Allies: a loose formation around and behind you, all facing the way you do.
            var allies = new List<BodyRef>();
            for (int i = 0; i < allyCount; i++)
            {
                var ship = new BodyRef();
                allies.Add(ship);
                float angle = 180f + random.Next(-70, 70), distance = 3000f + random.Next(0, 6000);
                yield return Task.BFMBGDIDKEI.UsingSpawner(MakeSpawner(Starfleet[random.Next(Starfleet.Length)], "USS", i, Tag.Friendly))
                    .AtAngleAndDistanceFromBody(player, angle, distance).OutputTo(ship);
            }

            yield return Task.KNMMMJOOBHC.WithTime(5f); // a moment to see the fleet before the enemy arrives
            yield return Task.OKLDNLKLLFJ.WithGroup("State_Group_Mus_Dyn").WithState("Tension");

            // Balance (from the logged hull and shields: with fire spread over thirty ships no allied shield
            // ever broke): allied ships can be destroyed (some Starfleet ships spawn invulnerable, as mission
            // allies) and have weak shields; the enemy fleet has strong ones and hunts in packs (see EnemyTarget),
            // and sends reinforcements when it's down to half.
            foreach (var ally in allies)
            {
                yield return Task.FEBCHOJGMHM.TellAI(ally).ToSetVulnerable();
                yield return Task.OFNIKPPKOGI.TellAI(ally).ToSetShieldStrength(AI.IDCAFFBNKFO.Weak);
            }

            var enemies = new List<BodyRef>();
            foreach (var step in SpawnEnemies(size, empire, enemies, allies)) yield return step;
            foreach (var ally in allies)
                yield return Task.NDFEDBHFGFB.TellAI(ally).ToAddObjectives(enemies[random.Next(enemies.Count)]).OfObjectiveType(AIObjective.Attack);
            int reinforcements = 0;
            foreach (var ship in allies.Concat(enemies)) spawnedAt[ship] = UnityEngine.Time.time;
            // A little battle-worn: the allied fleet starts at 85% hull.
            yield return Task.CIAHOPCODGJ.WithTargets(allies.ToArray()).SetHullHealthPercent(85f);

            // The fight: re-order idle ships at the survivors, report the count as it changes.
            int enemiesLeft = enemies.Count, alliesLeft = allies.Count;
            string status = Status(enemiesLeft, alliesLeft);
            yield return Task.NECMCDDOFJB.AddSubDirectives(directive, status);
            int ticks = 0;
            while (enemiesLeft > 0)
            {
                yield return Task.KNMMMJOOBHC.WithTime(2f);
                var liveEnemies = enemies.Where(Alive).ToList();
                var liveAllies = allies.Where(Alive).ToList();
                if (++ticks % 10 == 0) // every 20 seconds, for the balance
                    Plugin.Log.LogInfo($"Fleet battle: enemies {Condition(liveEnemies)}; allies {Condition(liveAllies)}");
                if (liveEnemies.Count == enemiesLeft && liveAllies.Count == alliesLeft) continue;

                // Ships whose target died go after a survivor (re-ordering everyone is cheap and keeps it simple).
                if (liveEnemies.Count < enemiesLeft && liveEnemies.Count > 0)
                    foreach (var ally in liveAllies)
                        yield return Task.NDFEDBHFGFB.TellAI(ally).ToAddObjectives(liveEnemies[random.Next(liveEnemies.Count)]).OfObjectiveType(AIObjective.Attack);
                if (liveAllies.Count < alliesLeft)
                    for (int i = 0; i < liveEnemies.Count; i++)
                        yield return Task.NDFEDBHFGFB.TellAI(liveEnemies[i]).ToAddObjectives(EnemyTarget(i, liveAllies))
                            .OfObjectiveType(AIObjective.Attack);

                // Reinforcements whenever the enemy is down to half, up to twice: a third as many again each time,
                // warping in ahead (half was too many).
                if (liveEnemies.Count > 0 && reinforcements < 2 && liveEnemies.Count <= size / 2)
                {
                    reinforcements++;
                    string arriving = $"{EnemyNames[empire]} reinforcements arriving";
                    yield return Task.NECMCDDOFJB.AddSubDirectives(directive, arriving);
                    yield return Task.ABHANLLBILL.WithEventID("PLAY_SND_INCOMING_CLOAK_ALARM_LP").ContinueImmediately();
                    yield return Task.ABHANLLBILL.WithEventID("STOP_SND_INCOMING_CLOAK_ALARM_LP").WithDelay(6f);
                    int extra = Math.Max(1, size / 3);
                    Plugin.Log.LogInfo($"Fleet battle: {extra} enemy reinforcements");
                    var more = new List<BodyRef>();
                    foreach (var step in SpawnEnemies(extra, empire, more, liveAllies.Count > 0 ? liveAllies : allies)) yield return step;
                    enemies.AddRange(more);
                    liveEnemies.AddRange(more);
                    foreach (var ship in more) spawnedAt[ship] = UnityEngine.Time.time;
                    yield return Task.KNMMMJOOBHC.WithTime(6f);
                    yield return Task.NECMCDDOFJB.RemoveSubDirective(directive, arriving);
                }

                enemiesLeft = liveEnemies.Count;
                alliesLeft = liveAllies.Count;
                Plugin.Log.LogInfo($"Fleet battle: {enemiesLeft} enemies ({Condition(liveEnemies)}), {alliesLeft} allies ({Condition(liveAllies)}) left");
                yield return Task.NECMCDDOFJB.RemoveSubDirective(directive, status);
                status = Status(enemiesLeft, alliesLeft);
                yield return Task.NECMCDDOFJB.AddSubDirectives(directive, status);
            }

            Plugin.Log.LogInfo($"Fleet battle: won, {alliesLeft} of {allies.Count} allies survived");
            yield return Task.OKLDNLKLLFJ.WithGroup("State_Group_Mus_Dyn").WithState("Explore");
            yield return Task.NECMCDDOFJB.CompleteSubDirective(directive, status);
            yield return Task.HHNFJKHGEPH.WithMissionResult($"Victory: {alliesLeft} of {allies.Count} allied ships survived", true);
            yield return Task.KNMMMJOOBHC.WithTime(10f);
        }

        // Enemy ships at the far side of the field, facing you, with strong shields and their orders; a flagship
        // among them for every six ships.
        private IEnumerable<Step> SpawnEnemies(int count, int empire, List<BodyRef> into, List<BodyRef> allies)
        {
            int first = enemyCount;
            for (int i = 0; i < count; i++)
            {
                var ship = new BodyRef();
                into.Add(ship);
                bool flagship = (first + i) % 6 == 5;
                string prefab = flagship ? EnemyFlagships[empire] : Enemies[empire][random.Next(Enemies[empire].Length)];
                float angle = random.Next(-50, 50), distance = 25000f + random.Next(0, 10000);
                yield return Task.BFMBGDIDKEI.UsingSpawner(MakeSpawner(prefab, EnemyNames[empire], enemyCount++, Tag.Enemy))
                    .AtAngleAndDistanceFromBody(player, angle, distance).FaceOtherBody(player).OutputTo(ship);
            }
            for (int i = 0; i < into.Count; i++)
            {
                yield return Task.OFNIKPPKOGI.TellAI(into[i]).ToSetShieldStrength(AI.IDCAFFBNKFO.Strong);
                yield return Task.NDFEDBHFGFB.TellAI(into[i]).ToAddObjectives(EnemyTarget(first + i, allies)).OfObjectiveType(AIObjective.Attack);
            }
        }

        // Enemies hunt in packs of five, each pack on one allied ship, so shields actually break; one ship in
        // six comes for the player instead.
        private BodyRef EnemyTarget(int index, List<BodyRef> allies) =>
            allies.Count == 0 || index % 6 == 0 ? player : allies[(index / 5) % allies.Count];

        // When each ship appeared: a ship counts as alive for its first seconds, before its body is set.
        private readonly Dictionary<BodyRef, float> spawnedAt = new Dictionary<BodyRef, float>();

        private bool Alive(BodyRef ship) =>
            LastStandWaves.Alive(ship, spawnedAt.TryGetValue(ship, out var t) && UnityEngine.Time.time - t < 10f);

        // "hull 87%, shields 40%": the side's average, for judging the balance from the log.
        private static string Condition(List<BodyRef> ships)
        {
            double hull = 0, hullMax = 0, shield = 0, shieldMax = 0;
            foreach (var ship in ships)
            {
                try
                {
                    var info = ship.AHMPFHDGMKJ?.GetTargetInfo();
                    if (info == null) continue;
                    hull += Read(info, "get_HullHealth");
                    hullMax += Read(info, "get_HullMaxHealth");
                    shield += Read(info, "get_ShieldHealth");
                    shieldMax += Read(info, "get_ShieldMaxHealth");
                }
                catch { /* gone */ }
            }
            return $"hull {(hullMax > 0 ? 100 * hull / hullMax : 0):F0}%, shields {(shieldMax > 0 ? 100 * shield / shieldMax : 0):F0}%";
        }

        private static double Read(object info, string getter) =>
            info.GetType().GetMethod(getter)?.Invoke(info, null) is float f ? f : 0;

        private static string Status(int enemies, int allies) => $"Enemy ships left: {enemies}. Allied ships left: {allies}";

        private Spawner MakeSpawner(string prefab, string side, int number, Tag allegiance) =>
            Objective.MNKGANNGIKK.WithPrefab(prefab).InSystem(system)
                .AssignName($"{side} {prefab.Substring(prefab.LastIndexOf(" - ", StringComparison.Ordinal) + 3)} {number + 1}")
                .AddTags(allegiance, Tag.Ship);
    }
}
