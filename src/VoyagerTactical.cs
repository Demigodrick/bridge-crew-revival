using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// Test (bridge set): the tactical station's own face for the set's console. The game's tactical panels keep running,
    /// unseen; this panel is drawn in the set's style (LCARS bars, its labels, the game panel's values read off its text
    /// fields) on the console's screen, and the game's own hand targets (its buttons' touch zones) are moved onto the
    /// buttons drawn here, so a touch or a click works the game's controls exactly as before. Prototype: scan and phasers.
    /// </summary>
    internal static class VoyagerTactical
    {
        private const int W = 1024, H = 512;
        private static readonly Color32 Black = new Color32(0, 0, 0, 255), Orange = new Color32(0xFF, 0x99, 0x33, 255), Peach = new Color32(0xFF, 0xCC, 0x99, 255),
            Lilac = new Color32(0xCC, 0x99, 0xCC, 255), Red = new Color32(0xDD, 0x44, 0x44, 255), Blue = new Color32(0x99, 0x99, 0xFF, 255), Text = new Color32(0xFF, 0xCC, 0x99, 255);

        private struct RectInt { public int x, y, width, height; public RectInt(int x, int y, int w, int h) { this.x = x; this.y = y; width = w; height = h; } } // (this Unity has none)
        private sealed class Button { public string Label, Target; public RectInt Box; public Color32 Colour; }
        private sealed class Readout { public string Label, Source; public int X, Y; public Component Text; public string Shown; }

        private static readonly Button[] Buttons =
        {
            new Button { Label = "SCAN", Target = "Hnd_Scan", Box = new RectInt(110, 40, 260, 110), Colour = Orange },
            new Button { Label = "PHASER", Target = "Hnd_FirePhasers", Box = new RectInt(400, 40, 300, 110), Colour = Red },
        };
        private static readonly Readout[] Readouts =
        {
            new Readout { Label = "ZIEL", Source = "Text_TargetName", X = 110, Y = 330 },
            new Readout { Label = "SCHILDE", Source = "Text_ShieldsValue", X = 110, Y = 260 },
            new Readout { Label = "HUELLE", Source = "Text_HullValue", X = 560, Y = 260 },
        };

        private static Texture2D texture;
        private static GameObject face;
        private static float next;
        private static MethodInfo textGetter;

        /// <summary>The panel built onto the console screen (centre, facing, width in metres); the game's own faces hidden.</summary>
        internal static void Build(Transform ui, Vector3 centre, Vector3 n, float width)
        {
            var iface = ui.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Interface-Tactical");
            if (iface == null) { Plugin.Log.LogWarning("Voyager tactical: the game's tactical interface wasn't found"); return; }
            var up = Vector3.ProjectOnPlane(Vector3.up, n).normalized;
            var right = Vector3.Cross(up, -n);
            float height = width * H / W;

            // Where the game's buttons face now (to keep each one's turn relative to its panel when moving it).
            var gameFace = iface.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(f => f.name == "TacticalGUICurvedCenter_PresentationMesh");
            Quaternion gameFacing = Quaternion.identity;
            if (gameFace != null)
            {
                var normals = gameFace.sharedMesh.normals;
                var gn = normals.Length > 0 ? normals.Aggregate(Vector3.zero, (a, b) => a + gameFace.transform.TransformDirection(b)).normalized : -gameFace.transform.forward;
                if (Vector3.Dot(gn, n) < 0f) gn = -gn;
                gameFacing = Quaternion.LookRotation(-gn, Vector3.ProjectOnPlane(Vector3.up, gn).normalized);
            }

            // The game's faces go (they keep working underneath).
            foreach (var r in iface.GetComponentsInChildren<Renderer>(true).Where(r => r.name.EndsWith("PresentationMesh"))) r.enabled = false;
            var nav = iface.Find("Navmap Canvas");
            if (nav != null) foreach (var g in nav.GetComponentsInChildren<UnityEngine.UI.Graphic>(true)) g.enabled = false;
            var target3d = iface.Find("SubsystemTargeting");
            if (target3d != null) foreach (var r in target3d.GetComponentsInChildren<Renderer>(true)) r.enabled = false;

            texture = new Texture2D(W, H, TextureFormat.RGBA32, true) { name = "Voyager tactical", wrapMode = TextureWrapMode.Clamp, anisoLevel = 4 };
            face = GameObject.CreatePrimitive(PrimitiveType.Quad);
            face.name = "Voyager tactical face";
            UnityEngine.Object.Destroy(face.GetComponent<Collider>());
            face.transform.SetParent(ui, true);
            face.transform.position = centre + n * 0.004f;
            face.transform.rotation = Quaternion.LookRotation(-n, up);
            face.transform.localScale = Vector3.one;
            var lossy = face.transform.lossyScale;
            face.transform.localScale = new Vector3(width / Mathf.Max(1e-4f, lossy.x), height / Mathf.Max(1e-4f, lossy.y), 1f);
            var material = BridgeMaterial(face.GetComponent<MeshRenderer>().sharedMaterial);
            material.SetTexture("_MainTex", texture);
            material.SetTexture("_EmissionMap", texture);
            material.SetColor("_EmissionColor", Color.white);
            material.SetColor("_Color", Color.white);
            material.EnableKeyword("_EMISSION");
            face.GetComponent<MeshRenderer>().sharedMaterial = material;

            // The game's buttons onto the drawn ones.
            var all = iface.GetComponentsInChildren<Transform>(true);
            foreach (var b in Buttons)
            {
                var t = all.FirstOrDefault(x => x.name == b.Target);
                if (t == null) { Plugin.Log.LogWarning($"Voyager tactical: no game button {b.Target}"); continue; }
                float u = (b.Box.x + b.Box.width * 0.5f) / W - 0.5f, v = (b.Box.y + b.Box.height * 0.5f) / H - 0.5f;
                var relative = Quaternion.Inverse(gameFacing) * t.rotation;
                t.position = centre + right * (u * width) + up * (v * height) + n * 0.01f;
                t.rotation = face.transform.rotation * relative;
            }
            foreach (var r in Readouts)
            {
                var src = all.FirstOrDefault(x => x.name == r.Source);
                r.Text = src != null ? src.GetComponents<Component>().FirstOrDefault(c => c != null && c.GetType().Name == "TextMeshPro") : null;
                r.Shown = null;
            }
            Draw();
            Plugin.Log.LogInfo($"Voyager tactical: panel on the console ({width:0.00} x {height:0.00} m), {Buttons.Length} game buttons moved onto it");
        }

        private static Material BridgeMaterial(Material primitive)
        {
            var shader = Shader.Find("Standard");
            var m = shader != null ? new Material(shader) : new Material(primitive);
            foreach (var keyword in new[] { "_NORMALMAP", "_METALLICGLOSSMAP", "_SPECGLOSSMAP" }) m.DisableKeyword(keyword);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.6f);
            return m;
        }

        /// <summary>Each frame: the values read off the game's panel; drawn again when one changed.</summary>
        internal static void Update()
        {
            if (face == null || texture == null || Time.unscaledTime < next) return;
            next = Time.unscaledTime + 0.25f;
            bool changed = false;
            foreach (var r in Readouts)
            {
                var now = TextOf(r.Text) ?? "-";
                if (now != r.Shown) { r.Shown = now; changed = true; }
            }
            if (changed) Draw();
        }

        private static string TextOf(Component tmp)
        {
            if (tmp == null) return null;
            if (textGetter == null)
                textGetter = typeof(TMPro.TMP_Text).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.PropertyType == typeof(string) && p.CanRead && p.GetIndexParameters().Length == 0)
                    .Select(p => p.GetGetMethod()).FirstOrDefault(g => g != null && g.Name.Contains("text"))
                    ?? typeof(TMPro.TMP_Text).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.PropertyType == typeof(string) && p.CanWrite).Select(p => p.GetGetMethod()).FirstOrDefault();
            try { return textGetter?.Invoke(tmp, null) as string; } catch { return null; }
        }

        // ---- drawing ----

        private static void Draw()
        {
            var px = new Color32[W * H];
            for (int i = 0; i < px.Length; i++) px[i] = Black;
            // The LCARS frame: an elbow at the top left, a bar down the left, a bar along the top.
            Fill(px, new RectInt(0, 200, 70, 312), Orange, 0);
            Fill(px, new RectInt(0, 430, 300, 82), Orange, 40);
            Fill(px, new RectInt(70, 380, 60, 50), Black, 0);
            Fill(px, new RectInt(310, 470, 714, 42), Peach, 21);
            Fill(px, new RectInt(0, 20, 70, 170), Lilac, 0);
            HullName.DrawLabel(px, W, H, "TAKTIK", 330, 440, 30, 400, Orange, true);
            foreach (var r in Readouts)
            {
                HullName.DrawLabel(px, W, H, r.Label, r.X, r.Y, 26, 220, Lilac, true);
                HullName.DrawLabel(px, W, H, (r.Shown ?? "-").ToUpperInvariant(), r.X + 230, r.Y, 30, 600, Text, true);
            }
            foreach (var b in Buttons)
            {
                Fill(px, b.Box, b.Colour, b.Box.height / 2);
                HullName.DrawLabel(px, W, H, b.Label, b.Box.x + b.Box.width / 2, b.Box.y + b.Box.height / 2, 40, b.Box.width - 40, Black, false);
            }
            texture.SetPixels32(px);
            texture.Apply(true);
        }

        /// <summary>A filled rectangle with rounded corners (radius in pixels), smoothed at its edge.</summary>
        private static void Fill(Color32[] px, RectInt box, Color32 colour, int radius)
        {
            for (int y = Mathf.Max(0, box.y); y < Mathf.Min(H, box.y + box.height); y++)
                for (int x = Mathf.Max(0, box.x); x < Mathf.Min(W, box.x + box.width); x++)
                {
                    float a = 1f;
                    if (radius > 0)
                    {
                        float cx = Mathf.Clamp(x + 0.5f, box.x + radius, box.x + box.width - radius), cy = Mathf.Clamp(y + 0.5f, box.y + radius, box.y + box.height - radius);
                        float d = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                        a = Mathf.Clamp01(radius - d + 0.5f);
                    }
                    if (a <= 0f) continue;
                    int i = y * W + x;
                    px[i] = Color32.Lerp(px[i], colour, a);
                }
        }
    }
}
