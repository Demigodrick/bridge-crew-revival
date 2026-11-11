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

        private sealed class Loaded
        {
            public List<ObjModel.Part> Parts;
            public Dictionary<string, ObjModel.Material> Materials;
            public Dictionary<string, byte[]> Images = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            public Vector3 Captain = new Vector3(0f, 0.30f, -0.75f);
            public Vector3? Screen;     // the set's viewscreen: the middle of its back plane (model file)
            public float ScreenHeight;  // and its height
            public Vector3? Tactical, TacticalNormal, TacticalStand; // its tactical console's screen (middle, facing) and where its officer stands
            public string Error;
        }

        private static string name;
        private static Thread loading;
        private static volatile Loaded loaded;
        private static GameObject template;
        private static Vector3 placedCaptain; // the set's captain's place in its model file (the last one built)
        private static Vector3? screen; private static float screenHeight;
        private static Vector3? tactical, tacticalNormal, tacticalStand;
        private static int dressedScene = -1;
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
                        if (p.Length >= 4 && p[0] == "tacticalstand")
                        {
                            var inv = System.Globalization.CultureInfo.InvariantCulture;
                            result.TacticalStand = new Vector3(float.Parse(p[1], inv), float.Parse(p[2], inv), float.Parse(p[3], inv));
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
            tactical = data.Tactical; tacticalNormal = data.TacticalNormal; tacticalStand = data.TacticalStand;
            Func<Vector3, Vector3> place = v => new Vector3(v.x - c.x, v.y - c.y, -(v.z - c.z)) + GameCaptain;
            int pieces = 0, tris = 0;
            for (int n = 0; n < data.Parts.Count; n++)
            {
                var part = data.Parts[n];
                var material = MaterialFor(part.Texture);
                if (material == null) continue;
                var t = new int[part.Triangles.Length];
                for (int i = 0; i + 2 < t.Length; i += 3) { t[i] = part.Triangles[i]; t[i + 1] = part.Triangles[i + 2]; t[i + 2] = part.Triangles[i + 1]; }
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
                var back = new Mesh { name = mesh.name + " back", vertices = mesh.vertices, normals = mesh.normals.Select(x => -x).ToArray(), triangles = part.Triangles };
                if (mesh.uv.Length == mesh.vertexCount) back.uv = mesh.uv;
                back.RecalculateBounds();
                var goBack = new GameObject(go.name + "_back");
                goBack.transform.SetParent(root.transform, false);
                goBack.AddComponent<MeshFilter>().sharedMesh = back;
                goBack.AddComponent<MeshRenderer>().sharedMaterial = material;
                pieces++; tris += t.Length / 3;
            }
            Plugin.Log.LogInfo($"Bridge set {name}: {pieces} pieces, {tris} triangles, {materials.Count} materials, {textures.Count} textures, built in {(Time.realtimeSinceStartup - started) * 1000f:0} ms");
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

            var n = PlaceDirection(tacticalNormal.Value);           // the screen's facing: up and toward its officer
            var centre = Place(tactical.Value) + n * 0.012f;
            var view = Vector3.ProjectOnPlane(-n, Vector3.up).normalized; // the officer looks this way
            var t = root.transform;
            t.position = Place(tacticalStand.Value);
            t.rotation = Quaternion.LookRotation(view, Vector3.up);

            // The panel: the biggest drawn rectangle under it is its face.
            var face = ui.GetComponentsInChildren<RectTransform>(true)
                .OrderByDescending(r => r.rect.width * r.lossyScale.x * r.rect.height * r.lossyScale.y).FirstOrDefault();
            if (face == null) { Plugin.Log.LogWarning("Bridge set: the tactical panel has no face"); return; }
            var up = Vector3.ProjectOnPlane(Vector3.up, n).normalized;  // the screen's top: away from the officer, up the slope
            var turn = Quaternion.LookRotation(-n, up) * Quaternion.Inverse(Quaternion.LookRotation(face.forward, face.up));
            ui.rotation = turn * ui.rotation;
            var faceCentre = face.TransformPoint(face.rect.center);
            ui.position += centre - faceCentre;
            Plugin.Log.LogInfo($"Bridge set {name}: tactical station at {t.position}, its panel ('{face.name}', {face.rect.width * face.lossyScale.x:0.00} x {face.rect.height * face.lossyScale.y:0.00} m) on the console at {centre}");
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
