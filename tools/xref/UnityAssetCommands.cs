// xref's commands on Unity's own types (UnityAssets.cs has their layouts):
//   `xref objects <file> [classId]`     each object: path ID, class, size and name (for types that start with one)
//   `xref gameobject <file> <pathId>`   a GameObject's components, the sprites each links to, its parent and children
//   `xref sprites <file> [out dir]`     every Sprite: name, rect, texture; with a folder, each saved as a PNG

using StbcRevival;

static class UnityAssetCommands
{
    public static int Objects(string dataDir, string file, int? classId)
    {
        using var a = new AssetFile(Path.Combine(dataDir, file));
        foreach (var e in a.Objects.Where(o => classId == null || o.ClassId == classId))
        {
            string? name = e.ClassId is 1 or 114 or 115 or 4 or 224 ? null : a.NameOf(e);
            if (e.ClassId == 1) { var g = GameObjectAsset.Read(a.At(e)); name = g.Name; }
            Console.WriteLine($"{e.PathId,8}  {UnityAssets.ClassNames.GetValueOrDefault(e.ClassId, e.ClassId.ToString()),-14} {e.Size,9}  @{e.Offset,-11} {name}");
        }
        return 0;
    }

    /// <summary>A GameObject's components, and any links to sprites in each one's data (a 12-byte PPtr that points at a
    /// Sprite, found by scanning: for Unity's UI components, whose layouts aren't written out here).</summary>
    public static int GameObject(string dataDir, string file, long pathId)
    {
        using var a = new AssetFile(Path.Combine(dataDir, file));
        var e = a.Find(pathId);
        if (e == null || e.ClassId != 1) { Console.WriteLine("not a GameObject"); return 1; }
        var g = GameObjectAsset.Read(a.At(e));
        Console.WriteLine($"GameObject {pathId} '{g.Name}'");
        var others = new Dictionary<string, AssetFile?>();
        AssetFile? Other(string name)
        {
            if (!others.TryGetValue(name, out var f)) { try { f = new AssetFile(Path.Combine(dataDir, name)); } catch { f = null; } others[name] = f; }
            return f;
        }
        foreach (var c in g.Components)
        {
            var ce = a.Find(c.Value.PathId);
            Console.WriteLine($"  {UnityAssets.ClassNames.GetValueOrDefault(c.Key, c.Key.ToString())} {c.Value}");
            if (ce == null) continue;
            var r = a.At(ce);
            var bytes = r.ReadBytes((int)ce.Size);
            for (int i = 0; i + 12 <= bytes.Length; i += 4)
            {
                int fileId = BitConverter.ToInt32(bytes, i);
                long target = BitConverter.ToInt64(bytes, i + 4);
                if (fileId < 0 || fileId > a.Externals.Count || target == 0) continue;
                var tf = fileId == 0 ? a : Other(Path.GetFileName(a.Externals[fileId - 1]));
                var te = tf?.Find(target);
                if (te == null || te.ClassId != UnityAssets.SpriteClass) continue;
                Console.WriteLine($"    links to sprite '{tf!.NameOf(te)}' ({Path.GetFileName(tf.Path)}:{target}) at byte {i}");
            }
        }
        // Where it sits: its parent and children (RectTransform starts as a Transform does).
        var tc = g.Components.FirstOrDefault(c => c.Key is 4 or 224);
        var te2 = tc.Value.IsNull ? null : a.Find(tc.Value.PathId);
        if (te2 != null)
        {
            var t = TransformAsset.Read(a.At(te2));
            string NameOfTransform(PPtr link)
            {
                var x = a.Find(link.PathId);
                if (link.FileId != 0 || x == null) return link.ToString();
                var go = a.Find(TransformAsset.Read(a.At(x)).GameObject.PathId);
                return go == null ? link.ToString() : $"'{GameObjectAsset.Read(a.At(go)).Name}' (GameObject {TransformAsset.Read(a.At(x)).GameObject.PathId})";
            }
            if (!t.Father.IsNull) Console.WriteLine($"  parent {NameOfTransform(t.Father)}");
            foreach (var c in t.Children) Console.WriteLine($"  child {NameOfTransform(c)}");
        }
        foreach (var f in others.Values) f?.Dispose();
        return 0;
    }

    public static int Sprites(string dataDir, string file, string? outDir)
    {
        using var a = new AssetFile(Path.Combine(dataDir, file));
        var entries = a.Objects.Where(o => o.ClassId == UnityAssets.SpriteClass).ToList();
        int exact = 0;
        foreach (var e in entries)
        {
            try { var r = a.At(e); UnityAssets.ReadSprite(r, e); if (r.BaseStream.Position - e.Offset == e.Size) exact++; else Console.WriteLine($"  sprite {e.PathId}: used {r.BaseStream.Position - e.Offset} of {e.Size} bytes"); }
            catch (Exception ex) { Console.WriteLine($"  sprite {e.PathId}: {ex.Message}"); }
        }
        Console.WriteLine($"{file}: {entries.Count} sprites, {exact} read exactly");
        if (exact != entries.Count) return 2;

        var others = new Dictionary<string, AssetFile?>();
        AssetFile? Open(string name)
        {
            if (name == file) return a;
            if (!others.TryGetValue(name, out var f)) { try { f = new AssetFile(Path.Combine(dataDir, name)); } catch { f = null; } others[name] = f; }
            return f;
        }
        var pictures = new UnityAssets.SpritePictures(dataDir, Open);
        if (outDir != null) Directory.CreateDirectory(outDir);
        foreach (var e in entries)
        {
            var s = UnityAssets.ReadSprite(a.At(e), e);
            string texFile = a.FileOf(s.Texture) ?? file;
            Console.WriteLine($"{s.PathId,8}  {s.Name,-40} rect {s.TextureRect[2]:0}x{s.TextureRect[3]:0} at {s.TextureRect[0]:0},{s.TextureRect[1]:0}  texture {texFile}:{s.Texture.PathId}");
            if (outDir == null) continue;
            var png = pictures.Png(a, s);
            if (png != null) File.WriteAllBytes(Path.Combine(outDir, Safe(s.Name) + ".png"), png);
        }
        foreach (var w in pictures.Warnings) Console.Error.WriteLine("  " + w);
        foreach (var f in others.Values) f?.Dispose();
        return 0;
    }

    static string Safe(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
