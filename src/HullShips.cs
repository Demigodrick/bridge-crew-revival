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
    /// Our ship classes' name and registry on their hulls, where their models had their own (which was taken off the
    /// models): the places come from BepInEx/ships/&lt;Name&gt;/&lt;Name&gt;.lettering.txt, measured on the original model (each
    /// place's centre, the surface's normal, the reading axis, the length and height of the old lettering, what it said,
    /// and optionally which way its letters' tops pointed, and for lettering on an arc the arc's radius, the letters' height
    /// and where the arc's centre lies across the place). Kinds: "both" (registry large, "U.S.S. NAME" under it),
    /// "nameover" ("U.S.S. NAME" small, the registry large under it), "registry", "name", "barename" (the name without
    /// "U.S.S."), "line" ("U.S.S. NAME  NCC-…" on one line). Each place gets a decal cut from the hull itself:
    /// the triangles there facing the same way, lifted a hair off it, the text laid over them; the letters' tops point
    /// as given, or else to the ship's middle. Letters in the Enterprise-D decal's style: dark, a red rim. The decals go
    /// on the classes' bodies (CustomShip's templates), so every ship made from then on wears them; the textures are
    /// redrawn when the name changes, on the materials all of them share.
    /// </summary>
    internal static partial class HullName
    {
        private const string ShipDecalSuffix = " Name Decal";
        private static readonly Style ShipStyle = new Style { Fill = new Color32(0x16, 0x16, 0x1A, 255), Gap = 0.025f, Rim = new Color32(0xB5, 0x39, 0x3A, 255), RimWidth = 0.035f };

        private sealed class ShipPlace
        {
            public string Kind;               // both, registry, name, line
            public Vector3 Centre, Normal, Axis;
            public Vector3? Up;               // which way the letters' tops point (model file); null: to the ship's middle
            public float ArcRadius, ArcCap, ArcAcross; // on an arc: its radius to the letters' middle line, their height, the
                                                       // centre's place across (toward the tops: they face it); 0: straight
            public string Key => ArcRadius > 0f ? Kind + "|" + ArcRadius.ToString("0.####", CultureInfo.InvariantCulture) + "|" + ArcAcross.ToString("0.####", CultureInfo.InvariantCulture) : Kind;
            public float Length, Height;      // model file units
        }

        private sealed class ShipText { public Texture2D Texture; public Material Material; public int W, H; }

        private sealed class ShipLettering
        {
            public List<ShipPlace> Places;
            public readonly Dictionary<string, ShipText> Texts = new Dictionary<string, ShipText>();
            public string DrawnFor;
        }

        private static readonly Dictionary<string, ShipLettering> shipLettering = new Dictionary<string, ShipLettering>();
        private static Material shipDecalBase;
        private static bool keptBase;
        private static float shipsNext;
        private static readonly HashSet<int> shipsDecorated = new HashSet<int>();

        private static List<ShipPlace> ReadShipPlaces(CustomShip ship)
        {
            var file = Path.Combine(ship.Folder, ship.Name + ".lettering.txt");
            var places = new List<ShipPlace>();
            if (!File.Exists(file)) return places;
            var inv = CultureInfo.InvariantCulture;
            int number = 0;
            foreach (var raw in File.ReadAllLines(file))
            {
                number++;
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                int count = p.Length >= 18 ? 17 : p.Length >= 15 ? 14 : 11;
                var f = new float[18];
                bool ok = p.Length >= 12;
                for (int i = 1; ok && i <= count; i++) ok = float.TryParse(p[i], NumberStyles.Float, inv, out f[i]);
                if (!ok || f[10] <= 0f || f[11] <= 0f) { Plugin.Log.LogWarning($"Hull name: {ship.Name}.lettering.txt line {number} isn't a place; skipped"); continue; }
                float F(int i) => f[i];
                places.Add(new ShipPlace
                {
                    Kind = p[0].ToLowerInvariant(),
                    Centre = new Vector3(F(1), F(2), F(3)), Normal = new Vector3(F(4), F(5), F(6)), Axis = new Vector3(F(7), F(8), F(9)),
                    Length = F(10), Height = F(11),
                    Up = p.Length >= 15 ? new Vector3(F(12), F(13), F(14)) : (Vector3?)null,
                    ArcRadius = p.Length >= 18 ? Mathf.Abs(F(15)) : 0f, ArcCap = p.Length >= 18 ? F(16) : 0f, ArcAcross = p.Length >= 18 ? F(17) : 0f,
                });
            }
            return places;
        }

        internal static void UpdateShips()
        {
            if (Time.unscaledTime < shipsNext) return;
            shipsNext = Time.unscaledTime + 1f;
            foreach (var ship in CustomShip.All)
            {
                try { UpdateShip(ship); }
                catch (Exception e) { Plugin.Log.LogWarning($"Hull name: the {ship.Name}'s: " + e); shipsNext = Time.unscaledTime + 30f; }
            }
            // The decal material is the briefing room's and missions' too: kept across scenes.
            if (shipDecalBase != null && !keptBase) { UnityEngine.Object.DontDestroyOnLoad(shipDecalBase); keptBase = true; }
        }

        private static void UpdateShip(CustomShip ship)
        {
            var bodies = ship.Templates;
            if (bodies.Length == 0 || ship.FileToModel(Vector3.zero) == null) return;
            if (!shipLettering.TryGetValue(ship.Name, out var lettering))
            {
                List<ShipPlace> places;
                try { places = ReadShipPlaces(ship); }
                catch (Exception e) { places = new List<ShipPlace>(); Plugin.Log.LogWarning($"Hull name: {ship.Name}.lettering.txt: {e.Message}"); } // read once, however it went
                shipLettering[ship.Name] = lettering = new ShipLettering { Places = places };
            }
            if (lettering.Places.Count == 0) return;
            string name = ShipName.Name, registry = ShipName.Registry;
            bool show = name.Length > 0;
            if (show)
            {
                string want = name + "|" + registry + "|" + Plugin.HullFont.Value;
                if (lettering.DrawnFor != want && DrawShipTexts(ship, lettering, name, registry)) lettering.DrawnFor = want;
                if (lettering.Texts.Count == 0) return;
                foreach (var body in bodies)
                    if (body != null && shipsDecorated.Add(body.GetInstanceID())) { AddShipDecals(ship, lettering, body); CustomShip.Changed(); }
            }
            // Every ship of the class about (the templates and the ships made from them) shares the materials: see-through without a name.
            foreach (var t in lettering.Texts.Values)
                if (t.Material != null && t.Material.HasProperty("_Color")) t.Material.SetColor("_Color", new Color(1f, 1f, 1f, show ? 1f : 0f));
        }

        /// <summary>The Aegis's name decal material (Standard, faded at the edges), for ours: no glow.</summary>
        private static Material ShipDecalBaseMaterial()
        {
            if (shipDecalBase != null) return shipDecalBase;
            var ship = Resources.Load<GameObject>("Sector/Ships/Ship - Aegis");
            var body = ship != null ? AccessTools.Field(typeof(BaseSpacecraft), "_body")?.GetValue(ship.GetComponent<BaseSpacecraft>()) as SpacecraftBody : null;
            var source = body?.GetComponentsInChildren<MeshRenderer>(true).FirstOrDefault(r => r.name == "Aegis_Name_Decal")?.sharedMaterial;
            if (source == null) { Plugin.Log.LogWarning("Hull name: the Aegis's decal material wasn't found; our ships go without their names"); return null; }
            shipDecalBase = new Material(source) { name = "Ship Name Decal" };
            shipDecalBase.DisableKeyword("_EMISSION");
            if (shipDecalBase.HasProperty("_EmissionMap")) shipDecalBase.SetTexture("_EmissionMap", null);
            if (shipDecalBase.HasProperty("_EmissionColor")) shipDecalBase.SetColor("_EmissionColor", Color.black);
            if (shipDecalBase.HasProperty("_Color")) shipDecalBase.SetColor("_Color", Color.white);
            return shipDecalBase;
        }

        /// <summary>One texture per kind of place, as wide as its places are long (relative to their height).</summary>
        private static bool DrawShipTexts(CustomShip ship, ShipLettering lettering, string name, string registry)
        {
            string hull = HullText(name), reg = registry.Trim().ToUpperInvariant();
            if (reg.Length == 0) reg = hull; // no registry: the name where it would be
            string line = reg == hull ? hull : hull + "  " + reg;
            string bare = hull.StartsWith("U.S.S. ", StringComparison.Ordinal) ? hull.Substring(7) : hull;
            var face = LoadFace(hull + reg);
            var baseMaterial = ShipDecalBaseMaterial();
            if (face == null || baseMaterial == null) return false;
            foreach (var key in lettering.Places.Select(p => p.Key).Distinct())
            {
                var place = lettering.Places.First(p => p.Key == key);
                var kind = place.Kind;
                float aspect = place.Height / Mathf.Max(0.01f, place.Length);
                int w = 1024, h = Mathf.RoundToInt(w * aspect);
                if (h < 48) { h = 48; w = Mathf.Min(2048, Mathf.RoundToInt(h / Mathf.Max(1e-4f, aspect))); }
                if (h > 512) { h = 512; w = Mathf.Max(64, Mathf.RoundToInt(h / aspect)); }
                var lines = new List<KeyValuePair<Line, string>>();
                Line Make(float cy, float cap, string reference, float fill) =>
                    new Line { Shape = Shape.Straight, Cx = w / 2f, Cy = cy, Cap = cap, Reference = reference, ReferenceLength = w * fill, MaxLength = w * 0.96f, Tracking = 0.1f, Style = ShipStyle };
                if (place.ArcRadius > 0f)
                {
                    // On an arc: a smile (the circle's centre toward the letters' tops) or a frown, as the old lettering ran.
                    float py = h / place.Height;   // pixels per model unit (the texture keeps the place's proportions)
                    bool smile = place.ArcAcross > 0f;
                    float radius = place.ArcRadius * py, cap = place.ArcCap * py;
                    float half = Mathf.Min(0.99f, (place.Length - place.ArcCap) * 0.5f / place.ArcRadius);
                    float arc = 2f * Mathf.Asin(half) * radius;
                    string text = kind == "name" ? hull : kind == "barename" ? bare : kind == "line" ? line : reg;
                    lines.Add(new KeyValuePair<Line, string>(new Line
                    {
                        Shape = smile ? Shape.Smile : Shape.Frown, Cx = w / 2f, Cy = (place.ArcAcross / place.Height + 0.5f) * h, Radius = radius, Mid = smile ? -90f : 90f,
                        Cap = cap, Reference = text.StartsWith("U.S.S.") ? "U.S.S. NOVA" : "NCC-00000", ReferenceLength = arc, MaxLength = arc * 1.03f, Tracking = 0.12f, Style = ShipStyle,
                    }, text));
                }
                else switch (kind)
                {
                    case "nameover":
                        lines.Add(new KeyValuePair<Line, string>(Make(h * 0.32f, h * 0.4f, "NCC-00000", 0.78f), reg));
                        if (hull != reg) lines.Add(new KeyValuePair<Line, string>(Make(h * 0.8f, h * 0.2f, "U.S.S. NOVA", 0.62f), hull));
                        break;
                    case "barename":
                        lines.Add(new KeyValuePair<Line, string>(Make(h * 0.5f, h * 0.62f, "NOVA", 0.9f), bare));
                        break;
                    case "both":
                        lines.Add(new KeyValuePair<Line, string>(Make(h * 0.66f, h * 0.38f, "NCC-00000", 0.78f), reg));
                        if (hull != reg) lines.Add(new KeyValuePair<Line, string>(Make(h * 0.2f, h * 0.18f, "U.S.S. NOVA", 0.5f), hull));
                        break;
                    case "name":
                        lines.Add(new KeyValuePair<Line, string>(Make(h * 0.5f, h * 0.62f, "U.S.S. NOVA", 0.9f), hull));
                        break;
                    case "line":
                        lines.Add(new KeyValuePair<Line, string>(Make(h * 0.5f, h * 0.62f, "U.S.S. NOVA  NCC-00000", 0.95f), line));
                        break;
                    default:
                        lines.Add(new KeyValuePair<Line, string>(Make(h * 0.5f, h * 0.62f, "NCC-00000", 0.9f), reg));
                        break;
                }
                var px = new Color32[w * h];
                for (int i = 0; i < px.Length; i++) px[i] = new Color32(0x16, 0x16, 0x1A, 0);
                foreach (var kv in lines) DrawShipLine(face, kv.Key, kv.Value, px, w, h);
                if (!lettering.Texts.TryGetValue(key, out var t))
                {
                    t = new ShipText { W = w, H = h };
                    t.Material = new Material(baseMaterial) { name = ship.Name + " Name Decal (" + key + ")" };
                    UnityEngine.Object.DontDestroyOnLoad(t.Material);
                    lettering.Texts[key] = t;
                }
                // Made anew each time and not readable (no copy kept in memory); the old one freed.
                var old = t.Texture;
                t.Texture = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = ship.Name + " " + key, wrapMode = TextureWrapMode.Clamp, anisoLevel = 4 };
                t.Texture.SetPixels32(px);
                t.Texture.Apply(true, true);
                UnityEngine.Object.DontDestroyOnLoad(t.Texture);
                t.Material.SetTexture("_MainTex", t.Texture);
                if (old != null) UnityEngine.Object.Destroy(old);
                if (Plugin.HullPreview.Value) SavePreview(px, w, h, "hull-name-" + ship.Name + "-" + key.Replace('|', '_') + ".png");
            }
            Plugin.Log.LogInfo($"Hull name: the {ship.Name}'s: \"{reg}\" / \"{hull}\" in {face.Name}, {lettering.Texts.Count} kinds of place");
            return true;
        }

        private static void DrawShipLine(Face face, Line line, string text, Color32[] px, int w, int h)
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
        /// The decals on one body of a class, one per place: the hull's triangles there facing the same way, copied a hair
        /// above it, the text laid on them.
        /// </summary>
        private static void AddShipDecals(CustomShip ship, ShipLettering lettering, SpacecraftBody body)
        {
            var model = body.transform.Cast<Transform>().FirstOrDefault(t => t.name.StartsWith("Mdl_", StringComparison.Ordinal));
            if (model == null) return;
            var parts = model.GetComponentsInChildren<MeshFilter>(true).Where(f => f.name.StartsWith(ship.Name + "_", StringComparison.Ordinal) && f.sharedMesh != null).ToList();
            if (parts.Count == 0) return;
            var meshes = parts.Select(f => new KeyValuePair<Vector3[], int[]>(f.sharedMesh.vertices, f.sharedMesh.triangles)).ToList();
            float scale = ship.FileScale;
            int made = 0;
            foreach (var place in lettering.Places)
            {
                if (!lettering.Texts.TryGetValue(place.Key, out var text)) continue;
                var c = ship.FileToModel(place.Centre).Value;
                var n = ship.FileDirectionToModel(place.Normal);
                var axis = ship.FileDirectionToModel(place.Axis);
                // Letters' tops: across the reading axis, in the surface; as the old lettering's, or else toward the ship's middle.
                var up = Vector3.ProjectOnPlane(Vector3.Cross(n, axis), n).normalized;
                var towards = place.Up.HasValue ? ship.FileDirectionToModel(place.Up.Value) : -c;
                if (Vector3.Dot(up, towards) < 0f) up = -up;
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
                if (tris.Count == 0) { Plugin.Log.LogWarning($"Hull name: no hull at the {ship.Name}'s {place.Kind} place {place.Centre}"); continue; }
                var decal = new Mesh { name = ship.Name + ShipDecalSuffix, vertices = verts.ToArray(), uv = uvs.ToArray(), triangles = tris.ToArray() };
                decal.RecalculateNormals();
                decal.RecalculateBounds();
                var go = new GameObject(ship.Name + ShipDecalSuffix);
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
