using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// Test (bridge set): the game's tactical console shown on the set's console screens at its own quality.
    ///
    /// The game draws each tactical panel (a QuasarGUI) into a picture of its own with its own camera, and shows that
    /// picture on a curved strip; its buttons are touch targets under the strip's anchor, which the game places anew
    /// every frame (in the anchor's coordinates, the panel's own units). Here:
    ///   - the main panel's picture is cut into three pieces, the cuts found in its empty columns (never through a button),
    ///     each piece shown flat and undistorted on one of the three screens (left, middle, right), all at one scale;
    ///   - each button is moved under a stand-in anchor made for its piece, whose coordinates map the panel's units onto
    ///     that piece exactly as the picture lies there, so the game goes on placing its buttons, now on the pieces;
    ///   - the info panel (left) and the nav map go on the big screen behind the officer;
    ///   - the panels are kept drawing wherever their old place is (the game stops drawing a panel out of sight).
    /// The measurements (picture size, what was cut where, each button's place) go to the log and
    /// BepInEx/bridge-tactical-split.txt.
    /// </summary>
    internal static class TacticalSplit
    {
        internal sealed class Screen { public string Name; public Vector3 Centre, Normal; public float Width, Height; }

        private static readonly HashSet<int> keepDrawing = new HashSet<int>(); // the tactical panels' culling controllers
        private static readonly StringBuilder report = new StringBuilder();

        internal static void Patch(Harmony harmony)
        {
            var type = AccessTools.TypeByName("Quasar.GUI.QuasarGUICullingController") ?? AccessTools.TypeByName("QuasarGUICullingController");
            var culled = type != null ? AccessTools.PropertyGetter(type, "NGLMANKJENN") : null;
            if (culled != null) harmony.Patch(culled, postfix: new HarmonyMethod(typeof(TacticalSplit), nameof(Culled_Postfix)));
            else Plugin.Log.LogWarning("Tactical split: the GUI culling check wasn't found");
            // The check above is a one-line property the runtime may fold into its callers; the manager's check under it too.
            var manager = AccessTools.TypeByName("Quasar.GUI.QuasarGUICullingManager") ?? AccessTools.TypeByName("QuasarGUICullingManager");
            var managerCheck = manager != null ? AccessTools.Method(manager, "IsControllerCulled") : null;
            if (managerCheck != null) harmony.Patch(managerCheck, postfix: new HarmonyMethod(typeof(TacticalSplit), nameof(ManagerCulled_Postfix)));
        }

        internal static int CulledCalls;
        private static void Culled_Postfix(Component __instance, ref bool __result)
        {
            if (keepDrawing.Count == 0) return;
            CulledCalls++;
            if (__result && __instance != null && keepDrawing.Contains(__instance.GetInstanceID())) __result = false;
        }

        // While the split is up every panel draws (the manager's check doesn't say which panel it asks for).
        private static void ManagerCulled_Postfix(ref bool __result)
        {
            if (keepDrawing.Count == 0) return;
            CulledCalls++;
            __result = false;
        }

        /// <summary>Everything placed: ui is the tactical station's panel anchor (its children made by the game).</summary>
        internal static void Build(Transform ui, Func<Vector3, Vector3> place, Func<Vector3, Vector3> placeDirection, Dictionary<string, Screen> screens)
        {
            report.Length = 0;
            var iface = ui.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Interface-Tactical");
            if (iface == null) { Plugin.Log.LogWarning("Tactical split: the tactical interface wasn't found"); return; }
            foreach (var c in iface.GetComponentsInChildren<Component>(true).Where(c => c != null && c.GetType().Name == "QuasarGUICullingController")) keepDrawing.Add(c.GetInstanceID());

            var centre = iface.Find("TacticalGUICurvedCenter");
            var left = iface.Find("TacticalGUICurvedLeft");
            var nav = iface.Find("Navmap Canvas");
            Screen sl, sc, sr, wall;
            screens.TryGetValue("L", out sl); screens.TryGetValue("C", out sc); screens.TryGetValue("R", out sr); screens.TryGetValue("wall", out wall);
            if (centre != null && sl != null && sc != null && sr != null) SplitMain(centre, new[] { sl, sc, sr }, place, placeDirection);
            if (wall != null) Wall(left, nav, wall, place, placeDirection);
            try { File.WriteAllText(Path.Combine(BepInEx.Paths.BepInExRootPath, "bridge-tactical-split.txt"), report.ToString()); } catch { }
        }

        private static readonly List<RenderTexture> pictures = new List<RenderTexture>();

        /// <summary>Some time after the split: the pictures saved again (to see they still change) and how often the culling checks ran.</summary>
        internal static void Recheck()
        {
            foreach (var rt in pictures)
            {
                if (rt == null) continue;
                var prev = RenderTexture.active; var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                try { RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); tex.Apply(false); File.WriteAllBytes(Path.Combine(BepInEx.Paths.BepInExRootPath, "bridge-tactical-" + rt.name.Replace(' ', '_') + "-later.png"), tex.EncodeToPNG()); }
                catch { }
                finally { RenderTexture.active = prev; UnityEngine.Object.Destroy(tex); }
            }
            Plugin.Log.LogInfo($"Tactical split: culling checks overridden {CulledCalls} times; pictures saved again");
        }

        private static void Note(string line) { report.AppendLine(line); Plugin.Log.LogInfo("Tactical split: " + line); }

        // ---- the panel's picture ----

        private sealed class Panel
        {
            public Transform Root, Anchor; public MeshFilter Face; public Material Material; public RenderTexture Picture;
            public Vector2 UvMin, UvMax; // the part of the picture the strip shows
        }

        private static Panel Read(Transform root)
        {
            var p = new Panel { Root = root };
            p.Face = root.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(f => f.name.EndsWith("PresentationMesh") && f.sharedMesh != null);
            if (p.Face == null) return null;
            p.Anchor = p.Face.transform.parent;
            p.Material = p.Face.GetComponent<MeshRenderer>().sharedMaterial;
            var cam = root.GetComponentsInChildren<Camera>(true).FirstOrDefault();
            p.Picture = cam != null ? cam.targetTexture : null;
            if (p.Picture == null && p.Material != null) p.Picture = p.Material.mainTexture as RenderTexture;
            var uv = p.Face.sharedMesh.uv;
            p.UvMin = new Vector2(uv.Min(x => x.x), uv.Min(x => x.y)); p.UvMax = new Vector2(uv.Max(x => x.x), uv.Max(x => x.y));
            return p;
        }

        /// <summary>The picture read back: per column and per row, how much is drawn (not near black).</summary>
        private static bool Profile(RenderTexture rt, out float[] cols, out float[] rows)
        {
            cols = rows = null;
            if (rt == null) return false;
            if (!pictures.Contains(rt)) pictures.Add(rt);
            var prev = RenderTexture.active;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            try
            {
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply(false);
                var px = tex.GetPixels32();
                cols = new float[rt.width]; rows = new float[rt.height];
                for (int y = 0; y < rt.height; y++)
                    for (int x = 0; x < rt.width; x++)
                    {
                        var c = px[y * rt.width + x];
                        if (c.r + c.g + c.b > 45) { cols[x]++; rows[y]++; }
                    }
                try { File.WriteAllBytes(Path.Combine(BepInEx.Paths.BepInExRootPath, "bridge-tactical-" + rt.name.Replace(' ', '_') + ".png"), tex.EncodeToPNG()); } catch { }
                return true;
            }
            finally { RenderTexture.active = prev; UnityEngine.Object.Destroy(tex); }
        }

        /// <summary>Where each button sits on the picture: its UV (nearest point of the strip) and its anchor coordinates.</summary>
        private static float buttonLift; // the buttons' distance in front of the strip (toward the officer) per unit of their anchor z

        private static List<KeyValuePair<Transform, Vector2>> ButtonsOnPicture(Panel p)
        {
            var officer = GameObject.Find("Bridge/Stations-1701D/Tactical_root/Tactical");
            var eye = officer != null ? officer.transform.position + Vector3.up * 1.6f : p.Root.position + Vector3.up;
            double zd = 0, zz = 0;
            var result = new List<KeyValuePair<Transform, Vector2>>();
            var mesh = p.Face.sharedMesh; var uv = mesh.uv; var tris = mesh.triangles;
            var world = mesh.vertices.Select(v => p.Face.transform.TransformPoint(v)).ToArray();
            foreach (var t in p.Anchor.Cast<Transform>().Where(t => t.name.StartsWith("Hnd_", StringComparison.Ordinal)))
            {
                float best = float.MaxValue; Vector2 at = Vector2.zero; float lift = 0f;
                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    Vector3 ta = world[tris[i]], tb = world[tris[i + 1]], tc = world[tris[i + 2]];
                    var q = Closest(t.position, ta, tb, tc, out float wa, out float wb, out float wc);
                    float d = (t.position - q).sqrMagnitude;
                    if (d < best)
                    {
                        best = d; at = uv[tris[i]] * wa + uv[tris[i + 1]] * wb + uv[tris[i + 2]] * wc;
                        var nrm = Vector3.Cross(tb - ta, tc - ta).normalized;
                        if (Vector3.Dot(nrm, eye - q) < 0f) nrm = -nrm;
                        lift = Vector3.Dot(t.position - q, nrm);
                    }
                }
                zd += lift * t.localPosition.z; zz += t.localPosition.z * t.localPosition.z;
                result.Add(new KeyValuePair<Transform, Vector2>(t, at));
            }
            buttonLift = zz > 1e-9 ? (float)(zd / zz) : 0f;
            return result;
        }

        /// <summary>Least squares: u = a x + b over the buttons (x their anchor coordinate, u their picture coordinate).</summary>
        private static void Fit(IList<float> x, IList<float> u, out float a, out float b, out float rms)
        {
            int n = x.Count; double sx = 0, su = 0, sxx = 0, sxu = 0;
            for (int i = 0; i < n; i++) { sx += x[i]; su += u[i]; sxx += x[i] * x[i]; sxu += x[i] * u[i]; }
            double den = n * sxx - sx * sx;
            a = den != 0 ? (float)((n * sxu - sx * su) / den) : 0f; b = n > 0 ? (float)((su - a * sx) / n) : 0f;
            double e = 0; for (int i = 0; i < n; i++) { double d = a * x[i] + b - u[i]; e += d * d; }
            rms = n > 0 ? (float)Math.Sqrt(e / n) : 0f;
        }

        // ---- the main panel on three screens ----

        private static void SplitMain(Transform root, Screen[] screens, Func<Vector3, Vector3> place, Func<Vector3, Vector3> placeDirection)
        {
            var p = Read(root);
            if (p == null || p.Picture == null) { Note("main panel: no picture found"); return; }
            int W = p.Picture.width, H = p.Picture.height;
            Note($"main panel picture {p.Picture.name} {W}x{H}, strip shows uv {p.UvMin}..{p.UvMax}");
            if (!Profile(p.Picture, out var cols, out var rows)) { Note("main panel: picture not readable"); return; }

            var buttons = ButtonsOnPicture(p);
            float a, b, ru, c, d, rv;
            Fit(buttons.Select(k => k.Key.localPosition.x).ToList(), buttons.Select(k => k.Value.x).ToList(), out a, out b, out ru);
            Fit(buttons.Select(k => k.Key.localPosition.y).ToList(), buttons.Select(k => k.Value.y).ToList(), out c, out d, out rv);
            Note($"buttons: {buttons.Count}; u = {a:0.#####} x + {b:0.####} (rms {ru * W:0.#} px), v = {c:0.#####} y + {d:0.####} (rms {rv * H:0.#} px); lift {buttonLift * 1000f:0.###} mm per anchor z");
            foreach (var k in buttons) Note($"  {k.Key.name}: anchor ({k.Key.localPosition.x:0.##}, {k.Key.localPosition.y:0.##}) picture px ({k.Value.x * W:0}, {k.Value.y * H:0})");

            // What's drawn (or holds a button): rows and columns.
            Func<float, bool> drawnCol = n => n > 1f, drawnRow = n => n > 1f;
            var guard = new bool[W];
            foreach (var k in buttons) { int bx = Mathf.RoundToInt(k.Value.x * W); for (int x = Math.Max(0, bx - 14); x < Math.Min(W, bx + 15); x++) guard[x] = true; }
            int c0 = 0, c1 = W - 1, r0 = 0, r1 = H - 1;
            while (c0 < W - 1 && !drawnCol(cols[c0]) && !guard[c0]) c0++;
            while (c1 > c0 && !drawnCol(cols[c1]) && !guard[c1]) c1--;
            while (r0 < H - 1 && !drawnRow(rows[r0])) r0++;
            while (r1 > r0 && !drawnRow(rows[r1])) r1--;
            foreach (var k in buttons) { int by = Mathf.RoundToInt(k.Value.y * H); r0 = Math.Min(r0, by - 10); r1 = Math.Max(r1, by + 10); }
            r0 = Math.Max(0, r0 - 4); r1 = Math.Min(H - 1, r1 + 4); c0 = Math.Max(0, c0 - 4); c1 = Math.Min(W - 1, c1 + 4);
            Note($"drawn: columns {c0}..{c1}, rows {r0}..{r1}");

            // Cuts: two columns where little is drawn and no button is, the pieces' widths fitting the screens best.
            const float margin = 0.008f;
            float h = screens[0].Height - 2f * margin;
            float[] wMax = screens.Select(s => s.Width - 2f * margin).ToArray();
            var prefix = new float[W + 1]; for (int x = 0; x < W; x++) prefix[x + 1] = prefix[x] + cols[x];
            Func<int, int, float> sum = (x0, x1) => prefix[Math.Min(W, x1 + 1)] - prefix[Math.Max(0, x0)];
            int rowsShown = r1 - r0 + 1, span = c1 - c0 + 1;
            float bestScore = float.MinValue; int bx1 = c0 + span / 3, bx2 = c0 + 2 * span / 3; float bestP = 0f;
            for (int x1 = c0 + span / 8; x1 < c0 + span * 5 / 8; x1 += 2)
            {
                if (guard[x1]) continue;
                for (int x2 = x1 + span / 5; x2 < c1 - span / 8; x2 += 2)
                {
                    if (guard[x2]) continue;
                    // Each piece's own drawn extent (blank columns at a cut are left out).
                    int a0 = c0, a1 = Trim(cols, guard, x1, -1, c0), m0 = Trim(cols, guard, x1, +1, x2), m1 = Trim(cols, guard, x2, -1, x1), z0 = Trim(cols, guard, x2, +1, c1), z1 = c1;
                    float pxSize = Mathf.Min(h / rowsShown, Mathf.Min(wMax[0] / Math.Max(1, a1 - a0 + 1), Mathf.Min(wMax[1] / Math.Max(1, m1 - m0 + 1), wMax[2] / Math.Max(1, z1 - z0 + 1))));
                    float through = (sum(x1 - 1, x1 + 1) + sum(x2 - 1, x2 + 1)) / Mathf.Max(1f, rowsShown);
                    float score = pxSize * 10000f - through * 50f;
                    if (score > bestScore) { bestScore = score; bx1 = x1; bx2 = x2; bestP = pxSize; }
                }
            }
            int[][] pieces =
            {
                new[] { c0, Trim(cols, guard, bx1, -1, c0) },
                new[] { Trim(cols, guard, bx1, +1, bx2), Trim(cols, guard, bx2, -1, bx1) },
                new[] { Trim(cols, guard, bx2, +1, c1), c1 },
            };
            Note($"cuts at columns {bx1} and {bx2}; pieces {string.Join(", ", pieces.Select(q => q[0] + ".." + q[1]).ToArray())}; {bestP * 1000f:0.###} mm per picture pixel, {rowsShown * bestP:0.000} m tall");

            // The pieces and their button anchors.
            var anchors = new Transform[3];
            for (int i = 0; i < 3; i++)
            {
                var s = screens[i];
                var n = placeDirection(s.Normal);
                var up = Vector3.ProjectOnPlane(Vector3.up, n).normalized;
                var right = Vector3.Cross(up, -n);
                var centreW = place(s.Centre) + n * 0.004f;
                int x0 = pieces[i][0], x1 = pieces[i][1];
                float wPiece = (x1 - x0 + 1) * bestP, hPiece = rowsShown * bestP;
                Vector2 uvA = new Vector2((float)x0 / W, (float)r0 / H), uvB = new Vector2((float)(x1 + 1) / W, (float)(r1 + 1) / H);
                Quad(root.parent, root.name + " piece " + s.Name, centreW, right, up, wPiece, hPiece, uvA, uvB, p.Material, p.Face.gameObject.layer == LayerMask.NameToLayer("Attic") ? 0 : p.Face.gameObject.layer);
                // Stand-in anchor: anchor coordinates (x, y) -> picture (a x + b, c y + d) -> this piece's place.
                float mPerU = bestP * W, mPerV = bestP * H;
                var origin = centreW + right * ((b - (uvA.x + uvB.x) * 0.5f) * mPerU) + up * ((d - (uvA.y + uvB.y) * 0.5f) * mPerV) + n * 0.006f;
                var go = new GameObject(root.name + " anchor " + s.Name);
                go.transform.SetParent(root.parent, false);
                go.transform.position = origin;
                go.transform.rotation = Quaternion.LookRotation(Vector3.Cross(right, up), up);
                // Anchor z: toward the officer as on the original strip (its sign measured there), at the pieces' scale.
                float zAbs = (Mathf.Abs(a * mPerU) + Mathf.Abs(c * mPerV)) * 0.5f;
                var z = buttonLift > 0f ? -zAbs : zAbs; // local z runs into the screen: a z the original lifted toward the officer goes negative
                go.transform.localScale = Vector3.one;
                var parentScale = go.transform.lossyScale;
                go.transform.localScale = new Vector3(a * mPerU / parentScale.x, c * mPerV / parentScale.y, z / parentScale.z);
                anchors[i] = go.transform;
            }
            foreach (var k in buttons)
            {
                int px = Mathf.RoundToInt(k.Value.x * W);
                int i = px <= bx1 ? 0 : px <= bx2 ? 1 : 2;
                var t = k.Key;
                var local = t.localPosition; var lr = t.localRotation; var ls = t.localScale;
                t.SetParent(anchors[i], false);
                t.localPosition = local; t.localRotation = lr; t.localScale = ls;
            }
            p.Face.GetComponent<MeshRenderer>().enabled = false;
            Note("main panel split onto the three screens, its curved strip hidden");
        }

        /// <summary>From a cut, stepping away (dir) while columns are blank and hold no button, up to limit: the piece's drawn edge.</summary>
        private static int Trim(float[] cols, bool[] guard, int at, int dir, int limit)
        {
            int x = at;
            while (x != limit && cols[x] <= 1f && !guard[x]) x += dir;
            return x;
        }

        private static void Quad(Transform parent, string name, Vector3 centre, Vector3 right, Vector3 up, float w, float h, Vector2 uvA, Vector2 uvB, Material material, int layer)
        {
            var go = new GameObject(name);
            go.layer = layer;
            go.transform.SetParent(parent, true);
            go.transform.position = centre; go.transform.rotation = Quaternion.identity; go.transform.localScale = Vector3.one;
            var inv = go.transform;
            Func<float, float, Vector3> corner = (x, y) => inv.InverseTransformPoint(centre + right * (x * w) + up * (y * h));
            var mesh = new Mesh
            {
                name = name,
                vertices = new[] { corner(-0.5f, -0.5f), corner(0.5f, -0.5f), corner(0.5f, 0.5f), corner(-0.5f, 0.5f) },
                uv = new[] { new Vector2(uvA.x, uvA.y), new Vector2(uvB.x, uvA.y), new Vector2(uvB.x, uvB.y), new Vector2(uvA.x, uvB.y) },
                triangles = new[] { 0, 2, 1, 0, 3, 2 },
            };
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            // Facing the viewer (along -forward of right x up): flip the winding if the normal points away.
            var normal = Vector3.Cross(right, up);
            if (Vector3.Dot(inv.TransformDirection(mesh.normals[0]), -normal) < 0f) { mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 }; mesh.RecalculateNormals(); }
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
        }

        // ---- the info panel and the nav map on the wall screen ----

        private static void Wall(Transform left, Transform nav, Screen wall, Func<Vector3, Vector3> place, Func<Vector3, Vector3> placeDirection)
        {
            var n = placeDirection(wall.Normal);
            var up = Vector3.ProjectOnPlane(Vector3.up, n).normalized;
            var right = Vector3.Cross(up, -n);
            var centre = place(wall.Centre) + n * 0.004f;
            const float margin = 0.02f;
            float half = wall.Width * 0.5f - margin * 1.5f, h = wall.Height - 2f * margin;
            if (left != null)
            {
                var p = Read(left);
                if (p != null && p.Picture != null && Profile(p.Picture, out var cols, out var rows))
                {
                    int W = p.Picture.width, H = p.Picture.height, c0 = 0, c1 = W - 1, r0 = 0, r1 = H - 1;
                    while (c0 < W - 1 && cols[c0] <= 1f) c0++; while (c1 > c0 && cols[c1] <= 1f) c1--;
                    while (r0 < H - 1 && rows[r0] <= 1f) r0++; while (r1 > r0 && rows[r1] <= 1f) r1--;
                    float px = Mathf.Min(half / (c1 - c0 + 1), h / (r1 - r0 + 1));
                    var at = centre - right * (wall.Width * 0.25f);
                    Quad(left.parent, "info panel on the wall", at, right, up, (c1 - c0 + 1) * px, (r1 - r0 + 1) * px,
                        new Vector2((float)c0 / W, (float)r0 / H), new Vector2((float)(c1 + 1) / W, (float)(r1 + 1) / H), p.Material, 0);
                    p.Face.GetComponent<MeshRenderer>().enabled = false;
                    Note($"info panel {W}x{H}, drawn {c0}..{c1} x {r0}..{r1}, on the wall at {(c1 - c0 + 1) * px:0.00} x {(r1 - r0 + 1) * px:0.00} m");
                }
            }
            var rect = nav as RectTransform;
            if (rect != null)
            {
                var face = rect.TransformPoint(rect.rect.center);
                var nNav = -rect.forward; var upNav = rect.up;
                float wNav = rect.rect.width * rect.lossyScale.x, hNav = rect.rect.height * rect.lossyScale.y;
                float k = Mathf.Min(half / Mathf.Max(1e-3f, wNav), h / Mathf.Max(1e-3f, hNav));
                var turn = Quaternion.LookRotation(-n, up) * Quaternion.Inverse(Quaternion.LookRotation(-nNav, upNav));
                var target = centre + right * (wall.Width * 0.25f) + n * 0.002f;
                rect.position = target + turn * (rect.position - face) * k;
                rect.rotation = turn * rect.rotation;
                rect.localScale *= k;
                Note($"nav map {wNav:0.00} x {hNav:0.00} m onto the wall at {k:0.##} its size");
            }
        }

        private static Vector3 Closest(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out float wa, out float wb, out float wc)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) { wa = 1; wb = 0; wc = 0; return a; }
            Vector3 bp = p - b; float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) { wa = 0; wb = 1; wc = 0; return b; }
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f) { float v = d1 / (d1 - d3); wa = 1 - v; wb = v; wc = 0; return a + v * ab; }
            Vector3 cp = p - c; float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) { wa = 0; wb = 0; wc = 1; return c; }
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f) { float w = d2 / (d2 - d6); wa = 1 - w; wb = 0; wc = w; return a + w * ac; }
            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f) { float w = (d4 - d3) / ((d4 - d3) + (d5 - d6)); wa = 0; wb = 1 - w; wc = w; return b + w * (c - b); }
            float denom = 1f / (va + vb + vc); float v2 = vb * denom, w2 = vc * denom;
            wa = 1 - v2 - w2; wb = v2; wc = w2;
            return a + ab * v2 + ac * w2;
        }
    }
}
