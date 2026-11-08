using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;

namespace StbcRevival.Walking
{
    /// <summary>
    /// Floors and walls from the bridge's visible meshes. Some bridges (the Enterprise-D) have hardly any colliders,
    /// so the meshes within 25 m of you are read once as you stand up, and their triangles sorted into floors and walls
    /// on a 0.5 m grid. The game knows nothing of this.
    /// </summary>
    internal static class WalkMesh
    {
        private const float Cell = 0.5f;
        private const float Radius = 25f;
        private static readonly Dictionary<long, List<int>> floorCells = new Dictionary<long, List<int>>();
        private static readonly Dictionary<long, List<int>> wallCells = new Dictionary<long, List<int>>();
        private static readonly List<Vector3> fv = new List<Vector3>();
        private static readonly List<Vector3> wv = new List<Vector3>();
        private static Vector3 builtAt = new Vector3(1e9f, 0, 0);
        private static int builtScene = -1;
        private static bool builtWithIndex;
        public static bool Ready => fv.Count > 0 || wv.Count > 0;

        private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;
        private static int C(float v) => Mathf.FloorToInt(v / Cell);

        public static void Ensure(Vector3 center)
        {
            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex;
            if (scene == builtScene && (center - builtAt).magnitude < 10f && (builtWithIndex || !BridgeMeshes.Indexed)) return;
            Build(center);
            builtScene = scene;
        }

