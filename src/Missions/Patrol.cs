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
using ShipSystem = Sector.NHGGGDMADKM;                          // a target's subsystems, as mission conditions see them (Life, ...)

namespace StbcRevival.Missions
{
    /// <summary>
    /// Patrol (Revival Missions): a mission that never ends. Starfleet hands out one assignment at a time in
    /// another system of the sector, marked on the warp map; warping there accepts it. Assignments:
    ///   - Hunt: raiders attacking shipping; destroy them.
    ///   - Defend: a disabled freighter under attack; keep it alive through two waves.
    ///   - Rescue: a disabled freighter with survivors; scan it for life signs, beam them aboard.
    ///   - Ambush: offered as a rescue, but Klingons decloak around you as you close in; rescue the survivors
    ///     and beat off the ambush.
    /// Warping out of the assignment's system abandons it (it counts as failed) and a new one is offered.
    /// Between assignments (before accepting the next), flying within 5 km of a space station repairs and
    /// restocks the ship. Raiders grow in number as assignments are completed. Leave through the pause menu.
    /// </summary>
    public sealed class PatrolSequence : MissionSequence
    {
        public override void CreateSequence()
        {
            var patrol = Sequence.MakeNode<PatrolObjective>();
            Sequence.OnFail(patrol).MissionFails();
            Sequence.OnSucceed(patrol).MissionSucceeds();
            Sequence.StartObjective(patrol);
        }

        public override void StartingSystemSelection(StartSystem selection)
        {
            selection.ForceRandomSystemSelection = true;
            selection.Choice = StartSystem.LPCLBKKEBMK.Random;
        }
    }

    public sealed class PatrolObjective : MissionObjective
    {
        internal const string Title = "PATROL";
        private const string Directive = "Patrol: carry out Starfleet's assignments";
        private const double RepairRange = 5000;   // metres from a station
        private const float RepairCooldown = 60f;  // seconds between station repairs
        private const double AmbushRange = 10000;  // metres from the freighter when the ambush is sprung

        private static readonly string[] Raiders =
        {
            "Sector/Ships/Ship - Pirate A", "Sector/Ships/Ship - Pirate B", "Sector/Ships/Ship - Bird Of Prey",
        };
        private static readonly string[] Ambushers =
        {
            "Sector/Ships/Ship - Bird Of Prey", "Sector/Ships/Ship - Bird Of Prey", "Sector/Ships/Ship - D5 Cruiser",
        };
        private static readonly string[] Freighters =
        {
            "Sector/Ships/Ship - Freighter A", "Sector/Ships/Ship - Freighter B", "Sector/Ships/Ship - Freighter C",
        };

        private readonly System.Random random = new System.Random(Guid.NewGuid().GetHashCode());
        private BodyRef player;
        private int completed, failed, spawnCount;
        private bool succeeded, abandoned;
        private Map.SystemRoot jobSystem;
        private float lastRepair = -RepairCooldown;

        public override void StartScript()
        {
            player = Objective.FFKKADIEOGP;
            Objective.AddStep("patrol", Task.MFLJBKCDBMC.WithSubtaskCoroutine(Patrol));
        }

