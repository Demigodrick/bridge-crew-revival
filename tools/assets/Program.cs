// assets: look inside the game's own asset files with the mod's reader (src/AssetFile.cs), offline.
// Nothing from the game is stored here.
//
// Usage: dotnet run -- <command> [args]     (game folder: $GAME_DIR, default ~/.local/share/Steam/...)
//   names <file> [classId] [filter]   objects (path ID, class, size, name), e.g. names sharedassets6.assets 43 Klingon
//   renderers <file> [mesh filter]    skinned mesh renderers: their mesh and materials, followed into the other files
//   material <file> <path ID>        a material: shader, keywords, textures (format, size, where), values
//   lights <file>                    a scene's lights: on, baked or real-time, kind, intensity, range, layers
//   tree <file> [depth] [root filter]  a scene's hierarchy: names, local positions, component class IDs
//   drawn <file> <name filter>     drawn objects under matching ones: mesh, materials, static batching
//   prefab <file> <renderer path ID>  the renderer's prefab: its root's name and children (local position, rotation)
//   fit <file> <renderer path ID>     where each bind pose puts the head mesh's middle, against the eyes in Head space
//   mesh <file> <name or path ID>              a mesh's counts: vertices, channels, bind poses, blend shapes, sub-meshes
//   glb <scene file> [out.glb]       a scene's drawn objects, textured, as binary glTF (Export.cs), e.g. glb level6 aegis.glb
using StbcRevival;

var gameDir = Environment.GetEnvironmentVariable("GAME_DIR")
              ?? Path.Combine(Environment.GetEnvironmentVariable("HOME")!, ".local/share/Steam/steamapps/common/Star Trek Bridge Crew");
if (args.Length < 2) { Console.Error.WriteLine("usage: see the comment at the top of Program.cs"); return 1; }
var file = Path.IsPathRooted(args[1]) ? args[1] : Path.Combine(gameDir, "stbc_Data", args[1]);
using var assets = new AssetFile(file);

