// Unity's own types the map data links to, as Unity 5.4 saves them (the files carry no type trees, so these are
// written out here and checked by every object reading exactly its stored size):
// a sprite's pixels as a PNG, and the Resources list (ResourceManager). Shared by xref (its commands on these are in
// UnityAssetCommands.cs) and the galaxy designer (tools/galaxy-designer, which links this file).
//
// Sprite (class 213), Unity 5.4 player build (the editor's m_RenderDataKey and m_AtlasTags aren't written):
//   string m_Name; Rectf m_Rect; Vector2f m_Offset; Vector4f m_Border; float m_PixelsToUnits; Vector2f m_Pivot;
//   UInt32 m_Extrude; bool m_IsPolygon (padded); SpriteRenderData m_RD {PPtr<Texture2D> texture;
//   PPtr<Texture2D> alphaTexture; vector<SpriteVertex{Vector3f pos}> vertices; vector<UInt16> indices (padded);
//   Rectf textureRect; Vector2f textureRectOffset; UInt32 settingsRaw; Vector4f uvTransform}.
// Texture2D (class 28), Unity 5.4:
//   string m_Name; int m_Width, m_Height, m_CompleteImageSize, m_TextureFormat, m_MipCount; bool m_IsReadable,
//   m_ReadAllowed (padded); int m_ImageCount, m_TextureDimension; GLTextureSettings {int m_FilterMode, m_Aniso;
//   float m_MipBias; int m_WrapMode}; int m_LightmapFormat, m_ColorSpace; TypelessData image data (Int32 size,
//   bytes, padded); StreamingInfo m_StreamData {UInt32 offset, size; string path} (the pixels are in that .resS
//   file when size > 0).
// ResourceManager (class 147, in globalgamemanagers), Unity 5.4: map<string, PPtr<Object>> m_Container (the path
//   under a Resources folder, lower case, and the asset), then m_DependentAssets (not read).

#nullable enable
using System.IO.Compression;
using System.Text;
using StbcRevival;

static class UnityAssets
{
    public const int Texture2DClass = 28, SpriteClass = 213;

    public static readonly Dictionary<int, string> ClassNames = new()
    {
        [1] = "GameObject", [4] = "Transform", [21] = "Material", [28] = "Texture2D", [43] = "Mesh", [48] = "Shader",
        [49] = "TextAsset", [74] = "AnimationClip", [83] = "AudioClip", [114] = "MonoBehaviour", [115] = "MonoScript",
        [128] = "Font", [213] = "Sprite", [224] = "RectTransform",
    };

    public sealed class Sprite
    {
        public long PathId;
        public string Name = "";
        public float[] Rect = new float[4], TextureRect = new float[4];
        public float[] Pivot = new float[2];
        public PPtr Texture, AlphaTexture;
    }

    public sealed class Texture
    {
        public string Name = "";
        public int Width, Height, Format, MipCount;
        public byte[] Data = Array.Empty<byte>();
        public uint StreamOffset, StreamSize;
        public string StreamPath = "";
    }

    static float[] Floats(BinaryReader r, int n) { var f = new float[n]; for (int i = 0; i < n; i++) f[i] = r.ReadSingle(); return f; }

    static void Align(BinaryReader r, long start)
    {
        long at = r.BaseStream.Position - start;
        if (at % 4 != 0) r.BaseStream.Position += 4 - at % 4;
    }

    static string Str(BinaryReader r, long start)
    {
        int length = r.ReadInt32();
        if (length < 0 || length > 1 << 20) throw new InvalidDataException("string length " + length);
        var s = Encoding.UTF8.GetString(r.ReadBytes(length));
        Align(r, start);
        return s;
    }

