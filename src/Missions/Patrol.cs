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
    /// another system of the sector, marked on the warp map; warping there accepts it. The assignment itself
    /// is out at one of that system's planets, moons or stations, away from where you drop out of warp, so
    /// the last leg is by impulse. Assignments:
    ///   - Hunt: raiders attacking shipping; destroy them.
    ///   - Defend: a disabled freighter under attack; keep it alive through two waves.
    ///   - Rescue: a disabled freighter with survivors; scan it for life signs, beam them aboard.
    ///   - Ambush: offered as a rescue, but Klingons decloak around you as you close in; rescue the survivors
    ///     and beat off the ambush.
    ///   - Escort: freighters waiting at the site for an escort fly a route to another body of the system, through
    ///     a waypoint when one lies on the way; delivered when they get there, failed if they're all lost. One
    ///     escort in two, raiders attack the convoy on the way (sometimes Klingons decloaking around it).
    ///   - Research: unidentified objects scattered about the site; scan them all. Now and then raiders
    ///     turn up after the first scan.
    /// One assignment in twenty, a Borg cube warps in partway through and attacks: destroy it, warp out, or
    /// hold out until it warps away.
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
        private const double EngageRange = 20000;  // metres from the site when raiders there come for you
        private const double MinTravel = 30000;    // a site at least this far from the warp arrival, if there is one
        private const int BorgChance = 20;         // one assignment in this many has a Borg cube turn up
        private const float BorgStays = 240f;      // seconds the cube attacks before it warps away
        private static readonly Tag[] SiteTags =
        {
            Tag.ImpulseDestination, Tag.Planet, Tag.Moon, Tag.SpaceStation, Tag.Asteroid, Tag.Anomaly, Tag.Nebula,
        };

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

        /// <summary>Assignments completed in the patrol running now (Patrol never ends by itself: quitting it after
        /// at least one counts as a success, RevivalMissions.EndRequest_Prefix).</summary>
        internal static int Completed;
        private bool succeeded, abandoned;
        private Map.SystemRoot jobSystem;
        private BodyRef site;      // where the assignment is: a body of the system, or the player if none was found
        private string siteName;
        private float lastRepair = -RepairCooldown;

        public override void StartScript()
        {
            Completed = 0;
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
            var systems = Objective.MLHNDAEKLHO ?? new List<Map.SystemRoot>();
            Plugin.Log.LogInfo($"Patrol: starting in {SystemName(Objective.KALBLMGFNIN)}; {systems.Count} systems: "
                               + string.Join(", ", systems.Where(x => x != null).Select(SystemName).ToArray()));

            for (;;)
            {
                // The offer: a job in another system, marked on the warp map. Never the same kind of job twice in a
                // row, nor the system of the last one (where it'd send you straight back to what you just left).
                int job = lastJob < 0 ? random.Next(Jobs) : (lastJob + 1 + random.Next(Jobs - 1)) % Jobs;
                lastJob = job;
                var here = Objective.KALBLMGFNIN;
                var target = PickSystem(here, jobSystem);
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
                yield return Task.KNMMMJOOBHC.WithTime(3f); // out of warp
                PickSite();
                if (random.Next(BorgChance) == 0)
                {
                    var system = target;
                    string borg = "borg " + (++spawnCount);
                    Objective.AddStep(borg, Task.MFLJBKCDBMC.WithSubtaskCoroutine(() => Borg(system)));
                    yield return Task.KKALNAMJCNH.WithName(borg); // a step added while another runs waits to be started
                }
                IEnumerable<Step> run = job == 0 ? Hunt() : job == 1 ? Defend() : job == 4 ? Escort() : job == 5 ? Research() : Rescue(job == 3);
                foreach (var step in run) yield return step;
                yield return Task.BCJDEKKIHDD.RemoveAllMarkers();

                if (succeeded) completed++; else failed++;
                Completed = completed;
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
            string line = $"Destroy the raiders{Near()}";
            yield return Task.NECMCDDOFJB.AddSubDirectives(Directive, line);
            var raiders = new List<BodyRef>();
            foreach (var step in SpawnRaiders(RaiderCount(), raiders, player, around: site, distance: 3000f, spread: 4000, attack: false)) yield return step;
            yield return Task.BCJDEKKIHDD.AddMarker(raiders.ToArray());
            foreach (var step in UntilClose(site)) yield return step;
            if (!abandoned)
            {
                yield return Task.OKLDNLKLLFJ.WithGroup("State_Group_Mus_Dyn").WithState("Tension");
                foreach (var step in Attack(raiders, player)) yield return step;
            }
            foreach (var step in UntilGone(raiders, () => false)) yield return step;
            succeeded = !abandoned;
            yield return Task.NECMCDDOFJB.RemoveSubDirective(Directive, line);
        }

        // The Borg: a cube warps in near you a while into the assignment (alongside it, not in its place) and
        // attacks. Set up as the game's own chance Borg encounters in Ongoing Voyage (BorgPhaseExplore: 4000
        // hull, no regeneration), so it can be beaten. It warps away again after BorgStays; leave the system
        // and it's left behind.
        private IEnumerator<Step> Borg(Map.SystemRoot system)
        {
            float wait = 30 + random.Next(0, 120);
            Plugin.Log.LogInfo($"Patrol: a Borg cube will turn up in {wait:0} s");
            yield return Task.KNMMMJOOBHC.WithTime(wait);
            if (Objective.KALBLMGFNIN != system)
            {
                Plugin.Log.LogInfo("Patrol: no Borg cube after all, the ship has left the system");
                yield break;
            }
            var cube = new BodyRef();
            yield return Task.BFMBGDIDKEI.UsingSpawner(MakeSpawner("Sector/Ships/Ship - Borg Cube", "Borg cube", Tag.Enemy))
                .UsingSpawnMode(AI.POGGHNHDBAL.Warp).ApplyingConfigurators(typeof(BorgVariants.BorgPhaseExplore))
                .AtAngleAndDistanceFromBody(player, random.Next(0, 360), 8000 + random.Next(0, 4000)).FaceOtherBody(player).OutputTo(cube);
            float arrived = UnityEngine.Time.time;
            Plugin.Log.LogInfo($"Patrol: Borg cube warping in ({(cube.AHMPFHDGMKJ as UnityEngine.Object)?.name ?? "not spawned"})");
            string line = "A Borg cube has arrived! Destroy it or warp out";
            yield return Task.ABHANLLBILL.WithEventID("PLAY_NPC_WARP_IN").ContinueImmediately();
            yield return Task.OKLDNLKLLFJ.WithGroup("State_Group_Mus_Dyn").WithState("Tension");
            yield return Task.NECMCDDOFJB.AddSubDirectives(Directive, line);
            yield return Task.NDFEDBHFGFB.TellAI(cube).ToAddObjectives(player).OfObjectiveType(AIObjective.Attack);

            string outcome;
            for (;;)
            {
                yield return Task.KNMMMJOOBHC.WithTime(2f);
                if (Objective.KALBLMGFNIN != system) { outcome = "left behind"; break; }
                if (!LastStandWaves.Alive(cube, UnityEngine.Time.time - arrived < 20f)) { outcome = "destroyed"; break; }
                if (UnityEngine.Time.time - arrived >= BorgStays) { outcome = "warped away"; break; }
            }
            Plugin.Log.LogInfo($"Patrol: Borg cube {outcome}");
            yield return Task.NECMCDDOFJB.RemoveSubDirective(Directive, line);
            if (outcome == "left behind") yield break;
            if (outcome == "warped away") yield return Task.OONDNDHIEDI.TellAI(cube).UsingMode(AI.POGGHNHDBAL.Warp);
            string after = outcome == "destroyed" ? "The Borg cube is destroyed" : "The Borg cube has warped away";
            yield return Task.NECMCDDOFJB.AddSubDirectives(Directive, after);
            yield return Task.KNMMMJOOBHC.WithTime(10f);
            yield return Task.NECMCDDOFJB.RemoveSubDirective(Directive, after);
        }

        // A disabled freighter; two waves of raiders after it. Lost if it's destroyed.
        private IEnumerable<Step> Defend()
        {
            string line = $"Protect the disabled freighter{Near()} from raiders";
            yield return Task.NECMCDDOFJB.AddSubDirectives(Directive, line);
            var freighter = new BodyRef();
            yield return Task.BFMBGDIDKEI.UsingSpawner(MakeSpawner(Freighters[random.Next(Freighters.Length)], "Freighter", Tag.Friendly))
                .AtAngleAndDistanceFromBody(site, random.Next(0, 360), site == player ? 4000f : OffSurface(site, 3000f)).OutputTo(freighter);
            yield return Task.JPLFLPFBCDJ.TellAI(freighter).ToSetTeam(Sector.DGGJOECIFAF.Federation); // the prefab's team shows as an enemy
            yield return Task.KDNOMKAPDKG.TellAI(freighter).ToDisable();
            yield return Task.BCJDEKKIHDD.AddMarker(new[] { freighter });
            yield return Task.KNMMMJOOBHC.WithTime(5f);
            foreach (var step in UntilClose(freighter)) yield return step; // the raiders arrive with you
            yield return Task.OKLDNLKLLFJ.WithGroup("State_Group_Mus_Dyn").WithState("Tension");
            float started = UnityEngine.Time.time;
            bool lost = false;
            for (int wave = 0; wave < 2 && !lost && !abandoned; wave++)
            {
                var raiders = new List<BodyRef>();
                foreach (var step in SpawnRaiders(RaiderCount(), raiders, freighter, around: freighter)) yield return step;
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
            string line = $"Scan the disabled freighter{Near()} for life signs, then beam the survivors aboard";
            yield return Task.NECMCDDOFJB.AddSubDirectives(Directive, line);
            var wreck = new BodyRef();
            yield return Task.BFMBGDIDKEI.UsingSpawner(MakeSpawner(Freighters[random.Next(Freighters.Length)], "Freighter", Tag.Friendly))
                .AtAngleAndDistanceFromBody(site, random.Next(0, 360), site != player ? OffSurface(site, 3000f) : ambush ? 20000f : 6000f).OutputTo(wreck);
            yield return Task.OFNIKPPKOGI.TellAI(wreck).ToSetShieldStrength(AI.IDCAFFBNKFO.Weak);
            yield return Task.JPLFLPFBCDJ.TellAI(wreck).ToSetTeam(Sector.DGGJOECIFAF.Federation); // as the campaign's disabled freighters
            yield return Task.KDNOMKAPDKG.TellAI(wreck).ToDisable();
            // The survivors: the game's rescue encounters load them as "People" cargo; a plain spawn has none,
            // which leaves no life signs to find and nothing to beam. A plain freighter has no cargo bay either, and
            // the game's cargo task does nothing without one, so they're put aboard as Stellar Evacuation does: into a
            // cargo bay (added), shared with the crew's games. Its life signs and cargo are left to be found by a scan.
            int survivors = 8 + random.Next(0, 13);
            yield return Task.KNMMMJOOBHC.WithTime(1f); // the freighter fully in
            StellarEvacuationObjective.PutPeople(wreck, survivors, "Patrol");
            Unscanned(wreck);
            Plugin.Log.LogInfo($"Patrol: rescue freighter with {survivors} survivors (reads back {StellarEvacuationObjective.People(wreck)})");
            yield return Task.BCJDEKKIHDD.AddMarker(new[] { wreck });
            yield return Task.PAMPNJNPLCI.OfPOIChild(wreck); // as the game's rescue missions do with their target

            // Its shields down and the transporter count started from the outset. The transporter itself won't lock
            // on until the freighter's been scanned (the game's own check); see Scanned for a scan that misses them.
            yield return Task.CIAHOPCODGJ.WithTargets(wreck).SetShieldHealthPercent(0f);
            yield return Task.HOHEEDLMMPH.Reset().WithCargoType(Sector.CargoTypes.People).WithDescriptionString(Directive, "Beam the survivors aboard");
            CountBeamsFrom(wreck);
            bool rescued = false, scanned = false, peopleOn = StellarEvacuationObjective.People(wreck) > 0;
            string steps = "rescue " + (++spawnCount);
            Objective.AddStep(steps,
                Task.LJAGNEPFGJO.IsEqualTo(100f),
                Task.MFLJBKCDBMC.WithAction(() => rescued = true));
            yield return Task.KKALNAMJCNH.WithName(steps);
            Objective.AddStep(steps + " scanned",
                Task.JAIMKEDGFEG.WhenTargetSubsystem(wreck, ShipSystem.Life).IsEqualTo(100f),
                Task.MFLJBKCDBMC.WithAction(() => Plugin.Log.LogInfo("Patrol: the freighter's life signs scanned")));
            yield return Task.KKALNAMJCNH.WithName(steps + " scanned");

            var raiders = new List<BodyRef>();
            if (!ambush && random.Next(2) == 0)
            {
                foreach (var step in UntilClose(wreck)) yield return step;
                if (!abandoned)
                {
                    yield return Task.KNMMMJOOBHC.WithTime(20f);
                    yield return Task.OKLDNLKLLFJ.WithGroup("State_Group_Mus_Dyn").WithState("Tension");
                    foreach (var step in SpawnRaiders(Math.Max(1, RaiderCount() - 1), raiders, wreck, around: wreck)) yield return step;
                }
            }

            float started = UnityEngine.Time.time;
            bool lost = false, sprung = false;
            string fight = "Ambush! Destroy the Klingon ships";
            for (;;)
            {
                yield return Task.KNMMMJOOBHC.WithTime(2f);
                bool justSpawned = UnityEngine.Time.time - started < 10f;
                if (Abandoned()) break;
                if (!rescued) yield return Task.CIAHOPCODGJ.WithTargets(wreck).SetShieldHealthPercent(0f); // kept down: a ship's shields recharge // first: out of its system the freighter is gone from view, not lost
                lost = !LastStandWaves.Alive(wreck, justSpawned);
                if (lost) break;
                if (!scanned && (scanned = Scanned(wreck))) Plugin.Log.LogInfo("Patrol: the freighter's been scanned; its survivors can be beamed");
                // Also by the freighter's own count, as Stellar Evacuation does, should the game's count miss a beam.
                if (!rescued && peopleOn && StellarEvacuationObjective.People(wreck) == 0 && StellarEvacuationObjective.People(player) > 0)
                {
                    rescued = true;
                    Plugin.Log.LogInfo($"Patrol: the survivors are aboard ({StellarEvacuationObjective.People(player)} on the ship)");
                }
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
                bool raidersLeft = sprung && Remaining(raiders, UnityEngine.Time.time - started < 10f) > 0;
                if (rescued && (!ambush || (sprung && !raidersLeft))) break;
            }
            yield return Task.CLNFMDFOFKL.WithNames(steps, steps + " scanned");
            Plugin.Log.LogInfo($"Patrol: {(ambush ? "ambush" : "rescue")} {(lost ? "failed, freighter lost" : abandoned ? "abandoned" : "complete")}");
            succeeded = !lost && !abandoned;
            if (sprung) yield return Task.NECMCDDOFJB.RemoveSubDirective(Directive, fight);
            yield return Task.NECMCDDOFJB.RemoveSubDirective(Directive, line);
            // Raiders still about stay in the system: a parting gift.
        }

        // Freighters waiting at the site for an escort, then flying a route (Route) to another body of the system,
        // each ordered on leg by leg with the game's AI move-to task. A freighter that stops getting closer is
        // ordered on again; should none get any closer for GiveUpTime, the run counts as made, so the job can't
        // hang. One escort in EscortRaidChance, raiders go for the convoy on the way: after a while, at the
        // waypoint, or as the first freighter arrives, whichever comes first. One raid in three is Klingons
        // decloaking around it. Delivered if any freighter gets there; raiders still about stay in the system.
        private const int EscortRaidChance = 2;   // one escort in this many is raided
        private const double ArriveRange = 5000;  // metres from a route point's surface that counts as reaching it
        private const double MinLeg = 15000;      // the destination at least this far from the site's surface
        private const float StallTime = 60f;      // seconds a freighter gets no closer before it's ordered on again
        private const float GiveUpTime = 300f;    // seconds no freighter gets any closer before the run counts as made

        private sealed class Freighter
        {
            public BodyRef Ship;
            public int Leg;                       // index into the route of where it's headed
            public bool Arrived, Lost;
            public double Best = double.MaxValue; // closest it's been to that point
            public float Since;                   // when it last got closer
        }

        private IEnumerable<Step> Escort()
        {
            var route = Route();
            if (route == null)
            {
                Plugin.Log.LogInfo("Patrol: nowhere in this system for a convoy to go; a freighter to defend instead");
                foreach (var step in Defend()) yield return step;
                yield break;
            }
            var end = route[route.Count - 1];
            string to = BodyName(end) ?? "the marked destination";
            string line = $"Escort the convoy to {to}; it's waiting{Near()}";
            yield return Task.NECMCDDOFJB.AddSubDirectives(Directive, line);

            int size = 2 + random.Next(2);
            var convoy = new List<Freighter>();
            for (int i = 0; i < size; i++)
            {
                var ship = new BodyRef();
                var spawn = Task.BFMBGDIDKEI.UsingSpawner(MakeSpawner(Freighters[random.Next(Freighters.Length)], "Freighter", Tag.Friendly));
                yield return (i == 0 ? spawn.AtAngleAndDistanceFromBody(site, random.Next(0, 360), OffSurface(site, 3000f))
                    : spawn.AtAngleAndDistanceFromBody(convoy[0].Ship, random.Next(0, 360), 600f + random.Next(0, 400))).OutputTo(ship);
                yield return Task.JPLFLPFBCDJ.TellAI(ship).ToSetTeam(Sector.DGGJOECIFAF.Federation); // the prefab's team shows as an enemy
                convoy.Add(new Freighter { Ship = ship });
            }
            var ships = convoy.Select(c => c.Ship).ToList();
            bool raid = random.Next(EscortRaidChance) == 0;
            double length = Gap(site, route[0]) + Enumerable.Range(1, route.Count - 1).Sum(i => Gap(route[i - 1], route[i]));
            Plugin.Log.LogInfo($"Patrol: escort of {size} freighters from {BodyName(site) ?? "the site"} to {to}"
                               + (route.Count > 1 ? $" by way of {BodyName(route[0]) ?? "a waypoint"}" : "")
                               + $", about {length / 1000:0} km; {(raid ? "raiders will attack" : "no raiders")}");
            yield return Task.BCJDEKKIHDD.AddMarker(ships.Concat(new[] { end }).ToArray());

            // The convoy sets off once its escort has come.
            yield return Task.KNMMMJOOBHC.WithTime(5f);
            foreach (var step in UntilClose(ships[0])) yield return step;
            var raiders = new List<BodyRef>();
            string fight = null;
            if (!abandoned)
            {
                Plugin.Log.LogInfo("Patrol: the escort is here; the convoy sets off");
                float departed = UnityEngine.Time.time, lastProgress = departed, nextLog = departed + StatusEvery, raidAt = 0;
                float raidAfter = 45 + random.Next(0, 106);
                bool sprung = false;
                foreach (var c in convoy)
                {
                    c.Since = departed;
                    yield return Task.LAADENAGNCO.TellAI(c.Ship).ToDestination(route[0]);
                }
                for (;;)
                {
                    yield return Task.KNMMMJOOBHC.WithTime(2f);
                    if (Abandoned()) break; // first: out of its system the convoy is gone from view, not lost
                    float now = UnityEngine.Time.time;
                    var orders = new List<Freighter>();
                    foreach (var c in convoy.Where(c => !c.Arrived && !c.Lost))
                    {
                        if (!LastStandWaves.Alive(c.Ship, false))
                        {
                            c.Lost = true;
                            Plugin.Log.LogInfo($"Patrol: {ShipName(c.Ship)} lost on leg {c.Leg + 1} of {route.Count}");
                            continue;
                        }
                        double d = Gap(c.Ship, route[c.Leg]);
                        if (d <= ArriveRange)
                        {
                            lastProgress = now;
                            if (++c.Leg == route.Count)
                            {
                                c.Arrived = true;
                                Plugin.Log.LogInfo($"Patrol: {ShipName(c.Ship)} has arrived at {to} ({now - departed:0} s)");
                                continue;
                            }
                            Plugin.Log.LogInfo($"Patrol: {ShipName(c.Ship)} at the waypoint; on to {to}");
                            c.Best = double.MaxValue;
                            c.Since = now;
                            orders.Add(c);
                        }
                        else if (d < c.Best - 300)
                        {
                            c.Best = d;
                            c.Since = lastProgress = now;
                        }
                        else if (now - c.Since >= StallTime)
                        {
                            Plugin.Log.LogInfo($"Patrol: {ShipName(c.Ship)} no closer in {StallTime:0} s ({Km(d)} km off); ordered on again");
                            c.Since = now;
                            orders.Add(c);
                        }
                    }
                    foreach (var c in orders) yield return Task.LAADENAGNCO.TellAI(c.Ship).ToDestination(route[c.Leg]);

                    // The raid.
                    var going = convoy.Where(c => !c.Arrived && !c.Lost).ToList();
                    if (raid && !sprung && going.Count > 0
                        && (now - departed >= raidAfter || route.Count > 1 && going.Any(c => c.Leg > 0) || convoy.Any(c => c.Arrived)))
                    {
                        sprung = true;
                        raidAt = now;
                        var near = going[random.Next(going.Count)].Ship;
                        bool klingons = random.Next(3) == 0;
                        Plugin.Log.LogInfo($"Patrol: {(klingons ? "Klingons decloaking" : "raiders closing")} on the convoy at {ShipName(near)}, {now - departed:0} s out");
                        if (klingons)
                        {
                            yield return Task.ABHANLLBILL.WithEventID("PLAY_SND_INCOMING_CLOAK_ALARM_LP").ContinueImmediately();
                            yield return Task.ABHANLLBILL.WithEventID("STOP_SND_INCOMING_CLOAK_ALARM_LP").WithDelay(8f);
                            yield return Task.ABHANLLBILL.WithEventID("PLAY_NPC_SHIP_KLING_DECLOAK").ContinueImmediately();
                        }
                        yield return Task.OKLDNLKLLFJ.WithGroup("State_Group_Mus_Dyn").WithState("Tension");
                        fight = $"{(klingons ? "Klingons" : "Raiders")} are attacking the convoy! Protect the freighters";
                        yield return Task.NECMCDDOFJB.AddSubDirectives(Directive, fight);
                        var spawned = new List<BodyRef>();
                        foreach (var step in klingons ? SpawnRaiders(RaiderCount(), spawned, near, Ambushers, 3000f, 3000, around: near, attack: false)
                                     : SpawnRaiders(RaiderCount(), spawned, near, distance: 15000f, spread: 6000, around: near, attack: false))
                            yield return step;
                        foreach (var step in Attack(spawned, going.Select(c => c.Ship).ToList())) yield return step;
                        raiders.AddRange(spawned);
                    }
                    else if (raid && !sprung && going.Count == 0)
                    {
                        raid = false;
                        Plugin.Log.LogInfo("Patrol: the convoy got there before the raiders caught it");
                    }
                    if (fight != null && Remaining(raiders, now - raidAt < 10f) == 0)
                    {
                        Plugin.Log.LogInfo("Patrol: the raid on the convoy is over");
                        yield return Task.NECMCDDOFJB.RemoveSubDirective(Directive, fight);
                        yield return Task.OKLDNLKLLFJ.WithGroup("State_Group_Mus_Dyn").WithState("Explore");
                        fight = null;
                    }

                    if (going.Count == 0) break;
                    if (now - lastProgress >= GiveUpTime)
                    {
                        Plugin.Log.LogInfo($"Patrol: the convoy has got no closer in {GiveUpTime:0} s; its run counts as made");
                        foreach (var c in going) c.Arrived = true;
                        break;
                    }
                    if (now >= nextLog)
                    {
                        nextLog = now + StatusEvery;
                        Plugin.Log.LogInfo("Patrol: convoy: " + string.Join(", ", going.Select(c =>
                            $"{ShipName(c.Ship)} leg {c.Leg + 1} of {route.Count}, {Km(Gap(c.Ship, route[c.Leg]))} km to go, hull {Hull(c.Ship)}").ToArray()));
                    }
                }
            }
            int arrived = convoy.Count(c => c.Arrived), lost = convoy.Count(c => c.Lost);
            succeeded = !abandoned && arrived > 0;
            Plugin.Log.LogInfo($"Patrol: escort {(abandoned ? "abandoned" : arrived > 0 ? "delivered" : "failed, the convoy lost")}: "
                               + $"{arrived} of {size} freighters arrived, {lost} lost");
            if (fight != null) yield return Task.NECMCDDOFJB.RemoveSubDirective(Directive, fight);
            yield return Task.NECMCDDOFJB.RemoveSubDirective(Directive, line);
        }

        // Research: unidentified objects scattered about the site, to scan. They're the scannable props of the
        // game's Explore mission (Prop - ScannableObjectA-D), set up as it does: their science readings made
        // unknown, then the scan trigger on the whole object at 100. Each scan shows what it found, in the
        // game's own words. A scan the trigger misses counts once everything about the object is known. One
        // survey in ResearchRaidChance, raiders come for the ship after the first scan, as the game's research
        // mission calls in enemy patrols on its science reports; the survey needs only the scans.
        private const int ResearchRaidChance = 4;  // one survey in this many is interrupted by raiders
        private static readonly string[] Scannables =
        {
            "Sector/System/Prop - ScannableObjectA", "Sector/System/Prop - ScannableObjectB",
            "Sector/System/Prop - ScannableObjectC", "Sector/System/Prop - ScannableObjectD",
        };
        private static readonly string[] Discoveries = // Explore's result_Crystal, Fragment, Graviton, Verteron, by prop
        {
            "12862|Crystallized Protomatter discovered", "12863|String Fragment discovered",
            "12864|Graviton Stream discovered", "12861|Verteron Pocket discovered",
        };

        private IEnumerable<Step> Research()
        {
            int count = 3 + random.Next(0, 3);
            string Line(int done) => $"Scan the unidentified objects{Near()} ({done} of {count})";
            string line = Line(0);
            yield return Task.NECMCDDOFJB.AddSubDirectives(Directive, line);

            var objects = new List<BodyRef>();
            var kinds = new List<int>();
            float angle = random.Next(0, 360);
            for (int i = 0; i < count; i++)
            {
                var body = new BodyRef();
                int kind = random.Next(Scannables.Length);
                angle += 360f / count + random.Next(-25, 25);
                float distance = site == player ? 6000f + random.Next(0, 9000) : OffSurface(site, 2000f + random.Next(0, 6000));
                yield return Task.BFMBGDIDKEI.UsingSpawner(Objective.MNKGANNGIKK.WithPrefab(Scannables[kind]).InSystem(Objective.KALBLMGFNIN)
                        .AssignName($"Unidentified object {++spawnCount}").AddTags(Tag.Spawned))
                    .AtAngleAndDistanceFromBody(site, angle, distance).OutputTo(body);
                objects.Add(body);
                kinds.Add(kind);
            }
            yield return Task.KNMMMJOOBHC.WithTime(1f); // the objects fully in
            var done = new bool[count];
            var unknown = new bool[count]; // something about it left to find, so "all known" means scanned
            var steps = new string[count];
            for (int i = 0; i < count; i++)
            {
                unknown[i] = Unknown(objects[i]);
                int n = i;
                steps[i] = "scan " + (++spawnCount);
                Objective.AddStep(steps[i],
                    Task.JAIMKEDGFEG.WhenTargetSubsystem(objects[i], ShipSystem.All).IsEqualTo(100f),
                    Task.MFLJBKCDBMC.WithAction(() => done[n] = true));
                yield return Task.KKALNAMJCNH.WithName(steps[i]);
            }
            Plugin.Log.LogInfo($"Patrol: {count} objects to scan{(site == player ? " around the ship" : $" around {BodyName(site) ?? "the site"}")}: "
                               + string.Join(", ", objects.Select((o, i) => $"{ShipName(o)} ({DisplayName(Discoveries[kinds[i]])}, {Km(Distance(player, o))} km)").ToArray()));
            yield return Task.BCJDEKKIHDD.AddMarker(objects.ToArray());

            bool raid = random.Next(ResearchRaidChance) == 0, sprung = false;
            var raiders = new List<BodyRef>();
            string found = null, fight = null;
            int scanned = 0;
            float raidAt = 0;
            var counted = new bool[count];
            for (;;)
            {
                yield return Task.KNMMMJOOBHC.WithTime(2f);
                if (Abandoned()) break;
                for (int i = 0; i < count; i++)
                {
                    if (counted[i]) continue;
                    if (!done[i] && unknown[i] && Known(objects[i]))
                    {
                        done[i] = true;
                        Plugin.Log.LogInfo($"Patrol: {ShipName(objects[i])} fully known without the scan trigger; counted as scanned");
                    }
                    if (!done[i]) continue;
                    counted[i] = true;
                    scanned++;
                    Plugin.Log.LogInfo($"Patrol: {ShipName(objects[i])} scanned, {scanned} of {count}: {DisplayName(Discoveries[kinds[i]])}");
                    yield return Task.NECMCDDOFJB.RemoveSubDirective(Directive, line);
                    if (found != null) yield return Task.NECMCDDOFJB.RemoveSubDirective(Directive, found);
                    line = Line(scanned);
                    found = DisplayName(Discoveries[kinds[i]]) ?? "Readings recorded";
                    yield return Task.NECMCDDOFJB.AddSubDirectives(Directive, line);
                    yield return Task.NECMCDDOFJB.AddSubDirectives(Directive, found);
                }
                if (raid && !sprung && scanned > 0)
                {
                    sprung = true;
                    raidAt = UnityEngine.Time.time;
                    Plugin.Log.LogInfo("Patrol: the survey has drawn raiders");
                    yield return Task.OKLDNLKLLFJ.WithGroup("State_Group_Mus_Dyn").WithState("Tension");
                    fight = "Raiders are closing on the ship! Drive them off";
                    yield return Task.NECMCDDOFJB.AddSubDirectives(Directive, fight);
                    foreach (var step in SpawnRaiders(Math.Max(1, RaiderCount() - 1), raiders, player, distance: 15000f, spread: 6000)) yield return step;
                }
                if (fight != null && Remaining(raiders, UnityEngine.Time.time - raidAt < 10f) == 0)
                {
                    Plugin.Log.LogInfo("Patrol: the raiders are driven off");
                    yield return Task.NECMCDDOFJB.RemoveSubDirective(Directive, fight);
                    yield return Task.OKLDNLKLLFJ.WithGroup("State_Group_Mus_Dyn").WithState("Explore");
                    fight = null;
                }
                if (scanned == count) break;
            }
            yield return Task.CLNFMDFOFKL.WithNames(steps);
            succeeded = !abandoned;
            Plugin.Log.LogInfo($"Patrol: survey {(abandoned ? "abandoned" : "complete")}, {scanned} of {count} objects scanned");
            if (fight != null) yield return Task.NECMCDDOFJB.RemoveSubDirective(Directive, fight);
            if (found != null) yield return Task.NECMCDDOFJB.RemoveSubDirective(Directive, found);
            yield return Task.NECMCDDOFJB.RemoveSubDirective(Directive, line);
            // Raiders still about stay in the system.
        }

        // An object's science readings made unknown, as Explore does with its scannables, so there's a scan to do.
        // Whether anything about it is then left to find.
        private static bool Unknown(BodyRef body)
        {
            try
            {
                if ((body?.AHMPFHDGMKJ as Sector.SectorBody)?.GetDetectable() is Sector.DIENHALJFIO targetable)
                {
                    targetable.SetSubsystemKnown(ShipSystem.Science, false);
                    if (targetable.GetUnknownSubsystemCount() > 0) return true;
                    Plugin.Log.LogInfo($"Patrol: {ShipName(body)} has nothing unknown to scan for; left to the scan trigger");
                }
                else Plugin.Log.LogInfo($"Patrol: {ShipName(body)} isn't scannable");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Patrol: leaving an object to be scanned: " + e.Message);
            }
            return false;
        }

        private static bool Known(BodyRef body)
        {
            try { return (body?.AHMPFHDGMKJ as Sector.SectorBody)?.GetDetectable() is Sector.DIENHALJFIO targetable && targetable.GetUnknownSubsystemCount() == 0; }
            catch { return false; }
        }

        // The convoy's route from the site: a destination among the nearest few bodies at least MinLeg off,
        // through a waypoint when a body lies on the way (no more than 40% further than flying straight there).
        // Null when there's nowhere to go.
        private List<BodyRef> Route()
        {
            if (site == player) return null;
            try
            {
                var others = SystemBodies().Where(b => b.AHMPFHDGMKJ != site.AHMPFHDGMKJ).ToList();
                var far = others.Where(b => Gap(site, b) - Radius(site) >= MinLeg).OrderBy(b => Distance(site, b)).ToList();
                if (far.Count == 0) return null;
                var end = far[random.Next(Math.Min(3, far.Count))];
                double straight = Distance(site, end);
                var on = others.Where(b => b.AHMPFHDGMKJ != end.AHMPFHDGMKJ
                                           && Gap(site, b) - Radius(site) > 2 * ArriveRange && Gap(b, end) - Radius(b) > 2 * ArriveRange
                                           && Distance(site, b) + Distance(b, end) <= 1.4 * straight).ToList();
                var route = new List<BodyRef>();
                if (on.Count > 0) route.Add(on[random.Next(on.Count)]);
                route.Add(end);
                return route;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Patrol: convoy route: " + e.Message);
                return null;
            }
        }

        // ---- Helpers ----------------------------------------------------------------------------------

        // Another system of the sector, at random (the game's FindSystemDifferentThan always gives the same one).
        private int lastJob = -1;

        private Map.SystemRoot PickSystem(Map.SystemRoot here, Map.SystemRoot last)
        {
            var all = (Objective.MLHNDAEKLHO ?? new List<Map.SystemRoot>()).Where(s => s != null && s != here).ToList();
            var others = all.Where(s => s != last).ToList();
            if (others.Count == 0) others = all;
            return others.Count > 0 ? others[random.Next(others.Count)] : Objective.FindSystemDifferentThan(new[] { here }) ?? here;
        }

        private int RaiderCount() => Math.Min(2 + completed / 2, 6);

        // Raiders spread round a body (the player by default), coming for the target (or, one in three, the
        // player); with attack false they wait (see Attack).
        private IEnumerable<Step> SpawnRaiders(int count, List<BodyRef> into, BodyRef target, string[] pool = null,
                                               float distance = 18000f, int spread = 8000, BodyRef around = null, bool attack = true)
        {
            pool = pool ?? Raiders;
            around = around ?? player;
            var spawned = new List<BodyRef>();
            float angle = random.Next(0, 360);
            for (int i = 0; i < count; i++)
            {
                var ship = new BodyRef();
                spawned.Add(ship);
                angle += 360f / count + random.Next(-20, 20);
                yield return Task.BFMBGDIDKEI.UsingSpawner(MakeSpawner(pool[random.Next(pool.Length)], "Raider", Tag.Enemy))
                    .AtAngleAndDistanceFromBody(around, angle, OffSurface(around, distance + random.Next(0, spread))).FaceOtherBody(around).OutputTo(ship);
            }
            // No disengaging: a Bird of Prey's is to cloak and slip away, and the assignment then waits on a
            // ship no one can find.
            yield return Task.GLACCPHKCDO.TellAI(spawned.ToArray());
            if (attack)
                foreach (var step in Attack(spawned, target)) yield return step;
            into.AddRange(spawned);
        }

        private IEnumerable<Step> Attack(List<BodyRef> ships, BodyRef target)
        {
            foreach (var ship in ships)
                yield return Task.NDFEDBHFGFB.TellAI(ship).ToAddObjectives(random.Next(3) == 0 ? player : target).OfObjectiveType(AIObjective.Attack);
        }

        // Each raider after one of the targets (or, one in three, the player).
        private IEnumerable<Step> Attack(List<BodyRef> ships, List<BodyRef> targets)
        {
            foreach (var ship in ships)
                yield return Task.NDFEDBHFGFB.TellAI(ship).ToAddObjectives(random.Next(3) == 0 ? player : targets[random.Next(targets.Count)]).OfObjectiveType(AIObjective.Attack);
        }

        // Waits until the player is within EngageRange of the body (or has left the system).
        private IEnumerable<Step> UntilClose(BodyRef body)
        {
            if (body == player) yield break;
            double d;
            while (!Abandoned() && (d = Distance(player, body)) > EngageRange && d != double.MaxValue) // MaxValue: unknown, go ahead
                yield return Task.KNMMMJOOBHC.WithTime(2f);
        }

        // A distance from a body's surface, not its centre (3 km from a planet's or asteroid's centre is inside it).
        private static float OffSurface(BodyRef site, float clearance)
        {
            try
            {
                double radius = site?.AHMPFHDGMKJ?.ALPAOOKFIEE?.Radius ?? 0;
                if (radius > 0 && radius < 1e7)
                {
                    if (radius > 1000) Plugin.Log.LogInfo($"Patrol: spawning {clearance / 1000:0.#} km off a body of radius {radius / 1000:0.#} km");
                    return (float)(radius + clearance);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Patrol: site radius: " + e.Message);
            }
            return clearance;
        }

        // The assignment's site: a planet, moon, station (...) of this system, preferably one at least MinTravel
        // from where the ship came out of warp, at random; else the furthest; none at all, the player.
        private void PickSite()
        {
            site = player;
            siteName = null;
            try
            {
                var here = Objective.KALBLMGFNIN;
                var me = player.AHMPFHDGMKJ?.ALPAOOKFIEE;
                var bodies = SystemBodies();
                if (me == null || bodies.Count == 0)
                {
                    Plugin.Log.LogInfo($"Patrol: no planets or stations found in {SystemName(here)}; the assignment is where you are");
                    return;
                }
                var far = bodies.Where(b => Distance(player, b) >= MinTravel).ToList();
                site = far.Count > 0 ? far[random.Next(far.Count)] : bodies.OrderByDescending(b => Distance(player, b)).First();
                siteName = site.AHMPFHDGMKJ.ALPAOOKFIEE.BaseName;
                Plugin.Log.LogInfo($"Patrol: site {(string.IsNullOrEmpty(siteName) ? "(unnamed)" : siteName)}, {Distance(player, site) / 1000:0} km away "
                                   + $"({far.Count} of {bodies.Count} bodies {MinTravel / 1000:0}+ km out)");
            }
            catch (Exception e)
            {
                site = player;
                Plugin.Log.LogWarning("Patrol: site: " + e.Message);
            }
        }

        // The planets, moons, stations (...) of the system the ship is in.
        private List<BodyRef> SystemBodies()
        {
            var here = Objective.KALBLMGFNIN;
            var bodies = new List<BodyRef>();
            foreach (var tag in SiteTags)
            {
                var found = new List<BodyRef>();
                Objective.FindAllBodyWithTags(found, new[] { tag });
                bodies.AddRange(found.Where(b => b?.AHMPFHDGMKJ?.ALPAOOKFIEE is var t && t != null && t.System == here
                                                 && !bodies.Any(o => o.AHMPFHDGMKJ == b.AHMPFHDGMKJ)));
            }
            return bodies;
        }

        private string Near()
        {
            if (site == player) return "";
            string name = siteName;
            try { if (!string.IsNullOrEmpty(name)) name = SVR.Localization.GetLocalizedText(name); } catch { }
            return string.IsNullOrEmpty(name) ? " (marked; set an impulse course)" : $" near {name} (set an impulse course)";
        }

        // The freighter as a ship not yet scanned: its life signs and cargo added as systems to find, and nothing
        // about it known (as the game's own missions do with a ship to investigate; the game's RPC, so on every
        // player's game), so the transporter won't lock on until the crew has scanned it. Only for a body with ship
        // data (GetTargetInfo), as StellarEvacuationObjective.Beamable: the captain's order checks need it.
        private static void Unscanned(BodyRef body)
        {
            try
            {
                var sector = body?.AHMPFHDGMKJ as Sector.SectorBody;
                if (!(sector?.GetDetectable() is Sector.TargetableComponent targetable) || sector.GetTargetInfo() == null)
                {
                    Plugin.Log.LogInfo($"Patrol: {sector?.name ?? "the freighter"} has no ship data; its life signs can't be scanned for");
                    return;
                }
                targetable.AddTargetableSubsystem(ShipSystem.Life);
                targetable.AddTargetableSubsystem(ShipSystem.Cargo);
                targetable.SetSubsystemKnown(ShipSystem.All, false);
                Plugin.Log.LogInfo($"Patrol: {sector.name} to be scanned ({targetable.GetAllSubsystems().Length} systems)");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Patrol: leaving the freighter to be scanned: " + e.Message);
                StellarEvacuationObjective.Beamable(body, "Patrol");
            }
        }

        // Whether the freighter's been scanned: its life signs and cargo found. These freighters' scans haven't
        // always found the two systems added to them, which left nothing to beam from, so a scan that's found
        // everything else counts and makes them known too.
        private static bool Scanned(BodyRef body)
        {
            try
            {
                if (!((body?.AHMPFHDGMKJ as Sector.SectorBody)?.GetDetectable() is Sector.TargetableComponent targetable)) return true;
                if (targetable.IsSubsystemKnown(ShipSystem.Life) && targetable.IsSubsystemKnown(ShipSystem.Cargo)) return true;
                var others = targetable.GetAllSubsystems().Where(x => x != ShipSystem.Life && x != ShipSystem.Cargo).ToList();
                if (others.Count == 0 || !others.All(targetable.IsSubsystemKnown)) return false;
                Plugin.Log.LogInfo("Patrol: the freighter's scan didn't find its life signs; made known with the rest");
                StellarEvacuationObjective.Beamable(body, "Patrol");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Patrol: freighter scan: " + e.Message);
                return true;
            }
        }

        // The game's transporter count only follows ships registered as sources, which it does as they spawn;
        // the survivors are put aboard after that, so the freighter is registered here once they are. Without it
        // beaming them across never counted, and the assignment couldn't be finished.
        private static void CountBeamsFrom(BodyRef body)
        {
            try
            {
                var monitor = ((Mission.MissionManager)Mission.MissionManager.IEFBKOMBFGO).GNMPCGLFMBM;
                monitor.MonitorSectorBody(body.AHMPFHDGMKJ);
                Plugin.Log.LogInfo($"Patrol: transporter count following the freighter ({monitor.Progress():0}% beamed)");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Patrol: counting the survivors beamed across: " + e.Message);
            }
        }

        // Left the assignment's system: the assignment is given up.
        private bool Abandoned()
        {
            if (!abandoned && jobSystem != null && Objective.KALBLMGFNIN != jobSystem) abandoned = true;
            return abandoned;
        }

        // A body's radius, or 0 if it hasn't one that makes sense.
        private static double Radius(BodyRef body)
        {
            try { double r = body?.AHMPFHDGMKJ?.ALPAOOKFIEE?.Radius ?? 0; return r > 0 && r < 1e7 ? r : 0; }
            catch { return 0; }
        }

        // From a ship to a body's surface.
        private static double Gap(BodyRef ship, BodyRef body) => Distance(ship, body) - Radius(body);

        private static string Km(double metres) => metres >= double.MaxValue / 2 ? "?" : $"{metres / 1000:0.#}";

        private static string BodyName(BodyRef body)
        {
            try { return DisplayName(body?.AHMPFHDGMKJ?.ALPAOOKFIEE?.BaseName); }
            catch { return null; }
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
                if (Abandoned() || stop()) yield break; // abandoned first: out of the system, what was fought over is out of view, not lost
                bool justSpawned = UnityEngine.Time.time - started < 10f;
                if (Remaining(ships, justSpawned) == 0) yield break;
            }
        }

        // ---- Raiders' fates ---------------------------------------------------------------------------
        // A raider is gone when destroyed, or when it has stayed cloaked for CloakedGone: a cloaked ship can't be
        // found or targeted (a Bird of Prey still cloaks in a fight), so it has slipped away. Each change is
        // logged, and every StatusEvery what's left: where it is, its hull, whether it's cloaked.
        private const float CloakedGone = 60f, StatusEvery = 60f;
        private readonly Dictionary<BodyRef, string> fates = new Dictionary<BodyRef, string>();
        private readonly Dictionary<BodyRef, float> cloakedSince = new Dictionary<BodyRef, float>();
        private float nextStatus;
        private static readonly System.Reflection.MethodInfo CloakActive = HarmonyLib.AccessTools.Method(typeof(AI.AgentShip), "get_IsCloakActive");

        private int Remaining(List<BodyRef> ships, bool justSpawned)
        {
            float now = UnityEngine.Time.time;
            int left = 0;
            foreach (var ship in ships)
            {
                if (fates.ContainsKey(ship)) continue;
                string name = ShipName(ship);
                if (!LastStandWaves.Alive(ship, justSpawned))
                {
                    fates[ship] = "destroyed";
                    Plugin.Log.LogInfo($"Patrol: {name} destroyed");
                    continue;
                }
                if (Cloaked(ship))
                {
                    if (!cloakedSince.TryGetValue(ship, out float since))
                    {
                        cloakedSince[ship] = since = now;
                        Plugin.Log.LogInfo($"Patrol: {name} cloaked");
                    }
                    if (now - since >= CloakedGone)
                    {
                        fates[ship] = "slipped away";
                        Plugin.Log.LogInfo($"Patrol: {name} stayed cloaked {CloakedGone:0} s; it has slipped away");
                        continue;
                    }
                }
                else if (cloakedSince.Remove(ship)) Plugin.Log.LogInfo($"Patrol: {name} decloaked");
                left++;
            }
            if (left > 0 && now >= nextStatus)
            {
                nextStatus = now + StatusEvery;
                Plugin.Log.LogInfo($"Patrol: {left} raider{(left == 1 ? "" : "s")} left: " + string.Join(", ", ships.Where(x => !fates.ContainsKey(x)).Select(Status).ToArray()));
            }
            return left;
        }

        private static bool Cloaked(BodyRef ship)
        {
            try { return ship.AHMPFHDGMKJ is AI.AgentShip agent && CloakActive?.Invoke(agent, null) is true; }
            catch { return false; }
        }

        private static string ShipName(BodyRef ship) => (ship.AHMPFHDGMKJ as UnityEngine.Object)?.name ?? "a raider";

        private string Status(BodyRef ship)
        {
            double d = Distance(player, ship);
            return $"{ShipName(ship)} ({(d == double.MaxValue ? "?" : $"{d / 1000:0.#}")} km, hull {Hull(ship)}{(Cloaked(ship) ? ", cloaked" : "")})";
        }

        private static string Hull(BodyRef ship)
        {
            try { if (ship.AHMPFHDGMKJ?.GetTargetInfo() is var info && info != null) return $"{info.GBNCJMFFIDI:0}"; }
            catch { }
            return "?";
        }

        // The name of a space station within repair range in this system, or null. A station is what the game tags
        // SpaceStation, or a Satellite named like one (the game files several of its stations, "Space Station"
        // among them, as satellites). Range is from its surface (its radius, for a station up to 5 km across).
        private string NearStation()
        {
            try
            {
                var me = player.AHMPFHDGMKJ;
                if (me == null) return null;
                var here = Objective.KALBLMGFNIN;
                var stations = new List<BodyRef>();
                Objective.FindAllBodyWithTags(stations, new[] { Tag.SpaceStation });
                var satellites = new List<BodyRef>();
                Objective.FindAllBodyWithTags(satellites, new[] { Tag.Satellite });
                stations.AddRange(satellites.Where(s => LooksLikeStation(s?.AHMPFHDGMKJ?.ALPAOOKFIEE?.BaseName)));
                foreach (var station in stations)
                {
                    var body = station?.AHMPFHDGMKJ;
                    var transform = body?.ALPAOOKFIEE;
                    if (transform == null || transform.System != here) continue;
                    double radius = transform.Radius > 0 && transform.Radius < 5000 ? transform.Radius : 0;
                    if (UnityEngine.Vector3d.Distance(transform.Position, me.ALPAOOKFIEE.Position) - radius <= RepairRange)
                        return DisplayName(transform.BaseName) ?? "the station";
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Patrol: station check: " + e.Message);
            }
            LogNearby();
            return null;
        }

        private static bool LooksLikeStation(string name)
        {
            foreach (var text in new[] { name, DisplayName(name) })
                if (!string.IsNullOrEmpty(text) && new[] { "station", "starbase", "outpost", "depot" }.Any(word => text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0))
                    return true;
            return false;
        }

        // A body's name as the game shows it ("5796|Space Station" is a text ID and its English).
        private static string DisplayName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            try { name = SVR.Localization.GetLocalizedText(name); }
            catch { }
            int bar = name.IndexOf('|');
            return bar >= 0 ? name.Substring(bar + 1) : name;
        }

        // What's near the ship when no station is in range: each body within 15 km once, with its distance
        // and every tag the game gave it, so a "station" that doesn't repair shows what it really is.
        private const double NearbyRange = 15000;
        private static readonly Tag[] NearbyTags = ((Tag[])Enum.GetValues(typeof(Tag)))
            .Where(t => { var n = t.ToString(); return !n.StartsWith("Label", StringComparison.Ordinal) && n.Length > 1 && !n.StartsWith("Region", StringComparison.Ordinal) || n == "RegionSpaceStation"; })
            .Where(t => !new[] { "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten", "Alpha", "Beta", "Gamma", "Delta", "Epsilon" }.Contains(t.ToString()))
            .ToArray();
        private readonly HashSet<object> loggedNearby = new HashSet<object>();
        private float nextNearby;

        private void LogNearby()
        {
            if (UnityEngine.Time.time < nextNearby) return;
            nextNearby = UnityEngine.Time.time + 10f;
            try
            {
                var me = player.AHMPFHDGMKJ;
                if (me == null) return;
                var here = Objective.KALBLMGFNIN;
                var tags = new Dictionary<object, List<string>>();
                var bodies = new Dictionary<object, SystemTransform>();
                var found = new List<BodyRef>();
                foreach (var tag in NearbyTags)
                {
                    found.Clear();
                    Objective.FindAllBodyWithTags(found, new[] { tag });
                    foreach (var reference in found)
                    {
                        var body = reference?.AHMPFHDGMKJ;
                        var transform = body?.ALPAOOKFIEE;
                        if (transform == null || body == me || transform.System != here || loggedNearby.Contains(body)) continue;
                        if (UnityEngine.Vector3d.Distance(transform.Position, me.ALPAOOKFIEE.Position) > NearbyRange) continue;
                        if (!tags.TryGetValue(body, out var list)) { tags[body] = list = new List<string>(); bodies[body] = transform; }
                        list.Add(tag.ToString());
                    }
                }
                foreach (var pair in tags)
                {
                    loggedNearby.Add(pair.Key);
                    var transform = bodies[pair.Key];
                    double km = UnityEngine.Vector3d.Distance(transform.Position, me.ALPAOOKFIEE.Position) / 1000;
                    Plugin.Log.LogInfo($"Patrol: near {(string.IsNullOrEmpty(transform.BaseName) ? "(no name)" : transform.BaseName)} ({pair.Key.GetType().Name}), "
                                       + $"{km:0.0} km, tags {string.Join(", ", pair.Value.ToArray())}: "
                                       + (pair.Value.Contains("SpaceStation") || pair.Value.Contains("Satellite") && LooksLikeStation(transform.BaseName)
                                           ? $"a space station, repairs within {RepairRange / 1000:0} km of it" : "not a space station, so no repairs here"));
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Patrol: nearby check: " + e.Message);
            }
        }

        private static string SystemName(Map.SystemRoot system)
        {
            if (system == null) return "this";
            try { return SVR.Localization.GetLocalizedText(system.SystemName); }
            catch { return system.SystemName; }
        }

        private const int Jobs = 6;

        private static string JobName(int job) => job == 0 ? "hunt" : job == 1 ? "defend" : job == 2 ? "rescue" : job == 3 ? "ambush" : job == 4 ? "escort" : "research";

        // An ambush is offered as a rescue: it's a trap.
        private static string Offer(int job) => job == 0 ? "raiders are attacking shipping"
            : job == 1 ? "a disabled freighter is under attack"
            : job == 4 ? "a convoy of freighters needs an escort"
            : job == 5 ? "sensors have picked up unidentified objects to survey"
            : "a freighter is adrift with survivors aboard";

        private Spawner MakeSpawner(string prefab, string role, Tag allegiance) =>
            Objective.MNKGANNGIKK.WithPrefab(prefab).InSystem(Objective.KALBLMGFNIN)
                .AssignName($"{role} {++spawnCount}")
                .AddTags(allegiance, Tag.Ship);
    }
}
