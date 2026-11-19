using System;
using System.Collections.Generic;
using Sector;
using Sector.Spacecraft;
using UnityEngine;
using BodyRef = Mission.Scripting.KOOLIJPNDOH;                // a reference to a sector body, filled by spawn tasks
using ObjectiveApi = Mission.Scripting.Framework.IHKAJDDPCIB; // MissionObjective.Objective
using Step = Mission.Scripting.Framework.Tasks.KAPOBAJDOEJ;
using Tag = BLECEHNPNBH;
using TaskBuilders = Mission.Scripting.Framework.Tasks.FNICGFPIEOL; // MissionObjective.Task

namespace StbcRevival.Science
{
    /// <summary>A thing the science console can scan. Id is the body's guid, the same on every machine.</summary>
    internal sealed class ScienceTarget
    {
        public string Id;
        public string Name;
        public UpgradeArea? Area; // known on the host only (a crewmate gets null)
        public int Team;
        public Vector3d Position;
    }

    /// <summary>
    /// The things the science console scans in a Patrol (ScienceSession.Active). The host's mission script spawns them
    /// and is the only one to spawn, unspawn or list them (ScienceSession.HostSetTargets); everyone finds them by the
    /// ids on the board.
    ///   - Readings: at each assignment's site, three of Patrol's scannable props (Research's A-D) 6-15 km out, named
    ///     "Unidentified reading N". Their area is the prop's (ScienceKinds.ReadingArea). Unlike Research's objects
    ///     their readings stay known, so Tactical has nothing to scan on them.
    ///   - Debris: where a hostile AI ship died (BodyDiedEvent, sent by AgentShip.Die on every machine; heard on the
    ///     host), named "&lt;Faction&gt; debris", giving weak spots against that faction.
    /// A target goes once it is scanned (the host unspawns it) or when the ship is in another system than the one it
    /// was spawned in (unspawned too, so none are left behind).
    /// </summary>
    internal static class ScienceTargets
    {
        // An Explore prop that reads as wreckage: ExploreScannables spawns this station debris as something to scan,
        // so it is targetable. (The "Debris - Big 0N" props are only seen in the test missions, at debris points.)
        private const string DebrisPrefab = "Sector/System/Debris - SpaceStation 03";
        private const int ReadingsPerSite = 3;
        private const float ReadingMin = 6000f, ReadingSpread = 9000f; // metres out from the site (its surface for a body)
        private const float Every = 1f; // the host's loop and everyone's lookups, seconds

        private sealed class Live { public ScienceTarget Target; public SectorBody Body; public Map.SystemRoot System; }
        private struct Death { public Vector3d Position; public int Team; }

        // Host
        private static readonly Dictionary<string, Live> live = new Dictionary<string, Live>();
        private static readonly List<string> liveOrder = new List<string>();
        private static readonly Queue<Death> deaths = new Queue<Death>();
        private static readonly System.Random random = new System.Random(Guid.NewGuid().GetHashCode());
        private static int readings; // name counter: never reset, so names stay unique
        private static bool hooked;
        private static FFEMIIJDFMK.LCHKEJBPNEF<EEKAKKNBAIH> onDied; // EventManager.EventDelegate<BodyDiedEvent>, kept
        private static SectorSpawnManager spawnManager;

        // Everyone: the board's bodies, looked up once a second
        private static readonly List<KeyValuePair<string, SectorBody>> found = new List<KeyValuePair<string, SectorBody>>();
        private static float nextLookup;
        private static PlayerSpacecraft ship;

        // ------------------------------------------------------------------------------------------- host: readings
        /// <summary>Three readings around the assignment's site (host, console on).</summary>
        internal static IEnumerable<Step> SpawnReadings(ObjectiveApi objective, TaskBuilders task, BodyRef site, BodyRef player)
        {
            if (!ScienceSession.Active || !ScienceSession.IsHost) yield break;
            Hook();
            var system = objective.KALBLMGFNIN;
            DropOutside(system);
            var bodies = new List<BodyRef>();
            var kinds = new List<int>();
            float angle = random.Next(0, 360);
            for (int i = 0; i < ReadingsPerSite; i++)
            {
                var body = new BodyRef();
                int kind = random.Next(Missions.PatrolObjective.Scannables.Length);
                angle += 360f / ReadingsPerSite + random.Next(-25, 25);
                float distance = ReadingMin + (float)random.NextDouble() * ReadingSpread;
                if (site != player) distance = Missions.PatrolObjective.OffSurface(site, distance);
                yield return task.BFMBGDIDKEI.UsingSpawner(objective.MNKGANNGIKK.WithPrefab(Missions.PatrolObjective.Scannables[kind]).InSystem(system)
                        .AssignName("Unidentified reading " + ++readings).AddTags(Tag.Spawned))
                    .AtAngleAndDistanceFromBody(site, angle, distance).OutputTo(body);
                bodies.Add(body);
                kinds.Add(kind);
            }
            yield return task.KNMMMJOOBHC.WithTime(1f); // fully in, with their guids
            for (int i = 0; i < bodies.Count; i++)
                Add(bodies[i].AHMPFHDGMKJ as SectorBody, system, ScienceKinds.ReadingArea(kinds[i]), 0);
            Publish();
        }

