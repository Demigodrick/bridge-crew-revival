using System;
using System.Collections.Generic;
using Mission.Scripting;
using Mission.Scripting.Framework;
using UnityEngine;
using BodyRef = Mission.Scripting.KOOLIJPNDOH;          // a reference to a sector body (ship), filled by spawn tasks
using MissionSequence = Mission.Scripting.MNFBGALLEAJ;  // base class of mission sequences (KMSequence, Defend_Sequence1, ...)
using MissionObjective = Mission.Scripting.MJHDDKANLEC; // base class of a sequence's objectives
using Spawner = Mission.Scripting.FFPNMKADDPK;          // a spawn definition (prefab, system, name, tags)
using Step = Mission.Scripting.Framework.Tasks.KAPOBAJDOEJ; // a scripting task, yielded from objective coroutines
using StartSystem = BHNEPPPPMKH;                         // starting-system choice
using Tag = BLECEHNPNBH;
using Subsystem = Sector.Spacecraft.BaseSpacecraft.HGKJKJMPFMB;
using AIObjective = AI.CPLIILIDJKG;                      // AI objective kinds (Attack, ...)

namespace StbcRevival.Missions
{
    /// <summary>
    /// Last Stand (Revival Missions): hold your ground against endless, growing waves of hostiles.
    /// Each wave warps in around the ship; destroy them all for a short breather (the ship is repaired and
    /// its torpedoes restocked), then the next wave comes, bigger and tougher (Klingons, then pirates and
    /// Romulans, with a flagship every fifth wave). Warp and impulse travel are locked: the fight is here.
    /// It ends when your ship is destroyed; the score is the number of waves cleared.
    ///
    /// Written in the game's own mission framework: a sequence with one objective whose script is a
    /// coroutine yielding the framework's tasks (spawn, wait, wait-until-destroyed, directives), so the
    /// game handles spawning, AI, networking and the end of the mission as for its own missions.
    /// </summary>
    public sealed class LastStandSequence : MissionSequence
    {
        public override void CreateSequence()
        {
            var waves = Sequence.MakeNode<LastStandWaves>();
            Sequence.OnFail(waves).MissionFails();
            Sequence.OnSucceed(waves).MissionSucceeds();
            Sequence.StartObjective(waves);
        }

        // Any system will do: the fight comes to you.
        public override void StartingSystemSelection(StartSystem selection)
        {
            selection.ForceRandomSystemSelection = true;
            selection.Choice = StartSystem.LPCLBKKEBMK.Random;
        }
    }

    public sealed class LastStandWaves : MissionObjective
    {
        internal const string Title = "LAST STAND";
        private const string Directive = "Last Stand: hold out against the waves";
        private const float SpawnDistance = 30000f;   // metres; about where the game's own test waves arrive
        private const float BreatherSeconds = 25f;

        private static readonly string[] Klingons = { "Sector/Ships/Ship - Bird Of Prey", "Sector/Ships/Ship - D5 Cruiser" };
        private static readonly string[] Raiders = { "Sector/Ships/Ship - Pirate A", "Sector/Ships/Ship - Pirate B", "Sector/Ships/Ship - Bird Of Prey" };
        private static readonly string[] Romulans = { "Sector/Ships/Ship - Romulan Scout", "Sector/Ships/Ship - Romulan Medium" };
        private const string Warbird = "Sector/Ships/Ship - Romulan DDeridex";
        private const string Flagship = "Sector/Ships/Ship - Klingon Flagship";

        private readonly System.Random random = new System.Random();
        private BodyRef player;
        private Map.SystemRoot system;
        private int cleared;

        public override void StartScript()
        {
            player = Objective.FFKKADIEOGP;   // the player's ship
            system = Objective.KALBLMGFNIN;   // the system it starts in
            Objective.AddStep("waves", Task.MFLJBKCDBMC.WithSubtaskCoroutine(Waves));
        }

