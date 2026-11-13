using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace StbcRevival
{
    internal static partial class HullName
    {
        private static Face kelvinFace;
        private static Face KelvinFace()
        {
            if (kelvinFace != null) return kelvinFace;
            var dir = Path.Combine(CustomShip.Find("AbramsEnterprise").Folder, "Michroma");
            var data = File.ReadAllBytes(Path.Combine(dir, "Michroma-atlas.bin"));
            if (data.Length != 2048 * 2048) throw new IOException("Invalid Michroma atlas");
            var glyphs = new Dictionary<char, Glyph>();
            foreach (var line in File.ReadAllLines(Path.Combine(dir, "Michroma-glyphs.txt"))) {
                var f = line.Split(' ').Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                glyphs[(char)(int)f[0]] = new Glyph { MinX=f[1], MaxX=f[2], MinY=f[3], MaxY=f[4], Advance=f[5],
                    BL=new Vector2(f[6]/2048f,f[7]/2048f), BR=new Vector2(f[8]/2048f,f[7]/2048f), TL=new Vector2(f[6]/2048f,f[9]/2048f), Found=true };
            }
            return kelvinFace = new Face { Name="Michroma Regular", Values=data, W=2048, H=2048,
                Get=c => glyphs.TryGetValue(c, out var g) ? g : glyphs['?'] };
        }
        private sealed class KelvinPaint
        {
            internal string File, Want;
            internal bool Pending, Mirror;
            internal Texture2D Done;
            internal Line[] Lines;
        }
        private static readonly KelvinPaint[] kelvinPaints = {
            new KelvinPaint { File = "tex_41_SaucerTop_jpg.png", Lines = new[] {
                new Line { Shape = Shape.Straight, Cx = 1500, Cy = 876, Cap = 50, Says = Says.Name,
                    Reference = "U.S.S. ENTERPRISE", ReferenceLength = 910, MaxLength = 1100, Tracking = .06f, Style = EnterprisePlain },
                new Line { Shape = Shape.Smile, Cx = 1500, Cy = 1500, Radius = 990, Mid = -90, Cap = 180, Says = Says.Registry,
                    Reference = "NCC-1701", ReferenceLength = 1500, MaxLength = 1700, Tracking = .12f, Style = EnterprisePlain }
            } },
            new KelvinPaint { File = "tex_29_NacelleOutside_jpg.png", Lines = new[] { KelvinNacelleLine() } },
            new KelvinPaint { File = "tex_30_NacelleOutsideLeft_jp.png", Mirror = true, Lines = new[] { KelvinNacelleLine() } }
        };
        private static Line KelvinNacelleLine() => new Line { Shape = Shape.Straight, Cx = 1120, Cy = 183, Cap = 32,
            Says = Says.Registry, Reference = "NCC-1701", ReferenceLength = 335, MaxLength = 500, Tracking = .12f, Style = EnterprisePlain };

        private static void UpdateKelvin(Material[] mats, string name, string registry)
        {
            foreach (var paint in kelvinPaints)
            {
                var mine = mats.Where(m => m != null && m.name.StartsWith("AbramsEnterprise ", StringComparison.Ordinal)
                    && m.HasProperty("_MainTex") && m.GetTexture("_MainTex") != null
                    && (m.GetTexture("_MainTex").name == paint.File || m.GetTexture("_MainTex") == paint.Done)).ToArray();
                if (mine.Length == 0) continue;
                string want = name + "|" + registry + "|" + Plugin.HullFont.Value;
                if (paint.Want != want && !paint.Pending)
                {
                    var texts = paint.Lines.Select(l => l.Says == Says.Name ? HullText(name) :
                        (registry.Length > 0 ? registry : "NCC-1701").ToUpperInvariant()).ToArray();
                    // Kelvin-only extended, light-stroke lettering; other ships keep HullFont.
                    var face = KelvinFace();
                    if (face == null) continue;
                    var path = Path.Combine(CustomShip.Find("AbramsEnterprise").Folder, paint.File);
                    var read = new Texture2D(2, 2, TextureFormat.RGB24, false);
                    Color32[] px; int w, h;
                    try {
                        if (!read.LoadImage(File.ReadAllBytes(path))) throw new IOException("Cannot decode " + path);
                        w = read.width; h = read.height; px = read.GetPixels32();
                    } finally { UnityEngine.Object.Destroy(read); }
                    paint.Pending = true;
                    Run("Kelvin lettering " + paint.File, () => {
                        // Work from the unmodified disk texture every time, so changed names never accumulate.
                        if (w == 3000 && h == 3000) {
                            KelvinErase(px, w, h, 1025, 2075, 1980, 2170);
                            KelvinErase(px, w, h, 760, 2175, 2230, 2620);
                        } else if (w == 2000 && h == 284) KelvinErase(px, w, h, 940, 70, 1305, 130);
                        else throw new InvalidOperationException("Unexpected Kelvin texture dimensions: " + w + "x" + h);
                        for (int n = 0; n < paint.Lines.Length; n++) {
                            var line = paint.Lines[n]; var laid = Lay(face, line, texts[n]);
                            var area = Bounds(laid, 12, Math.Max(w, h)); if (area == null) continue;
                            var cover = new float[area.W * area.H]; Raster(face, laid, area, cover);
                            var distance = Distance(cover, area.W, area.H);
                            float cap = laid.CapUnits * laid.Scale, gap = line.Style.Gap * cap, rim = line.Style.RimWidth * cap;
                            for (int y = 0; y < area.H; y++) for (int x = 0; x < area.W; x++) {
                                int gx = area.X + x, gy = area.Y + y, at = y * area.W + x;
                                if (paint.Mirror) gx = (int)(2 * line.Cx) - gx;
                                if (gx < 0 || gx >= w || gy < 0 || gy >= h) continue;
                                float c = cover[at], r = rim > 0 ? Band(distance[at], gap, gap + rim) : 0;
                                int i = gy * w + gx;
                                if (r > 0) px[i] = Color32.Lerp(px[i], line.Style.Rim, r);
                                if (c > 0) px[i] = Color32.Lerp(px[i], line.Style.Fill, c);
                            }
                        }
                        return () => {
                            var tex = new Texture2D(w, h, TextureFormat.RGB24, true) { name = paint.File + " (crew lettering)", anisoLevel = 4 };
                            tex.SetPixels32(px); tex.Apply(true, false); tex.Compress(false); tex.Apply(false, true);
                            UnityEngine.Object.DontDestroyOnLoad(tex);
                            var old = paint.Done; paint.Done = tex; paint.Want = want; Swap(old, tex);
                            if (Plugin.HullPreview.Value) SavePreview(px, w, h, "hull-name-Kelvin-" + paint.File);
                            Plugin.Log.LogInfo("Hull name: Kelvin " + paint.File + ": " + string.Join(" / ", texts) + " in " + face.Name);
                        };
                    }, () => paint.Pending = false);
                }
                if (paint.Done == null) continue;
                foreach (var m in mine) {
                    if (!originals.ContainsKey(m)) originals[m] = new[] { m.GetTexture("_MainTex"), (Texture)null };
                    m.SetTexture("_MainTex", paint.Done);
                }
            }
        }

        // Remove only the dark printed lettering plus its antialiasing border. Grow nearby hull
        // colours inward; the surrounding aztec panels and stripes remain untouched. Coordinates y-down.
        private static void KelvinErase(Color32[] px, int w, int h, int x0, int y0, int x1, int y1)
        {
            int rw = x1 - x0 + 1, rh = y1 - y0 + 1;
            var mask = new bool[rw * rh];
            for (int y = 0; y < rh; y++) for (int x = 0; x < rw; x++) {
                var c = px[(h - 1 - y0 - y) * w + x0 + x];
                if (Math.Min(c.r, Math.Min(c.g, c.b)) >= 180) continue;
                for (int dy = -4; dy <= 4; dy++) for (int dx = -4; dx <= 4; dx++)
                    if (x + dx >= 0 && x + dx < rw && y + dy >= 0 && y + dy < rh) mask[(y + dy) * rw + x + dx] = true;
            }
            var queue = new Queue<int>();
            for (int i = 0; i < mask.Length; i++) if (!mask[i]) queue.Enqueue(i);
            while (queue.Count > 0) {
                int at = queue.Dequeue(), x = at % rw, y = at / rw;
                var c = px[(h - 1 - y0 - y) * w + x0 + x];
                for (int d = 0; d < 4; d++) {
                    int nx = x + (d == 0 ? -1 : d == 1 ? 1 : 0), ny = y + (d == 2 ? -1 : d == 3 ? 1 : 0);
                    if (nx < 0 || nx >= rw || ny < 0 || ny >= rh || !mask[ny * rw + nx]) continue;
                    mask[ny * rw + nx] = false;
                    px[(h - 1 - y0 - ny) * w + x0 + nx] = c; queue.Enqueue(ny * rw + nx);
                }
            }
        }
    }
}
