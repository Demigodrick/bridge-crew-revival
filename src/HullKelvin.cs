using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// The Kelvin Enterprise's name and registry (the saucer's "U.S.S. ENTERPRISE" and "NCC-1701", the nacelles'
    /// "NCC-1701"), painted into its own textures in Michroma, a font that comes with the model (an atlas beside it:
    /// Unity 5 doesn't reliably find per-user Windows fonts). As the game ships' hull numbers (HullPaint): the
    /// texture's DXT1 blocks, kept as it was made (CustomShip), the piece with the lettering decoded, painted over and
    /// encoded again in every mip that still shows it, all on the worker thread; only the texture is made on the main thread.
    /// </summary>
    internal static partial class HullName
    {
        private const string KelvinShip = "AbramsEnterprise";

        private sealed class KelvinPaint
        {
            public string File;
            public int DesignW, DesignH;   // the texture's size as the places below were measured (it may come smaller)
            public bool Mirror;            // the port nacelle's texture is the starboard one mirrored: its letters too
            public int[][] Erase;          // the old lettering: x0, y0, x1, y1 (pixels, y up, inclusive)
            public Line[] Lines;
            public Texture2D Done;
            public string DrawnFor;
            public bool Pending;
        }

        private static Line KelvinNacelleLine() => new Line { Shape = Shape.Straight, Cx = 1120, Cy = 183, Cap = 32,
            Says = Says.Registry, Reference = "NCC-1701", ReferenceLength = 335, MaxLength = 500, Tracking = .12f, Style = EnterprisePlain };

        // HullName spans several files: their static field order follows compiler input order.
        // Resolve EnterprisePlain only after the entire type has finished initializing.
        private static KelvinPaint[] kelvinPaintCache;
        private static KelvinPaint[] kelvinPaints => kelvinPaintCache ?? (kelvinPaintCache = CreateKelvinPaints());
        private static KelvinPaint[] CreateKelvinPaints() => new KelvinPaint[]
        {
            new KelvinPaint
            {
                File = "tex_41_SaucerTop_jpg.png", DesignW = 3000, DesignH = 3000,
                Erase = new[] { new[] { 1025, 829, 1980, 924 }, new[] { 760, 379, 2230, 824 } },
                Lines = new[]
                {
                    new Line { Shape = Shape.Straight, Cx = 1500, Cy = 876, Cap = 50, Says = Says.Name,
                        Reference = "U.S.S. ENTERPRISE", ReferenceLength = 910, MaxLength = 1100, Tracking = .06f, Style = EnterprisePlain },
                    new Line { Shape = Shape.Smile, Cx = 1500, Cy = 1500, Radius = 990, Mid = -90, Cap = 180, Says = Says.Registry,
                        Reference = "NCC-1701", ReferenceLength = 1500, MaxLength = 1700, Tracking = .12f, Style = EnterprisePlain },
                },
            },
            new KelvinPaint { File = "tex_29_NacelleOutside_jpg.png", DesignW = 2000, DesignH = 284, Erase = new[] { new[] { 940, 153, 1305, 213 } }, Lines = new[] { KelvinNacelleLine() } },
            new KelvinPaint { File = "tex_30_NacelleOutsideLeft_jp.png", DesignW = 2000, DesignH = 284, Mirror = true, Erase = new[] { new[] { 940, 153, 1305, 213 } }, Lines = new[] { KelvinNacelleLine() } },
        };

        // ---- the font ----

        private static Face kelvinFace;
        private static bool kelvinFaceTried;

        /// <summary>Michroma, from the atlas beside the model (read once); null if it isn't there (logged once).</summary>
        private static Face KelvinFace()
        {
            if (kelvinFace != null || kelvinFaceTried) return kelvinFace;
            kelvinFaceTried = true;
            try
            {
                var dir = CustomShip.Find(KelvinShip).Folder;
                var data = File.ReadAllBytes(Path.Combine(dir, "Michroma-atlas.bin"));
                if (data.Length != 2048 * 2048) throw new InvalidDataException("Michroma-atlas.bin isn't a 2048x2048 atlas");
                var glyphs = new Dictionary<char, Glyph>();
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                foreach (var line in File.ReadAllLines(Path.Combine(dir, "Michroma-glyphs.txt")))
                {
                    var p = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length < 10) continue;
                    var f = p.Select(s => float.Parse(s, inv)).ToArray();
                    glyphs[(char)(int)f[0]] = new Glyph
                    {
                        MinX = f[1], MaxX = f[2], MinY = f[3], MaxY = f[4], Advance = f[5],
                        BL = new Vector2(f[6] / 2048f, f[7] / 2048f), BR = new Vector2(f[8] / 2048f, f[7] / 2048f), TL = new Vector2(f[6] / 2048f, f[9] / 2048f), Found = true,
                    };
                }
                if (!glyphs.TryGetValue('?', out var unknown)) throw new InvalidDataException("Michroma-glyphs.txt has no '?'");
                kelvinFace = new Face { Name = "Michroma Regular", Values = data, W = 2048, H = 2048, Get = c => glyphs.TryGetValue(c, out var g) ? g : unknown };
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Hull name: the Kelvin's font: {e.Message}; its hull keeps the lettering it has"); }
            return kelvinFace;
        }

        // ---- painting ----

        private static void UpdateKelvin(Material[] mats, string name, string registry)
        {
            foreach (var paint in kelvinPaints)
            {
                try
                {
                    var mine = mats.Where(m => m != null && m.name.StartsWith(KelvinShip + " ", StringComparison.Ordinal) && m.HasProperty("_MainTex")
                        && m.GetTexture("_MainTex") is Texture t && (t.name == paint.File || t == paint.Done)).ToArray();
                    if (mine.Length == 0) continue;
                    string want = name + "|" + registry;
                    if (paint.DrawnFor != want && !paint.Pending)
                    {
                        paint.DrawnFor = want; // tried once per name, whether it works or not (failures are logged)
                        var original = mine.Select(m => m.GetTexture("_MainTex")).FirstOrDefault(t => t != paint.Done);
                        DrawKelvin(paint, name, registry, original);
                    }
                    if (paint.Done == null) continue;
                    foreach (var m in mine)
                    {
                        if (m.GetTexture("_MainTex") == paint.Done) continue;
                        if (!originals.ContainsKey(m)) originals[m] = new[] { m.GetTexture("_MainTex"), (Texture)null };
                        m.SetTexture("_MainTex", paint.Done);
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning($"Hull name: the Kelvin's {paint.File}: {e}"); }
            }
        }

        private static Line Scaled(Line l, float s) => new Line
        {
            Shape = l.Shape, Cx = l.Cx * s, Cy = l.Cy * s, Radius = l.Radius * s, Mid = l.Mid, Angle = l.Angle, Cap = l.Cap * s, Says = l.Says,
            Reference = l.Reference, ReferenceLength = l.ReferenceLength * s, MaxLength = l.MaxLength * s, Tracking = l.Tracking, Style = l.Style,
        };

        /// <summary>Starts painting one of the Kelvin's textures: the font read here, the blocks painted on the worker thread.</summary>
        private static void DrawKelvin(KelvinPaint paint, string name, string registry, Texture original)
        {
            var face = KelvinFace();
            var raw = CustomShip.Find(KelvinShip)?.BlocksOf(paint.File);
            if (face == null) return;
            if (raw == null) { Plugin.Log.LogInfo($"Hull name: the Kelvin's {paint.File} has no blocks kept; its lettering stays as it is"); return; }
            var texts = paint.Lines.Select(l => l.Says == Says.Name ? HullText(name) : (registry.Length > 0 ? registry : l.Reference).ToUpperInvariant()).ToArray();
            bool preview = Plugin.HullPreview.Value;
            paint.Pending = true;
            Run("Kelvin lettering " + paint.File, () =>
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                int w = raw.W, h = raw.H;
                float s = (float)w / paint.DesignW;
                if (Mathf.Abs(h - paint.DesignH * s) > 4f) throw new InvalidDataException($"{w}x{h}, not the shape of the {paint.DesignW}x{paint.DesignH} it was measured on");

                // The piece decoded: the old lettering and the new, in whole blocks for as many mips as it can be.
                var lines = paint.Lines.Select(l => Scaled(l, s)).ToArray();
                var laid = lines.Select((l, i) => Lay(face, l, texts[i])).ToArray();
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                foreach (var e in paint.Erase)
                {
                    minX = Mathf.Min(minX, e[0] * s); minY = Mathf.Min(minY, e[1] * s); maxX = Mathf.Max(maxX, e[2] * s); maxY = Mathf.Max(maxY, e[3] * s);
                }
                for (int n = 0; n < laid.Length; n++)
                {
                    var b = Bounds(laid[n], 4f, Math.Max(w, h));
                    if (b == null) continue;
                    float x0 = b.X, x1 = b.X + b.W - 1;
                    if (paint.Mirror) { float m0 = 2f * lines[n].Cx - x1, m1 = 2f * lines[n].Cx - x0; x0 = m0; x1 = m1; }
                    minX = Mathf.Min(minX, x0); maxX = Mathf.Max(maxX, x1); minY = Mathf.Min(minY, b.Y); maxY = Mathf.Max(maxY, b.Y + b.H - 1);
                }
                const int align = 4 << (PaintMips - 1);
                int ax = Math.Max(0, Mathf.FloorToInt(minX) / align * align), ay = Math.Max(0, Mathf.FloorToInt(minY) / align * align);
                int ax1 = Math.Min(w, (Mathf.CeilToInt(maxX) / align + 1) * align), ay1 = Math.Min(h, (Mathf.CeilToInt(maxY) / align + 1) * align);
                int mips = 0;
                while (mips < Math.Min(PaintMips, raw.MipCount) && ((ax | ay | ax1 | ay1) & ((4 << mips) - 1)) == 0) mips++;
                if (mips == 0 || ax1 <= ax || ay1 <= ay) throw new InvalidDataException($"the lettering's piece ({ax},{ay})-({ax1},{ay1}) isn't in whole blocks");
                int aw = ax1 - ax, ah = ay1 - ay;

                long expected = 0;
                for (int m = 0; m < raw.MipCount; m++) expected += Math.Max(1, (Math.Max(1, w >> m) + 3) / 4) * Math.Max(1, (Math.Max(1, h >> m) + 3) / 4) * 8;
                if (raw.Data.Length != expected) throw new InvalidDataException($"{raw.Data.Length} bytes of blocks, {expected} expected");
                var data = (byte[])raw.Data.Clone(); // the kept blocks stay as made: a later name starts from them again
                var px = DecodeDxt1(data, 0, w, ax, ay, aw, ah, 8);

                int grow = Math.Max(2, Mathf.RoundToInt(4f * s));
                foreach (var e in paint.Erase)
                    KelvinErase(px, aw, ah, ax, ay, Mathf.RoundToInt(e[0] * s), Mathf.RoundToInt(e[1] * s), Mathf.RoundToInt(e[2] * s), Mathf.RoundToInt(e[3] * s), grow);

                for (int n = 0; n < laid.Length; n++)
                {
                    var area = Bounds(laid[n], 2f, Math.Max(w, h));
                    if (area == null) continue;
                    var cover = new float[area.W * area.H];
                    Raster(face, laid[n], area, cover);
                    var fill = lines[n].Style.Fill;
                    int mirrorAt = Mathf.RoundToInt(2f * lines[n].Cx);
                    for (int y = 0; y < area.H; y++)
                        for (int x = 0; x < area.W; x++)
                        {
                            float c = cover[y * area.W + x];
                            if (c <= 0f) continue;
                            int gx = area.X + x, gy = area.Y + y;
                            if (paint.Mirror) gx = mirrorAt - gx;
                            gx -= ax; gy -= ay;
                            if (gx < 0 || gy < 0 || gx >= aw || gy >= ah) continue;
                            int i = gy * aw + gx;
                            px[i] = Color32.Lerp(px[i], fill, c);
                        }
                }

                // Back into the blocks, each mip that still shows it from the painted piece.
                int offset = 0;
                for (int m = 0; m < raw.MipCount; m++)
                {
                    int mw = Math.Max(1, w >> m), mh = Math.Max(1, h >> m);
                    if (m < mips) EncodeDxt1(Downsample(px, aw, ah, m), aw >> m, ah >> m, data, offset, mw, ax >> m, ay >> m, 8);
                    offset += Math.Max(1, (mw + 3) / 4) * Math.Max(1, (mh + 3) / 4) * 8;
                }
                long ms = watch.ElapsedMilliseconds;
                return () =>
                {
                    var old = paint.Done;
                    paint.Done = FromBlocks(paint.File + " (crew's name)", w, h, TextureFormat.DXT1, raw.MipCount > 1, false, data, original);
                    Swap(old, paint.Done);
                    if (preview) SavePreview(px, aw, ah, "hull-name-Kelvin-" + paint.File);
                    Plugin.Log.LogInfo($"Hull name: the Kelvin's {paint.File}: {string.Join(" / ", texts)} painted in {ms} ms (off the main thread)");
                };
            }, () => paint.Pending = false);
        }

        /// <summary>
        /// The old lettering in (x0, y0)-(x1, y1) (texture pixels, y up, inclusive) taken off the piece decoded at (ax, ay):
        /// its dark pixels and a border of <paramref name="grow"/> round them, filled in from the hull colours around,
        /// grown inward. The panels and stripes round it stay as they are.
        /// </summary>
        private static void KelvinErase(Color32[] px, int aw, int ah, int ax, int ay, int x0, int y0, int x1, int y1, int grow)
        {
            x0 = Math.Max(x0, ax); y0 = Math.Max(y0, ay); x1 = Math.Min(x1, ax + aw - 1); y1 = Math.Min(y1, ay + ah - 1);
            if (x1 < x0 || y1 < y0) return;
            int rw = x1 - x0 + 1, rh = y1 - y0 + 1;
            int At(int x, int y) => (y0 - ay + y) * aw + (x0 - ax + x);
            var mask = new bool[rw * rh];
            for (int y = 0; y < rh; y++)
                for (int x = 0; x < rw; x++)
                {
                    var c = px[At(x, y)];
                    if (Math.Min(c.r, Math.Min(c.g, c.b)) >= 180) continue;
                    for (int dy = -grow; dy <= grow; dy++)
                        for (int dx = -grow; dx <= grow; dx++)
                            if (x + dx >= 0 && x + dx < rw && y + dy >= 0 && y + dy < rh) mask[(y + dy) * rw + x + dx] = true;
                }
            var queue = new Queue<int>();
            for (int i = 0; i < mask.Length; i++) if (!mask[i]) queue.Enqueue(i);
            while (queue.Count > 0)
            {
                int at = queue.Dequeue(), x = at % rw, y = at / rw;
                var c = px[At(x, y)];
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + (d == 0 ? -1 : d == 1 ? 1 : 0), ny = y + (d == 2 ? -1 : d == 3 ? 1 : 0);
                    if (nx < 0 || nx >= rw || ny < 0 || ny >= rh || !mask[ny * rw + nx]) continue;
                    mask[ny * rw + nx] = false;
                    px[At(nx, ny)] = c;
                    queue.Enqueue(ny * rw + nx);
                }
            }
        }
    }
}
