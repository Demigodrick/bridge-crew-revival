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

        /// <summary>The tactical panels kept drawing from now on (before the split, so their pictures can be read).</summary>
        internal static void KeepDrawing(Transform ui)
        {
            foreach (var c in ui.GetComponentsInChildren<Component>(true).Where(c => c != null && c.GetType().Name == "QuasarGUICullingController")) keepDrawing.Add(c.GetInstanceID());
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
            // The set's own screen pictures under the pieces go.
            foreach (var r in UnityEngine.Object.FindObjectsOfType<MeshRenderer>())
                foreach (var m in new[] { "scrn2_2", "scrn2_3", "scrn2_4", "scrn2_7", "scrn2_8" })
                    if (r.name.StartsWith("Bridge_" + m + "_", StringComparison.Ordinal)) r.enabled = false;
            Screen sl, sc, sr, wall;
            screens.TryGetValue("L", out sl); screens.TryGetValue("C", out sc); screens.TryGetValue("R", out sr); screens.TryGetValue("wall", out wall);
            // Left screen: the weapons; middle: target selection; right: the weapons info panel; the wall: the map part and the nav map.
            if (centre != null && sl != null && sc != null && wall != null) SplitMain(centre, sl, sc, wall, place, placeDirection);
            if (left != null && sr != null) Whole(left, sr, place, placeDirection, "info");
            if (wall != null) Wall(nav, wall, place, placeDirection);
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
                try { RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); tex.Apply(false); File.WriteAllBytes(Path.Combine(BepInEx.Paths.BepInExRootPath, "bridge-tactical-" + rt.width + "x" + rt.height + "-later.png"), tex.EncodeToPNG()); }
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
        /// <summary>How much of a panel's picture is drawn now (0..1), to wait until the game has drawn it.</summary>
        internal static float Drawn(RenderTexture rt)
        {
            if (rt == null) return 0f;
            var prev = RenderTexture.active; var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            try
            {
                RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); tex.Apply(false);
                var px = tex.GetPixels32(); int n = 0;
                for (int i = 0; i < px.Length; i += 7) if (px[i].r + px[i].g + px[i].b > 45) n++;
                return n * 7f / px.Length;
            }
            finally { RenderTexture.active = prev; UnityEngine.Object.Destroy(tex); }
        }

        private static string pictureName;
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
                try { File.WriteAllBytes(Path.Combine(BepInEx.Paths.BepInExRootPath, "bridge-tactical-" + (pictureName ?? rt.GetInstanceID().ToString()) + ".png"), tex.EncodeToPNG()); } catch { }
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

        private static void SplitMain(Transform root, Screen sl, Screen sc, Screen wall, Func<Vector3, Vector3> place, Func<Vector3, Vector3> placeDirection)
        {
            var p = Read(root);
            if (p == null || p.Picture == null) { Note("main panel: no picture found"); return; }
            int W = p.Picture.width, H = p.Picture.height;
            Note($"main panel picture {W}x{H}, strip shows uv {p.UvMin}..{p.UvMax}, {Drawn(p.Picture) * 100f:0.#}% drawn");
            pictureName = "main";
            if (!Profile(p.Picture, out var cols, out var rows)) { Note("main panel: picture not readable"); return; }

            var buttons = ButtonsOnPicture(p);
            float a, b, ru, c, d, rv;
            Fit(buttons.Select(k => k.Key.localPosition.x).ToList(), buttons.Select(k => k.Value.x).ToList(), out a, out b, out ru);
            Fit(buttons.Select(k => k.Key.localPosition.y).ToList(), buttons.Select(k => k.Value.y).ToList(), out c, out d, out rv);
            Note($"buttons: {buttons.Count}; u = {a:0.#####} x + {b:0.####} (rms {ru * W:0.#} px), v = {c:0.#####} y + {d:0.####} (rms {rv * H:0.#} px); lift {buttonLift * 1000f:0.###} mm per anchor z");
            foreach (var k in buttons) Note($"  {k.Key.name}: anchor ({k.Key.localPosition.x:0.##}, {k.Key.localPosition.y:0.##}) picture px ({k.Value.x * W:0}, {k.Value.y * H:0})");

            // What's drawn: rows and columns; button columns count as drawn.
            var guard = new bool[W];
            foreach (var k in buttons) { int bx = Mathf.RoundToInt(k.Value.x * W); for (int x = Math.Max(0, bx - 14); x < Math.Min(W, bx + 15); x++) guard[x] = true; }
            int c0 = 0, c1 = W - 1, r0 = 0, r1 = H - 1;
            while (c0 < W - 1 && cols[c0] <= 1f && !guard[c0]) c0++;
            while (c1 > c0 && cols[c1] <= 1f && !guard[c1]) c1--;
            while (r0 < H - 1 && rows[r0] <= 1f) r0++;
            while (r1 > r0 && rows[r1] <= 1f) r1--;
            foreach (var k in buttons) { int by = Mathf.RoundToInt(k.Value.y * H); r0 = Math.Min(r0, by - 10); r1 = Math.Max(r1, by + 10); }
            r0 = Mathf.Clamp(r0 - 4, 0, H - 1); r1 = Mathf.Clamp(r1 + 4, 0, H - 1);
            Note($"drawn: columns {c0}..{c1}, rows {r0}..{r1}");

            // Its parts, by its buttons: the weapons (the left cluster), target selection (the next), the map (what's right of it).
            var xs = buttons.Select(k => Mathf.RoundToInt(k.Value.x * W)).OrderBy(x => x).ToList();
            int gapAt = 0, gap = -1;
            for (int i = 0; i + 1 < xs.Count; i++) if (xs[i + 1] - xs[i] > gap) { gap = xs[i + 1] - xs[i]; gapAt = i; }
            int weaponsEnd = xs[gapAt], targetStart = xs[gapAt + 1], targetEnd = xs[xs.Count - 1];
            int cut1 = BlankestNear(cols, weaponsEnd + 15, targetStart - 15, (weaponsEnd + targetStart) / 2);
            int cut2 = BlankestNear(cols, targetEnd + 25, Math.Min(c1, targetEnd + 260), targetEnd + 60);
            var weapons = new[] { c0, Trim(cols, guard, cut1, -1, c0) };
            var target = new[] { Trim(cols, guard, cut1, +1, cut2), Trim(cols, guard, cut2, -1, cut1) };
            var map = new[] { Trim(cols, guard, cut2, +1, c1), c1 };
            Note($"parts: weapons {weapons[0]}..{weapons[1]}, target {target[0]}..{target[1]}, map {map[0]}..{map[1]} (cuts {cut1}, {cut2})");

            // One scale for the two console pieces (as large as both screens allow); the map part fills its half of the wall.
            const float margin = 0.008f;
            int rowsShown = r1 - r0 + 1;
            float pxSize = Mathf.Min((sl.Height - 2f * margin) / rowsShown,
                Mathf.Min((sl.Width - 2f * margin) / (weapons[1] - weapons[0] + 1), (sc.Width - 2f * margin) / (target[1] - target[0] + 1)));
            float wallHalf = wall.Width * 0.5f - 0.03f;
            float mapPx = Mathf.Min((wall.Height - 0.04f) / rowsShown, wallHalf / Math.Max(1, map[1] - map[0] + 1));
            Note($"console pieces at {pxSize * 1000f:0.###} mm per picture pixel ({pxSize / 0.00089f * 100f:0}% of the game's size), {rowsShown * pxSize:0.000} m tall; map part {mapPx * 1000f:0.###} mm per pixel");

            var parts = new List<KeyValuePair<int[], Transform>>();
            parts.Add(new KeyValuePair<int[], Transform>(weapons, Piece(p, root, "weapons", sl, place, placeDirection, Vector3.zero, weapons[0], weapons[1], r0, r1, pxSize, a, b, c, d)));
            parts.Add(new KeyValuePair<int[], Transform>(target, Piece(p, root, "target", sc, place, placeDirection, Vector3.zero, target[0], target[1], r0, r1, pxSize, a, b, c, d)));
            if (map[1] > map[0] + 20)
                parts.Add(new KeyValuePair<int[], Transform>(map, Piece(p, root, "map", wall, place, placeDirection, new Vector3(-wall.Width * 0.25f, 0f, 0f), map[0], map[1], r0, r1, mapPx, a, b, c, d)));
            foreach (var k in buttons)
            {
                int px = Mathf.RoundToInt(k.Value.x * W);
                var part = px <= cut1 ? parts[0] : px <= cut2 || parts.Count < 3 ? parts[1] : parts[2];
                var t = k.Key;
                var local = t.localPosition; var lr = t.localRotation; var ls = t.localScale;
                t.SetParent(part.Value, false);
                t.localPosition = local; t.localRotation = lr; t.localScale = ls;
            }
            p.Face.GetComponent<MeshRenderer>().enabled = false;
            Note("main panel cut into its parts, its curved strip hidden");
        }

        /// <summary>The column with least drawn between lo and hi (nearest to prefer on ties).</summary>
        private static int BlankestNear(float[] cols, int lo, int hi, int prefer)
        {
            lo = Mathf.Clamp(lo, 0, cols.Length - 1); hi = Mathf.Clamp(hi, lo, cols.Length - 1);
            int best = Mathf.Clamp(prefer, lo, hi); float bestV = float.MaxValue;
            for (int x = lo; x <= hi; x++)
            {
                float v = cols[x] * 1000f + Mathf.Abs(x - prefer);
                if (v < bestV) { bestV = v; best = x; }
            }
            return best;
        }

        /// <summary>
        /// One piece of a panel's picture (columns x0..x1, rows r0..r1) shown flat on a screen at pxSize metres per picture
        /// pixel (offset in the screen's right/up/out), and the stand-in anchor its buttons go under: anchor coordinates
        /// (x, y) -> picture (a x + b, c y + d) -> this piece's place.
        /// </summary>
        private static Transform Piece(Panel p, Transform root, string name, Screen s, Func<Vector3, Vector3> place, Func<Vector3, Vector3> placeDirection,
            Vector3 offset, int x0, int x1, int r0, int r1, float pxSize, float a, float b, float c, float d)
        {
            int W = p.Picture.width, H = p.Picture.height;
            var n = placeDirection(s.Normal);
            var up = Vector3.ProjectOnPlane(Vector3.up, n).normalized;
            var right = Vector3.Cross(up, -n);
            var centreW = place(s.Centre) + right * offset.x + up * offset.y + n * (0.004f + offset.z);
            float wPiece = (x1 - x0 + 1) * pxSize, hPiece = (r1 - r0 + 1) * pxSize;
            Vector2 uvA = new Vector2((float)x0 / W, (float)r0 / H), uvB = new Vector2((float)(x1 + 1) / W, (float)(r1 + 1) / H);
            Quad(root.parent, root.name + " piece " + name, centreW, right, up, wPiece, hPiece, uvA, uvB, p.Material, 0);
            float mPerU = pxSize * W, mPerV = pxSize * H;
            var origin = centreW + right * ((b - (uvA.x + uvB.x) * 0.5f) * mPerU) + up * ((d - (uvA.y + uvB.y) * 0.5f) * mPerV) + n * 0.002f;
            var go = new GameObject(root.name + " anchor " + name);
            go.transform.SetParent(root.parent, false);
            go.transform.position = origin;
            go.transform.rotation = Quaternion.LookRotation(Vector3.Cross(right, up), up);
            float zAbs = (Mathf.Abs(a * mPerU) + Mathf.Abs(c * mPerV)) * 0.5f;
            var z = buttonLift > 0f ? -zAbs : zAbs; // local z runs into the screen: a z the original lifted toward the officer goes negative
            go.transform.localScale = Vector3.one;
            var ps = go.transform.lossyScale;
            go.transform.localScale = new Vector3(a * mPerU / ps.x, c * mPerV / ps.y, z / ps.z);
            Note($"piece {name}: columns {x0}..{x1}, {wPiece:0.000} x {hPiece:0.000} m on {s.Name}");
            return go.transform;
        }

        /// <summary>A whole panel (its drawn part) flat on a screen, as large as fits.</summary>
        private static void Whole(Transform panel, Screen s, Func<Vector3, Vector3> place, Func<Vector3, Vector3> placeDirection, string name)
        {
            var p = Read(panel);
            pictureName = name;
            if (p == null || p.Picture == null || !Profile(p.Picture, out var cols, out var rows)) { Note($"{name} panel: no picture"); return; }
            int W = p.Picture.width, H = p.Picture.height, c0 = 0, c1 = W - 1, r0 = 0, r1 = H - 1;
            while (c0 < W - 1 && cols[c0] <= 1f) c0++; while (c1 > c0 && cols[c1] <= 1f) c1--;
            while (r0 < H - 1 && rows[r0] <= 1f) r0++; while (r1 > r0 && rows[r1] <= 1f) r1--;
            if (c1 - c0 < 20 || r1 - r0 < 20) { c0 = 0; c1 = W - 1; r0 = 0; r1 = H - 1; } // nothing drawn yet: all of it
            const float margin = 0.008f;
            float px = Mathf.Min((s.Width - 2f * margin) / (c1 - c0 + 1), (s.Height - 2f * margin) / (r1 - r0 + 1));
            var n = placeDirection(s.Normal);
            var up = Vector3.ProjectOnPlane(Vector3.up, n).normalized;
            var right = Vector3.Cross(up, -n);
            Quad(panel.parent, name + " panel on " + s.Name, place(s.Centre) + n * 0.004f, right, up, (c1 - c0 + 1) * px, (r1 - r0 + 1) * px,
                new Vector2((float)c0 / W, (float)r0 / H), new Vector2((float)(c1 + 1) / W, (float)(r1 + 1) / H), p.Material, 0);
            p.Face.GetComponent<MeshRenderer>().enabled = false;
            Note($"{name} panel {W}x{H}, drawn {c0}..{c1} x {r0}..{r1}, on {s.Name} at {(c1 - c0 + 1) * px:0.000} x {(r1 - r0 + 1) * px:0.000} m");
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

        private static void Wall(Transform nav, Screen wall, Func<Vector3, Vector3> place, Func<Vector3, Vector3> placeDirection)
        {
            var n = placeDirection(wall.Normal);
            var up = Vector3.ProjectOnPlane(Vector3.up, n).normalized;
            var right = Vector3.Cross(up, -n);
            var centre = place(wall.Centre) + n * 0.004f;
            const float margin = 0.02f;
            float half = wall.Width * 0.5f - margin * 1.5f, h = wall.Height - 2f * margin;
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
