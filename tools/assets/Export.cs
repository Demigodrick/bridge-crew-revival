// glb: a bridge scene, out of the game's files, as a binary glTF (.glb) any 3D viewer opens. The spike for a
// standalone bridge designer: does a bridge come out whole and textured with the mod's reader?
//
// Every drawn object (MeshRenderer) is a node of its own, named by its path in the scene, so a designer can pick
// and move it. Statically batched objects are cut out of their combined mesh (whose vertices are in the scene's
// space). Each material's main texture and emission map are decoded and embedded as PNG. Unity is left-handed
// (z forward), glTF right-handed: z is flipped and triangles turned round. Nothing from the game is stored in
// the repository: the .glb is written where it's asked for.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using StbcRevival;

internal static class GlbExport
{
    private const int MaxTexture = 1024; // larger textures are halved down to this, to keep the file small

    public static int Run(string dataDir, string sceneFile, string outPath)
    {
        var x = new Exporter(dataDir, sceneFile);
        x.Export();
        x.Write(outPath);
        return 0;
    }

    private sealed class Exporter
    {
        private readonly string dataDir;
        private readonly AssetFile scene;
        private readonly Dictionary<string, AssetFile> files = new();
        private readonly Dictionary<(string, long), MeshAsset> meshes = new();
        private readonly Dictionary<(string, long), int> materialIndex = new();
        private readonly Dictionary<(string, long), int> textureIndex = new();
        private readonly Dictionary<(string, long, int, int), int> meshIndex = new(); // mesh, sub-mesh set, material set
        private readonly MemoryStream bin = new();
        private readonly JsonArray nodes = new(), gmeshes = new(), accessors = new(), views = new(), materials = new(), textures = new(), images = new();
        private readonly JsonArray roots = new();
        private int drawn, skippedInactive, skippedCompressed, skippedOther, batched, textureFails;
        private readonly SortedDictionary<string, int> problems = new();
        private Vector3 sceneMin = new(float.MaxValue), sceneMax = new(float.MinValue);

        public Exporter(string dataDir, string sceneFile)
        {
            this.dataDir = dataDir;
            scene = new AssetFile(Path.Combine(dataDir, sceneFile));
            files[sceneFile] = scene;
        }

        private void Problem(string what) => problems[what] = problems.TryGetValue(what, out var n) ? n + 1 : 1;

        // ------------------------------------------------------------------------------------------- files
        private (AssetFile file, string name) Open(AssetFile from, PPtr link)
        {
            if (link.FileId == 0) return (from, Path.GetFileName(from.Path));
            var name = from.FileOf(link);
            // Unity's built-in meshes and textures are in Resources/ ("unity default resources").
            string at = File.Exists(Path.Combine(dataDir, name)) ? Path.Combine(dataDir, name) : Path.Combine(dataDir, "Resources", name);
            if (!files.TryGetValue(name, out var f)) files[name] = f = new AssetFile(at);
            return (f, name);
        }

        // ------------------------------------------------------------------------------------------- the scene
        private sealed class Tr
        {
            public long Id, Father, Go;
            public float[] Pos, Rot, Scale;
            public string Name;
            public bool Active;
            public List<KeyValuePair<int, PPtr>> Components;
        }