        private static void Build(Vector3 center)
        {
            float t0 = Time.realtimeSinceStartup;
            floorCells.Clear(); wallCells.Clear(); fv.Clear(); wv.Clear();
            builtAt = center;
            int nearby = 0, unreadable = 0, used = 0, fromDisk = 0;
            var combinedDone = new HashSet<Mesh>();
            builtWithIndex = BridgeMeshes.Indexed;
            try
            {
                foreach (var mf in UnityEngine.Object.FindObjectsOfType<MeshFilter>())
                {
                    var mr = mf.GetComponent<MeshRenderer>();
                    if (mr == null || !mr.enabled || !mf.gameObject.activeInHierarchy) continue;
                    var mesh = mf.sharedMesh;
                    if (mesh == null) continue;
                    Bounds b = mr.bounds;
                    if ((b.ClosestPoint(center) - center).magnitude > Radius) continue;
                    if (b.size.magnitude > 400f) continue; // sky, stars, the view outside
                    string path = Bridge.Path(mf.transform);
                    if (path.StartsWith("/Players/") || path.IndexOf("/FX/", StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf("Canvas", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("Hologram", StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    nearby++;
                    // Static batching merges meshes into world-space "Combined Mesh" objects shared by many filters.
                    bool combined = mesh.name.StartsWith("Combined Mesh");
                    if (combined && !combinedDone.Add(mesh)) continue;
                    Matrix4x4 m = combined ? Matrix4x4.identity : mf.transform.localToWorldMatrix;
                    Vector3[] verts = null; int[] tris = null;
                    if (mesh.isReadable)
                    {
                        try { verts = mesh.vertices; tris = mesh.triangles; } catch { verts = null; }
                    }
                    if (verts == null)
                    {
                        if (BridgeMeshes.TryLoad(mesh, out verts, out tris)) fromDisk++;
                        else { unreadable++; continue; }
                    }
                    used++;
                    for (int i = 0; i + 2 < tris.Length; i += 3)
                    {
                        Vector3 a = m.MultiplyPoint3x4(verts[tris[i]]), bb = m.MultiplyPoint3x4(verts[tris[i + 1]]), c = m.MultiplyPoint3x4(verts[tris[i + 2]]);
                        if ((a - center).sqrMagnitude > Radius * Radius * 1.5f && (bb - center).sqrMagnitude > Radius * Radius * 1.5f) continue;
                        Vector3 n = Vector3.Cross(bb - a, c - a);
                        float len = n.magnitude;
                        if (len < 1e-6f) continue;
                        float ny = n.y / len;
                        if (Mathf.Abs(ny) > 0.6f) Add(floorCells, fv, a, bb, c); // floor (either winding)
                        else if (Mathf.Abs(ny) < 0.35f) Add(wallCells, wv, a, bb, c);
                    }
                }
            }
            finally { BridgeMeshes.CloseFiles(); }
            Plugin.Log.LogInfo($"Bridge walk: bridge geometry: {nearby} meshes nearby, {used} used ({fromDisk} from the asset files), {unreadable} unreadable, "
                + $"{fv.Count / 3} floor and {wv.Count / 3} wall triangles, {(Time.realtimeSinceStartup - t0) * 1000f:0} ms; {BridgeMeshes.Status}");
        }

        private static void Add(Dictionary<long, List<int>> cells, List<Vector3> store, Vector3 a, Vector3 b, Vector3 c)
        {
            int idx = store.Count / 3;
            store.Add(a); store.Add(b); store.Add(c);
            int x0 = C(Mathf.Min(a.x, Mathf.Min(b.x, c.x))), x1 = C(Mathf.Max(a.x, Mathf.Max(b.x, c.x)));
            int z0 = C(Mathf.Min(a.z, Mathf.Min(b.z, c.z))), z1 = C(Mathf.Max(a.z, Mathf.Max(b.z, c.z)));
            if ((x1 - x0 + 1) * (z1 - z0 + 1) > 2500) return;
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    long k = Key(x, z);
                    if (!cells.TryGetValue(k, out var l)) { l = new List<int>(); cells[k] = l; }
                    l.Add(idx);
                }
        }

        /// <summary>The heights of all floor triangles at (x, z) between yBottom and yTop.</summary>
        public static void Floors(Vector3 p, float yTop, float yBottom, List<float> heights)
        {
            if (!floorCells.TryGetValue(Key(C(p.x), C(p.z)), out var l)) return;
            foreach (int i in l)
            {
                Vector3 a = fv[i * 3], b = fv[i * 3 + 1], c = fv[i * 3 + 2];
                float d = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
                if (Mathf.Abs(d) < 1e-8f) continue;
                float u = ((b.z - c.z) * (p.x - c.x) + (c.x - b.x) * (p.z - c.z)) / d;
                float v = ((c.z - a.z) * (p.x - c.x) + (a.x - c.x) * (p.z - c.z)) / d;
                float w = 1f - u - v;
                if (u < -1e-4f || v < -1e-4f || w < -1e-4f) continue;
                float y = u * a.y + v * b.y + w * c.y;
                if (y <= yTop && y >= yBottom) heights.Add(y);
            }
        }

        /// <summary>Does the segment from s to e cross a wall triangle?</summary>
        public static bool Hits(Vector3 s, Vector3 e)
        {
            int x0 = C(Mathf.Min(s.x, e.x)), x1 = C(Mathf.Max(s.x, e.x)), z0 = C(Mathf.Min(s.z, e.z)), z1 = C(Mathf.Max(s.z, e.z));
            Vector3 dir = e - s;
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    if (!wallCells.TryGetValue(Key(x, z), out var l)) continue;
                    foreach (int i in l)
                        if (Segment(s, dir, wv[i * 3], wv[i * 3 + 1], wv[i * 3 + 2])) return true;
                }
            return false;
        }

        // Möller-Trumbore, limited to the segment.
        private static bool Segment(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 e1 = b - a, e2 = c - a, p = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, p);
            if (Mathf.Abs(det) < 1e-9f) return false;
            float inv = 1f / det;
            Vector3 t = o - a;
            float u = Vector3.Dot(t, p) * inv;
            if (u < 0f || u > 1f) return false;
            Vector3 q = Vector3.Cross(t, e1);
            float v = Vector3.Dot(d, q) * inv;
            if (v < 0f || u + v > 1f) return false;
            float k = Vector3.Dot(e2, q) * inv;
            return k >= 0f && k <= 1f;
        }
    }

    /// <summary>
    /// Most bridge meshes aren't readable in memory (isReadable is off), so their triangles are read from the game's
    /// own asset files with AssetFile. A background thread lists every mesh in the level and shared asset files by
    /// name at launch; a mesh is then found by its name and vertex count.
    /// </summary>
    internal static class BridgeMeshes
    {
        private sealed class Ref { public string File; public long PathId; }

