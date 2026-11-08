using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// The Klingon captains' heads read straight from the game's own files on this PC (AssetFile), instead of
    /// loading the Aegis bridge for them: the hail characters' prefabs in sharedassets6.assets (each a
    /// KlingonCaptainA / KlingonCaptainFlagship root with Klingon_Head, Head, Eye_L and Eye_R under it), the head
    /// mesh beside them, and its materials and textures wherever they are (resources.assets, sharedassets6 and
    /// their .resS files). The files are read on a background thread; the mesh, textures and materials are made
    /// on the main thread, with the game's own shaders (they're in memory by the time the shuttle loads).
    /// Anything unexpected and the bridge preload runs as before.
    /// </summary>
    internal static class KlingonHeadFiles
    {
        private const string PrefabFile = "sharedassets6.assets";

        /// <summary>One captain's head as read from the files.</summary>
        internal sealed class Head
        {
            public string Character;
            public MeshAsset Mesh;
            public readonly List<MaterialPlan> Materials = new List<MaterialPlan>();
            public Vector3 HeadEyeMid;
            public float EyeSpacing;
        }

        internal sealed class MaterialPlan
        {
            public MaterialAsset Material;
            public string Shader;
            public readonly List<KeyValuePair<MaterialAsset.TexEnv, TexturePlan>> Textures = new List<KeyValuePair<MaterialAsset.TexEnv, TexturePlan>>();
        }

        internal sealed class TexturePlan
        {
            public string Key; // file and path ID: textures shared between the heads are made once
            public TextureAsset Texture;
            public byte[] Pixels;
        }

        // ---- Reading (background thread: plain .NET, no Unity calls) ---------------------------------

        /// <summary>Both captains' heads from the files in the game's data folder, by character name.</summary>
        internal static Dictionary<string, Head> Read(string dataDir, string[] characters)
        {
            var heads = new Dictionary<string, Head>();
            var files = new Dictionary<string, AssetFile>(StringComparer.OrdinalIgnoreCase);
            try
            {
                AssetFile Open(AssetFile from, PPtr link)
                {
                    var name = from.FileOf(link);
                    if (name == null) return from;
                    if (!files.TryGetValue(name, out var file)) files[name] = file = new AssetFile(Path.Combine(dataDir, name));
                    return file;
                }
                var assets = files[PrefabFile] = new AssetFile(Path.Combine(dataDir, PrefabFile));
                GameObjectAsset Go(PPtr p) => GameObjectAsset.Read(assets.At(assets.Find(p.PathId)));
                TransformAsset Tr(PPtr p) => TransformAsset.Read(assets.At(assets.Find(p.PathId)));

                foreach (var entry in assets.Objects)
                {
                    if (entry.ClassId != 137) continue; // SkinnedMeshRenderer
                    var renderer = SkinnedRendererAsset.Read(assets.At(entry));
                    if (renderer.Mesh.FileId != 0 || renderer.GameObject.IsNull) continue;
                    var meshEntry = assets.Find(renderer.Mesh.PathId);
                    if (meshEntry == null || assets.NameOf(meshEntry) != "Klingon_Head") continue;
                    var root = Tr(Go(renderer.GameObject).Component(4));
                    while (!root.Father.IsNull) root = Tr(root.Father);
                    var character = Go(root.GameObject).Name;
                    if (Array.IndexOf(characters, character) < 0 || heads.ContainsKey(character)) continue;

                    var bones = new Dictionary<string, TransformAsset>();
                    foreach (var child in root.Children)
                    {
                        var t = Tr(child);
                        bones[Go(t.GameObject).Name] = t;
                    }
                    if (!bones.ContainsKey("Head") || !bones.ContainsKey("Eye_L") || !bones.ContainsKey("Eye_R"))
                        throw new InvalidDataException($"{character} has no Head, Eye_L or Eye_R");
                    var head = new Head { Character = character, Mesh = MeshAsset.Read(assets.At(meshEntry)) };
                    // The eyes' midpoint in the Head bone's space (all three sit directly under the root).
                    var headBone = bones["Head"];
                    var left = V(bones["Eye_L"].Position);
                    var right = V(bones["Eye_R"].Position);
                    head.HeadEyeMid = Quaternion.Inverse(Q(headBone.Rotation)) * ((left + right) * 0.5f - V(headBone.Position));
                    head.EyeSpacing = (left - right).magnitude;

                    foreach (var link in renderer.Materials) head.Materials.Add(ReadMaterial(dataDir, Open, assets, link));
                    heads[character] = head;
                }
            }
            finally
            {
                foreach (var file in files.Values) file.Dispose();
            }
            return heads;
        }

        /// <summary>A material a renderer links to, with its shader's name and its textures' pixels (open: the
        /// asset file a link from a file points into, opened once).</summary>
        internal static MaterialPlan ReadMaterial(string dataDir, Func<AssetFile, PPtr, AssetFile> open, AssetFile from, PPtr link)
        {
            var file = open(from, link);
            var materialEntry = file.Find(link.PathId) ?? throw new InvalidDataException($"material {link} missing in {file.Path}");
            var plan = new MaterialPlan { Material = MaterialAsset.Read(file.At(materialEntry)) };
            plan.Shader = ShaderName(open(file, plan.Material.Shader), plan.Material.Shader.PathId);
            foreach (var env in plan.Material.Textures)
            {
                if (env.Texture.IsNull) continue;
                var texFile = open(file, env.Texture);
                var texEntry = texFile.Find(env.Texture.PathId) ?? throw new InvalidDataException($"{plan.Material.Name}: texture {env.Name} missing");
                var texture = TextureAsset.Read(texFile.At(texEntry));
                plan.Textures.Add(new KeyValuePair<MaterialAsset.TexEnv, TexturePlan>(env, new TexturePlan
                {
                    Key = Path.GetFileName(texFile.Path) + ":" + env.Texture.PathId,
                    Texture = texture,
                    Pixels = texture.Data.Length > 0 ? texture.Data : Stream(dataDir, texture),
                }));
            }
            return plan;
        }

        // A 5.4 shader's name is the start of its text: Shader "RSE/SimpleSkinSinglePass" { ...
        private static string ShaderName(AssetFile file, long pathId)
        {
            var entry = file.Find(pathId) ?? throw new InvalidDataException($"shader {pathId} missing in {file.Path}");
            var r = file.At(entry);
            AssetFile.ReadString(r);
            var script = AssetFile.ReadString(r);
            int open = script.IndexOf('"'), close = open < 0 ? -1 : script.IndexOf('"', open + 1);
            if (close < 0) throw new InvalidDataException($"shader {pathId} in {file.Path} has no name");
            return script.Substring(open + 1, close - open - 1);
        }

        private static byte[] Stream(string dataDir, TextureAsset texture)
        {
            using (var stream = new FileStream(Path.Combine(dataDir, texture.StreamPath), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                stream.Position = texture.StreamOffset;
                var bytes = new byte[texture.StreamSize];
                for (int got = 0; got < bytes.Length;)
                {
                    int n = stream.Read(bytes, got, bytes.Length - got);
                    if (n <= 0) throw new EndOfStreamException($"{texture.Name}: {texture.StreamPath} ends early");
                    got += n;
                }
                return bytes;
            }
        }

        private static Vector3 V(float[] v) => new Vector3(v[0], v[1], v[2]);
        private static Quaternion Q(float[] q) => new Quaternion(q[0], q[1], q[2], q[3]);

        /// <summary>Reads the files on a background thread; the coroutine waits for it, then hands the result on.</summary>
        internal static System.Collections.IEnumerator ReadInBackground(string[] characters, Action<Dictionary<string, Head>, Exception> done)
        {
            Dictionary<string, Head> heads = null;
            Exception error = null;
            string dataDir = Application.dataPath;
            var thread = new Thread(() =>
            {
                try { heads = Read(dataDir, characters); }
                catch (Exception e) { error = e; }
            }) { IsBackground = true, Name = "Klingon heads" };
            thread.Start();
            while (thread.IsAlive) yield return null;
            done(heads, error);
        }

        // ---- Making them (main thread) -------------------------------------------------------------------

        private static readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>();

        internal static Mesh MakeMesh(MeshAsset m)
        {
            // The vertex data: streams one after another (each 16-byte aligned), channels interleaved within one.
            const int Position = 0, Normal = 1, Uv = 3, Tangent = 7;
            int count = (int)m.VertexCount;
            var strides = new Dictionary<int, int>();
            var starts = new Dictionary<int, int>();
            foreach (var c in m.Channels)
            {
                if (c.Dimension == 0) continue;
                if (c.Format != 0) throw new NotSupportedException($"{m.Name}: vertex channel format {c.Format} (only floats are read)");
                strides[c.Stream] = (strides.TryGetValue(c.Stream, out var s) ? s : 0) + c.Dimension * 4;
            }
            int offset = 0;
            foreach (var stream in strides.Keys.OrderBy(k => k))
            {
                starts[stream] = offset;
                offset += strides[stream] * count;
                offset = (offset + 15) & ~15;
            }
            if (offset < m.VertexData.Length - 15 || offset > m.VertexData.Length + 15)
                throw new InvalidDataException($"{m.Name}: vertex data is {m.VertexData.Length} bytes, the channels make {offset}");
            float F(int channel, int vertex, int component)
            {
                var c = m.Channels[channel];
                return BitConverter.ToSingle(m.VertexData, starts[c.Stream] + vertex * strides[c.Stream] + c.Offset + component * 4);
            }
            bool Has(int channel) => channel < m.Channels.Count && m.Channels[channel].Dimension > 0;

            var mesh = new Mesh { name = m.Name };
            var vertices = new Vector3[count];
            for (int i = 0; i < count; i++) vertices[i] = new Vector3(F(Position, i, 0), F(Position, i, 1), F(Position, i, 2));
            mesh.vertices = vertices;
            if (Has(Normal))
            {
                var normals = new Vector3[count];
                for (int i = 0; i < count; i++) normals[i] = new Vector3(F(Normal, i, 0), F(Normal, i, 1), F(Normal, i, 2));
                mesh.normals = normals;
            }
            if (Has(Tangent))
            {
                var tangents = new Vector4[count];
                for (int i = 0; i < count; i++) tangents[i] = new Vector4(F(Tangent, i, 0), F(Tangent, i, 1), F(Tangent, i, 2), F(Tangent, i, 3));
                mesh.tangents = tangents;
            }
            if (Has(Uv))
            {
                var uv = new Vector2[count];
                for (int i = 0; i < count; i++) uv[i] = new Vector2(F(Uv, i, 0), F(Uv, i, 1));
                mesh.uv = uv;
            }
            mesh.subMeshCount = m.SubMeshes.Count;
            for (int s = 0; s < m.SubMeshes.Count; s++)
            {
                var sub = m.SubMeshes[s];
                if (sub.Topology != 0) throw new NotSupportedException($"{m.Name}: sub-mesh {s} isn't triangles");
                var triangles = new int[sub.IndexCount];
                for (int i = 0; i < triangles.Length; i++) triangles[i] = BitConverter.ToUInt16(m.IndexBuffer, (int)sub.FirstByte + i * 2);
                mesh.SetTriangles(triangles, s);
            }
            if (m.SkinWeights.Count == count)
            {
                var weights = new BoneWeight[count];
                for (int i = 0; i < count; i++)
                {
                    var w = m.SkinWeights[i];
                    var b = m.SkinBones[i];
                    weights[i] = new BoneWeight { weight0 = w[0], weight1 = w[1], weight2 = w[2], weight3 = w[3], boneIndex0 = b[0], boneIndex1 = b[1], boneIndex2 = b[2], boneIndex3 = b[3] };
                }
                mesh.boneWeights = weights;
            }
            // Bind poses: stored row by row (e00 e01 e02 e03 e10 ...).
            var poses = new Matrix4x4[m.BindPoses.Count];
            for (int p = 0; p < poses.Length; p++)
                for (int row = 0; row < 4; row++)
                    for (int col = 0; col < 4; col++)
                        poses[p][row, col] = m.BindPoses[p][row * 4 + col];
            mesh.bindposes = poses;
            // Face shapes: each channel's frames list the vertices they move, by index.
            foreach (var channel in m.ShapeChannels)
                for (int f = channel.FrameIndex; f < channel.FrameIndex + channel.FrameCount; f++)
                {
                    var shape = m.Shapes[f];
                    var dv = new Vector3[count];
                    var dn = shape.HasNormals ? new Vector3[count] : null;
                    var dt = shape.HasTangents ? new Vector3[count] : null;
                    for (uint k = shape.FirstVertex; k < shape.FirstVertex + shape.VertexCount; k++)
                    {
                        var v = m.ShapeVertices[(int)k];
                        dv[v.Index] = V(v.Vertex);
                        if (dn != null) dn[v.Index] = V(v.Normal);
                        if (dt != null) dt[v.Index] = V(v.Tangent);
                    }
                    mesh.AddBlendShapeFrame(channel.Name, m.ShapeFullWeights[f], dv, dn, dt);
                }
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>The material, or null while its shader isn't loaded yet (the game loads them with what uses them).</summary>
        internal static Material MakeMaterial(MaterialPlan plan)
        {
            var shader = Shader.Find(plan.Shader);
            if (shader == null)
                foreach (var loaded in Resources.FindObjectsOfTypeAll<Material>())
                    if (loaded != null && loaded.shader != null && loaded.shader.name == plan.Shader) { shader = loaded.shader; break; }
            if (shader == null) return null;
            var m = plan.Material;
            var material = new Material(shader) { name = m.Name };
            if (m.RenderQueue >= 0) material.renderQueue = m.RenderQueue;
            material.shaderKeywords = m.Keywords.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var f in m.Floats) material.SetFloat(f.Key, f.Value);
            foreach (var c in m.Colors) material.SetColor(c.Key, new Color(c.Value[0], c.Value[1], c.Value[2], c.Value[3]));
            foreach (var t in plan.Textures)
            {
                material.SetTexture(t.Key.Name, MakeTexture(t.Value));
                material.SetTextureScale(t.Key.Name, new Vector2(t.Key.Scale[0], t.Key.Scale[1]));
                material.SetTextureOffset(t.Key.Name, new Vector2(t.Key.Offset[0], t.Key.Offset[1]));
            }
            material.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            return material;
        }

        // Once a texture is on the graphics card its pixels are let go, in this plan and in every other plan of the
        // same texture as it's asked for (each material read its own copy). They were kept for the whole session
        // (the holodeck room's and the Klingon heads' plans are kept to build them again), many times over for
        // textures several materials share: the memory floor rose by about 170 MB on entering a PvP room.
        private static Texture2D MakeTexture(TexturePlan plan)
        {
            if (textures.TryGetValue(plan.Key, out var made) && made != null)
            {
                LetGo(plan);
                return made;
            }
            if (plan.Pixels == null)
            {
                Plugin.Log.LogWarning($"Texture {plan.Key} ({plan.Texture?.Name}) is gone and its pixels were let go: drawn without it");
                return null;
            }
            var t = plan.Texture;
            var format = t.Format == 10 ? TextureFormat.DXT1 : t.Format == 12 ? TextureFormat.DXT5 : (TextureFormat)t.Format;
            // Colour space 0 is linear data (normal maps): no sRGB conversion.
            var texture = new Texture2D(t.Width, t.Height, format, t.MipCount > 1, t.ColorSpace == 0)
            {
                name = t.Name,
                wrapMode = (TextureWrapMode)t.WrapMode,
                filterMode = (FilterMode)t.FilterMode,
                anisoLevel = t.Aniso,
                mipMapBias = t.MipBias,
            };
            texture.LoadRawTextureData(plan.Pixels);
            texture.Apply(false, true); // no longer readable: the pixels live on the graphics card only
            texture.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            textures[plan.Key] = texture;
            LetGo(plan);
            return texture;
        }

        private static void LetGo(TexturePlan plan)
        {
            plan.Pixels = null;
            if (plan.Texture != null) plan.Texture.Data = new byte[0];
        }

        /// <summary>The pixels these plans still hold (each plan's own copy counted), in bytes.</summary>
        internal static long PixelBytes(IEnumerable<MaterialPlan> plans) =>
            plans.SelectMany(p => p.Textures).Sum(t => (long)(t.Value.Pixels?.Length ?? 0));
    }
}
