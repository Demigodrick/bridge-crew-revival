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
    /// Stations stay put: a change to anything that is or holds a console or station is left out.
    ///
    /// Most bridge pieces are statically batched (drawn as part of one combined mesh), so they can't move: those are
    /// hidden, and a copy of their shape read from the scene's file moves instead (BatchedPieces). Colliders move with
    /// the object. Bridge Walk's floors and walls are read again after a layout goes on; a hidden or moved batched
    /// piece still counts where it was for those (they come from the combined mesh).
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
        }

        /// <summary>A layout file's changes (format 1); throws, saying what's wrong, if it isn't one.</summary>
        internal static List<LayoutPiece> ParseLayout(string text)
        {
            var root = MiniJson.Parse(text) as Dictionary<string, object>;
            if (root == null) throw new FormatException("not a JSON object");
            if (!root.TryGetValue("format", out var format) || !(format is double f) || f != 1) throw new FormatException($"format {format ?? "missing"}, not 1");
            var list = new List<LayoutPiece>();
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

            public Transform T;
            public Vector3 BasePos;
            public Quaternion BaseRot;
            public readonly List<Renderer> Renderers = new List<Renderer>(); // on at the start
            public readonly List<Renderer> Batched = new List<Renderer>();
            public readonly List<Collider> Colliders = new List<Collider>();
            public GameObject Copy;
            public readonly List<Mesh> CopyMeshes = new List<Mesh>();
            public string CopyProblem;

            public bool Moved => Move.sqrMagnitude > 1e-8f || Mathf.Abs(Turn) > 1e-3f;
        }

        private static readonly List<Change> changes = new List<Change>();
        private static Scene scene;
        private static int boundRoot;          // the scene's first root object: changes when it's loaded again
        private static string boundFile;
        private static DateTime boundWrite;
        private static float nextLook;
        private static bool lookedOnce;

        private static string Folder => Path.Combine(Paths.BepInExRootPath, "bridges");

        // ------------------------------------------------------------------------------------------- every frame
        internal static void Update()
        {
            if (Plugin.BridgeLayouts == null || !Plugin.BridgeLayouts.Value) return;
            if (boundRoot != 0)
            {
                // Copies of batched pieces wait for the scene's file to be read.
                foreach (var c in changes) if (c.T != null && c.Moved && c.Batched.Count > 0 && c.Copy == null && c.CopyProblem == null) Refresh(c);
            }
            if (Time.unscaledTime < nextLook) return;
            nextLook = Time.unscaledTime + 1f;

            if (boundRoot != 0)
            {
                // Still loaded, the same load of it, and the file unchanged: nothing to do.
                int root = scene.IsValid() && scene.isLoaded ? RootId(scene) : 0;
                if (root != boundRoot) { Forget(); }
                else
                {
                    DateTime write = File.Exists(boundFile) ? File.GetLastWriteTimeUtc(boundFile) : DateTime.MinValue;
                    if (write == boundWrite) return;
                    Plugin.Log.LogInfo($"Bridge layout: {Path.GetFileName(boundFile)} changed: put on again");
                    foreach (var c in changes) Reset(c);
                    Forget();
                }
            }
            if (!lookedOnce) { lookedOnce = true; Plugin.Log.LogInfo($"Bridge layout: layouts are read from {Folder}{(Directory.Exists(Folder) ? "" : " (not there yet)")}"); }
            if (!Directory.Exists(Folder)) return;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (!s.isLoaded) continue;
                string file = Path.Combine(Folder, s.name + ".json");
                if (!File.Exists(file)) continue;
                int root = RootId(s);
                if (root == 0) continue;
                scene = s;
                boundRoot = root;
                boundFile = file;
                boundWrite = File.GetLastWriteTimeUtc(file);
                Load(file);
                return;
            }
        }

        private static int RootId(Scene s)
        {
            var roots = s.GetRootGameObjects();
            return roots.Length > 0 ? roots[0].GetInstanceID() : 0;
        }

        // ------------------------------------------------------------------------------------------- the file
        private static void Load(string file)
        {
            List<LayoutPiece> layout;
            try { layout = ParseLayout(File.ReadAllText(file)); }
            catch (Exception e) { Plugin.Log.LogWarning($"Bridge layout: {file} isn't a layout this version can read: {e.Message}"); return; }
            Plugin.Log.LogInfo($"Bridge layout: {Path.GetFileName(file)} for {scene.name}: {layout.Count} change(s)");
            if (layout.Count == 0) return;
            var byPath = Index();
            int on = 0, missing = 0, refused = 0;
            foreach (var p in layout)
            {
                var c = new Change { Path = p.Path, Pivot = p.Pivot, Move = p.Move, Turn = p.Turn, Hidden = p.Hidden };
                var t = Resolve(byPath, c.Path, c.Pivot);
                if (t == null) { missing++; Plugin.Log.LogInfo($"Bridge layout: {c.Path} isn't in this bridge"); continue; }
                if (IsStation(t)) { refused++; Plugin.Log.LogInfo($"Bridge layout: {c.Path} is or holds a station: stations stay put"); continue; }
                if (changes.Exists(o => o.T != null && (t.IsChildOf(o.T) || o.T.IsChildOf(t)))) { refused++; Plugin.Log.LogInfo($"Bridge layout: {c.Path} overlaps another change: left out"); continue; }
                Bind(c, t);
                changes.Add(c);
                Refresh(c);
                on++;
            }
            Plugin.Log.LogInfo($"Bridge layout: {scene.name}: {on} change(s) put on"
                               + (missing > 0 ? $", {missing} not found" : "") + (refused > 0 ? $", {refused} left out" : ""));
            try { WalkMesh.Clear(); } catch { }
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

        private static bool IsStation(Transform t) =>
            t.GetComponentInParent<ConsoleBase>() != null || t.GetComponentInParent<NovaStation>() != null
            || t.GetComponentInChildren<ConsoleBase>(true) != null || t.GetComponentInChildren<NovaStation>(true) != null;

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
                if (r.isPartOfStaticBatch && r is MeshRenderer) c.Batched.Add(r);
            }
            foreach (var col in t.GetComponentsInChildren<Collider>(false)) if (col.enabled) c.Colliders.Add(col);
            if (c.Batched.Count > 0 && c.Moved) BatchedPieces.Ready(scene.buildIndex);
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
            foreach (var r in c.Renderers) if (r != null) r.enabled = true;
            foreach (var col in c.Colliders) if (col != null) col.enabled = true;
            if (c.Copy != null) UnityEngine.Object.Destroy(c.Copy);
            c.Copy = null;
        }

        /// <summary>The scene's gone (its objects with it) or its layout is put on again: what we made goes.</summary>
        private static void Forget()
        {
            foreach (var c in changes) foreach (var m in c.CopyMeshes) if (m != null) UnityEngine.Object.Destroy(m);
            changes.Clear();
            boundRoot = 0;
            boundFile = null;
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
            if (m == null) return null;
            var map = new Dictionary<int, int>();
            var vertices = new List<Vector3>();
            var normals = m.Has(1) ? new List<Vector3>() : null;
            var uvs = m.Has(3) ? new List<Vector2>() : null;
            var uv2s = m.Has(4) ? new List<Vector2>() : null;
            var tangents = m.Has(7) ? new List<Vector4>() : null;
            var triangles = new List<int[]>();
            foreach (var s in piece.SubMeshes)
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
            var mesh = new Mesh { name = BridgeLayout.CopyName + " piece" };
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