        private static readonly Dictionary<string, List<Ref>> byName = new Dictionary<string, List<Ref>>();
        private static readonly Dictionary<string, KeyValuePair<Vector3[], int[]>> loaded = new Dictionary<string, KeyValuePair<Vector3[], int[]>>();
        private static readonly Dictionary<string, AssetFile> open = new Dictionary<string, AssetFile>();
        private static volatile bool indexed;
        private static Thread worker;
        public static bool Indexed => indexed;
        public static volatile string Status = "asset file index not started";

        public static void StartIndex(string dataDir)
        {
            if (worker != null) return;
            worker = new Thread(() =>
            {
                var t0 = DateTime.Now;
                int files = 0, meshes = 0;
                try
                {
                    foreach (var path in Directory.GetFiles(dataDir))
                    {
                        string n = Path.GetFileName(path);
                        bool candidate = (n.StartsWith("level") && !n.Contains(".")) || (n.EndsWith(".assets") && (n.StartsWith("sharedassets") || n == "resources.assets"));
                        if (!candidate) continue;
                        try
                        {
                            using (var file = new AssetFile(path))
                                foreach (var entry in file.Objects)
                                {
                                    if (entry.ClassId != AssetFile.MeshClass) continue;
                                    string name = file.NameOf(entry);
                                    if (name == null) continue;
                                    lock (byName)
                                    {
                                        if (!byName.TryGetValue(name, out var l)) byName[name] = l = new List<Ref>();
                                        l.Add(new Ref { File = path, PathId = entry.PathId });
                                    }
                                    meshes++;
                                }
                            files++;
                        }
                        catch (Exception e) { Status = "asset file index: " + n + ": " + e.Message; }
                    }
                    Status = $"asset file index: {meshes} meshes in {files} files ({(DateTime.Now - t0).TotalSeconds:0.0} s)";
                }
                catch (Exception e) { Status = "asset file index failed: " + e.Message; }
                indexed = true;
            });
            worker.IsBackground = true;
            worker.Start();
        }

        /// <summary>A mesh's positions and triangle indices, from the asset files.</summary>
        public static bool TryLoad(Mesh mesh, out Vector3[] verts, out int[] tris)
        {
            verts = null; tris = null;
            if (!indexed) return false;
            int vc = mesh.vertexCount;
            string key = mesh.name + "#" + vc;
            if (loaded.TryGetValue(key, out var hit)) { verts = hit.Key; tris = hit.Value; return verts != null; }
            List<Ref> refs;
            lock (byName) byName.TryGetValue(mesh.name, out refs);
            if (refs != null)
                foreach (var rf in refs)
                {
                    try { if (Read(rf, vc, out verts, out tris)) break; }
                    catch { verts = null; tris = null; }
                }
            loaded[key] = new KeyValuePair<Vector3[], int[]>(verts, tris);
            return verts != null;
        }

        private static bool Read(Ref rf, int vertexCount, out Vector3[] verts, out int[] tris)
        {
            verts = null; tris = null;
            if (!open.TryGetValue(rf.File, out var file)) open[rf.File] = file = new AssetFile(rf.File);
            var entry = file.Find(rf.PathId);
            if (entry == null) return false;
            var m = MeshAsset.Read(file.At(entry));
            // Compressed meshes keep no plain vertex data; their positions aren't read here.
            if (m.VertexCount != vertexCount || m.VertexData == null || m.VertexData.Length == 0 || !m.Has(0)) return false;
            verts = new Vector3[m.VertexCount];
            for (int i = 0; i < verts.Length; i++)
            {
                var p = m.Get(0, i);
                verts[i] = new Vector3(p[0], p[1], p[2]);
            }
            var list = new List<int>();
            foreach (var s in m.SubMeshes)
            {
                if (s.Topology != 0) continue; // triangles only
                for (uint i = 0; i < s.IndexCount; i++)
                {
                    long at = s.FirstByte + i * 2L;
                    if (at + 1 >= m.IndexBuffer.Length) break;
                    int index = BitConverter.ToUInt16(m.IndexBuffer, (int)at);
                    if (index >= verts.Length) { verts = null; return false; }
                    list.Add(index);
                }
            }
            tris = list.ToArray();
            return true;
        }

        /// <summary>The asset files opened while reading the bridge are closed again once it's read.</summary>
        public static void CloseFiles()
        {
            foreach (var f in open.Values) f.Dispose();
            open.Clear();
        }
    }
}