        private IEnumerator<Step> Patrol()
        {
            foreach (var step in MissionAudio.Start(Task)) yield return step;
            yield return Task.NECMCDDOFJB.WithDescriptionString(Directive);
            yield return Task.HHNFJKHGEPH.WithMissionString(Title);
            yield return Task.OKLDNLKLLFJ.WithGroup("State_Group_Mus_Dyn").WithState("Explore");
            yield return Task.KNMMMJOOBHC.WithTime(10f);

            for (;;)
            {
                // The offer: a job in another system, marked on the warp map.
                int job = random.Next(4);
                var here = Objective.KALBLMGFNIN;
                var target = PickSystem(here);
                string name = SystemName(target);
                string offer = $"New assignment in the {name} system: {Offer(job)}. Warp there to accept";
                string repairs = "Until then, repairs and torpedoes at any space station (within 5 km)";
                Plugin.Log.LogInfo($"Patrol: offering {JobName(job)} in {name}");
                yield return Task.NECMCDDOFJB.AddSubDirectives(Directive, offer);
                yield return Task.NECMCDDOFJB.AddSubDirectives(Directive, repairs);
                yield return Task.BCJDEKKIHDD.AddMarker(new[] { target });

                // Until they're there: repairs at stations.
                while (Objective.KALBLMGFNIN != target)
                {
                    yield return Task.KNMMMJOOBHC.WithTime(2f);
                    if (NearStation() is string station && UnityEngine.Time.time - lastRepair > RepairCooldown)
                    {
                        lastRepair = UnityEngine.Time.time;
                        yield return LastStandWaves.RepairAndRestock(Task, player, "Patrol");
                        string docked = $"Repaired and restocked at {station}";
                        yield return Task.NECMCDDOFJB.AddSubDirectives(Directive, docked);
                        yield return Task.KNMMMJOOBHC.WithTime(6f);
                        yield return Task.NECMCDDOFJB.RemoveSubDirective(Directive, docked);
                    }
                }

                // Accepted.
                yield return Task.BCJDEKKIHDD.RemoveAllMarkers();
                yield return Task.NECMCDDOFJB.RemoveSubDirective(Directive, repairs);
                yield return Task.NECMCDDOFJB.RemoveSubDirective(Directive, offer);
                Plugin.Log.LogInfo($"Patrol: {JobName(job)} in {name} accepted");
                succeeded = abandoned = false;
                jobSystem = target;
                IEnumerable<Step> run = job == 0 ? Hunt() : job == 1 ? Defend() : Rescue(job == 3);
                foreach (var step in run) yield return step;
                yield return Task.BCJDEKKIHDD.RemoveAllMarkers();

                if (succeeded) completed++; else failed++;
                Plugin.Log.LogInfo($"Patrol: {JobName(job)} {(succeeded ? "complete" : abandoned ? "abandoned" : "failed")}; {completed} done, {failed} failed");
                yield return Task.HHNFJKHGEPH.WithMissionResult($"Assignments completed: {completed}", false);
                yield return Task.OKLDNLKLLFJ.WithGroup("State_Group_Mus_Dyn").WithState("Explore");
                string result = succeeded ? $"Assignment complete. Well done. Assignments completed: {completed}"
                    : abandoned ? $"Assignment abandoned. Assignments completed: {completed}"
                    : $"Assignment failed. Assignments completed: {completed}";
                yield return Task.NECMCDDOFJB.AddSubDirectives(Directive, result);
                yield return Task.KNMMMJOOBHC.WithTime(20f);
                yield return Task.NECMCDDOFJB.RemoveSubDirective(Directive, result);
            }
        }

        // ---- Assignments ------------------------------------------------------------------------------

        // Raiders near the arrival point, coming for you. Won when they're all gone.
        private IEnumerable<Step> Hunt()
        {
            string line = "Destroy the raiders";
            yield return Task.NECMCDDOFJB.AddSubDirectives(Directive, line);
            yield return Task.OKLDNLKLLFJ.WithGroup("State_Group_Mus_Dyn").WithState("Tension");
            var raiders = new List<BodyRef>();
            foreach (var step in SpawnRaiders(RaiderCount(), raiders, player)) yield return step;
            foreach (var step in UntilGone(raiders, () => false)) yield return step;
            succeeded = !abandoned;
            yield return Task.NECMCDDOFJB.RemoveSubDirective(Directive, line);
        }

        // A disabled freighter; two waves of raiders after it. Lost if it's destroyed.
        private IEnumerable<Step> Defend()
        {
            string line = "Protect the disabled freighter from raiders";
            yield return Task.NECMCDDOFJB.AddSubDirectives(Directive, line);
            var freighter = new BodyRef();
            yield return Task.BFMBGDIDKEI.UsingSpawner(MakeSpawner(Freighters[random.Next(Freighters.Length)], "Freighter", Tag.Friendly))
                .AtAngleAndDistanceFromBody(player, random.Next(0, 360), 4000f).OutputTo(freighter);
            yield return Task.KDNOMKAPDKG.TellAI(freighter).ToDisable();
            yield return Task.BCJDEKKIHDD.AddMarker(new[] { freighter });
            yield return Task.KNMMMJOOBHC.WithTime(10f);
            yield return Task.OKLDNLKLLFJ.WithGroup("State_Group_Mus_Dyn").WithState("Tension");
            float started = UnityEngine.Time.time;
            bool lost = false;
            for (int wave = 0; wave < 2 && !lost && !abandoned; wave++)
            {
                var raiders = new List<BodyRef>();
                foreach (var step in SpawnRaiders(RaiderCount(), raiders, freighter)) yield return step;
                foreach (var step in UntilGone(raiders, () => lost = !LastStandWaves.Alive(freighter, UnityEngine.Time.time - started < 10f))) yield return step;
                if (!lost && !abandoned && wave == 0) yield return Task.KNMMMJOOBHC.WithTime(15f);
            }
            succeeded = !lost && !abandoned;
            yield return Task.NECMCDDOFJB.RemoveSubDirective(Directive, line);
        }