        public void Export()
        {
            var trs = new Dictionary<long, Tr>();
            foreach (var e in scene.Objects)
            {
                if (e.ClassId != 4 && e.ClassId != 224) continue;
                var t = TransformAsset.Read(scene.At(e));
                var goEntry = scene.Find(t.GameObject.PathId);
                if (goEntry == null) continue;
                var r = scene.At(goEntry);
                var go = GameObjectAsset.Read(r);
                r.ReadUInt16();                 // tag
                bool active = r.ReadBoolean();  // is active
                trs[e.PathId] = new Tr { Id = e.PathId, Father = t.Father.IsNull ? 0 : t.Father.PathId, Go = t.GameObject.PathId, Pos = t.Position, Rot = t.Rotation, Scale = t.Scale, Name = go.Name, Active = active, Components = go.Components };
            }
            var world = new Dictionary<long, Matrix4x4>();
            Matrix4x4 World(Tr t)
            {
                if (world.TryGetValue(t.Id, out var m)) return m;
                var local = Matrix4x4.CreateScale(t.Scale[0], t.Scale[1], t.Scale[2])
                            * Matrix4x4.CreateFromQuaternion(new Quaternion(t.Rot[0], t.Rot[1], t.Rot[2], t.Rot[3]))
                            * Matrix4x4.CreateTranslation(t.Pos[0], t.Pos[1], t.Pos[2]);
                m = t.Father != 0 && trs.TryGetValue(t.Father, out var f) ? local * World(f) : local;
                return world[t.Id] = m;
            }
            bool ActiveInHierarchy(Tr t)
            {
                for (var a = t; a != null; a = a.Father != 0 && trs.TryGetValue(a.Father, out var f) ? f : null) if (!a.Active) return false;
                return true;
            }
            string PathOf(Tr t)
            {
                var sb = new StringBuilder();
                for (var a = t; a != null; a = a.Father != 0 && trs.TryGetValue(a.Father, out var f) ? f : null) sb.Insert(0, "/" + a.Name);
                return sb.ToString();
            }

            foreach (var t in trs.Values)
            {
                PPtr rendererRef = default, filterRef = default;
                foreach (var c in t.Components) { if (c.Key == 23) rendererRef = c.Value; if (c.Key == 33) filterRef = c.Value; }
                if (rendererRef.IsNull || filterRef.IsNull) continue;
                var mr = MeshRendererAsset.Read(scene.At(scene.Find(rendererRef.PathId)));
                var mf = MeshFilterAsset.Read(scene.At(scene.Find(filterRef.PathId)));
                if (!mr.Enabled || !ActiveInHierarchy(t)) { skippedInactive++; continue; }
                if (mf.Mesh.IsNull) { skippedOther++; Problem("no mesh"); continue; }
                string path = PathOf(t);
                try
                {
                    bool isBatched = mr.SubsetIndices.Count > 0;
                    if (isBatched && !mr.StaticBatchRoot.IsNull) { skippedOther++; Problem("batched under a root of its own"); continue; }
                    var (mfile, mname) = Open(scene, mf.Mesh);
                    var mesh = Mesh(mfile, mname, mf.Mesh.PathId);
                    if (mesh == null) { skippedOther++; Problem("mesh missing"); continue; }
                    if (mesh.VertexData.Length == 0) { skippedCompressed++; Problem("compressed mesh: " + mesh.Name); continue; }
                    // Batched: its sub-meshes of the combined mesh, already in the scene's space. Otherwise all of
                    // the mesh's sub-meshes, in the object's own space.
                    var subs = isBatched ? mr.SubsetIndices.Select(i => (int)i).ToList() : Enumerable.Range(0, mesh.SubMeshes.Count).ToList();
                    int gm = GltfMesh(mname, mf.Mesh.PathId, mesh, subs, mr.Materials, path);
                    if (gm < 0) { skippedOther++; continue; }
                    var node = new JsonObject { ["name"] = path, ["mesh"] = gm };
                    var m = isBatched ? Matrix4x4.Identity : World(t);
                    if (!m.IsIdentity) node["matrix"] = Matrix(Flip(m));
                    node["extras"] = new JsonObject { ["batched"] = isBatched };
                    roots.Add(nodes.Count);
                    nodes.Add(node);
                    drawn++;
                    if (isBatched) batched++;
                }
                catch (Exception ex) { skippedOther++; Problem(ex.GetType().Name + ": " + ex.Message); }
            }
        }

        private static readonly Matrix4x4 FlipZ = Matrix4x4.CreateScale(1, 1, -1);
        private static Matrix4x4 Flip(Matrix4x4 m) => FlipZ * m * FlipZ;

        // System.Numerics is row-vector: its row-major order is glTF's column-major one.
        private static JsonArray Matrix(Matrix4x4 m) => new(
            m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);

        private MeshAsset Mesh(AssetFile file, string name, long id)
        {
            if (meshes.TryGetValue((name, id), out var m)) return m;
            var e = file.Find(id);
            m = e == null ? null : MeshAsset.Read(file.At(e));
            return meshes[(name, id)] = m;
        }

