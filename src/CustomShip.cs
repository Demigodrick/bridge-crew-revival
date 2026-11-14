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
    /// [Ships] PlayerShip = Nova, Sovereign or AbramsEnterprise (beta): the crew flies a ship class of its own, built from a model in
    /// BepInEx/ships/&lt;Name&gt; (comes with the mod, from assets/ships): a Wavefront &lt;Name&gt;.obj with its .mtl and
    /// textures (.png or .jpg beside it). The ship and its bridge stay the game's Aegis for now (its bridge is a copy of
    /// the Aegis's, with a layout of its own: BridgeLayout).
    ///
    /// A player ship is two prefabs: the ship (Ship - Aegis: its systems, console interfaces, crew, network) and its
    /// body (Body - Aegis: model, hull collider, weapon hardpoints, damage spots by side, cameras, warp effects), which
    /// the ship makes from its _body field when it wakes. Each class gets a body of its own, built once from the game
    /// ship's: that model goes, the class's meshes take its place (about the Aegis's length, as the game sizes all its
    /// player ships), the hull collider is its shape, the torpedo tubes and phaser turrets sit on its launchers and phaser
    /// strips (places given as fractions of its half size), the damage spots and the bridge's view point move onto its
    /// hull. The ship takes that body as it wakes (only the player ship, not the original Enterprise or a campaign
    /// mission). Its systems stay the game ship's.
    ///
    /// The model is read on a thread as the briefing room opens, and the bodies are built a piece per frame, textures
    /// and meshes once for both; the mission's loading never waits for them (not ready: the game's own ship that time).
    /// The host's choice goes on the room ("SHP" = the class's name) so the whole crew flies the same ship; a crewmate
    /// without the model files sees the game's own.
    /// </summary>
    internal sealed class CustomShip
    {
        // ---- the classes ----

        private static readonly CustomShip[] ships = { Nova(), Sovereign(), AbramsEnterprise() };

        /// <summary>Every ship class there is (whether its model is on this PC or not), in the ship box's order.</summary>
        internal static IList<CustomShip> All => ships;

        internal static CustomShip Find(string name) => ships.FirstOrDefault(s => string.Equals(s.Name, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

        private static CustomShip Nova()
        {
            // Places as fractions of its half size (x starboard, y up, z forward): its two forward launchers, its phaser strips.
            Vector3 tubeL = new Vector3(-0.2605f, 0f, 0.914f), tubeR = new Vector3(0.2605f, 0f, 0.914f);
            return new CustomShip("Nova", "NOVA", 37.6f, "USS Nova", "NX-73515", new Dictionary<string, Vector3>
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
            return new CustomShip("Sovereign", "SOVEREIGN", 44f, "USS Sovereign", "NX-73811", new Dictionary<string, Vector3>
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

        private static CustomShip AbramsEnterprise()
        {
            // Places as fractions of its half size (x starboard, y up, z forward): the two launcher groups under the
            // saucer, and the six phaser banks.
            var weapons = new Dictionary<string, Vector3>
            {
                ["Tube 1"] = new Vector3(-0.011f, -0.250f, 0.301f),
                ["Tube 2"] = new Vector3(0f, -0.250f, 0.301f),
                ["Tube 3"] = new Vector3(0.011f, -0.250f, 0.301f),
                ["Tube 4"] = new Vector3(-0.010f, -0.810f, 0.094f),
                ["Tube 5"] = new Vector3(0.010f, -0.810f, 0.094f),
                ["Top Front Turret"] = new Vector3(0f, 0.435f, 0.745f),
                ["Top Left Turret"] = new Vector3(-0.453f, 0.435f, 0.539f),
                ["Top Right Turret"] = new Vector3(0.453f, 0.435f, 0.539f),
                ["Bottom Front Turret"] = new Vector3(0f, 0.110f, 0.707f),
                ["Bottom Left Turret"] = new Vector3(-0.370f, 0.110f, 0.539f),
                ["Bottom Right Turret"] = new Vector3(0.370f, 0.110f, 0.539f),
            };
            return new CustomShip("AbramsEnterprise", "ENTERPRISE (KELVIN)", 42f, "USS Enterprise", "NCC-1701", weapons)
            {
                // Its nacelles' fronts are the game ship's own Bussard collectors: the glass drum with the blades that
                // turn inside it, their materials and animation, taken whole from the Aegis. The model's own front
                // pieces there go (the grey fan, the clamp and the dome), which would hide or cut through the drum.
                originalBussards = true,
                hidePart = part => part.Name == "m038_FanGrey" || part.Name == "m039_DomeClamp" || part.Name == "m060_NacelleDomes",
                // The nacelles' rear shells face inward: their outsides drawn too.
                twoSided = part => part.Name == "m063_NacRear",
                windowPart = "m023_BridgeWindow",
                // The saucer's name and registry and the nacelles' registry (HullKelvin).
                lettered = new[] { "tex_41_SaucerTop_jpg.png", "tex_29_NacelleOutside_jpg.png", "tex_30_NacelleOutsideLeft_jp.png" },
            };
        }

        // ---- one class ----

        internal readonly string Name;   // the room's "SHP", the folder's and the model file's name
        internal readonly string Label;  // in the briefing room's ship box
        internal readonly string DefaultName, DefaultRegistry; // its texts and hull with no ship name set
        private readonly float length;   // the game's units, bow to stern (the Aegis is about 42)
        private readonly Dictionary<string, Vector3> weapons; // fractions of its half size (x starboard, y up, z forward)
        private Func<ObjModel.Part, Mesh, float, Mesh> takePlasma;  // its Bussard collectors' faces, taken out of a piece; or null
        private Func<ObjModel.Part, Mesh, bool, Mesh> takeLight;    // its port (true) or starboard navigation lights' faces; or null
        private bool originalBussards;                     // its nacelles' fronts are the Aegis's own Bussard collectors (KeepBussards)
        private Func<ObjModel.Part, bool> hidePart;        // pieces left out when those are in place
        private Func<ObjModel.Part, bool> twoSided;        // pieces drawn from both sides (a shell that faces inward)
        private string windowPart;                         // the bridge window's piece: the view from the bridge is placed at it
        private string[] lettered;                         // textures the crew's name is painted into (HullName, on their DXT1 blocks)

        /// <summary>A lettered texture's DXT1 blocks (every mip), as made: what HullName paints the crew's name into.</summary>
        internal sealed class Blocks { public int W, H, MipCount; public byte[] Data; }
        private readonly Dictionary<string, Blocks> blocks = new Dictionary<string, Blocks>(StringComparer.OrdinalIgnoreCase);
        internal Blocks BlocksOf(string file) => blocks.TryGetValue(file, out var b) ? b : null;

        // Preserve the actual Aegis renderers, materials and Animator controllers. Containers
        // change placement without changing the animated blades' local transforms or bindings.
        private List<Transform> KeepBussards(Transform model)
        {
            var groups = new List<Transform>();
            if (!originalBussards) return groups;
            var candidates = model.Cast<Transform>().Where(t => t.name.StartsWith("Aegis_Collectors", StringComparison.Ordinal) || t.name.StartsWith("Aegis_Blade_", StringComparison.Ordinal)).ToArray();
            if (candidates.Length != 6)
            {
                Plugin.Log.LogWarning($"{Name}: Aegis collectors incomplete ({candidates.Length}/6); retaining Kelvin fronts");
                return groups;
            }
            for (int side = 0; side < 2; side++)
            {
                var container = new GameObject("Kelvin Aegis collector " + side).transform;
                container.SetParent(model.parent, false);
                var axes = new GameObject("Original Aegis axes").transform;
                axes.SetParent(container, false);
                axes.localRotation = model.localRotation;
                axes.localScale = model.localScale;
                foreach (var child in candidates.Where(t => t.name.EndsWith(" (1)", StringComparison.Ordinal) == (side == 1)))
                    child.SetParent(axes, false);
                groups.Add(container);
            }
            return groups;
        }

        private void FitBussards(List<Transform> groups, Transform model, Loaded data,
            Func<Vector3, Vector3> orient, Vector3 centre, float scale, List<MeshRenderer> renderers)
        {
            foreach (var group in groups)
            {
                group.SetParent(model, false);
                var filters = group.GetComponentsInChildren<MeshFilter>(true);
                var source = new UnityEngine.Bounds(); bool first = true;
                foreach (var filter in filters)
                    foreach (var vertex in filter.sharedMesh.vertices)
                    {
                        var p = group.InverseTransformPoint(filter.transform.TransformPoint(vertex));
                        if (first) { source = new UnityEngine.Bounds(p, Vector3.zero); first = false; }
                        else source.Encapsulate(p);
                    }
                // The original pair's side is determined in the body's axes, not by its asset suffix.
                float sign = Mathf.Sign(source.center.x);
                var target = new UnityEngine.Bounds(); first = true;
                foreach (var part in data.Parts.Where(p => p.Name == "m060_NacelleDomes"))
                    foreach (var vertex in part.Vertices)
                    {
                        var p = (orient(vertex) - centre) * scale;
                        if (Mathf.Sign(p.x) != sign) continue;
                        if (first) { target = new UnityEngine.Bounds(p, Vector3.zero); first = false; }
                        else target.Encapsulate(p);
                    }
                if (first) throw new InvalidOperationException("Kelvin collector dome bounds missing");
                var fit = new Vector3(target.size.x / Mathf.Max(.001f, source.size.x), target.size.y / Mathf.Max(.001f, source.size.y), target.size.z / Mathf.Max(.001f, source.size.z));
                group.localScale = fit;
                group.localPosition = target.center - Vector3.Scale(source.center, fit);
                foreach (var t in group.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = model.gameObject.layer;
                foreach (var animator in group.GetComponentsInChildren<Animator>(true)) animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                renderers.AddRange(group.GetComponentsInChildren<MeshRenderer>(true));
                Plugin.Log.LogInfo($"{Name}: original Aegis collector fitted at {target.center}, size {target.size}; {group.GetComponentsInChildren<Animator>(true).Length} original animators");
            }
        }

        private CustomShip(string name, string label, float length, string defaultName, string defaultRegistry, Dictionary<string, Vector3> weapons)
        {
            Name = name; Label = label; this.length = length; DefaultName = defaultName; DefaultRegistry = defaultRegistry; this.weapons = weapons;
        }

        private const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;
        private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static readonly FieldInfo BodyField = AccessTools.Field(typeof(BaseSpacecraft), "_body");
        private static GameObject holder;          // inactive: the templates under it never wake
        private SpacecraftBody aegisTemplate; // the class's body on the Aegis's ship

        /// <summary>The class's bodies built so far (on the Aegis's ship).</summary>
        internal SpacecraftBody[] Templates => aegisTemplate != null ? new SpacecraftBody[] { aegisTemplate } : new SpacecraftBody[0];

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
                var body = aegisTemplate;
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
            if (getProps == null || !getProps.DeclaringType.IsAssignableFrom(room.GetType()))
                getProps = room.GetType().GetMethod("get_customProperties", Instance);
            return getProps?.Invoke(room, null) as IDictionary;
        }

        /// <summary>The class the room flies (or, out of a room, your own default), read now; null for the game's own ship.</summary>
        internal static CustomShip Flown
        {
            get
            {
                var room = Room;
                if (room == null) return Chosen;
                return Find(Props(room)?[Excelsior.Key] as string);
            }
        }

        /// <summary><see cref="Flown"/> as of the last check (twice a second): for what runs every frame.</summary>
        internal static CustomShip FlownNow { get; private set; }

        /// <summary>The class flown on this PC: chosen, not a campaign mission, and its model here; or null.</summary>
        internal static CustomShip InUse => FlownNow != null && FlownNow.Available && !Campaign ? FlownNow : null;

        /// <summary>Campaign missions are chosen: those are flown on the game's own ship, never one of ours.</summary>
        internal static bool Campaign
        {
            get { try { return Loader.GetMissionType() == Loader.DAPMGBKPDNM.Campaign; } catch { return false; } }
        }

        private static float nextCheck, holdUntil;
        private static CustomShip wanted;

        /// <summary>The ship box's choice: a class of ours, or null for the game's own ship, for the room (or your default out of one).</summary>
        internal static void Choose(CustomShip ship)
        {
            // Shown at once, and held for a few seconds while the room's property comes back from the server: the
            // ship box would otherwise untick the row straight after it's pressed, from the room as it was.
            FlownNow = ship; wanted = ship; holdUntil = Time.unscaledTime + 4f;
            var room = Room;
            if (room == null) { Plugin.PlayerShip.Value = ship?.Name ?? "Aegis"; return; }
            if (PhotonProps.Set(room, Excelsior.Key, ship?.Name ?? ""))
                Plugin.Log.LogInfo($"Ships: the crew flies {(ship != null ? "the " + ship.Name : "the game's own ship")}");
        }

        private static CustomShip Chosen => Find(Plugin.PlayerShip.Value);

        /// <summary>The class's model is on this PC.</summary>
        internal bool Available
        {
            get
            {
                // Looked up once a minute: it's asked for in every text the game looks up (the ship's name).
                if (Time.unscaledTime >= availableAt) { available = File.Exists(ObjFile); availableAt = Time.unscaledTime + 60f; }
                return available;
            }
        }
        private bool available;
        private float availableAt = -1f;

        /// <summary>Some class's model is on this PC (the ship box adds rows only then).</summary>
        internal static bool AnyAvailable => ships.Any(s => s.Available);

        internal static void Update()
        {
            ShipGlow.Animate();
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 0.5f;
            var room = Room;
            if (room != null && IsMaster())
            {
                // A new room starts with the host's default; after that the briefing room's ship box decides.
                var props = Props(room);
                var chosen = Chosen;
                if (props != null && !props.Contains(Excelsior.Key) && chosen != null && PhotonProps.Set(room, Excelsior.Key, chosen.Name))
                    Plugin.Log.LogInfo($"Ships: the crew flies the {chosen.Name} (the host's default)");
            }
            var now = Flown;
            if (Time.unscaledTime >= holdUntil || now == wanted) { FlownNow = now; holdUntil = 0f; }
            foreach (var ship in ships) ship.Tick();
        }

        // ---- the hologram: a lighter copy for the briefing room's table ----
        // The full model is far too much for a table ornament in VR (the Nova 430,000 triangles, the Sovereign 545,000).
        // Each piece's vertices are merged on a grid (about 200 cells along the ship), on a thread, once.

        private List<KeyValuePair<Mesh, UnityEngine.Material>> hologram;
        private volatile List<KeyValuePair<KeyValuePair<Vector3[], KeyValuePair<Vector3[], Vector2[]>>, KeyValuePair<int[], UnityEngine.Material>>> hologramMade;
        private bool hologramStarted;

        /// <summary>The class's hologram pieces (mesh and material, in its model space), or null while it's being made.</summary>
        internal List<KeyValuePair<Mesh, UnityEngine.Material>> Hologram
        {
            get
            {
                if (hologram == null && !hologramStarted && built && !building && meshes.Count > 0) StartHologram();
                return hologram;
            }
        }

        private void StartHologram()
        {
            hologramStarted = true;
            var parts = meshes.Values.Where(p => p.Material != null).ToList();
            float cell = length / 200f;
            var thread = new Thread(() =>
            {
                var made = new List<KeyValuePair<KeyValuePair<Vector3[], KeyValuePair<Vector3[], Vector2[]>>, KeyValuePair<int[], UnityEngine.Material>>>();
                int before = 0, after = 0;
                try
                {
                    foreach (var p in parts)
                    {
                        var light = Simplify(p.Verts, p.Normals, p.Uv, p.Tris, cell, out var v, out var n, out var uv);
                        before += p.Tris.Length / 3; after += light.Length / 3;
                        if (light.Length > 0) made.Add(new KeyValuePair<KeyValuePair<Vector3[], KeyValuePair<Vector3[], Vector2[]>>, KeyValuePair<int[], UnityEngine.Material>>(
                            new KeyValuePair<Vector3[], KeyValuePair<Vector3[], Vector2[]>>(v, new KeyValuePair<Vector3[], Vector2[]>(n, uv)), new KeyValuePair<int[], UnityEngine.Material>(light, p.Material)));
                    }
                }
                catch { made.Clear(); }
                hologramCounts = $"{before} triangles to {after}";
                hologramMade = made;
            }) { IsBackground = true, Name = Name + " hologram", Priority = System.Threading.ThreadPriority.BelowNormal };
            thread.Start();
        }
        private string hologramCounts;

        private void FinishHologram()
        {
            var made = hologramMade;
            if (made == null || hologram != null) return;
            hologramMade = null;
            var pieces = new List<KeyValuePair<Mesh, UnityEngine.Material>>();
            foreach (var p in made)
            {
                var mesh = new Mesh { name = Name + " hologram", vertices = p.Key.Key, triangles = p.Value.Key };
                if (p.Key.Value.Key != null) mesh.normals = p.Key.Value.Key; else mesh.RecalculateNormals();
                if (p.Key.Value.Value != null) mesh.uv = p.Key.Value.Value;
                mesh.RecalculateBounds();
                UnityEngine.Object.DontDestroyOnLoad(mesh);
                pieces.Add(new KeyValuePair<Mesh, UnityEngine.Material>(mesh, p.Value.Value));
            }
            hologram = pieces;
            Plugin.Log.LogInfo($"{Name}: hologram made, {hologramCounts}");
        }

        /// <summary>Vertices merged on a grid (positions averaged; a corner's normal and mapping kept), collapsed triangles dropped.</summary>
        private static int[] Simplify(Vector3[] verts, Vector3[] normals, Vector2[] uv, int[] tris, float cell, out Vector3[] outV, out Vector3[] outN, out Vector2[] outUv)
        {
            var map = new Dictionary<long, int>();
            var remap = new int[verts.Length];
            var sum = new List<Vector3>(); var count = new List<int>(); var n = new List<Vector3>(); var u = new List<Vector2>();
            for (int i = 0; i < verts.Length; i++)
            {
                var p = verts[i];
                long key = ((long)Mathf.RoundToInt(p.x / cell) & 0xFFFFF) << 40 | ((long)Mathf.RoundToInt(p.y / cell) & 0xFFFFF) << 20 | ((long)Mathf.RoundToInt(p.z / cell) & 0xFFFFF);
                if (!map.TryGetValue(key, out var at))
                {
                    at = map[key] = sum.Count;
                    sum.Add(Vector3.zero); count.Add(0);
                    n.Add(normals != null ? normals[i] : Vector3.up);
                    u.Add(uv != null ? uv[i] : Vector2.zero);
                }
                sum[at] += p; count[at]++;
                remap[i] = at;
            }
            var t = new List<int>(tris.Length / 4);
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                int a = remap[tris[i]], b = remap[tris[i + 1]], c = remap[tris[i + 2]];
                if (a != b && b != c && a != c) { t.Add(a); t.Add(b); t.Add(c); }
            }
            outV = new Vector3[sum.Count];
            for (int i = 0; i < outV.Length; i++) outV[i] = sum[i] / count[i];
            outN = normals != null ? n.ToArray() : null;
            outUv = uv != null ? u.ToArray() : null;
            return t.ToArray();
        }

        private void Tick()
        {
            if (hologramMade != null) FinishHologram();
            // Read and built as the briefing room opens (with the model here), so the hologram and the ship are ready
            // when it's pressed rather than seconds after; one class at a time (each holds a lot while it's read).
            if (!started && (FlownNow == this || ShipChoice.InBriefing) && Available && !ships.Any(s => s != this && s.Busy))
            {
                started = true;
                loading = new Thread(Load) { IsBackground = true, Name = Name + " model", Priority = System.Threading.ThreadPriority.BelowNormal };
                loading.Start();
            }
            // Then the bodies are built a piece per frame, so the briefing room doesn't stop for it.
            if (!built && loaded != null)
            {
                built = true;
                var data = loaded; loaded = null;
                if (data.Error != null) { Plugin.Log.LogWarning($"{Name}: reading the model: " + data.Error); building = false; }
                else { building = true; Plugin.Instance.StartCoroutine(BuildAll(data)); }
            }
        }

        private bool building;
        private bool Busy => started && (!built || building);

        /// <summary>Bumped as a body is built or decorated: what shows the classes' pieces (the hologram) looks again.</summary>
        internal static int Version { get; private set; }
        internal static void Changed() => Version++;

        private IEnumerator BuildAll(Loaded data)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            // The textures first, one per frame, made once for both bodies.
            foreach (var m in data.Materials.Values)
            {
                foreach (var file in new[] { m.Diffuse, m.Emission })
                    if (file != null && !textures.ContainsKey(file)) { textures[file] = MakeTexture(data, file, false); yield return null; }
                foreach (var file in new[] { m.Normal, m.Gloss })
                    if (file != null && !textures.ContainsKey(file + " data")) { textures[file + " data"] = MakeTexture(data, file, true); yield return null; }
                if (m.Emission != null && m.Diffuse != null && m.Emission != m.Diffuse)
                {
                    string key = m.Emission + " glow";
                    if (!textures.ContainsKey(key)) { textures[key] = Glow(data, m.Diffuse, m.Emission); yield return null; }
                }
            }
            SpacecraftBody body = null;
            yield return Guard(Build(data, "Sector/Ships/Ship - Aegis", "Aegis", b => body = b), "building its body (Aegis)");
            aegisTemplate = body;
            data.ImageFiles.Clear();
            building = false;
            Changed();
            Plugin.Log.LogInfo($"{Name}: built over {watch.ElapsedMilliseconds} ms, a piece per frame");
        }

        /// <summary>A coroutine that stops (and says why) at an exception rather than breaking the caller.</summary>
        private IEnumerator Guard(IEnumerator inner, string what)
        {
            while (true)
            {
                object current;
                try
                {
                    if (!inner.MoveNext()) yield break;
                    current = inner.Current;
                }
                catch (Exception e) { Plugin.Log.LogWarning($"{Name}: {what}: {e}"); yield break; }
                yield return current;
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
                if (Campaign) { Plugin.Log.LogInfo($"{ship.Name}: a campaign mission; the ship keeps its own body"); return; }
                // Ours fly on the Aegis's ship and bridge only (the ship box sets the Aegis under them).
                if (Loader.IOMGCNMDDCK) { Plugin.Log.LogInfo($"{ship.Name}: the Next Generation's ship is chosen; it keeps its own body"); return; }
                var template = ship.aegisTemplate;
                // Never waited for here (that would hold up the mission's loading, a frozen headset in VR): a class that
                // isn't ready yet means the game's own ship this time.
                if (template == null)
                {
                    Plugin.Log.LogWarning(ship.Busy || ship.loaded != null ? $"{ship.Name}: the model isn't ready yet; the ship keeps its own body this time"
                        : $"{ship.Name}: no body (see above); the ship keeps its own");
                    return;
                }
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

        private sealed class PartMeshes { public Mesh Hull, Plasma, Port, Starboard, Back; public Vector3[] Verts, Normals; public Vector2[] Uv; public int[] Tris; public UnityEngine.Material Material; }

        /// <summary>The hull's shapes (each piece's vertices and triangles, in its model space), for cutting decals from; empty before it's built.</summary>
        internal List<KeyValuePair<Vector3[], int[]>> HullShapes => meshes.Values.Select(m => new KeyValuePair<Vector3[], int[]>(m.Verts, m.Tris)).ToList();
        private readonly Dictionary<int, PartMeshes> meshes = new Dictionary<int, PartMeshes>(); // made once, shared by both bodies

        /// <summary>The class's body made from a player ship's own (its cameras, warp effects, damage spots, hardpoints), a piece per frame.</summary>
        private IEnumerator Build(Loaded data, string shipPrefabPath, string label, Action<SpacecraftBody> done)
        {
            var startedAt = Time.realtimeSinceStartup;
            var shipPrefab = Resources.Load<GameObject>(shipPrefabPath);
            var gameBody = shipPrefab != null ? BodyField.GetValue(shipPrefab.GetComponent<BaseSpacecraft>()) as SpacecraftBody : null;
            if (gameBody == null) { Plugin.Log.LogWarning($"{Name}: the {label}'s body wasn't found"); yield break; }

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
            if (model == null) { Plugin.Log.LogWarning($"{Name}: the {label}'s body has no model"); UnityEngine.Object.Destroy(body); yield break; }
            var collider = model.GetComponentInChildren<MeshCollider>(true);
            var hullMaterial = model.GetComponentsInChildren<MeshRenderer>(true).Select(r => r.sharedMaterial).FirstOrDefault(m => m != null && m.name.StartsWith("Aegis_Body", StringComparison.Ordinal))
                               ?? model.GetComponentsInChildren<MeshRenderer>(true).Select(r => r.sharedMaterial).FirstOrDefault(m => m != null);
            Bounds(model, out var gameMin, out var gameMax);
            var bussards = KeepBussards(model);
            // The hull collider (the ship's size comes from it) straight under the model first: on the Enterprise-D it sits
            // under its mesh, which goes.
            if (collider != null) collider.transform.SetParent(model, false);
            foreach (Transform child in model.Cast<Transform>().ToList())
                if (collider == null || child != collider.transform) UnityEngine.Object.DestroyImmediate(child.gameObject);
            // The model's own turn and size go (the Aegis's is turned a quarter about the vertical, the Enterprise-D's
            // scaled): the class is placed and sized here in the body's own axes.
            model.localRotation = Quaternion.identity;
            model.localScale = Vector3.one;
            var occlusion = body.transform.Find("ExternalViewCameraRig/ExteriorOcclusionProxy");
            if (occlusion != null) occlusion.gameObject.SetActive(false);

            // Into the body's space (x starboard, y up, z forward): both are right-handed, so one axis is mirrored and
            // the triangles turn. Then centred and sized to the game's length.
            Func<Vector3, Vector3> orient = v => new Vector3(-v.x, v.y, v.z);
            Vector3 lo = Vector3.one * float.MaxValue, hi = Vector3.one * float.MinValue;
            foreach (var p in data.Parts) foreach (var v in p.Vertices) { var o = orient(v); lo = Vector3.Min(lo, o); hi = Vector3.Max(hi, o); }
            float scale = length / Mathf.Max(1e-3f, hi.z - lo.z);
            var centre = (lo + hi) / 2f;
            spaceOrient = orient; spaceCentre = centre; spaceScale = scale;

            var renderers = new List<MeshRenderer>();
            FitBussards(bussards, model, data, orient, centre, scale, renderers);
            var all = new List<Vector3>(); var allTris = new List<int>();
            for (int n = 0; n < data.Parts.Count; n++)
            {
                var part = data.Parts[n];
                if (bussards.Count > 0 && hidePart != null && hidePart(part)) continue;
                var material = MaterialFor(data, part.Texture, hullMaterial);
                if (material == null) continue; // fully see-through in the model (its window glass): left out
                if (!meshes.TryGetValue(n, out var shared))
                {
                    var verts = part.Vertices.Select(v => (orient(v) - centre) * scale).ToArray();
                    var tris = new int[part.Triangles.Length];
                    for (int i = 0; i < tris.Length; i += 3) { tris[i] = part.Triangles[i]; tris[i + 1] = part.Triangles[i + 2]; tris[i + 2] = part.Triangles[i + 1]; }
                    var m = new Mesh { name = Name + " " + part.Name, vertices = verts, triangles = tris };
                    if (part.Uv != null && part.Uv.Length == verts.Length) m.uv = part.Uv;
                    if (part.Normals != null && part.Normals.Length == verts.Length && part.Normals.Any(x => x != Vector3.zero)) m.normals = part.Normals.Select(x => orient(x).normalized).ToArray();
                    else m.RecalculateNormals();
                    var keptNormals = m.normals;
                    if (material.IsKeywordEnabled("_NORMALMAP")) Tangents(m);
                    m.RecalculateBounds();
                    UnityEngine.Object.DontDestroyOnLoad(m);
                    shared = new PartMeshes { Hull = m, Verts = verts, Tris = tris, Normals = keptNormals, Uv = part.Uv != null && part.Uv.Length == verts.Length ? part.Uv : null, Material = material };
                    // A shell that faces inward: its inside kept, and the opposite winding added so it shows from outside too.
                    if (twoSided != null && twoSided(part))
                    {
                        var backTris = (int[])tris.Clone();
                        for (int i = 0; i < backTris.Length; i += 3)
                        { int t = backTris[i + 1]; backTris[i + 1] = backTris[i + 2]; backTris[i + 2] = t; }
                        shared.Back = new Mesh { name = Name + " " + part.Name + " outside", vertices = verts,
                            triangles = backTris, normals = keptNormals.Select(v => -v).ToArray(), uv = m.uv };
                        if (material.IsKeywordEnabled("_NORMALMAP")) Tangents(shared.Back);
                        shared.Back.RecalculateBounds();
                        UnityEngine.Object.DontDestroyOnLoad(shared.Back);
                        Plugin.Log.LogInfo($"{Name}: {part.Name} drawn from both sides ({backTris.Length / 3} triangles)");
                    }
                    // Its Bussard collectors' faces (the red plasma) apart, alive, and its navigation lights (red to port,
                    // green to starboard) blinking together: taken out of the piece.
                    shared.Plasma = takePlasma?.Invoke(part, m, scale);
                    shared.Port = takeLight?.Invoke(part, m, true);
                    shared.Starboard = takeLight?.Invoke(part, m, false);
                    meshes[n] = shared;
                }
                if (shared.Plasma != null)
                {
                    Plugin.Log.LogInfo($"{Name}: bussard plasma from {part.Name} ({shared.Plasma.triangles.Length / 3} triangles)");
                    AddPiece(model, Name + "_Bussard_" + n, shared.Plasma, ShipGlow.Material(material), true, renderers);
                }
                if (shared.Port != null) AddPiece(model, Name + "_PortLight_" + n, shared.Port, ShipGlow.LightMaterial(material, true), false, renderers);
                if (shared.Starboard != null) AddPiece(model, Name + "_StarboardLight_" + n, shared.Starboard, ShipGlow.LightMaterial(material, false), false, renderers);
                if (shared.Hull.triangles.Length > 0) AddPiece(model, Name + "_" + part.Name + "_" + n, shared.Hull, material, true, renderers);
                if (shared.Back != null) AddPiece(model, Name + "_" + part.Name + "_Outside_" + n, shared.Back, material, true, renderers);
                if (all.Count + shared.Verts.Length <= 65000 * 4) { int offset = all.Count; all.AddRange(shared.Verts); allTris.AddRange(shared.Tris.Select(t => t + offset)); }
                yield return null; // a piece per frame
            }
            yield return null;
            if (collider != null)
            {
                collider.transform.localPosition = Vector3.zero; collider.transform.localRotation = Quaternion.identity; collider.transform.localScale = Vector3.one;
                collider.sharedMesh = ColliderMesh(all, allTris);
                var rb = body.GetComponentInParent<Rigidbody>();
                Plugin.Log.LogInfo($"{Name}: hull collider ({label}): convex {collider.convex}, rigidbody {(rb == null ? "none" : rb.isKinematic ? "kinematic" : "dynamic")}");
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

            // A model with a front window below the bridge roof (the Kelvin): the roof + 1 m placement above would put
            // the view about 15 m over it at the bridge's 10:1 scale. The eye goes to that window instead, keeping the
            // game's seated/walking parallax.
            if (windowPart != null)
            {
                var windowVertices = data.Parts.Where(p => p.Name == windowPart)
                    .SelectMany(p => p.Vertices).Select(v => (orient(v) - centre) * scale).ToArray();
                if (windowVertices.Length > 0)
                {
                    var windowMin = windowVertices.Aggregate(Vector3.Min);
                    var windowMax = windowVertices.Aggregate(Vector3.Max);
                    var eye = offsetInBody + (windowMin + windowMax) * .5f;
                    eye.x = 0f;
                    // The Aegis interior's seated/standing parallax puts the effective window
                    // below this reference. Clearance calibrated from the in-game window view:
                    // the literal model-window anchor clipped into the saucer while walking.
                    eye.y += .7f;
                    eye.z = offsetInBody.z + windowMax.z + .03f; // outside the glass
                    var rig = body.transform.Find("ExternalViewCameraRig");
                    var camera = rig != null ? rig.GetComponentInChildren<Camera>(true) : null;
                    // Pivot moves with the observer; remove only the camera's fixed eye-height
                    // offset, not the observer-dependent pivot translation.
                    var cameraOffset = camera != null ? camera.transform.localPosition : Vector3.zero;
                    var anchor = eye - cameraOffset;
                    if (rig != null)
                    {
                        rig.localPosition = anchor;
                        foreach (var c in rig.GetComponentsInChildren<Camera>(true)) c.nearClipPlane = .05f;
                    }
                    var view = body.transform.Find("SpaceViewReferenceTransform");
                    // Space-bar view has no seated/standing parallax: give it its own elevated
                    // reference. Keep the calibrated bridge/window rig untouched.
                    if (view != null) view.localPosition = anchor + Vector3.up * 1.0f;
                    Plugin.Log.LogInfo($"{Name}: bridge window eye {eye}, camera offset {cameraOffset}, rig anchor {anchor}, near clip 0.05");
                }
                else Plugin.Log.LogWarning($"{Name}: bridge window mesh missing; keeping generic viewpoint");
            }

            var made = body.GetComponent<SpacecraftBody>();
            Plugin.Log.LogInfo($"{Name}: body ({label}) built from {Name}.obj: {data.Parts.Count} pieces, {data.Parts.Sum(p => p.Vertices.Length)} vertices, {data.Parts.Sum(p => p.Triangles.Length) / 3} triangles, {materials.Count} materials; read in {data.Seconds:0.0} s, built over {(Time.realtimeSinceStartup - startedAt) * 1000f:0} ms; size {half * 2f}");
            done(made);
        }

        /// <summary>The hull collider's shape: the model's surface welded to a coarse grid, then its convex hull. Unity's
        /// convex collider takes at most 255 polygons: a detailed hull (the Abrams' 22,800 welded triangles) fails to
        /// cook, and physics then retries the failed cook every time something touches it (a ship under fire drops to
        /// about one frame a second). A hull of n points has at most 2n-4 faces, so a spread of about a hundred points
        /// stays well under the limit and keeps the shape.</summary>
        private Mesh ColliderMesh(List<Vector3> verts, List<int> tris)
        {
            // Always welded: the collider needs the shape, not the detail, and a light mesh cooks quickly (on the main
            // thread) and costs less in every physics query.
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

            // A hull this simple cooks without Unity's builder giving up (the Nova's and the Sovereign's do): kept as
            // it is, so their collision stays exactly what it was.
            if (outT.Count / 3 <= 18000)
            {
                Plugin.Log.LogInfo($"{Name}: hull collider {outV.Count} vertices, {outT.Count / 3} triangles (from {verts.Count} / {tris.Count / 3})");
                return new Mesh { name = Name + " hull collider", vertices = outV.ToArray(), triangles = outT.ToArray() };
            }

            // A detailed hull (the Abrams' 22,800 welded triangles): its convex hull has more polygons than Unity takes.
            var points = Spread(outV, 100);
            var faces = ConvexHull(points);
            if (faces != null && faces.Count >= 4 && faces.Count <= 255)
            {
                var hullVerts = new List<Vector3>(); var hullTris = new List<int>();
                int Index(Vector3 v)
                {
                    for (int i = 0; i < hullVerts.Count; i++) if ((hullVerts[i] - v).sqrMagnitude < 1e-8f) return i;
                    hullVerts.Add(v); return hullVerts.Count - 1;
                }
                foreach (var f in faces) { hullTris.Add(Index(points[f.A])); hullTris.Add(Index(points[f.B])); hullTris.Add(Index(points[f.C])); }
                Plugin.Log.LogInfo($"{Name}: hull collider: {hullVerts.Count} points, {faces.Count} faces (welded {outV.Count} vertices from {verts.Count} / {tris.Count / 3})");
                return new Mesh { name = Name + " hull collider", vertices = hullVerts.ToArray(), triangles = hullTris.ToArray() };
            }

            // A hull of the spread points can't go wrong in theory; if it did, a box still cooks and the ship collides.
            Plugin.Log.LogWarning($"{Name}: the hull collider's hull failed; using its bounding box");
            Vector3 lo = Vector3.one * float.MaxValue, hi = Vector3.one * float.MinValue;
            foreach (var v in outV) { lo = Vector3.Min(lo, v); hi = Vector3.Max(hi, v); }
            var boxV = new Vector3[8]; for (int i = 0; i < 8; i++) boxV[i] = new Vector3((i & 1) == 0 ? lo.x : hi.x, (i & 2) == 0 ? lo.y : hi.y, (i & 4) == 0 ? lo.z : hi.z);
            var boxT = new[] { 0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 4, 1, 5, 2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 5, 3, 7 };
            return new Mesh { name = Name + " hull collider", vertices = boxV, triangles = boxT };
        }

        /// <summary>At most <paramref name="max"/> of the points, spread over the outside of the shape: for each of a
        /// hundred directions all round, the point furthest that way. Points on the hull, and few enough that their
        /// hull stays under Unity's 255 polygons.</summary>
        private static List<Vector3> Spread(List<Vector3> points, int max)
        {
            if (points.Count <= max) return points;
            var centre = Vector3.zero; foreach (var p in points) centre += p; centre /= points.Count;
            int count = max - 6;
            float golden = Mathf.PI * (3f - Mathf.Sqrt(5f));
            var dirs = new List<Vector3>(max) { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
            for (int i = 0; i < count; i++)
            {
                float y = 1f - 2f * (i + 0.5f) / count;
                float r = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
                float theta = golden * i;
                dirs.Add(new Vector3(Mathf.Cos(theta) * r, y, Mathf.Sin(theta) * r));
            }
            var chosen = new List<Vector3>(max);
            foreach (var dir in dirs)
            {
                int best = 0; float bestDot = float.MinValue;
                for (int i = 0; i < points.Count; i++) { float dot = Vector3.Dot(points[i] - centre, dir); if (dot > bestDot) { bestDot = dot; best = i; } }
                bool have = false;
                foreach (var c in chosen) if ((c - points[best]).sqrMagnitude < 1e-6f) { have = true; break; }
                if (!have) chosen.Add(points[best]);
            }
            return chosen;
        }

        private struct HullFace { public int A, B, C; public Vector3 Normal; public float D; public bool Gone; }
        private struct HullEdge { public int A, B, Count; }

        /// <summary>The convex hull of up to a few hundred points (an incremental hull): its outward-facing triangles,
        /// or null if the points don't make a solid.</summary>
        private static List<HullFace> ConvexHull(List<Vector3> p)
        {
            int n = p.Count;
            if (n < 4) return null;
            float extent = 0f; for (int i = 0; i < n; i++) extent = Mathf.Max(extent, (p[i] - p[0]).magnitude);
            float eps = Mathf.Max(1e-4f, extent * 1e-4f);

            // A tetrahedron to start from: two ends, a third off their line, a fourth off their plane.
            int i0 = 0; for (int i = 1; i < n; i++) if (p[i].x < p[i0].x) i0 = i;
            int i1 = -1; float best = 0f;
            for (int i = 0; i < n; i++) { float d = (p[i] - p[i0]).sqrMagnitude; if (i != i0 && d > best) { best = d; i1 = i; } }
            if (i1 < 0 || best <= eps * eps) return null;
            var axis = (p[i1] - p[i0]).normalized;
            int i2 = -1; best = 0f;
            for (int i = 0; i < n; i++) { var v = p[i] - p[i0]; var d = v - Vector3.Dot(v, axis) * axis; if (d.sqrMagnitude > best) { best = d.sqrMagnitude; i2 = i; } }
            if (i2 < 0 || best <= eps * eps) return null;
            var normal = Vector3.Cross(p[i1] - p[i0], p[i2] - p[i0]).normalized;
            int i3 = -1; best = 0f;
            for (int i = 0; i < n; i++) { float d = Mathf.Abs(Vector3.Dot(p[i] - p[i0], normal)); if (d > best) { best = d; i3 = i; } }
            if (i3 < 0 || best <= eps) return null;

            var inside = (p[i0] + p[i1] + p[i2] + p[i3]) / 4f;
            var faces = new List<HullFace>();
            AddFace(faces, p, i0, i1, i2, inside, eps);
            AddFace(faces, p, i0, i1, i3, inside, eps);
            AddFace(faces, p, i0, i2, i3, inside, eps);
            AddFace(faces, p, i1, i2, i3, inside, eps);
            if (faces.Count < 4) return null;

            var horizon = new Dictionary<long, HullEdge>();
            for (int pi = 0; pi < n; pi++)
            {
                if (pi == i0 || pi == i1 || pi == i2 || pi == i3) continue;
                var q = p[pi];
                bool outside = false;
                for (int f = 0; f < faces.Count; f++)
                {
                    var face = faces[f];
                    if (face.Gone) continue;
                    if (Vector3.Dot(face.Normal, q) > face.D + eps) { face.Gone = true; faces[f] = face; outside = true; }
                }
                if (!outside) continue;
                horizon.Clear();
                foreach (var face in faces)
                {
                    if (!face.Gone) continue;
                    Edge(horizon, face.A, face.B); Edge(horizon, face.B, face.C); Edge(horizon, face.C, face.A);
                }
                faces.RemoveAll(f => f.Gone);
                foreach (var edge in horizon.Values)
                    if (edge.Count == 1) AddFace(faces, p, edge.A, edge.B, pi, inside, eps);
            }
            return faces;
        }

        private static void Edge(Dictionary<long, HullEdge> horizon, int a, int b)
        {
            long key = ((long)Mathf.Min(a, b) << 32) | (uint)Mathf.Max(a, b);
            HullEdge edge;
            if (horizon.TryGetValue(key, out edge)) { edge.Count++; horizon[key] = edge; return; }
            horizon[key] = new HullEdge { A = a, B = b, Count = 1 };
        }

        private static void AddFace(List<HullFace> faces, List<Vector3> p, int a, int b, int c, Vector3 inside, float eps)
        {
            var normal = Vector3.Cross(p[b] - p[a], p[c] - p[a]);
            if (normal.sqrMagnitude < eps * eps) return; // a sliver: no face
            normal.Normalize();
            if (Vector3.Dot(normal, inside - p[a]) > 0f) { int t = b; b = c; c = t; normal = -normal; }
            faces.Add(new HullFace { A = a, B = b, C = c, Normal = normal, D = Vector3.Dot(normal, p[a]) });
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

        // The model's textures, made once (a piece per frame) and shared by both bodies: by file name, "<file> data" for
        // normal and gloss maps (linear), and "<mask> glow".
        private readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, UnityEngine.Material> materials = new Dictionary<string, UnityEngine.Material>(StringComparer.OrdinalIgnoreCase); // both bodies share them

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

        /// <summary>A texture beside the model, compressed (the fast compressor: this runs in the briefing room); linear for data (normal and gloss maps), not colours.</summary>
        private Texture2D MakeTexture(Loaded data, string name, bool linear)
        {
            if (name == null || !data.ImageFiles.TryGetValue(name, out var bytes)) return null;
            var tex = linear ? new Texture2D(2, 2, TextureFormat.ARGB32, true, true) { name = name } : new Texture2D(2, 2, TextureFormat.RGB24, true) { name = name };
            if (!tex.LoadImage(bytes)) { UnityEngine.Object.Destroy(tex); return null; }
            bool keepLettering = !linear && lettered != null && Array.IndexOf(lettered, name) >= 0;
            if (keepLettering && tex.format != TextureFormat.RGB24)
            {
                // LoadImage may replace RGB24 with an alpha format even for an opaque PNG.
                // Compress then produces DXT5, but HullName's block painter only accepts DXT1.
                var pixels = tex.GetPixels32();
                bool opaque = Array.TrueForAll(pixels, p => p.a == 255);
                if (opaque)
                {
                    var rgb = new Texture2D(tex.width, tex.height, TextureFormat.RGB24, true) { name = name };
                    rgb.SetPixels32(pixels);
                    rgb.Apply(true, false);
                    UnityEngine.Object.Destroy(tex);
                    tex = rgb;
                }
                // Preserve real transparency; the format guard below reports unsupported assets.
            }
            tex.anisoLevel = 4;
            tex.Compress(false);
            // A texture the crew's name is painted into (HullName): its blocks kept, as the texture itself won't be readable.
            if (keepLettering)
            {
                if (tex.format == TextureFormat.DXT1) blocks[name] = new Blocks { W = tex.width, H = tex.height, MipCount = tex.mipmapCount, Data = tex.GetRawTextureData() };
                else Plugin.Log.LogWarning($"{Name}: {name} compressed to {tex.format}, not DXT1; its lettering stays as it is");
            }
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

        private Texture2D Made(string key) => key != null && textures.TryGetValue(key, out var t) ? t : null;

        /// <summary>A material for a piece: the game hull's (its shader), with the piece's own textures and colours (one per material, for both bodies).</summary>
        private UnityEngine.Material MaterialFor(Loaded data, string key, UnityEngine.Material hull)
        {
            key = key ?? "";
            if (materials.TryGetValue(key, out var m)) return m;
            data.Materials.TryGetValue(key, out var om);
            if (om != null && om.Opacity < 0.1f) { materials[key] = null; return null; } // see-through glass: not drawn
            m = hull != null ? new UnityEngine.Material(hull) : new UnityEngine.Material(Shader.Find("Standard"));
            m.name = Name + " " + key;
            UnityEngine.Object.DontDestroyOnLoad(m);
            foreach (var keyword in new[] { "_NORMALMAP", "_METALLICGLOSSMAP", "_SPECGLOSSMAP", "_DETAIL_MULX2", "_PARALLAXMAP", "_EMISSION" }) m.DisableKeyword(keyword);
            foreach (var prop in new[] { "_MainTex", "_BumpMap", "_MetallicGlossMap", "_SpecGlossMap", "_DetailAlbedoMap", "_DetailNormalMap", "_OcclusionMap", "_ParallaxMap", "_EmissionMap" })
                if (m.HasProperty(prop)) m.SetTexture(prop, null);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0.35f);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.45f);
            if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", Color.black);

            var albedo = Made(om?.Diffuse);
            var glow = om?.Emission == null ? null : om.Emission == om.Diffuse ? Made(om.Emission) : Made(om.Emission + " glow");
            if (m.HasProperty("_Color")) m.SetColor("_Color", om != null && albedo == null ? om.Colour : Color.white);
            if (albedo != null) m.SetTexture("_MainTex", albedo);
            // The relief of its panels and how they shine.
            var normal = m.HasProperty("_BumpMap") && om?.Normal != null ? Made(om.Normal + " data") : null;
            if (normal != null)
            {
                m.SetTexture("_BumpMap", normal);
                if (m.HasProperty("_BumpScale")) m.SetFloat("_BumpScale", 1f);
                m.EnableKeyword("_NORMALMAP");
            }
            var gloss = m.HasProperty("_MetallicGlossMap") && om?.Gloss != null ? Made(om.Gloss + " data") : null;
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
