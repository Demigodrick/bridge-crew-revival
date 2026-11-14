using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using BepInEx;
using StbcRevival.Walking;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StbcRevival
{
    /// <summary>
    /// Bridge layouts, made with the bridge designer (tools/bridge-designer): BepInEx/bridges/&lt;scene&gt;.json says which
    /// of a bridge scene's objects are moved, turned or hidden, and this puts that on whenever the scene is loaded
    /// (and again when the file changes, so a layout saved from the designer shows at once). Only for you: crewmates
    /// see their own bridge, with their own layout if they have one.
    ///
    /// A change is for an object by its path in the scene (a piece, or a group: everything under it moves with it);
    /// if more than one object has that path, the one whose middle is nearest the change's pivot. Moves and pivots are
    /// in the scene's space (the game's world: bridges don't move), turns in degrees about up, around the pivot.
    /// A station (where a player sits, NovaStation) moves only as a whole: a change to an object holding exactly one
    /// station and inside none (the designer's station unit: the station with its console holder and chair) moves the
    /// station, so the console the game later puts in its holder, and the player who sits there, go with it. A change
    /// to a part of a station, or to anything holding more than one, is left out. (The designer writes the captain's
    /// station's change itself when the captain's chair, which stands apart from it, is moved.)
    ///
    /// Most bridge pieces are statically batched (drawn as part of one combined mesh), so they can't move: those are
    /// hidden, and a copy of their shape read from the scene's file moves instead (BatchedPieces). Colliders move with
    /// the object. Bridge Walk's floors and walls are read again after a layout goes on, and again whenever which
    /// batched pieces are hidden or copied changes: those are left out of the combined meshes it reads (WalkHoles),
    /// and a copy counts where it is now.
    ///
    /// The background crew (RedShirtStation) are spawned by the game as children of their seat objects, each with a
    /// chair of its own in its model, standing where one of the bridge's chairs is. Where it sits comes from its
    /// animation, so it's only known once it's spawned: then, if its chair is where a moved piece was, its seat
    /// object gets the same move, and the NPC (and any that later takes its place) goes along (Followers).
    ///
    /// Each ship class of ours (CustomShip: the Nova, the Sovereign) has a bridge of its own: a copy of the Aegis's
    /// bridge, which it flies on, with a layout of its own, &lt;scene&gt;-&lt;Class&gt;.json (Bridge_scene_02-Nova.json),
    /// never the Aegis's. With no such file in the
    /// layouts folder, the one that comes with the ship (BepInEx/ships/&lt;Class&gt;/&lt;scene&gt;.json), if there is one.
    ///
    /// Lights go with the objects they're on, go off when hidden, and can be set and added (BridgeLights).
    /// </summary>
    internal static class BridgeLayout
    {
        internal const string CopyName = "RevivalLayoutCopy";

        /// <summary>One change from the file.</summary>
        internal sealed class LayoutPiece
        {
            public string Path;
            public Vector3 Pivot, Move;
            public float Turn;
            public bool Hidden;
            public BridgeLights.Settings Light; // for a light: what's set on it (BridgeLights)
        }

        /// <summary>A layout file's changes (format 1); throws, saying what's wrong, if it isn't one.</summary>
        internal static List<LayoutPiece> ParseLayout(string text) => ParseLayout(text, out _);

        /// <summary>A layout file's changes and added parts (format 1); throws, saying what's wrong, if it isn't one.</summary>
        internal static List<LayoutPiece> ParseLayout(string text, out List<BridgeParts.Spec> added) => ParseLayout(text, out added, out _);

        /// <summary>A layout file's changes, added parts and added lights (format 1); throws, saying what's wrong, if it isn't one.</summary>
        internal static List<LayoutPiece> ParseLayout(string text, out List<BridgeParts.Spec> added, out List<BridgeLights.Added> lights)
        {
            added = new List<BridgeParts.Spec>();
            var root = MiniJson.Parse(text) as Dictionary<string, object>;
            if (root == null) throw new FormatException("not a JSON object");
            if (!root.TryGetValue("format", out var format) || !(format is double f) || f != 1) throw new FormatException($"format {format ?? "missing"}, not 1");
            var list = new List<LayoutPiece>();
            Vector3 VecOf(Dictionary<string, object> o, string name, string what)
            {
                if (!o.TryGetValue(name, out var v) || !(v is List<object> a) || a.Count != 3 || !(a[0] is double) || !(a[1] is double) || !(a[2] is double))
                    throw new FormatException($"{what}: {name} isn't [x, y, z]");
                return new Vector3((float)(double)a[0], (float)(double)a[1], (float)(double)a[2]);
            }
            lights = BridgeLights.ParseAdded(root, VecOf);
            if (root.TryGetValue("added", out var addedList) && addedList is List<object> adds)
            {
                int k = 0;
                foreach (var item in adds)
                {
                    k++;
                    if (!(item is Dictionary<string, object> o)) throw new FormatException($"added part {k} isn't an object");
                    string Str(string name) => o.TryGetValue(name, out var v) ? v as string : null;
                    var spec = new BridgeParts.Spec
                    {
                        Id = Str("id") ?? k.ToString(),
                        Scene = Str("scene"), Path = Str("path"), Model = Str("model"),
                        Level = o.TryGetValue("level", out var lv) && lv is double ld ? (int)ld : -1,
                        Pivot = VecOf(o, "pivot", $"added part {k}"),
                        Move = VecOf(o, "move", $"added part {k}"),
                        Turn = o.TryGetValue("turn", out var t) && t is double td ? (float)td : 0f,
                        Scale = o.TryGetValue("scale", out var sc) && sc is double sd ? (float)sd : 1f,
                    };
                    if (spec.Model == null && (spec.Path == null || spec.Level < 0)) throw new FormatException($"added part {k}: neither a model nor a bridge's piece (path and level)");
                    added.Add(spec);
                }
            }
            if (!root.TryGetValue("pieces", out var pieces) || pieces == null) return list;
            if (!(pieces is List<object> array)) throw new FormatException("pieces isn't a list");
            int n = 0;
            foreach (var item in array)
            {
                n++;
                var o = item as Dictionary<string, object>;
                if (o == null) throw new FormatException($"change {n} isn't an object");
                Vector3 Vec(string name)
                {
                    if (!o.TryGetValue(name, out var v) || !(v is List<object> a) || a.Count != 3 || !(a[0] is double) || !(a[1] is double) || !(a[2] is double))
                        throw new FormatException($"change {n}: {name} isn't [x, y, z]");
                    return new Vector3((float)(double)a[0], (float)(double)a[1], (float)(double)a[2]);
                }
                if (!o.TryGetValue("path", out var path) || !(path is string ps) || ps.Length == 0) throw new FormatException($"change {n} has no path");
                list.Add(new LayoutPiece
                {
                    Path = ps,
                    Pivot = Vec("pivot"),
                    Move = Vec("move"),
                    Turn = o.TryGetValue("turn", out var t) && t is double td ? (float)td : 0f,
                    Hidden = o.TryGetValue("hidden", out var h) && h is bool hb && hb,
                    Light = BridgeLights.Settings.Parse(o.TryGetValue("light", out var lt) ? lt : null, $"change {n}"),
                });
            }
            return list;
        }

        private sealed class Change
        {
            public string Path;
            public Vector3 Pivot, Move;
            public float Turn;
            public bool Hidden;
            public BridgeLights.Settings Light;

            public Transform T;
            public Vector3 BasePos;
            public Quaternion BaseRot;
            public readonly List<Renderer> Renderers = new List<Renderer>(); // on at the start
            public readonly List<Renderer> Batched = new List<Renderer>();
            public readonly List<Vector3> BatchedAt = new List<Vector3>(); // the middle of each, where the bridge drew it
            public readonly List<Collider> Colliders = new List<Collider>();
            public readonly List<BridgeLights.Saved> Lights = new List<BridgeLights.Saved>(); // on at the start, or its own
            public GameObject Copy;
            public readonly List<Mesh> CopyMeshes = new List<Mesh>();
            public string CopyProblem;
            public Bounds BaseBounds;   // what's drawn, where the bridge had it
            public bool HasBounds;
            public readonly List<Bounds> Chairs = new List<Bounds>(); // its chairs (not a station's), where the bridge had them

            public bool Moved => Move.sqrMagnitude > 1e-8f || Mathf.Abs(Turn) > 1e-3f;
        }

        private static readonly List<Change> changes = new List<Change>();
        private static Scene scene;
        private static int boundRoot;          // the scene's first root object: changes when it's loaded again
        private static string boundKey;   // which version of its layout is on (the file's time, or the host's version)
        private static float nextLook;
        private static bool lookedOnce;

        private static string Folder => Path.Combine(Paths.BepInExRootPath, "bridges");

        // The game's bridge a class of ours flies on (CustomShip): the Aegis's.
        private static readonly string[] ShipBridges = { "Bridge_scene_02" };

        /// <summary>
        /// The name a loaded scene's layout goes by: on a bridge flown as a class of ours, that class's own copy of it
        /// (Bridge_scene_02-Nova); otherwise the scene's own name.
        /// </summary>
        internal static string LayoutName(string sceneName)
        {
            var ship = CustomShip.FlownNow;
            if (ship == null || Array.IndexOf(ShipBridges, sceneName) < 0 || CustomShip.Campaign) return sceneName;
            return sceneName + "-" + ship.Name;
        }

        /// <summary>A layout's file on this PC (yours in the layouts folder, else for a class's bridge the one its ship comes with), or null.</summary>
        private static string OwnFile(string name)
        {
            string folder = Path.GetFullPath(Folder);
            string file = Path.GetFullPath(Path.Combine(folder, name + ".json"));
            // Only a file right in the layouts folder (the name may come from a crewmate).
            if (!string.Equals(Path.GetDirectoryName(file), folder.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return null;
            if (File.Exists(file)) return file;
            int dash = name.LastIndexOf('-');
            if (dash <= 0) return null;
            var ship = CustomShip.Find(name.Substring(dash + 1));
            string scene = name.Substring(0, dash);
            if (ship == null || Array.IndexOf(ShipBridges, scene) < 0) return null;
            string shipped = Path.Combine(ship.Folder, scene + ".json");
            return File.Exists(shipped) ? shipped : null;
        }

        // ------------------------------------------------------------------------------------------- every frame
        internal static void Update()
        {
            if (Plugin.BridgeLayouts == null || !Plugin.BridgeLayouts.Value) return;
            BridgeShare.Update();
            if (boundRoot != 0)
            {
                BridgeParts.Update();
                // Copies of batched pieces wait for the scene's file to be read.
                foreach (var c in changes) if (c.T != null && c.Moved && c.Batched.Count > 0 && c.Copy == null && c.CopyProblem == null) Refresh(c);
                // Lights it switches off stay off (the game's scripts switch some on and off).
                foreach (var c in changes)
                    if (c.Hidden) foreach (var l in c.Lights) if (l.Light != null && l.Light.enabled) l.Light.enabled = false;
                WalkFollow();
            }
            if (Time.unscaledTime < nextLook) return;
            nextLook = Time.unscaledTime + 1f;
            if (!lookedOnce) { lookedOnce = true; Plugin.Log.LogInfo($"Bridge layout: layouts are read from {Folder}{(Directory.Exists(Folder) ? "" : " (not there yet)")}"); }

            if (boundRoot != 0)
            {
                // Still loaded, the same load of it, and the same layout (the file unchanged, or the host's the same):
                // nothing to do.
                int root = scene.IsValid() && scene.isLoaded ? RootId(scene) : 0;
                if (root != boundRoot) { Forget(); }
                else
                {
                    var now = SourceFor(LayoutName(scene.name));
                    if (now.Key == boundKey) { Followers(); return; }
                    if (now.Wait) return;
                    Plugin.Log.LogInfo($"Bridge layout: {scene.name}'s layout changed ({now.Label}): put on again");
                    foreach (var c in changes) Reset(c);
                    ResetFollowers();
                    BridgeParts.Clear();
                    Forget();
                    // Gone altogether (the file deleted, or the host has none): the bridge stays as it is.
                    if (now.Text == null) { boundRoot = RootId(scene); boundKey = now.Key; return; }
                }
            }
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (!s.isLoaded) continue;
                string name = LayoutName(s.name);
                var source = SourceFor(name);
                if (source.Wait) return;           // asking the host for its layout: a moment
                if (source.Text == null) continue;
                int root = RootId(s);
                if (root == 0) continue;
                scene = s;
                boundRoot = root;
                boundKey = source.Key;
                Load(source.Text, source.Label);
                if (source.Shared == false && BridgeShare.IsHost) BridgeShare.Push(name, source.Text);
                return;
            }
        }

        private struct Source
        {
            public string Text, Key, Label;
            public bool Wait;
            public bool? Shared;  // true: the host's; false: your own file; null: none
        }

        /// <summary>
        /// Where a scene's layout comes from: in a crew whose host has the mod, the host's (none if the host has none);
        /// otherwise (hosting, alone, or a host without the mod) your own file.
        /// </summary>
        private static Source SourceFor(string name)
        {
            // The name's in the key: a bridge that becomes a class's (or stops being one) takes that one's layout.
            switch (BridgeShare.HostLayout(name, out var text, out int version))
            {
                case BridgeShare.State.Waiting: return new Source { Wait = true, Key = "waiting" };
                case BridgeShare.State.Has: return new Source { Text = text, Key = name + ":host:" + version, Label = "the host's", Shared = true };
                case BridgeShare.State.None: return new Source { Key = name + ":host:none:" + version, Label = "the host has none" };
            }
            try
            {
                string file = OwnFile(name);
                if (file == null) return new Source { Key = name + ":none", Label = "no file" };
                var write = File.GetLastWriteTimeUtc(file);
                return new Source { Text = File.ReadAllText(file), Key = name + ":file:" + file + ":" + write.Ticks, Label = Path.GetFileName(file), Shared = false };
            }
            catch (Exception e) { return new Source { Key = name + ":unreadable", Label = e.Message }; }
        }

        /// <summary>This scene's layout as your own file has it (for the host to share), or null.</summary>
        internal static string OwnLayout(string sceneName)
        {
            // Asked for by a crewmate: a plain name, and only a file in the layouts folder (or a class's bridge's own).
            if (!BridgeShare.SceneName(sceneName)) return null;
            try
            {
                string file = OwnFile(sceneName);
                return file != null ? File.ReadAllText(file) : null;
            }
            catch { return null; }
        }

        private static int RootId(Scene s)
        {
            var roots = s.GetRootGameObjects();
            return roots.Length > 0 ? roots[0].GetInstanceID() : 0;
        }

        // ------------------------------------------------------------------------------------------- the file
        private static void Load(string text, string label)
        {
            List<LayoutPiece> layout;
            List<BridgeParts.Spec> added;
            List<BridgeLights.Added> lights;
            try { layout = ParseLayout(text, out added, out lights); }
            catch (Exception e) { Plugin.Log.LogWarning($"Bridge layout: {label} for {scene.name} isn't a layout this version can read: {e.Message}"); return; }
            Plugin.Log.LogInfo($"Bridge layout: {label} for {scene.name}: {layout.Count} change(s), {added.Count} added part(s), {lights.Count} added light(s)");
            BridgeParts.Start(added, scene);
            BridgeLights.Start(lights, scene);
            if (layout.Count == 0) return;
            var byPath = Index();
            int on = 0, missing = 0, refused = 0;
            foreach (var p in layout)
            {
                var c = new Change { Path = p.Path, Pivot = p.Pivot, Move = p.Move, Turn = p.Turn, Hidden = p.Hidden, Light = p.Light };
                var t = Resolve(byPath, c.Path, c.Pivot);
                if (t == null) { missing++; Plugin.Log.LogInfo($"Bridge layout: {c.Path} isn't in this bridge"); continue; }
                string why = StationProblem(t);
                if (why != null) { refused++; Plugin.Log.LogInfo($"Bridge layout: {c.Path} {why}: left out"); continue; }
                if (c.Hidden && t.GetComponentInChildren<NovaStation>(true) != null) { c.Hidden = false; Plugin.Log.LogInfo($"Bridge layout: {c.Path} is a station: it can move, not hide"); }
                if (changes.Exists(o => o.T != null && (t.IsChildOf(o.T) || o.T.IsChildOf(t)))) { refused++; Plugin.Log.LogInfo($"Bridge layout: {c.Path} overlaps another change: left out"); continue; }
                Bind(c, t);
                changes.Add(c);
                Refresh(c);
                on++;
            }
            on += ChairStations(byPath);
            Plugin.Log.LogInfo($"Bridge layout: {scene.name}: {on} change(s) put on"
                               + (missing > 0 ? $", {missing} not found" : "") + (refused > 0 ? $", {refused} left out" : ""));
            try { WalkMesh.Clear(); } catch { }
        }

        /// <summary>
        /// A moved chair standing at a station's seat apart from the station (the captain's) takes the station with it,
        /// moved the same: its console and its player go there too. The designer writes the station's change itself;
        /// this is for a layout without it (one saved by an older designer).
        /// </summary>
        private static int ChairStations(Dictionary<string, List<Transform>> byPath)
        {
            int linked = 0;
            foreach (var station in UnityEngine.Object.FindObjectsOfType<NovaStation>())
            {
                if (station == null || station.gameObject.scene != scene) continue;
                // Its unit: the highest object holding it and no other station, below the scene's root.
                var unit = station.transform;
                while (unit.parent != null && unit.parent.parent != null && unit.parent.GetComponentsInChildren<NovaStation>(true).Length == 1) unit = unit.parent;
                if (changes.Exists(o => o.T != null && (o.T == unit || unit.IsChildOf(o.T) || o.T.IsChildOf(unit)))) continue;
                Vector3 seat = station.transform.position;
                Change by = null;
                foreach (var c in changes)
                {
                    if (!c.Moved || c.T == null) continue;
                    foreach (var b in c.Chairs)
                    {
                        float dx = Mathf.Max(0f, Mathf.Abs(seat.x - b.center.x) - b.extents.x), dz = Mathf.Max(0f, Mathf.Abs(seat.z - b.center.z) - b.extents.z);
                        if (Mathf.Sqrt(dx * dx + dz * dz) < 0.3f && b.min.y < seat.y + 0.5f && b.max.y > seat.y - 1.2f) { by = c; break; }
                    }
                    if (by != null) break;
                }
                if (by == null) continue;
                var s = new Change { Path = Walking.Bridge.Path(unit), Pivot = by.Pivot, Move = by.Move, Turn = by.Turn };
                Bind(s, unit);
                changes.Add(s);
                Refresh(s);
                linked++;
                Plugin.Log.LogInfo($"Bridge layout: {s.Path} (a station) goes with {by.Path}, the chair at its seat");
            }
            return linked;
        }

        // Every object of the scene by its path (our own copies left out).
        private static Dictionary<string, List<Transform>> Index()
        {
            var map = new Dictionary<string, List<Transform>>();
            void Go(Transform t)
            {
                if (t.name == CopyName) return;
                string p = Walking.Bridge.Path(t);
                if (!map.TryGetValue(p, out var l)) map[p] = l = new List<Transform>();
                l.Add(t);
                foreach (Transform c in t) Go(c);
            }
            foreach (var root in scene.GetRootGameObjects()) Go(root.transform);
            return map;
        }

        private static Transform Resolve(Dictionary<string, List<Transform>> byPath, string path, Vector3 pivot)
        {
            if (!byPath.TryGetValue(path, out var list)) return null;
            Transform best = null;
            float bestD = float.MaxValue;
            foreach (var t in list)
            {
                float d = (Middle(t) - pivot).sqrMagnitude;
                if (d < bestD) { bestD = d; best = t; }
            }
            return best;
        }

        // The middle of what's drawn under it (as the designer works it out), or where it is.
        private static Vector3 Middle(Transform t)
        {
            Bounds? all = null;
            foreach (var r in t.GetComponentsInChildren<MeshRenderer>(false))
            {
                if (!r.enabled) continue;
                if (all == null) all = r.bounds; else { var b = all.Value; b.Encapsulate(r.bounds); all = b; }
            }
            return all?.center ?? t.position;
        }

        // Why this object can't be changed as far as stations go, or null: a part of a station or console, or holding
        // more than one station, or a console that isn't in a station.
        private static string StationProblem(Transform t)
        {
            for (var a = t.parent; a != null; a = a.parent)
                if (a.GetComponent<NovaStation>() != null || a.GetComponent<ConsoleBase>() != null) return "is part of a station (a station moves only as a whole)";
            int stations = t.GetComponentsInChildren<NovaStation>(true).Length;
            if (stations > 1) return "holds more than one station";
            if (stations == 0 && t.GetComponentInChildren<ConsoleBase>(true) != null) return "holds a console";
            return null;
        }

        // The players sitting at a station in this change: put where their station now is (the game puts them there
        // only when they sit down, which is before a layout saved during play goes on).
        private static void SeatPlayers(Change c)
        {
            if (c.T == null || c.T.GetComponentInChildren<NovaStation>(true) == null) return;
            foreach (var player in Tracked.All<NovaPlayerInfo>())
            {
                var station = player == null ? null : player.Station;
                if (station == null || player.PawnObject == null || !station.transform.IsChildOf(c.T)) continue;
                if (player.PawnObject.transform.IsChildOf(c.T)) continue;
                player.PawnObject.transform.position = station.transform.position;
                player.PawnObject.transform.rotation = station.transform.rotation;
            }
        }

        // ------------------------------------------------------------------------------------------- applying
        private static void Bind(Change c, Transform t)
        {
            c.T = t;
            c.BasePos = t.position;
            c.BaseRot = t.rotation;
            foreach (var r in t.GetComponentsInChildren<Renderer>(false))
            {
                if (!r.enabled || r is ParticleSystemRenderer) continue;
                c.Renderers.Add(r);
                if (r.isPartOfStaticBatch && r is MeshRenderer) { c.Batched.Add(r); c.BatchedAt.Add(r.bounds.center); }
                if (r is MeshRenderer)
                {
                    if (!c.HasBounds) { c.BaseBounds = r.bounds; c.HasBounds = true; }
                    else c.BaseBounds.Encapsulate(r.bounds);
                    if (Walking.Bridge.Path(r.transform).IndexOf("chair", StringComparison.OrdinalIgnoreCase) >= 0 && r.GetComponentInParent<NovaStation>() == null)
                        c.Chairs.Add(r.bounds);
                }
            }
            foreach (var col in t.GetComponentsInChildren<Collider>(false)) if (col.enabled) c.Colliders.Add(col);
            // Its lights: the ones on, and its own (whose values the change may set) on or not.
            var own = t.GetComponent<Light>();
            foreach (var l in t.GetComponentsInChildren<Light>(true))
                if (l == own || (l.enabled && l.gameObject.activeInHierarchy)) c.Lights.Add(BridgeLights.Saved.Of(l));
            // Its shapes in the bridge's file: to copy it (moved), and for Bridge Walk to leave out (moved or hidden).
            if (c.Batched.Count > 0 && (c.Moved || c.Hidden)) BatchedPieces.Ready(scene.buildIndex);
        }

        private static void Refresh(Change c)
        {
            if (c.T == null) return;
            if (c.Moved && c.Batched.Count > 0 && c.Copy == null && c.CopyProblem == null) MakeCopy(c);
            Quaternion turn = Quaternion.AngleAxis(c.Turn, Vector3.up);
            c.T.position = c.Pivot + turn * (c.BasePos - c.Pivot) + c.Move;
            c.T.rotation = turn * c.BaseRot;
            bool copied = c.Copy != null;
            foreach (var r in c.Renderers)
            {
                if (r == null) continue;
                // A batched piece is drawn where it was baked whatever its transform does: hidden while its copy stands in.
                r.enabled = !c.Hidden && !(copied && c.Batched.Contains(r));
            }
            if (copied) foreach (var r in c.Copy.GetComponentsInChildren<Renderer>(true)) r.enabled = !c.Hidden;
            foreach (var col in c.Colliders) if (col != null) col.enabled = !c.Hidden;
            foreach (var l in c.Lights)
            {
                if (l.Light == null) continue;
                if (l.Light.transform == c.T) { l.PutBack(); c.Light?.On(l.Light); }
                if (c.Hidden) l.Light.enabled = false;
                else if (l.Enabled) l.Light.enabled = true;
            }
            SeatPlayers(c);
        }

        private static void MakeCopy(Change c)
        {
            if (!BatchedPieces.Ready(scene.buildIndex)) return; // still reading the scene's file: tried again next frame
            if (BatchedPieces.Error != null) { c.CopyProblem = "the bridge's file couldn't be read: " + BatchedPieces.Error; Report(c); return; }
            foreach (var r in c.Batched)
            {
                if (r == null) continue;
                var candidates = BatchedPieces.At(Walking.Bridge.Path(r.transform));
                if (candidates == null || candidates.Count == 0) { c.CopyProblem = r.name + " isn't in the bridge's file"; Report(c); return; }
                if (candidates.Exists(p => p.HasRoot)) { c.CopyProblem = r.name + " is batched under a root of its own"; Report(c); return; }
                bool loaded = true;
                foreach (var p in candidates) if (!BatchedPieces.Loaded(p.Combined)) loaded = false;
                if (!loaded) return; // its combined mesh is being read
            }
            // The copy is made with the object where it was baked, then the change goes back on.
            c.T.position = c.BasePos;
            c.T.rotation = c.BaseRot;
            var root = new GameObject(CopyName);
            SceneManager.MoveGameObjectToScene(root, c.T.gameObject.scene);
            root.transform.SetParent(c.T, true);
            int made = 0;
            foreach (var r in c.Batched)
            {
                if (r == null) continue;
                // The same path more than once in the bridge: the one whose shape sits where this renderer is drawn.
                Mesh best = null; float bestD = float.MaxValue;
                foreach (var p in BatchedPieces.At(Walking.Bridge.Path(r.transform)))
                {
                    var m = BatchedPieces.Slice(p);
                    if (m == null) continue;
                    float d = (m.bounds.center - r.bounds.center).sqrMagnitude;
                    if (d < bestD) { if (best != null) UnityEngine.Object.Destroy(best); best = m; bestD = d; }
                    else UnityEngine.Object.Destroy(m);
                }
                if (best == null) continue;
                c.CopyMeshes.Add(best);
                var go = new GameObject(r.name);
                go.transform.SetParent(root.transform, false);
                // The slices' vertices are in the scene's space: the copy's own transform has to be the world's.
                go.transform.position = Vector3.zero;
                go.transform.rotation = Quaternion.identity;
                go.AddComponent<MeshFilter>().sharedMesh = best;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterials = r.sharedMaterials;
                mr.shadowCastingMode = r.shadowCastingMode;
                mr.receiveShadows = r.receiveShadows;
                mr.lightmapIndex = r.lightmapIndex;
                mr.lightmapScaleOffset = r.lightmapScaleOffset;
                made++;
            }
            if (made == 0) { UnityEngine.Object.Destroy(root); c.CopyProblem = "no shapes copied"; Report(c); return; }
            c.Copy = root;
            Plugin.Log.LogInfo($"Bridge layout: {c.Path}: {made} batched part(s) copied from the bridge's file to move");
        }

        private static void Report(Change c) =>
            Plugin.Log.LogInfo($"Bridge layout: {c.Path} can't move: {c.CopyProblem} (it stays where it was; hiding it still works)");

        /// <summary>The object back as the bridge had it.</summary>
        private static void Reset(Change c)
        {
            if (c.T != null) { c.T.position = c.BasePos; c.T.rotation = c.BaseRot; }
            SeatPlayers(c);
            foreach (var r in c.Renderers) if (r != null) r.enabled = true;
            foreach (var col in c.Colliders) if (col != null) col.enabled = true;
            foreach (var l in c.Lights) l.PutBack();
            if (c.Copy != null) UnityEngine.Object.Destroy(c.Copy);
            c.Copy = null;
        }

        // ------------------------------------------------------------------------------------------- Bridge Walk
        /// <summary>A batched piece the layout hides (or hides while its copy stands in), for Bridge Walk to leave out
        /// of the combined meshes: the shapes with its path in the bridge's file (combined mesh and its sub-meshes),
        /// and where it was drawn, to tell which one it is.</summary>
        internal sealed class WalkHole
        {
            public string File;
            public long[] Combined;
            public int[][] SubMeshes;
            public Vector3 Center;
        }

        private static int walkHoles = -1;  // how many holes Bridge Walk's floors and walls were last read with

        /// <summary>The layout's hidden batched pieces whose shapes in the bridge's file are known (main thread).</summary>
        internal static List<WalkHole> WalkHoles()
        {
            var list = new List<WalkHole>();
            if (boundRoot == 0 || !scene.IsValid() || !scene.isLoaded) return list;
            string file = "level" + scene.buildIndex;
            foreach (var c in changes)
                for (int i = 0; i < c.Batched.Count; i++)
                {
                    var r = c.Batched[i];
                    if (r == null || r.enabled) continue;
                    var shapes = BatchedPieces.At(Walking.Bridge.Path(r.transform));
                    if (shapes == null || shapes.Count == 0) continue;
                    var h = new WalkHole { File = file, Combined = new long[shapes.Count], SubMeshes = new int[shapes.Count][], Center = c.BatchedAt[i] };
                    for (int k = 0; k < shapes.Count; k++) { h.Combined[k] = shapes[k].Combined; h.SubMeshes[k] = shapes[k].SubMeshes.ToArray(); }
                    list.Add(h);
                }
            return list;
        }

        // Every frame: once more of the layout's hidden pieces can be left out (the bridge's file read, or a copy
        // made), Bridge Walk reads its floors and walls again.
        private static void WalkFollow()
        {
            int n = 0;
            foreach (var c in changes)
                foreach (var r in c.Batched)
                    if (r != null && !r.enabled) n++;
            // Their shapes are known once the bridge's file is read.
            if (n > 0 && (!BatchedPieces.Ready(scene.buildIndex) || BatchedPieces.Error != null)) n = 0;
            if (n == walkHoles) return;
            try { if (WalkMesh.Clear()) walkHoles = n; } catch { walkHoles = n; }
        }

        // ------------------------------------------------------------------------------------------- background crew
        private sealed class Follower
        {
            public Transform Seat;
            public Vector3 BasePos;
            public Quaternion BaseRot;
        }

        private static readonly List<Follower> followers = new List<Follower>();
        private static readonly HashSet<int> seatsDone = new HashSet<int>();

        /// <summary>Every second: each background crew member spawned since, sitting in a chair that was moved, goes with it.</summary>
        private static void Followers()
        {
            if (changes.Count == 0) return;
            foreach (var seat in UnityEngine.Object.FindObjectsOfType<RedShirtStation>())
            {
                if (seat == null || seat.gameObject.scene != scene || seatsDone.Contains(seat.GetInstanceID())) continue;
                // Its own chair (in the NPC's model): there once it's spawned.
                Renderer chair = null;
                foreach (var r in seat.GetComponentsInChildren<SkinnedMeshRenderer>(false))
                    if (r.enabled && (r.name == "Chair" || r.name == "ChairRetro")) { chair = r; break; }
                if (chair == null) continue;
                seatsDone.Add(seat.GetInstanceID());
                Vector3 at = chair.bounds.center;
                Change by = null;
                float best = 0.6f;
                foreach (var c in changes)
                {
                    if (!c.HasBounds || !c.Moved || c.T == null) continue;
                    // Where the piece was: the NPC's chair inside it, seen from above.
                    Vector3 middle = c.BaseBounds.center, half = c.BaseBounds.extents;
                    float dx = Mathf.Max(0f, Mathf.Abs(at.x - middle.x) - half.x), dz = Mathf.Max(0f, Mathf.Abs(at.z - middle.z) - half.z);
                    float d = Mathf.Sqrt(dx * dx + dz * dz);
                    if (d < best && Mathf.Abs(at.y - middle.y) < 1.5f) { best = d; by = c; }
                }
                if (by == null) continue;
                var t = seat.transform;
                followers.Add(new Follower { Seat = t, BasePos = t.position, BaseRot = t.rotation });
                Quaternion turn = Quaternion.AngleAxis(by.Turn, Vector3.up);
                t.position = by.Pivot + turn * (t.position - by.Pivot) + by.Move;
                t.rotation = turn * t.rotation;
                Plugin.Log.LogInfo($"Bridge layout: the crew member at {t.name} sits in {by.Path}: moved with it");
            }
        }

        private static void ResetFollowers()
        {
            foreach (var f in followers) if (f.Seat != null) { f.Seat.position = f.BasePos; f.Seat.rotation = f.BaseRot; }
            followers.Clear();
            seatsDone.Clear();
        }

        /// <summary>The scene's gone (its objects with it) or its layout is put on again: what we made goes.</summary>
        private static void Forget()
        {
            BridgeParts.Clear();
            BridgeLights.Clear();
            followers.Clear();
            seatsDone.Clear();
            foreach (var c in changes) foreach (var m in c.CopyMeshes) if (m != null) UnityEngine.Object.Destroy(m);
            changes.Clear();
            walkHoles = -1;
            boundRoot = 0;
            boundKey = null;
        }
    }

    /// <summary>
    /// A bridge scene's statically batched pieces as its file has them: for each object (by path), which combined
    /// mesh it's drawn in and which of that mesh's sub-meshes are its. The list is read once per scene on a background
    /// thread; a combined mesh itself is read (also in the background) the first time a piece of it is asked for.
    /// </summary>
    internal static class BatchedPieces
    {
        internal sealed class Piece
        {
            public long Combined;
            public bool HasRoot;
            public readonly List<int> SubMeshes = new List<int>();
        }

        private static readonly Dictionary<string, List<Piece>> byPath = new Dictionary<string, List<Piece>>();
        private static readonly Dictionary<long, MeshAsset> meshes = new Dictionary<long, MeshAsset>();
        private static readonly HashSet<long> wanted = new HashSet<long>();
        private static int scene = -1;
        private static string file;
        private static volatile bool reading, done, loading;
        internal static volatile string Error;

        /// <summary>Starts reading (once per scene); true once the list is read (or failed: see Error).</summary>
        internal static bool Ready(int buildIndex)
        {
            if (buildIndex < 0) { Error = "the bridge has no file"; return true; }
            if (scene == buildIndex) return done;
            if (reading || loading) return false;
            scene = buildIndex;
            done = false;
            Error = null;
            lock (meshes) { byPath.Clear(); meshes.Clear(); wanted.Clear(); }
            file = Path.Combine(Application.dataPath, "level" + buildIndex);
            reading = true;
            string path = file;
            new Thread(() =>
            {
                try { Read(path); }
                catch (Exception e) { Error = e.Message; }
                reading = false;
                done = true;
            }) { IsBackground = true, Name = "Bridge layout pieces" }.Start();
            return false;
        }

        private static void Read(string path)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int count = 0;
            using (var f = new AssetFile(path))
            {
                var names = new Dictionary<long, string>();   // transform path ID -> its object's name
                var fathers = new Dictionary<long, long>();   // transform path ID -> its parent's (0: none)
                var goTransform = new Dictionary<long, long>();
                foreach (var entry in f.Objects)
                {
                    if (entry.ClassId != 4 && entry.ClassId != 224) continue;
                    var t = TransformAsset.Read(f.At(entry));
                    var goEntry = f.Find(t.GameObject.PathId);
                    if (goEntry == null) continue;
                    names[entry.PathId] = GameObjectAsset.Read(f.At(goEntry)).Name;
                    fathers[entry.PathId] = t.Father.IsNull ? 0 : t.Father.PathId;
                    goTransform[t.GameObject.PathId] = entry.PathId;
                }
                string PathOf(long tid)
                {
                    var sb = new StringBuilder();
                    for (int guard = 0; tid != 0 && guard < 64; guard++)
                    {
                        if (!names.TryGetValue(tid, out var n)) break;
                        sb.Insert(0, "/" + n);
                        fathers.TryGetValue(tid, out tid);
                    }
                    return sb.ToString();
                }
                foreach (var entry in f.Objects)
                {
                    if (entry.ClassId != 23) continue;
                    var renderer = MeshRendererAsset.Read(f.At(entry));
                    if (renderer.SubsetIndices.Count == 0) continue;
                    var goEntry = f.Find(renderer.GameObject.PathId);
                    if (goEntry == null || !goTransform.TryGetValue(renderer.GameObject.PathId, out var tid)) continue;
                    var go = GameObjectAsset.Read(f.At(goEntry));
                    var filterRef = go.Component(33);
                    if (filterRef.IsNull) continue;
                    var filterEntry = f.Find(filterRef.PathId);
                    if (filterEntry == null) continue;
                    var filter = MeshFilterAsset.Read(f.At(filterEntry));
                    if (filter.Mesh.FileId != 0) continue;
                    var piece = new Piece { Combined = filter.Mesh.PathId, HasRoot = !renderer.StaticBatchRoot.IsNull };
                    foreach (var i in renderer.SubsetIndices) piece.SubMeshes.Add((int)i);
                    string p = PathOf(tid);
                    lock (meshes)
                    {
                        if (!byPath.TryGetValue(p, out var l)) byPath[p] = l = new List<Piece>();
                        l.Add(piece);
                    }
                    count++;
                }
            }
            Plugin.Log.LogInfo($"Bridge layout: {count} batched pieces listed in {Path.GetFileName(path)} ({sw.ElapsedMilliseconds} ms, in the background)");
        }

        internal static List<Piece> At(string path)
        {
            lock (meshes) return byPath.TryGetValue(path, out var l) ? l : null;
        }

        /// <summary>Whether a combined mesh is read; if not, it's asked for.</summary>
        internal static bool Loaded(long combined)
        {
            lock (meshes)
            {
                if (meshes.ContainsKey(combined)) return true;
                wanted.Add(combined);
                if (loading) return false;
                loading = true;
            }
            string path = file;
            new Thread(() =>
            {
                try
                {
                    using (var f = new AssetFile(path))
                        while (true)
                        {
                            long id;
                            lock (meshes)
                            {
                                id = 0;
                                foreach (var w in wanted) { id = w; break; }
                                if (id == 0) { loading = false; return; }
                                wanted.Remove(id);
                            }
                            var entry = f.Find(id);
                            var m = entry != null ? MeshAsset.Read(f.At(entry)) : null;
                            lock (meshes) meshes[id] = m;
                        }
                }
                catch (Exception e)
                {
                    Error = e.Message;
                    lock (meshes) { foreach (var w in wanted) meshes[w] = null; wanted.Clear(); loading = false; }
                }
            }) { IsBackground = true, Name = "Bridge layout mesh" }.Start();
            return false;
        }

        /// <summary>Main thread: a piece's sub-meshes of its combined mesh (in the scene's space), as a mesh of its own.</summary>
        internal static Mesh Slice(Piece piece)
        {
            MeshAsset m;
            lock (meshes) meshes.TryGetValue(piece.Combined, out m);
            return m == null ? null : SliceMesh(m, piece.SubMeshes, BridgeLayout.CopyName + " piece");
        }

        /// <summary>Some of a mesh's sub-meshes, as a mesh of its own (only the vertices they use; any vertex format).</summary>
        internal static Mesh SliceMesh(MeshAsset m, IList<int> subMeshes, string name)
        {
            var map = new Dictionary<int, int>();
            var vertices = new List<Vector3>();
            var normals = m.Has(1) ? new List<Vector3>() : null;
            var uvs = m.Has(3) ? new List<Vector2>() : null;
            var uv2s = m.Has(4) ? new List<Vector2>() : null;
            var tangents = m.Has(7) ? new List<Vector4>() : null;
            var triangles = new List<int[]>();
            foreach (var s in subMeshes)
            {
                if (s < 0 || s >= m.SubMeshes.Count) continue;
                var sub = m.SubMeshes[s];
                var tri = new int[sub.IndexCount];
                for (int i = 0; i < tri.Length; i++)
                {
                    int v = BitConverter.ToUInt16(m.IndexBuffer, (int)sub.FirstByte + i * 2);
                    if (!map.TryGetValue(v, out var at))
                    {
                        at = map[v] = vertices.Count;
                        var p = m.Get(0, v);
                        vertices.Add(new Vector3(p[0], p[1], p[2]));
                        if (normals != null) { var n = m.Get(1, v); normals.Add(new Vector3(n[0], n[1], n[2])); }
                        if (uvs != null) { var u = m.Get(3, v); uvs.Add(new Vector2(u[0], u[1])); }
                        if (uv2s != null) { var u = m.Get(4, v); uv2s.Add(new Vector2(u[0], u[1])); }
                        if (tangents != null) { var t = m.Get(7, v); tangents.Add(new Vector4(t[0], t[1], t[2], t[3])); }
                    }
                    tri[i] = at;
                }
                triangles.Add(tri);
            }
            if (vertices.Count == 0) return null;
            var mesh = new Mesh { name = name };
            mesh.vertices = vertices.ToArray();
            if (normals != null) mesh.normals = normals.ToArray();
            if (uvs != null) mesh.uv = uvs.ToArray();
            if (uv2s != null) mesh.uv2 = uv2s.ToArray();
            if (tangents != null) mesh.tangents = tangents.ToArray();
            mesh.subMeshCount = triangles.Count;
            for (int i = 0; i < triangles.Count; i++) mesh.SetTriangles(triangles[i], i);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
