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
    /// [Ships] PlayerShip = Nova: the crew flies a Nova-class ship of its own, built from a model on this PC in
    /// BepInEx/ships/Nova (comes with the mod, from assets/ships): a Wavefront Nova.obj with its .mtl and textures (.png or .jpg
    /// beside it). The ship and its bridge stay the game's own: the Aegis, or the Enterprise-D with the Next Generation chosen.
    ///
    /// A player ship is two prefabs: the ship (Ship - Aegis: its systems, console interfaces, crew, network) and its
    /// body (Body - Aegis: model, hull collider, weapon hardpoints, damage spots by side, cameras, warp effects), which
    /// the ship makes from its _body field when it wakes. The Nova gets a body of its own, built once from the Aegis's:
    /// the Aegis's model goes, the Nova's meshes take its place (about the Aegis's length, as the game sizes all its
    /// player ships), the hull collider is the Nova's shape, the torpedo tubes and phaser turrets sit on its launchers and
    /// phaser strips (places given relative to its size), the damage spots and the bridge's view point move
    /// onto the Nova's hull. The ship takes that body instead of the Aegis's as it wakes (only the player ship, only
    /// with the Aegis chosen, not the Enterprises). Its systems stay the Aegis's.
    ///
    /// The model is read on a background thread as soon as the room flies the Nova (in the briefing room); the body is
    /// made on the main thread from it. The host's choice goes on the room ("SHP" = Nova) so the whole crew flies the
    /// same ship; a crewmate without the model files sees the Aegis's body.
    /// </summary>
    internal static class NovaShip
    {
        internal const string Name = "Nova";
        private const float Length = 37.6f;   // the game's units, bow to stern (the Aegis is about 42)
        private const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        // The frame the weapon places below are given in (x starboard, y forward, z up): the Nova's half width, length and height in it.
        private static readonly Vector3 WeaponFrameHalfSize = new Vector3(0.5567f, 1.105f, 0.205f);

        private static readonly FieldInfo BodyField = AccessTools.Field(typeof(BaseSpacecraft), "_body");
        private static GameObject holder;          // inactive: the template below never wakes
        private static SpacecraftBody aegisTemplate, tngTemplate; // the Nova's body on the Aegis's ship, and on the Enterprise-D's

        /// <summary>The Nova's bodies built so far (on the Aegis's ship and on the Enterprise-D's).</summary>
        internal static SpacecraftBody[] Templates => new[] { aegisTemplate, tngTemplate }.Where(b => b != null).ToArray();

        private static Func<Vector3, Vector3> spaceOrient;
        private static Vector3 spaceCentre;
        private static float spaceScale;

        /// <summary>A point of the model file (its raw coordinates) in the Nova's model space (in its body, forward z, up y); null before it's built.</summary>
        internal static Vector3? FileToModel(Vector3 raw) => spaceOrient == null ? (Vector3?)null : (spaceOrient(raw) - spaceCentre) * spaceScale;

        /// <summary>A direction of the model file in the Nova's model space.</summary>
        internal static Vector3 FileDirectionToModel(Vector3 raw) => spaceOrient == null ? raw : spaceOrient(raw).normalized;

        /// <summary>Model file units to the Nova's (its scale).</summary>
        internal static float FileScale => spaceScale;

        /// <summary>The Nova's model pieces (in its body, forward z and up y), once built; empty before.</summary>
        internal static MeshFilter[] ModelParts
        {
            get
            {
                var body = tngTemplate ?? aegisTemplate;
                if (body == null) return new MeshFilter[0];
                // Its hull pieces ("Nova_…") and whatever else was put on it since (its name decal, "Nova Name Decal").
                return body.GetComponentsInChildren<MeshFilter>(true).Where(f => f.name.StartsWith("Nova", StringComparison.Ordinal) && f.sharedMesh != null).ToArray();
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

        private static Thread loading;
        private static volatile Loaded loaded;
        private static bool started, built;

        private static readonly FieldInfo TngBridgeField = AccessTools.Field(typeof(Loader), "_tngBridgeScene");
        private static readonly FieldInfo LoaderInstance = typeof(Loader).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).FirstOrDefault(f => f.FieldType == typeof(Loader));

        /// <summary>The Nova's bridge for now: the Enterprise-D's (the game's TNG bridge scene), or null if it isn't known.</summary>
        internal static string BridgeScene
        {
            get
            {
                try { return TngBridgeField?.GetValue(LoaderInstance?.GetValue(null)) as string ?? Loader.CDLHKADDPPK; }
                catch { return null; }
            }
        }

        internal static string Folder => Path.Combine(BepInEx.Paths.BepInExRootPath, Path.Combine("ships", "Nova"));
        private static string ObjFile => Path.Combine(Folder, "Nova.obj");

        internal static void Patch(Harmony harmony)
        {
            var awake = AccessTools.Method(typeof(BaseSpacecraft), "Awake");
            if (awake == null || BodyField == null) { Plugin.Log.LogWarning("Nova: the spacecraft's body wasn't found; no Nova"); return; }
            harmony.Patch(awake, prefix: new HarmonyMethod(typeof(NovaShip), nameof(Awake_Prefix)));
        }

        // ---- the crew's choice ----

        // Photon's room and its properties, looked up once (they're read often).
        private static MethodInfo getRoom, getMaster, getProps;
        private static object Room
        {
            get
            {
                if (getRoom == null) getRoom = Patches.PhotonNetworkType?.GetMethod("get_room", Static);
                return getRoom?.Invoke(null, null);
            }
        }
        private static bool IsMaster()
        {
            if (getMaster == null) getMaster = Patches.PhotonNetworkType?.GetMethod("get_isMasterClient", Static);
            return getMaster?.Invoke(null, null) is bool m && m;
        }
        private static IDictionary Props(object room)
        {
            if (getProps == null || getProps.DeclaringType != room.GetType() && !getProps.DeclaringType.IsAssignableFrom(room.GetType()))
                getProps = room.GetType().GetMethod("get_customProperties", All);
            return getProps?.Invoke(room, null) as IDictionary;
        }

        /// <summary>The room this game is in flies the Nova (or, out of a room, your own choice is the Nova). Read now.</summary>
        internal static bool Active
        {
            get
            {
                var room = Room;
                if (room == null) return Chosen;
                return Props(room)?[Excelsior.Key] as string == Name;
            }
        }

        /// <summary>The crew flies the Nova on this PC: chosen, not a campaign mission, and its model here.</summary>
        internal static bool InUse => ActiveNow && Available && !Campaign;

        /// <summary>Campaign missions are chosen: those are flown on the game's own ship, never the Nova.</summary>
        internal static bool Campaign
        {
            get { try { return Loader.GetMissionType() == Loader.DAPMGBKPDNM.Campaign; } catch { return false; } }
        }

        /// <summary><see cref="Active"/> as of the last check (twice a second): for what runs every frame.</summary>
        internal static bool ActiveNow { get; private set; }
        private static float nextCheck, holdUntil;
        private static bool wanted;

        /// <summary>The ship box's choice: the Nova (true) or the game's own ship, for the room (or your default out of one).</summary>
        internal static void SetForRoom(bool nova)
        {
            // Shown at once, and held for a few seconds while the room's property comes back from the server: the
            // ship box would otherwise untick NOVA straight after it's pressed, from the room as it was.
            ActiveNow = nova; wanted = nova; holdUntil = Time.unscaledTime + 4f;
            var room = Room;
            if (room == null) { Plugin.PlayerShip.Value = nova ? Name : "Aegis"; return; }
            if (PhotonProps.Set(room, Excelsior.Key, nova ? Name : ""))
                Plugin.Log.LogInfo($"Nova: the crew flies {(nova ? "the Nova" : "the game's own ship")}");
        }

        private static bool Chosen => string.Equals((Plugin.PlayerShip.Value ?? "").Trim(), Name, StringComparison.OrdinalIgnoreCase);

        /// <summary>The Nova's model is on this PC.</summary>
        internal static bool Available
        {
            get
            {
                // Looked up once a minute: it's asked for in every text the game looks up (the ship's name).
                if (Time.unscaledTime >= availableAt) { available = File.Exists(ObjFile); availableAt = Time.unscaledTime + 60f; }
                return available;
            }
        }
        private static bool available;
        private static float availableAt = -1f;

        internal static void Update()
        {
            NovaWarp.Animate();
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 0.5f;
            var room = Room;
            if (room != null && IsMaster())
            {
                // A new room starts with the host's default; after that the briefing room's ship box decides.
                var props = Props(room);
                if (props != null && !props.Contains(Excelsior.Key) && Chosen && PhotonProps.Set(room, Excelsior.Key, Name))
                    Plugin.Log.LogInfo("Nova: the crew flies the Nova (the host's default)");
            }
            bool now = Active;
            if (Time.unscaledTime >= holdUntil || now == wanted) { ActiveNow = now; holdUntil = 0f; }
            // The model is read ahead of time (in the briefing room), not while the mission loads.
            // Read and built as the briefing room opens (with the model here), so NOVA's hologram and ship are ready
            // when it's pressed rather than seconds after.
            if (!started && (ActiveNow || ShipChoice.InBriefing) && Available)
            {
                started = true;
                loading = new Thread(Load) { IsBackground = true, Name = "Nova model", Priority = System.Threading.ThreadPriority.BelowNormal };
                loading.Start();
            }
            // Then the bodies are built a piece per frame, so the briefing room doesn't stop for it.
            if (!built && loaded != null)
            {
                built = true;
                var data = loaded; loaded = null;
                if (data.Error != null) Plugin.Log.LogWarning("Nova: reading the model: " + data.Error);
                else Plugin.Instance.StartCoroutine(BuildAll(data));
            }
        }

        /// <summary>Bumped as a body is built or decorated: what shows the Nova's pieces (the hologram) looks again.</summary>
        internal static int Version { get; private set; }
        internal static void Changed() => Version++;

        private static IEnumerator BuildAll(Loaded data)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            // The textures first, one per frame, made once for both bodies.
            foreach (var m in data.Materials.Values)
            {
                foreach (var file in new[] { m.Diffuse, m.Emission })
                    if (file != null && !textures.ContainsKey(file)) { textures[file] = MakeTexture(data, file); yield return null; }
                if (m.Emission != null && m.Diffuse != null && m.Emission != m.Diffuse)
                {
                    string key = m.Emission + " glow";
                    if (!textures.ContainsKey(key)) { textures[key] = Glow(data, m.Diffuse, m.Emission); yield return null; }
                }
            }
            SpacecraftBody body = null;
            yield return Guard(Build(data, "Sector/Ships/Ship - Aegis", "Aegis", b => body = b), "building its body (Aegis)");
            aegisTemplate = body; body = null;
            yield return Guard(Build(data, "Sector/Ships/Ship - Enterprise 1701D", "Enterprise-D", b => body = b), "building its body (Enterprise-D)");
            tngTemplate = body;
            data.ImageFiles.Clear();
            Changed();
            Plugin.Log.LogInfo($"Nova: built over {watch.ElapsedMilliseconds} ms, a piece per frame");
        }

        /// <summary>A coroutine that stops (and says why) at an exception rather than breaking the caller.</summary>
        private static IEnumerator Guard(IEnumerator inner, string what)
        {
            while (true)
            {
                object current;
                try
                {
                    if (!inner.MoveNext()) yield break;
                    current = inner.Current;
                }
                catch (Exception e) { Plugin.Log.LogWarning($"Nova: {what}: {e}"); yield break; }
                yield return current;
            }
        }

        // ---- the ship takes the Nova's body ----

        private static void Awake_Prefix(BaseSpacecraft __instance)
        {
            try
            {
                if (!(__instance is PlayerSpacecraft) || !Active || Loader.JCEFNOJHCKO) return; // not on the original Enterprise
                if (Campaign) { Plugin.Log.LogInfo("Nova: a campaign mission; the ship keeps its own body"); return; }
                var template = Loader.IOMGCNMDDCK ? tngTemplate : aegisTemplate;
                // Never waited for here (that would hold up the mission's loading, a frozen headset in VR): a Nova
                // that isn't ready yet means the game's own ship this time.
                if (template == null)
                {
                    Plugin.Log.LogWarning(started && !built || loaded != null ? "Nova: the model isn't ready yet; the ship keeps its own body this time"
                        : "Nova: no Nova body (see above); the ship keeps its own");
                    return;
                }
                BodyField.SetValue(__instance, template);
                Plugin.Log.LogInfo($"Nova: {__instance.name} takes the Nova's body");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Nova: giving the ship its body: " + e); }
        }

        // ---- reading (background thread) ----

        private static void Load()
        {
            var result = new Loaded();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                result.Parts = ObjModel.Read(ObjFile, out var materials);
                result.Materials = materials;
                foreach (var m in materials.Values)
                    foreach (var file in new[] { m.Diffuse, m.Emission })
                        if (file != null) LoadImage(result, file);
                if (result.Parts.Count == 0) result.Error = "no meshes in the model";
            }
            catch (Exception e) { result.Error = e.ToString(); }
            result.Seconds = (float)watch.Elapsed.TotalSeconds;
            loaded = result;
        }

        /// <summary>A texture beside the model: the file named, or one of the same name as .png or .jpg.</summary>
        private static void LoadImage(Loaded result, string name)
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

        /// <summary>The Nova's body made from a player ship's own (its cameras, warp effects, damage spots, hardpoints), or null.</summary>
        private static IEnumerator Build(Loaded data, string shipPrefabPath, string label, Action<SpacecraftBody> done)
        {
            var started = Time.realtimeSinceStartup;
            {
                var shipPrefab = Resources.Load<GameObject>(shipPrefabPath);
                var aegisBody = shipPrefab != null ? BodyField.GetValue(shipPrefab.GetComponent<BaseSpacecraft>()) as SpacecraftBody : null;
                if (aegisBody == null) { Plugin.Log.LogWarning($"Nova: the {label}'s body wasn't found"); yield break; }

                if (holder == null)
                {
                    holder = new GameObject("Revival ship templates");
                    holder.SetActive(false);
                    UnityEngine.Object.DontDestroyOnLoad(holder);
                }
                var body = (GameObject)UnityEngine.Object.Instantiate(aegisBody.gameObject, holder.transform, false);
                body.name = "Body - Nova (" + label + ")";

                // The Aegis's model goes (its hull collider stays, to take the Nova's shape).
                var model = body.transform.Cast<Transform>().FirstOrDefault(t => t.name.StartsWith("Mdl_", StringComparison.Ordinal));
                if (model == null) { Plugin.Log.LogWarning($"Nova: the {label}'s body has no model"); UnityEngine.Object.Destroy(body); yield break; }
                var collider = model.GetComponentInChildren<MeshCollider>(true);
                var hullMaterial = model.GetComponentsInChildren<MeshRenderer>(true).Select(r => r.sharedMaterial).FirstOrDefault(m => m != null && m.name.StartsWith("Aegis_Body", StringComparison.Ordinal))
                                   ?? model.GetComponentsInChildren<MeshRenderer>(true).Select(r => r.sharedMaterial).FirstOrDefault(m => m != null);
                Bounds(model, out var aegisMin, out var aegisMax);
                // The hull collider (the ship's size comes from it) straight under the model first: on the Enterprise-D it sits under its
                // mesh, which goes.
                if (collider != null) collider.transform.SetParent(model, false);
                foreach (Transform child in model.Cast<Transform>().ToList())
                    if (collider == null || child != collider.transform) UnityEngine.Object.DestroyImmediate(child.gameObject);
                // The model's own turn and size go (the Aegis's is turned a quarter about the vertical, the Enterprise-D's scaled):
                // the Nova is placed and sized here in the body's own axes.
                model.localRotation = Quaternion.identity;
                model.localScale = Vector3.one;
                var occlusion = body.transform.Find("ExternalViewCameraRig/ExteriorOcclusionProxy");
                if (occlusion != null) occlusion.gameObject.SetActive(false);

                // Into the body's space (x starboard, y up, z forward): both sources are right-handed, so one axis is
                // mirrored and the triangles turn. Then centred and sized to the game's length.
                Func<Vector3, Vector3> orient = v => new Vector3(-v.x, v.y, v.z);
                var oriented = data.Parts.Select(p => p.Vertices.Select(orient).ToArray()).ToList();
                Vector3 lo = Vector3.one * float.MaxValue, hi = Vector3.one * float.MinValue;
                foreach (var vs in oriented) foreach (var v in vs) { lo = Vector3.Min(lo, v); hi = Vector3.Max(hi, v); }
                float scale = Length / Mathf.Max(1e-3f, hi.z - lo.z);
                var centre = (lo + hi) / 2f;
                spaceOrient = orient; spaceCentre = centre; spaceScale = scale;

                var renderers = new List<MeshRenderer>();
                var materials = new Dictionary<string, UnityEngine.Material>(StringComparer.OrdinalIgnoreCase);
                var all = new List<Vector3>(); var allTris = new List<int>();
                for (int n = 0; n < data.Parts.Count; n++)
                {
                    var part = data.Parts[n];
                    var material = MaterialFor(data, part.Texture, hullMaterial, materials);
                    if (material == null) continue; // fully see-through in the model (its window glass): left out
                    // The meshes are made once and shared by both bodies (they're the same shape in the same space).
                    if (!meshes.TryGetValue(n, out var shared))
                    {
                        var verts = oriented[n].Select(v => (v - centre) * scale).ToArray();
                        var tris = new int[part.Triangles.Length];
                        for (int i = 0; i < tris.Length; i += 3) { tris[i] = part.Triangles[i]; tris[i + 1] = part.Triangles[i + 2]; tris[i + 2] = part.Triangles[i + 1]; }
                        var m = new Mesh { name = "Nova " + part.Name, vertices = verts, triangles = tris };
                        if (part.Uv != null && part.Uv.Length == verts.Length) m.uv = part.Uv;
                        if (part.Normals != null && part.Normals.Length == verts.Length && part.Normals.Any(x => x != Vector3.zero)) m.normals = part.Normals.Select(x => orient(x).normalized).ToArray();
                        else m.RecalculateNormals();
                        m.RecalculateBounds();
                        UnityEngine.Object.DontDestroyOnLoad(m);
                        shared = new PartMeshes { Hull = m, Verts = verts, Tris = tris };
                        // Its Bussard collectors' faces (the red plasma) apart, alive, and its navigation lights (red to
                        // port, green to starboard) blinking together: taken out of the hull piece.
                        shared.Plasma = NovaWarp.TakePlasma(m, 15f * scale); // out on the nacelles (the hull has a few red lights too)
                        shared.Port = NovaWarp.TakeLight(m, true);
                        shared.Starboard = NovaWarp.TakeLight(m, false);
                        meshes[n] = shared;
                    }
                    var mesh = shared.Hull; var tris0 = shared.Tris; var verts0 = shared.Verts;
                    var go = new GameObject("Nova_" + part.Name + "_" + n);
                    go.layer = model.gameObject.layer;
                    go.transform.SetParent(model, false);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var renderer = go.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial = material;
                    renderers.Add(renderer);
                    var plasma = shared.Plasma;
                    foreach (bool port in new[] { true, false })
                    {
                        var bulbs = port ? shared.Port : shared.Starboard;
                        if (bulbs == null) continue;
                        var light = new GameObject((port ? "Nova_PortLight_" : "Nova_StarboardLight_") + n);
                        light.layer = model.gameObject.layer;
                        light.transform.SetParent(model, false);
                        light.AddComponent<MeshFilter>().sharedMesh = bulbs;
                        var lightRenderer = light.AddComponent<MeshRenderer>();
                        lightRenderer.sharedMaterial = NovaWarp.LightMaterial(material, port);
                        lightRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                        renderers.Add(lightRenderer);
                    }
                    if (plasma != null)
                    {
                        var glow = new GameObject("Nova_Bussard_" + n);
                        glow.layer = model.gameObject.layer;
                        glow.transform.SetParent(model, false);
                        glow.AddComponent<MeshFilter>().sharedMesh = plasma;
                        var glowRenderer = glow.AddComponent<MeshRenderer>();
                        glowRenderer.sharedMaterial = NovaWarp.Material(material);
                        renderers.Add(glowRenderer);
                    }
                    if (all.Count + verts0.Length <= 65000 * 4) { int offset = all.Count; all.AddRange(verts0); allTris.AddRange(tris0.Select(t => t + offset)); }
                    yield return null; // a piece per frame
                }
                yield return null;
                if (collider != null)
                {
                    collider.transform.localPosition = Vector3.zero; collider.transform.localRotation = Quaternion.identity; collider.transform.localScale = Vector3.one;
                    collider.sharedMesh = ColliderMesh(all, allTris);
                    var rb = body.GetComponentInParent<Rigidbody>();
                    Plugin.Log.LogInfo($"Nova: hull collider ({label}): convex {collider.convex}, rigidbody {(rb == null ? "none" : rb.isKinematic ? "kinematic" : "dynamic")}");
                }
                var transparency = body.GetComponent<ShipTransparencyController>();
                if (transparency != null) transparency.ShipParts = renderers.ToArray();

                Vector3 half = (hi - lo) * scale / 2f, offsetInBody = model.localPosition;
                Vector3 novaMin = offsetInBody - half, novaMax = offsetInBody + half;
                Func<Vector3, Vector3> fromFrame = h => offsetInBody + new Vector3(h.x / WeaponFrameHalfSize.x * half.x, h.z / WeaponFrameHalfSize.z * half.y, h.y / WeaponFrameHalfSize.y * half.z);

                PlaceWeapons(body.transform.Find("Weapons"), fromFrame);
                // Damage spots: from the Aegis's hull onto the Nova's, side by side.
                var damage = body.transform.Find("Damage FX");
                if (damage != null)
                    foreach (var loc in damage.GetComponentsInChildren<Transform>(true).Where(t => t.name == "Loc"))
                        loc.localPosition = Remap(loc.localPosition, aegisMin, aegisMax, novaMin, novaMax);
                // The bridge: the top of the hull on the centre line, in the forward half (the Nova's dome sits there).
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
                    Plugin.Log.LogInfo($"Nova: bridge at {bridgeTop}");
                }

                var made = body.GetComponent<SpacecraftBody>();
                Plugin.Log.LogInfo($"Nova: body ({label}) built from Nova.obj: {data.Parts.Count} pieces, {data.Parts.Sum(p => p.Vertices.Length)} vertices, {data.Parts.Sum(p => p.Triangles.Length) / 3} triangles, {materials.Count} materials; read in {data.Seconds:0.0} s, built over {(Time.realtimeSinceStartup - started) * 1000f:0} ms; size {half * 2f}");
                done(made);
            }
        }

        /// <summary>The hull collider's shape: the model's triangles (at most 65000 vertices, every other piece dropped beyond that).</summary>
        private static Mesh ColliderMesh(List<Vector3> verts, List<int> tris)
        {
            // Always welded to a coarse grid: the collider needs the shape, not the detail, and a light mesh cooks quickly
            // (on the main thread) and costs less in every physics query.
            var map = new Dictionary<long, int>(); var outV = new List<Vector3>(); var outT = new List<int>();
            const float cell = 0.3f;
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
            Plugin.Log.LogInfo($"Nova: hull collider {outV.Count} vertices, {outT.Count / 3} triangles (from {verts.Count} / {tris.Count / 3})");
            return new Mesh { name = "Nova hull collider", vertices = outV.ToArray(), triangles = outT.ToArray() };
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

        /// <summary>
        /// The Aegis's five torpedo tubes and six phaser turrets onto the Nova's: the tubes spread over its two forward
        /// launchers, the turrets on its forward and aft phaser strips (top and bottom, port and starboard).
        /// </summary>
        private static void PlaceWeapons(Transform weapons, Func<Vector3, Vector3> fromFrame)
        {
            if (weapons == null) return;
            Vector3 Mid(Vector3 a, Vector3 b) => (a + b) / 2f;
            // In the weapon frame (WeaponFrameHalfSize): the two forward launchers, the phaser strips' ends.
            var tubeL = new Vector3(-0.145f, 1.01f, 0f); var tubeR = new Vector3(0.145f, 1.01f, 0f);
            var places = new Dictionary<string, Vector3>
            {
                ["Tube 1"] = tubeL, ["Tube 2"] = Vector3.Lerp(tubeL, tubeR, 0.25f), ["Tube 3"] = Mid(tubeL, tubeR), ["Tube 4"] = Vector3.Lerp(tubeL, tubeR, 0.75f), ["Tube 5"] = tubeR,
                ["Top Front Turret"] = new Vector3(0f, 0.42f, 0.06f),
                ["Top Left Turret"] = new Vector3(-0.34f, 0.145f, 0.01f),
                ["Top Right Turret"] = new Vector3(0.34f, 0.145f, 0.01f),
                ["Bottom Front Turret"] = new Vector3(0f, 0.42f, -0.042f),
                ["Bottom Left Turret"] = new Vector3(-0.34f, 0.155f, -0.0415f),
                ["Bottom Right Turret"] = new Vector3(0.35f, 0.155f, -0.0415f),
            };
            var turrets = new List<Vector3>();
            foreach (var kv in places)
            {
                var t = weapons.Find(kv.Key);
                if (t == null) continue;
                t.localPosition = fromFrame(kv.Value);
                if (kv.Key.EndsWith("Turret")) turrets.Add(t.localPosition);
            }
            var centre = weapons.Find("Turret Center Reference");
            if (centre != null && turrets.Count > 0) centre.localPosition = turrets.Aggregate(Vector3.zero, (a, b) => a + b) / turrets.Count;
        }

        private sealed class PartMeshes { public Mesh Hull, Plasma, Port, Starboard; public Vector3[] Verts; public int[] Tris; }
        private static readonly Dictionary<int, PartMeshes> meshes = new Dictionary<int, PartMeshes>();

        // ---- materials ----

        // The model's textures, made once (a piece per frame) and shared by both bodies: by file name, and "<mask> glow".
        private static readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

        private static Texture2D MakeTexture(Loaded data, string name)
        {
            if (name == null) return null;
            Texture2D tex = null;
            if (data.ImageFiles.TryGetValue(name, out var bytes))
            {
                tex = new Texture2D(2, 2, TextureFormat.RGB24, true) { name = name };
                if (!tex.LoadImage(bytes)) { UnityEngine.Object.Destroy(tex); return null; }
            }
            if (tex == null) return null;
            tex.anisoLevel = 4;
            tex.Compress(false); // the fast compressor: this runs in the briefing room
            tex.Apply(false, true);
            UnityEngine.Object.DontDestroyOnLoad(tex);
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
                int w = m.width, h = m.height, cw = c.width, ch = c.height;
                var maskPx = m.GetPixels32();
                var colPx = c.GetPixels32(); // read once: sampled here, not by an engine call per pixel
                var glow = new Color32[w * h];
                bool any = false;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        var k = maskPx[y * w + x];
                        float lit = Mathf.Max(k.r, Mathf.Max(k.g, k.b)) / 255f;
                        if (lit <= 0.02f) { glow[y * w + x] = new Color32(0, 0, 0, 255); continue; }
                        // Bilinear, the colour texture at this pixel's place.
                        float fx = (x + 0.5f) / w * cw - 0.5f, fy = (y + 0.5f) / h * ch - 0.5f;
                        int x0 = Mathf.Clamp((int)Mathf.Floor(fx), 0, cw - 1), y0 = Mathf.Clamp((int)Mathf.Floor(fy), 0, ch - 1);
                        int x1 = Math.Min(x0 + 1, cw - 1), y1 = Math.Min(y0 + 1, ch - 1);
                        float tx = Mathf.Clamp01(fx - x0), ty = Mathf.Clamp01(fy - y0);
                        var col = Color32.Lerp(Color32.Lerp(colPx[y0 * cw + x0], colPx[y0 * cw + x1], tx), Color32.Lerp(colPx[y1 * cw + x0], colPx[y1 * cw + x1], tx), ty);
                        glow[y * w + x] = new Color32((byte)(col.r * lit), (byte)(col.g * lit), (byte)(col.b * lit), 255);
                        any = true;
                    }
                if (!any) return null;
                var tex = new Texture2D(w, h, TextureFormat.RGB24, true) { name = mask + " glow", anisoLevel = 4 };
                tex.SetPixels32(glow); tex.Apply(true); tex.Compress(false); tex.Apply(false, true);
                UnityEngine.Object.DontDestroyOnLoad(tex);
                return tex;
            }
            finally { UnityEngine.Object.Destroy(m); UnityEngine.Object.Destroy(c); }
        }

        private static Texture2D Made(string key) => key != null && textures.TryGetValue(key, out var t) ? t : null;

        /// <summary>A material for a piece: the Aegis hull's (its shader), with the piece's own textures and colour.</summary>
        private static UnityEngine.Material MaterialFor(Loaded data, string key, UnityEngine.Material hull, Dictionary<string, UnityEngine.Material> made)
        {
            key = key ?? "";
            if (made.TryGetValue(key, out var m)) return m;
            m = hull != null ? new UnityEngine.Material(hull) : new UnityEngine.Material(Shader.Find("Standard"));
            m.name = "Nova " + key;
            foreach (var keyword in new[] { "_NORMALMAP", "_METALLICGLOSSMAP", "_SPECGLOSSMAP", "_DETAIL_MULX2", "_PARALLAXMAP", "_EMISSION" }) m.DisableKeyword(keyword);
            foreach (var prop in new[] { "_MainTex", "_BumpMap", "_MetallicGlossMap", "_SpecGlossMap", "_DetailAlbedoMap", "_DetailNormalMap", "_OcclusionMap", "_ParallaxMap", "_EmissionMap" })
                if (m.HasProperty(prop)) m.SetTexture(prop, null);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0.35f);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.45f);
            if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", Color.black);

            Texture2D albedo, glow;
            Color tint = Color.white;
            data.Materials.TryGetValue(key, out var om);
            albedo = Made(om?.Diffuse);
            glow = om?.Emission == null ? null : om.Emission == om.Diffuse ? Made(om.Emission) : Made(om.Emission + " glow");
            if (om != null && albedo == null) tint = om.Colour;
            if (om != null && om.Opacity < 0.1f) { made[key] = null; UnityEngine.Object.Destroy(m); return null; } // see-through glass: not drawn
            if (m.HasProperty("_Color")) m.SetColor("_Color", tint);
            if (albedo != null) m.SetTexture("_MainTex", albedo);
            if (glow != null)
            {
                m.SetTexture("_EmissionMap", glow);
                m.SetColor("_EmissionColor", Color.white);
                m.EnableKeyword("_EMISSION");
            }
            made[key] = m;
            return m;
        }
    }
}
