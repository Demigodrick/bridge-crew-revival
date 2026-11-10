using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// The ship's name on the hull: the Aegis wears its name and registry as a decal (material Aegis_Name_Decal,
    /// textures Ship_Decals_D and the emission map Ship_Decals_E). One straight "U.S.S. AEGIS" line and "NX-1787" bent
    /// twice along the saucer's rim, upper and lower side. With a ship name set (<see cref="ShipName"/>), both
    /// textures are drawn again at the same places, in the decal's own colours: dark fill, red then light grey rim.
    /// The letters come from [Rooms] HullFont if that font is installed in Windows (e.g. Starfleet Bold Extended,
    /// which isn't shipped with the mod), else from the game's own United Sans.
    /// </summary>
    internal static class HullName
    {
        private const int Size = 1024;
        private static readonly Color32 Fill = new Color32(0x22, 0x11, 0x22, 255), Red = new Color32(0x77, 0x11, 0x11, 255), Grey = new Color32(0xCC, 0xCC, 0xCC, 255);

        private enum Shape { Straight, Smile, Frown }

        /// <summary>Where a line of the decal is, in the texture's pixels (y up), and the original text it replaces.</summary>
        private sealed class Line
        {
            public Shape Shape;
            public float Cx, Cy, Radius, Mid; // the straight line's centre, or the arc's circle and middle angle (radians)
            public float Cap;                 // capital height in pixels
            public string Reference;          // the game's text: our letters are stretched to fill its length...
            public float ReferenceLength, MaxLength; // ...and squeezed when longer than MaxLength
            public float Tracking;            // extra space between letters, in capital heights
        }

        // Measured on the game's Ship_Decals_D: letter blobs, circles fitted through their centres.
        private static readonly Line NameLine = new Line { Shape = Shape.Straight, Cx = 507f, Cy = 897f, Cap = 50f, Reference = "U.S.S. AEGIS", ReferenceLength = 487f, MaxLength = 960f, Tracking = 0.06f };
        private static readonly Line UpperArc = new Line { Shape = Shape.Smile, Cx = 495f, Cy = 1303f, Radius = 651f, Mid = -88.4f * Mathf.Deg2Rad, Cap = 132f, Reference = "NX-1787", ReferenceLength = 1000f, MaxLength = 1010f, Tracking = 0.12f };
        private static readonly Line LowerArc = new Line { Shape = Shape.Frown, Cx = 524f, Cy = -511f, Radius = 819f, Mid = 89.85f * Mathf.Deg2Rad, Cap = 124f, Reference = "NX-1787", ReferenceLength = 968f, MaxLength = 975f, Tracking = 0.12f };

        private static string drawnFor;          // the "name|registry|font" the textures show, or null
        private static Texture2D diffuse, emission;
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
                string want = name + "|" + registry + "|" + Plugin.HullFont.Value;
                if (drawnFor != want) { if (!Draw(name, registry)) { nextScan = Time.unscaledTime + 30f; return; } drawnFor = want; }
                var mats = Resources.FindObjectsOfTypeAll<Material>().Where(IsDecal).ToList(); // none: no Aegis loaded yet
                foreach (var m in mats)
                {
                    if (m.GetTexture("_MainTex") == diffuse) continue;
                    if (!originals.ContainsKey(m)) originals[m] = new[] { m.GetTexture("_MainTex"), m.GetTexture("_EmissionMap") };
                    m.SetTexture("_MainTex", diffuse);
                    m.SetTexture("_EmissionMap", emission);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Hull name: " + e); nextScan = Time.unscaledTime + 30f; }
        }

        private static bool IsDecal(Material m)
        {
            if (m == null || !m.HasProperty("_MainTex")) return false;
            if (m.name.StartsWith("Aegis_Name_Decal", StringComparison.Ordinal)) return true;
            var t = m.GetTexture("_MainTex");
            return t != null && (t.name == "Ship_Decals_D" || (diffuse != null && t == diffuse));
        }

        private static void Restore()
        {
            foreach (var kv in originals)
                if (kv.Key != null) { kv.Key.SetTexture("_MainTex", kv.Value[0]); kv.Key.SetTexture("_EmissionMap", kv.Value[1]); }
            originals.Clear();
            drawnFor = null;
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

        /// <summary>Texture pixel → the line's own coordinates (font units: along from the start, up from the baseline).</summary>
        private static Vector2 ToLine(Laid l, float x, float y)
        {
            var line = l.Line;
            if (line.Shape == Shape.Straight)
                return new Vector2((x - line.Cx) / (l.Scale * l.Stretch) + l.Width / 2f, (y - line.Cy) / l.Scale + l.CapUnits / 2f);
            float dx = x - line.Cx, dy = y - line.Cy, r = Mathf.Sqrt(dx * dx + dy * dy), a = Mathf.Atan2(dy, dx);
            float along = (line.Shape == Shape.Smile ? a - line.Mid : line.Mid - a) * line.Radius;
            float up = line.Shape == Shape.Smile ? line.Radius - r : r - line.Radius;
            return new Vector2(along / (l.Scale * l.Stretch) + l.Width / 2f, up / l.Scale + l.CapUnits / 2f);
        }

        /// <summary>The line's coordinates → texture pixel.</summary>
        private static Vector2 FromLine(Laid l, float u, float w)
        {
            var line = l.Line;
            float along = (u - l.Width / 2f) * l.Scale * l.Stretch, up = (w - l.CapUnits / 2f) * l.Scale;
            if (line.Shape == Shape.Straight) return new Vector2(line.Cx + along, line.Cy + up);
            float a = line.Shape == Shape.Smile ? line.Mid + along / line.Radius : line.Mid - along / line.Radius;
            float r = line.Shape == Shape.Smile ? line.Radius - up : line.Radius + up;
            return new Vector2(line.Cx + r * Mathf.Cos(a), line.Cy + r * Mathf.Sin(a));
        }

        private static void Raster(Face f, Laid l, float[] cover)
        {
            for (int i = 0; i < l.Glyphs.Length; i++)
            {
                var g = l.Glyphs[i];
                if (!g.Found || g.MaxX <= g.MinX || g.MaxY <= g.MinY) continue;
                float px0 = l.Pen[i];
                // The letter's box on the texture, from a grid of its points (arcs bend the edges).
                float xMin = float.MaxValue, xMax = float.MinValue, yMin = float.MaxValue, yMax = float.MinValue;
                for (int a = 0; a <= 4; a++)
                    for (int b = 0; b <= 4; b++)
                    {
                        var p = FromLine(l, px0 + Mathf.Lerp(g.MinX, g.MaxX, a / 4f), Mathf.Lerp(g.MinY, g.MaxY, b / 4f));
                        xMin = Mathf.Min(xMin, p.x); xMax = Mathf.Max(xMax, p.x); yMin = Mathf.Min(yMin, p.y); yMax = Mathf.Max(yMax, p.y);
                    }
                int x0 = Mathf.Max(0, Mathf.FloorToInt(xMin) - 2), x1 = Mathf.Min(Size - 1, Mathf.CeilToInt(xMax) + 2);
                int y0 = Mathf.Max(0, Mathf.FloorToInt(yMin) - 2), y1 = Mathf.Min(Size - 1, Mathf.CeilToInt(yMax) + 2);
                float pixelsPerTexel = l.Scale * Mathf.Sqrt(l.Stretch) / f.TexelsPerUnit;
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
                        int k = y * Size + x;
                        if (c > cover[k]) cover[k] = c;
                    }
            }
        }

        /// <summary>Distance from each pixel to the nearest filled one (cover ≥ 0.5), by two passes over offset vectors.</summary>
        private static float[] Distance(float[] cover)
        {
            const int Far = 9999;
            var ox = new int[Size * Size]; var oy = new int[Size * Size];
            for (int i = 0; i < cover.Length; i++) { bool inside = cover[i] >= 0.5f; ox[i] = inside ? 0 : Far; oy[i] = inside ? 0 : Far; }
            void Try(int i, int x, int y, int dx, int dy)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= Size || ny >= Size) return;
                int j = ny * Size + nx; if (ox[j] == Far) return;
                int cx = ox[j] + dx, cy = oy[j] + dy;
                if (ox[i] == Far || cx * cx + cy * cy < ox[i] * ox[i] + oy[i] * oy[i]) { ox[i] = cx; oy[i] = cy; }
            }
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++) { int i = y * Size + x; Try(i, x, y, -1, 0); Try(i, x, y, 0, -1); Try(i, x, y, -1, -1); Try(i, x, y, 1, -1); }
                for (int x = Size - 1; x >= 0; x--) Try(y * Size + x, x, y, 1, 0);
            }
            for (int y = Size - 1; y >= 0; y--)
            {
                for (int x = Size - 1; x >= 0; x--) { int i = y * Size + x; Try(i, x, y, 1, 0); Try(i, x, y, 0, 1); Try(i, x, y, 1, 1); Try(i, x, y, -1, 1); }
                for (int x = 0; x < Size; x++) Try(y * Size + x, x, y, -1, 0);
            }
            var d = new float[Size * Size];
            for (int i = 0; i < d.Length; i++) d[i] = ox[i] == Far ? Far : Mathf.Sqrt(ox[i] * ox[i] + oy[i] * oy[i]);
            return d;
        }

        private static bool Draw(string name, string registry)
        {
            string hull = HullText(name), reg = (registry.Length > 0 ? registry : "NX-1787").ToUpperInvariant();
            var face = LoadFace(hull + reg);
            if (face == null) { Plugin.Log.LogInfo("Hull name: no font loaded yet"); return false; }
            var started = Time.realtimeSinceStartup;

            var lines = new[] { Lay(face, NameLine, hull), Lay(face, UpperArc, reg), Lay(face, LowerArc, reg) };
            var px = new Color32[Size * Size];
            var glow = new Color32[Size * Size];
            for (int i = 0; i < glow.Length; i++) glow[i] = new Color32(255, 255, 255, 255);
            // Each line on its own layer: the rim widths follow the letters' size.
            foreach (var l in lines)
            {
                var cover = new float[Size * Size];
                Raster(face, l, cover);
                var dist = Distance(cover);
                float capPx = l.CapUnits * l.Scale;
                float red = Mathf.Max(1.2f, capPx * 0.022f);
                float grey = Mathf.Max(1.4f, capPx * 0.03f);
                for (int i = 0; i < px.Length; i++)
                {
                    float c = cover[i], d = dist[i];
                    float rim = Mathf.Clamp01(red + grey + 0.5f - d);
                    float alpha = Mathf.Max(c, rim);
                    if (alpha <= 0f) continue;
                    var edge = Color32.Lerp(Red, Grey, Mathf.Clamp01(d - red + 0.5f));
                    var col = Color32.Lerp(edge, Fill, c);
                    byte a = (byte)Mathf.RoundToInt(alpha * 255f);
                    if (a > px[i].a) px[i] = new Color32(col.r, col.g, col.b, a);
                    byte e = (byte)Mathf.RoundToInt((1f - c) * 255f);
                    if (e < glow[i].r) glow[i] = new Color32(e, e, e, 255);
                }
            }
            if (diffuse == null) { diffuse = new Texture2D(Size, Size, TextureFormat.RGBA32, true) { name = "Ship_Decals_D (crew's name)", wrapMode = TextureWrapMode.Clamp, anisoLevel = 4 }; UnityEngine.Object.DontDestroyOnLoad(diffuse); }
            if (emission == null) { emission = new Texture2D(Size, Size, TextureFormat.RGB24, true) { name = "Ship_Decals_E (crew's name)", wrapMode = TextureWrapMode.Clamp }; UnityEngine.Object.DontDestroyOnLoad(emission); }
            diffuse.SetPixels32(px); diffuse.Apply(true);
            try { System.IO.File.WriteAllBytes(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "hull-name-preview.png"), diffuse.EncodeToPNG()); } catch { }
            emission.SetPixels32(glow); emission.Apply(true);
            Plugin.Log.LogInfo($"Hull name: \"{hull}\" / \"{reg}\" in {face.Name}, drawn in {(Time.realtimeSinceStartup - started) * 1000f:0} ms");
            return true;
        }
    }
}