        private IEnumerator<Step> Waves()
        {
            foreach (var step in MissionAudio.Start(Task)) yield return step;
            yield return Task.OGJONIFINCB.DisableWarping(); // and the helm's warp/impulse travel: see LastStandLocks
            yield return Task.NECMCDDOFJB.WithDescriptionString(Directive);
            yield return Task.HHNFJKHGEPH.WithMissionString(Title);
            yield return Task.KNMMMJOOBHC.WithTime(8f); // a moment to settle in before the first wave
            for (int wave = 1; ; wave++)
            {
                var ships = WaveShips(wave);
                string waveLine = $"Wave {wave}: destroy all {ships.Count} hostiles";
                yield return Task.NECMCDDOFJB.AddSubDirectives(Directive, waveLine);
                yield return Task.OKLDNLKLLFJ.WithGroup("State_Group_Mus_Dyn").WithState("Tension");
                Plugin.Log.LogInfo($"Last Stand: wave {wave}: {string.Join(", ", ships.ToArray())}");

                var spawned = new List<BodyRef>();
                float angle = random.Next(0, 360);
                foreach (var prefab in ships)
                {
                    var ship = new BodyRef();
                    spawned.Add(ship);
                    angle += 360f / ships.Count + random.Next(-15, 15);
                    yield return Task.BFMBGDIDKEI.UsingSpawner(MakeSpawner(prefab, wave))
                        .AtAngleAndDistanceFromBody(player, angle, SpawnDistance + random.Next(-5000, 5000))
                        .FaceOtherBody(player)
                        .OutputTo(ship);
                }

                // Straight at the player: without orders, spawned ships patrol until they notice you.
                foreach (var ship in spawned)
                    yield return Task.NDFEDBHFGFB.TellAI(ship).ToAddObjectives(player).OfObjectiveType(AIObjective.Attack);

                // Wait until every ship is gone or at zero hull. (Checked here rather than with the game's
                // health condition, which never fires once a destroyed ship has been removed.)
                int remaining = spawned.Count;
                float started = UnityEngine.Time.time;
                while (remaining > 0)
                {
                    yield return Task.KNMMMJOOBHC.WithTime(1f);
                    int alive = 0;
                    foreach (var ship in spawned) if (Alive(ship, UnityEngine.Time.time - started < 10f)) alive++;
                    if (alive != remaining) Plugin.Log.LogInfo($"Last Stand: wave {wave}: {alive} of {spawned.Count} left");
                    remaining = alive;
                }

                cleared = wave;
                Plugin.Log.LogInfo($"Last Stand: wave {wave} cleared");
                yield return RepairAndRestock(Task, player, "Last Stand");
                yield return Task.NECMCDDOFJB.CompleteSubDirective(Directive, waveLine);
                yield return Task.HHNFJKHGEPH.WithMissionResult($"Waves survived: {cleared}", false);
                yield return Task.OKLDNLKLLFJ.WithGroup("State_Group_Mus_Dyn").WithState("Explore");
                string breather = $"Wave {wave} cleared. Repair and reroute power: wave {wave + 1} is coming";
                yield return Task.NECMCDDOFJB.AddSubDirectives(Directive, breather);
                yield return Task.KNMMMJOOBHC.WithTime(BreatherSeconds);
                yield return Task.NECMCDDOFJB.RemoveSubDirective(Directive, breather);
                yield return Task.NECMCDDOFJB.RemoveSubDirective(Directive, waveLine);
            }
        }

        // Wave makeup: more ships as waves go on, tougher kinds later, a flagship every fifth wave.
        private List<string> WaveShips(int wave)
        {
            var ships = new List<string>();
            int count = Math.Min(1 + (wave + 1) / 2, 6);
            for (int i = 0; i < count; i++)
            {
                string[] pool = wave <= 2 ? new[] { Klingons[0] }
                    : wave <= 4 ? Klingons
                    : wave <= 7 ? (i % 2 == 0 ? Klingons : Raiders)
                    : (i % 3 == 0 ? Romulans : i % 3 == 1 ? Klingons : Raiders);
                ships.Add(pool[random.Next(pool.Length)]);
            }
            if (wave >= 8 && wave % 3 == 2) ships[ships.Count - 1] = Warbird;
            if (wave % 5 == 0) ships.Add(Flagship);
            return ships;
        }