switch (args[0])
{
    case "names":
    {
        int? classId = args.Length > 2 ? int.Parse(args[2]) : null;
        var filter = args.Length > 3 ? args[3] : null;
        Console.WriteLine($"{assets.UnityVersion}, {assets.Objects.Count} objects");
        foreach (var o in assets.Objects)
        {
            if (classId != null && o.ClassId != classId) continue;
            var name = assets.NameOf(o) ?? "";
            if (filter != null && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            Console.WriteLine($"{o.PathId,8} class {o.ClassId,4} {o.Size,10} {name}");
        }
        break;
    }
    case "mesh":
    {
        var entry = assets.Objects.FirstOrDefault(o => o.ClassId == AssetFile.MeshClass && (o.PathId.ToString() == args[2] || assets.NameOf(o) == args[2]));
        if (entry == null) { Console.Error.WriteLine("no mesh " + args[2]); return 1; }
        var m = MeshAsset.Read(assets.At(entry));
        long read = m.EndOfVertexData - entry.Offset;
        Console.WriteLine($"{m.Name} (path ID {entry.PathId}, {entry.Size} bytes; read {read} up to the compressed mesh)");
        Console.WriteLine($"  vertices {m.VertexCount}, channels mask 0x{m.CurrentChannels:X}, vertex data {m.VertexData.Length} bytes, compression {m.Compression}, readable {m.Readable}");
        for (int i = 0; i < m.Channels.Count; i++)
        {
            var c = m.Channels[i];
            if (c.Dimension > 0) Console.WriteLine($"    channel {i}: stream {c.Stream} offset {c.Offset} format {c.Format} dimension {c.Dimension}");
        }
        Console.WriteLine($"  indices {m.IndexBuffer.Length / 2}, sub-meshes {m.SubMeshes.Count}: " +
                          string.Join(", ", m.SubMeshes.Select(s => $"{s.IndexCount} indices from {s.FirstByte}, vertices {s.FirstVertex}+{s.VertexCount}")));
        Console.WriteLine($"  bind poses {m.BindPoses.Count}, bone hashes {m.BoneNameHashes.Length}, skin weights {m.SkinWeights.Count}");
        Console.WriteLine($"  blend shapes: {m.ShapeChannels.Count} channels, {m.Shapes.Count} frames, {m.ShapeVertices.Count} shape vertices");
        Console.WriteLine("    " + string.Join(" ", m.ShapeChannels.Select(c => c.Name)));
        break;
    }
    case "material":
    {
        var files = new Dictionary<string, AssetFile>();
        AssetFile Open(PPtr link)
        {
            var other = assets.FileOf(link);
            return other == null ? assets : files.TryGetValue(other, out var f) ? f : files[other] = new AssetFile(Path.Combine(gameDir, "stbc_Data", other));
        }
        var entry = assets.Find(long.Parse(args[2]));
        var m = MaterialAsset.Read(assets.At(entry));
        var shaderFile = Open(m.Shader);
        var shaderEntry = shaderFile.Find(m.Shader.PathId);
        string shader = "(missing)";
        if (shaderEntry != null)
        {
            var r = shaderFile.At(shaderEntry);
            AssetFile.ReadString(r); // name
            var script = AssetFile.ReadString(r);
            shader = script.Length > 0 && script.IndexOf('"') >= 0 ? script.Substring(script.IndexOf('"') + 1, Math.Max(0, script.IndexOf('"', script.IndexOf('"') + 1) - script.IndexOf('"') - 1)) : $"(script {script.Length} chars)";
        }
        Console.WriteLine($"{m.Name}: shader {assets.FileOf(m.Shader) ?? "here"}:{m.Shader.PathId} \"{shader}\", keywords '{m.Keywords}', queue {m.RenderQueue}, tags {string.Join(",", m.Tags.Select(t => t.Key + "=" + t.Value))}");
        foreach (var t in m.Textures)
        {
            if (t.Texture.IsNull) { Console.WriteLine($"  {t.Name}: none"); continue; }
            var texFile = Open(t.Texture);
            var te = texFile.Find(t.Texture.PathId);
            if (te == null) { Console.WriteLine($"  {t.Name}: {texFile.Path}:{t.Texture.PathId} missing"); continue; }
            var tex = TextureAsset.Read(texFile.At(te));
            Console.WriteLine($"  {t.Name}: {tex.Name} {tex.Width}x{tex.Height} format {tex.Format} mips {tex.MipCount} size {tex.ImageSize} " +
                              (tex.Data.Length > 0 ? $"inline {tex.Data.Length}" : $"in {tex.StreamPath} at {tex.StreamOffset} ({tex.StreamSize})") +
                              $" wrap {tex.WrapMode} filter {tex.FilterMode} aniso {tex.Aniso} colour space {tex.ColorSpace}; scale ({t.Scale[0]}, {t.Scale[1]}) offset ({t.Offset[0]}, {t.Offset[1]})");
        }
        Console.WriteLine("  floats: " + string.Join(", ", m.Floats.Select(f => $"{f.Key}={f.Value}")));
        Console.WriteLine("  colours: " + string.Join(", ", m.Colors.Select(c => $"{c.Key}=({string.Join(",", c.Value.Select(v => v.ToString("0.###")))})")));
        break;
    }
    case "lights":
    {
        // A scene's lights: on or off, baked or real-time, kind, intensity, range, which layers they light.
        string[] kinds = { "spot", "dir", "point", "area" };
        foreach (var e in assets.Objects.Where(o => o.ClassId == 108))
        {
            var l = LightAsset.Read(assets.At(e), e.Size);
            if (l == null) { Console.WriteLine($"{e.PathId}: not a 5.4 light ({e.Size} bytes)"); continue; }
            var go = GameObjectAsset.Read(assets.At(assets.Find(l.GameObject.PathId)));
            Console.WriteLine($"{(l.Enabled ? "on " : "off")} {(l.Baked ? "baked" : "real-time"),-9} {kinds[Math.Clamp(l.Type, 0, 3)],-5} i={l.Intensity:0.##} r={l.Range:0.#} layers={l.CullingMask:X8} layer={go.Layer} {go.Name}");
        }
        break;
    }
    case "tree":
    {
        // A scene's hierarchy: every root and its children (name, local position, component class IDs), to a depth.
        int depth = args.Length > 2 ? int.Parse(args[2]) : 3;
        string filter = args.Length > 3 ? args[3] : null;
        GameObjectAsset Go(PPtr p) => GameObjectAsset.Read(assets.At(assets.Find(p.PathId)));
        TransformAsset Tr(PPtr p) => TransformAsset.Read(assets.At(assets.Find(p.PathId)));
        string F(float[] v) => "(" + string.Join(", ", v.Select(x => x.ToString("0.##"))) + ")";
        void Show(TransformAsset t, int level)
        {
            var go = Go(t.GameObject);
            var classes = string.Join(",", go.Components.Select(c => c.Key).Where(k => k != 4 && k != 224));
            Console.WriteLine($"{new string(' ', level * 2)}{go.Name} {F(t.Position)}{(classes.Length > 0 ? " [" + classes + "]" : "")}{(t.Children.Count > 0 ? $" {t.Children.Count} children" : "")}");
            if (level + 1 < depth) foreach (var c in t.Children) Show(Tr(c), level + 1);
        }
        foreach (var e in assets.Objects.Where(o => o.ClassId == 4 || o.ClassId == 224))
        {
            var t = TransformAsset.Read(assets.At(e));
            if (!t.Father.IsNull) continue;
            if (filter != null && !Go(t.GameObject).Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            Show(t, 0);
        }
        break;
    }
    case "drawn":
    {
        // Under the roots whose name contains a filter: each drawn object's mesh (name, file) and materials, and
        // whether it's statically batched (a combined mesh's sub-meshes).
        string filter = args[2];
        GameObjectAsset Go(PPtr p) => GameObjectAsset.Read(assets.At(assets.Find(p.PathId)));
        TransformAsset Tr(PPtr p) => TransformAsset.Read(assets.At(assets.Find(p.PathId)));
        string MeshName(PPtr p) => p.FileId == 0 && assets.Find(p.PathId) is { } e ? $"{assets.NameOf(e)} #{p.PathId}" : $"{assets.FileOf(p)}:{p.PathId}";
        var meshes = new Dictionary<long, MeshAsset>();
        // Where a batched renderer's sub-meshes sit (the combined mesh's vertices are in the scene's space).
        string Extent(MeshFilterAsset? mf, MeshRendererAsset mr)
        {
            if (mf == null || mf.Mesh.FileId != 0) return "";
            if (!meshes.TryGetValue(mf.Mesh.PathId, out var mesh)) meshes[mf.Mesh.PathId] = mesh = MeshAsset.Read(assets.At(assets.Find(mf.Mesh.PathId)));
            int stride = mesh.Channels.Where(c => c.Stream == 0 && c.Dimension > 0).Sum(c => c.Dimension * (c.Format == 0 ? 4 : c.Format == 1 ? 2 : 1));
            var min = new System.Numerics.Vector3(float.MaxValue); var max = new System.Numerics.Vector3(float.MinValue);
            foreach (var i in mr.SubsetIndices)
            {
                var sub = mesh.SubMeshes[(int)i];
                for (uint v = sub.FirstVertex; v < sub.FirstVertex + sub.VertexCount; v++)
                {
                    int at = (int)v * stride;
                    var p = new System.Numerics.Vector3(BitConverter.ToSingle(mesh.VertexData, at), BitConverter.ToSingle(mesh.VertexData, at + 4), BitConverter.ToSingle(mesh.VertexData, at + 8));
                    min = System.Numerics.Vector3.Min(min, p); max = System.Numerics.Vector3.Max(max, p);
                }
            }
            string F(System.Numerics.Vector3 v) => $"({v.X:0.0}, {v.Y:0.0}, {v.Z:0.0})";
            // How much of it lies flat at its lowest (a floor) and highest (a ceiling).
            var low = new System.Numerics.Vector3(float.MaxValue); var lowMax = new System.Numerics.Vector3(float.MinValue);
            foreach (var i in mr.SubsetIndices)
            {
                var sub = mesh.SubMeshes[(int)i];
                for (uint v = sub.FirstVertex; v < sub.FirstVertex + sub.VertexCount; v++)
                {
                    int at = (int)v * stride;
                    var p = new System.Numerics.Vector3(BitConverter.ToSingle(mesh.VertexData, at), BitConverter.ToSingle(mesh.VertexData, at + 4), BitConverter.ToSingle(mesh.VertexData, at + 8));
                    if (p.Y > min.Y + 0.1f) continue;
                    low = System.Numerics.Vector3.Min(low, p); lowMax = System.Numerics.Vector3.Max(lowMax, p);
                }
            }
            // The heights most vertices sit at (a floor or ceiling shows up as a crowded height spread wide).
            var levels = new Dictionary<int, (int count, float minX, float maxX)>();
            foreach (var i in mr.SubsetIndices)
            {
                var sub = mesh.SubMeshes[(int)i];
                for (uint v = sub.FirstVertex; v < sub.FirstVertex + sub.VertexCount; v++)
                {
                    int at = (int)v * stride;
                    float x = BitConverter.ToSingle(mesh.VertexData, at), y = BitConverter.ToSingle(mesh.VertexData, at + 4);
                    int key = (int)Math.Round(y * 10);
                    var l = levels.TryGetValue(key, out var e) ? e : (0, float.MaxValue, float.MinValue);
                    levels[key] = (l.Item1 + 1, Math.Min(l.Item2, x), Math.Max(l.Item3, x));
                }
            }
            var crowded = string.Join(", ", levels.OrderByDescending(l => l.Value.count).Take(4).Select(l => $"y {l.Key / 10f:0.0}: {l.Value.count} (x {l.Value.minX:0} to {l.Value.maxX:0})"));
            return $"from {F(min)} to {F(max)}; at its lowest, x {low.X:0.0} to {lowMax.X:0.0}, z {low.Z:0.0} to {lowMax.Z:0.0}; heights {crowded}";
        }
        void Show(TransformAsset t, string path)
        {
            var go = Go(t.GameObject);
            path = path + "/" + go.Name;
            var filterRef = go.Component(33);
            var rendererRef = go.Component(23);
            if (!rendererRef.IsNull)
            {
                var mr = MeshRendererAsset.Read(assets.At(assets.Find(rendererRef.PathId)));
                var mf = filterRef.IsNull ? null : MeshFilterAsset.Read(assets.At(assets.Find(filterRef.PathId)));
                Console.WriteLine($"{path}: mesh {(mf == null ? "-" : MeshName(mf.Mesh))}{(mr.Enabled ? "" : " (off)")}, "
                    + $"materials {string.Join(" ", mr.Materials.Select(m => m.FileId == 0 ? assets.NameOf(assets.Find(m.PathId)) : $"{assets.FileOf(m)}:{m.PathId}"))}"
                    + (mr.StaticBatchRoot.IsNull && mr.SubsetIndices.Count == 0 ? "" : $", batched: sub-meshes {string.Join(",", mr.SubsetIndices)} {Extent(mf, mr)}"));
            }
            foreach (var c in t.Children) Show(Tr(c), path);
        }
        foreach (var e in assets.Objects.Where(o => o.ClassId == 4))
        {
            var t = TransformAsset.Read(assets.At(e));
            var name = Go(t.GameObject).Name;
            if (name.Contains(filter)) Show(t, "");
        }
        break;
    }
    case "prefab":
    {
        var smr = SkinnedRendererAsset.Read(assets.At(assets.Find(long.Parse(args[2]))));
        GameObjectAsset Go(PPtr p) => GameObjectAsset.Read(assets.At(assets.Find(p.PathId)));
        TransformAsset Tr(PPtr p) => TransformAsset.Read(assets.At(assets.Find(p.PathId)));
        var t = Tr(Go(smr.GameObject).Component(4));
        while (!t.Father.IsNull) t = Tr(t.Father);
        Console.WriteLine($"root {Go(t.GameObject).Name}");
        foreach (var c in t.Children)
        {
            var child = Tr(c);
            string F(float[] v) => "(" + string.Join(", ", v.Select(x => x.ToString("0.####"))) + ")";
            Console.WriteLine($"  {Go(child.GameObject).Name}: position {F(child.Position)} rotation {F(child.Rotation)} scale {F(child.Scale)}, {child.Children.Count} children");
        }
        break;
    }
    case "fit":
    {
        var smr = SkinnedRendererAsset.Read(assets.At(assets.Find(long.Parse(args[2]))));
        var mesh = MeshAsset.Read(assets.At(assets.Find(smr.Mesh.PathId)));
        GameObjectAsset Go(PPtr p) => GameObjectAsset.Read(assets.At(assets.Find(p.PathId)));
        TransformAsset Tr(PPtr p) => TransformAsset.Read(assets.At(assets.Find(p.PathId)));
        var t = Tr(Go(smr.GameObject).Component(4));
        while (!t.Father.IsNull) t = Tr(t.Father);
        var bones = t.Children.Select(Tr).ToDictionary(c => Go(c.GameObject).Name);
        System.Numerics.Vector3 V(float[] v) => new(v[0], v[1], v[2]);
        System.Numerics.Quaternion Q(float[] q) => new(q[0], q[1], q[2], q[3]);
        var head = bones["Head"];
        var eyeMid = (V(bones["Eye_L"].Position) + V(bones["Eye_R"].Position)) / 2;
        var headEyeMid = System.Numerics.Vector3.Transform(eyeMid - V(head.Position), System.Numerics.Quaternion.Inverse(Q(head.Rotation)));
        // The mesh's middle: its vertex positions' bounds (channel 0, stream 0, floats, stride 40 here).
        var c0 = mesh.Channels[0];
        int stride = mesh.Channels.Where(c => c.Stream == c0.Stream && c.Dimension > 0).Sum(c => c.Dimension * 4);
        var min = new System.Numerics.Vector3(float.MaxValue); var max = new System.Numerics.Vector3(float.MinValue);
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            var p = new System.Numerics.Vector3(BitConverter.ToSingle(mesh.VertexData, i * stride + c0.Offset), BitConverter.ToSingle(mesh.VertexData, i * stride + c0.Offset + 4), BitConverter.ToSingle(mesh.VertexData, i * stride + c0.Offset + 8));
            min = System.Numerics.Vector3.Min(min, p); max = System.Numerics.Vector3.Max(max, p);
        }
        var mid = (min + max) / 2;
        Console.WriteLine($"eyes in Head space {headEyeMid}, apart {System.Numerics.Vector3.Distance(V(bones["Eye_L"].Position), V(bones["Eye_R"].Position)):0.0000}; mesh middle {mid} size {max - min}");
        for (int i = 0; i < mesh.BindPoses.Count; i++)
        {
            var m = mesh.BindPoses[i];
            // Column-major (m[col*4 + row]) and row-major readings of the same 16 numbers.
            System.Numerics.Vector3 Apply(Func<int, int, float> e) => new(e(0,0)*mid.X + e(0,1)*mid.Y + e(0,2)*mid.Z + e(0,3), e(1,0)*mid.X + e(1,1)*mid.Y + e(1,2)*mid.Z + e(1,3), e(2,0)*mid.X + e(2,1)*mid.Y + e(2,2)*mid.Z + e(2,3));
            var colMajor = Apply((r, c) => m[c * 4 + r]);
            var rowMajor = Apply((r, c) => m[r * 4 + c]);
            Console.WriteLine($"  bind pose {i}: column-major miss {System.Numerics.Vector3.Distance(colMajor, headEyeMid):0.000} m, row-major miss {System.Numerics.Vector3.Distance(rowMajor, headEyeMid):0.000} m");
        }
        break;
    }
    case "renderers":
    {
        var filter = args.Length > 2 ? args[2] : null;
        var files = new Dictionary<string, AssetFile>();
        string Name(PPtr link)
        {
            var other = assets.FileOf(link);
            var file = other == null ? assets : files.TryGetValue(other, out var f) ? f : files[other] = new AssetFile(Path.Combine(gameDir, "stbc_Data", other));
            var entry = file.Find(link.PathId);
            return $"{other ?? "here"}:{link.PathId} {(entry == null ? "(missing)" : file.NameOf(entry))}";
        }
        foreach (var o in assets.Objects.Where(o => o.ClassId == 137))
        {
            SkinnedRendererAsset smr;
            try { smr = SkinnedRendererAsset.Read(assets.At(o)); }
            catch (Exception e) { Console.WriteLine($"{o.PathId}: {e.Message}"); continue; }
            var mesh = smr.Mesh.IsNull ? "(none)" : Name(smr.Mesh);
            if (filter != null && mesh.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            Console.WriteLine($"{o.PathId}: mesh {mesh}; bones {smr.Bones.Count}, shape weights {smr.BlendShapeWeights.Length}");
            foreach (var m in smr.Materials) Console.WriteLine($"    material {Name(m)}");
        }
        break;
    }
    case "glb":
        return GlbExport.Run(Path.GetDirectoryName(file)!, Path.GetFileName(file), args.Length > 2 ? args[2] : Path.GetFileName(file) + ".glb");
    default:
        Console.Error.WriteLine("unknown command " + args[0]);
        return 1;
}
return 0;