        // ------------------------------------------------------------------------------------------- meshes
        private int GltfMesh(string file, long id, MeshAsset mesh, List<int> subs, List<PPtr> mats, string path)
        {
            // The same mesh drawn with the same materials is written once (sub-mesh and material sets hashed).
            int subKey = subs.Aggregate(17, (h, s) => h * 31 + s);
            int matKey = mats.Aggregate(17, (h, p) => h * 31 + p.PathId.GetHashCode() + p.FileId);
            if (meshIndex.TryGetValue((file, id, subKey, matKey), out var done)) return done;
            var prims = new JsonArray();
            for (int k = 0; k < subs.Count; k++)
            {
                int s = subs[k];
                if (s < 0 || s >= mesh.SubMeshes.Count) { Problem("sub-mesh out of range"); continue; }
                var sub = mesh.SubMeshes[s];
                if (sub.Topology != 0) { Problem("not triangles (topology " + sub.Topology + ")"); continue; }
                // Unity's materials beyond the sub-meshes repeat on the last; fewer: the sub-mesh draws with the last too.
                var matRef = mats.Count == 0 ? default : mats[Math.Min(k, mats.Count - 1)];
                var (scale, offset) = (new[] { 1f, 1f }, new[] { 0f, 0f });
                int mat = matRef.IsNull ? -1 : Material(scene, matRef, ref scale, ref offset);
                var prim = Primitive(mesh, sub, scale, offset);
                if (prim == null) continue;
                if (mat >= 0) prim["material"] = mat;
                prims.Add(prim);
            }
            if (prims.Count == 0) return meshIndex[(file, id, subKey, matKey)] = -1;
            int index = gmeshes.Count;
            gmeshes.Add(new JsonObject { ["name"] = mesh.Name, ["primitives"] = prims });
            return meshIndex[(file, id, subKey, matKey)] = index;
        }

        private JsonObject Primitive(MeshAsset m, MeshAsset.SubMesh sub, float[] uvScale, float[] uvOffset)
        {
            var map = new Dictionary<int, int>();
            var order = new List<int>();
            var tris = new int[sub.IndexCount];
            for (int i = 0; i < tris.Length; i++)
            {
                int v = BinaryPrimitives.ReadUInt16LittleEndian(m.IndexBuffer.AsSpan((int)sub.FirstByte + i * 2));
                if (!map.TryGetValue(v, out var at)) { at = map[v] = order.Count; order.Add(v); }
                tris[i] = at;
            }
            if (order.Count == 0) return null;
            // z flipped: each triangle turned round so it still faces out.
            for (int i = 0; i + 2 < tris.Length; i += 3) (tris[i + 1], tris[i + 2]) = (tris[i + 2], tris[i + 1]);
            var pos = new float[order.Count * 3];
            var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
            for (int i = 0; i < order.Count; i++)
            {
                var p = m.Get(0, order[i]);
                pos[i * 3] = p[0]; pos[i * 3 + 1] = p[1]; pos[i * 3 + 2] = -p[2];
                var v = new Vector3(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]);
                min = Vector3.Min(min, v); max = Vector3.Max(max, v);
            }
            sceneMin = Vector3.Min(sceneMin, min); sceneMax = Vector3.Max(sceneMax, max);
            var attrs = new JsonObject { ["POSITION"] = Accessor(pos, 3, min, max) };
            if (m.Has(1))
            {
                var n = new float[order.Count * 3];
                for (int i = 0; i < order.Count; i++)
                {
                    var q = m.Get(1, order[i]);
                    var v = Vector3.Normalize(new Vector3(q[0], q[1], -q[2]));
                    if (float.IsNaN(v.X)) v = Vector3.UnitY;
                    n[i * 3] = v.X; n[i * 3 + 1] = v.Y; n[i * 3 + 2] = v.Z;
                }
                attrs["NORMAL"] = Accessor(n, 3);
            }
            if (m.Has(3))
            {
                // The material's tiling and offset baked in, and v turned over (Unity's textures start at the bottom).
                var uv = new float[order.Count * 2];
                for (int i = 0; i < order.Count; i++)
                {
                    var q = m.Get(3, order[i]);
                    uv[i * 2] = q[0] * uvScale[0] + uvOffset[0];
                    uv[i * 2 + 1] = 1f - (q[1] * uvScale[1] + uvOffset[1]);
                }
                attrs["TEXCOORD_0"] = Accessor(uv, 2);
            }
            int idx = Indices(tris, order.Count);
            return new JsonObject { ["attributes"] = attrs, ["indices"] = idx, ["mode"] = 4 };
        }

