using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using Sector.Spacecraft;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// The Nova's name and registry on its hull, where its model had its own (which was taken off the model): the
    /// places come from BepInEx/ships/Nova/Nova.lettering.txt, measured on the original model (each place's centre,
    /// the surface's normal, the reading axis, the length and height of the old lettering; and what it said: both lines,
    /// the registry or the name). Each place gets a decal cut from the hull itself: the triangles there facing the same
    /// way, lifted a hair off it, the text laid over them; the letters' tops face the ship's middle, as the old ones
    /// did. Letters in the Enterprise-D decal's style: dark, a red rim. The decals go on the Nova's bodies (NovaShip's
    /// templates), so every Nova made from then on wears them; the textures are redrawn when the name changes, on the
    /// materials all of them share.
    /// </summary>
    internal static partial class HullName
    {
        private const string NovaDecalName = "Nova Name Decal";
        private static readonly Style NovaStyle = new Style { Fill = new Color32(0x16, 0x16, 0x1A, 255), Gap = 0.025f, Rim = new Color32(0xB5, 0x39, 0x3A, 255), RimWidth = 0.035f };

        private sealed class NovaPlace
        {
            public string Kind;               // both, registry, name
            public Vector3 Centre, Normal, Axis;
            public float Length, Height;      // model file units
        }

        private sealed class NovaText { public Texture2D Texture; public Material Material; public int W, H; }

        private static readonly Dictionary<string, NovaText> novaTexts = new Dictionary<string, NovaText>();
        private static Material novaBase;
        private static string novaDrawnFor;
        private static float novaNext;
        private static List<NovaPlace> novaPlaces;
        private static readonly HashSet<int> novaDecorated = new HashSet<int>();

        private static List<NovaPlace> ReadNovaPlaces()
        {
            var file = Path.Combine(NovaShip.Folder, "Nova.lettering.txt");
            var places = new List<NovaPlace>();
            if (!File.Exists(file)) return places;
            var inv = CultureInfo.InvariantCulture;
            foreach (var raw in File.ReadAllLines(file))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 12) continue;
                float F(int i) => float.Parse(p[i], inv);
                places.Add(new NovaPlace
                {
                    Kind = p[0].ToLowerInvariant(),
                    Centre = new Vector3(F(1), F(2), F(3)), Normal = new Vector3(F(4), F(5), F(6)), Axis = new Vector3(F(7), F(8), F(9)),
                    Length = F(10), Height = F(11),
                });
            }
            return places;
        }

        internal static void UpdateNova()
        {
            if (Time.unscaledTime < novaNext) return;
            novaNext = Time.unscaledTime + 1f;
            try
            {
                var bodies = NovaShip.Templates;
                if (bodies.Length == 0 || NovaShip.FileToModel(Vector3.zero) == null) return;
                if (novaPlaces == null) novaPlaces = ReadNovaPlaces();
                if (novaPlaces.Count == 0) return;
                string name = ShipName.Name, registry = ShipName.Registry;
                bool show = name.Length > 0;
                if (show)
                {
                    string want = name + "|" + registry + "|" + Plugin.HullFont.Value;
                    if (novaDrawnFor != want && DrawNovaTexts(name, registry)) novaDrawnFor = want;
                    if (novaTexts.Count == 0) return;
                    foreach (var body in bodies)
                        if (body != null && novaDecorated.Add(body.GetInstanceID())) AddNovaDecals(body);
                }
                // Every Nova about (the templates and the ships made from them) shares the materials: see-through without a name.
                foreach (var t in novaTexts.Values)
                    if (t.Material != null && t.Material.HasProperty("_Color")) t.Material.SetColor("_Color", new Color(1f, 1f, 1f, show ? 1f : 0f));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Hull name: the Nova's: " + e); novaNext = Time.unscaledTime + 30f; }
        }

        /// <summary>The Aegis's name decal material (Standard, faded at the edges), for the Nova's: no glow.</summary>
        private static Material NovaBaseMaterial()
        {
            if (novaBase != null) return novaBase;
            var ship = Resources.Load<GameObject>("Sector/Ships/Ship - Aegis");
            var body = ship != null ? AccessTools.Field(typeof(BaseSpacecraft), "_body")?.GetValue(ship.GetComponent<BaseSpacecraft>()) as SpacecraftBody : null;
            var source = body?.GetComponentsInChildren<MeshRenderer>(true).FirstOrDefault(r => r.name == "Aegis_Name_Decal")?.sharedMaterial;
            if (source == null) { Plugin.Log.LogWarning("Hull name: the Aegis's decal material wasn't found; the Nova goes without its name"); return null; }
            novaBase = new Material(source) { name = "Nova Name Decal" };
            novaBase.DisableKeyword("_EMISSION");
            if (novaBase.HasProperty("_EmissionMap")) novaBase.SetTexture("_EmissionMap", null);
            if (novaBase.HasProperty("_EmissionColor")) novaBase.SetColor("_EmissionColor", Color.black);
            if (novaBase.HasProperty("_Color")) novaBase.SetColor("_Color", Color.white);
            return novaBase;
        }

        /// <summary>One texture per kind of place, as wide as its places are long (relative to their height).</summary>
        private static bool DrawNovaTexts(string name, string registry)
        {
            string hull = HullText(name), reg = registry.Trim().ToUpperInvariant();
            if (reg.Length == 0) reg = hull; // no registry: the name where it would be
            var face = LoadFace(hull + reg);
            var baseMaterial = NovaBaseMaterial();
            if (face == null || baseMaterial == null) return false;
            foreach (var kind in novaPlaces.Select(p => p.Kind).Distinct())
            {
                var place = novaPlaces.First(p => p.Kind == kind);
                const int w = 1024;
                int h = Mathf.Clamp(Mathf.RoundToInt(w * place.Height / Mathf.Max(0.01f, place.Length)), 64, 512);
                var lines = new List<KeyValuePair<Line, string>>();
                Line Make(float cy, float cap, string reference, float fill, string text) =>
                    new Line { Shape = Shape.Straight, Cx = w / 2f, Cy = cy, Cap = cap, Reference = reference, ReferenceLength = w * fill, MaxLength = w * 0.96f, Tracking = 0.1f, Style = NovaStyle };
                switch (kind)
                {
                    case "both":
                        lines.Add(new KeyValuePair<Line, string>(Make(h * 0.66f, h * 0.38f, "NCC-00000", 0.78f, reg), reg));
                        if (hull != reg) lines.Add(new KeyValuePair<Line, string>(Make(h * 0.2f, h * 0.18f, "U.S.S. NOVA", 0.5f, hull), hull));
                        break;
                    case "name":
                        lines.Add(new KeyValuePair<Line, string>(Make(h * 0.5f, h * 0.62f, "U.S.S. NOVA", 0.9f, hull), hull));
                        break;
                    default:
                        lines.Add(new KeyValuePair<Line, string>(Make(h * 0.5f, h * 0.62f, "NCC-00000", 0.9f, reg), reg));
                        break;
                }
                var px = new Color32[w * h];
                for (int i = 0; i < px.Length; i++) px[i] = new Color32(0x16, 0x16, 0x1A, 0);
                foreach (var kv in lines) DrawNovaLine(face, kv.Key, kv.Value, px, w, h);
                if (!novaTexts.TryGetValue(kind, out var t))
                {
                    t = new NovaText { W = w, H = h };
                    t.Texture = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = "Nova " + kind, wrapMode = TextureWrapMode.Clamp, anisoLevel = 4 };
                    UnityEngine.Object.DontDestroyOnLoad(t.Texture);
                    t.Material = new Material(baseMaterial) { name = "Nova Name Decal (" + kind + ")" };
                    t.Material.SetTexture("_MainTex", t.Texture);
                    novaTexts[kind] = t;
                }
                t.Texture.SetPixels32(px);
                t.Texture.Apply(true);
                if (Plugin.HullPreview.Value)
                    try { File.WriteAllBytes(Path.Combine(BepInEx.Paths.BepInExRootPath, "hull-name-Nova-" + kind + ".png"), t.Texture.EncodeToPNG()); } catch { }
            }
            Plugin.Log.LogInfo($"Hull name: the Nova's: \"{reg}\" / \"{hull}\" in {face.Name}, {novaTexts.Count} kinds of place");
            return true;
        }

        private static void DrawNovaLine(Face face, Line line, string text, Color32[] px, int w, int h)
        {
            var l = Lay(face, line, text);
            float capPx = l.CapUnits * l.Scale, gap = line.Style.Gap * capPx, rim = Mathf.Max(1.2f, line.Style.RimWidth * capPx);
            var area = Bounds(l, gap + rim + 4f, Math.Max(w, h));
            if (area == null) return;
            var cover = new float[area.W * area.H];
            Raster(face, l, area, cover);
            var dist = Distance(cover, area.W, area.H);
            for (int y = 0; y < area.H; y++)
                for (int x = 0; x < area.W; x++)
                {
                    int gx = area.X + x, gy = area.Y + y;
                    if (gx >= w || gy >= h) continue;
                    int li = y * area.W + x, i = gy * w + gx;
                    float c = cover[li], band = Band(dist[li], gap, gap + rim);
                    float alpha = Mathf.Max(c, band);
                    if (alpha <= 0f) continue;
                    var col = band > 0f ? Color32.Lerp(line.Style.Rim, line.Style.Fill, c) : line.Style.Fill;
                    byte a = (byte)Mathf.RoundToInt(alpha * 255f);
                    if (a >= px[i].a) px[i] = new Color32(col.r, col.g, col.b, a);
                }
        }

        /// <summary>
        /// The decals on one Nova body, one per place: the hull's triangles there facing the same way, copied a hair
        /// above it, the text laid on them (the letters' tops toward the ship's middle).
        /// </summary>
        private static void AddNovaDecals(SpacecraftBody body)
        {
            var model = body.transform.Cast<Transform>().FirstOrDefault(t => t.name.StartsWith("Mdl_", StringComparison.Ordinal));
            if (model == null) return;
            var parts = model.GetComponentsInChildren<MeshFilter>(true).Where(f => f.name.StartsWith("Nova_", StringComparison.Ordinal) && f.sharedMesh != null).ToList();
            if (parts.Count == 0) return;
            var meshes = parts.Select(f => new KeyValuePair<Vector3[], int[]>(f.sharedMesh.vertices, f.sharedMesh.triangles)).ToList();
            float scale = NovaShip.FileScale;
            int made = 0;
            foreach (var place in novaPlaces)
            {
                if (!novaTexts.TryGetValue(place.Kind, out var text)) continue;
                var c = NovaShip.FileToModel(place.Centre).Value;
                var n = NovaShip.FileDirectionToModel(place.Normal);
                var axis = NovaShip.FileDirectionToModel(place.Axis);
                // Letters' tops: across the reading axis, in the surface, toward the ship's middle.
                var up = Vector3.ProjectOnPlane(Vector3.Cross(n, axis), n).normalized;
                if (Vector3.Dot(up, -c) < 0f) up = -up;
                var read = Vector3.Cross(n, up).normalized; // left to right, seen from outside
                float length = place.Length * scale, height = place.Height * scale, depth = Mathf.Max(0.25f, height);
                const float lift = 0.012f;

                var verts = new List<Vector3>(); var uvs = new List<Vector2>(); var tris = new List<int>();
                foreach (var m in meshes)
                {
                    var v = m.Key; var t = m.Value;
                    for (int i = 0; i + 2 < t.Length && verts.Count < 64000; i += 3)
                    {
                        Vector3 a = v[t[i]], b = v[t[i + 1]], cc = v[t[i + 2]];
                        var tn = Vector3.Cross(b - a, cc - a);
                        if (tn.sqrMagnitude < 1e-14f) continue;
                        tn.Normalize();
                        if (Vector3.Dot(tn, n) < 0.5f) continue;
                        bool near = false, inside = false;
                        foreach (var p in new[] { a, b, cc })
                        {
                            var d = p - c;
                            float along = Vector3.Dot(d, read), across = Vector3.Dot(d, up), off = Vector3.Dot(d, n);
                            if (Mathf.Abs(off) < depth) near = true;
                            if (Mathf.Abs(along) < length * 0.5f + 0.05f && Mathf.Abs(across) < height * 0.5f + 0.05f) inside = true;
                        }
                        if (!near || !inside) continue;
                        int at = verts.Count;
                        foreach (var p in new[] { a, b, cc })
                        {
                            var d = p - c;
                            verts.Add(p + tn * lift);
                            uvs.Add(new Vector2(Vector3.Dot(d, read) / length + 0.5f, Vector3.Dot(d, up) / height + 0.5f));
                        }
                        tris.Add(at); tris.Add(at + 1); tris.Add(at + 2);
                    }
                }
                if (tris.Count == 0) { Plugin.Log.LogWarning($"Hull name: no hull at the Nova's {place.Kind} place {place.Centre}"); continue; }
                var decal = new Mesh { name = NovaDecalName, vertices = verts.ToArray(), uv = uvs.ToArray(), triangles = tris.ToArray() };
                decal.RecalculateNormals();
                decal.RecalculateBounds();
                var go = new GameObject(NovaDecalName);
                go.layer = model.gameObject.layer;
                go.transform.SetParent(model, false);
                go.AddComponent<MeshFilter>().sharedMesh = decal;
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = text.Material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                made++;
            }
            Plugin.Log.LogInfo($"Hull name: {made} name decals on {body.name}");
        }
    }
}
