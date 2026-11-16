using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace StbcRevival.Galaxy
{
    /// <summary>
    /// Pictures of the catalogue's prefabs for the galaxy designer: 128×128 PNGs in BepInEx/galaxies/thumbs/, one per
    /// frame, only on the shuttle or in the briefing room; GalaxyCatalogue writes their index, thumbs.json.
    ///
    /// Without disturbing the player's view or the game:
    /// - Each prefab is instantiated under an inactive holder, so none of its scripts wake (no Awake, OnEnable,
    ///   network or sector registration, sound). Every component but the ones that draw is destroyed while still
    ///   inactive (so no OnDestroy either); a prefab whose components can't all be removed is skipped. What stays:
    ///   Transform, MeshFilter, the renderers, ParticleSystem, LODGroup, and the rendering-only scripts in
    ///   <see cref="DrawingScripts"/> (Space Graphics Toolkit coronas, atmospheres, gas giants, clouds, rings,
    ///   prominences, singularities and their models, and the game's Planet tint), which stars and planets need to look
    ///   like anything. Those register with their own static lists and Camera.onPreCull/onPreRender/onPostRender in
    ///   OnEnable and leave both in OnDisable; the instance is destroyed (OnDisable runs) before the frame ends, and
    ///   the three camera callbacks are put back as they were before activation anyway, logged if anything was left.
    /// - Everything is moved onto one unnamed layer, ideally one no game camera draws; a temporary camera (disabled,
    ///   mono: stereoTargetEye None, culling only that layer) is rendered by hand into a RenderTexture, with a
    ///   temporary directional light limited to that layer, on only while the instance exists, and fog off for it.
    /// - The instance is destroyed in the same frame it was made, before the game renders, so no game camera ever
    ///   sees it; the camera, light, holder and textures are destroyed when the run ends or stops.
    /// A picture with too few lit pixels counts as none (Thumb null, logged). Any prefab that throws is logged and
    /// skipped; anything wrong with the run itself logs and stops the pictures.
    /// </summary>
    internal static class GalaxyThumbnails
    {
        private const int Size = 128, RenderSize = Size * 2; // rendered at twice the size and averaged down: smooth edges
        private const int LitLevel = 12, MinLitPixels = 40;  // a picture needs this many pixels brighter than this
        private const float Fov = 30f;
        private static readonly Vector3 Place = new Vector3(0f, 100000f, 0f);
        private static readonly Vector3 ViewDir = new Vector3(-0.55f, -0.35f, 1f).normalized;
        private static readonly Vector3 LightDir = new Vector3(0.45f, -0.5f, 1f).normalized;
        private static readonly Type[] Keep =
        {
            typeof(Transform), typeof(MeshFilter), typeof(MeshRenderer), typeof(SkinnedMeshRenderer), typeof(ParticleSystem),
            typeof(ParticleSystemRenderer), typeof(LODGroup), typeof(LineRenderer), typeof(TrailRenderer),
        };

        /// <summary>
        /// Scripts that only draw, kept on the instance (a subclass counts by its base: SgtAtmosphere is an SgtCorona,
        /// SgtAccretion an SgtRing). From the decompiled game: their Awake sets gradients and drops stale model
        /// links; OnEnable adds them to their class's static list (AllCoronas, AllJovians, …) and, for corona,
        /// atmosphere, jovian, cloudsphere and prominence, to Camera.onPreRender/onPreCull/onPostRender; OnDisable
        /// undoes both; OnDestroy destroys their temporary materials and textures and releases their model children
        /// (destroyed with the instance). UpdateState (normally from Update, which never runs here) makes their
        /// materials and model children; it's called by hand. Planet has no Awake/OnEnable; its Start (called by
        /// hand) only tints its own renderers' materials. No networking, sector, sound or event registration.
        /// The game's other star/cloud scripts (SunShaftsSource, ParticleFlareInterface, AnomalyCloudVolume,
        /// CameraFacingBillboard, ParticleLOD, LensFlareVR) register with game managers, so they're removed.
        /// </summary>
        private static readonly HashSet<string> DrawingScripts = new HashSet<string>
        {
            "SgtCorona", "SgtCoronaOuter", "SgtJovian", "SgtJovianModel", "SgtCloudsphere", "SgtCloudsphereModel",
            "SgtRing", "SgtRingSegment", "SgtProminence", "SgtProminencePlane", "SgtSingularity", "SgtSingularityModel",
            "SgtShadow", "Planet",
        };

        internal static string Folder => Path.Combine(GalaxyCatalogue.Folder, "thumbs");

        internal sealed class Item
        {
            public CataloguePrefab Prefab;
            public GameObject Source;
            public string File;
        }

        private sealed class Run
        {
            public List<Item> Items;
            public int Index, Taken, Empty, Failed, FailedInARow;
            public int Layer;
            public GameObject Holder, CameraObject, LightObject;
            public Camera Camera;
            public Light Light;
            public RenderTexture Target;
            public Texture2D Read, Out;
            public Action<bool, string> Done;
            public Stopwatch Clock;
            public bool ShuttleOnly;
            public readonly List<string> Skipped = new List<string>();
            public readonly List<string> Blank = new List<string>();
            // Kind -> [pictures, prefabs reached].
            public readonly Dictionary<string, int[]> Kinds = new Dictionary<string, int[]>();
        }

        private static Run current;

        internal static bool Running => current != null;

        /// <summary>On the shuttle or in the briefing room, not loading: the only places pictures are taken.</summary>
        internal static bool CanRun(out string why, bool shuttleOnly = false)
        {
            string phase = GameState.Phase();
            if (phase != "GamePhase_MainMenu" && (shuttleOnly || phase != "GamePhase_BriefingRoom"))
            {
                why = (shuttleOnly ? "not on the shuttle (" : "not on the shuttle or in the briefing room (") + (phase ?? "no phase") + ")";
                return false;
            }
            if (GameState.Loading())
            {
                why = "a level is loading";
                return false;
            }
            why = null;
            return true;
        }

        /// <summary>
        /// Starts a run. done(interrupted, summary) is called once when it ends: interrupted only when the player left
        /// (the shuttle, for the automatic run; the shuttle or briefing room otherwise), a level loads or the scene
        /// went; any other end, finished or not, is as complete as it will get. False if it couldn't start (done not called).
        /// </summary>
        internal static bool Start(List<Item> items, bool shuttleOnly, Action<bool, string> done)
        {
            if (current != null) return false;
            var r = new Run { Items = items, Clock = Stopwatch.StartNew(), ShuttleOnly = shuttleOnly };
            current = r;
            try
            {
                Directory.CreateDirectory(Folder);
                r.Layer = PickLayer();
                r.Holder = new GameObject("Revival galaxy picture holder");
                r.Holder.SetActive(false);
                r.Holder.transform.position = Place;

                r.Target = new RenderTexture(RenderSize, RenderSize, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
                r.Target.Create();
                r.Read = new Texture2D(RenderSize, RenderSize, TextureFormat.ARGB32, false);
                r.Out = new Texture2D(Size, Size, TextureFormat.ARGB32, false);

                // Set up in the same call as it's made, so it never renders on its own (no frame passes in between).
                r.CameraObject = new GameObject("Revival galaxy picture camera");
                r.Camera = r.CameraObject.AddComponent<Camera>();
                r.Camera.enabled = false;
                r.Camera.stereoTargetEye = StereoTargetEyeMask.None;
                r.Camera.targetTexture = r.Target;
                r.Camera.cullingMask = 1 << r.Layer;
                r.Camera.clearFlags = CameraClearFlags.SolidColor;
                r.Camera.backgroundColor = Color.black;
                r.Camera.fieldOfView = Fov;
                r.Camera.aspect = 1f;
                r.Camera.renderingPath = RenderingPath.Forward;
                r.Camera.useOcclusionCulling = false;
                r.Camera.depth = -100;

                r.LightObject = new GameObject("Revival galaxy picture light");
                r.Light = r.LightObject.AddComponent<Light>();
                r.Light.enabled = false;
                r.Light.type = LightType.Directional;
                r.Light.cullingMask = 1 << r.Layer;
                r.Light.shadows = LightShadows.None;
                r.Light.intensity = 1.2f;
                r.Light.color = Color.white;
                r.LightObject.transform.rotation = Quaternion.LookRotation(LightDir);

                Plugin.Log.LogInfo($"Galaxy: taking pictures of {items.Count} prefabs on layer {r.Layer}, one each frame");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Galaxy: pictures could not start: " + e);
                Stop("could not start", false); // Done not set yet: the caller hears false instead
                return false;
            }
            r.Done = done;
            return true;
        }

        /// <summary>Every frame: one more picture while a run is on.</summary>
        internal static void Update()
        {
            var r = current;
            if (r == null) return;
            try
            {
                if (!CanRun(out string why, r.ShuttleOnly))
                {
                    Stop("stopped: " + why, true);
                    return;
                }
                if (r.Camera == null || r.Light == null || r.Holder == null)
                {
                    Stop("stopped: the picture camera went away (scene change?)", true);
                    return;
                }
                if (r.Index >= r.Items.Count)
                {
                    Stop("done", false);
                    return;
                }
                var item = r.Items[r.Index++];
                if (TakeOne(r, item)) r.FailedInARow = 0;
                else if (++r.FailedInARow >= 10)
                {
                    Stop("stopped: 10 prefabs in a row failed", false);
                    return;
                }
                if (r.Index % 50 == 0)
                    Plugin.Log.LogInfo($"Galaxy: pictures {r.Index} of {r.Items.Count} ({r.Taken} taken, {r.Empty} without a picture, {r.Failed} failed)");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Galaxy: pictures: " + e);
                Stop("stopped after an error", false);
            }
        }

        // One prefab: false if it failed (threw or couldn't be made safe), true otherwise (a picture, or none to take).
        private static bool TakeOne(Run r, Item item)
        {
            GameObject instance = null;
            string kind = string.IsNullOrEmpty(item.Prefab.Kind) ? "other" : item.Prefab.Kind;
            if (!r.Kinds.TryGetValue(kind, out var counts)) r.Kinds[kind] = counts = new int[2];
            counts[1]++;
            Camera.CameraCallback preCull = Camera.onPreCull, preRender = Camera.onPreRender, postRender = Camera.onPostRender;
            bool activated = false;
            Transform[] before = null;
            string station = null;
            try
            {
                item.Prefab.Thumb = null;
                if (item.Source == null)
                {
                    NoPicture(r, item, null);
                    return true;
                }
                // Under the inactive holder nothing on it wakes up.
                instance = (GameObject)UnityEngine.Object.Instantiate(item.Source, r.Holder.transform, false);
                instance.transform.localPosition = Vector3.zero;
                // A procedural station's model is built from pieces by the game's Awake: built here instead, still
                // inactive, before the strip (which then covers the pieces too).
                try { station = GalaxyStations.Build(instance); }
                catch (Exception e)
                {
                    r.Failed++;
                    Skip(r, item, "building the station: " + e.GetType().Name + ": " + e.Message);
                    return false;
                }
                if (!Strip(instance, out string left))
                {
                    r.Failed++;
                    Skip(r, item, "kept " + left + " that couldn't be removed");
                    return false;
                }
                SetLayer(instance.transform, r.Layer);
                if (!instance.activeSelf) instance.SetActive(true);
                r.Light.enabled = true; // before activation, for scripts that look for lights; off again in finally
                activated = true;
                r.Holder.SetActive(true);

                before = instance.GetComponentsInChildren<Transform>(true);
                Prepare(instance);
                ReleasePooled(before, instance.transform, r.Layer, item);
                SetLayer(instance.transform, r.Layer); // models the scripts just made

                string built = station != null ? $" (station built: {station})" : "";
                if (!Bounds(instance, out var bounds))
                {
                    NoPicture(r, item, "nothing drawn (no renderer with a material and a size)" + built);
                    return true;
                }
                byte[] png = Render(r, bounds, out int lit);
                if (png == null)
                {
                    NoPicture(r, item, $"the picture came out blank ({lit} lit pixels)" + built);
                    return true;
                }
                if (station != null) Plugin.Log.LogInfo($"Galaxy: picture of {item.Prefab.Name}: station built ({station}), {lit} lit pixels");
                File.WriteAllBytes(Path.Combine(Folder, item.File), png);
                item.Prefab.Thumb = item.File;
                r.Taken++;
                counts[0]++;
                return true;
            }
            catch (Exception e)
            {
                r.Failed++;
                Skip(r, item, e.GetType().Name + ": " + e.Message);
                return false;
            }
            finally
            {
                try
                {
                    if (instance != null) UnityEngine.Object.DestroyImmediate(instance);
                    if (r.Holder != null) r.Holder.SetActive(false);
                    if (r.Light != null) r.Light.enabled = false;
                }
                catch (Exception e) { Plugin.Log.LogWarning($"Galaxy: picture of {item.Prefab.Name}: clearing up: {e.Message}"); }
                if (before != null) ReleasePooled(before, null, r.Layer, item);
                if (activated && (Camera.onPreCull != preCull || Camera.onPreRender != preRender || Camera.onPostRender != postRender))
                {
                    Camera.onPreCull = preCull;
                    Camera.onPreRender = preRender;
                    Camera.onPostRender = postRender;
                    Plugin.Log.LogWarning($"Galaxy: picture of {item.Prefab.Name}: camera callbacks were left behind; put back as they were");
                }
            }
        }

        // An SGT script whose model count shrinks hands the spare models to SGT's shared pool (SgtComponentPoolOf, kept
        // for the session) instead of destroying them: anything that was part of the instance and has left it
        // (or, after the instance is gone, is still there) goes back to layer 0, what a fresh model has, so the
        // game's own bodies that later take it from the pool draw on its cameras.
        private static void ReleasePooled(Transform[] before, Transform instance, int layer, Item item)
        {
            int reset = 0;
            foreach (var t in before)
            {
                if (t == null || (instance != null && (t == instance || t.IsChildOf(instance)))) continue;
                reset += ResetLayer(t, layer);
            }
            if (reset > 0) Plugin.Log.LogInfo($"Galaxy: picture of {item.Prefab.Name}: {reset} pooled model object(s) put back on layer 0");
        }

        private static int ResetLayer(Transform t, int layer)
        {
            int n = 0;
            if (t.gameObject.layer == layer)
            {
                t.gameObject.layer = 0;
                n++;
            }
            for (int i = 0; i < t.childCount; i++) n += ResetLayer(t.GetChild(i), layer);
            return n;
        }

        // No picture for this prefab: an earlier run's file goes too.
        private static void NoPicture(Run r, Item item, string why)
        {
            r.Empty++;
            if (why != null)
            {
                r.Blank.Add(item.Prefab.Name);
                Plugin.Log.LogInfo($"Galaxy: no picture of {item.Prefab.Name} ({item.Prefab.Kind}): {why}");
            }
            try
            {
                string path = Path.Combine(Folder, item.File);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Galaxy: removing the old picture of {item.Prefab.Name}: {e.Message}"); }
        }

        private static void Skip(Run r, Item item, string why)
        {
            r.Skipped.Add(item.Prefab.Name);
            Plugin.Log.LogWarning($"Galaxy: no picture of {item.Prefab.Name}: {why}");
        }

        // What Update would have done before the first frame: the drawing scripts' UpdateState (materials, models),
        // Planet's Start (tints), and the particle systems run for a while (a filled-in cloud, not its first frame).
        private static void Prepare(GameObject instance)
        {
            foreach (var script in instance.GetComponentsInChildren<MonoBehaviour>())
            {
                if (script == null) continue;
                var type = script.GetType();
                string name = type.Name == "Planet" ? "Start" : "UpdateState";
                try
                {
                    var method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    method?.Invoke(script, null);
                }
                catch (Exception e)
                {
                    var inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                    Plugin.Log.LogInfo($"Galaxy: picture: {type.Name}.{name} on {script.name}: {inner.GetType().Name}: {inner.Message}");
                }
            }
            foreach (var ps in instance.GetComponentsInChildren<ParticleSystem>())
                if (ps.transform.parent == null || ps.transform.parent.GetComponentInParent<ParticleSystem>() == null)
                    ps.Simulate(3f, true, true);
        }

        // Destroys every component but the ones that draw, while the instance is still inactive. Dependants first
        // (scripts, then other behaviours, joints, the rest, rigidbodies last), a few passes for the ones another needed.
        private static bool Strip(GameObject instance, out string left)
        {
            left = null;
            for (int pass = 0; pass < 4; pass++)
            {
                var remove = new List<Component>();
                foreach (var c in instance.GetComponentsInChildren<Component>(true))
                    if (c != null && !Kept(c)) remove.Add(c); // null: a missing script, which can't run anyway
                if (remove.Count == 0) return true;
                remove.Reverse();
                remove.Sort((a, b) => Order(a).CompareTo(Order(b))); // List.Sort isn't stable, but within a group order hardly matters
                if (pass == 3)
                {
                    var names = new List<string>();
                    foreach (var c in remove) if (!names.Contains(c.GetType().Name)) names.Add(c.GetType().Name);
                    left = string.Join(", ", names.ToArray());
                    return false;
                }
                foreach (var c in remove)
                    if (c != null) UnityEngine.Object.DestroyImmediate(c);
            }
            return true;
        }

        private static bool Kept(Component c)
        {
            foreach (var t in Keep) if (t.IsInstanceOfType(c)) return true;
            if (c is MonoBehaviour)
                for (var t = c.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                    if (DrawingScripts.Contains(t.Name)) return true;
            return false;
        }

        private static int Order(Component c) =>
            c is MonoBehaviour ? 0 : c is Behaviour ? 1 : c is Joint ? 2 : c is Rigidbody ? 4 : 3;

        private static void SetLayer(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++) SetLayer(t.GetChild(i), layer);
        }

        // What the instance draws, in world space: enabled renderers with a material, particles only if any.
        private static bool Bounds(GameObject instance, out Bounds bounds)
        {
            bounds = new Bounds();
            bool any = false;
            foreach (var renderer in instance.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.enabled || renderer.sharedMaterial == null) continue;
                if (renderer is ParticleSystemRenderer)
                {
                    var ps = renderer.GetComponent<ParticleSystem>();
                    if (ps == null || ps.particleCount == 0) continue;
                }
                var b = renderer.bounds;
                if (b.size.sqrMagnitude <= 0 || Bad(b.center) || Bad(b.size)) continue;
                if (any) bounds.Encapsulate(b);
                else bounds = b;
                any = true;
            }
            return any && bounds.extents.magnitude > 1e-4f;
        }

        private static bool Bad(Vector3 v) => float.IsNaN(v.x + v.y + v.z) || float.IsInfinity(v.x + v.y + v.z);

        // Frames the bounds' sphere, renders once by hand and returns the PNG, or null when too few pixels are lit.
        private static byte[] Render(Run r, Bounds b, out int lit)
        {
            float radius = b.extents.magnitude;
            float dist = radius / Mathf.Sin(Fov * 0.5f * Mathf.Deg2Rad) * 1.02f;
            r.CameraObject.transform.position = b.center - ViewDir * dist;
            r.CameraObject.transform.rotation = Quaternion.LookRotation(ViewDir);
            r.Camera.nearClipPlane = Mathf.Max(dist - radius * 1.05f, dist * 0.001f);
            r.Camera.farClipPlane = dist + radius * 1.05f;

            bool fog = RenderSettings.fog;
            var previous = RenderTexture.active;
            try
            {
                RenderSettings.fog = false;
                r.Camera.Render();
                RenderTexture.active = r.Target;
                r.Read.ReadPixels(new Rect(0, 0, RenderSize, RenderSize), 0, 0, false);
            }
            finally
            {
                RenderSettings.fog = fog;
                RenderTexture.active = previous;
            }

            var src = r.Read.GetPixels32();
            var dst = new Color32[Size * Size];
            lit = 0;
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    int i = y * 2 * RenderSize + x * 2;
                    Color32 a = src[i], c = src[i + 1], d = src[i + RenderSize], e = src[i + RenderSize + 1];
                    var p = new Color32((byte)((a.r + c.r + d.r + e.r) / 4), (byte)((a.g + c.g + d.g + e.g) / 4), (byte)((a.b + c.b + d.b + e.b) / 4), 255);
                    if (Math.Max(p.r, Math.Max(p.g, p.b)) > LitLevel) lit++;
                    dst[y * Size + x] = p;
                }
            if (lit < MinLitPixels) return null;
            r.Out.SetPixels32(dst);
            return r.Out.EncodeToPNG();
        }

        // An unnamed layer (8..31) that no enabled camera draws, else any unnamed one: the instance only exists
        // between frames anyway, so that's a second line of defence, not the only one.
        private static int PickLayer()
        {
            int seen = 0;
            foreach (var cam in Camera.allCameras) if (cam != null) seen |= cam.cullingMask;
            int fallback = -1;
            for (int layer = 31; layer >= 8; layer--)
            {
                if (!string.IsNullOrEmpty(LayerMask.LayerToName(layer))) continue;
                if ((seen & (1 << layer)) == 0) return layer;
                if (fallback < 0) fallback = layer;
            }
            if (fallback < 0) throw new InvalidOperationException("no unnamed layer to take pictures on");
            Plugin.Log.LogInfo($"Galaxy: every unnamed layer is drawn by some camera; pictures on layer {fallback} (instances never last into a rendered frame)");
            return fallback;
        }

        // Ends the run: everything made for it destroyed, then the summary handed to Done.
        private static void Stop(string how, bool interrupted)
        {
            var r = current;
            current = null;
            if (r == null) return;
            try
            {
                if (r.Camera != null) r.Camera.targetTexture = null;
                if (r.Target != null) r.Target.Release();
            }
            catch (Exception e) { Plugin.Log.LogWarning("Galaxy: pictures: releasing the target: " + e.Message); }
            foreach (var o in new UnityEngine.Object[] { r.Holder, r.CameraObject, r.LightObject, r.Target, r.Read, r.Out })
                try { if (o != null) UnityEngine.Object.Destroy(o); }
                catch (Exception e) { Plugin.Log.LogWarning("Galaxy: pictures: clearing up: " + e.Message); }
            var kinds = new List<string>();
            foreach (var kv in r.Kinds) kinds.Add($"{kv.Key} {kv.Value[0]}/{kv.Value[1]}");
            string summary = $"pictures {how}: {r.Taken} taken, {r.Empty} without a picture, {r.Failed} failed, " +
                $"{r.Items.Count - r.Index} not reached, in {r.Clock.Elapsed.TotalSeconds:0.0} s; pictures by kind: {string.Join(", ", kinds.ToArray())}";
            if (r.Skipped.Count > 0) summary += "; failed: " + string.Join(", ", r.Skipped.ToArray());
            if (r.Blank.Count > 0) summary += "; drew nothing: " + string.Join(", ", r.Blank.ToArray());
            try { r.Done?.Invoke(interrupted, summary); }
            catch (Exception e) { Plugin.Log.LogError("Galaxy: after the pictures: " + e); }
        }
    }
}