        // ------------------------------------------------------------------------------------------- materials
        private int Material(AssetFile from, PPtr link, ref float[] uvScale, ref float[] uvOffset)
        {
            var (file, fname) = Open(from, link);
            var e = file.Find(link.PathId);
            if (e == null) { Problem("material missing"); return -1; }
            var m = MaterialAsset.Read(file.At(e));
            var main = m.Textures.FirstOrDefault(t => t.Name == "_MainTex");
            if (main != null) { uvScale = main.Scale; uvOffset = main.Offset; }
            if (materialIndex.TryGetValue((fname, link.PathId), out var done)) return done;

            float[] Color(string name, float[] or) => m.Colors.FirstOrDefault(c => c.Key == name).Value ?? or;
            float Float(string name, float or) => m.Floats.Any(f => f.Key == name) ? m.Floats.First(f => f.Key == name).Value : or;
            var color = Color("_Color", new[] { 1f, 1f, 1f, 1f });
            var pbr = new JsonObject
            {
                ["baseColorFactor"] = new JsonArray(Lin(color[0]), Lin(color[1]), Lin(color[2]), color[3]),
                ["metallicFactor"] = Math.Clamp(Float("_Metallic", 0f), 0f, 1f),
                ["roughnessFactor"] = Math.Clamp(1f - Float("_Glossiness", 0.3f), 0.05f, 1f),
            };
            if (main != null && !main.Texture.IsNull) { int t = Texture(file, main.Texture); if (t >= 0) pbr["baseColorTexture"] = new JsonObject { ["index"] = t }; }
            var mat = new JsonObject { ["name"] = m.Name, ["pbrMetallicRoughness"] = pbr };
            string kw = m.Keywords ?? "";
            if (kw.Contains("_EMISSION"))
            {
                var em = Color("_EmissionColor", new[] { 0f, 0f, 0f, 1f });
                float peak = Math.Max(1f, Math.Max(em[0], Math.Max(em[1], em[2])));
                mat["emissiveFactor"] = new JsonArray(Lin(em[0] / peak), Lin(em[1] / peak), Lin(em[2] / peak));
                var emTex = m.Textures.FirstOrDefault(t => t.Name == "_EmissionMap");
                if (emTex != null && !emTex.Texture.IsNull) { int t = Texture(file, emTex.Texture); if (t >= 0) mat["emissiveTexture"] = new JsonObject { ["index"] = t }; }
            }
            if (kw.Contains("_ALPHATEST_ON")) { mat["alphaMode"] = "MASK"; mat["alphaCutoff"] = Float("_Cutoff", 0.5f); }
            else if (kw.Contains("_ALPHABLEND_ON") || kw.Contains("_ALPHAPREMULTIPLY_ON") || m.RenderQueue >= 3000) mat["alphaMode"] = "BLEND";
            mat["extras"] = new JsonObject { ["shader"] = ShaderName(file, m.Shader), ["keywords"] = kw, ["queue"] = m.RenderQueue };
            int index = materials.Count;
            materials.Add(mat);
            return materialIndex[(fname, link.PathId)] = index;
        }

        // Unity's colours here are in gamma space (the game renders in gamma); glTF's factors are linear.
        private static float Lin(float c) => (float)Math.Pow(Math.Clamp(c, 0f, 1f), 2.2);

        private string ShaderName(AssetFile from, PPtr link)
        {
            try
            {
                var (file, _) = Open(from, link);
                var e = file.Find(link.PathId);
                if (e == null) return "?";
                var r = file.At(e);
                AssetFile.ReadString(r);
                var script = AssetFile.ReadString(r);
                int open = script.IndexOf('"'), close = open < 0 ? -1 : script.IndexOf('"', open + 1);
                return close < 0 ? "?" : script.Substring(open + 1, close - open - 1);
            }
            catch { return "?"; }
        }

