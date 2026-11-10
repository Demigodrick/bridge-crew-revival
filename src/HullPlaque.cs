using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// The Aegis bridge's dedication plaque (material Aegis_Plaque): "USS AEGIS" and "STARFLEET REGISTRY NX-1787" become
    /// the crew's. Its 512² DXT1 texture (also its occlusion map) gets the two lines painted over and written again in
    /// the plaque's engraved style, light letters with a thin grey outline; its 256² normal map (ARGB4444, x in alpha,
    /// y in green) is smoothed there and the new letters pressed in, so the old ones don't show through.
    /// </summary>
    internal static partial class HullName
    {
        private const string PlaqueFile = "sharedassets6.assets", PlaqueDiffuse = "AEGIS_Dedication_Plaque_D", PlaqueNormal = "AEGIS_Dedication_Plaque_N";
        private const int PlaqueAreaY = 128, PlaqueAreaH = 256; // the piece of the 512² texture with both lines (whole blocks down to mip 5)

        private static readonly Style PlaqueStyle = new Style { Fill = new Color32(255, 255, 255, 255), Rim = new Color32(0x9C, 0x9C, 0x9C, 255), RimWidth = 0.04f };

        private sealed class PlaqueLine
        {
            public Line Line;
            public int EraseX0, EraseX1, EraseY0, EraseY1;
        }

        // Measured on the plaque (texture pixels, y up): each line's middle and capital height, and its panel's inside.
        private static readonly PlaqueLine[] PlaqueLines =
        {
            new PlaqueLine
            {
                Line = new Line { Shape = Shape.Straight, Cx = 258f, Cy = 266f, Cap = 36f, Says = Says.Name, Reference = "USS AEGIS", ReferenceLength = 323f, MaxLength = 320f, Tracking = 0.08f, Style = PlaqueStyle },
                EraseX0 = 96, EraseX1 = 420, EraseY0 = 244, EraseY1 = 290,
            },
            new PlaqueLine
            {
                Line = new Line { Shape = Shape.Straight, Cx = 255f, Cy = 161f, Cap = 20f, Says = Says.Registry, Reference = "STARFLEET REGISTRY NX-1787", ReferenceLength = 286f, MaxLength = 300f, Tracking = 0.04f, Style = PlaqueStyle },
                EraseX0 = 96, EraseX1 = 414, EraseY0 = 147, EraseY1 = 175,
            },
        };

        private static Texture2D plaqueD, plaqueN;
        private static string plaqueFor;

        private static void UpdatePlaque(Material[] mats, string name, string registry)
        {
            var mine = mats.Where(m => m != null && m.HasProperty("_MainTex") && m.GetTexture("_MainTex") is Texture t && (t.name == PlaqueDiffuse || (plaqueD != null && t == plaqueD))).ToList();
            if (mine.Count == 0) return;
            string want = name + "|" + registry + "|" + Plugin.HullFont.Value;
            if (plaqueFor != want)
            {
                plaqueFor = want; // tried once per name, whether it works or not (failures are logged)
                if (!DrawPlaque(name, registry)) return;
            }
            if (plaqueD == null) return;
            foreach (var m in mine)
            {
                if (m.GetTexture("_MainTex") == plaqueD) continue;
                if (!originals.ContainsKey(m))
                    originals[m] = new[] { m.GetTexture("_MainTex"), null, m.HasProperty("_OcclusionMap") ? m.GetTexture("_OcclusionMap") : null, m.HasProperty("_BumpMap") ? m.GetTexture("_BumpMap") : null };
                m.SetTexture("_MainTex", plaqueD);
                if (m.HasProperty("_OcclusionMap") && m.GetTexture("_OcclusionMap") is Texture occ && occ.name == PlaqueDiffuse) m.SetTexture("_OcclusionMap", plaqueD);
                if (plaqueN != null && m.HasProperty("_BumpMap")) m.SetTexture("_BumpMap", plaqueN);
            }
        }

        private static bool DrawPlaque(string name, string registry)
        {
            var started = Time.realtimeSinceStartup;
            try
            {
                string[] texts =
                {
                    name.Trim().ToUpperInvariant(),
                    "STARFLEET REGISTRY " + (registry.Length > 0 ? registry : "NX-1787").ToUpperInvariant(),
                };
                var face = LoadFace(string.Concat(texts));
                if (face == null) return false;
                if (!ReadTexture(PlaqueDiffuse, out var t, out var data, PlaqueFile)) return false;
                if (t.Format != 10) { Plugin.Log.LogWarning($"Hull name: {PlaqueDiffuse} is format {t.Format}, not DXT1"); return false; }
                int w = t.Width, h = t.Height, aw = w;

                // The diffuse: each line's panel cleared (each column carried across), the new line drawn.
                var px = DecodeDxt1(data, 0, w, 0, PlaqueAreaY, aw, PlaqueAreaH, 8);
                var height = new float[aw * PlaqueAreaH]; // the new letters, for the normal map
                for (int n = 0; n < PlaqueLines.Length; n++)
                {
                    var pl = PlaqueLines[n];
                    for (int x = pl.EraseX0; x <= pl.EraseX1; x++)
                    {
                        Color32 below = px[(pl.EraseY0 - 1 - PlaqueAreaY) * aw + x], above = px[(pl.EraseY1 + 1 - PlaqueAreaY) * aw + x];
                        for (int y = pl.EraseY0; y <= pl.EraseY1; y++)
                            px[(y - PlaqueAreaY) * aw + x] = Color32.Lerp(below, above, (y - pl.EraseY0 + 1f) / (pl.EraseY1 - pl.EraseY0 + 2f));
                    }
                    var l = Lay(face, pl.Line, texts[n]);
                    float rim = Mathf.Max(1.1f, pl.Line.Style.RimWidth * l.CapUnits * l.Scale);
                    var area = Bounds(l, rim + 3f, w);
                    if (area == null) continue;
                    var cover = new float[area.W * area.H];
                    Raster(face, l, area, cover);
                    var dist = Distance(cover, area.W, area.H);
                    for (int y = 0; y < area.H; y++)
                        for (int x = 0; x < area.W; x++)
                        {
                            int gx = area.X + x, gy = area.Y + y - PlaqueAreaY;
                            if (gx < 0 || gy < 0 || gx >= aw || gy >= PlaqueAreaH) continue;
                            int li = y * area.W + x, i = gy * aw + gx;
                            float c = cover[li], band = Band(dist[li], 0f, rim);
                            if (c <= 0f && band <= 0f) continue;
                            var col = Color32.Lerp(px[i], pl.Line.Style.Rim, band);
                            px[i] = Color32.Lerp(col, pl.Line.Style.Fill, c * (1f - band));
                            if (c > height[i]) height[i] = c;
                        }
                }
                var patched = (byte[])data.Clone();
                int offset = 0;
                for (int m = 0; m < t.MipCount; m++)
                {
                    int mw = Math.Max(1, w >> m), mh = Math.Max(1, h >> m);
                    if (m < PaintMips) EncodeDxt1(Downsample(px, aw, PlaqueAreaH, m), aw >> m, PlaqueAreaH >> m, patched, offset, mw, 0, PlaqueAreaY >> m, 8);
                    offset += Math.Max(1, (mw + 3) / 4) * Math.Max(1, (mh + 3) / 4) * 8;
                }
                if (plaqueD == null) { plaqueD = new Texture2D(w, h, TextureFormat.DXT1, t.MipCount > 1, t.ColorSpace == 0) { name = PlaqueDiffuse + " (crew's name)" }; UnityEngine.Object.DontDestroyOnLoad(plaqueD); }
                plaqueD.LoadRawTextureData(patched);
                plaqueD.Apply(false);

                // The normal map (half the size): the panels smoothed, the new letters pressed in.
                if (ReadTexture(PlaqueNormal, out var nt, out var ndata, PlaqueFile) && nt.Format == 2)
                {
                    int nw = nt.Width, nh = nt.Height, scale = w / nw;
                    var nx = new float[nw * nh]; var ny = new float[nw * nh];
                    for (int i = 0; i < nw * nh; i++)
                    {
                        int v = ndata[2 * i] | ndata[2 * i + 1] << 8;
                        nx[i] = ((v >> 12) & 15) / 15f; ny[i] = ((v >> 4) & 15) / 15f;
                    }
                    float H(int x, int y) // the new letters at normal-map resolution
                    {
                        float sum = 0f;
                        for (int dy = 0; dy < scale; dy++)
                            for (int dx = 0; dx < scale; dx++)
                            {
                                int gx = x * scale + dx, gy = y * scale + dy - PlaqueAreaY;
                                if (gx >= 0 && gy >= 0 && gx < aw && gy < PlaqueAreaH) sum += height[gy * aw + gx];
                            }
                        return sum / (scale * scale);
                    }
                    foreach (var pl in PlaqueLines)
                        for (int y = pl.EraseY0 / scale; y <= pl.EraseY1 / scale; y++)
                            for (int x = pl.EraseX0 / scale; x <= pl.EraseX1 / scale; x++)
                            {
                                const float press = 0.6f;
                                float dhx = (H(x + 1, y) - H(x - 1, y)) * 0.5f, dhy = (H(x, y + 1) - H(x, y - 1)) * 0.5f;
                                nx[y * nw + x] = Mathf.Clamp01(0.5f - press * dhx);
                                ny[y * nw + x] = Mathf.Clamp01(0.5f - press * dhy);
                            }
                    // All its mips again, from the top one.
                    var raw = new byte[ndata.Length];
                    int o = 0, lw = nw, lh = nh;
                    float[] cx = nx, cy = ny;
                    for (int m = 0; m < nt.MipCount && o < raw.Length; m++)
                    {
                        for (int i = 0; i < lw * lh; i++)
                        {
                            int a = Mathf.RoundToInt(cx[i] * 15f), g = Mathf.RoundToInt(cy[i] * 15f);
                            int v = a << 12 | g << 8 | g << 4 | g;
                            raw[o + 2 * i] = (byte)v; raw[o + 2 * i + 1] = (byte)(v >> 8);
                        }
                        o += lw * lh * 2;
                        int nwm = Math.Max(1, lw / 2), nhm = Math.Max(1, lh / 2);
                        var dx2 = new float[nwm * nhm]; var dy2 = new float[nwm * nhm];
                        for (int y = 0; y < nhm; y++)
                            for (int x = 0; x < nwm; x++)
                            {
                                int x0 = Math.Min(2 * x, lw - 1), x1 = Math.Min(2 * x + 1, lw - 1), y0 = Math.Min(2 * y, lh - 1), y1 = Math.Min(2 * y + 1, lh - 1);
                                dx2[y * nwm + x] = (cx[y0 * lw + x0] + cx[y0 * lw + x1] + cx[y1 * lw + x0] + cx[y1 * lw + x1]) / 4f;
                                dy2[y * nwm + x] = (cy[y0 * lw + x0] + cy[y0 * lw + x1] + cy[y1 * lw + x0] + cy[y1 * lw + x1]) / 4f;
                            }
                        cx = dx2; cy = dy2; lw = nwm; lh = nhm;
                    }
                    if (plaqueN == null) { plaqueN = new Texture2D(nw, nh, TextureFormat.ARGB4444, nt.MipCount > 1, true) { name = PlaqueNormal + " (crew's name)" }; UnityEngine.Object.DontDestroyOnLoad(plaqueN); }
                    plaqueN.LoadRawTextureData(raw);
                    plaqueN.Apply(false);
                }
                else Plugin.Log.LogWarning("Hull name: the plaque's normal map wasn't read; its old letters may show through");

                if (Plugin.HullPreview.Value)
                {
                    var preview = new Texture2D(aw, PlaqueAreaH, TextureFormat.RGBA32, false);
                    preview.SetPixels32(px); preview.Apply();
                    try { File.WriteAllBytes(Path.Combine(BepInEx.Paths.BepInExRootPath, "hull-name-Aegis-plaque.png"), preview.EncodeToPNG()); } catch { }
                    UnityEngine.Object.Destroy(preview);
                }
                Plugin.Log.LogInfo($"Hull name: the Aegis's plaque: \"{texts[0]}\" / \"{texts[1]}\" in {(Time.realtimeSinceStartup - started) * 1000f:0} ms");
                return true;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Hull name: painting the plaque: " + e); return false; }
        }
    }
}