        // A disabled freighter with survivors: scan it for life signs, then beam them aboard (the game's
        // rescue missions do the same: Life at 100 when scanned, then the "People" cargo tracker to 100).
        // Sometimes raiders turn up. Lost if the freighter is destroyed first.
        // As an ambush the freighter is further off, and when you close to within AmbushRange, Klingons
        // decloak around you; the job also needs them beaten off.
        private IEnumerable<Step> Rescue(bool ambush)
        {
            string line = "Scan the disabled freighter for life signs, then beam the survivors aboard";
            yield return Task.NECMCDDOFJB.AddSubDirectives(Directive, line);
            var wreck = new BodyRef();
            yield return Task.BFMBGDIDKEI.UsingSpawner(MakeSpawner(Freighters[random.Next(Freighters.Length)], "Freighter", Tag.Friendly) /* neutral shows as hostile: no beaming */)
                .AtAngleAndDistanceFromBody(player, random.Next(0, 360), ambush ? 20000f : 6000f).OutputTo(wreck);
            yield return Task.OFNIKPPKOGI.TellAI(wreck).ToSetShieldStrength(AI.IDCAFFBNKFO.Weak);
            yield return Task.KDNOMKAPDKG.TellAI(wreck).ToDisable();
            // The survivors: the game's rescue encounters load them as "People" cargo; a plain spawn has none,
            // which leaves no life signs to find and nothing to beam.
            int survivors = 8 + random.Next(0, 13);
            yield return Task.CIAHOPCODGJ.WithTargets(wreck).SetCargo(Sector.CargoTypes.People, survivors);
            Plugin.Log.LogInfo($"Patrol: rescue freighter with {survivors} survivors");
            yield return Task.BCJDEKKIHDD.AddMarker(new[] { wreck });
            yield return Task.PAMPNJNPLCI.OfPOIChild(wreck); // as the game's rescue missions do with their target

            bool rescued = false;
            string steps = "rescue " + (++spawnCount);
            Objective.AddStep(steps,
                Task.JAIMKEDGFEG.WhenTargetSubsystem(wreck, ShipSystem.Life).IsEqualTo(100f),
                Task.HOHEEDLMMPH.Reset().WithCargoType(Sector.CargoTypes.People).WithDescriptionString(Directive, "Beam the survivors aboard"),
                Task.CIAHOPCODGJ.WithTargets(wreck).SetShieldHealthPercent(0f),
                Task.LJAGNEPFGJO.IsEqualTo(100f),
                Task.MFLJBKCDBMC.WithAction(() => rescued = true));
            yield return Task.KKALNAMJCNH.WithName(steps);

            var raiders = new List<BodyRef>();
            if (!ambush && random.Next(2) == 0)
            {
                yield return Task.KNMMMJOOBHC.WithTime(20f);
                yield return Task.OKLDNLKLLFJ.WithGroup("State_Group_Mus_Dyn").WithState("Tension");
                foreach (var step in SpawnRaiders(Math.Max(1, RaiderCount() - 1), raiders, wreck)) yield return step;
            }

            float started = UnityEngine.Time.time;
            bool lost = false, sprung = false;
            string fight = "Ambush! Destroy the Klingon ships";
            for (;;)
            {
                yield return Task.KNMMMJOOBHC.WithTime(2f);
                bool justSpawned = UnityEngine.Time.time - started < 10f;
                lost = !LastStandWaves.Alive(wreck, justSpawned);
                if (lost || Abandoned()) break;
                // Sprung on approach (or at the latest once the survivors are aboard, if beamed from further out).
                if (ambush && !sprung && (Distance(player, wreck) <= AmbushRange || rescued))
                {
                    sprung = true;
                    Plugin.Log.LogInfo("Patrol: ambush sprung");
                    yield return Task.ABHANLLBILL.WithEventID("PLAY_SND_INCOMING_CLOAK_ALARM_LP").ContinueImmediately();
                    yield return Task.ABHANLLBILL.WithEventID("STOP_SND_INCOMING_CLOAK_ALARM_LP").WithDelay(8f);
                    yield return Task.ABHANLLBILL.WithEventID("PLAY_NPC_SHIP_KLING_DECLOAK").ContinueImmediately();
                    yield return Task.OKLDNLKLLFJ.WithGroup("State_Group_Mus_Dyn").WithState("Tension");
                    yield return Task.NECMCDDOFJB.AddSubDirectives(Directive, fight);
                    foreach (var step in SpawnRaiders(RaiderCount() + 1, raiders, player, Ambushers, 5000f, 4000)) yield return step;
                    started = UnityEngine.Time.time; // the new ships count as alive while they appear
                }
                bool raidersLeft = sprung && raiders.Any(r => LastStandWaves.Alive(r, UnityEngine.Time.time - started < 10f));
                if (rescued && (!ambush || (sprung && !raidersLeft))) break;
            }
            yield return Task.CLNFMDFOFKL.WithName(steps);
            Plugin.Log.LogInfo($"Patrol: {(ambush ? "ambush" : "rescue")} {(lost ? "failed, freighter lost" : abandoned ? "abandoned" : "complete")}");
            succeeded = !lost && !abandoned;
            if (sprung) yield return Task.NECMCDDOFJB.RemoveSubDirective(Directive, fight);
            yield return Task.NECMCDDOFJB.RemoveSubDirective(Directive, line);
            // Raiders still about stay in the system: a parting gift.
        }

