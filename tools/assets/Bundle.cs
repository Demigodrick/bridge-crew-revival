using System.Buffers.Binary;
using System.Text;

/// <summary>
/// A Unity 5.4 asset bundle (UnityFS, format 6), unpacked to a folder: each file in it (the serialized file
/// "CAB-…", its texture data "CAB-….resS", …) written there under its own name, so the readers for the game's own
/// files (AssetFile, GlbExport) read it as they do the game's. Blocks stored plain or with LZ4 / LZ4HC (Unity's
/// "chunk-based compression"); LZMA, Unity's default for bundles, isn't read: the bundle has to be built with
/// BuildAssetBundleOptions.ChunkBasedCompression (or UncompressedAssetBundle).
/// </summary>
internal static class Bundle
{
    /// <summary>Unpacks <paramref name="bundle"/> into <paramref name="folder"/>; the names of the files in it.</summary>
    public static List<string> Unpack(string bundle, string folder)
    {
        using var s = new FileStream(bundle, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var r = new BinaryReader(s);
        string sig = CString(r);
        if (sig != "UnityFS") throw new InvalidDataException(sig == "UnityWeb" || sig == "UnityRaw" ? "an old-style bundle (" + sig + "); build it with Unity 5.4" : "not an asset bundle");
        uint format = BE32(r);
        CString(r); // "5.x.x"
        string engine = CString(r);
        if (format != 6) throw new InvalidDataException($"bundle format {format} (Unity {engine}); this game is Unity 5.4, which writes format 6");
        if (!engine.StartsWith("5.4")) throw new InvalidDataException($"built with Unity {engine}; the game is Unity 5.4.4 and only loads bundles from Unity 5.4");
        BE64(r); // file size
        int infoPacked = (int)BE32(r), infoSize = (int)BE32(r);
        uint flags = BE32(r);
        long dataStart;
        byte[] packed;
        if ((flags & 0x80) != 0) // index at the end
        {
            dataStart = s.Position;
            s.Position = s.Length - infoPacked;
            packed = r.ReadBytes(infoPacked);
        }
        else
        {
            packed = r.ReadBytes(infoPacked);
            dataStart = s.Position;
        }
        var info = Decompress(packed, infoSize, (int)(flags & 0x3f));
        int p = 16; // a hash of the data
        int blockCount = BinaryPrimitives.ReadInt32BigEndian(info.AsSpan(p)); p += 4;
        var blocks = new List<(int size, int packed, int kind)>();
        for (int i = 0; i < blockCount; i++, p += 10)
            blocks.Add((BinaryPrimitives.ReadInt32BigEndian(info.AsSpan(p)), BinaryPrimitives.ReadInt32BigEndian(info.AsSpan(p + 4)), BinaryPrimitives.ReadUInt16BigEndian(info.AsSpan(p + 8)) & 0x3f));
        int fileCount = BinaryPrimitives.ReadInt32BigEndian(info.AsSpan(p)); p += 4;
        var files = new List<(long offset, long size, string name)>();
        for (int i = 0; i < fileCount; i++)
        {
            long offset = BinaryPrimitives.ReadInt64BigEndian(info.AsSpan(p)), size = BinaryPrimitives.ReadInt64BigEndian(info.AsSpan(p + 8));
            p += 20; // and flags
            int end = Array.IndexOf(info, (byte)0, p);
            files.Add((offset, size, Encoding.UTF8.GetString(info, p, end - p)));
            p = end + 1;
        }

        // The blocks, one after another, make one stream the files sit in: unpacked to a scratch file.
        Directory.CreateDirectory(folder);
        string all = Path.Combine(folder, ".data");
        using (var o = new FileStream(all, FileMode.Create, FileAccess.ReadWrite))
        {
            s.Position = dataStart;
            foreach (var (size, packedSize, kind) in blocks)
                o.Write(Decompress(r.ReadBytes(packedSize), size, kind));
            foreach (var (offset, size, name) in files)
            {
                string safe = Path.GetFileName(name);
                if (safe.Length == 0 || safe.StartsWith('.')) continue;
                using var f = new FileStream(Path.Combine(folder, safe), FileMode.Create, FileAccess.Write);
                o.Position = offset;
                Copy(o, f, size);
            }
        }
        File.Delete(all);
        return files.Select(f => Path.GetFileName(f.name)).Where(n => n.Length > 0 && !n.StartsWith('.')).ToList();
    }

    private static void Copy(Stream from, Stream to, long count)
    {
        var buffer = new byte[1 << 16];
        while (count > 0)
        {
            int n = from.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (n <= 0) throw new EndOfStreamException();
            to.Write(buffer, 0, n);
            count -= n;
        }
    }

    private static byte[] Decompress(byte[] data, int size, int kind) => kind switch
    {
        0 => data,
        1 => throw new InvalidDataException("compressed with LZMA; build the bundle with BuildAssetBundleOptions.ChunkBasedCompression (LZ4)"),
        2 or 3 => Lz4(data, size),
        _ => throw new InvalidDataException($"compression {kind} isn't known"),
    };

    /// <summary>An LZ4 block (no frame), as Unity stores LZ4 and LZ4HC.</summary>
    internal static byte[] Lz4(byte[] src, int size)
    {
        var dst = new byte[size];
        int i = 0, o = 0;
        while (i < src.Length)
        {
            int token = src[i++];
            int literal = token >> 4;
            if (literal == 15) { int b; do { b = src[i++]; literal += b; } while (b == 255); }
            Buffer.BlockCopy(src, i, dst, o, literal);
            i += literal; o += literal;
            if (i >= src.Length) break;
            int offset = src[i] | src[i + 1] << 8;
            i += 2;
            if (offset == 0) throw new InvalidDataException("bad LZ4 data");
            int match = token & 15;
            if (match == 15) { int b; do { b = src[i++]; match += b; } while (b == 255); }
            match += 4;
            for (int from = o - offset, end = o + match; o < end; ) dst[o++] = dst[from++];
        }
        if (o != size) throw new InvalidDataException($"LZ4 data unpacked to {o} bytes, not {size}");
        return dst;
    }

    private static string CString(BinaryReader r)
    {
        var b = new List<byte>();
        for (byte c; (c = r.ReadByte()) != 0;) { b.Add(c); if (b.Count > 64) break; }
        return Encoding.ASCII.GetString(b.ToArray());
    }

    private static uint BE32(BinaryReader r) => BinaryPrimitives.ReadUInt32BigEndian(r.ReadBytes(4));
    private static long BE64(BinaryReader r) => BinaryPrimitives.ReadInt64BigEndian(r.ReadBytes(8));
}

/// <summary>What an asset bundle offers by name: its AssetBundle object's container (Unity 5.4's layout).</summary>
internal static class BundleContents
{
    internal const int AssetBundleClass = 142;
    internal const int GameObjectClass = 1;