        // A ship counts as alive while its body exists with hull left. Just after spawning the body may not be
        // set yet; count it as alive then.
        internal static bool Alive(BodyRef ship, bool justSpawned)
        {
            try
            {
                var body = ship.AHMPFHDGMKJ;
                if (body == null || (body is UnityEngine.Object o && o == null)) return justSpawned;
                if (body is Component c && !c.gameObject.activeInHierarchy) return false;
                var info = body.GetTargetInfo();
                return info == null || info.GBNCJMFFIDI > 0f; // hull health
            }
            catch (Exception)
            {
                return false; // a torn-down ship
            }
        }

        // Between waves: hull, shields and every system back to full (the game's ship modifier task), and
        // torpedoes back to the ship's starting load.
        internal static Step RepairAndRestock(Mission.Scripting.Framework.Tasks.FNICGFPIEOL task, BodyRef player, string who)
        {
            var repair = task.CIAHOPCODGJ.WithTargets(player).SetHullHealthPercent(100f).SetShieldHealthPercent(100f);
            foreach (var system in new[]
                     {
                         Subsystem.Engines, Subsystem.Shields, Subsystem.Phasers, Subsystem.Lauchers, Subsystem.Sensors,
                         Subsystem.Warpcore, Subsystem.TransporterRoom, Subsystem.ComputerCore, Subsystem.Sickbay, Subsystem.ScienceLab,
                     })
                repair = repair.SetSubsystemHealthPercent(system, 100f);
            Plugin.Log.LogInfo($"{who}: repaired; torpedoes " + Restock());
            return repair;
        }

        private static readonly System.Reflection.FieldInfo StartingAmmo = HarmonyLib.AccessTools.Field(typeof(LauncherGroup), "_startingAmmo");
        private static readonly System.Reflection.MethodInfo SetRemainingAmmo = HarmonyLib.AccessTools.PropertySetter(typeof(LauncherGroup), "RemainingAmmo");

        // The fore and aft launcher groups back to their starting reserve, as when the ship was built
        // (LauncherGroup.Init). Torpedoes already loaded in the tubes stay loaded.
        private static string Restock()
        {
            try
            {
                var ship = UnityEngine.Object.FindObjectOfType<Sector.Spacecraft.PlayerSpacecraft>();
                if (ship == null) return "unchanged (no player ship)";
                var parts = new List<string>();
                foreach (var side in new[] { "_foreLaunchers", "_aftLaunchers" })
                {
                    var group = HarmonyLib.AccessTools.Field(typeof(Sector.Spacecraft.BaseSpacecraft), side).GetValue(ship) as LauncherGroup;
                    if (group == null) continue;
                    int full = (int)StartingAmmo.GetValue(group);
                    parts.Add($"{group.RemainingAmmo}->{full}");
                    if (group.RemainingAmmo < full) SetRemainingAmmo.Invoke(group, new object[] { full });
                }
                return parts.Count == 0 ? "unchanged (no launchers)" : "restocked " + string.Join(", ", parts.ToArray());
            }
            catch (Exception e)
            {
                return "unchanged (" + e.Message + ")";
            }
        }

        private Spawner MakeSpawner(string prefab, int wave) =>
            Objective.MNKGANNGIKK.WithPrefab(prefab).InSystem(system)
                .AssignName(ShipName(prefab) + " " + wave)
                .AddTags(Tag.Enemy, Tag.Ship);

        private static string ShipName(string prefab) =>
            prefab.Substring(prefab.LastIndexOf(" - ", StringComparison.Ordinal) + 3);

        internal int Cleared => cleared;
    }
}
