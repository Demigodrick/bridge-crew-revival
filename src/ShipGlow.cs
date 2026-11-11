using System.Collections.Generic;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// The ships' Bussard collectors (the red at the nacelles' fronts) alive: their faces are taken out of the hull pieces
    /// (the Nova's are the ones mapped onto the red plasma square of its texture; other classes' are pieces of their own)
    /// and given a material of their own, a glowing red plasma that swirls and pulses. Their navigation lights likewise:
    /// red to port, green to starboard, flashing together. The plasma is a loop of frames made once (two layers of noise flowing
    /// against each other), stepped through while the game runs: nothing is worked out per frame but which one shows.
    /// </summary>
    internal static class ShipGlow
    {
        // The red plasma square in the model's colour texture (the .obj's texture coordinates, v up).
        private const float U0 = 0.6035f, U1 = 0.6885f, V0 = 0.915f, V1 = 1.0f, Margin = 0.004f;
        private const int Size = 128, Frames = 96;
        private const float FramesPerSecond = 16f;

        private static Texture2D[] frames;
        private static Material material;
        private static int shown = -1;

        /// <summary>
        /// The faces of a hull piece mapped onto the plasma square, taken out of it (the piece keeps the rest) into a mesh of
        /// their own, mapped onto the whole plasma texture; null when the piece has none.
        /// </summary>
        internal static Mesh TakePlasma(Mesh piece, float minSide) =>
            Take(piece, (c, at) => Mathf.Abs(at.x) > minSide && c.x > U0 - Margin && c.x < U1 + Margin && c.y > V0 - Margin && c.y < V1 + Margin,
                 (uv, at) => new Vector2((uv.x - U0) / (U1 - U0), (uv.y - V0) / (V1 - V0)), " plasma");

        /// <summary>A whole piece (a class's own Bussard collector piece) as plasma: mapped from the side, so the plasma flows over it without a seam.</summary>
        internal static Mesh TakeAllAsPlasma(Mesh piece)
        {
            var b = piece.bounds;
            float span = Mathf.Max(1e-3f, Mathf.Max(b.size.y, b.size.z));
            float side = 0f; foreach (var p in piece.vertices) side += Mathf.Abs(p.x); side /= Mathf.Max(1, piece.vertexCount);
            return Take(piece, (c, at) => true, (uv, at) => new Vector2((at.y - b.min.y + Mathf.Abs(at.x) - side) / span, (at.z - b.min.z) / span), " plasma");
        }

        /// <summary>A whole piece (a class's own lamp piece) taken out as it is.</summary>
        internal static Mesh TakeAll(Mesh piece) => Take(piece, (c, at) => true, (uv, at) => uv, " light");

        /// <summary>
        /// The faces of a hull piece chosen by where they're mapped on the texture and where they are (the centre of each),
        /// taken out of it into a mesh of their own, their mapping changed by <paramref name="remap"/>; null when none.
        /// </summary>
        private static Mesh Take(Mesh piece, System.Func<Vector2, Vector3, bool> pick, System.Func<Vector2, Vector3, Vector2> remap, string suffix)
        {
            var pv = piece.vertices;
            var uv = piece.uv;
            if (uv == null || uv.Length != pv.Length) uv = new Vector2[pv.Length];
            var tris = piece.triangles;
            var keep = new List<int>(tris.Length);
            var take = new List<int>();
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                var c = (uv[tris[i]] + uv[tris[i + 1]] + uv[tris[i + 2]]) / 3f;
                var at3 = (pv[tris[i]] + pv[tris[i + 1]] + pv[tris[i + 2]]) / 3f;
                var to = pick(c, at3) ? take : keep;
                to.Add(tris[i]); to.Add(tris[i + 1]); to.Add(tris[i + 2]);
            }
            if (take.Count == 0) return null;

            var verts = piece.vertices; var normals = piece.normals;
            var map = new Dictionary<int, int>();
            var v = new List<Vector3>(); var n = new List<Vector3>(); var u = new List<Vector2>(); var t = new List<int>(take.Count);
            foreach (int old in take)
            {
                if (!map.TryGetValue(old, out int at))
                {
                    at = map[old] = v.Count;
                    v.Add(verts[old]);
                    if (normals != null && normals.Length == verts.Length) n.Add(normals[old]);
                    u.Add(remap(uv[old], verts[old]));
                }
                t.Add(at);
            }
            piece.triangles = keep.ToArray();
            piece.RecalculateBounds();
            var mesh = new Mesh { name = piece.name + suffix, vertices = v.ToArray(), uv = u.ToArray(), triangles = t.ToArray() };
            if (n.Count == v.Count) mesh.normals = n.ToArray(); else mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // ---- the navigation lights ----

        // The light bulbs' colour squares in the model's colour texture (v up): red (port), and the yellow one the starboard bulbs have, made green.
        private const float LightU0 = 0.248f, LightU1 = 0.270f, RedV0 = 0.960f, RedV1 = 0.985f;
        private static readonly Color Red = new Color(1f, 0.06f, 0.04f), Green = new Color(0.1f, 1f, 0.25f);
        private static Material redLight, greenLight;
        private const float BlinkPeriod = 2.2f;

        /// <summary>The port (red) or starboard (green) navigation light bulbs in a hull piece, taken out of it; null when none.</summary>
        internal static Mesh TakeLight(Mesh piece, bool port) =>
            Take(piece, (c, at) => c.x > LightU0 && c.x < LightU1 && (port ? c.y > RedV0 && c.y < RedV1 : c.y >= RedV1),
                 (uv, at) => uv, port ? " port light" : " starboard light");

        /// <summary>The red or green light's material (one each for every ship): the hull's shader, plain colour, glowing.</summary>
        internal static Material LightMaterial(Material like, bool port)
        {
            var m = port ? redLight : greenLight;
            if (m != null) return m;
            m = like != null ? new Material(like) : new Material(Shader.Find("Standard"));
            m.name = port ? "Ship port light" : "Ship starboard light";
            foreach (var keyword in new[] { "_NORMALMAP", "_METALLICGLOSSMAP", "_SPECGLOSSMAP", "_DETAIL_MULX2", "_PARALLAXMAP" }) m.DisableKeyword(keyword);
            foreach (var prop in new[] { "_MainTex", "_BumpMap", "_MetallicGlossMap", "_SpecGlossMap", "_DetailAlbedoMap", "_DetailNormalMap", "_OcclusionMap", "_ParallaxMap", "_EmissionMap" })
                if (m.HasProperty(prop)) m.SetTexture(prop, null);
            var colour = port ? Red : Green;
            if (m.HasProperty("_Color")) m.SetColor("_Color", colour * 0.35f);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.8f);
            m.SetColor("_EmissionColor", colour);
            m.EnableKeyword("_EMISSION");
            Object.DontDestroyOnLoad(m);
            if (port) redLight = m; else greenLight = m;
            return m;
        }

        /// <summary>
        /// Both lights' brightness now, the same for both: a soft flash once a period (up quickly, down slowly), barely glowing between.
        /// </summary>
        private static float Blink(float time)
        {
            float phase = time / BlinkPeriod % 1f;
            float flash = phase < 0.08f ? Mathf.SmoothStep(0f, 1f, phase / 0.08f) : phase < 0.4f ? Mathf.SmoothStep(1f, 0f, (phase - 0.08f) / 0.32f) : 0f;
            return 0.12f + 3.2f * flash;
        }

        /// <summary>The plasma material (one for every ship): the hull's shader, the first frame, glowing.</summary>
        internal static Material Material(Material like)
        {
            if (material != null) return material;
            MakeFrames();
            material = like != null ? new Material(like) : new Material(Shader.Find("Standard"));
            material.name = "Ship Bussard plasma";
            foreach (var keyword in new[] { "_NORMALMAP", "_METALLICGLOSSMAP", "_SPECGLOSSMAP", "_DETAIL_MULX2", "_PARALLAXMAP" }) material.DisableKeyword(keyword);
            foreach (var prop in new[] { "_BumpMap", "_MetallicGlossMap", "_SpecGlossMap", "_DetailAlbedoMap", "_DetailNormalMap", "_OcclusionMap", "_ParallaxMap" })
                if (material.HasProperty(prop)) material.SetTexture(prop, null);
            if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", 0.75f);
            material.SetTexture("_MainTex", frames[0]);
            material.SetTexture("_EmissionMap", frames[0]);
            material.SetColor("_EmissionColor", Color.white * 1.5f);
            material.EnableKeyword("_EMISSION");
            Object.DontDestroyOnLoad(material);
            return material;
        }

        /// <summary>Every frame: the plasma's next picture, and its glow breathing.</summary>
        internal static void Animate()
        {
            float time = Time.time;
            if (redLight != null || greenLight != null)
            {
                float blink = Blink(time);
                if (redLight != null) redLight.SetColor("_EmissionColor", Red * blink);
                if (greenLight != null) greenLight.SetColor("_EmissionColor", Green * blink);
            }
            if (material == null) return;
            int frame = (int)(time * FramesPerSecond) % Frames;
            if (frame < 0) frame += Frames;
            if (frame != shown)
            {
                shown = frame;
                material.SetTexture("_MainTex", frames[frame]);
                material.SetTexture("_EmissionMap", frames[frame]);
            }
            float pulse = 1.45f + 0.3f * Mathf.Sin(time * Mathf.PI * 2f * 0.28f) + 0.1f * Mathf.Sin(time * Mathf.PI * 2f * 1.0f);
            material.SetColor("_EmissionColor", Color.white * pulse);
        }

        // ---- the plasma frames ----

        private static void MakeFrames()
        {
            if (frames != null) return;
            var rng = new System.Random(1701);
            var a = Noise(rng, 8, 4); // large, slow swirls
            var b = Noise(rng, 16, 3); // finer turbulence
            frames = new Texture2D[Frames];
            var px = new Color32[Size * Size];
            for (int f = 0; f < Frames; f++)
            {
                float t = f / (float)Frames;
                // Each layer moves a whole tile per loop (so the loop closes): one up, one across; the bands turn through a whole cycle too.
                int ay = Mathf.RoundToInt(t * Size), bx = Mathf.RoundToInt(t * Size * 2) % Size;
                for (int y = 0; y < Size; y++)
                    for (int x = 0; x < Size; x++)
                    {
                        float na = a[((y + ay) % Size) * Size + x];
                        float nb = b[y * Size + (x + Size - bx) % Size];
                        float s = 0.5f + 0.5f * Mathf.Sin(Mathf.PI * 2f * (2.2f * na + 1.3f * nb - t));
                        float v = Mathf.Clamp01(0.55f * s + 0.45f * na * nb * 2.2f);
                        // dark ember, deep red, bright red, an orange-white heart where it's hottest
                        float r = Mathf.Lerp(0.22f, 1f, Mathf.Pow(v, 0.8f));
                        float g = 0.04f + 0.55f * Mathf.Pow(v, 4f);
                        float bl = 0.02f + 0.35f * Mathf.Pow(v, 7f);
                        px[y * Size + x] = new Color(r, g, bl);
                    }
                var tex = new Texture2D(Size, Size, TextureFormat.RGB24, true) { name = "Ship plasma " + f, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, anisoLevel = 2 };
                tex.SetPixels32(px);
                tex.Apply(true, true);
                Object.DontDestroyOnLoad(tex);
                frames[f] = tex;
            }
        }

        /// <summary>Tileable fractal value noise (0..1), Size×Size: a grid of cells random values, smoothed, octaves added.</summary>
        private static float[] Noise(System.Random rng, int cells, int octaves)
        {
            var result = new float[Size * Size];
            float amplitude = 1f, total = 0f;
            for (int o = 0; o < octaves; o++, cells *= 2, amplitude *= 0.5f)
            {
                var grid = new float[cells * cells];
                for (int i = 0; i < grid.Length; i++) grid[i] = (float)rng.NextDouble();
                float cell = Size / (float)cells;
                for (int y = 0; y < Size; y++)
                {
                    float fy = y / cell; int y0 = (int)fy; float ty = Smooth(fy - y0); int y1 = (y0 + 1) % cells; y0 %= cells;
                    for (int x = 0; x < Size; x++)
                    {
                        float fx = x / cell; int x0 = (int)fx; float tx = Smooth(fx - x0); int x1 = (x0 + 1) % cells; x0 %= cells;
                        float top = Mathf.Lerp(grid[y0 * cells + x0], grid[y0 * cells + x1], tx);
                        float bottom = Mathf.Lerp(grid[y1 * cells + x0], grid[y1 * cells + x1], tx);
                        result[y * Size + x] += amplitude * Mathf.Lerp(top, bottom, ty);
                    }
                }
                total += amplitude;
            }
            for (int i = 0; i < result.Length; i++) result[i] /= total;
            return result;
        }

        private static float Smooth(float t) => t * t * (3f - 2f * t);
    }
}
