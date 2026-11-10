using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// Names painted into a ship's hull texture itself rather than on a decal. The Enterprise-D has "‖ 1701-D ‖" in yellow
    /// behind the bridge, in its 4096² DXT1 hull texture EnterpriseD_01_D. That texture isn't readable, so its blocks are
    /// read from the game's files, the piece with the number is decoded, painted over (the hull above and below it
    /// carried across, the crew's number drawn in) and encoded again, in every mip that still shows it.
    /// </summary>
    internal static partial class HullName
    {
        private sealed class Paint
        {
            public string Ship, Texture;
            public int AreaX, AreaY, AreaW, AreaH;          // the piece decoded and encoded again (multiples of 128: whole blocks down to mip 5)
            public int EraseX0, EraseX1, EraseY0, EraseY1;  // the old number, painted over with the hull around it
            public Line Line;
            public Texture2D Done;
            public string DrawnFor;
        }

        private const int PaintMips = 6; // mip 0 to 5; below that the number is a few texels

        private static readonly Paint[] Paints =
        {
            new Paint
            {
                Ship = "Enterprise-D", Texture = "EnterpriseD_01_D", AreaX = 768, AreaY = 0, AreaW = 512, AreaH = 256,
                EraseX0 = 905, EraseX1 = 1132, EraseY0 = 117, EraseY1 = 153,
                Line = new Line
                {
                    Shape = Shape.Straight, Cx = 1019f, Cy = 135f, Cap = 27f, Says = Says.Number, Reference = "1701-D", ReferenceLength = 200f, MaxLength = 219f, Tracking = 0.15f,
                    Style = new Style { Fill = new Color32(240, 204, 2, 255) },
                },
            },
        };

        /// <summary>"NCC-91234" → "91234": the number without its prefix, as the Enterprise-D's back writes it.</summary>
        private static string NumberOf(string registry) => Regex.Replace(registry.Trim(), @"^[A-Za-z]+[\s\-]*", "");

        private static void UpdatePaints(Material[] mats, string name, string registry)
        {
            foreach (var paint in Paints)
            {
                var mine = mats.Where(m => m != null && m.HasProperty("_MainTex") && m.GetTexture("_MainTex") is Texture t && (t.name == paint.Texture || t == paint.Done)).ToList();
                if (mine.Count == 0) continue;
                string want = registry + "|" + Plugin.HullFont.Value;
                if (paint.DrawnFor != want)
                {
                    var original = mine.Select(m => m.GetTexture("_MainTex")).FirstOrDefault(t => t != paint.Done);
                    if (!DrawPaint(paint, registry, original)) { paint.DrawnFor = want; continue; } // failed: logged, not tried again for this number
                    paint.DrawnFor = want;
                }
                foreach (var m in mine)
                {
                    if (m.GetTexture("_MainTex") == paint.Done) continue;
                    if (!originals.ContainsKey(m)) originals[m] = new[] { m.GetTexture("_MainTex"), null };
                    m.SetTexture("_MainTex", paint.Done);
                }
            }
        }

        private static bool DrawPaint(Paint paint, string registry, Texture original)
        {
            var started = Time.realtimeSinceStartup;
            try
            {
                string text = NumberOf(registry.Length > 0 ? registry : "1701-D").ToUpperInvariant();
                var face = LoadFace(text);
                if (face == null) return false;
                if (!ReadTexture(paint.Texture, out var t, out var data)) return false;
                if (t.Format != 10) { Plugin.Log.LogWarning($"Hull name: {paint.Texture} is format {t.Format}, not DXT1"); return false; }

                int w = t.Width, h = t.Height;
                var px = DecodeDxt1(data, 0, w, paint.AreaX, paint.AreaY, paint.AreaW, paint.AreaH);
                int aw = paint.AreaW;
                Color32 At(int x, int y) => px[(y - paint.AreaY) * aw + (x - paint.AreaX)];
                // The old number: each column carried across from just above it to just below it.
                for (int x = paint.EraseX0; x <= paint.EraseX1; x++)
                {
                    Color32 below = At(x, paint.EraseY0 - 1), above = At(x, paint.EraseY1 + 1);
                    for (int y = paint.EraseY0; y <= paint.EraseY1; y++)
                        px[(y - paint.AreaY) * aw + (x - paint.AreaX)] = Color32.Lerp(below, above, (y - paint.EraseY0 + 1f) / (paint.EraseY1 - paint.EraseY0 + 2f));
                }
                // The new one.
                var l = Lay(face, paint.Line, text);
                var area = Bounds(l, 2f, Math.Max(w, h));
                if (area != null)
                {
                    var cover = new float[area.W * area.H];
                    Raster(face, l, area, cover);
                    for (int y = 0; y < area.H; y++)
                        for (int x = 0; x < area.W; x++)
                        {
                            float c = cover[y * area.W + x];
                            int gx = area.X + x - paint.AreaX, gy = area.Y + y - paint.AreaY;
                            if (c <= 0f || gx < 0 || gy < 0 || gx >= aw || gy >= paint.AreaH) continue;
                            int i = gy * aw + gx;
                            px[i] = Color32.Lerp(px[i], paint.Line.Style.Fill, c);
                        }
                }
                // Back into the blocks, each mip from the painted piece.
                var patched = (byte[])data.Clone();
                int offset = 0;
                for (int m = 0; m < t.MipCount; m++)
                {
                    int mw = Math.Max(1, w >> m), mh = Math.Max(1, h >> m);
                    if (m < PaintMips)
                    {
                        var level = Downsample(px, aw, paint.AreaH, m);
                        EncodeDxt1(level, paint.AreaW >> m, paint.AreaH >> m, patched, offset, mw, paint.AreaX >> m, paint.AreaY >> m);
                    }
                    offset += Math.Max(1, (mw + 3) / 4) * Math.Max(1, (mh + 3) / 4) * 8;
                }

                if (paint.Done == null)
                {
                    paint.Done = new Texture2D(w, h, TextureFormat.DXT1, t.MipCount > 1, t.ColorSpace == 0) { name = paint.Texture + " (crew's number)" };
                    UnityEngine.Object.DontDestroyOnLoad(paint.Done);
                }
                if (original != null) { paint.Done.wrapMode = original.wrapMode; paint.Done.filterMode = original.filterMode; paint.Done.anisoLevel = original.anisoLevel; paint.Done.mipMapBias = original.mipMapBias; }
                paint.Done.LoadRawTextureData(patched);
                paint.Done.Apply(false);
                if (Plugin.HullPreview.Value)
                {
                    var preview = new Texture2D(aw, paint.AreaH, TextureFormat.RGBA32, false);
                    preview.SetPixels32(px); preview.Apply();
                    try { File.WriteAllBytes(Path.Combine(BepInEx.Paths.BepInExRootPath, "hull-name-" + paint.Ship + "-hull.png"), preview.EncodeToPNG()); } catch { }
                    UnityEngine.Object.Destroy(preview);
                }
                Plugin.Log.LogInfo($"Hull name: {paint.Ship}'s hull: \"{text}\" painted in {(Time.realtimeSinceStartup - started) * 1000f:0} ms");
                return true;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Hull name: painting {paint.Texture}: {e}"); return false; }
        }

        /// <summary>A texture's header and all its bytes (every mip), read from the game's resources.assets.</summary>
        private static bool ReadTexture(string name, out TextureAsset texture, out byte[] data)
        {
            texture = null; data = null;
            string dataDir = Application.dataPath;
            using (var file = new AssetFile(Path.Combine(dataDir, "resources.assets")))
            {
                foreach (var e in file.Objects)
                {
                    if (e.ClassId != 28) continue;
                    string n;
                    try { n = AssetFile.ReadString(file.At(e)); } catch { continue; }
                    if (n != name) continue;
                    texture = TextureAsset.Read(file.At(e));
                    break;
                }
            }
            if (texture == null) { Plugin.Log.LogWarning($"Hull name: {name} isn't in resources.assets"); return false; }
            if (texture.Data != null && texture.Data.Length > 0) { data = texture.Data; return true; }
            using (var s = new FileStream(Path.Combine(dataDir, texture.StreamPath), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                s.Position = texture.StreamOffset;
                data = new byte[texture.StreamSize];
                for (int got = 0; got < data.Length;)
                {
                    int r = s.Read(data, got, data.Length - got);
                    if (r <= 0) throw new EndOfStreamException(name);
                    got += r;
                }
            }
            return true;
        }

        private static Color32 C565(int c) => new Color32((byte)(((c >> 11) & 31) * 255 / 31), (byte)(((c >> 5) & 63) * 255 / 63), (byte)((c & 31) * 255 / 31), 255);

        /// <summary>A rectangle of a DXT1 level (whole blocks), as pixels in Unity's row order.</summary>
        private static Color32[] DecodeDxt1(byte[] d, int offset, int levelW, int x0, int y0, int w, int h)
        {
            var px = new Color32[w * h];
            var c = new Color32[4];
            int blocksW = Math.Max(1, (levelW + 3) / 4);
            for (int by = 0; by < h / 4; by++)
                for (int bx = 0; bx < w / 4; bx++)
                {
                    int o = offset + (((y0 / 4) + by) * blocksW + (x0 / 4) + bx) * 8;
                    int c0 = d[o] | d[o + 1] << 8, c1 = d[o + 2] | d[o + 3] << 8;
                    uint bits = (uint)(d[o + 4] | d[o + 5] << 8 | d[o + 6] << 16 | d[o + 7] << 24);
                    c[0] = C565(c0); c[1] = C565(c1);
                    if (c0 > c1) { c[2] = Color32.Lerp(c[0], c[1], 1f / 3f); c[3] = Color32.Lerp(c[0], c[1], 2f / 3f); }
                    else { c[2] = Color32.Lerp(c[0], c[1], 0.5f); c[3] = new Color32(0, 0, 0, 255); }
                    for (int i = 0; i < 16; i++)
                        px[(by * 4 + i / 4) * w + bx * 4 + i % 4] = c[(bits >> (2 * i)) & 3];
                }
            return px;
        }

        private static Color32[] Downsample(Color32[] px, int w, int h, int mip)
        {
            if (mip == 0) return px;
            int f = 1 << mip, ow = w >> mip, oh = h >> mip;
            var o = new Color32[ow * oh];
            for (int y = 0; y < oh; y++)
                for (int x = 0; x < ow; x++)
                {
                    int r = 0, g = 0, b = 0;
                    for (int yy = 0; yy < f; yy++)
                        for (int xx = 0; xx < f; xx++) { var p = px[(y * f + yy) * w + x * f + xx]; r += p.r; g += p.g; b += p.b; }
                    int n = f * f;
                    o[y * ow + x] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 255);
                }
            return o;
        }

        private static int To565(Color32 c) => (c.r * 31 + 127) / 255 << 11 | (c.g * 63 + 127) / 255 << 5 | (c.b * 31 + 127) / 255;

        /// <summary>Pixels into a DXT1 level's blocks at (x0, y0): the ends of each block's colour spread, four colours between.</summary>
        private static void EncodeDxt1(Color32[] px, int w, int h, byte[] d, int offset, int levelW, int x0, int y0)
        {
            int blocksW = Math.Max(1, (levelW + 3) / 4);
            var block = new Color32[16];
            var pal = new Color32[4];
            for (int by = 0; by < h / 4; by++)
                for (int bx = 0; bx < w / 4; bx++)
                {
                    for (int i = 0; i < 16; i++) block[i] = px[(by * 4 + i / 4) * w + bx * 4 + i % 4];
                    // The axis of the spread: from the darkest to the brightest pixel, refined to the extremes along it.
                    float mr = 0, mg = 0, mb = 0;
                    foreach (var p in block) { mr += p.r; mg += p.g; mb += p.b; }
                    mr /= 16f; mg /= 16f; mb /= 16f;
                    float ar = 0, ag = 0, ab = 0;
                    foreach (var p in block) { float dr = p.r - mr, dg = p.g - mg, db = p.b - mb, s = dr * 0.3f + dg * 0.59f + db * 0.11f; ar += dr * Math.Sign(s); ag += dg * Math.Sign(s); ab += db * Math.Sign(s); }
                    if (ar * ar + ag * ag + ab * ab < 1e-3f) { ar = 1; ag = 1; ab = 1; }
                    float lo = float.MaxValue, hi = float.MinValue; Color32 cLo = block[0], cHi = block[0];
                    foreach (var p in block) { float s = p.r * ar + p.g * ag + p.b * ab; if (s < lo) { lo = s; cLo = p; } if (s > hi) { hi = s; cHi = p; } }
                    int e0 = To565(cHi), e1 = To565(cLo);
                    if (e0 < e1) { int tmp = e0; e0 = e1; e1 = tmp; }
                    uint bits = 0;
                    if (e0 != e1)
                    {
                        pal[0] = C565(e0); pal[1] = C565(e1); pal[2] = Color32.Lerp(pal[0], pal[1], 1f / 3f); pal[3] = Color32.Lerp(pal[0], pal[1], 2f / 3f);
                        for (int i = 0; i < 16; i++)
                        {
                            int best = 0, bestD = int.MaxValue;
                            for (int k = 0; k < 4; k++)
                            {
                                int dr = block[i].r - pal[k].r, dg = block[i].g - pal[k].g, db = block[i].b - pal[k].b, dd = dr * dr + dg * dg + db * db;
                                if (dd < bestD) { bestD = dd; best = k; }
                            }
                            bits |= (uint)best << (2 * i);
                        }
                    }
                    int o = offset + ((y0 / 4 + by) * blocksW + x0 / 4 + bx) * 8;
                    d[o] = (byte)e0; d[o + 1] = (byte)(e0 >> 8); d[o + 2] = (byte)e1; d[o + 3] = (byte)(e1 >> 8);
                    d[o + 4] = (byte)bits; d[o + 5] = (byte)(bits >> 8); d[o + 6] = (byte)(bits >> 16); d[o + 7] = (byte)(bits >> 24);
                }
        }
    }
}