        // ---- Helpers ----------------------------------------------------------------------------------

        // Another system of the sector, at random (the game's FindSystemDifferentThan always gives the same one).
        private Map.SystemRoot PickSystem(Map.SystemRoot here)
        {
            var others = (Objective.MLHNDAEKLHO ?? new List<Map.SystemRoot>()).Where(s => s != null && s != here).ToList();
            return others.Count > 0 ? others[random.Next(others.Count)] : Objective.FindSystemDifferentThan(new[] { here }) ?? here;
        }

        private int RaiderCount() => Math.Min(2 + completed / 2, 6);

        private IEnumerable<Step> SpawnRaiders(int count, List<BodyRef> into, BodyRef target,
                                               string[] pool = null, float distance = 18000f, int spread = 8000)
        {
            pool = pool ?? Raiders;
            var spawned = new List<BodyRef>();
            float angle = random.Next(0, 360);
            for (int i = 0; i < count; i++)
            {
                var ship = new BodyRef();
                spawned.Add(ship);
                angle += 360f / count + random.Next(-20, 20);
                yield return Task.BFMBGDIDKEI.UsingSpawner(MakeSpawner(pool[random.Next(pool.Length)], "Raider", Tag.Enemy))
                    .AtAngleAndDistanceFromBody(player, angle, distance + random.Next(0, spread)).FaceOtherBody(player).OutputTo(ship);
            }
            foreach (var ship in spawned)
                yield return Task.NDFEDBHFGFB.TellAI(ship).ToAddObjectives(random.Next(3) == 0 ? player : target).OfObjectiveType(AIObjective.Attack);
            into.AddRange(spawned);
        }

        // Left the assignment's system: the assignment is given up.
        private bool Abandoned()
        {
            if (!abandoned && jobSystem != null && Objective.KALBLMGFNIN != jobSystem) abandoned = true;
            return abandoned;
        }

        private static double Distance(BodyRef a, BodyRef b)
        {
            try { return UnityEngine.Vector3d.Distance(a.AHMPFHDGMKJ.ALPAOOKFIEE.Position, b.AHMPFHDGMKJ.ALPAOOKFIEE.Position); }
            catch { return double.MaxValue; }
        }

        // Waits until every ship is gone, or stop() says to give up, or the crew has left the system.
        private IEnumerable<Step> UntilGone(List<BodyRef> ships, Func<bool> stop)
        {
            float started = UnityEngine.Time.time;
            for (;;)
            {
                yield return Task.KNMMMJOOBHC.WithTime(2f);
                if (stop() || Abandoned()) yield break;
                bool justSpawned = UnityEngine.Time.time - started < 10f;
                if (!ships.Any(s => LastStandWaves.Alive(s, justSpawned))) yield break;
            }
        }

        // The name of a space station within repair range in this system, or null.
        private string NearStation()
        {
            try
            {
                var me = player.AHMPFHDGMKJ;
                if (me == null) return null;
                var here = Objective.KALBLMGFNIN;
                var stations = new List<BodyRef>();
                Objective.FindAllBodyWithTags(stations, new[] { Tag.SpaceStation });
                foreach (var station in stations)
                {
                    var body = station?.AHMPFHDGMKJ;
                    var transform = body?.ALPAOOKFIEE;
                    if (transform == null || transform.System != here) continue;
                    if (UnityEngine.Vector3d.Distance(transform.Position, me.ALPAOOKFIEE.Position) <= RepairRange)
                        return string.IsNullOrEmpty(transform.BaseName) ? "the station" : transform.BaseName;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Patrol: station check: " + e.Message);
            }
            return null;
        }

        private static string SystemName(Map.SystemRoot system)
        {
            if (system == null) return "this";
            try { return SVR.Localization.GetLocalizedText(system.SystemName); }
            catch { return system.SystemName; }
        }

        private static string JobName(int job) => job == 0 ? "hunt" : job == 1 ? "defend" : job == 2 ? "rescue" : "ambush";

        // An ambush is offered as a rescue: it's a trap.
        private static string Offer(int job) => job == 0 ? "raiders are attacking shipping"
            : job == 1 ? "a disabled freighter is under attack"
            : "a freighter is adrift with survivors aboard";

        private Spawner MakeSpawner(string prefab, string role, Tag allegiance) =>
            Objective.MNKGANNGIKK.WithPrefab(prefab).InSystem(Objective.KALBLMGFNIN)
                .AssignName($"{role} {++spawnCount}")
                .AddTags(allegiance, Tag.Ship);
    }
}