    /// <summary>The bundle's named assets in an unpacked file: (name as built, e.g. "assets/bridges/mybridge.prefab", link).</summary>
    public static List<(string name, StbcRevival.PPtr asset)> Read(StbcRevival.AssetFile f)
    {
        var list = new List<(string, StbcRevival.PPtr)>();
        foreach (var e in f.Objects)
        {
            if (e.ClassId != AssetBundleClass) continue;
            var r = f.At(e);
            StbcRevival.AssetFile.ReadString(r);                       // m_Name
            StbcRevival.AssetFile.ReadPPtrs(r);                        // m_PreloadTable
            for (int n = r.ReadInt32(), i = 0; i < n; i++)            // m_Container
            {
                string name = StbcRevival.AssetFile.ReadString(r);
                r.ReadInt32(); r.ReadInt32();                          // preload index and size
                list.Add((name, StbcRevival.AssetFile.ReadPPtr(r)));
            }
        }
        return list;
    }

    /// <summary>The prefabs (GameObjects) a bundle offers, from its unpacked files: (name, file, root GameObject path ID).</summary>
    public static List<(string name, string file, long root)> Prefabs(string folder, IEnumerable<string> files)
    {
        var found = new List<(string, string, long)>();
        foreach (var name in files)
        {
            StbcRevival.AssetFile f;
            try { f = new StbcRevival.AssetFile(Path.Combine(folder, name)); } catch { continue; }
            using (f)
                foreach (var (asset, link) in Read(f))
                    if (link.FileId == 0 && f.Find(link.PathId)?.ClassId == GameObjectClass) found.Add((asset, name, link.PathId));
        }
        return found;
    }
}
