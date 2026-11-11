using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Sector;
using Sector.Spacecraft;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// [Ships] PlayerShip = Nova or Sovereign (beta): the crew flies a ship class of its own, built from a model on this PC in
    /// BepInEx/ships/&lt;Name&gt; (none of it ships with the mod): a Wavefront &lt;Name&gt;.obj with its .mtl and textures (.png
    /// or .jpg beside it). The ship and its bridge stay the game's own: the Aegis, or the Enterprise-D with the Next
    /// Generation chosen.
    ///
    /// A player ship is two prefabs: the ship (Ship - Aegis: its systems, console interfaces, crew, network) and its
    /// body (Body - Aegis: model, hull collider, weapon hardpoints, damage spots by side, cameras, warp effects), which
    /// the ship makes from its _body field when it wakes. Each ship class here gets a body of its own, built once from
    /// the game ship's: that model goes, the class's meshes take its place (about the Aegis's length, as the game sizes
    /// all its player ships), the hull collider is its shape, the torpedo tubes and phaser turrets sit on its launchers
    /// and phaser strips (places given relative to its size), the damage spots and the bridge's view point move onto
    /// its hull. The ship takes that body instead of its own as it wakes (only the player ship, not the original
    /// Enterprise). Its systems stay the game ship's.
    ///
    /// The model is read on a background thread as soon as the room flies the class (in the briefing room); the body is
    /// made on the main thread from it. The host's choice goes on the room ("SHP" = the class's name) so the whole crew
    /// flies the same ship; a crewmate without the model files sees the game's own.
    /// </summary>
    internal sealed class CustomShip
    {
        // ---- the classes ----

        private static readonly CustomShip[] ships = { Nova(), Sovereign() };

        /// <summary>Every ship class there is (whether its model is on this PC or not), in the ship box's order.</summary>
        internal static IList<CustomShip> All => ships;

        /// <summary>The class the room flies (or, out of a room, your own default), or null for the game's own ship.</summary>
        internal static CustomShip Flown => ships.FirstOrDefault(s => s.Active);

        internal static CustomShip Find(string name) => ships.FirstOrDefault(s => string.Equals(s.Name, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

        private static CustomShip Nova()
        {
            // Places as fractions of its half size (x starboard, y up, z forward): its two forward launchers, its phaser strips.
            Vector3 tubeL = new Vector3(-0.2605f, 0f, 0.914f), tubeR = new Vector3(0.2605f, 0f, 0.914f);
            return new CustomShip("Nova", "NOVA", 37.6f, new Dictionary<string, Vector3>
            {
                ["Tube 1"] = tubeL, ["Tube 2"] = Vector3.Lerp(tubeL, tubeR, 0.25f), ["Tube 3"] = Vector3.Lerp(tubeL, tubeR, 0.5f), ["Tube 4"] = Vector3.Lerp(tubeL, tubeR, 0.75f), ["Tube 5"] = tubeR,
                ["Top Front Turret"] = new Vector3(0f, 0.2927f, 0.3801f),
                ["Top Left Turret"] = new Vector3(-0.6107f, 0.0488f, 0.1312f),
                ["Top Right Turret"] = new Vector3(0.6107f, 0.0488f, 0.1312f),
                ["Bottom Front Turret"] = new Vector3(0f, -0.2049f, 0.3801f),
                ["Bottom Left Turret"] = new Vector3(-0.6107f, -0.2024f, 0.1403f),
                ["Bottom Right Turret"] = new Vector3(0.6287f, -0.2024f, 0.1403f),
            })
            {
                // Its Bussard collectors and navigation lights share the hull's texture: picked by where they're mapped.
                takePlasma = (part, mesh, scale) => ShipGlow.TakePlasma(mesh, 15f * scale), // out on the nacelles (the hull has a few red lights too)
                takeLight = (part, mesh, port) => ShipGlow.TakeLight(mesh, port),
            };
        }

        private static CustomShip Sovereign()
        {
            return new CustomShip("Sovereign", "SOVEREIGN", 44f, new Dictionary<string, Vector3>
            {
                // The saucer's dorsal launcher and the one under the saucer's neck.
                ["Tube 1"] = new Vector3(-0.025f, 0.70f, 0.30f), ["Tube 2"] = new Vector3(0f, 0.70f, 0.31f), ["Tube 3"] = new Vector3(0.025f, 0.70f, 0.30f),
                ["Tube 4"] = new Vector3(-0.03f, -0.70f, 0.24f), ["Tube 5"] = new Vector3(0.03f, -0.70f, 0.24f),
                // The saucer's phaser strips, top and bottom.
                ["Top Front Turret"] = new Vector3(0f, 0.30f, 0.80f),
                ["Top Left Turret"] = new Vector3(-0.70f, 0.32f, 0.50f),
                ["Top Right Turret"] = new Vector3(0.70f, 0.32f, 0.50f),
                ["Bottom Front Turret"] = new Vector3(0f, 0.06f, 0.80f),
                ["Bottom Left Turret"] = new Vector3(-0.75f, 0.07f, 0.50f),
                ["Bottom Right Turret"] = new Vector3(0.75f, 0.07f, 0.50f),
            })
            {
                // Its Bussard collectors and navigation lights are pieces of their own (materials of their own).
                takePlasma = (part, mesh, scale) => part.Name == "bussard" ? ShipGlow.TakeAllAsPlasma(mesh) : null,
                takeLight = (part, mesh, port) => part.Name == (port ? "LIGHT_red" : "LIGHT_green") ? ShipGlow.TakeAll(mesh) : null,
            };
        }

        // ---- one class ----

        internal readonly string Name;   // the room's "SHP", the folder's and the model file's name
        internal readonly string Label;  // in the briefing room's ship box
        private readonly float length;   // the game's units, bow to stern (the Aegis is about 42)
        private readonly Dictionary<string, Vector3> weapons; // fractions of its half size (x starboard, y up, z forward)
        private Func<ObjModel.Part, Mesh, float, Mesh> takePlasma;  // its Bussard collectors' faces, taken out of a piece; or null
        private Func<ObjModel.Part, Mesh, bool, Mesh> takeLight;    // its port (true) or starboard navigation lights' faces; or null

        private CustomShip(string name, string label, float length, Dictionary<string, Vector3> weapons)
        {
            Name = name; Label = label; this.length = length; this.weapons = weapons;
        }

        private const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;
        private const BindingFlags All_ = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static readonly FieldInfo BodyField = AccessTools.Field(typeof(BaseSpacecraft), "_body");
        private static GameObject holder;          // inactive: the templates under it never wake
        private SpacecraftBody aegisTemplate, tngTemplate; // the class's body on the Aegis's ship, and on the Enterprise-D's
        private readonly Dictionary<string, UnityEngine.Material> materials = new Dictionary<string, UnityEngine.Material>(StringComparer.OrdinalIgnoreCase); // both bodies share them

        /// <summary>The class's bodies built so far (on the Aegis's ship and on the Enterprise-D's).</summary>
        internal SpacecraftBody[] Templates => new[] { aegisTemplate, tngTemplate }.Where(b => b != null).ToArray();

        private Func<Vector3, Vector3> spaceOrient;
        private Vector3 spaceCentre;
        private float spaceScale;

        /// <summary>A point of the model file (its raw coordinates) in the class's model space (in its body, forward z, up y); null before it's built.</summary>
        internal Vector3? FileToModel(Vector3 raw) => spaceOrient == null ? (Vector3?)null : (spaceOrient(raw) - spaceCentre) * spaceScale;

        /// <summary>A direction of the model file in the class's model space.</summary>
        internal Vector3 FileDirectionToModel(Vector3 raw) => spaceOrient == null ? raw : spaceOrient(raw).normalized;

        /// <summary>Model file units to the class's (its scale).</summary>
        internal float FileScale => spaceScale;

        /// <summary>The class's model pieces (in its body, forward z and up y), once built; empty before.</summary>
        internal MeshFilter[] ModelParts
        {
            get
            {
                var body = tngTemplate ?? aegisTemplate;
                if (body == null) return new MeshFilter[0];
                // Its hull pieces ("Name_…") and whatever else was put on it since (its name decals, "Name Name Decal").
                return body.GetComponentsInChildren<MeshFilter>(true).Where(f => f.name.StartsWith(Name, StringComparison.Ordinal) && f.sharedMesh != null).ToArray();
            }
        }

        private sealed class Loaded
        {
            public List<ObjModel.Part> Parts;
            public Dictionary<string, ObjModel.Material> Materials;
            public Dictionary<string, byte[]> ImageFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);      // .png / .jpg
            public string Error;
            public float Seconds;
        }

        private Thread loading;
        private volatile Loaded loaded;
        private bool started, built;

        internal string Folder => Path.Combine(BepInEx.Paths.BepInExRootPath, Path.Combine("ships", Name));
        private string ObjFile => Path.Combine(Folder, Name + ".obj");

        internal static void Patch(Harmony harmony)
        {
            var awake = AccessTools.Method(typeof(BaseSpacecraft), "Awake");
            if (awake == null || BodyField == null) { Plugin.Log.LogWarning("Ships: the spacecraft's body wasn't found; no ship classes of our own"); return; }
            harmony.Patch(awake, prefix: new HarmonyMethod(typeof(CustomShip), nameof(Awake_Prefix)));
        }

        // ---- the crew's choice ----

        private static object Room => Patches.PhotonNetworkType?.GetMethod("get_room", Static)?.Invoke(null, null);

        private static string RoomShip(object room) => (room.GetType().GetMethod("get_customProperties", All_)?.Invoke(room, null) as IDictionary)?[Excelsior.Key] as string;

        /// <summary>The room this game is in flies this class (or, out of a room, your own choice is this class).</summary>
        internal bool Active
        {
            get
            {
                var room = Room;
                if (room == null) return Chosen;
                return RoomShip(room) == Name;
            }
        }

        /// <summary>The ship box's choice: a class of ours, or null for the game's own ship, for the room (or your default out of one).</summary>
        internal static void Choose(CustomShip ship)
        {
            var room = Room;
            if (room == null) { Plugin.PlayerShip.Value = ship?.Name ?? "Aegis"; return; }
            if (PhotonProps.Set(room, Excelsior.Key, ship?.Name ?? ""))
                Plugin.Log.LogInfo($"Ships: the crew flies {(ship != null ? "the " + ship.Name : "the game's own ship")}");
        }

        private bool Chosen => string.Equals((Plugin.PlayerShip.Value ?? "").Trim(), Name, StringComparison.OrdinalIgnoreCase);

        /// <summary>The class's model is on this PC.</summary>
        internal bool Available => File.Exists(ObjFile);

        internal static void Update()
        {
            ShipGlow.Animate();
            var room = Room;
            if (room != null && Patches.PhotonNetworkType.GetMethod("get_isMasterClient", Static)?.Invoke(null, null) is bool master && master)
            {
                // A new room starts with the host's default; after that the briefing room's ship box decides.
                var props = room.GetType().GetMethod("get_customProperties", All_)?.Invoke(room, null) as IDictionary;
                var chosen = ships.FirstOrDefault(s => s.Chosen);
                if (props != null && !props.Contains(Excelsior.Key) && chosen != null && PhotonProps.Set(room, Excelsior.Key, chosen.Name))
                    Plugin.Log.LogInfo($"Ships: the crew flies the {chosen.Name} (the host's default)");
            }
            foreach (var ship in ships) ship.Tick();
        }

        private void Tick()
        {
            // The model is read ahead of time (in the briefing room), not while the mission loads.
            if (!started && Active && Available)
            {
                started = true;
                loading = new Thread(Load) { IsBackground = true, Name = Name + " model" };
                loading.Start();
            }
            if (!built && loaded != null)
            {
                built = true;
                if (loaded.Error != null) Plugin.Log.LogWarning($"{Name}: reading the model: " + loaded.Error);
                else
                {
                    aegisTemplate = Build(loaded, "Sector/Ships/Ship - Aegis", "Aegis");
                    tngTemplate = Build(loaded, "Sector/Ships/Ship - Enterprise 1701D", "Enterprise-D");
                }
                loaded = null;
            }
        }

        // ---- the ship takes the class's body ----

        private static void Awake_Prefix(BaseSpacecraft __instance)
        {
            try
            {
                if (!(__instance is PlayerSpacecraft) || Loader.JCEFNOJHCKO) return; // not on the original Enterprise
                var ship = Flown;
                if (ship == null) return;
                if (ship.aegisTemplate == null && ship.tngTemplate == null && ship.loading != null && ship.loading.IsAlive)
                {
                    Plugin.Log.LogInfo($"{ship.Name}: the model is still being read; waiting for it");
                    ship.loading.Join(30000);
                    ship.Tick();
                }
                var template = Loader.IOMGCNMDDCK ? ship.tngTemplate : ship.aegisTemplate;
                if (template == null) { Plugin.Log.LogWarning($"{ship.Name}: no body (see above); the ship keeps its own"); return; }
                BodyField.SetValue(__instance, template);
                Plugin.Log.LogInfo($"{ship.Name}: {__instance.name} takes the {ship.Name}'s body");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Ships: giving the ship its body: " + e); }
        }

        // ---- reading (background thread) ----

        private void Load()
        {
            var result = new Loaded();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                result.Parts = ObjModel.Read(ObjFile, out var mats);
                result.Materials = mats;
                foreach (var m in mats.Values)
                    foreach (var file in new[] { m.Diffuse, m.Emission, m.Normal, m.Gloss })
                        if (file != null) LoadImage(result, file);
                if (result.Parts.Count == 0) result.Error = "no meshes in the model";
            }
            catch (Exception e) { result.Error = e.ToString(); }
            result.Seconds = (float)watch.Elapsed.TotalSeconds;
            loaded = result;
        }

        /// <summary>A texture beside the model: the file named, or one of the same name as .png or .jpg.</summary>
        private void LoadImage(Loaded result, string name)
        {
            if (result.ImageFiles.ContainsKey(name)) return;
            string stem = Path.GetFileNameWithoutExtension(name);
            foreach (var candidate in new[] { name, stem + ".png", stem + ".jpg", stem + ".jpeg" })
            {
                var file = Path.Combine(Folder, candidate);
                if (!File.Exists(file)) continue;
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext == ".png" || ext == ".jpg" || ext == ".jpeg") { result.ImageFiles[name] = File.ReadAllBytes(file); return; }
            }
        }

        // ---- the body (main thread) ----

        private static void AddPiece(Transform model, string name, Mesh mesh, UnityEngine.Material material, bool shadows, List<MeshRenderer> renderers)
        {
            var go = new GameObject(name);
            go.layer = model.gameObject.layer;
            go.transform.SetParent(model, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            if (!shadows) renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderers.Add(renderer);
        }

        /// <summary>The class's body made from a player ship's own (its cameras, warp effects, damage spots, hardpoints), or null.</summary>
        private SpacecraftBody Build(Loaded data, string shipPrefabPath, string label)
        {
            var startedAt = Time.realtimeSinceStartup;
            try
            {
                var shipPrefab = Resources.Load<GameObject>(shipPrefabPath);
                var gameBody = shipPrefab != null ? BodyField.GetValue(shipPrefab.GetComponent<BaseSpacecraft>()) as SpacecraftBody : null;
                if (gameBody == null) { Plugin.Log.LogWarning($"{Name}: the {label}'s body wasn't found"); return null; }

                if (holder == null)
                {
                    holder = new GameObject("Revival ship templates");
                    holder.SetActive(false);
                    UnityEngine.Object.DontDestroyOnLoad(holder);
                }
                var body = (GameObject)UnityEngine.Object.Instantiate(gameBody.gameObject, holder.transform, false);
                body.name = "Body - " + Name + " (" + label + ")";

                // The game ship's model goes (its hull collider stays, to take this class's shape).
                var model = body.transform.Cast<Transform>().FirstOrDefault(t => t.name.StartsWith("Mdl_", StringComparison.Ordinal));
                if (model == null) { Plugin.Log.LogWarning($"{Name}: the {label}'s body has no model"); UnityEngine.Object.Destroy(body); return null; }
                var collider = model.GetComponentInChildren<MeshCollider>(true);
                var hullMaterial = model.GetComponentsInChildren<MeshRenderer>(true).Select(r => r.sharedMaterial).FirstOrDefault(m => m != null && m.name.StartsWith("Aegis_Body", StringComparison.Ordinal))
                                   ?? model.GetComponentsInChildren<MeshRenderer>(true).Select(r => r.sharedMaterial).FirstOrDefault(m => m != null);
                Bounds(model, out var gameMin, out var gameMax);
                // The hull collider (the ship's size comes from it) straight under the model first: on the Enterprise-D it sits under its
                // mesh, which goes.
                if (collider != null) collider.transform.SetParent(model, false);
                foreach (Transform child in model.Cast<Transform>().ToList())
                    if (collider == null || child != collider.transform) UnityEngine.Object.DestroyImmediate(child.gameObject);
                // The model's own turn and size go (the Aegis's is turned a quarter about the vertical, the Enterprise-D's scaled):
                // the class is placed and sized here in the body's own axes.
                model.localRotation = Quaternion.identity;
                model.localScale = Vector3.one;
                var occlusion = body.transform.Find("ExternalViewCameraRig/ExteriorOcclusionProxy");
                if (occlusion != null) occlusion.gameObject.SetActive(false);

                // Into the body's space (x starboard, y up, z forward): both are right-handed, so one axis is mirrored and
                // the triangles turn. Then centred and sized to the game's length.
                Func<Vector3, Vector3> orient = v => new Vector3(-v.x, v.y, v.z);
                var oriented = data.Parts.Select(p => p.Vertices.Select(orient).ToArray()).ToList();
                Vector3 lo = Vector3.one * float.MaxValue, hi = Vector3.one * float.MinValue;
                foreach (var vs in oriented) foreach (var v in vs) { lo = Vector3.Min(lo, v); hi = Vector3.Max(hi, v); }
                float scale = length / Mathf.Max(1e-3f, hi.z - lo.z);
                var centre = (lo + hi) / 2f;
                spaceOrient = orient; spaceCentre = centre; spaceScale = scale;

                var renderers = new List<MeshRenderer>();
                var all = new List<Vector3>(); var allTris = new List<int>();
                for (int n = 0; n < data.Parts.Count; n++)
                {
                    var part = data.Parts[n];
                    var verts = oriented[n].Select(v => (v - centre) * scale).ToArray();
                    var material = MaterialFor(data, part.Texture, hullMaterial);
                    if (material == null) continue; // fully see-through in the model (its window glass): left out
                    var tris = new int[part.Triangles.Length];
                    for (int i = 0; i < tris.Length; i += 3) { tris[i] = part.Triangles[i]; tris[i + 1] = part.Triangles[i + 2]; tris[i + 2] = part.Triangles[i + 1]; }
                    var mesh = new Mesh { name = Name + " " + part.Name, vertices = verts, triangles = tris };
                    if (part.Uv != null && part.Uv.Length == verts.Length) mesh.uv = part.Uv;
                    if (part.Normals != null && part.Normals.Length == verts.Length && part.Normals.Any(x => x != Vector3.zero)) mesh.normals = part.Normals.Select(x => orient(x).normalized).ToArray();
                    else mesh.RecalculateNormals();
                    if (material.IsKeywordEnabled("_NORMALMAP")) Tangents(mesh);
                    mesh.RecalculateBounds();
                    if (all.Count + verts.Length <= 65000 * 4) { int offset = all.Count; all.AddRange(verts); allTris.AddRange(tris.Select(t => t + offset)); }

                    // Its Bussard collectors' faces (the red plasma) apart, alive; its navigation lights (red to port, green
                    // to starboard) apart, blinking together.
                    var plasma = takePlasma?.Invoke(part, mesh, scale);
                    if (plasma != null) AddPiece(model, Name + "_Bussard_" + n, plasma, ShipGlow.Material(material), true, renderers);
                    foreach (bool port in new[] { true, false })
                    {
                        var bulbs = takeLight?.Invoke(part, mesh, port);
                        if (bulbs != null) AddPiece(model, Name + (port ? "_PortLight_" : "_StarboardLight_") + n, bulbs, ShipGlow.LightMaterial(material, port), false, renderers);
                    }
                    if (mesh.triangles.Length > 0) AddPiece(model, Name + "_" + part.Name + "_" + n, mesh, material, true, renderers);
                }
                if (collider != null)
                {
                    collider.transform.localPosition = Vector3.zero; collider.transform.localRotation = Quaternion.identity; collider.transform.localScale = Vector3.one;
                    collider.sharedMesh = ColliderMesh(all, allTris);
                }
                var transparency = body.GetComponent<ShipTransparencyController>();
                if (transparency != null) transparency.ShipParts = renderers.ToArray();

                Vector3 half = (hi - lo) * scale / 2f, offsetInBody = model.localPosition;
                Vector3 shipMin = offsetInBody - half, shipMax = offsetInBody + half;

                PlaceWeapons(body.transform.Find("Weapons"), f => offsetInBody + Vector3.Scale(f, half));
                // Damage spots: from the game ship's hull onto this one, side by side.
                var damage = body.transform.Find("Damage FX");
                if (damage != null)
                    foreach (var loc in damage.GetComponentsInChildren<Transform>(true).Where(t => t.name == "Loc"))
                        loc.localPosition = Remap(loc.localPosition, gameMin, gameMax, shipMin, shipMax);
                // The bridge: the top of the hull on the centre line, in the forward half.
                Vector3 top = new Vector3(0f, float.MinValue, 0f);
                foreach (var v in all)
                    if (Mathf.Abs(v.x) < half.x * 0.06f && v.z > -half.z * 0.1f && v.z < half.z * 0.75f && v.y > top.y) top = v;
                if (top.y > float.MinValue)
                {
                    var nearTop = all.Where(v => Mathf.Abs(v.x) < half.x * 0.06f && v.z > -half.z * 0.1f && v.z < half.z * 0.75f && v.y > top.y - 0.15f).ToList();
                    top.z = nearTop.Average(v => v.z);
                    top.x = 0f; // on the centre line
                    var bridgeTop = offsetInBody + top;
                    // The viewscreen's camera and the outside view (space bar) look forward from just over it.
                    var view = body.transform.Find("SpaceViewReferenceTransform");
                    if (view != null) view.localPosition = bridgeTop + new Vector3(0f, 1.2f, 0f);
                    var rig = body.transform.Find("ExternalViewCameraRig");
                    if (rig != null) rig.localPosition = bridgeTop + new Vector3(0f, 1.0f, 0f);
                    Plugin.Log.LogInfo($"{Name}: bridge at {bridgeTop}");
                }

                var made = body.GetComponent<SpacecraftBody>();
                Plugin.Log.LogInfo($"{Name}: body ({label}) built from {Name}.obj: {data.Parts.Count} pieces, {data.Parts.Sum(p => p.Vertices.Length)} vertices, {data.Parts.Sum(p => p.Triangles.Length) / 3} triangles, {materials.Count} materials; read in {data.Seconds:0.0} s, built in {(Time.realtimeSinceStartup - startedAt) * 1000f:0} ms; size {half * 2f}");
                return made;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Name}: building its body: " + e);
                return null;
            }
        }

        /// <summary>The hull collider's shape: the model's triangles (at most 65000 vertices, welded to a coarse grid beyond that).</summary>
        private Mesh ColliderMesh(List<Vector3> verts, List<int> tris)
        {
            if (verts.Count <= 65000) return new Mesh { name = Name + " hull collider", vertices = verts.ToArray(), triangles = tris.ToArray() };
            // Too many for one mesh: weld to a coarse grid (the collider needs the shape, not the detail).
            var map = new Dictionary<long, int>(); var outV = new List<Vector3>(); var outT = new List<int>();
            const float cell = 0.25f;
            int Weld(Vector3 v)
            {
                long key = ((long)Mathf.RoundToInt(v.x / cell) & 0xFFFFF) << 40 | ((long)Mathf.RoundToInt(v.y / cell) & 0xFFFFF) << 20 | ((long)Mathf.RoundToInt(v.z / cell) & 0xFFFFF);
                if (map.TryGetValue(key, out var i)) return i;
                map[key] = outV.Count; outV.Add(v); return outV.Count - 1;
            }
            for (int i = 0; i + 2 < tris.Count && outV.Count < 64990; i += 3)
            {
                int a = Weld(verts[tris[i]]), b = Weld(verts[tris[i + 1]]), c = Weld(verts[tris[i + 2]]);
                if (a != b && b != c && a != c) { outT.Add(a); outT.Add(b); outT.Add(c); }
            }
            return new Mesh { name = Name + " hull collider", vertices = outV.ToArray(), triangles = outT.ToArray() };
        }

        private static Vector3 Remap(Vector3 p, Vector3 fromMin, Vector3 fromMax, Vector3 toMin, Vector3 toMax)
        {
            float F(float v, float a, float b, float c, float d) => b - a < 1e-3f ? (c + d) / 2f : c + (v - a) / (b - a) * (d - c);
            return new Vector3(F(p.x, fromMin.x, fromMax.x, toMin.x, toMax.x), F(p.y, fromMin.y, fromMax.y, toMin.y, toMax.y), F(p.z, fromMin.z, fromMax.z, toMin.z, toMax.z));
        }

        private static void Bounds(Transform model, out Vector3 min, out Vector3 max)
        {
            min = Vector3.one * float.MaxValue; max = Vector3.one * float.MinValue;
            foreach (var f in model.GetComponentsInChildren<MeshFilter>(true))
            {
                if (f.sharedMesh == null) continue;
                var b = f.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                    var p = model.parent.InverseTransformPoint(f.transform.TransformPoint(corner));
                    min = Vector3.Min(min, p); max = Vector3.Max(max, p);
                }
            }
        }

        /// <summary>The game ship's five torpedo tubes and six phaser turrets onto this class's launchers and phaser strips.</summary>
        private void PlaceWeapons(Transform weaponsRoot, Func<Vector3, Vector3> fromFraction)
        {
            if (weaponsRoot == null) return;
            var turrets = new List<Vector3>();
            foreach (var kv in weapons)
            {
                var t = weaponsRoot.Find(kv.Key);
                if (t == null) continue;
                t.localPosition = fromFraction(kv.Value);
                if (kv.Key.EndsWith("Turret")) turrets.Add(t.localPosition);
            }
            var centre = weaponsRoot.Find("Turret Center Reference");
            if (centre != null && turrets.Count > 0) centre.localPosition = turrets.Aggregate(Vector3.zero, (a, b) => a + b) / turrets.Count;
        }

        // ---- materials ----

        /// <summary>
        /// Tangents for a normal-mapped piece (this Unity can't work them out itself): each corner's direction of growing u,
        /// from its triangles, square to its normal; w says which way v grows.
        /// </summary>
        private static void Tangents(Mesh mesh)
        {
            var v = mesh.vertices; var uv = mesh.uv; var n = mesh.normals; var t = mesh.triangles;
            if (uv == null || uv.Length != v.Length || n == null || n.Length != v.Length) return;
            var tan1 = new Vector3[v.Length]; var tan2 = new Vector3[v.Length];
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                int a = t[i], b = t[i + 1], c = t[i + 2];
                Vector3 e1 = v[b] - v[a], e2 = v[c] - v[a];
                float s1 = uv[b].x - uv[a].x, s2 = uv[c].x - uv[a].x, t1 = uv[b].y - uv[a].y, t2 = uv[c].y - uv[a].y;
                float d = s1 * t2 - s2 * t1;
                if (Mathf.Abs(d) < 1e-12f) continue;
                float r = 1f / d;
                var sdir = (e1 * t2 - e2 * t1) * r; var tdir = (e2 * s1 - e1 * s2) * r;
                tan1[a] += sdir; tan1[b] += sdir; tan1[c] += sdir;
                tan2[a] += tdir; tan2[b] += tdir; tan2[c] += tdir;
            }
            var tangents = new Vector4[v.Length];
            for (int i = 0; i < v.Length; i++)
            {
                var tg = tan1[i] - n[i] * Vector3.Dot(n[i], tan1[i]);
                if (tg.sqrMagnitude < 1e-12f) tg = Vector3.Cross(n[i], Mathf.Abs(n[i].y) < 0.9f ? Vector3.up : Vector3.right);
                tg.Normalize();
                tangents[i] = new Vector4(tg.x, tg.y, tg.z, Vector3.Dot(Vector3.Cross(n[i], tg), tan2[i]) < 0f ? -1f : 1f);
            }
            mesh.tangents = tangents;
        }

        /// <summary>A texture beside the model, compressed; linear for data (normal and gloss maps), not colours.</summary>
        private static Texture2D MakeTexture(Loaded data, string name, bool linear = false)
        {
            if (name == null) return null;
            Texture2D tex = null;
            if (data.ImageFiles.TryGetValue(name, out var bytes))
            {
                tex = linear ? new Texture2D(2, 2, TextureFormat.ARGB32, true, true) { name = name } : new Texture2D(2, 2, TextureFormat.RGB24, true) { name = name };
                if (!tex.LoadImage(bytes)) { UnityEngine.Object.Destroy(tex); return null; }
            }
            if (tex == null) return null;
            tex.anisoLevel = 4;
            tex.Compress(true);
            tex.Apply(false, true);
            return tex;
        }

        /// <summary>
        /// A glow map from a light mask (white where it glows) and the colour texture: the colours where the mask is lit,
        /// black elsewhere (the mask alone would glow white).
        /// </summary>
        private static Texture2D Glow(Loaded data, string diffuse, string mask)
        {
            if (diffuse == null || !data.ImageFiles.TryGetValue(mask, out var maskBytes) || !data.ImageFiles.TryGetValue(diffuse, out var colourBytes)) return null;
            var m = new Texture2D(2, 2, TextureFormat.RGB24, false); var c = new Texture2D(2, 2, TextureFormat.RGB24, false);
            try
            {
                if (!m.LoadImage(maskBytes) || !c.LoadImage(colourBytes)) return null;
                int w = m.width, h = m.height;
                var maskPx = m.GetPixels32();
                var glow = new Color32[w * h];
                bool any = false;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        var k = maskPx[y * w + x];
                        float lit = Mathf.Max(k.r, Mathf.Max(k.g, k.b)) / 255f;
                        if (lit <= 0.02f) { glow[y * w + x] = new Color32(0, 0, 0, 255); continue; }
                        var col = c.GetPixelBilinear((x + 0.5f) / w, (y + 0.5f) / h);
                        glow[y * w + x] = new Color(col.r * lit, col.g * lit, col.b * lit, 1f);
                        any = true;
                    }
                if (!any) return null;
                var tex = new Texture2D(w, h, TextureFormat.RGB24, true) { name = mask + " glow", anisoLevel = 4 };
                tex.SetPixels32(glow); tex.Apply(true); tex.Compress(true); tex.Apply(false, true);
                return tex;
            }
            finally { UnityEngine.Object.Destroy(m); UnityEngine.Object.Destroy(c); }
        }

        /// <summary>A material for a piece: the game hull's (its shader), with the piece's own textures and colours (one per material, for both bodies).</summary>
        private UnityEngine.Material MaterialFor(Loaded data, string key, UnityEngine.Material hull)
        {
            key = key ?? "";
            if (materials.TryGetValue(key, out var m)) return m;
            m = hull != null ? new UnityEngine.Material(hull) : new UnityEngine.Material(Shader.Find("Standard"));
            m.name = Name + " " + key;
            foreach (var keyword in new[] { "_NORMALMAP", "_METALLICGLOSSMAP", "_SPECGLOSSMAP", "_DETAIL_MULX2", "_PARALLAXMAP", "_EMISSION" }) m.DisableKeyword(keyword);
            foreach (var prop in new[] { "_MainTex", "_BumpMap", "_MetallicGlossMap", "_SpecGlossMap", "_DetailAlbedoMap", "_DetailNormalMap", "_OcclusionMap", "_ParallaxMap", "_EmissionMap" })
                if (m.HasProperty(prop)) m.SetTexture(prop, null);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0.35f);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.45f);
            if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", Color.black);

            Color tint = Color.white;
            data.Materials.TryGetValue(key, out var om);
            if (om != null && om.Opacity < 0.1f) { materials[key] = null; UnityEngine.Object.Destroy(m); return null; } // see-through glass: not drawn
            var albedo = MakeTexture(data, om?.Diffuse);
            var glow = om?.Emission == null ? null : om.Emission == om.Diffuse ? MakeTexture(data, om.Emission) : Glow(data, om.Diffuse, om.Emission);
            if (om != null && albedo == null) tint = om.Colour;
            if (m.HasProperty("_Color")) m.SetColor("_Color", tint);
            if (albedo != null) m.SetTexture("_MainTex", albedo);
            // The relief of its panels and how they shine.
            var normal = m.HasProperty("_BumpMap") ? MakeTexture(data, om?.Normal, true) : null;
            if (normal != null)
            {
                m.SetTexture("_BumpMap", normal);
                if (m.HasProperty("_BumpScale")) m.SetFloat("_BumpScale", 1f);
                m.EnableKeyword("_NORMALMAP");
            }
            var gloss = m.HasProperty("_MetallicGlossMap") ? MakeTexture(data, om?.Gloss, true) : null;
            if (gloss != null)
            {
                m.SetTexture("_MetallicGlossMap", gloss);
                if (m.HasProperty("_GlossMapScale")) m.SetFloat("_GlossMapScale", 1f);
                m.EnableKeyword("_METALLICGLOSSMAP");
            }
            if (materials.Count == 0)
                Plugin.Log.LogInfo($"{Name}: hull shader {m.shader?.name}; normal maps {(m.HasProperty("_BumpMap") ? "yes" : "no")}, gloss maps {(m.HasProperty("_MetallicGlossMap") ? "yes" : "no")}");
            if (glow != null)
            {
                m.SetTexture("_EmissionMap", glow);
                m.SetColor("_EmissionColor", Color.white);
                m.EnableKeyword("_EMISSION");
            }
            else if (om != null && om.Glow.maxColorComponent > 0.01f)
            {
                // A glow of its own colour (lamps, the deflector, the warp coils).
                m.SetColor("_EmissionColor", om.Glow);
                m.EnableKeyword("_EMISSION");
            }
            materials[key] = m;
            return m;
        }
    }
}