        // ------------------------------------------------------------------------------------------- host: the loop
        /// <summary>The whole Patrol, on the host: spawns debris where hostile ships died, unspawns what was scanned
        /// or is out of the ship's system, and keeps the board's list up to date.</summary>
        internal static IEnumerator<Step> DebrisLoop(ObjectiveApi objective, TaskBuilders task, BodyRef player)
        {
            for (;;)
            {
                yield return task.KNMMMJOOBHC.WithTime(Every);
                if (!ScienceSession.IsHost) continue;
                Hook();
                var system = objective.KALBLMGFNIN;
                bool changed = DropOutside(system);
                foreach (string id in liveOrder.ToArray())
                    if (ScienceSession.Board.IsDone(id) || !Is(live[id].Body, id)) { Unspawn(id); changed = true; }
                if (changed) Publish();
                if (!ScienceSession.Active) { deaths.Clear(); continue; }

                while (deaths.Count > 0)
                {
                    Death d = deaths.Dequeue();
                    if (live.Count >= ScanBoard.MaxTargets) continue;
                    var me = (player?.AHMPFHDGMKJ as SectorBody)?.ALPAOOKFIEE;
                    if (me == null) continue;
                    Vector3 f = me.Forward;
                    Vector3d at = me.Position;
                    double dx = d.Position.x - at.x, dz = d.Position.z - at.z;
                    float angle = (float)ScienceKinds.Bearing(f.x, f.z, dx, dz);
                    float distance = (float)Math.Sqrt(dx * dx + dz * dz); // the spawner places on the ship's level
                    var body = new BodyRef();
                    yield return task.BFMBGDIDKEI.UsingSpawner(objective.MNKGANNGIKK.WithPrefab(DebrisPrefab).InSystem(system)
                            .AssignName(ScienceKinds.DebrisName(d.Team)).AddTags(Tag.Spawned)) // the spawner keeps names unique
                        .AtAngleAndDistanceFromBody(player, angle, distance).OutputTo(body);
                    yield return task.KNMMMJOOBHC.WithTime(1f);
                    var sb = body.AHMPFHDGMKJ as SectorBody;
                    if (Add(sb, system, null, d.Team, ScienceKinds.DebrisName(d.Team)))
                    {
                        Publish();
                        Plugin.Log.LogInfo($"Science console: {ScienceKinds.DebrisName(d.Team)} {distance / 1000:0.#} km out");
                    }
                }
            }
        }

        private static void Hook()
        {
            if (hooked) return;
            hooked = true;
            ScienceSession.ResolveTarget = id => live.TryGetValue(id, out var l) ? new TargetInfo { Area = l.Target.Area, Team = l.Target.Team } : null;
            ScienceSession.TargetName = id => live.TryGetValue(id, out var l) ? l.Target.Name : id;
            try
            {
                onDied = OnDied;
                CIFDOFLFGEA.AddListener(onDied); // Events (the game's event bus)
            }
            catch (Exception e) { Plugin.Log.LogWarning("Science console: no debris (the death event): " + e.Message); }
        }