    static int Count(BinaryReader r, int max = 1 << 22)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > max) throw new InvalidDataException("count " + n);
        return n;
    }

    /// <summary>Reads a Sprite; the caller checks the size used.</summary>
    public static Sprite ReadSprite(BinaryReader r, AssetFile.Entry e)
    {
        long start = e.Offset;
        var s = new Sprite { PathId = e.PathId, Name = Str(r, start), Rect = Floats(r, 4) };
        Floats(r, 2);           // m_Offset
        Floats(r, 4);           // m_Border
        r.ReadSingle();         // m_PixelsToUnits
        s.Pivot = Floats(r, 2);
        r.ReadUInt32();         // m_Extrude
        r.ReadByte(); Align(r, start); // m_IsPolygon
        s.Texture = AssetFile.ReadPPtr(r);
        s.AlphaTexture = AssetFile.ReadPPtr(r);
        r.ReadBytes(Count(r) * 12);              // vertices
        r.ReadBytes(Count(r) * 2); Align(r, start); // indices
        s.TextureRect = Floats(r, 4);
        Floats(r, 2);           // textureRectOffset
        r.ReadUInt32();         // settingsRaw
        Floats(r, 4);           // uvTransform
        return s;
    }

    public static Texture ReadTexture(BinaryReader r, AssetFile.Entry e)
    {
        long start = e.Offset;
        var t = new Texture { Name = Str(r, start), Width = r.ReadInt32(), Height = r.ReadInt32() };
        r.ReadInt32();          // m_CompleteImageSize
        t.Format = r.ReadInt32();
        t.MipCount = r.ReadInt32();
        r.ReadByte(); r.ReadByte(); Align(r, start); // m_IsReadable, m_ReadAllowed
        r.ReadInt32(); r.ReadInt32();                // m_ImageCount, m_TextureDimension
        r.ReadInt32(); r.ReadInt32(); r.ReadSingle(); r.ReadInt32(); // GLTextureSettings
        r.ReadInt32(); r.ReadInt32();                // m_LightmapFormat, m_ColorSpace
        t.Data = r.ReadBytes(Count(r, 1 << 28)); Align(r, start);
        t.StreamOffset = r.ReadUInt32(); t.StreamSize = r.ReadUInt32(); t.StreamPath = Str(r, start);
        return t;
    }

    /// <summary>The Resources list: each asset's path under a Resources folder (lower case) and its link.</summary>
    public static List<KeyValuePair<string, PPtr>> ReadResourceContainer(BinaryReader r, AssetFile.Entry e)
    {
        var list = new List<KeyValuePair<string, PPtr>>();
        for (int n = Count(r), i = 0; i < n; i++)
        {
            var path = Str(r, e.Offset);
            list.Add(new KeyValuePair<string, PPtr>(path, AssetFile.ReadPPtr(r)));
        }
        return list;
    }

    // ---- Sprites as pictures ----------------------------------------------------------------------------------

    /// <summary>
    /// Cuts sprites out of their textures as PNGs. Each texture is decoded once (kept for the next sprite on it);
    /// its file is opened through open (a file name in stbc_Data to the file, or null), its .resS beside it.
    /// </summary>
    public sealed class SpritePictures
    {
        readonly string dataDir;
        readonly Func<string, AssetFile?> open;
        readonly Dictionary<string, (Texture tex, byte[] rgba)?> textures = new();

        /// <summary>Problems met (a texture that can't be read or decoded), for the caller to show.</summary>
        public readonly List<string> Warnings = new();

        public SpritePictures(string dataDir, Func<string, AssetFile?> open)
        {
            this.dataDir = dataDir;
            this.open = open;
        }

        /// <summary>The sprite's pixels as a PNG, or null when its texture can't be read or decoded.</summary>
        public byte[]? Png(AssetFile spriteFile, Sprite s)
        {
            string texFile = spriteFile.FileOf(s.Texture) ?? Path.GetFileName(spriteFile.Path);
            var key = texFile + ":" + s.Texture.PathId;
            if (!textures.TryGetValue(key, out var decoded))
            {
                decoded = null;
                try
                {
                    var tf = open(texFile);
                    var te = tf?.Find(s.Texture.PathId);
                    if (tf != null && te != null && te.ClassId == Texture2DClass)
                    {
                        var r = tf.At(te);
                        var tex = ReadTexture(r, te);
                        if (r.BaseStream.Position - te.Offset != te.Size) Warnings.Add($"texture {key}: used {r.BaseStream.Position - te.Offset} of {te.Size} bytes");
                        var pixels = tex.Data;
                        if (tex.StreamSize > 0)
                        {
                            using var res = File.OpenRead(Path.Combine(dataDir, Path.GetFileName(tex.StreamPath)));
                            res.Position = tex.StreamOffset;
                            pixels = new byte[tex.StreamSize];
                            res.ReadExactly(pixels);
                        }
                        var rgba = Decode(tex.Format, tex.Width, tex.Height, pixels);
                        decoded = rgba == null ? null : (tex, rgba);
                        if (rgba == null) Warnings.Add($"texture {tex.Name}: format {tex.Format} not decoded");
                    }
                    else Warnings.Add($"texture {key}: not found");
                }
                catch (Exception ex) { Warnings.Add($"texture {key}: {ex.Message}"); }
                textures[key] = decoded;
            }
            if (decoded is not { } d) return null;
            int x0 = (int)Math.Round(s.TextureRect[0]), y0 = (int)Math.Round(s.TextureRect[1]);
            int w = (int)Math.Round(s.TextureRect[2]), h = (int)Math.Round(s.TextureRect[3]);
            x0 = Math.Clamp(x0, 0, d.tex.Width - 1); y0 = Math.Clamp(y0, 0, d.tex.Height - 1);
            w = Math.Clamp(w, 1, d.tex.Width - x0); h = Math.Clamp(h, 1, d.tex.Height - y0);
            var crop = new byte[w * h * 4];
            for (int y = 0; y < h; y++)   // Unity's rows go bottom up; a PNG's top down
                Buffer.BlockCopy(d.rgba, ((y0 + y) * d.tex.Width + x0) * 4, crop, (h - 1 - y) * w * 4, w * 4);
            return UnityAssets.Png(w, h, crop);
        }
    }

    // ---- Pixels -----------------------------------------------------------------------------------------------

    /// <summary>The top mip level as RGBA, rows bottom up as Unity stores them; null for a format not handled.</summary>
    public static byte[]? Decode(int format, int w, int h, byte[] src)
    {
        var o = new byte[w * h * 4];
        switch (format)
        {
            case 1: // Alpha8
                for (int i = 0; i < w * h; i++) { o[i * 4] = o[i * 4 + 1] = o[i * 4 + 2] = 255; o[i * 4 + 3] = src[i]; }
                return o;
            case 3: // RGB24
                for (int i = 0; i < w * h; i++) { o[i * 4] = src[i * 3]; o[i * 4 + 1] = src[i * 3 + 1]; o[i * 4 + 2] = src[i * 3 + 2]; o[i * 4 + 3] = 255; }
                return o;
            case 4: // RGBA32
                Buffer.BlockCopy(src, 0, o, 0, w * h * 4);
                return o;
            case 5: // ARGB32
                for (int i = 0; i < w * h; i++) { o[i * 4] = src[i * 4 + 1]; o[i * 4 + 1] = src[i * 4 + 2]; o[i * 4 + 2] = src[i * 4 + 3]; o[i * 4 + 3] = src[i * 4]; }
                return o;
            case 10: // DXT1
            case 12: // DXT5
            {
                int block = format == 10 ? 8 : 16, p = 0;
                for (int by = 0; by < (h + 3) / 4; by++)
                    for (int bx = 0; bx < (w + 3) / 4; bx++, p += block)
                    {
                        var alpha = new byte[16];
                        int c = p;
                        if (format == 12)
                        {
                            byte a0 = src[p], a1 = src[p + 1];
                            ulong bits = 0;
                            for (int i = 0; i < 6; i++) bits |= (ulong)src[p + 2 + i] << (8 * i);
                            var table = new byte[8];
                            table[0] = a0; table[1] = a1;
                            for (int i = 2; i < 8; i++)
                                table[i] = a0 > a1 ? (byte)(((8 - i) * a0 + (i - 1) * a1) / 7)
                                    : i < 6 ? (byte)(((6 - i) * a0 + (i - 1) * a1) / 5) : (byte)(i == 6 ? 0 : 255);
                            for (int i = 0; i < 16; i++) alpha[i] = table[(bits >> (3 * i)) & 7];
                            c = p + 8;
                        }
                        else for (int i = 0; i < 16; i++) alpha[i] = 255;
                        ushort c0 = (ushort)(src[c] | src[c + 1] << 8), c1 = (ushort)(src[c + 2] | src[c + 3] << 8);
                        var col = new int[4][];
                        col[0] = Rgb565(c0); col[1] = Rgb565(c1);
                        if (c0 > c1 || format == 12)
                        {
                            col[2] = new[] { (2 * col[0][0] + col[1][0]) / 3, (2 * col[0][1] + col[1][1]) / 3, (2 * col[0][2] + col[1][2]) / 3, 255 };
                            col[3] = new[] { (col[0][0] + 2 * col[1][0]) / 3, (col[0][1] + 2 * col[1][1]) / 3, (col[0][2] + 2 * col[1][2]) / 3, 255 };
                        }
                        else
                        {
                            col[2] = new[] { (col[0][0] + col[1][0]) / 2, (col[0][1] + col[1][1]) / 2, (col[0][2] + col[1][2]) / 2, 255 };
                            col[3] = new[] { 0, 0, 0, 0 };
                        }
                        uint idx = (uint)(src[c + 4] | src[c + 5] << 8 | src[c + 6] << 16 | src[c + 7] << 24);
                        for (int i = 0; i < 16; i++)
                        {
                            int x = bx * 4 + i % 4, y = by * 4 + i / 4;
                            if (x >= w || y >= h) continue;
                            var k = col[(idx >> (2 * i)) & 3];
                            int q = (y * w + x) * 4;
                            o[q] = (byte)k[0]; o[q + 1] = (byte)k[1]; o[q + 2] = (byte)k[2];
                            o[q + 3] = format == 12 ? alpha[i] : (byte)k[3];
                        }
                    }
                return o;
            }
            default:
                return null;
        }
    }

    static int[] Rgb565(ushort v) => new[] { (v >> 11 & 31) * 255 / 31, (v >> 5 & 63) * 255 / 63, (v & 31) * 255 / 31, 255 };

    public static byte[] Png(int w, int h, byte[] rgba)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        void Chunk(string type, byte[] data)
        {
            var len = BitConverter.GetBytes(data.Length); Array.Reverse(len); ms.Write(len);
            var t = Encoding.ASCII.GetBytes(type);
            ms.Write(t); ms.Write(data);
            var crc = BitConverter.GetBytes(Crc(t.Concat(data).ToArray())); Array.Reverse(crc); ms.Write(crc);
        }
        var ihdr = new byte[13];
        var bw = BitConverter.GetBytes(w); Array.Reverse(bw); bw.CopyTo(ihdr, 0);
        var bh = BitConverter.GetBytes(h); Array.Reverse(bh); bh.CopyTo(ihdr, 4);
        ihdr[8] = 8; ihdr[9] = 6;
        Chunk("IHDR", ihdr);
        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.Optimal, true))
                for (int y = 0; y < h; y++) { z.WriteByte(0); z.Write(rgba, y * w * 4, w * 4); }
            Chunk("IDAT", raw.ToArray());
        }
        Chunk("IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    static uint Crc(byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in data)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return ~c;
    }
}
