// assets: look inside the game's own asset files with the mod's reader (src/AssetFile.cs), offline.
// Nothing from the game is stored here.
//
// Usage: dotnet run -- <command> [args]     (game folder: $GAME_DIR, default ~/.local/share/Steam/...)
//   names <file> [classId] [filter]   objects (path ID, class, size, name), e.g. names sharedassets6.assets 43 Klingon
//   mesh <file> <name or path ID>              a mesh's counts: vertices, channels, bind poses, blend shapes, sub-meshes
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
    default:
        Console.Error.WriteLine("unknown command " + args[0]);
        return 1;
}
return 0;
