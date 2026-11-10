using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// The bridges' dedication plaques take the crew's ship name (and registry, where they have one):
    ///  - the Aegis's (AEGIS_Dedication_Plaque_D/_N): "USS AEGIS", "STARFLEET REGISTRY NX-1787"; engraved outlines,
    ///  - the Enterprise-D's (DedicationPlaque_AlbedoTransparency/_Normal): "U.S.S. ENTERPRISE" and
    ///    "GALAXY CLASS • STARFLEET REGISTRY NCC-1701-D", turned on its texture; raised brass letters,
    ///  - the original Enterprise's (dedicationplaque_D/_N): "USS ENTERPRISE"; raised brass letters.
    /// Each line's place is painted over (the plate around it carried across, or its plain colour) and the new line
    /// written in the plaque's style; the normal map is smoothed there and the new letters pressed in, so the old ones
    /// don't show in the light. The brass plaques' serif letters come from Windows' Times New Roman.
    /// </summary>
    internal static partial class HullName
    {
        private enum Engraving { Outlined, Raised, Sunk }

        private sealed class PlaqueLine
        {
            public Line Line;
            public Func<string, string, string> Text; // (name, registry) → the line
            public Engraving Look;
            public int EraseX0, EraseX1, EraseY0, EraseY1;
        }

        private sealed class Plaque
        {
            public string Ship, File, Diffuse, Normal, Font;
            public int AreaX, AreaY, AreaW, AreaH;   // the diffuse's piece with the lines (whole blocks down to mip 5)
            public bool PlainFill;                   // cleared with the plate's colour (a plain plate), else carried across
            public PlaqueLine[] Lines;
            public Texture2D D, N;
            public string DrawnFor;
        }

        private static string Upper(string s) => (s ?? "").Trim().ToUpperInvariant();

        private static readonly Plaque[] Plaques =
        {
            new Plaque
            {
                Ship = "Aegis", File = "sharedassets6.assets", Diffuse = "AEGIS_Dedication_Plaque_D", Normal = "AEGIS_Dedication_Plaque_N",
                AreaX = 0, AreaY = 128, AreaW = 512, AreaH = 256, PlainFill = true,
                Lines = new[]
                {
                    new PlaqueLine
                    {
                        Line = new Line { Shape = Shape.Straight, Cx = 258f, Cy = 267.5f, Cap = 42f, Reference = "USS AEGIS", ReferenceLength = 316f, MaxLength = 318f, Tracking = 0.08f },
                        Text = (n, r) => Upper(n), Look = Engraving.Outlined, EraseX0 = 94, EraseX1 = 416, EraseY0 = 245, EraseY1 = 290,
                    },
                    new PlaqueLine
                    {
                        Line = new Line { Shape = Shape.Straight, Cx = 255f, Cy = 159.5f, Cap = 23f, Reference = "STARFLEET REGISTRY NX-1787", ReferenceLength = 284f, MaxLength = 300f, Tracking = 0.04f },
                        Text = (n, r) => "STARFLEET REGISTRY " + Upper(r.Length > 0 ? r : "NX-1787"), Look = Engraving.Outlined, EraseX0 = 96, EraseX1 = 414, EraseY0 = 145, EraseY1 = 175,
                    },
                },
            },
            new Plaque
            {
                Ship = "Enterprise-D", File = "sharedassets21.assets", Diffuse = "DedicationPlaque_AlbedoTransparency", Normal = "DedicationPlaque_Normal", Font = "Times New Roman",
                AreaX = 128, AreaY = 0, AreaW = 256, AreaH = 512,
                Lines = new[]
                {
                    new PlaqueLine
                    {
                        Line = new Line { Shape = Shape.Straight, Cx = 242f, Cy = 258f, Angle = -90f, Cap = 25f, Reference = "U.S.S. ENTERPRISE", ReferenceLength = 344f, MaxLength = 350f, Tracking = 0.06f },
                        Text = (n, r) => HullText(n), Look = Engraving.Raised, EraseX0 = 225, EraseX1 = 259, EraseY0 = 80, EraseY1 = 436,
                    },
                    new PlaqueLine
                    {
                        Line = new Line { Shape = Shape.Straight, Cx = 209f, Cy = 294f, Angle = -90f, Cap = 6.5f, Reference = "GALAXY CLASS • STARFLEET REGISTRY NCC-1701-D", ReferenceLength = 192f, MaxLength = 200f, Tracking = 0.04f },
                        Text = (n, r) => "GALAXY CLASS • STARFLEET REGISTRY " + Upper(r.Length > 0 ? r : "NCC-1701-D"), Look = Engraving.Sunk, EraseX0 = 204, EraseX1 = 214, EraseY0 = 194, EraseY1 = 396,
                    },
                },
            },
            new Plaque
            {
                Ship = "Enterprise", File = "sharedassets13.assets", Diffuse = "dedicationplaque_D", Normal = "dedicationplaque_N", Font = "Times New Roman",
                AreaX = 0, AreaY = 256, AreaW = 1024, AreaH = 256,
                Lines = new[]
                {
                    new PlaqueLine
                    {
                        Line = new Line { Shape = Shape.Straight, Cx = 513f, Cy = 374f, Cap = 48f, Reference = "USS ENTERPRISE", ReferenceLength = 812f, MaxLength = 840f, Tracking = 0.1f },
                        Text = (n, r) => Upper(n), Look = Engraving.Raised, EraseX0 = 98, EraseX1 = 928, EraseY0 = 338, EraseY1 = 409,
                    },
                },
            },
        };

        private static void UpdatePlaque(Material[] mats, string name, string registry)
        {
            foreach (var plaque in Plaques)
            {
                var mine = mats.Where(m => m != null && m.HasProperty("_MainTex") && m.GetTexture("_MainTex") is Texture t && (t.name == plaque.Diffuse || (plaque.D != null && t == plaque.D))).ToList();
                if (mine.Count == 0) continue;
                string want = name + "|" + registry + "|" + Plugin.HullFont.Value;
                if (plaque.DrawnFor != want)
                {
                    plaque.DrawnFor = want; // tried once per name, whether it works or not (failures are logged)
                    if (!DrawPlaque(plaque, name, registry)) continue;
                }
                if (plaque.D == null) continue;
                foreach (var m in mine)
                {
                    if (m.GetTexture("_MainTex") == plaque.D) continue;
                    if (!originals.ContainsKey(m))
                        originals[m] = new[] { m.GetTexture("_MainTex"), null, m.HasProperty("_OcclusionMap") ? m.GetTexture("_OcclusionMap") : null, m.HasProperty("_BumpMap") ? m.GetTexture("_BumpMap") : null };
                    m.SetTexture("_MainTex", plaque.D);
                    if (m.HasProperty("_OcclusionMap") && m.GetTexture("_OcclusionMap") is Texture occ && occ.name == plaque.Diffuse) m.SetTexture("_OcclusionMap", plaque.D);
                    if (plaque.N != null && m.HasProperty("_BumpMap") && m.GetTexture("_BumpMap") is Texture bump && bump.name == plaque.Normal) m.SetTexture("_BumpMap", plaque.N);
                }
            }
        }

        private static Color32 Scaled(Color32 c, float k) =>
            new Color32((byte)Mathf.Clamp(c.r * k, 0f, 255f), (byte)Mathf.Clamp(c.g * k, 0f, 255f), (byte)Mathf.Clamp(c.b * k, 0f, 255f), c.a);

        private static Face PlaqueFace(Plaque plaque, string text)
        {
            if (plaque.Font != null)
            {
                string installed = null;
                try { installed = Font.GetOSInstalledFontNames().FirstOrDefault(f => string.Equals(f, plaque.Font, StringComparison.OrdinalIgnoreCase)); } catch { }
                if (installed != null) { var face = OsFace(installed, text, FontStyle.Bold); if (face != null) return face; }
            }
            return LoadFace(text);
        }

        private static bool DrawPlaque(Plaque plaque, string name, string registry)
        {
            var started = Time.realtimeSinceStartup;
            try
            {
                var texts = plaque.Lines.Select(l => l.Text(name, registry)).ToArray();
                var face = PlaqueFace(plaque, string.Concat(texts));
                if (face == null) return false;
                if (!ReadTexture(plaque.Diffuse, out var t, out var data, plaque.File)) return false;
                if (t.Format != 10 && t.Format != 12) { Plugin.Log.LogWarning($"Hull name: {plaque.Diffuse} is format {t.Format}"); return false; }
                int block = t.Format == 12 ? 16 : 8, w = t.Width, h = t.Height, aw = plaque.AreaW, ah = plaque.AreaH;
                var px = DecodeDxt1(data, 0, w, plaque.AreaX, plaque.AreaY, aw, ah, block);
                var height = new float[aw * ah]; // the new letters: + raised, - sunk; for the normal map
                int Idx(int x, int y) => (y - plaque.AreaY) * aw + (x - plaque.AreaX);

                for (int n = 0; n < plaque.Lines.Length; n++)
                {
                    var pl = plaque.Lines[n];
                    // Clear the old line.
                    bool across = Mathf.Abs(Mathf.Abs(pl.Line.Angle) - 90f) < 1f; // a turned line: carried from its left to its right
                    Color32 plain = default(Color32);
                    if (plaque.PlainFill)
                    {
                        var all = new List<Color32>();
                        for (int y = pl.EraseY0; y <= pl.EraseY1; y++) for (int x = pl.EraseX0; x <= pl.EraseX1; x++) all.Add(px[Idx(x, y)]);
                        plain = all.OrderBy(c => c.r + c.g + c.b).ElementAt(all.Count * 3 / 4);
                    }
                    for (int y = pl.EraseY0; y <= pl.EraseY1; y++)
                        for (int x = pl.EraseX0; x <= pl.EraseX1; x++)
                        {
                            Color32 c;
                            if (plaque.PlainFill) c = plain;
                            else if (across) c = Color32.Lerp(px[Idx(pl.EraseX0 - 1, y)], px[Idx(pl.EraseX1 + 1, y)], (x - pl.EraseX0 + 1f) / (pl.EraseX1 - pl.EraseX0 + 2f));
                            else c = Color32.Lerp(px[Idx(x, pl.EraseY0 - 1)], px[Idx(x, pl.EraseY1 + 1)], (y - pl.EraseY0 + 1f) / (pl.EraseY1 - pl.EraseY0 + 2f));
                            var keep = px[Idx(x, y)].a;
                            px[Idx(x, y)] = new Color32(c.r, c.g, c.b, keep);
                        }
                    // Write the new one.
                    var l = Lay(face, pl.Line, texts[n]);
                    float capPx = l.CapUnits * l.Scale;
                    float rim = pl.Look == Engraving.Sunk ? 0f : Mathf.Max(1.1f, (pl.Look == Engraving.Outlined ? 0.04f : 0.06f) * capPx);
                    var area = Bounds(l, rim + 3f, Math.Max(w, h));
                    if (area == null) continue;
                    var cover = new float[area.W * area.H];
                    Raster(face, l, area, cover);
                    var dist = Distance(cover, area.W, area.H);
                    for (int y = 0; y < area.H; y++)
                        for (int x = 0; x < area.W; x++)
                        {
                            int gx = area.X + x, gy = area.Y + y;
                            if (gx < plaque.AreaX || gy < plaque.AreaY || gx >= plaque.AreaX + aw || gy >= plaque.AreaY + ah) continue;
                            int li = y * area.W + x, i = Idx(gx, gy);
                            float c = cover[li], band = rim > 0f ? Band(dist[li], 0f, rim) : 0f;
                            if (c <= 0f && band <= 0f) continue;
                            var bg = px[i];
                            Color32 fill, edge;
                            switch (pl.Look)
                            {
                                case Engraving.Outlined: fill = new Color32(255, 255, 255, bg.a); edge = new Color32(0x9C, 0x9C, 0x9C, bg.a); break;
                                case Engraving.Raised: fill = Scaled(bg, 1.22f); edge = Scaled(bg, 0.5f); break;
                                default: fill = Scaled(bg, 0.5f); edge = bg; break;
                            }
                            var col = Color32.Lerp(bg, edge, band);
                            col = Color32.Lerp(col, fill, c * (1f - band));
                            px[i] = new Color32(col.r, col.g, col.b, bg.a);
                            float hgt = pl.Look == Engraving.Sunk ? -c : c;
                            if (Mathf.Abs(hgt) > Mathf.Abs(height[i])) height[i] = hgt;
                        }
                }
                var patched = (byte[])data.Clone();
                int offset = 0;
                for (int m = 0; m < t.MipCount; m++)
                {
                    int mw = Math.Max(1, w >> m), mh = Math.Max(1, h >> m);
                    if (m < PaintMips && (aw >> m) >= 4 && (ah >> m) >= 4)
                        EncodeDxt1(Downsample(px, aw, ah, m), aw >> m, ah >> m, patched, offset, mw, plaque.AreaX >> m, plaque.AreaY >> m, block);
                    offset += Math.Max(1, (mw + 3) / 4) * Math.Max(1, (mh + 3) / 4) * block;
                }
                if (plaque.D == null) { plaque.D = new Texture2D(w, h, block == 16 ? TextureFormat.DXT5 : TextureFormat.DXT1, t.MipCount > 1, t.ColorSpace == 0) { name = plaque.Diffuse + " (crew's name)" }; UnityEngine.Object.DontDestroyOnLoad(plaque.D); }
                plaque.D.LoadRawTextureData(patched);
                plaque.D.Apply(false);

                PressNormal(plaque, height, w);

                if (Plugin.HullPreview.Value)
                {
                    var preview = new Texture2D(aw, ah, TextureFormat.RGBA32, false);
                    preview.SetPixels32(px.Select(c => new Color32(c.r, c.g, c.b, 255)).ToArray()); preview.Apply();
                    try { File.WriteAllBytes(Path.Combine(BepInEx.Paths.BepInExRootPath, "hull-name-" + plaque.Ship + "-plaque.png"), preview.EncodeToPNG()); } catch { }
                    UnityEngine.Object.Destroy(preview);
                }
                Plugin.Log.LogInfo($"Hull name: the {plaque.Ship}'s plaque: \"{string.Join("\" / \"", texts)}\" in {face.Name}, {(Time.realtimeSinceStartup - started) * 1000f:0} ms");
                return true;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Hull name: painting the {plaque.Ship}'s plaque: {e}"); return false; }
        }

        // ---- the normal map ----

        private const float Press = 0.28f; // how deep the new letters look

        /// <summary>The new letters pressed into the plaque's normal map (x in alpha, y in green), the old ones smoothed away.</summary>
        private static void PressNormal(Plaque plaque, float[] height, int diffuseW)
        {
            if (plaque.Normal == null) return;
            if (!ReadTexture(plaque.Normal, out var nt, out var ndata, plaque.File)) { Plugin.Log.LogWarning($"Hull name: {plaque.Normal} wasn't read; the old letters may show through"); return; }
            int nw = nt.Width, nh = nt.Height, scale = Math.Max(1, diffuseW / nw), aw = plaque.AreaW, ah = plaque.AreaH;
            float Hgt(int x, int y) // the new letters at the normal map's size
            {
                float sum = 0f;
                for (int dy = 0; dy < scale; dy++)
                    for (int dx = 0; dx < scale; dx++)
                    {
                        int gx = x * scale + dx - plaque.AreaX, gy = y * scale + dy - plaque.AreaY;
                        if (gx >= 0 && gy >= 0 && gx < aw && gy < ah) sum += height[gy * aw + gx];
                    }
                return sum / (scale * scale);
            }
            void Normal(int x, int y, out float nx, out float ny)
            {
                float dhx = (Hgt(x + 1, y) - Hgt(x - 1, y)) * 0.5f, dhy = (Hgt(x, y + 1) - Hgt(x, y - 1)) * 0.5f;
                nx = Mathf.Clamp01(0.5f - Press * dhx); ny = Mathf.Clamp01(0.5f - Press * dhy);
            }
            bool InErase(int x, int y) => plaque.Lines.Any(l => x >= l.EraseX0 / scale && x <= l.EraseX1 / scale && y >= l.EraseY0 / scale && y <= l.EraseY1 / scale);

            Texture2D tex;
            if (nt.Format == 2) // ARGB4444, whole: every mip again from the top one
            {
                var cx = new float[nw * nh]; var cy = new float[nw * nh];
                for (int i = 0; i < nw * nh; i++) { int v = ndata[2 * i] | ndata[2 * i + 1] << 8; cx[i] = ((v >> 12) & 15) / 15f; cy[i] = ((v >> 4) & 15) / 15f; }
                for (int y = 0; y < nh; y++) for (int x = 0; x < nw; x++) if (InErase(x, y)) { Normal(x, y, out var a, out var b); cx[y * nw + x] = a; cy[y * nw + x] = b; }
                var raw = new byte[ndata.Length];
                int o = 0, lw = nw, lh = nh;
                for (int m = 0; m < nt.MipCount && o < raw.Length; m++)
                {
                    for (int i = 0; i < lw * lh; i++)
                    {
                        int a = Mathf.RoundToInt(cx[i] * 15f), g = Mathf.RoundToInt(cy[i] * 15f), v = a << 12 | g << 8 | g << 4 | g;
                        raw[o + 2 * i] = (byte)v; raw[o + 2 * i + 1] = (byte)(v >> 8);
                    }
                    o += lw * lh * 2;
                    int w2 = Math.Max(1, lw / 2), h2 = Math.Max(1, lh / 2);
                    var x2 = new float[w2 * h2]; var y2 = new float[w2 * h2];
                    for (int y = 0; y < h2; y++)
                        for (int x = 0; x < w2; x++)
                        {
                            int x0 = Math.Min(2 * x, lw - 1), x1 = Math.Min(2 * x + 1, lw - 1), y0 = Math.Min(2 * y, lh - 1), y1 = Math.Min(2 * y + 1, lh - 1);
                            x2[y * w2 + x] = (cx[y0 * lw + x0] + cx[y0 * lw + x1] + cx[y1 * lw + x0] + cx[y1 * lw + x1]) / 4f;
                            y2[y * w2 + x] = (cy[y0 * lw + x0] + cy[y0 * lw + x1] + cy[y1 * lw + x0] + cy[y1 * lw + x1]) / 4f;
                        }
                    cx = x2; cy = y2; lw = w2; lh = h2;
                }
                tex = plaque.N ?? new Texture2D(nw, nh, TextureFormat.ARGB4444, nt.MipCount > 1, true);
                tex.LoadRawTextureData(raw);
            }
            else if (nt.Format == 12) // DXT5: the piece with the lines, its alpha and colour blocks
            {
                int ax = plaque.AreaX / scale, ay = plaque.AreaY / scale, nAw = aw / scale, nAh = ah / scale;
                var px = DecodeDxt5(ndata, 0, nw, ax, ay, nAw, nAh);
                for (int y = ay; y < ay + nAh; y++)
                    for (int x = ax; x < ax + nAw; x++)
                        if (InErase(x, y))
                        {
                            Normal(x, y, out var a, out var b);
                            int i = (y - ay) * nAw + (x - ax);
                            byte g = (byte)Mathf.RoundToInt(b * 255f);
                            px[i] = new Color32(px[i].r, g, px[i].b, (byte)Mathf.RoundToInt(a * 255f));
                        }
                var patched = (byte[])ndata.Clone();
                int offset = 0;
                for (int m = 0; m < nt.MipCount; m++)
                {
                    int mw = Math.Max(1, nw >> m), mh = Math.Max(1, nh >> m);
                    if (m < PaintMips && (nAw >> m) >= 4 && (nAh >> m) >= 4)
                    {
                        var level = DownsampleRgba(px, nAw, nAh, m);
                        EncodeDxt1(level, nAw >> m, nAh >> m, patched, offset, mw, ax >> m, ay >> m, 16);
                        EncodeAlpha(level, nAw >> m, nAh >> m, patched, offset, mw, ax >> m, ay >> m);
                    }
                    offset += Math.Max(1, (mw + 3) / 4) * Math.Max(1, (mh + 3) / 4) * 16;
                }
                tex = plaque.N ?? new Texture2D(nw, nh, TextureFormat.DXT5, nt.MipCount > 1, true);
                tex.LoadRawTextureData(patched);
            }
            else { Plugin.Log.LogWarning($"Hull name: {plaque.Normal} is format {nt.Format}; left as it is"); return; }
            tex.name = plaque.Normal + " (crew's name)";
            tex.Apply(false);
            if (plaque.N == null) { plaque.N = tex; UnityEngine.Object.DontDestroyOnLoad(tex); }
        }

        /// <summary>A rectangle of a DXT5 level (whole blocks): colours and alpha, rows in Unity's order.</summary>
        private static Color32[] DecodeDxt5(byte[] d, int offset, int levelW, int x0, int y0, int w, int h)
        {
            var px = DecodeDxt1(d, offset, levelW, x0, y0, w, h, 16);
            int blocksW = Math.Max(1, (levelW + 3) / 4);
            var a = new byte[8];
            for (int by = 0; by < h / 4; by++)
                for (int bx = 0; bx < w / 4; bx++)
                {
                    int o = offset + (((y0 / 4) + by) * blocksW + (x0 / 4) + bx) * 16;
                    a[0] = d[o]; a[1] = d[o + 1];
                    if (a[0] > a[1]) for (int i = 1; i < 7; i++) a[i + 1] = (byte)(((7 - i) * a[0] + i * a[1]) / 7);
                    else { for (int i = 1; i < 5; i++) a[i + 1] = (byte)(((5 - i) * a[0] + i * a[1]) / 5); a[6] = 0; a[7] = 255; }
                    ulong bits = 0;
                    for (int i = 0; i < 6; i++) bits |= (ulong)d[o + 2 + i] << (8 * i);
                    for (int i = 0; i < 16; i++)
                    {
                        int k = (by * 4 + i / 4) * w + bx * 4 + i % 4;
                        var c = px[k];
                        px[k] = new Color32(c.r, c.g, c.b, a[(int)((bits >> (3 * i)) & 7)]);
                    }
                }
            return px;
        }

        /// <summary>The alpha halves of a DXT5 level's blocks at (x0, y0): the block's lowest and highest, eight steps between.</summary>
        private static void EncodeAlpha(Color32[] px, int w, int h, byte[] d, int offset, int levelW, int x0, int y0)
        {
            int blocksW = Math.Max(1, (levelW + 3) / 4);
            var pal = new int[8];
            for (int by = 0; by < h / 4; by++)
                for (int bx = 0; bx < w / 4; bx++)
                {
                    int lo = 255, hi = 0;
                    for (int i = 0; i < 16; i++) { int v = px[(by * 4 + i / 4) * w + bx * 4 + i % 4].a; if (v < lo) lo = v; if (v > hi) hi = v; }
                    int o = offset + ((y0 / 4 + by) * blocksW + x0 / 4 + bx) * 16;
                    ulong bits = 0;
                    if (hi == lo) { d[o] = (byte)hi; d[o + 1] = (byte)lo; }
                    else
                    {
                        d[o] = (byte)hi; d[o + 1] = (byte)lo;
                        pal[0] = hi; pal[1] = lo;
                        for (int i = 1; i < 7; i++) pal[i + 1] = ((7 - i) * hi + i * lo) / 7;
                        for (int i = 0; i < 16; i++)
                        {
                            int v = px[(by * 4 + i / 4) * w + bx * 4 + i % 4].a, best = 0, bestD = int.MaxValue;
                            for (int k = 0; k < 8; k++) { int dd = Math.Abs(v - pal[k]); if (dd < bestD) { bestD = dd; best = k; } }
                            bits |= (ulong)best << (3 * i);
                        }
                    }
                    for (int i = 0; i < 6; i++) d[o + 2 + i] = (byte)(bits >> (8 * i));
                }
        }

        private static Color32[] DownsampleRgba(Color32[] px, int w, int h, int mip)
        {
            if (mip == 0) return px;
            int f = 1 << mip, ow = w >> mip, oh = h >> mip;
            var o = new Color32[ow * oh];
            for (int y = 0; y < oh; y++)
                for (int x = 0; x < ow; x++)
                {
                    int r = 0, g = 0, b = 0, a = 0;
                    for (int yy = 0; yy < f; yy++)
                        for (int xx = 0; xx < f; xx++) { var p = px[(y * f + yy) * w + x * f + xx]; r += p.r; g += p.g; b += p.b; a += p.a; }
                    int n = f * f;
                    o[y * ow + x] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), (byte)(a / n));
                }
            return o;
        }
    }
}
