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
    /// <summary>A thing the science console can scan. Id is the body's SectorId (SectorBody.id.value), the same on every
    /// machine (the game's own RPCs name bodies by it; SectorBody.guid is the prefab's, shared by every copy).</summary>
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
        private static EGGMAHHMNCE sector; // ISector: bodies by SectorId

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
            int count = Math.Min(ReadingsPerSite, ScanBoard.MaxTargets - live.Count);
            for (int i = 0; i < count; i++)
            {
                var body = new BodyRef();
                int kind = random.Next(Missions.PatrolObjective.Scannables.Length);
                angle += 360f / count + random.Next(-25, 25);
                float distance = ReadingMin + (float)random.NextDouble() * ReadingSpread;
                if (site != player) distance = Missions.PatrolObjective.OffSurface(site, distance);
                yield return task.BFMBGDIDKEI.UsingSpawner(objective.MNKGANNGIKK.WithPrefab(Missions.PatrolObjective.Scannables[kind]).InSystem(system)
                        .AssignName("Unidentified reading " + ++readings).AddTags(Tag.Spawned))
                    .AtAngleAndDistanceFromBody(site, angle, distance).OutputTo(body);
                bodies.Add(body);
                kinds.Add(kind);
            }
            yield return task.KNMMMJOOBHC.WithTime(1f); // fully in, with their ids
            var ids = new List<string>();
            for (int i = 0; i < bodies.Count; i++)
            {
                var sb = bodies[i].AHMPFHDGMKJ as SectorBody;
                if (Add(sb, system, ScienceKinds.ReadingArea(kinds[i]), 0)) ids.Add(IdOf(sb) + " " + ScienceKinds.ReadingArea(kinds[i]));
            }
            Publish();
            Plugin.Log.LogInfo($"Science console: {ids.Count} of {count} readings, ids {string.Join(", ", ids.ToArray())}");
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
                if (!dead.HAFBKMDMMIO) return; // IsDestroyed: a death, not a scripted despawn
                int team = dead.GetTargetInfo() is TargetInfoComponent info ? (int)info.DGGJOECIFAF : 0;
                if (!Ledger.IsHostileTeam(team) || dead.ALPAOOKFIEE == null) return;
                if (deaths.Count < ScanBoard.MaxTargets) deaths.Enqueue(new Death { Position = dead.ALPAOOKFIEE.Position, Team = team });
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Science console: a ship's death: " + ex.Message); }
        }

        private static bool Add(SectorBody body, Map.SystemRoot system, UpgradeArea? area, int team, string name = null)
        {
            if (body == null || body.ALPAOOKFIEE == null) { Plugin.Log.LogWarning("Science console: a target didn't spawn"); return false; }
            string id = IdOf(body);
            if (sector == null) sector = body.PKGJIOJBMBP;
            if (!ScanBoard.ValidId(id) || live.ContainsKey(id))
            {
                Plugin.Log.LogWarning("Science console: a target's id " + id + " is taken; unspawned");
                Unspawn(body); // not left behind untracked
                return false;
            }
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
            if (Is(body, id)) Unspawn(body); // else already gone (or its id now names another body)
        }

        private static void Unspawn(SectorBody body)
        {
            try
            {
                if (spawnManager == null) spawnManager = UnityEngine.Object.FindObjectOfType<SectorSpawnManager>();
                spawnManager?.UnspawnBody(body, JBFDKPHCLFC.Other); // UnspawnReason
            }
            catch (Exception e) { Plugin.Log.LogWarning("Science console: unspawning a target: " + e.Message); }
        }

        private static bool Gone(SectorBody body) => body == null || body.ALPAOOKFIEE == null;

        private static string IdOf(SectorBody body) => body.CDLFNBGDJIB.value.ToString(System.Globalization.CultureInfo.InvariantCulture); // id (SectorId)

        // The body the sector has registered under this id now, or null (unspawned bodies are taken out of it).
        private static SectorBody Find(string id)
        {
            if (sector == null || !int.TryParse(id, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int v)) return null;
            var body = sector.GetBodyWithID<SectorBody>(new CFOKIEECCHC(v)); // SectorId
            return Gone(body) ? null : body;
        }

        // Still that target: the sector still has this very body under its id (not unspawned, its id not reused).
        private static bool Is(SectorBody body, string id) => !Gone(body) && Find(id) == body;

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
            sector = null;
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

        // Once a second: the board's ids matched to bodies (the host's own, else the sector's by SectorId), and the ship.
        private static void Lookup()
        {
            float now = Time.unscaledTime;
            if (now < nextLookup) return;
            nextLookup = now + Every;
            if (Gone(ship)) ship = UnityEngine.Object.FindObjectOfType<PlayerSpacecraft>();
            if (!Gone(ship)) sector = ship.PKGJIOJBMBP;
            found.Clear();
            foreach (string id in ScienceSession.Board.Ids())
            {
                SectorBody body = live.TryGetValue(id, out var l) ? l.Body : Find(id);
                if (Is(body, id)) found.Add(new KeyValuePair<string, SectorBody>(id, body));
            }
        }
    }
}