        // ------------------------------------------------------------------------------------------- textures
        private int Texture(AssetFile from, PPtr link)
        {
            var (file, fname) = Open(from, link);
            if (textureIndex.TryGetValue((fname, link.PathId), out var done)) return done;
            int index = -1;
            try
            {
                var e = file.Find(link.PathId);
                if (e == null || e.ClassId != 28) { Problem("texture missing or not a 2D texture"); }
                else
                {
                    var t = TextureAsset.Read(file.At(e));
                    var data = t.Data.Length > 0 ? t.Data : Stream(t);
                    var rgba = Decode(t, data, out int w, out int h);
                    if (rgba == null) { textureFails++; Problem("texture format " + t.Format); }
                    else
                    {
                        while (w > MaxTexture || h > MaxTexture) (rgba, w, h) = Half(rgba, w, h);
                        var png = Png(rgba, w, h);
                        int view = View(png, null);
                        images.Add(new JsonObject { ["name"] = t.Name, ["bufferView"] = view, ["mimeType"] = "image/png" });
                        index = textures.Count;
                        textures.Add(new JsonObject { ["source"] = images.Count - 1, ["sampler"] = 0 });
                    }
                }
            }
            catch (Exception ex) { textureFails++; Problem("texture: " + ex.Message); }
            return textureIndex[(fname, link.PathId)] = index;
        }

        private byte[] Stream(TextureAsset t)
        {
            using var s = new FileStream(Path.Combine(dataDir, t.StreamPath), FileMode.Open, FileAccess.Read, FileShare.Read);
            s.Position = t.StreamOffset;
            var bytes = new byte[t.StreamSize];
            s.ReadExactly(bytes);
            return bytes;
        }

        /// <summary>The top mip as RGBA, rows bottom-up as Unity keeps them; null for a format not decoded here.</summary>
        private static byte[] Decode(TextureAsset t, byte[] d, out int w, out int h)
        {
            w = t.Width; h = t.Height;
            var o = new byte[w * h * 4];
            switch (t.Format)
            {
                case 10: Dxt(d, w, h, o, false); return o;  // DXT1
                case 12: Dxt(d, w, h, o, true); return o;   // DXT5
                case 1:  for (int i = 0; i < w * h; i++) { o[i * 4] = o[i * 4 + 1] = o[i * 4 + 2] = 255; o[i * 4 + 3] = d[i]; } return o; // Alpha8
                case 3:  for (int i = 0; i < w * h; i++) { o[i * 4] = d[i * 3]; o[i * 4 + 1] = d[i * 3 + 1]; o[i * 4 + 2] = d[i * 3 + 2]; o[i * 4 + 3] = 255; } return o; // RGB24
                case 4:  Array.Copy(d, o, o.Length); return o; // RGBA32
                case 5:  for (int i = 0; i < w * h; i++) { o[i * 4] = d[i * 4 + 1]; o[i * 4 + 1] = d[i * 4 + 2]; o[i * 4 + 2] = d[i * 4 + 3]; o[i * 4 + 3] = d[i * 4]; } return o; // ARGB32
                case 14: for (int i = 0; i < w * h; i++) { o[i * 4] = d[i * 4 + 2]; o[i * 4 + 1] = d[i * 4 + 1]; o[i * 4 + 2] = d[i * 4]; o[i * 4 + 3] = d[i * 4 + 3]; } return o; // BGRA32
                default: return null;
            }
        }

