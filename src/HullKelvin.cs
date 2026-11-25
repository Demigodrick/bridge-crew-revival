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
    /// texture's DXT blocks (DXT5 as LoadImage makes it, or DXT1), kept as it was made (CustomShip), the piece with the lettering decoded, painted over and
    /// encoded again in every mip that still shows it, all on the worker thread; only the texture is made on the main thread.
    /// </summary>
    internal static partial class HullName
    {
        private const string KelvinShip = "AbramsEnterprise";

        private sealed class KelvinPaint
        {
            public string Ship = KelvinShip;  // the class whose texture it is (the Kelvin's, the Miranda's)
            public string File;
            public bool[] MirrorLines;     // lines laid mirrored on their own (a texture mapped onto both sides)
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

        // Made on first use, not as a static field: HullKelvin.cs is compiled before HullName.cs, so its static fields
        // are set first and EnterprisePlain (HullName.cs) would still be null here.
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

            // The Miranda: its saucer's registry (black, rimmed red) and name in arcs over and under it, and the
            // registry on its nacelles (two of them mapped mirrored). Measured on its textures as they come (4096).
            new KelvinPaint
            {
                Ship = "Miranda", File = "Miranda_1_DIFF.png", DesignW = 4096, DesignH = 4096,
                Erase = new[] { new[] { 1150, 620, 2950, 1210 }, new[] { 1590, 1005, 2505, 1270 } },
                Lines = new[]
                {
                    new Line { Shape = Shape.Smile, Cx = 2047, Cy = 1695, Radius = 931, Mid = -90, Cap = 228, Says = Says.Registry,
                        Reference = "NCC-31060", ReferenceLength = 1800, MaxLength = 2100, Tracking = .10f, Style = EnterpriseRimmed },
                    new Line { Shape = Shape.Smile, Cx = 2047, Cy = 1695, Radius = 641, Mid = -90, Cap = 62, Says = Says.Name,
                        Reference = "U.S.S. MAJESTIC", ReferenceLength = 1000, MaxLength = 1150, Tracking = .16f, Style = EnterpriseSmallRimmed },
                },
            },
            new KelvinPaint
            {
                Ship = "Miranda", File = "Miranda_2_DIFF.png", DesignW = 4096, DesignH = 4096,
                // The saucer bottom's registry (its arc's middle reaches higher than the first box), its bare name, the
                // name on a hull plate, the name and registry upside down on another, and "STARSHIP U.S.S. …" twice in a band.
                Erase = new[]
                {
                    new[] { 1130, 2925, 2175, 3165 }, new[] { 1340, 3150, 1960, 3202 }, new[] { 1470, 1800, 1840, 1878 },
                    new[] { 3125, 3265, 3297, 3289 }, new[] { 180, 972, 297, 1000 }, new[] { 894, 4029, 1010, 4041 }, new[] { 2933, 4029, 3049, 4041 },
                },
                Lines = new[]
                {
                    new Line { Shape = Shape.Frown, Cx = 1652, Cy = 2273, Radius = 795, Mid = 90, Cap = 228, Says = Says.Registry,
                        Reference = "NCC-31060", ReferenceLength = 1046, MaxLength = 1250, Tracking = .10f, Style = EnterpriseRimmed },
                    new Line { Shape = Shape.Smile, Cx = 1652, Cy = 2273, Radius = 450, Mid = -90, Cap = 30, Says = Says.BareName,
                        Reference = "MAJESTIC", ReferenceLength = 358, MaxLength = 420, Tracking = .15f, Style = EnterprisePlain },
                    new Line { Shape = Shape.Straight, Cx = 3211, Cy = 3277, Cap = 17, Says = Says.BareName,
                        Reference = "MAJESTIC", ReferenceLength = 165, MaxLength = 200, Tracking = .12f, Style = EnterprisePlain },
                    new Line { Shape = Shape.Straight, Cx = 238.5f, Cy = 991.5f, Angle = 180, Cap = 10, Says = Says.BareName,
                        Reference = "MAJESTIC", ReferenceLength = 110, MaxLength = 130, Tracking = .12f, Style = EnterprisePlain },
                    new Line { Shape = Shape.Straight, Cx = 238.5f, Cy = 979.5f, Angle = 180, Cap = 8, Says = Says.Registry,
                        Reference = "NCC-31060", ReferenceLength = 82, MaxLength = 100, Tracking = .08f, Style = EnterprisePlain },
                    MirandaStarshipLine(952, 4035, 7, 110), MirandaStarshipLine(2991, 4035, 7, 110),
                },
            },
            new KelvinPaint
            {
                Ship = "Miranda", File = "Miranda_3_DIFF.png", DesignW = 4096, DesignH = 4096,
                // The nacelles' registries; the big tilted one (mirrored, rimmed: erased letter by letter, its small
                // "UNITED FEDERATION OF PLANETS" under it kept); and "STARSHIP U.S.S. …" in the red bands, two of them
                // mirrored and one tilted (erased in steps along it).
                Erase = new[]
                {
                    new[] { 1290, 2946, 1606, 2982 }, new[] { 2217, 2911, 2533, 2946 }, new[] { 3022, 2907, 3338, 2943 }, new[] { 3304, 3245, 3620, 3281 },
                    new[] { 335, 601, 409, 662 }, new[] { 412, 611, 482, 672 }, new[] { 485, 621, 559, 682 }, new[] { 562, 630, 593, 687 },
                    new[] { 595, 636, 664, 697 }, new[] { 669, 663, 692, 681 }, new[] { 694, 649, 768, 711 }, new[] { 771, 659, 845, 721 }, new[] { 848, 669, 931, 733 },
                    new[] { 1550, 2067, 1837, 2080 }, new[] { 2382, 1832, 2670, 1845 }, new[] { 1544, 1544, 1832, 1557 }, new[] { 3068, 1672, 3356, 1685 },
                    new[] { 1215, 837, 1285, 859 }, new[] { 1285, 829, 1355, 851 }, new[] { 1355, 821, 1425, 843 }, new[] { 1425, 813, 1501, 836 },
                },
                MirrorLines = new[] { false, true, true, false, true, false, false, true, true, false },
                Lines = new[]
                {
                    MirandaNacelleLine(1448, 2964), MirandaNacelleLine(2375, 2929), MirandaNacelleLine(3180, 2925), MirandaNacelleLine(3462, 3263),
                    new Line { Shape = Shape.Straight, Cx = 630.75f, Cy = 666.25f, Angle = -7.65f, Cap = 52, Says = Says.Registry,
                        Reference = "NCC-31060", ReferenceLength = 586, MaxLength = 650, Tracking = .10f, Style = EnterpriseRimmed },
                    MirandaStarshipLine(1693.5f, 2073.5f, 8, 282), MirandaStarshipLine(2526, 1838.5f, 8, 282),
                    MirandaStarshipLine(1688, 1550.5f, 8, 282), MirandaStarshipLine(3212, 1678.5f, 8, 282),
                    new Line { Shape = Shape.Straight, Cx = 1358, Cy = 836, Angle = -6.44f, Cap = 8, Says = Says.Name, Before = "STARSHIP ",
                        Reference = "STARSHIP U.S.S. MAJESTIC", ReferenceLength = 282, MaxLength = 300, Tracking = .2f, Style = EnterprisePlain },
                },
            },
        };

        private static Line MirandaStarshipLine(float cx, float cy, float cap, float length) => new Line { Shape = Shape.Straight, Cx = cx, Cy = cy, Cap = cap,
            Says = Says.Name, Before = "STARSHIP ", Reference = "STARSHIP U.S.S. MAJESTIC", ReferenceLength = length, MaxLength = length * 1.08f, Tracking = .2f, Style = EnterprisePlain };

        private static Line MirandaNacelleLine(float cx, float cy) => new Line { Shape = Shape.Straight, Cx = cx, Cy = cy, Cap = 30, Says = Says.Registry,
            Reference = "NCC-31060", ReferenceLength = 302, MaxLength = 360, Tracking = .08f, Style = EnterprisePlain };

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
                    var mine = mats.Where(m => m != null && m.name.StartsWith(paint.Ship + " ", StringComparison.Ordinal) && m.HasProperty("_MainTex")
                        && m.GetTexture("_MainTex") is Texture t && (t.name == paint.File || t == paint.Done)).ToArray();
                    if (mine.Length == 0) continue;
                    string want = name + "|" + registry;
                    // Painted only while the Kelvin is the ship flown (its model stays loaded once built; another class's name
                    // isn't wanted on it), or for the preview.
                    bool flown = CustomShip.InUse != null && CustomShip.InUse.Name == paint.Ship;
                    if (paint.DrawnFor != want && !paint.Pending && (flown || Plugin.HullPreview.Value))
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
            Before = l.Before, After = l.After,
        };

        /// <summary>What a Kelvin-painted line says: the hull name (or the bare name), the registry, or its number, with its fixed text round it.</summary>
        private static string KelvinText(Line l, string name, string registry)
        {
            string reg = (registry.Length > 0 ? registry : l.Reference).ToUpperInvariant();
            string hull = HullText(name);
            string says = l.Says == Says.Name ? hull
                : l.Says == Says.BareName ? (hull.StartsWith("U.S.S. ", StringComparison.Ordinal) ? hull.Substring(7) : hull)
                : l.Says == Says.Number ? NumberOf(reg).ToUpperInvariant()
                : reg;
            return (l.Before ?? "") + says + (l.After ?? "");
        }

        /// <summary>Starts painting one of the Kelvin's textures: the font read here, the blocks painted on the worker thread.</summary>
        private static void DrawKelvin(KelvinPaint paint, string name, string registry, Texture original)
        {
            var face = KelvinFace();
            var raw = CustomShip.Find(paint.Ship)?.BlocksOf(paint.File);
            if (face == null) return;
            if (raw == null) { Plugin.Log.LogInfo($"Hull name: the Kelvin's {paint.File} has no blocks kept; its lettering stays as it is"); return; }
            var texts = paint.Lines.Select(l => KelvinText(l, name, registry)).ToArray();
            bool preview = Plugin.HullPreview.Value;
            paint.Pending = true;
            Run("Kelvin lettering " + paint.File, () =>
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                int w = raw.W, h = raw.H, block = raw.BlockBytes; // DXT5: its alpha half stays as it is, only the colours are painted
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
                    float rimPad = lines[n].Style.RimWidth > 0f ? (lines[n].Style.Gap + lines[n].Style.RimWidth) * lines[n].Cap + 2f : 0f;
                    x0 -= rimPad; x1 += rimPad; minY = Mathf.Min(minY, b.Y - rimPad); maxY = Mathf.Max(maxY, b.Y + b.H - 1 + rimPad);
                    if (paint.Mirror || (paint.MirrorLines != null && paint.MirrorLines[n])) { float m0 = 2f * lines[n].Cx - x1, m1 = 2f * lines[n].Cx - x0; x0 = m0; x1 = m1; }
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
                for (int m = 0; m < raw.MipCount; m++) expected += Math.Max(1, (Math.Max(1, w >> m) + 3) / 4) * Math.Max(1, (Math.Max(1, h >> m) + 3) / 4) * block;
                if (raw.Data.Length != expected) throw new InvalidDataException($"{raw.Data.Length} bytes of blocks, {expected} expected");
                var data = (byte[])raw.Data.Clone(); // the kept blocks stay as made: a later name starts from them again
                var px = DecodeDxt1(data, 0, w, ax, ay, aw, ah, block);

                int grow = Math.Max(2, Mathf.RoundToInt(4f * s));
                foreach (var e in paint.Erase)
                    KelvinErase(px, aw, ah, ax, ay, Mathf.RoundToInt(e[0] * s), Mathf.RoundToInt(e[1] * s), Mathf.RoundToInt(e[2] * s), Mathf.RoundToInt(e[3] * s), grow);

                for (int n = 0; n < laid.Length; n++)
                {
                    var st = lines[n].Style;
                    float pad = st.RimWidth > 0f ? (st.Gap + st.RimWidth) * lines[n].Cap + 2f : 2f;
                    var area = Bounds(laid[n], pad, Math.Max(w, h));
                    if (area == null) continue;
                    var cover = new float[area.W * area.H];
                    Raster(face, laid[n], area, cover);
                    bool mirrored = paint.Mirror || (paint.MirrorLines != null && paint.MirrorLines[n]);
                    int mirrorAt = Mathf.RoundToInt(2f * lines[n].Cx);
                    // A rimmed style: the rim (and the gap inside it, hull white) round the letters first, grown from them.
                    var layers = new List<KeyValuePair<float[], Color32>>();
                    if (st.RimWidth > 0f)
                    {
                        layers.Add(new KeyValuePair<float[], Color32>(Grown(cover, area.W, area.H, (st.Gap + st.RimWidth) * lines[n].Cap), st.Rim));
                        if (st.Gap > 0f) layers.Add(new KeyValuePair<float[], Color32>(Grown(cover, area.W, area.H, st.Gap * lines[n].Cap), new Color32(0xF2, 0xF2, 0xF2, 255)));
                    }
                    layers.Add(new KeyValuePair<float[], Color32>(cover, st.Fill));
                    foreach (var layer in layers)
                        for (int y = 0; y < area.H; y++)
                            for (int x = 0; x < area.W; x++)
                            {
                                float c = layer.Key[y * area.W + x];
                                if (c <= 0f) continue;
                                int gx = area.X + x, gy = area.Y + y;
                                if (mirrored) gx = mirrorAt - gx;
                                gx -= ax; gy -= ay;
                                if (gx < 0 || gy < 0 || gx >= aw || gy >= ah) continue;
                                int i = gy * aw + gx;
                                px[i] = Color32.Lerp(px[i], layer.Value, c);
                            }
                }

                // Back into the blocks, each mip that still shows it from the painted piece.
                int offset = 0;
                for (int m = 0; m < raw.MipCount; m++)
                {
                    int mw = Math.Max(1, w >> m), mh = Math.Max(1, h >> m);
                    if (m < mips) EncodeDxt1(Downsample(px, aw, ah, m), aw >> m, ah >> m, data, offset, mw, ax >> m, ay >> m, block);
                    offset += Math.Max(1, (mw + 3) / 4) * Math.Max(1, (mh + 3) / 4) * block;
                }
                long ms = watch.ElapsedMilliseconds;
                return () =>
                {
                    var old = paint.Done;
                    paint.Done = FromBlocks(paint.File + " (crew's name)", w, h, raw.Format, raw.MipCount > 1, false, data, original);
                    Swap(old, paint.Done);
                    if (preview) SavePreview(px, aw, ah, "hull-name-Kelvin-" + paint.File);
                    Plugin.Log.LogInfo($"Hull name: the Kelvin's {paint.File}: {string.Join(" / ", texts)} painted in {ms} ms (off the main thread)");
                };
            }, () => paint.Pending = false);
        }

        /// <summary>A coverage grown outward by a radius (pixels): a round brush's reach, for a rim round letters.</summary>
        private static float[] Grown(float[] cover, int w, int h, float radius)
        {
            int r = Math.Max(1, Mathf.CeilToInt(radius));
            var grown = new float[cover.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float m = 0f;
                    for (int dy = -r; dy <= r; dy++)
                    {
                        int yy = y + dy; if (yy < 0 || yy >= h) continue;
                        // the brush round: each row's reach narrows with its distance
                        int reach = Mathf.FloorToInt(Mathf.Sqrt(Math.Max(0f, radius * radius - dy * dy)));
                        float v = 0f;
                        for (int dx = -reach; dx <= reach; dx++) { int xx = x + dx; if (xx >= 0 && xx < w) v = Math.Max(v, cover[yy * w + xx]); }
                        m = Math.Max(m, v);
                    }
                    grown[y * w + x] = m;
                }
            return grown;
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
