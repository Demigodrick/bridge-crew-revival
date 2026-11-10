using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// The ship's name on the hull. The Aegis wears its name and registry as a decal (material Aegis_Name_Decal,
    /// textures Ship_Decals_D and the emission map Ship_Decals_E): one straight "U.S.S. AEGIS" line and "NX-1787" bent
    /// twice along the saucer's rim. The Enterprise-D's decal (Enterprise_D_Decals, EnterpriseD_Decal_D) has five lines
    /// running down the texture, two of them bent, and a gold plate. With a ship name set (<see cref="ShipName"/>), a
    /// loaded ship's decal is drawn again with the same lines at the same places, in the decal's own colours.
    /// The letters come from [Rooms] HullFont if that font is installed in Windows (e.g. Starfleet Bold Extended,
    /// which isn't shipped with the mod), else from the game's own United Sans.
    /// </summary>
    internal static partial class HullName
    {
        private enum Shape { Straight, Smile, Frown } // a smile reads with the angle rising, letters' tops to the centre; a frown the other way
        private enum Says { Name, BareName, Registry, Number } // "U.S.S. COSMO", "COSMO", "NCC-91234", "91234"

        /// <summary>How a line's letters are coloured: the fill, then a gap and up to two rims outside it (widths in capital heights).</summary>
        private sealed class Style
        {
            public Color32 Fill, Rim, Outer;
            public float Gap, RimWidth, OuterWidth;
        }

        /// <summary>Where a line of a decal is, in the texture's pixels (y up), and the original text it replaces.</summary>
        private sealed class Line
        {
            public Shape Shape;
            public float Cx, Cy, Radius, Mid; // the straight line's centre, or the arc's circle and middle angle (degrees)
            public float Angle;               // a straight line's reading direction (degrees; 0 = left to right)
            public float Cap;                 // capital height in pixels
            public Says Says;
            public string Reference;          // the game's text: our letters are stretched to fill its length...
            public float ReferenceLength, MaxLength; // ...and squeezed when longer than MaxLength
            public float Tracking;            // extra space between letters, in capital heights
            public Style Style;
        }

        /// <summary>A ship's name decal: which material and texture, its lines, and what else is on it.</summary>
        private sealed class Decal
        {
            public string Ship, Material, Texture, DefaultRegistry;
            public int Size;
            public bool Emission;             // also an emission map: the fill dark, everything else lit
            public Color32 Clear;             // the colour of the transparent background (for the mipmaps' edges)
            public Line[] Lines;
            public Action<Color32[], int> Extra;
            public Texture2D Diffuse, Glow;
            public string DrawnFor;
        }

        private static readonly Style AegisStyle = new Style { Fill = new Color32(0x22, 0x11, 0x22, 255), Rim = new Color32(0x77, 0x11, 0x11, 255), RimWidth = 0.022f, Outer = new Color32(0xCC, 0xCC, 0xCC, 255), OuterWidth = 0.03f };
        private static readonly Style EnterpriseRimmed = new Style { Fill = new Color32(0, 0, 0, 255), Gap = 0.02f, Rim = new Color32(0xB5, 0x39, 0x3A, 255), RimWidth = 0.033f };
        private static readonly Style EnterpriseSmallRimmed = new Style { Fill = new Color32(0, 0, 0, 255), Gap = 0.025f, Rim = new Color32(0xB5, 0x51, 0x52, 255), RimWidth = 0.034f };
        private static readonly Style EnterprisePlain = new Style { Fill = new Color32(0, 0, 0, 255) };

        // Measured on the game's textures: letter blobs, circles fitted through their centres.
        private static readonly Decal[] Decals =
        {
            new Decal
            {
                Ship = "Aegis", Material = "Aegis_Name_Decal", Texture = "Ship_Decals_D", DefaultRegistry = "NX-1787", Size = 1024, Emission = true, Clear = new Color32(0, 0, 0, 0),
                Lines = new[]
                {
                    new Line { Shape = Shape.Straight, Cx = 507f, Cy = 897f, Cap = 50f, Says = Says.Name, Reference = "U.S.S. AEGIS", ReferenceLength = 487f, MaxLength = 960f, Tracking = 0.06f, Style = AegisStyle },
                    new Line { Shape = Shape.Smile, Cx = 495f, Cy = 1303f, Radius = 651f, Mid = -88.4f, Cap = 132f, Says = Says.Registry, Reference = "NX-1787", ReferenceLength = 1000f, MaxLength = 1010f, Tracking = 0.12f, Style = AegisStyle },
                    new Line { Shape = Shape.Frown, Cx = 524f, Cy = -511f, Radius = 819f, Mid = 89.85f, Cap = 124f, Says = Says.Registry, Reference = "NX-1787", ReferenceLength = 968f, MaxLength = 975f, Tracking = 0.12f, Style = AegisStyle },
                },
            },
            new Decal
            {
                Ship = "Enterprise-D", Material = "Enterprise_D_Decals", Texture = "EnterpriseD_Decal_D", DefaultRegistry = "NCC 1701-D", Size = 2048, Clear = new Color32(0xBD, 0xBD, 0xBD, 0),
                Lines = new[]
                {
                    new Line { Shape = Shape.Smile, Cx = 3917f, Cy = 1016f, Radius = 3618f, Mid = 179.9f, Cap = 150f, Says = Says.Registry, Reference = "NCC 1701-D", ReferenceLength = 1950f, MaxLength = 1950f, Tracking = 0.2f, Style = EnterpriseRimmed },
                    new Line { Shape = Shape.Straight, Cx = 538f, Cy = 1025f, Angle = -90f, Cap = 59f, Says = Says.Name, Reference = "U.S.S. ENTERPRISE", ReferenceLength = 1411f, MaxLength = 1411f, Tracking = 0.3f, Style = EnterpriseSmallRimmed },
                    new Line { Shape = Shape.Frown, Cx = -131f, Cy = 473f, Radius = 1484f, Mid = 0.3f, Cap = 66f, Says = Says.Registry, Reference = "NCC 1701-D", ReferenceLength = 850f, MaxLength = 850f, Tracking = 0.2f, Style = EnterprisePlain },
                    new Line { Shape = Shape.Straight, Cx = 1677f, Cy = 1035f, Angle = -90f, Cap = 123f, Says = Says.Registry, Reference = "NCC 1701-D", ReferenceLength = 1687f, MaxLength = 1687f, Tracking = 0.3f, Style = EnterprisePlain },
                    new Line { Shape = Shape.Straight, Cx = 1949f, Cy = 1025f, Angle = -90f, Cap = 123f, Says = Says.BareName, Reference = "ENTERPRISE", ReferenceLength = 1931f, MaxLength = 1931f, Tracking = 0.3f, Style = EnterprisePlain },
                },
                Extra = GoldPlate,
            },
        };

        private static readonly Dictionary<Material, Texture[]> originals = new Dictionary<Material, Texture[]>();
        private static float nextScan;

        internal static void Update()
        {
            if (Time.unscaledTime < nextScan) return;
            nextScan = Time.unscaledTime + 2f;
            try
            {
                string name = ShipName.Name, registry = ShipName.Registry;
                if (name.Length == 0) { Restore(); return; }
                var mats = Resources.FindObjectsOfTypeAll<Material>();
                foreach (var decal in Decals)
                {
                    var mine = mats.Where(m => IsDecal(decal, m)).ToList();
                    if (mine.Count == 0 && !Plugin.HullPreview.Value) continue; // that ship isn't loaded (the preview draws them all)
                    string want = name + "|" + registry + "|" + Plugin.HullFont.Value;
                    if (decal.DrawnFor != want) { if (!Draw(decal, name, registry)) { nextScan = Time.unscaledTime + 30f; return; } decal.DrawnFor = want; }
                    foreach (var m in mine)
                    {
                        if (m.GetTexture("_MainTex") == decal.Diffuse) continue;
                        if (!originals.ContainsKey(m)) originals[m] = new[] { m.GetTexture("_MainTex"), decal.Emission ? m.GetTexture("_EmissionMap") : null };
                        m.SetTexture("_MainTex", decal.Diffuse);
                        if (decal.Emission) m.SetTexture("_EmissionMap", decal.Glow);
                    }
                }
                UpdatePaints(mats, name, registry);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Hull name: " + e); nextScan = Time.unscaledTime + 30f; }
        }

        private static bool IsDecal(Decal decal, Material m)
        {
            if (m == null || !m.HasProperty("_MainTex")) return false;
            if (m.name.StartsWith(decal.Material, StringComparison.Ordinal)) return true;
            var t = m.GetTexture("_MainTex");
            return t != null && (t.name == decal.Texture || (decal.Diffuse != null && t == decal.Diffuse));
        }

        private static void Restore()
        {
            foreach (var kv in originals)
                if (kv.Key != null)
                {
                    kv.Key.SetTexture("_MainTex", kv.Value[0]);
                    if (kv.Value[1] != null) kv.Key.SetTexture("_EmissionMap", kv.Value[1]);
                }
            originals.Clear();
            foreach (var d in Decals) d.DrawnFor = null;
            foreach (var p in Paints) p.DrawnFor = null;
        }

        /// <summary>The Enterprise-D decal's gold plate (beside the lettering, kept as it is): red-brown frame, gold inside.</summary>
        private static void GoldPlate(Color32[] px, int size)
        {
            var frame = new Color32(0xB2, 0x3C, 0x1C, 255); var gold = new Color32(0xF2, 0xC3, 0x10, 255);
            const float cx = 1279.5f, cy = 1927f, hx = 141.5f, hy = 97f;
            for (int y = 1820; y < 2036; y++)
                for (int x = 1128; x < 1432; x++)
                {
                    float outer = RoundBox(x + 0.5f - cx, y + 0.5f - cy, hx, hy, 16f), inner = RoundBox(x + 0.5f - cx, y + 0.5f - cy, hx - 30f, hy - 30f, 8f);
                    float ring = Mathf.Clamp01(0.5f - outer) * Mathf.Clamp01(outer + 14.5f), fill = Mathf.Clamp01(0.5f - inner);
                    float a = Mathf.Max(ring, fill);
                    if (a <= 0f) continue;
                    var c = fill >= ring ? gold : frame;
                    px[y * size + x] = new Color32(c.r, c.g, c.b, (byte)Mathf.RoundToInt(a * 255f));
                }
        }

        private static float RoundBox(float x, float y, float hx, float hy, float r)
        {
            float qx = Mathf.Abs(x) - hx + r, qy = Mathf.Abs(y) - hy + r;
            return new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
        }

        /// <summary>"USS Cosmo" → "U.S.S. COSMO", as the hull writes it.</summary>
        internal static string HullText(string name)
        {
            string n = name.Trim();
            foreach (var prefix in new[] { "U.S.S.", "USS" })
                if (n.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase)) return "U.S.S. " + n.Substring(prefix.Length + 1).Trim().ToUpperInvariant();
            return n.ToUpperInvariant();
        }

        // ---- the letters ----

        /// <summary>A letter: its box in font units (baseline y = 0, x from the pen) and where it is on the glyph texture.</summary>
        private struct Glyph
        {
            public float MinX, MaxX, MinY, MaxY, Advance;
            public Vector2 BL, BR, TL; // texture coordinates of its corners
            public bool Found;
        }

        /// <summary>A font read back into memory: the glyph texture's coverage (or distance) values and the letters.</summary>
        private sealed class Face
        {
            public string Name;
            public byte[] Values; public int W, H;
            public bool Sdf; public float Spread; // a TMP distance atlas: 0.5 the edge, Spread texels from 0 to 1
            public float TexelsPerUnit = 1f;
            public Func<char, Glyph> Get;
        }

        private static Face LoadFace(string text)
        {
            string wanted = (Plugin.HullFont.Value ?? "").Trim();
            if (wanted.Length > 0)
            {
                string installed = null;
                try { installed = Font.GetOSInstalledFontNames().FirstOrDefault(f => string.Equals(f, wanted, StringComparison.OrdinalIgnoreCase)); } catch { }
                if (installed != null)
                {
                    var face = OsFace(installed, text);
                    if (face != null) return face;
                }
                else Plugin.Log.LogInfo($"Hull name: the font \"{wanted}\" isn't installed in Windows; the game's United Sans instead");
            }
            return TmpFace();
        }

        private static Face OsFace(string fontName, string text)
        {
            const int px = 128;
            var font = Font.CreateDynamicFontFromOSFont(fontName, px);
            string chars = new string((text + "H").Distinct().ToArray());
            font.RequestCharactersInTexture(chars, px, FontStyle.Normal);
            var tex = font.material != null ? font.material.mainTexture : null;
            if (tex == null) return null;
            var read = ReadBack(tex, out int w, out int h);
            if (read == null) return null;
            var face = new Face { Name = fontName, Values = read, W = w, H = h };
            var glyphs = new Dictionary<char, Glyph>();
            foreach (char c in chars)
            {
                if (!font.GetCharacterInfo(c, out CharacterInfo ci, px, FontStyle.Normal)) continue;
                glyphs[c] = new Glyph { MinX = ci.minX, MaxX = ci.maxX, MinY = ci.minY, MaxY = ci.maxY, Advance = ci.advance, BL = ci.uvBottomLeft, BR = ci.uvBottomRight, TL = ci.uvTopLeft, Found = true };
            }
            face.Get = c => glyphs.TryGetValue(c, out var g) ? g : default(Glyph);
            return face;
        }

        private static Face TmpFace()
        {
            var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            var asset = fonts.FirstOrDefault(f => f.name == "UnitedSansSmCdBd SDF") ?? fonts.FirstOrDefault(f => f.name.StartsWith("UnitedSansSmCdBd SDF", StringComparison.Ordinal) && f.atlas != null);
            if (asset == null || asset.atlas == null) return null;
            var info = asset.fontInfo;
            var read = ReadBack(asset.atlas, out int w, out int h);
            if (read == null) return null;
            var face = new Face { Name = asset.name, Values = read, W = w, H = h, Sdf = true, Spread = info.Padding + 1f };
            var dict = asset.characterDictionary;
            float pad = info.Padding;
            face.Get = c =>
            {
                if (!dict.TryGetValue(c, out var g)) return default(Glyph);
                float x0 = (g.x - pad) / w, x1 = (g.x + g.width + pad) / w, yTop = 1f - (g.y - pad) / h, yBottom = 1f - (g.y + g.height + pad) / h;
                return new Glyph
                {
                    MinX = g.xOffset - pad, MaxX = g.xOffset + g.width + pad, MaxY = g.yOffset + pad, MinY = g.yOffset - g.height - pad, Advance = g.xAdvance,
                    BL = new Vector2(x0, yBottom), BR = new Vector2(x1, yBottom), TL = new Vector2(x0, yTop), Found = true,
                };
            };
            return face;
        }

        /// <summary>
        /// A glyph texture's coverage (or distance) values as bytes, whether or not it's readable: read directly if it
        /// is, else drawn onto a black target with the font shader (red = the glyph's alpha), else copied as it is.
        /// A read with no spread of values (all empty or all full) doesn't count.
        /// </summary>
        private static byte[] ReadBack(Texture tex, out int w, out int h)
        {
            w = tex.width; h = tex.height;
            byte[] values;
            var t2 = tex as Texture2D;
            if (t2 != null)
            {
                try { values = Pick(t2.GetPixels32(), "direct"); if (values != null) return values; } catch { }
            }
            var shader = Shader.Find("GUI/Text Shader");
            if (shader != null)
            {
                var mat = new Material(shader) { color = Color.white };
                values = Pick(Blit(tex, w, h, mat), "font shader");
                UnityEngine.Object.Destroy(mat);
                if (values != null) return values;
            }
            values = Pick(Blit(tex, w, h, null), "copy");
            if (values != null) return values;
            Plugin.Log.LogWarning($"Hull name: couldn't read the glyphs of {tex.name} ({tex.width}x{tex.height})");
            return null;
        }

        private static Color32[] Blit(Texture tex, int w, int h, Material mat)
        {
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var was = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, new Color(0f, 0f, 0f, 0f));
            if (mat != null) Graphics.Blit(tex, rt, mat); else Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            var copy = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            copy.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            copy.Apply(false);
            RenderTexture.active = was;
            RenderTexture.ReleaseTemporary(rt);
            var px = copy.GetPixels32();
            UnityEngine.Object.Destroy(copy);
            return px;
        }

        /// <summary>The channel that holds the glyphs (the one whose values spread from empty to full), or null.</summary>
        private static byte[] Pick(Color32[] px, string how)
        {
            if (px == null || px.Length == 0) return null;
            int best = -1, bestSpread = 0;
            var stats = new System.Text.StringBuilder();
            for (int c = 0; c < 4; c++)
            {
                int lo = 255, hi = 0, mid = 0;
                for (int i = 0; i < px.Length; i += 7)
                {
                    byte v = c == 0 ? px[i].r : c == 1 ? px[i].g : c == 2 ? px[i].b : px[i].a;
                    if (v < lo) lo = v; if (v > hi) hi = v; if (v > 32 && v < 224) mid++;
                }
                stats.Append($" {"rgba"[c]} {lo}-{hi} mid {mid}");
                if (lo < 64 && hi > 192 && mid > bestSpread) { best = c; bestSpread = mid; }
            }
            Plugin.Log.LogInfo($"Hull name: glyph read ({how}):{stats}{(best < 0 ? " - no glyphs" : " - using " + "rgba"[best])}");
            if (best < 0) return null;
            var values = new byte[px.Length];
            for (int i = 0; i < px.Length; i++) values[i] = best == 0 ? px[i].r : best == 1 ? px[i].g : best == 2 ? px[i].b : px[i].a;
            return values;
        }

        private static float Sample(Face f, Vector2 uv, Glyph g)
        {
            // Bilinear, kept inside the letter's own box on the texture (no bleeding from neighbours).
            var tr = g.BR + g.TL - g.BL;
            float uMin = Mathf.Min(g.BL.x, g.BR.x, g.TL.x, tr.x), uMax = Mathf.Max(g.BL.x, g.BR.x, g.TL.x, tr.x);
            float vMin = Mathf.Min(g.BL.y, g.BR.y, g.TL.y, tr.y), vMax = Mathf.Max(g.BL.y, g.BR.y, g.TL.y, tr.y);
            float x = Mathf.Clamp(uv.x, uMin, uMax) * f.W - 0.5f, y = Mathf.Clamp(uv.y, vMin, vMax) * f.H - 0.5f;
            int x0 = Mathf.Clamp(Mathf.FloorToInt(x), 0, f.W - 1), y0 = Mathf.Clamp(Mathf.FloorToInt(y), 0, f.H - 1);
            int x1 = Mathf.Min(x0 + 1, f.W - 1), y1 = Mathf.Min(y0 + 1, f.H - 1);
            float tx = Mathf.Clamp01(x - x0), ty = Mathf.Clamp01(y - y0);
            float a = Mathf.Lerp(f.Values[y0 * f.W + x0], f.Values[y0 * f.W + x1], tx), b = Mathf.Lerp(f.Values[y1 * f.W + x0], f.Values[y1 * f.W + x1], tx);
            return Mathf.Lerp(a, b, ty) / 255f;
        }

        // ---- drawing ----

        private sealed class Laid
        {
            public Line Line; public Glyph[] Glyphs; public float[] Pen;
            public float Width, CapUnits, Scale, Stretch; // font units; pixels per unit; horizontal stretch
        }

        /// <summary>The padding a TMP letter's box has around the letter itself.</summary>
        private static float Pad(Face f) => f.Sdf ? f.Spread - 1f : 0f;

        private static float CapUnits(Face f)
        {
            var h = f.Get('H');
            return h.Found ? Mathf.Max(1f, h.MaxY - Pad(f)) : 1f;
        }

        private static float NaturalWidth(Face f, string text, float cap, float tracking, out Glyph[] glyphs, out float[] pen)
        {
            glyphs = new Glyph[text.Length]; pen = new float[text.Length];
            float x = 0f, right = 0f;
            for (int i = 0; i < text.Length; i++)
            {
                var g = f.Get(text[i]);
                glyphs[i] = g; pen[i] = x;
                if (g.Found) right = Mathf.Max(right, x + g.MaxX - Pad(f));
                x += (g.Found ? g.Advance : cap * 0.5f) + tracking * cap;
            }
            return Mathf.Max(right, 1f);
        }

        private static Laid Lay(Face f, Line line, string text)
        {
            float cap = CapUnits(f);
            float s = line.Cap / cap;
            float refWidth = NaturalWidth(f, line.Reference, cap, line.Tracking, out _, out _);
            float stretch = Mathf.Clamp(line.ReferenceLength / (refWidth * s), 0.5f, 2.5f); // as wide as the game's letters
            float width = NaturalWidth(f, text, cap, line.Tracking, out var glyphs, out var pen);
            if (width * s * stretch > line.MaxLength)
            {
                stretch = Mathf.Max(stretch * 0.6f, line.MaxLength / (width * s));       // narrower letters first...
                if (width * s * stretch > line.MaxLength) s = line.MaxLength / (width * stretch); // ...then smaller ones
            }
            return new Laid { Line = line, Glyphs = glyphs, Pen = pen, Width = width, CapUnits = cap, Scale = s, Stretch = stretch };
        }

        private static float Wrap(float radians)
        {
            while (radians > Mathf.PI) radians -= 2f * Mathf.PI;
            while (radians <= -Mathf.PI) radians += 2f * Mathf.PI;
            return radians;
        }

        /// <summary>Texture pixel → the line's own coordinates (font units: along from the start, up from the baseline).</summary>
        private static Vector2 ToLine(Laid l, float x, float y)
        {
            var line = l.Line;
            float along, up, dx = x - line.Cx, dy = y - line.Cy;
            if (line.Shape == Shape.Straight)
            {
                float c = Mathf.Cos(line.Angle * Mathf.Deg2Rad), s = Mathf.Sin(line.Angle * Mathf.Deg2Rad);
                along = dx * c + dy * s; up = -dx * s + dy * c;
            }
            else
            {
                float r = Mathf.Sqrt(dx * dx + dy * dy), turn = Wrap(Mathf.Atan2(dy, dx) - line.Mid * Mathf.Deg2Rad);
                along = (line.Shape == Shape.Smile ? turn : -turn) * line.Radius;
                up = line.Shape == Shape.Smile ? line.Radius - r : r - line.Radius;
            }
            return new Vector2(along / (l.Scale * l.Stretch) + l.Width / 2f, up / l.Scale + l.CapUnits / 2f);
        }

        /// <summary>The line's coordinates → texture pixel.</summary>
        private static Vector2 FromLine(Laid l, float u, float w)
        {
            var line = l.Line;
            float along = (u - l.Width / 2f) * l.Scale * l.Stretch, up = (w - l.CapUnits / 2f) * l.Scale;
            if (line.Shape == Shape.Straight)
            {
                float c = Mathf.Cos(line.Angle * Mathf.Deg2Rad), s = Mathf.Sin(line.Angle * Mathf.Deg2Rad);
                return new Vector2(line.Cx + along * c - up * s, line.Cy + along * s + up * c);
            }
            float a = line.Mid * Mathf.Deg2Rad + (line.Shape == Shape.Smile ? along : -along) / line.Radius;
            float r = line.Shape == Shape.Smile ? line.Radius - up : line.Radius + up;
            return new Vector2(line.Cx + r * Mathf.Cos(a), line.Cy + r * Mathf.Sin(a));
        }

        /// <summary>The rectangle of the texture a line is drawn in, with its own coverage and distance buffers.</summary>
        private sealed class Area { public int X, Y, W, H; }

        private static void GlyphBox(Laid l, int i, out float xMin, out float xMax, out float yMin, out float yMax)
        {
            var g = l.Glyphs[i];
            xMin = yMin = float.MaxValue; xMax = yMax = float.MinValue;
            for (int a = 0; a <= 4; a++)
                for (int b = 0; b <= 4; b++)
                {
                    var p = FromLine(l, l.Pen[i] + Mathf.Lerp(g.MinX, g.MaxX, a / 4f), Mathf.Lerp(g.MinY, g.MaxY, b / 4f));
                    xMin = Mathf.Min(xMin, p.x); xMax = Mathf.Max(xMax, p.x); yMin = Mathf.Min(yMin, p.y); yMax = Mathf.Max(yMax, p.y);
                }
        }

        private static Area Bounds(Laid l, float margin, int size)
        {
            float xMin = float.MaxValue, xMax = float.MinValue, yMin = float.MaxValue, yMax = float.MinValue;
            for (int i = 0; i < l.Glyphs.Length; i++)
            {
                if (!l.Glyphs[i].Found) continue;
                GlyphBox(l, i, out var a, out var b, out var c, out var d);
                xMin = Mathf.Min(xMin, a); xMax = Mathf.Max(xMax, b); yMin = Mathf.Min(yMin, c); yMax = Mathf.Max(yMax, d);
            }
            if (xMin > xMax) return null;
            int x0 = Mathf.Clamp(Mathf.FloorToInt(xMin - margin), 0, size - 1), x1 = Mathf.Clamp(Mathf.CeilToInt(xMax + margin), 0, size - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(yMin - margin), 0, size - 1), y1 = Mathf.Clamp(Mathf.CeilToInt(yMax + margin), 0, size - 1);
            return new Area { X = x0, Y = y0, W = x1 - x0 + 1, H = y1 - y0 + 1 };
        }

        private static void Raster(Face f, Laid l, Area area, float[] cover)
        {
            float pixelsPerTexel = l.Scale * Mathf.Sqrt(l.Stretch) / f.TexelsPerUnit;
            for (int i = 0; i < l.Glyphs.Length; i++)
            {
                var g = l.Glyphs[i];
                if (!g.Found || g.MaxX <= g.MinX || g.MaxY <= g.MinY) continue;
                float px0 = l.Pen[i];
                GlyphBox(l, i, out var xMin, out var xMax, out var yMin, out var yMax); // arcs bend the box's edges
                int x0 = Mathf.Max(area.X, Mathf.FloorToInt(xMin) - 2), x1 = Mathf.Min(area.X + area.W - 1, Mathf.CeilToInt(xMax) + 2);
                int y0 = Mathf.Max(area.Y, Mathf.FloorToInt(yMin) - 2), y1 = Mathf.Min(area.Y + area.H - 1, Mathf.CeilToInt(yMax) + 2);
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        var q = ToLine(l, x + 0.5f, y + 0.5f);
                        float gx = q.x - px0, gy = q.y;
                        if (gx < g.MinX || gx > g.MaxX || gy < g.MinY || gy > g.MaxY) continue;
                        float fx = (gx - g.MinX) / (g.MaxX - g.MinX), fy = (gy - g.MinY) / (g.MaxY - g.MinY);
                        var uv = g.BL + (g.BR - g.BL) * fx + (g.TL - g.BL) * fy;
                        float v = Sample(f, uv, g);
                        float c = f.Sdf ? Mathf.Clamp01((v - 0.5f) * f.Spread * pixelsPerTexel + 0.5f) : v;
                        int k = (y - area.Y) * area.W + (x - area.X);
                        if (c > cover[k]) cover[k] = c;
                    }
            }
        }

        /// <summary>Distance from each pixel to the nearest filled one (cover ≥ 0.5), by two passes over offset vectors.</summary>
        private static float[] Distance(float[] cover, int w, int h)
        {
            const int Far = 99999;
            var ox = new int[w * h]; var oy = new int[w * h];
            for (int i = 0; i < cover.Length; i++) { bool inside = cover[i] >= 0.5f; ox[i] = inside ? 0 : Far; oy[i] = inside ? 0 : Far; }
            void Try(int i, int x, int y, int dx, int dy)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) return;
                int j = ny * w + nx; if (ox[j] == Far) return;
                int cx = ox[j] + dx, cy = oy[j] + dy;
                if (ox[i] == Far || cx * cx + cy * cy < ox[i] * ox[i] + oy[i] * oy[i]) { ox[i] = cx; oy[i] = cy; }
            }
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++) { int i = y * w + x; Try(i, x, y, -1, 0); Try(i, x, y, 0, -1); Try(i, x, y, -1, -1); Try(i, x, y, 1, -1); }
                for (int x = w - 1; x >= 0; x--) Try(y * w + x, x, y, 1, 0);
            }
            for (int y = h - 1; y >= 0; y--)
            {
                for (int x = w - 1; x >= 0; x--) { int i = y * w + x; Try(i, x, y, 1, 0); Try(i, x, y, 0, 1); Try(i, x, y, 1, 1); Try(i, x, y, -1, 1); }
                for (int x = 0; x < w; x++) Try(y * w + x, x, y, -1, 0);
            }
            var d = new float[w * h];
            for (int i = 0; i < d.Length; i++) d[i] = ox[i] == Far ? Far : Mathf.Sqrt(ox[i] * ox[i] + oy[i] * oy[i]);
            return d;
        }

        /// <summary>How much of a rim from start to end (pixels out from the letter) a pixel at distance d is in.</summary>
        private static float Band(float d, float start, float end) =>
            end <= start ? 0f : (start <= 0f ? 1f : Mathf.Clamp01(d - start + 0.5f)) * Mathf.Clamp01(end + 0.5f - d);

        private static string TextFor(Line line, string name, string registry, Decal decal)
        {
            if (line.Says == Says.Number) return NumberOf(registry.Length > 0 ? registry : decal.DefaultRegistry).ToUpperInvariant();
            if (line.Says == Says.Registry) return (registry.Length > 0 ? registry : decal.DefaultRegistry).ToUpperInvariant();
            string hull = HullText(name);
            return line.Says == Says.BareName && hull.StartsWith("U.S.S. ", StringComparison.Ordinal) ? hull.Substring(7) : hull;
        }

        private static bool Draw(Decal decal, string name, string registry)
        {
            var texts = decal.Lines.Select(line => TextFor(line, name, registry, decal)).ToArray();
            var face = LoadFace(string.Concat(texts));
            if (face == null) { Plugin.Log.LogInfo("Hull name: no font loaded yet"); return false; }
            var started = Time.realtimeSinceStartup;
            int size = decal.Size;

            var px = new Color32[size * size];
            for (int i = 0; i < px.Length; i++) px[i] = decal.Clear;
            var glow = decal.Emission ? new Color32[size * size] : null;
            if (glow != null) for (int i = 0; i < glow.Length; i++) glow[i] = new Color32(255, 255, 255, 255);
            decal.Extra?.Invoke(px, size);

            // Each line on its own: the rims' widths follow its letters' size.
            for (int n = 0; n < decal.Lines.Length; n++)
            {
                var line = decal.Lines[n];
                var l = Lay(face, line, texts[n]);
                float capPx = l.CapUnits * l.Scale;
                var st = line.Style;
                float gap = st.Gap * capPx;
                float rim = st.RimWidth > 0f ? Mathf.Max(1.2f, st.RimWidth * capPx) : 0f, outer = st.OuterWidth > 0f ? Mathf.Max(1.4f, st.OuterWidth * capPx) : 0f;
                var area = Bounds(l, gap + rim + outer + 4f, size);
                if (area == null) continue;
                var cover = new float[area.W * area.H];
                Raster(face, l, area, cover);
                var dist = Distance(cover, area.W, area.H);
                for (int ly = 0; ly < area.H; ly++)
                    for (int lx = 0; lx < area.W; lx++)
                    {
                        int li = ly * area.W + lx, i = (area.Y + ly) * size + area.X + lx;
                        float c = cover[li], d = dist[li];
                        float rimA = Band(d, gap, gap + rim), outA = Band(d, gap + rim, gap + rim + outer);
                        float bandA = Mathf.Min(1f, rimA + outA);
                        float alpha = Mathf.Max(c, bandA);
                        if (alpha <= 0f) continue;
                        Color32 col = st.Fill;
                        if (bandA > 0f) col = Color32.Lerp(Color32.Lerp(st.Rim, st.Outer, outA / (rimA + outA)), st.Fill, c);
                        byte a = (byte)Mathf.RoundToInt(alpha * 255f);
                        if (a >= px[i].a) px[i] = new Color32(col.r, col.g, col.b, a);
                        if (glow != null)
                        {
                            byte e = (byte)Mathf.RoundToInt((1f - c) * 255f);
                            if (e < glow[i].r) glow[i] = new Color32(e, e, e, 255);
                        }
                    }
            }
            if (decal.Diffuse == null)
            {
                decal.Diffuse = new Texture2D(size, size, TextureFormat.RGBA32, true) { name = decal.Texture + " (crew's name)", wrapMode = TextureWrapMode.Clamp, anisoLevel = 4 };
                UnityEngine.Object.DontDestroyOnLoad(decal.Diffuse);
            }
            decal.Diffuse.SetPixels32(px); decal.Diffuse.Apply(true);
            if (Plugin.HullPreview.Value)
                try { System.IO.File.WriteAllBytes(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "hull-name-" + decal.Ship + ".png"), decal.Diffuse.EncodeToPNG()); } catch { }
            if (glow != null)
            {
                if (decal.Glow == null)
                {
                    decal.Glow = new Texture2D(size, size, TextureFormat.RGB24, true) { name = decal.Texture + " glow (crew's name)", wrapMode = TextureWrapMode.Clamp };
                    UnityEngine.Object.DontDestroyOnLoad(decal.Glow);
                }
                decal.Glow.SetPixels32(glow); decal.Glow.Apply(true);
            }
            Plugin.Log.LogInfo($"Hull name: {decal.Ship}: \"{string.Join("\" / \"", texts.Distinct().ToArray())}\" in {face.Name}, drawn in {(Time.realtimeSinceStartup - started) * 1000f:0} ms");
            return true;
        }
    }
}