        private static void Dxt(byte[] d, int w, int h, byte[] o, bool dxt5)
        {
            int bw = (w + 3) / 4, bh = (h + 3) / 4, size = dxt5 ? 16 : 8;
            var alpha = new byte[16];
            var av = new byte[8];
            var rgb = new byte[12];
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++)
                {
                    int at = (by * bw + bx) * size;
                    if (at + size > d.Length) return;
                    if (dxt5)
                    {
                        byte a0 = d[at], a1 = d[at + 1];
                        av[0] = a0; av[1] = a1;
                        if (a0 > a1) for (int i = 1; i < 7; i++) av[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
                        else { for (int i = 1; i < 5; i++) av[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5); av[6] = 0; av[7] = 255; }
                        ulong bits = 0;
                        for (int i = 0; i < 6; i++) bits |= (ulong)d[at + 2 + i] << (8 * i);
                        for (int i = 0; i < 16; i++) alpha[i] = av[(int)((bits >> (3 * i)) & 7)];
                        at += 8;
                    }
                    else Array.Fill(alpha, (byte)255);
                    ushort c0 = (ushort)(d[at] | d[at + 1] << 8), c1 = (ushort)(d[at + 2] | d[at + 3] << 8);
                    void C(int k, int r, int g, int b) { rgb[k * 3] = (byte)r; rgb[k * 3 + 1] = (byte)g; rgb[k * 3 + 2] = (byte)b; }
                    int R(ushort c) => (c >> 11 & 31) * 255 / 31; int G(ushort c) => (c >> 5 & 63) * 255 / 63; int B(ushort c) => (c & 31) * 255 / 31;
                    C(0, R(c0), G(c0), B(c0)); C(1, R(c1), G(c1), B(c1));
                    bool four = dxt5 || c0 > c1;
                    if (four) { C(2, (2 * R(c0) + R(c1)) / 3, (2 * G(c0) + G(c1)) / 3, (2 * B(c0) + B(c1)) / 3); C(3, (R(c0) + 2 * R(c1)) / 3, (G(c0) + 2 * G(c1)) / 3, (B(c0) + 2 * B(c1)) / 3); }
                    else { C(2, (R(c0) + R(c1)) / 2, (G(c0) + G(c1)) / 2, (B(c0) + B(c1)) / 2); C(3, 0, 0, 0); }
                    uint idx = (uint)(d[at + 4] | d[at + 5] << 8 | d[at + 6] << 16 | d[at + 7] << 24);
                    for (int py = 0; py < 4; py++)
                        for (int px = 0; px < 4; px++)
                        {
                            int x = bx * 4 + px, y = by * 4 + py;
                            if (x >= w || y >= h) continue;
                            int i = py * 4 + px, k = (int)(idx >> (2 * i) & 3), p = (y * w + x) * 4;
                            o[p] = rgb[k * 3]; o[p + 1] = rgb[k * 3 + 1]; o[p + 2] = rgb[k * 3 + 2];
                            o[p + 3] = !four && k == 3 ? (byte)0 : alpha[i];
                        }
                }
        }

        private static (byte[], int, int) Half(byte[] s, int w, int h)
        {
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            var o = new byte[nw * nh * 4];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                    for (int c = 0; c < 4; c++)
                    {
                        int sx = Math.Min(x * 2, w - 1), sy = Math.Min(y * 2, h - 1), sx1 = Math.Min(sx + 1, w - 1), sy1 = Math.Min(sy + 1, h - 1);
                        o[(y * nw + x) * 4 + c] = (byte)((s[(sy * w + sx) * 4 + c] + s[(sy * w + sx1) * 4 + c] + s[(sy1 * w + sx) * 4 + c] + s[(sy1 * w + sx1) * 4 + c]) / 4);
                    }
            return (o, nw, nh);
        }

        // A plain RGBA PNG. Rows go in as stored (bottom-up); the UVs' v is turned over to match.
        private static byte[] Png(byte[] rgba, int w, int h)
        {
            using var ms = new MemoryStream();
            ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            var ihdr = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(ihdr, w); BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
            ihdr[8] = 8; ihdr[9] = 6;
            Chunk(ms, "IHDR", ihdr);
            using (var raw = new MemoryStream())
            {
                using (var z = new ZLibStream(raw, CompressionLevel.Fastest, true))
                    for (int y = 0; y < h; y++) { z.WriteByte(0); z.Write(rgba, y * w * 4, w * 4); }
                Chunk(ms, "IDAT", raw.ToArray());
            }
            Chunk(ms, "IEND", Array.Empty<byte>());
            return ms.ToArray();
        }

        private static void Chunk(Stream s, string type, byte[] data)
        {
            Span<byte> b4 = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(b4, data.Length); s.Write(b4);
            var t = Encoding.ASCII.GetBytes(type);
            s.Write(t); s.Write(data);
            uint crc = Crc(Crc(0xFFFFFFFF, t), data) ^ 0xFFFFFFFF;
            BinaryPrimitives.WriteUInt32BigEndian(b4, crc); s.Write(b4);
        }

        private static uint[] crcTable;
        private static uint Crc(uint c, byte[] data)
        {
            if (crcTable == null)
            {
                crcTable = new uint[256];
                for (uint n = 0; n < 256; n++) { uint k = n; for (int i = 0; i < 8; i++) k = (k & 1) != 0 ? 0xEDB88320 ^ (k >> 1) : k >> 1; crcTable[n] = k; }
            }
            foreach (var b in data) c = crcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            return c;
        }

