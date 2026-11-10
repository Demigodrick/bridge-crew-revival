using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// The character creator's face sliders on the Klingon head. They're the last nine face shapes of the avatar's
    /// own head (FaceCustomization.SetCustomization: nose, jaw, cheeks...), which the Klingon head doesn't have. So
    /// they're carried over: each Klingon vertex takes the movement of the nearest vertex of the avatar's head (both
    /// in the avatar's Head bone space, where the graft puts the Klingon head), fading out over a few centimetres
    /// (the ridges, hair and beard stand off the human head and keep their shape). The shapes go on a copy of the
    /// Klingon head's mesh under the same names, so KlingonHeadGraft copies the sliders' weights as it copies speech.
    /// The avatar's head is read from memory, or from the game's files when it isn't readable there; the matching
    /// runs on a background thread, the mesh is made on the main thread (Tick).
    /// </summary>
    internal static class KlingonFace
    {
        internal const int Shapes = 9;

        private sealed class Human
        {
            public Vector3[] Verts;          // in the head's Head bone space (its bind pose)
            public string[] Names;
            public Vector3[][] Deltas;       // per shape, per vertex, in Head bone space
        }

        private sealed class Job
        {
            public KlingonHeadGraft Graft;
            public Mesh Source;
            public Vector3[] KlingonVerts;
            public Matrix4x4 Local, LocalInverse; // Klingon mesh space to and from the avatar's Head bone space
            public float Spacing;
            public string HeadKey;
            public Vector3[][] Result;
            public string[] Names;
            public volatile bool Done;
            public string Error;
        }

        private static readonly Dictionary<string, Human> humans = new Dictionary<string, Human>();
        private static readonly List<Job> jobs = new List<Job>();
        private static readonly object gate = new object();
        private static readonly object loading = new object();
        private static readonly Dictionary<string, string> failed = new Dictionary<string, string>(); // head key: why it couldn't be read

        /// <summary>Starts making the sliders' shapes for this graft (the Klingon head on that avatar head).</summary>
        internal static void Request(KlingonHeadGraft graft, SkinnedMeshRenderer head, int headBone, Mesh source, Matrix4x4 local, float spacing)
        {
            try
            {
                var mesh = head.sharedMesh;
                if (mesh == null || headBone < 0 || source == null) return;
                if (!source.isReadable) { Plugin.Log.LogInfo("Klingon head: its mesh can't be read back (it came from a bridge), so no face sliders on it"); return; }
                if (mesh.blendShapeCount < Shapes) return;
                string key = $"{mesh.name}|{mesh.vertexCount}|{headBone}";
                lock (gate) if (failed.ContainsKey(key)) return; // said once already (Tick logged why)
                var job = new Job
                {
                    Graft = graft, Source = source, KlingonVerts = source.vertices, Local = local, LocalInverse = local.inverse,
                    Spacing = spacing, HeadKey = key,
                };
                Human human;
                lock (gate) humans.TryGetValue(key, out human);
                if (human == null && mesh.isReadable) { human = FromMemory(mesh, headBone); lock (gate) humans[key] = human; }
                string meshName = mesh.name; int count = mesh.vertexCount; string dataDir = Path.Combine(Application.dataPath, "");
                lock (gate) jobs.Add(job);
                new Thread(() =>
                {
                    try
                    {
                        Human h;
                        // One thread reads a head from the files; the creator's other avatars wait for it, and a head
                        // that isn't there isn't looked for again.
                        lock (loading)
                        {
                            lock (gate) { humans.TryGetValue(key, out h); }
                            if (h == null)
                            {
                                string why;
                                lock (gate) failed.TryGetValue(key, out why);
                                if (why != null) throw new FileNotFoundException(why);
                                try { h = FromFiles(dataDir, meshName, count, headBone); }
                                catch (Exception e) { lock (gate) failed[key] = e.Message; throw; }
                                lock (gate) humans[key] = h;
                            }
                        }
                        Transfer(job, h);
                    }
                    catch (Exception e) { job.Error = e.Message; }
                    job.Done = true;
                }) { IsBackground = true, Name = "Klingon face sliders" }.Start();
            }
            catch (Exception e) { Plugin.Log.LogWarning("Klingon head: face sliders: " + e.Message); }
        }

        /// <summary>Main thread: finished shapes go onto a copy of the Klingon head's mesh, worn by the graft.</summary>
        internal static void Tick()
        {
            if (jobs.Count == 0) return;
            List<Job> done = null;
            lock (gate)
                foreach (var j in jobs) if (j.Done) (done ?? (done = new List<Job>())).Add(j);
            if (done == null) return;
            foreach (var job in done)
            {
                lock (gate) jobs.Remove(job);
                if (job.Graft == null) continue;
                if (job.Error != null || job.Result == null) { Plugin.Log.LogWarning("Klingon head: face sliders: " + (job.Error ?? "nothing made")); continue; }
                try
                {
                    var mesh = UnityEngine.Object.Instantiate(job.Source);
                    mesh.name = job.Source.name + " (face sliders)";
                    int added = 0;
                    for (int s = 0; s < job.Names.Length; s++)
                        if (mesh.GetBlendShapeIndex(job.Names[s]) < 0) { mesh.AddBlendShapeFrame(job.Names[s], 100f, job.Result[s], null, null); added++; }
                    job.Graft.UseMesh(mesh);
                    Plugin.Log.LogInfo($"Klingon head: the creator's face sliders work on it ({added} shapes from {job.HeadKey.Split('|')[0]}: {string.Join(", ", job.Names)})");
                }
                catch (Exception e) { Plugin.Log.LogWarning("Klingon head: putting the face sliders on: " + e.Message); }
            }
        }

        private static Human FromMemory(Mesh mesh, int headBone)
        {
            var bind = mesh.bindposes[headBone];
            var verts = mesh.vertices;
            var h = new Human { Verts = new Vector3[verts.Length], Names = new string[Shapes], Deltas = new Vector3[Shapes][] };
            for (int i = 0; i < verts.Length; i++) h.Verts[i] = bind.MultiplyPoint3x4(verts[i]);
            var dv = new Vector3[verts.Length];
            for (int s = 0; s < Shapes; s++)
            {
                int shape = mesh.blendShapeCount - Shapes + s;
                h.Names[s] = mesh.GetBlendShapeName(shape);
                mesh.GetBlendShapeFrameVertices(shape, mesh.GetBlendShapeFrameCount(shape) - 1, dv, null, null);
                h.Deltas[s] = new Vector3[verts.Length];
                for (int i = 0; i < verts.Length; i++) h.Deltas[s][i] = bind.MultiplyVector(dv[i]);
            }
            return h;
        }

        // The head's mesh from the game's asset files: the one with this name and vertex count.
        private static Human FromFiles(string dataDir, string name, int count, int headBone)
        {
            foreach (var path in Directory.GetFiles(dataDir).Where(p =>
            {
                var n = Path.GetFileName(p);
                return (n.StartsWith("sharedassets") && n.EndsWith(".assets")) || n == "resources.assets" || (n.StartsWith("level") && !n.Contains("."));
            }).OrderBy(p => Path.GetFileName(p) == "sharedassets1.assets" ? 0 : 1))
            {
                using (var file = new AssetFile(path))
                    foreach (var entry in file.Objects)
                    {
                        if (entry.ClassId != AssetFile.MeshClass || file.NameOf(entry) != name) continue;
                        var m = MeshAsset.Read(file.At(entry));
                        if (m.VertexCount != count || m.ShapeChannels.Count < Shapes || headBone >= m.BindPoses.Count) continue;
                        var b = m.BindPoses[headBone];
                        var bind = new Matrix4x4();
                        for (int row = 0; row < 4; row++) for (int col = 0; col < 4; col++) bind[row, col] = b[row * 4 + col];
                        var h = new Human { Verts = new Vector3[count], Names = new string[Shapes], Deltas = new Vector3[Shapes][] };
                        for (int i = 0; i < count; i++) { var p = m.Get(0, i); h.Verts[i] = bind.MultiplyPoint3x4(new Vector3(p[0], p[1], p[2])); }
                        for (int s = 0; s < Shapes; s++)
                        {
                            var channel = m.ShapeChannels[m.ShapeChannels.Count - Shapes + s];
                            h.Names[s] = channel.Name;
                            h.Deltas[s] = new Vector3[count];
                            var shape = m.Shapes[channel.FrameIndex + channel.FrameCount - 1];
                            for (uint k = shape.FirstVertex; k < shape.FirstVertex + shape.VertexCount; k++)
                            {
                                var sv = m.ShapeVertices[(int)k];
                                h.Deltas[s][sv.Index] = bind.MultiplyVector(new Vector3(sv.Vertex[0], sv.Vertex[1], sv.Vertex[2]));
                            }
                        }
                        return h;
                    }
            }
            throw new FileNotFoundException($"the head's mesh {name} ({count} vertices) isn't in the game's files");
        }

        // Each Klingon vertex follows the nearest head vertex: fully within 15% of the eye spacing (about a
        // centimetre), not at all beyond half of it.
        private static void Transfer(Job job, Human h)
        {
            float inner = job.Spacing * 0.15f, outer = job.Spacing * 0.5f, cell = outer;
            var grid = new Dictionary<long, List<int>>();
            long Key(int x, int y, int z) => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);
            for (int i = 0; i < h.Verts.Length; i++)
            {
                var v = h.Verts[i];
                long k = Key(Mathf.FloorToInt(v.x / cell), Mathf.FloorToInt(v.y / cell), Mathf.FloorToInt(v.z / cell));
                if (!grid.TryGetValue(k, out var list)) grid[k] = list = new List<int>();
                list.Add(i);
            }
            int n = job.KlingonVerts.Length;
            var result = new Vector3[Shapes][];
            for (int s = 0; s < Shapes; s++) result[s] = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                var p = job.Local.MultiplyPoint3x4(job.KlingonVerts[i]);
                int cx = Mathf.FloorToInt(p.x / cell), cy = Mathf.FloorToInt(p.y / cell), cz = Mathf.FloorToInt(p.z / cell);
                int best = -1; float bestD = outer * outer;
                for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
                    if (grid.TryGetValue(Key(cx + dx, cy + dy, cz + dz), out var list))
                        foreach (int j in list) { float d = (h.Verts[j] - p).sqrMagnitude; if (d < bestD) { bestD = d; best = j; } }
                if (best < 0) continue;
                float dist = Mathf.Sqrt(bestD);
                float w = dist <= inner ? 1f : 1f - (dist - inner) / (outer - inner);
                for (int s = 0; s < Shapes; s++)
                    result[s][i] = job.LocalInverse.MultiplyVector(h.Deltas[s][best]) * w;
            }
            job.Names = h.Names;
            job.Result = result;
        }
    }
}
