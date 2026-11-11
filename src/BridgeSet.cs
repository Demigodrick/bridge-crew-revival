using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using HarmonyLib;
using Sector.Spacecraft;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// Test: [Debug] BridgeSet = Voyager dresses the Next Generation's bridge as another bridge. The set comes from a model
    /// on this PC (BepInEx/bridges/&lt;Name&gt;/&lt;Name&gt;.obj with its .mtl and textures; nothing of it ships with the mod),
    /// placed around the captain's chair at full size, its viewscreen ahead where the game's is. The game's own walls,
    /// ceiling, floor, doors, back consoles and side chairs are hidden; what the crew works with stays: the flight control
    /// and operations consoles, the tactical rail, the captain's chair, the viewscreen.
    ///
    /// The model file's axes: y up, its viewscreen toward +z, metres. &lt;Name&gt;.bridge.txt says where its captain's place
    /// is ("captain x y z", the floor under the chairs' middle); the set is turned half round and put there.
    /// </summary>
    internal static class BridgeSet
    {
        private const string Marker = "EnvironmentArt/Environment_Static/BridgeTNG_Lance";
        private static readonly string[] Hidden =
        {
            "EnvironmentArt/Environment_Static/BridgeTNG_Lance", "EnvironmentArt/Environment_Static/BridgeTNG_Kevin",
            "EnvironmentArt/Environment_Static/BridgeTNG_Cody", "EnvironmentArt/Environment_Static/BridgeTNG_Tony",
            "EnvironmentArt/Environment_Static/FirstOfficer_Console", "EnvironmentArt/Environment_Static/Counselor_Console",
        };
        private static readonly Vector3 GameCaptain = new Vector3(0f, 49.81f, 0.49f); // the TNG bridge's captain's station

        /// <summary>A box of the model file (between two points on a plane, so wide up the plane, from n0 to n1 off it) whose triangles are left out.</summary>
        internal sealed class Carve
        {
            public Vector3 Centre, Along, Up, Normal; public float HalfLength, HalfUp, N0, N1;
            public bool Holds(Vector3 v)
            {
                var d = v - Centre; float o = Vector3.Dot(d, Normal);
                return Mathf.Abs(Vector3.Dot(d, Along)) <= HalfLength && Mathf.Abs(Vector3.Dot(d, Up)) <= HalfUp && o >= N0 && o <= N1;
            }
        }

        private sealed class PanelPlace { public string Part; public Vector3 Centre, Normal; public float Width; }

        private sealed class Loaded
        {
            public List<ObjModel.Part> Parts;
            public Dictionary<string, ObjModel.Material> Materials;
            public Dictionary<string, byte[]> Images = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            public Vector3 Captain = new Vector3(0f, 0.30f, -0.75f);
            public List<Carve> Carves = new List<Carve>(); // parts of the model taken away (above a console's screens, for a panel across them)
            public Vector3? Screen;     // the set's viewscreen: the middle of its back plane (model file)
            public float ScreenHeight;  // and its height
            public Vector3? Tactical, TacticalNormal, TacticalStand; // its tactical console's screen (middle, facing) and where its officer stands
            public List<PanelPlace> Panels = new List<PanelPlace>();  // which of the game's tactical panels goes on which of its screens
            public Dictionary<string, TacticalSplit.Screen> Splits = new Dictionary<string, TacticalSplit.Screen>(); // its console screens for the split tactical panel
            public string Error;
        }

        private static string name;
        private static Thread loading;
        private static volatile Loaded loaded;
        private static GameObject template;
        private static Vector3 placedCaptain; // the set's captain's place in its model file (the last one built)
        private static Vector3? screen; private static float screenHeight;
        private static Vector3? tactical, tacticalNormal, tacticalStand;
        private static List<PanelPlace> panels = new List<PanelPlace>();
        private static bool panelsPlaced;
        private static Dictionary<string, TacticalSplit.Screen> splits = new Dictionary<string, TacticalSplit.Screen>();
        private static float panelSeen = -1f, splitAt = -1f;
        private static int dressedScene = -1;
        private static bool panelDumped;
        private static float next;

        private static string Folder => Path.Combine(BepInEx.Paths.BepInExRootPath, Path.Combine("bridges", name));

        internal static void Update()
        {
            string want = (Plugin.BridgeSet.Value ?? "").Trim();
            if (want.Length == 0) return;
            if (want != name) { name = want; loading = null; loaded = null; template = null; dressedScene = -1; }
            if (loading == null && File.Exists(Path.Combine(Folder, name + ".obj")))
            {
                loading = new Thread(Load) { IsBackground = true, Name = name + " bridge" };
                loading.Start();
            }
            if (template == null && loaded != null)
            {
                if (loaded.Error != null) { Plugin.Log.LogWarning($"Bridge set {name}: " + loaded.Error); loaded = null; return; }
                template = Build(loaded);
                loaded = null;
            }
            if (template == null || Time.unscaledTime < next) return;
            // The tactical panel is made once a mission runs: its make-up written out then (for splitting it onto the set's screens).
            if (!panelDumped && dressedScene != -1)
            {
                var tacticalUi = GameObject.Find("Bridge/Stations-1701D/Tactical_root/Tactical_UIParent");
                if (tacticalUi != null && tacticalUi.transform.childCount > 0) { panelDumped = true; DumpTactical(tacticalUi.transform.parent); Plugin.Log.LogInfo("Bridge set: tactical panel written out"); }
            }
            if (panelsPlaced) { try { TacticalSplit.Probe(); } catch (Exception e) { Plugin.Log.LogWarning("Bridge set: probe: " + e.Message); } }
            if (splitAt > 0f && Time.unscaledTime - splitAt > 30f) { splitAt = -1f; try { TacticalSplit.Recheck(); } catch (Exception e) { Plugin.Log.LogWarning("Bridge set: recheck: " + e.Message); } }
            if (!panelsPlaced && dressedScene != -1 && splits.Count > 0)
            {
                // The split needs the panel's picture drawn: a few seconds after the game made it.
                var tacticalUi = GameObject.Find("Bridge/Stations-1701D/Tactical_root/Tactical_UIParent");
                if (tacticalUi != null && tacticalUi.transform.childCount > 0)
                {
                    if (panelSeen < 0f) { panelSeen = Time.unscaledTime; TacticalSplit.KeepDrawing(tacticalUi.transform); }
                    else if (Time.unscaledTime - panelSeen > 3f && (Time.unscaledTime - panelSeen > 60f || MainPanelDrawn(tacticalUi.transform)))
                    {
                        panelsPlaced = true; splitAt = Time.unscaledTime;
                        try { TacticalSplit.Build(tacticalUi.transform, Place, PlaceDirection, splits); }
                        catch (Exception e) { Plugin.Log.LogWarning("Bridge set: tactical split: " + e); }
                    }
                }
            }
            if (!panelsPlaced && dressedScene != -1 && panels.Count > 0 && splits.Count == 0)
            {
                var tacticalUi = GameObject.Find("Bridge/Stations-1701D/Tactical_root/Tactical_UIParent");
                if (tacticalUi != null && tacticalUi.transform.childCount > 0)
                {
                    panelsPlaced = true;
                    PlacePanels(tacticalUi.transform);
                }
            }
            next = Time.unscaledTime + 1f;
            var marker = GameObject.Find(Marker);
            if (marker == null) return;
            int scene = marker.GetInstanceID(); // a new bridge scene, a new marker
            if (scene == dressedScene) return;
            dressedScene = scene;
            Dress(marker.scene);
        }

        private static void Load()
        {
            var result = new Loaded();
            try
            {
                result.Parts = ObjModel.Read(Path.Combine(Folder, name + ".obj"), out var mats);
                result.Materials = mats;
                foreach (var m in mats.Values)
                    foreach (var file in new[] { m.Diffuse, m.Emission })
                        if (file != null && !result.Images.ContainsKey(file) && File.Exists(Path.Combine(Folder, file)))
                            result.Images[file] = File.ReadAllBytes(Path.Combine(Folder, file));
                var placeFile = Path.Combine(Folder, name + ".bridge.txt");
                if (File.Exists(placeFile))
                    foreach (var line in File.ReadAllLines(placeFile))
                    {
                        var p = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length >= 5 && p[0] == "screen")
                        {
                            var inv = System.Globalization.CultureInfo.InvariantCulture;
                            result.Screen = new Vector3(float.Parse(p[1], inv), float.Parse(p[2], inv), float.Parse(p[3], inv));
                            result.ScreenHeight = float.Parse(p[4], inv);
                        }
                        if (p.Length >= 7 && p[0] == "tactical")
                        {
                            var inv = System.Globalization.CultureInfo.InvariantCulture;
                            result.Tactical = new Vector3(float.Parse(p[1], inv), float.Parse(p[2], inv), float.Parse(p[3], inv));
                            result.TacticalNormal = new Vector3(float.Parse(p[4], inv), float.Parse(p[5], inv), float.Parse(p[6], inv));
                        }
                        if (p.Length >= 10 && p[0] == "split")
                        {
                            var inv = System.Globalization.CultureInfo.InvariantCulture;
                            result.Splits[p[1]] = new TacticalSplit.Screen
                            {
                                Name = p[1],
                                Centre = new Vector3(float.Parse(p[2], inv), float.Parse(p[3], inv), float.Parse(p[4], inv)),
                                Normal = new Vector3(float.Parse(p[5], inv), float.Parse(p[6], inv), float.Parse(p[7], inv)),
                                Width = float.Parse(p[8], inv), Height = float.Parse(p[9], inv),
                            };
                        }
                        if (p.Length >= 9 && p[0] == "panel")
                        {
                            var inv = System.Globalization.CultureInfo.InvariantCulture;
                            result.Panels.Add(new PanelPlace
                            {
                                Part = p[1].Replace('_', ' '),
                                Centre = new Vector3(float.Parse(p[2], inv), float.Parse(p[3], inv), float.Parse(p[4], inv)),
                                Normal = new Vector3(float.Parse(p[5], inv), float.Parse(p[6], inv), float.Parse(p[7], inv)),
                                Width = float.Parse(p[8], inv),
                            });
                        }
                        if (p.Length >= 4 && p[0] == "tacticalstand")
                        {
                            var inv = System.Globalization.CultureInfo.InvariantCulture;
                            result.TacticalStand = new Vector3(float.Parse(p[1], inv), float.Parse(p[2], inv), float.Parse(p[3], inv));
                        }
                        if (p.Length >= 13 && p[0] == "carve")
                        {
                            var inv = System.Globalization.CultureInfo.InvariantCulture;
                            Func<int, float> f = i => float.Parse(p[i], inv);
                            Vector3 l = new Vector3(f(1), f(2), f(3)), r = new Vector3(f(4), f(5), f(6)), nn = new Vector3(f(7), f(8), f(9)).normalized;
                            var along = (r - l).normalized;
                            result.Carves.Add(new Carve
                            {
                                Centre = (l + r) * 0.5f, Along = along, Normal = nn, Up = Vector3.Cross(nn, along).normalized,
                                HalfLength = (r - l).magnitude * 0.5f, HalfUp = f(10), N0 = f(11), N1 = f(12),
                            });
                        }
                        if (p.Length >= 4 && p[0] == "captain")
                            result.Captain = new Vector3(float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture), float.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture));
                    }
            }
            catch (Exception e) { result.Error = e.ToString(); }
            loaded = result;
        }

        /// <summary>The set, built once (inactive, kept across scenes): its pieces in the bridge's own space.</summary>
        private static GameObject Build(Loaded data)
        {
            var started = Time.realtimeSinceStartup;
            var root = new GameObject("Revival bridge set " + name);
            root.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(root);
            var baseMaterial = BaseMaterial();
            var materials = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
            var textures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
            Texture2D Tex(string file)
            {
                if (file == null || !data.Images.TryGetValue(file, out var bytes)) return null;
                if (textures.TryGetValue(file, out var t)) return t;
                t = new Texture2D(2, 2, TextureFormat.RGB24, true) { name = file, anisoLevel = 4 };
                if (!t.LoadImage(bytes)) { UnityEngine.Object.Destroy(t); t = null; }
                else { t.Compress(true); t.Apply(true, true); }
                return textures[file] = t;
            }
            Material MaterialFor(string key)
            {
                key = key ?? "";
                if (materials.TryGetValue(key, out var m)) return m;
                data.Materials.TryGetValue(key, out var om);
                if (om != null && om.Opacity < 0.1f) return materials[key] = null;
                m = baseMaterial != null ? new Material(baseMaterial) : new Material(Shader.Find("Standard"));
                m.name = "Bridge " + key;
                foreach (var keyword in new[] { "_NORMALMAP", "_METALLICGLOSSMAP", "_SPECGLOSSMAP", "_DETAIL_MULX2", "_PARALLAXMAP" }) m.DisableKeyword(keyword);
                foreach (var prop in new[] { "_MainTex", "_BumpMap", "_MetallicGlossMap", "_SpecGlossMap", "_DetailAlbedoMap", "_DetailNormalMap", "_OcclusionMap", "_ParallaxMap", "_EmissionMap" })
                    if (m.HasProperty(prop)) m.SetTexture(prop, null);
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0.1f);
                if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.35f);
                var albedo = Tex(om?.Diffuse);
                var glow = Tex(om?.Emission);
                var colour = albedo != null ? Color.white : (om?.Colour ?? Color.gray);
                m.SetColor("_Color", colour);
                if (albedo != null) m.SetTexture("_MainTex", albedo);
                // The game's bridge is lit by baked light the set doesn't get: a soft glow of its own colours keeps it from
                // going dark; screens and lamps glow fully.
                if (glow != null) { m.SetTexture("_EmissionMap", glow); m.SetColor("_EmissionColor", Color.white); }
                else if (om != null && om.Glow.maxColorComponent > 0.01f) m.SetColor("_EmissionColor", om.Glow);
                else { if (albedo != null) m.SetTexture("_EmissionMap", albedo); m.SetColor("_EmissionColor", colour * 0.2f); }
                m.EnableKeyword("_EMISSION");
                return materials[key] = m;
            }
            // Model file -> bridge: turned half round (its +z ahead becomes the bridge's -z), its captain's place on the
            // game's. (x, y, z) -> (x, y, -z) mirrors, as right-handed to left-handed does, so the triangles turn too.
            var c = data.Captain;
            placedCaptain = c; screen = data.Screen; screenHeight = data.ScreenHeight;
            tactical = data.Tactical; tacticalNormal = data.TacticalNormal; tacticalStand = data.TacticalStand; panels = data.Panels; splits = data.Splits;
            Func<Vector3, Vector3> place = v => new Vector3(v.x - c.x, v.y - c.y, -(v.z - c.z)) + GameCaptain;
            int pieces = 0, tris = 0, carved = 0;
            for (int n = 0; n < data.Parts.Count; n++)
            {
                var part = data.Parts[n];
                var material = MaterialFor(part.Texture);
                if (material == null) continue;
                var kept = part.Triangles;
                if (data.Carves.Count > 0)
                {
                    var list = new List<int>(kept.Length);
                    for (int i = 0; i + 2 < kept.Length; i += 3)
                    {
                        var mid = (part.Vertices[kept[i]] + part.Vertices[kept[i + 1]] + part.Vertices[kept[i + 2]]) / 3f;
                        if (data.Carves.Any(cv => cv.Holds(mid))) { carved++; continue; }
                        list.Add(kept[i]); list.Add(kept[i + 1]); list.Add(kept[i + 2]);
                    }
                    kept = list.ToArray();
                    if (kept.Length == 0) continue;
                }
                var t = new int[kept.Length];
                for (int i = 0; i + 2 < t.Length; i += 3) { t[i] = kept[i]; t[i + 1] = kept[i + 2]; t[i + 2] = kept[i + 1]; }
                var mesh = new Mesh { name = "Bridge " + part.Name, vertices = part.Vertices.Select(place).ToArray(), triangles = t };
                if (part.Uv != null && part.Uv.Length == part.Vertices.Length) mesh.uv = part.Uv;
                if (part.Normals != null && part.Normals.Length == part.Vertices.Length && part.Normals.Any(x => x != Vector3.zero)) mesh.normals = part.Normals.Select(x => new Vector3(x.x, x.y, -x.z)).ToArray();
                else mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                var go = new GameObject("Bridge_" + part.Name + "_" + n);
                go.transform.SetParent(root.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = material;
                // Its back too: the model was made to be seen from both sides (many of its faces point away from the room).
                var back = new Mesh { name = mesh.name + " back", vertices = mesh.vertices, normals = mesh.normals.Select(x => -x).ToArray(), triangles = kept };
                if (mesh.uv.Length == mesh.vertexCount) back.uv = mesh.uv;
                back.RecalculateBounds();
                var goBack = new GameObject(go.name + "_back");
                goBack.transform.SetParent(root.transform, false);
                goBack.AddComponent<MeshFilter>().sharedMesh = back;
                goBack.AddComponent<MeshRenderer>().sharedMaterial = material;
                pieces++; tris += t.Length / 3;
            }
            Plugin.Log.LogInfo($"Bridge set {name}: {pieces} pieces, {tris} triangles ({carved} carved away), {materials.Count} materials, {textures.Count} textures, built in {(Time.realtimeSinceStartup - started) * 1000f:0} ms");
            return root;
        }

        /// <summary>
        /// The game's viewscreen (its glass, which shows space, and the crew's screen overlays) moved and scaled into the
        /// set's viewscreen: as tall as it, its back against the set's screen plane.
        /// </summary>
        private static void MoveViewscreen()
        {
            if (screen == null || screenHeight <= 0f) return;
            var glass = GameObject.Find("Bridge/ViewScreenReferencePoint");
            if (glass == null) { Plugin.Log.LogWarning("Bridge set: the viewscreen wasn't found"); return; }
            var renderers = glass.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return;
            var bounds = renderers[0].bounds;
            foreach (var r in renderers) bounds.Encapsulate(r.bounds);
            float k = screenHeight / Mathf.Max(0.01f, bounds.size.y);
            var s = screen.Value; var c = placedCaptain;
            var back = new Vector3(s.x - c.x, s.y - c.y, -(s.z - c.z)) + GameCaptain;
            var target = back + new Vector3(0f, 0f, bounds.size.z * k * 0.5f); // toward the captain: the screen's depth in front of the plane
            foreach (var path in new[] { "Bridge/ViewScreenReferencePoint", "Bridge/ViewscreenUI-1701D" })
            {
                var go = GameObject.Find(path);
                if (go == null) continue;
                var t = go.transform;
                t.position = target + (t.position - bounds.center) * k;
                t.localScale *= k;
            }
            Plugin.Log.LogInfo($"Bridge set {name}: viewscreen moved to {target} at {k:0.###} its size (was {bounds.center}, {bounds.size})");
        }

        private static Vector3 Place(Vector3 v) => new Vector3(v.x - placedCaptain.x, v.y - placedCaptain.y, -(v.z - placedCaptain.z)) + GameCaptain;
        private static Vector3 PlaceDirection(Vector3 d) => new Vector3(d.x, d.y, -d.z).normalized;

        /// <summary>
        /// The tactical station moved to the set's tactical console: its officer's place (the station root) onto the
        /// set's floor before the console, facing it; its panel laid onto the console's screen, at its own size. The
        /// game's tactical rail (the horseshoe it stood at) goes. The panel's make-up is written out for splitting it later.
        /// </summary>
        private static void MoveTactical()
        {
            if (tactical == null || tacticalNormal == null || tacticalStand == null) return;
            var root = GameObject.Find("Bridge/Stations-1701D/Tactical_root");
            var ui = root != null ? root.transform.Find("Tactical_UIParent") : null;
            if (root == null || ui == null) { Plugin.Log.LogWarning("Bridge set: the tactical station wasn't found"); return; }
            DumpTactical(root.transform);
            foreach (var r in UnityEngine.Object.FindObjectsOfType<MeshRenderer>().Where(r => r.name == "BridgeTNG_TacticalConsole" || r.transform.parent != null && r.transform.parent.name == "BridgeTNG_TacticalConsole"))
                r.enabled = false;

            // The station as one: its officer's place (the station object) onto the set's floor before the console, turned
            // so the officer looks straight at the console (square to its screens), not at the angle the game's console
            // stands to its officer. The panels are laid onto the set's screens later, so they don't depend on this turn.
            var officer = root.transform.Find("Tactical");
            var o = officer != null ? officer.position : root.transform.position;
            var dirGame = Vector3.ProjectOnPlane(officer != null ? officer.forward : root.transform.forward, Vector3.up);
            var stand = Place(tacticalStand.Value);
            var dirSet = Vector3.ProjectOnPlane(Place(tactical.Value) - Place(tactical.Value + tacticalNormal.Value), Vector3.up);
            Plugin.Log.LogInfo($"Bridge set {name}: the tactical officer's facing is {Vector3.Angle(dirGame, Vector3.ProjectOnPlane(ui.position - o, Vector3.up)):0} degrees off the game's panel anchor");
            if (dirGame.sqrMagnitude < 1e-6f || dirSet.sqrMagnitude < 1e-6f) return;
            var turn = Quaternion.FromToRotation(dirGame.normalized, dirSet.normalized);
            var t = root.transform;
            t.position = stand + turn * (t.position - o);
            t.rotation = turn * t.rotation;
            Plugin.Log.LogInfo($"Bridge set {name}: tactical officer at {stand}, facing the console ({turn.eulerAngles.y:0} degrees turned)");
        }

        /// <summary>
        /// The game's tactical panels (each a whole: its picture and the hand targets on it) laid onto the set's console
        /// screens: turned flat onto each, as wide as it, a hair above it. The 3D target view goes with the middle panel.
        /// </summary>
        private static void PlacePanels(Transform ui)
        {
            var iface = ui.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Interface-Tactical");
            if (iface == null) { Plugin.Log.LogWarning("Bridge set: the tactical interface wasn't found"); return; }
            var officer = GameObject.Find("Bridge/Stations-1701D/Tactical_root/Tactical");
            foreach (var place in panels)
            {
                var part = iface.Find(place.Part);
                if (part == null) { Plugin.Log.LogWarning($"Bridge set: no tactical panel '{place.Part}'"); continue; }
                Vector3 centre, n, up; float width;
                var rect = part as RectTransform;
                var face = part.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(f => f.name.EndsWith("PresentationMesh") && f.sharedMesh != null);
                if (face != null)
                {
                    var verts = face.sharedMesh.vertices.Select(v => face.transform.TransformPoint(v)).ToArray();
                    var normals = face.sharedMesh.normals;
                    n = normals.Length > 0 ? normals.Aggregate(Vector3.zero, (a, b) => a + face.transform.TransformDirection(b)).normalized : -face.transform.forward;
                    centre = verts.Aggregate(Vector3.zero, (a, b) => a + b) / Mathf.Max(1, verts.Length);
                    if (officer != null && Vector3.Dot(n, officer.transform.position + Vector3.up * 1.6f - centre) < 0f) n = -n;
                    up = Vector3.ProjectOnPlane(Vector3.up, n).normalized;
                    var right = Vector3.Cross(up, n);
                    width = verts.Max(v => Vector3.Dot(v - centre, right)) - verts.Min(v => Vector3.Dot(v - centre, right));
                }
                else if (rect != null)
                {
                    n = -rect.forward; up = rect.up;
                    centre = rect.TransformPoint(rect.rect.center);
                    width = rect.rect.width * rect.lossyScale.x;
                }
                else continue;
                if (width < 1e-3f) continue;
                var nT = PlaceDirection(place.Normal);
                var upT = Vector3.ProjectOnPlane(Vector3.up, nT).normalized;
                var target = Place(place.Centre) + nT * 0.015f;
                var turn = Quaternion.LookRotation(-nT, upT) * Quaternion.Inverse(Quaternion.LookRotation(-n, up));
                float k = place.Width / width;
                var moved = new List<Transform> { part };
                if (place.Part == "TacticalGUICurvedCenter") { var t3d = iface.Find("SubsystemTargeting"); if (t3d != null) moved.Add(t3d); }
                foreach (var t in moved)
                {
                    t.position = target + turn * (t.position - centre) * k;
                    t.rotation = turn * t.rotation;
                    t.localScale *= k;
                }
                if (face != null) Flatten(face, part, nT, upT, target);
                Plugin.Log.LogInfo($"Bridge set {name}: tactical panel {place.Part} ({width:0.00} m wide) onto the console at {target}, at {k:0.##} its size");
            }
        }

        /// <summary>
        /// A curved panel made flat on the console: each corner of its picture mesh laid on the screen's plane where its
        /// picture coordinates put it (the picture keeps its proportions and area); the hand targets on it (its buttons)
        /// moved to the same places of the picture.
        /// </summary>
        private static void Flatten(MeshFilter face, Transform part, Vector3 n, Vector3 up, Vector3 centre)
        {
            var mesh = face.mesh;
            var local = mesh.vertices; var uv = mesh.uv; var tris = mesh.triangles;
            if (uv == null || uv.Length != local.Length || tris.Length < 3) return;
            var world = local.Select(v => face.transform.TransformPoint(v)).ToArray();
            double area = 0, uvArea = 0;
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                Vector3 a = world[tris[i]], b = world[tris[i + 1]], c = world[tris[i + 2]];
                area += Vector3.Cross(b - a, c - a).magnitude * 0.5;
                Vector2 ua = uv[tris[i]], ub = uv[tris[i + 1]], uc2 = uv[tris[i + 2]];
                uvArea += Mathf.Abs((ub.x - ua.x) * (uc2.y - ua.y) - (ub.y - ua.y) * (uc2.x - ua.x)) * 0.5;
            }
            if (uvArea < 1e-9) return;
            // Metres per unit of picture coordinate, across and up, measured on the curved mesh's edges that run along
            // each (the picture isn't square, so one scale for both would squash it).
            double lu = 0, nu = 0, lv = 0, nv = 0;
            for (int i = 0; i + 2 < tris.Length; i += 3)
                for (int e = 0; e < 3; e++)
                {
                    int i0 = tris[i + e], i1 = tris[i + (e + 1) % 3];
                    float du = Mathf.Abs(uv[i1].x - uv[i0].x), dv = Mathf.Abs(uv[i1].y - uv[i0].y), len = (world[i1] - world[i0]).magnitude;
                    if (du > 1e-5f && du > 4f * dv) { lu += len; nu += du; }
                    else if (dv > 1e-5f && dv > 4f * du) { lv += len; nv += dv; }
                }
            float fallback = (float)Math.Sqrt(area / uvArea);
            float scaleU = nu > 0 ? (float)(lu / nu) : fallback, scaleV = nv > 0 ? (float)(lv / nv) : fallback;
            float u0 = uv.Min(x => x.x), u1 = uv.Max(x => x.x), v0 = uv.Min(x => x.y), v1 = uv.Max(x => x.y);
            Vector2 mid = new Vector2((u0 + u1) * 0.5f, (v0 + v1) * 0.5f);
            var right = Vector3.Cross(up, -n);
            var meanWorld = world.Aggregate(Vector3.zero, (x, y) => x + y) / world.Length;
            // Which way the picture runs on the curved mesh, so the flat one isn't mirrored.
            float su = 0f, sv = 0f;
            for (int i = 0; i < world.Length; i++) { su += (uv[i].x - mid.x) * Vector3.Dot(world[i] - meanWorld, right); sv += (uv[i].y - mid.y) * Vector3.Dot(world[i] - meanWorld, up); }
            su = su < 0f ? -1f : 1f; sv = sv < 0f ? -1f : 1f;
            Func<Vector2, Vector3> flat = t => centre + right * ((t.x - mid.x) * scaleU * su) + up * ((t.y - mid.y) * scaleV * sv);
            // Buttons first: where on the picture each one sits (nearest point of the curved mesh).
            var targets = part.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith("Hnd_", StringComparison.Ordinal)).ToList();
            var moves = new List<KeyValuePair<Transform, Vector3>>();
            foreach (var t in targets)
            {
                var p = t.position; float best = float.MaxValue; Vector2 at = mid; float lift = 0f;
                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    Vector3 a = world[tris[i]], b = world[tris[i + 1]], c = world[tris[i + 2]];
                    var q = ClosestOnTriangle(p, a, b, c, out float wa, out float wb, out float wc);
                    float d = (p - q).sqrMagnitude;
                    if (d < best) { best = d; at = uv[tris[i]] * wa + uv[tris[i + 1]] * wb + uv[tris[i + 2]] * wc; lift = Vector3.Dot(p - q, Vector3.Cross(b - a, c - a).normalized); }
                }
                moves.Add(new KeyValuePair<Transform, Vector3>(t, flat(at) + n * Mathf.Abs(lift)));
            }
            mesh.vertices = uv.Select(t => face.transform.InverseTransformPoint(flat(t))).ToArray();
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            foreach (var mc in face.GetComponents<MeshCollider>()) mc.sharedMesh = mesh;
            var facing = Quaternion.LookRotation(-n, up);
            foreach (var m in moves) { m.Key.position = m.Value; }
            Plugin.Log.LogInfo($"Bridge set: {part.name} flattened ({(u1 - u0) * scaleU:0.00} x {(v1 - v0) * scaleV:0.00} m), {moves.Count} buttons moved");
        }

        private static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out float wa, out float wb, out float wc)
        {
            // Ericson, Real-Time Collision Detection 5.1.5
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

        // Whether the game has drawn the tactical main panel yet (its picture is black until then); checked once a second.
        private static float drawnCheck;
        private static bool MainPanelDrawn(Transform ui)
        {
            if (Time.unscaledTime < drawnCheck) return false;
            drawnCheck = Time.unscaledTime + 1f;
            var main = ui.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "TacticalGUICurvedCenter");
            var cam = main != null ? main.GetComponentsInChildren<Camera>(true).FirstOrDefault() : null;
            return cam != null && TacticalSplit.Drawn(cam.targetTexture) > 0.04f;
        }

        private static void DumpTactical(Transform root)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                void Walk(Transform x, int depth)
                {
                    var rt = x as RectTransform;
                    var comps = string.Join(",", x.GetComponents<Component>().Where(k => k != null && !(k is Transform)).Select(k => k.GetType().Name).ToArray());
                    sb.Append(new string(' ', depth * 2)).Append(x.name).Append(x.gameObject.activeSelf ? "" : " [off]")
                      .Append(rt != null ? $"  rect {rt.rect.width:0.#}x{rt.rect.height:0.#} at {rt.anchoredPosition} scale {rt.lossyScale.x:0.####}" : $"  pos {x.localPosition}")
                      .Append("  {").Append(comps).Append("}\n");
                    if (depth < 12) for (int i = 0; i < x.childCount; i++) Walk(x.GetChild(i), depth + 1);
                }
                Walk(root, 0);
                File.WriteAllText(Path.Combine(BepInEx.Paths.BepInExRootPath, "bridge-tactical-dump.txt"), sb.ToString());
            }
            catch (Exception e) { Plugin.Log.LogWarning("Bridge set: tactical dump: " + e.Message); }
        }

        private static Material BaseMaterial()
        {
            var ship = Resources.Load<GameObject>("Sector/Ships/Ship - Aegis");
            var body = ship != null ? AccessTools.Field(typeof(BaseSpacecraft), "_body")?.GetValue(ship.GetComponent<BaseSpacecraft>()) as SpacecraftBody : null;
            return body?.GetComponentsInChildren<MeshRenderer>(true).Select(r => r.sharedMaterial).FirstOrDefault(m => m != null && m.name.StartsWith("Aegis_Body", StringComparison.Ordinal));
        }

        /// <summary>The bridge scene dressed: the game's walls and such hidden, the set put in.</summary>
        private static void Dress(UnityEngine.SceneManagement.Scene scene)
        {
            int hidden = 0;
            foreach (var path in Hidden)
            {
                var go = GameObject.Find(path);
                if (go == null) continue;
                foreach (var r in go.GetComponentsInChildren<Renderer>(true)) { r.enabled = false; hidden++; }
            }
            foreach (var r in UnityEngine.Object.FindObjectsOfType<MeshRenderer>().Where(r => r.name == "BridgeTNG_BridgeChair")) { r.enabled = false; hidden++; }
            var set = UnityEngine.Object.Instantiate(template);
            set.name = "Bridge set " + name;
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(set, scene);
            set.SetActive(true);
            MoveViewscreen();
            MoveTactical();
            Plugin.Log.LogInfo($"Bridge set {name}: put into {scene.name}, {hidden} of the game's renderers hidden");
        }
    }
}
