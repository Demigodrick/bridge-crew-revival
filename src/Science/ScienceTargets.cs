using System;
using System.Collections.Generic;
using HarmonyLib;
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
        public double Distance;   // from the ship, metres (InRange)
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
        private const float NoticeFor = 6f; // seconds a notice stays on the objectives panel
        private const int MaxNotices = 8;   // queued, not yet shown

        private sealed class Live { public ScienceTarget Target; public SectorBody Body; public Map.SystemRoot System; }
        private sealed class Shown { public string Text; public float Until; }
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
        private static readonly Queue<string> notices = new Queue<string>(); // ScienceSession.Notice, to put on the panel
        private static readonly List<Shown> shown = new List<Shown>();      // on the panel now

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
        /// or is out of the ship's system, keeps the board's list up to date, and shows the console's notices
        /// (ScienceSession.Notice) on the objectives panel for a few seconds each, as Patrol's other short lines.</summary>
        internal static IEnumerator<Step> DebrisLoop(ObjectiveApi objective, TaskBuilders task, BodyRef player)
        {
            for (;;)
            {
                yield return task.KNMMMJOOBHC.WithTime(Every);
                if (!ScienceSession.IsHost) continue;
                Hook();
                foreach (var step in Notices(task)) yield return step;
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
            ScienceSession.Notice += text => { if (notices.Count < MaxNotices) notices.Enqueue(text); };
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
            sector = body.PKGJIOJBMBP; // the body's own: never one left from an earlier sector
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

        // Notices that have had their time come off the panel; new ones go on (one already there just stays longer).
        private static IEnumerable<Step> Notices(TaskBuilders task)
        {
            float now = Time.unscaledTime;
            List<Shown> over = shown.FindAll(x => now >= x.Until); // taken off first: the list may change while a step runs
            shown.RemoveAll(x => now >= x.Until);
            foreach (Shown x in over) yield return task.NECMCDDOFJB.RemoveSubDirective(Missions.PatrolObjective.Directive, x.Text);
            while (notices.Count > 0)
            {
                string text = notices.Dequeue();
                Shown same = shown.Find(x => x.Text == text);
                if (same != null) { same.Until = now + NoticeFor; continue; }
                shown.Add(new Shown { Text = text, Until = now + NoticeFor });
                yield return task.NECMCDDOFJB.AddSubDirectives(Missions.PatrolObjective.Directive, text);
            }
        }

        /// <summary>The Patrol or the room ended: nothing is a target any more (the bodies go with the mission).</summary>
        internal static void Forget()
        {
            live.Clear();
            liveOrder.Clear();
            deaths.Clear();
            notices.Clear();
            shown.Clear(); // the panel's lines go with the mission
            found.Clear();
            shrunk.Clear();
            newborn.Clear();
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
                    double d = Vector3d.Distance(me, at);
                    if (d > metres) continue;
                    if (live.TryGetValue(kv.Key, out var l))
                        result.Add(new ScienceTarget { Id = l.Target.Id, Name = l.Target.Name, Area = l.Target.Area, Team = l.Target.Team, Position = at, Distance = d });
                    else
                        result.Add(new ScienceTarget
                        {
                            Id = kv.Key, Name = kv.Value.ALPAOOKFIEE.BaseName ?? kv.Key, Position = at, Distance = d,
                            Team = kv.Value.GetTargetInfo() is TargetInfoComponent info ? (int)info.DGGJOECIFAF : 0,
                        });
                }
                result.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Science console: finding targets: " + e.Message); }
            return result;
        }

        /// <summary>
        /// Every machine, about once a second while the console is on: debris props (a space station's wreckage) are far
        /// bigger than the ship that died, so each is shrunk, once, to half the player's ship's size. Only how it looks
        /// here: its id, position and targeting are the game's as before.
        /// </summary>
        internal static void ShrinkDebris()
        {
            if (!ScienceSession.Active) return;
            try
            {
                Lookup();
                if (Gone(ship)) return;
                foreach (var kv in found)
                {
                    var body = kv.Value;
                    if (Gone(body) || !shrunk.Add(body.GetInstanceID())) continue;
                    string name = live.TryGetValue(kv.Key, out var l) ? l.Target.Name : body.ALPAOOKFIEE.BaseName;
                    if (name == null || !name.EndsWith(" debris", StringComparison.Ordinal)) continue;
                    if (!Shrink(body, name, true)) continue;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Science console: shrinking debris: " + e.Message); }
        }

        private static readonly HashSet<int> shrunk = new HashSet<int>();

        // Half the ship's size; false when there's nothing to measure yet (it's left for another go).
        private static bool Shrink(SectorBody body, string name, bool logNoSize)
        {
            float own = Size(ship.gameObject), size = Size(body.gameObject);
            if (own <= 0f || size <= 0f)
            {
                if (logNoSize) Plugin.Log.LogInfo($"Science console: {name}: no size to go by (it {size:0.#}, ship {own:0.#})");
                return false;
            }
            float want = own * 0.5f;
            if (size > want) body.transform.localScale *= want / size;
            Plugin.Log.LogInfo($"Science console: {name} {size:0.#} across, the ship {own:0.#}: {(size > want ? "shrunk to " + want.ToString("0.#") : "left as it is")}");
            return true;
        }

        // ---- debris shrunk on the frame it appears ----
        // The once-a-second pass above showed it at full size for a second or two; every new sector body is noted as
        // the game makes it (SectorBody.Awake, on every machine), and the debris prop among them is shrunk on the first
        // frame it can be measured. The pass above stays as the fallback.
        private const string DebrisObject = "Debris - SpaceStation 03"; // DebrisPrefab's object name ("...(Clone)")
        private const float NewbornFor = 5f;
        private struct Newborn { public SectorBody Body; public float Since; }
        private static readonly List<Newborn> newborn = new List<Newborn>();
        private static bool caughtOne;

        internal static void Patch(Harmony harmony)
        {
            try
            {
                var awake = AccessTools.Method(typeof(SectorBody), "Awake");
                if (awake == null) { Plugin.Log.LogWarning("Science console: SectorBody.Awake not found: debris is shrunk within a second"); return; }
                harmony.Patch(awake, postfix: new HarmonyMethod(typeof(ScienceTargets), nameof(Awake_Postfix)));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Science console: debris hook: " + e.Message); }
        }

        private static void Awake_Postfix(SectorBody __instance)
        {
            try
            {
                if (__instance == null || !ScienceSession.Active) return;
                if (__instance.gameObject.name.IndexOf(DebrisObject, StringComparison.Ordinal) < 0) return;
                newborn.Add(new Newborn { Body = __instance, Since = Time.unscaledTime });
            }
            catch { }
        }

        /// <summary>Every frame: debris made since the last frame, shrunk as soon as it can be measured.</summary>
        internal static void ShrinkNewDebris()
        {
            if (newborn.Count == 0) return;
            if (!ScienceSession.Active) { newborn.Clear(); return; }
            try
            {
                if (Gone(ship)) ship = UnityEngine.Object.FindObjectOfType<PlayerSpacecraft>();
                if (Gone(ship)) return;
                float now = Time.unscaledTime;
                for (int i = newborn.Count - 1; i >= 0; i--)
                {
                    var body = newborn[i].Body;
                    if (body == null || now - newborn[i].Since > NewbornFor) { newborn.RemoveAt(i); continue; }
                    if (shrunk.Contains(body.GetInstanceID())) { newborn.RemoveAt(i); continue; }
                    if (!Shrink(body, "debris " + body.gameObject.name, false)) continue;
                    shrunk.Add(body.GetInstanceID());
                    newborn.RemoveAt(i);
                    if (!caughtOne) { caughtOne = true; Plugin.Log.LogInfo("Science console: debris shrunk on the frame it appeared"); }
                }
            }
            catch (Exception e) { newborn.Clear(); Plugin.Log.LogWarning("Science console: shrinking new debris: " + e.Message); }
        }

        // The biggest extent of what's drawn of it (its renderers, but not particle effects).
        private static float Size(GameObject go)
        {
            bool any = false;
            var b = new Bounds();
            foreach (var r in go.GetComponentsInChildren<Renderer>(false))
            {
                if (r is ParticleSystemRenderer || !r.enabled) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            return any ? Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z)) : 0f;
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