        // Every machine hears a death; the host queues a hostile AI ship's.
        private static void OnDied(EEKAKKNBAIH e)
        {
            try
            {
                if (!ScienceSession.Active || !ScienceSession.IsHost || !(e?.Died is AI.AgentShip dead)) return;
                int team = dead.GetTargetInfo() is TargetInfoComponent info ? (int)info.DGGJOECIFAF : 0;
                if (!Ledger.IsHostileTeam(team) || dead.ALPAOOKFIEE == null) return;
                if (deaths.Count < ScanBoard.MaxTargets) deaths.Enqueue(new Death { Position = dead.ALPAOOKFIEE.Position, Team = team });
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Science console: a ship's death: " + ex.Message); }
        }

        private static bool Add(SectorBody body, Map.SystemRoot system, UpgradeArea? area, int team, string name = null)
        {
            if (body == null || body.ALPAOOKFIEE == null) { Plugin.Log.LogWarning("Science console: a target didn't spawn"); return false; }
            string id = body.FLOBHLBHLLJ.ToString();
            if (!ScanBoard.ValidId(id) || live.ContainsKey(id)) return false;
            live[id] = new Live
            {
                Body = body, System = system,
                Target = new ScienceTarget { Id = id, Name = body.ALPAOOKFIEE.BaseName ?? name ?? id, Area = area, Team = team }, // as crewmates see it
            };
            liveOrder.Add(id);
            return true;
        }

        // Targets in another system than the ship's go (unspawned).
        private static bool DropOutside(Map.SystemRoot system)
        {
            bool changed = false;
            foreach (string id in liveOrder.ToArray())
                if (live[id].System != system) { Unspawn(id); changed = true; }
            return changed;
        }

        private static void Unspawn(string id)
        {
            var body = live[id].Body;
            live.Remove(id);
            liveOrder.Remove(id);
            if (!Is(body, id)) return; // already gone (or its pooled object reused)
            try
            {
                if (spawnManager == null) spawnManager = UnityEngine.Object.FindObjectOfType<SectorSpawnManager>();
                spawnManager?.UnspawnBody(body, JBFDKPHCLFC.Other); // UnspawnReason
            }
            catch (Exception e) { Plugin.Log.LogWarning("Science console: unspawning a target: " + e.Message); }
        }

        private static bool Gone(SectorBody body) => body == null || body.ALPAOOKFIEE == null;

        // Still that target: there, and not a pooled object the spawner has since reused for another body.
        private static bool Is(SectorBody body, string id) => !Gone(body) && body.FLOBHLBHLLJ.ToString() == id;

        private static void Publish() => ScienceSession.HostSetTargets(liveOrder);

        /// <summary>The Patrol or the room ended: nothing is a target any more (the bodies go with the mission).</summary>
        internal static void Forget()
        {
            live.Clear();
            liveOrder.Clear();
            deaths.Clear();
            found.Clear();
            ship = null;
            spawnManager = null;
            nextLookup = 0f;
        }

        // ------------------------------------------------------------------------------------------- everyone
        /// <summary>The board's targets within this many metres of the ship, nearest first; not the ones done.</summary>
        internal static IList<ScienceTarget> InRange(float metres)
        {
            var result = new List<ScienceTarget>();
            if (!ScienceSession.Active) return result;
            try
            {
                Lookup();
                if (Gone(ship)) return result;
                Vector3d me = ship.ALPAOOKFIEE.Position;
                foreach (var kv in found)
                {
                    if (Gone(kv.Value) || ScienceSession.Board.IsDone(kv.Key)) continue;
                    Vector3d at = kv.Value.ALPAOOKFIEE.Position;
                    if (Vector3d.Distance(me, at) > metres) continue;
                    if (live.TryGetValue(kv.Key, out var l))
                        result.Add(new ScienceTarget { Id = l.Target.Id, Name = l.Target.Name, Area = l.Target.Area, Team = l.Target.Team, Position = at });
                    else
                        result.Add(new ScienceTarget
                        {
                            Id = kv.Key, Name = kv.Value.ALPAOOKFIEE.BaseName ?? kv.Key, Position = at,
                            Team = kv.Value.GetTargetInfo() is TargetInfoComponent info ? (int)info.DGGJOECIFAF : 0,
                        });
                }
                result.Sort((a, b) => Vector3d.Distance(me, a.Position).CompareTo(Vector3d.Distance(me, b.Position)));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Science console: finding targets: " + e.Message); }
            return result;
        }

        // Once a second: the board's ids matched to bodies (the host's own, else a search of the scene by guid, only
        // when one isn't known yet), and the ship.
        private static void Lookup()
        {
            float now = Time.unscaledTime;
            if (now < nextLookup) return;
            nextLookup = now + Every;
            if (Gone(ship)) ship = UnityEngine.Object.FindObjectOfType<PlayerSpacecraft>();
            var known = new Dictionary<string, SectorBody>();
            foreach (var kv in found) if (Is(kv.Value, kv.Key)) known[kv.Key] = kv.Value;
            found.Clear();
            SectorBody[] scene = null;
            foreach (string id in ScienceSession.Board.Ids())
            {
                SectorBody body;
                if (live.TryGetValue(id, out var l)) body = l.Body;
                else if (!known.TryGetValue(id, out body))
                {
                    scene = scene ?? UnityEngine.Object.FindObjectsOfType<SectorBody>();
                    body = Array.Find(scene, b => Is(b, id));
                }
                if (Is(body, id)) found.Add(new KeyValuePair<string, SectorBody>(id, body));
            }
        }
    }
}