        // ------------------------------------------------------------------------------------------- buffers
        private int View(byte[] data, int? target)
        {
            while (bin.Length % 4 != 0) bin.WriteByte(0);
            var v = new JsonObject { ["buffer"] = 0, ["byteOffset"] = bin.Length, ["byteLength"] = data.Length };
            if (target != null) v["target"] = target.Value;
            bin.Write(data);
            views.Add(v);
            return views.Count - 1;
        }

        private int Accessor(float[] values, int dim, Vector3? min = null, Vector3? max = null)
        {
            var bytes = new byte[values.Length * 4];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            var a = new JsonObject { ["bufferView"] = View(bytes, 34962), ["componentType"] = 5126, ["count"] = values.Length / dim, ["type"] = dim == 2 ? "VEC2" : "VEC3" };
            if (min != null) { a["min"] = new JsonArray(min.Value.X, min.Value.Y, min.Value.Z); a["max"] = new JsonArray(max.Value.X, max.Value.Y, max.Value.Z); }
            accessors.Add(a);
            return accessors.Count - 1;
        }

        private int Indices(int[] tris, int vertexCount)
        {
            byte[] bytes;
            int type;
            if (vertexCount <= 65535) { bytes = new byte[tris.Length * 2]; for (int i = 0; i < tris.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), (ushort)tris[i]); type = 5123; }
            else { bytes = new byte[tris.Length * 4]; Buffer.BlockCopy(tris, 0, bytes, 0, bytes.Length); type = 5125; }
            accessors.Add(new JsonObject { ["bufferView"] = View(bytes, 34963), ["componentType"] = type, ["count"] = tris.Length, ["type"] = "SCALAR" });
            return accessors.Count - 1;
        }

        // ------------------------------------------------------------------------------------------- writing
        public void Write(string outPath)
        {
            while (bin.Length % 4 != 0) bin.WriteByte(0);
            var gltf = new JsonObject
            {
                ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "Bridge Crew Revival tools/assets glb" },
                ["scene"] = 0,
                ["scenes"] = new JsonArray(new JsonObject { ["name"] = Path.GetFileName(scene.Path), ["nodes"] = roots }),
                ["nodes"] = nodes, ["meshes"] = gmeshes, ["accessors"] = accessors, ["bufferViews"] = views,
                ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = bin.Length }),
            };
            if (materials.Count > 0) gltf["materials"] = materials;
            if (textures.Count > 0)
            {
                gltf["textures"] = textures;
                gltf["images"] = images;
                gltf["samplers"] = new JsonArray(new JsonObject { ["magFilter"] = 9729, ["minFilter"] = 9987, ["wrapS"] = 10497, ["wrapT"] = 10497 });
            }
            var json = Encoding.UTF8.GetBytes(gltf.ToJsonString());
            int jsonLen = (json.Length + 3) & ~3;
            using (var f = new FileStream(outPath, FileMode.Create))
            using (var w = new BinaryWriter(f))
            {
                w.Write(0x46546C67u); w.Write(2u); w.Write((uint)(12 + 8 + jsonLen + 8 + bin.Length));
                w.Write((uint)jsonLen); w.Write(0x4E4F534Au); w.Write(json); for (int i = json.Length; i < jsonLen; i++) w.Write((byte)' ');
                w.Write((uint)bin.Length); w.Write(0x004E4942u); w.Write(bin.ToArray());
            }
            Console.WriteLine($"{outPath}: {new FileInfo(outPath).Length / 1048576.0:0.0} MB");
            Console.WriteLine($"  {drawn} drawn objects ({batched} cut out of batched meshes), {gmeshes.Count} meshes, {materials.Count} materials, {images.Count} textures");
            Console.WriteLine($"  skipped: {skippedInactive} switched off, {skippedCompressed} compressed meshes, {skippedOther} other; {textureFails} textures not decoded");
            Console.WriteLine($"  extent (glTF space): {sceneMin} to {sceneMax}");
            foreach (var p in problems) Console.WriteLine($"  {p.Value} x {p.Key}");
        }
    }
}
