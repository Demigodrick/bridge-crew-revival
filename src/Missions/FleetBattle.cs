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
    /// ahead; every ship is ordered to attack one on the other side, so the fight spreads out. Won when the
    /// enemy fleet is destroyed; lost if your ship is.
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

        private static readonly string[] Starfleet = { "Sector/Ships/Ship - Raleigh", "Sector/Ships/Ship - Saladin", "Sector/Ships/Ship - Huxley" };
        // One empire per battle: a mixed fleet might turn on itself.
        private static readonly string[][] Enemies =
        {
            new[] { "Sector/Ships/Ship - Bird Of Prey", "Sector/Ships/Ship - Bird Of Prey", "Sector/Ships/Ship - D5 Cruiser" },
            new[] { "Sector/Ships/Ship - Romulan Scout", "Sector/Ships/Ship - Romulan Scout", "Sector/Ships/Ship - Romulan Medium" },
        };
        private static readonly string[] EnemyFlagships = { "Sector/Ships/Ship - Klingon Flagship", "Sector/Ships/Ship - Romulan DDeridex" };
        private static readonly string[] EnemyNames = { "Klingon", "Romulan" };

        private readonly System.Random random = new System.Random();
        private BodyRef player;
        private Map.SystemRoot system;

        public override void StartScript()
        {
            player = Objective.FFKKADIEOGP;
            system = Objective.KALBLMGFNIN;
            Objective.AddStep("battle", Task.MFLJBKCDBMC.WithSubtaskCoroutine(Battle));
        }

        private IEnumerator<Step> Battle()
        {
            foreach (var step in MissionAudio.Start(Task)) yield return step;
            int size = Math.Max(1, Math.Min(Plugin.FleetBattleSize.Value, 60));
            int empire = random.Next(Enemies.Length);
            string directive = $"Fleet battle: destroy the {EnemyNames[empire]} fleet";
            yield return Task.NECMCDDOFJB.WithDescriptionString(directive);
            yield return Task.HHNFJKHGEPH.WithMissionString(Title);
            yield return Task.OKLDNLKLLFJ.WithGroup("State_Group_Mus_Dyn").WithState("Moderate_Tension");
            Plugin.Log.LogInfo($"Fleet battle: {size} Starfleet ships against {size} {EnemyNames[empire]} ships");

            // Allies: a loose formation around and behind you, all facing the way you do.
            var allies = new List<BodyRef>();
            for (int i = 0; i < size; i++)
            {
                var ship = new BodyRef();
                allies.Add(ship);
                float angle = 180f + random.Next(-70, 70), distance = 3000f + random.Next(0, 6000);
                yield return Task.BFMBGDIDKEI.UsingSpawner(MakeSpawner(Starfleet[random.Next(Starfleet.Length)], "USS", i, Tag.Friendly))
                    .AtAngleAndDistanceFromBody(player, angle, distance).OutputTo(ship);
            }

            yield return Task.KNMMMJOOBHC.WithTime(5f); // a moment to see the fleet before the enemy arrives
            yield return Task.OKLDNLKLLFJ.WithGroup("State_Group_Mus_Dyn").WithState("Tension");

            // Enemies: a wide front ahead, facing you; a flagship among them for every ten ships.
            var enemies = new List<BodyRef>();
            for (int i = 0; i < size; i++)
            {
                var ship = new BodyRef();
                enemies.Add(ship);
                bool flagship = i % 10 == 9;
                string prefab = flagship ? EnemyFlagships[empire] : Enemies[empire][random.Next(Enemies[empire].Length)];
                float angle = random.Next(-50, 50), distance = 25000f + random.Next(0, 10000);
                yield return Task.BFMBGDIDKEI.UsingSpawner(MakeSpawner(prefab, EnemyNames[empire], i, Tag.Enemy))
                    .AtAngleAndDistanceFromBody(player, angle, distance).FaceOtherBody(player).OutputTo(ship);
            }

            // Orders: every ally takes an enemy; every enemy takes an ally, or (one in four) you.
            foreach (var ally in allies)
                yield return Task.NDFEDBHFGFB.TellAI(ally).ToAddObjectives(enemies[random.Next(enemies.Count)]).OfObjectiveType(AIObjective.Attack);
            foreach (var enemy in enemies)
                yield return Task.NDFEDBHFGFB.TellAI(enemy).ToAddObjectives(random.Next(4) == 0 ? player : allies[random.Next(allies.Count)])
                    .OfObjectiveType(AIObjective.Attack);

            // The fight: re-order idle ships at the survivors, report the count as it changes.
            float started = UnityEngine.Time.time;
            int enemiesLeft = enemies.Count, alliesLeft = allies.Count;
            string status = Status(enemiesLeft, alliesLeft);
            yield return Task.NECMCDDOFJB.AddSubDirectives(directive, status);
            while (enemiesLeft > 0)
            {
                yield return Task.KNMMMJOOBHC.WithTime(2f);
                bool justSpawned = UnityEngine.Time.time - started < 10f;
                var liveEnemies = enemies.Where(e => LastStandWaves.Alive(e, justSpawned)).ToList();
                var liveAllies = allies.Where(a => LastStandWaves.Alive(a, justSpawned)).ToList();
                if (liveEnemies.Count == enemiesLeft && liveAllies.Count == alliesLeft) continue;

                // Ships whose target died go after a survivor (re-ordering everyone is cheap and keeps it simple).
                if (liveEnemies.Count < enemiesLeft && liveEnemies.Count > 0)
                    foreach (var ally in liveAllies)
                        yield return Task.NDFEDBHFGFB.TellAI(ally).ToAddObjectives(liveEnemies[random.Next(liveEnemies.Count)]).OfObjectiveType(AIObjective.Attack);
                if (liveAllies.Count < alliesLeft)
                    foreach (var enemy in liveEnemies)
                        yield return Task.NDFEDBHFGFB.TellAI(enemy)
                            .ToAddObjectives(liveAllies.Count == 0 || random.Next(4) == 0 ? player : liveAllies[random.Next(liveAllies.Count)])
                            .OfObjectiveType(AIObjective.Attack);

                enemiesLeft = liveEnemies.Count;
                alliesLeft = liveAllies.Count;
                Plugin.Log.LogInfo($"Fleet battle: {enemiesLeft} enemies, {alliesLeft} allies left");
                yield return Task.NECMCDDOFJB.RemoveSubDirective(directive, status);
                status = Status(enemiesLeft, alliesLeft);
                yield return Task.NECMCDDOFJB.AddSubDirectives(directive, status);
            }

            Plugin.Log.LogInfo($"Fleet battle: won, {alliesLeft} of {allies.Count} allies survived");
            yield return Task.OKLDNLKLLFJ.WithGroup("State_Group_Mus_Dyn").WithState("Explore");
            yield return Task.NECMCDDOFJB.CompleteSubDirective(directive, status);
            yield return Task.HHNFJKHGEPH.WithMissionResult($"Victory: {alliesLeft} of {allies.Count} allied ships survived", true);
            yield return Task.KNMMMJOOBHC.WithTime(10f);
            yield return Objective.Success();
        }

        private static string Status(int enemies, int allies) => $"Enemy ships left: {enemies}. Allied ships left: {allies}";

        private Spawner MakeSpawner(string prefab, string side, int number, Tag allegiance) =>
            Objective.MNKGANNGIKK.WithPrefab(prefab).InSystem(system)
                .AssignName($"{side} {prefab.Substring(prefab.LastIndexOf(" - ", StringComparison.Ordinal) + 3)} {number + 1}")
                .AddTags(allegiance, Tag.Ship);
    }
}
