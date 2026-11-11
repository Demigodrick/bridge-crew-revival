using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using BepInEx;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StbcRevival
{
    /// <summary>
    /// Parts added to a bridge by its layout (BridgeLayout): a piece (or group) of any bridge, read from that bridge's
    /// scene file, or a model (.obj, with its .mtl and textures) from BepInEx/bridges/models. Only what's drawn is
    /// built (meshes and materials, no scripts or colliders), lit by the bridge's light probes like anything that
    /// moves. Placed as the layout says: turned about up around its pivot (its middle where its own bridge, or its
    /// model file, has it), scaled, and moved.
    ///
    /// The files are read on a background thread; the meshes, materials and textures are made on the main thread.
    /// Coordinates: a model's are taken as glTF's and the designer's (right-handed: z flipped for the game, triangles
    /// turned round).
    /// </summary>
    internal static class BridgeParts
    {
        internal const string RootName = "RevivalAddedPart";

        internal sealed class Spec
        {
            public string Id, Scene, Path, Model;
            public int Level = -1;
            public Vector3 Pivot, Move;
            public float Turn, Scale = 1f;
        }

        // What's read from the files for one part (background thread).
        private sealed class Drawn
        {
            public string Name;
            public bool InSceneSpace;          // a batched piece: its vertices are already where the scene has it
            public Matrix4x4 World;            // otherwise: its own space to the scene's
            public MeshAsset Mesh;
            public List<int> SubMeshes;
            public readonly List<KlingonHeadFiles.MaterialPlan> Materials = new List<KlingonHeadFiles.MaterialPlan>();
        }

        private sealed class Read
        {
            public Spec Spec;
            public List<Drawn> Drawn;
            public List<ObjModel.Part> ObjParts;
            public Dictionary<string, ObjModel.Material> ObjMaterials;
            public readonly Dictionary<string, byte[]> Images = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            public string Error;
        }

        private static readonly List<GameObject> built = new List<GameObject>();
        private static readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
        private static volatile List<Read> ready;
        private static Scene target;
        private static int generation;

        private static string ModelFolder => System.IO.Path.Combine(System.IO.Path.Combine(Paths.BepInExRootPath, "bridges"), "models");

        /// <summary>Starts reading the parts for this scene (what was built before goes).</summary>
        internal static void Start(List<Spec> specs, Scene scene)
        {
            Clear();
            if (specs.Count == 0) return;
            target = scene;
            int gen = ++generation;
            string dataDir = Application.dataPath, models = ModelFolder;
            new Thread(() =>
            {
                var results = new List<Read>();
                foreach (var spec in specs)
                {
                    var r = new Read { Spec = spec };
                    try
                    {
                        if (!string.IsNullOrEmpty(spec.Model)) ReadModel(r, models);
                        else ReadPiece(r, dataDir);
                    }
                    catch (Exception e) { r.Error = e.Message; }
                    results.Add(r);
                }
                if (gen == generation) ready = results;
            }) { IsBackground = true, Name = "Bridge parts" }.Start();
        }

        /// <summary>Every frame: builds what's been read.</summary>
        internal static void Update()
        {
            var results = ready;
            if (results == null) return;
            ready = null;
            if (!target.IsValid() || !target.isLoaded) return;
            int made = 0;
            foreach (var r in results)
            {
                if (r.Error != null) { Plugin.Log.LogWarning($"Bridge layout: added part {Describe(r.Spec)}: {r.Error}"); continue; }
                try { if (Build(r)) made++; }
                catch (Exception e) { Plugin.Log.LogWarning($"Bridge layout: building added part {Describe(r.Spec)}: {e}"); }
            }
            Plugin.Log.LogInfo($"Bridge layout: {made} of {results.Count} added part(s) built");
            try { Walking.WalkMesh.Clear(); } catch { }
        }

        internal static void Clear()
        {
            generation++;
            ready = null;
            foreach (var go in built) if (go != null) UnityEngine.Object.Destroy(go);
            foreach (var o in owned) if (o != null) UnityEngine.Object.Destroy(o);
            built.Clear();
            owned.Clear();
        }

        private static string Describe(Spec s) => s.Model != null ? "model " + s.Model : $"{s.Path} from {s.Scene}";

        // ------------------------------------------------------------------------------------------- reading
        private static void ReadPiece(Read r, string dataDir)
        {
            var spec = r.Spec;
            if (spec.Level < 0) throw new InvalidDataException("no level given for its bridge");
            var files = new Dictionary<string, AssetFile>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var scene = new AssetFile(System.IO.Path.Combine(dataDir, "level" + spec.Level));
                files["level" + spec.Level] = scene;
                AssetFile Open(AssetFile from, PPtr link)
                {
                    var name = from.FileOf(link);
                    if (name == null) return from;
                    if (!files.TryGetValue(name, out var f))
                    {
                        string at = System.IO.Path.Combine(dataDir, name);
                        if (!File.Exists(at)) at = System.IO.Path.Combine(System.IO.Path.Combine(dataDir, "Resources"), name);
                        files[name] = f = new AssetFile(at);
                    }
                    return f;
                }

                // Every transform: its object's name, whether it's on, its parent and its own placement.
                var names = new Dictionary<long, string>();
                var active = new Dictionary<long, bool>();
                var fathers = new Dictionary<long, long>();
                var locals = new Dictionary<long, Matrix4x4>();
                var gos = new Dictionary<long, long>();          // transform -> its GameObject
                var children = new Dictionary<long, List<long>>();
                foreach (var e in scene.Objects)
                {
                    if (e.ClassId != 4 && e.ClassId != 224) continue;
                    var t = TransformAsset.Read(scene.At(e));
                    var goEntry = scene.Find(t.GameObject.PathId);
                    if (goEntry == null) continue;
                    var gr = scene.At(goEntry);
                    names[e.PathId] = GameObjectAsset.Read(gr).Name;
                    gr.ReadUInt16();                         // tag
                    active[e.PathId] = gr.ReadBoolean();     // is active
                    long father = t.Father.IsNull ? 0 : t.Father.PathId;
                    fathers[e.PathId] = father;
                    gos[e.PathId] = t.GameObject.PathId;
                    locals[e.PathId] = Matrix4x4.TRS(new Vector3(t.Position[0], t.Position[1], t.Position[2]),
                        new Quaternion(t.Rotation[0], t.Rotation[1], t.Rotation[2], t.Rotation[3]), new Vector3(t.Scale[0], t.Scale[1], t.Scale[2]));
                    if (!children.TryGetValue(father, out var l)) children[father] = l = new List<long>();
                    l.Add(e.PathId);
                }
                string PathOf(long id)
                {
                    var sb = new StringBuilder();
                    for (int guard = 0; id != 0 && guard < 64; guard++)
                    {
                        if (!names.TryGetValue(id, out var n)) break;
                        sb.Insert(0, "/" + n);
                        fathers.TryGetValue(id, out id);
                    }
                    return sb.ToString();
                }
                Matrix4x4 World(long id)
                {
                    var m = Matrix4x4.identity;
                    for (int guard = 0; id != 0 && guard < 64; guard++) { m = locals[id] * m; fathers.TryGetValue(id, out id); }
                    return m;
                }
                bool On(long id)
                {
                    for (int guard = 0; id != 0 && guard < 64; guard++) { if (active.TryGetValue(id, out var a) && !a) return false; fathers.TryGetValue(id, out id); }
                    return true;
                }

                // The object: by path; if more than one, the nearest to the pivot.
                long found = 0;
                float best = float.MaxValue;
                foreach (var id in names.Keys)
                {
                    if (PathOf(id) != spec.Path) continue;
                    float d = ((Vector3)World(id).GetColumn(3) - spec.Pivot).sqrMagnitude;
                    if (d < best) { best = d; found = id; }
                }
                if (found == 0) throw new InvalidDataException($"{spec.Path} isn't in level{spec.Level}");

                r.Drawn = new List<Drawn>();
                var meshes = new Dictionary<string, MeshAsset>();
                void Collect(long id)
                {
                    if (active.TryGetValue(id, out var a) && !a) return;
                    var goEntry = scene.Find(gos[id]);
                    var go = GameObjectAsset.Read(scene.At(goEntry));
                    var rendererRef = go.Component(23);
                    var filterRef = go.Component(33);
                    if (!rendererRef.IsNull && !filterRef.IsNull)
                    {
                        var mr = MeshRendererAsset.Read(scene.At(scene.Find(rendererRef.PathId)));
                        var mf = MeshFilterAsset.Read(scene.At(scene.Find(filterRef.PathId)));
                        bool batched = mr.SubsetIndices.Count > 0 && mf.Mesh.FileId == 0 && mr.StaticBatchRoot.IsNull;
                        if (mr.Enabled && !mf.Mesh.IsNull)
                        {
                            var meshFile = Open(scene, mf.Mesh);
                            string key = System.IO.Path.GetFileName(meshFile.Path) + ":" + mf.Mesh.PathId;
                            if (!meshes.TryGetValue(key, out var mesh))
                            {
                                var me = meshFile.Find(mf.Mesh.PathId);
                                meshes[key] = mesh = me == null ? null : MeshAsset.Read(meshFile.At(me));
                            }
                            if (mesh != null && mesh.VertexData.Length > 0)
                            {
                                var d = new Drawn { Name = names[id], Mesh = mesh, InSceneSpace = batched, World = batched ? Matrix4x4.identity : World(id) };
                                d.SubMeshes = new List<int>();
                                if (batched) foreach (var i in mr.SubsetIndices) d.SubMeshes.Add((int)i);
                                else for (int i = 0; i < mesh.SubMeshes.Count; i++) d.SubMeshes.Add(i);
                                foreach (var link in mr.Materials)
                                {
                                    KlingonHeadFiles.MaterialPlan plan = null;
                                    try { if (!link.IsNull) plan = KlingonHeadFiles.ReadMaterial(dataDir, Open, scene, link); } catch { }
                                    d.Materials.Add(plan);
                                }
                                r.Drawn.Add(d);
                            }
                        }
                    }
                    if (children.TryGetValue(id, out var kids)) foreach (var k in kids) Collect(k);
                }
                if (!On(found)) throw new InvalidDataException($"{spec.Path} is switched off in its bridge");
                Collect(found);
                if (r.Drawn.Count == 0) throw new InvalidDataException($"nothing drawn under {spec.Path}");
            }
            finally { foreach (var f in files.Values) f.Dispose(); }
        }

        private static void ReadModel(Read r, string folder)
        {
            string file = System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, r.Spec.Model));
            if (!file.StartsWith(System.IO.Path.GetFullPath(folder))) throw new InvalidDataException("not in the models folder");
            if (!File.Exists(file)) throw new FileNotFoundException("not in " + folder);
            r.ObjParts = ObjModel.Read(file, out var mats);
            r.ObjMaterials = mats;
            string dir = System.IO.Path.GetDirectoryName(file);
            foreach (var m in mats.Values)
                foreach (var name in new[] { m.Diffuse, m.Emission })
                {
                    if (name == null || r.Images.ContainsKey(name)) continue;
                    string stem = System.IO.Path.GetFileNameWithoutExtension(name);
                    foreach (var candidate in new[] { name, stem + ".png", stem + ".jpg", stem + ".jpeg" })
                    {
                        var at = System.IO.Path.Combine(dir, candidate);
                        if (File.Exists(at)) { r.Images[name] = File.ReadAllBytes(at); break; }
                    }
                }
            if (r.ObjParts.Count == 0) throw new InvalidDataException("no meshes in the model");
        }

        // ------------------------------------------------------------------------------------------- building
        private static bool Build(Read r)
        {
            var spec = r.Spec;
            var root = new GameObject(RootName + " " + spec.Id);
            SceneManager.MoveGameObjectToScene(root, target);
            built.Add(root);
            int pieces = 0;
            if (r.Drawn != null)
                foreach (var d in r.Drawn)
                {
                    var mesh = BatchedPieces.SliceMesh(d.Mesh, d.SubMeshes, RootName + " " + d.Name);
                    if (mesh == null) continue;
                    owned.Add(mesh);
                    var go = new GameObject(d.Name);
                    go.transform.SetParent(root.transform, false);
                    if (!d.InSceneSpace)
                    {
                        var m = d.World;
                        go.transform.localPosition = m.GetColumn(3);
                        go.transform.localRotation = Quaternion.LookRotation(m.GetColumn(2), m.GetColumn(1));
                        go.transform.localScale = new Vector3(m.GetColumn(0).magnitude, m.GetColumn(1).magnitude, m.GetColumn(2).magnitude);
                    }
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    var mats = new Material[Math.Max(1, d.Materials.Count)];
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var plan = i < d.Materials.Count ? d.Materials[i] : null;
                        Material made = null;
                        try { made = plan != null ? KlingonHeadFiles.MakeMaterial(plan) ?? Fallback(plan) : null; } catch { made = plan != null ? Fallback(plan) : null; }
                        mats[i] = made ?? Plain(Color.gray, null);
                    }
                    mr.sharedMaterials = mats;
                    mr.lightmapIndex = -1; // not in the bridge's baked light: the light probes light it, as anything that moves
                    pieces++;
                }
            if (r.ObjParts != null)
            {
                var textures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
                Texture2D Tex(string name)
                {
                    if (name == null || !r.Images.TryGetValue(name, out var bytes)) return null;
                    if (textures.TryGetValue(name, out var t)) return t;
                    t = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = name };
                    if (!t.LoadImage(bytes)) { UnityEngine.Object.Destroy(t); return textures[name] = null; }
                    t.Apply(true, true);
                    owned.Add(t);
                    return textures[name] = t;
                }
                var byName = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
                foreach (var part in r.ObjParts)
                {
                    // The model's space is right-handed (as the designer shows it): z flipped, triangles turned round.
                    var v = new Vector3[part.Vertices.Length];
                    var n = new Vector3[part.Normals.Length];
                    for (int i = 0; i < v.Length; i++) v[i] = new Vector3(part.Vertices[i].x, part.Vertices[i].y, -part.Vertices[i].z);
                    bool normals = false;
                    for (int i = 0; i < n.Length; i++) { n[i] = new Vector3(part.Normals[i].x, part.Normals[i].y, -part.Normals[i].z); if (n[i] != Vector3.zero) normals = true; }
                    var t = (int[])part.Triangles.Clone();
                    for (int i = 0; i + 2 < t.Length; i += 3) { int x = t[i + 1]; t[i + 1] = t[i + 2]; t[i + 2] = x; }
                    var mesh = new Mesh { name = RootName + " " + part.Name, vertices = v, uv = part.Uv, triangles = t };
                    if (normals) mesh.normals = n; else mesh.RecalculateNormals();
                    mesh.RecalculateBounds();
                    owned.Add(mesh);
                    if (!byName.TryGetValue(part.Texture ?? "", out var mat))
                    {
                        r.ObjMaterials.TryGetValue(part.Texture ?? "", out var om);
                        mat = Plain(om != null ? om.Colour : Color.gray, Tex(om?.Diffuse));
                        if (om != null && (om.Emission != null || om.Glow.maxColorComponent > 0.01f))
                        {
                            var glow = Tex(om.Emission);
                            mat.SetColor("_EmissionColor", glow != null ? Color.white : om.Glow);
                            if (glow != null) mat.SetTexture("_EmissionMap", glow);
                            mat.EnableKeyword("_EMISSION");
                        }
                        byName[part.Texture ?? ""] = mat;
                    }
                    var go = new GameObject(part.Name);
                    go.transform.SetParent(root.transform, false);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    go.AddComponent<MeshRenderer>().sharedMaterial = mat;
                    pieces++;
                }
            }
            if (pieces == 0) { UnityEngine.Object.Destroy(root); built.Remove(root); Plugin.Log.LogWarning($"Bridge layout: added part {Describe(spec)}: nothing to draw"); return false; }
            // Turned about up around the pivot, scaled, then moved (as the designer places it).
            float s = spec.Scale > 0.001f ? spec.Scale : 1f;
            Quaternion turn = Quaternion.AngleAxis(spec.Turn, Vector3.up);
            root.transform.localScale = Vector3.one * s;
            root.transform.rotation = turn;
            root.transform.position = spec.Pivot + spec.Move - turn * (spec.Pivot * s);
            Plugin.Log.LogInfo($"Bridge layout: added {Describe(spec)} ({pieces} piece(s))");
            return true;
        }

        // A material whose shader isn't loaded (from another bridge): the Standard shader with its colour, texture,
        // normal map and glow.
        private static Material Fallback(KlingonHeadFiles.MaterialPlan plan)
        {
            var m = Plain(Color.white, null);
            m.name = plan.Material.Name;
            foreach (var c in plan.Material.Colors)
                if (c.Key == "_Color") m.color = new Color(c.Value[0], c.Value[1], c.Value[2], c.Value[3]);
            // Its textures by their usual names, made through the same path as the material would (they're cached there).
            var probe = new KlingonHeadFiles.MaterialPlan { Material = plan.Material, Shader = "Standard" };
            probe.Textures.AddRange(plan.Textures);
            Material made = null;
            try { made = KlingonHeadFiles.MakeMaterial(probe); } catch { }
            if (made != null)
            {
                owned.Add(made);
                foreach (var name in new[] { "_MainTex", "_BumpMap", "_EmissionMap" })
                    if (made.HasProperty(name) && made.GetTexture(name) != null) m.SetTexture(name, made.GetTexture(name));
                if (m.GetTexture("_BumpMap") != null) m.EnableKeyword("_NORMALMAP");
                if (plan.Material.Keywords.Contains("_EMISSION"))
                {
                    m.EnableKeyword("_EMISSION");
                    foreach (var c in plan.Material.Colors)
                        if (c.Key == "_EmissionColor") m.SetColor("_EmissionColor", new Color(c.Value[0], c.Value[1], c.Value[2], c.Value[3]));
                }
            }
            return m;
        }

        private static Material Plain(Color colour, Texture2D texture)
        {
            var m = new Material(Shader.Find("Standard")) { name = RootName, color = colour };
            if (texture != null) m.mainTexture = texture;
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.3f);
            owned.Add(m);
            return m;
        }
    }
}
