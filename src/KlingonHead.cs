using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StbcRevival
{
    /// <summary>
    /// [Avatar] KlingonHead: wear a Klingon captain's head (one of the two hail-screen characters) on a male
    /// Human avatar. Unlike the Romulan captain, who has the player's female head with her own face material,
    /// the Klingons have their own head mesh (Klingon_Head: ridges, hair, 18 face shapes), so it's grafted on:
    /// a copy of their head rides on the avatar's Head bone, placed so its eyes are where the avatar's eyes
    /// are (scaled to the same eye width), and the avatar's own head is left out while cameras draw (it keeps its
    /// materials, so the creator still works on it). The avatar's face animation (speech, blinks, expressions) and
    /// the creator's face sliders (KlingonFace) are copied onto it by shape name each frame, its skin and hair
    /// colours too, and its lighting, layer, shadows and visibility follow the avatar's head. In the creator the
    /// look choice picks between the two Klingon heads.
    /// The heads are the hail views' character prefabs in the Aegis bridge's assets. They're read from the game's
    /// files as it starts (KlingonHeadFiles), and made once their shaders are loaded; if that fails, the bridge is
    /// loaded alongside at launch for them (PreloadBridge), and any bridge that loads later provides them too.
    /// The choice is a player property ("KH": 0 off, 1 or 2), so crewmates with the mod see it too.
    /// </summary>
    internal static class KlingonHead
    {
        private const string PlayerKey = "KH";
        internal static readonly string[] Characters = { null, "KlingonCaptainA", "KlingonCaptainFlagship" };
        internal static readonly string[] Names = { "off", "captain", "flagship captain" };

        private sealed class Source
        {
            public Mesh Mesh;
            public Material[] Materials;
            public Matrix4x4 MeshToHead; // the head mesh's space to the character's Head bone (that bone's bind pose)
            public Vector3 HeadEyeMid;   // the eye bones' midpoint and spacing in the Head bone's space
            public float HeadEyeSpacing;
        }

        private static readonly Source[] sources = new Source[Characters.Length];
        private static readonly Dictionary<SkinnedMeshRenderer, KlingonHeadGraft> grafts = new Dictionary<SkinnedMeshRenderer, KlingonHeadGraft>();
        private static readonly FieldInfo CharacterPrefab = AccessTools.Field(AccessTools.TypeByName("NPCHailingView"), "_characterPrefab");
        private static float nextCheck, nextSearch;
        private static Material hidden;
        private static bool toldNotLoaded;
        private static readonly HashSet<SkinnedMeshRenderer> unfit = new HashSet<SkinnedMeshRenderer>(); // no Head or eye bones: tried once

        internal static void Update()
        {
            KlingonFace.Tick();
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 1f;
            FindSources();

            bool inRoom = Publish();
            foreach (var info in UnityEngine.Object.FindObjectsOfType<PlayerInfo>())
                Apply(info.VisualPawnObject, Wants(info, inRoom));
            // The character creator's own copies of the presets (as RomulanFace).
            var creator = UnityEngine.Object.FindObjectOfType<CustomizationManager>();
            if (creator != null && RomulanFace.CreatorAvatars?.GetValue(creator) is GameObject[] avatars && RomulanFace.Profiles() is IList profiles)
            {
                FollowLook(avatars, profiles);
                for (int i = 0; i < avatars.Length && i < profiles.Count; i++)
                    if (avatars[i] != null && avatars[i].activeInHierarchy)
                        Apply(avatars[i], MaleHuman(profiles[i] as CharacterProfile) ? Chosen() : 0);
            }

            var gone = new List<SkinnedMeshRenderer>();
            foreach (var pair in grafts) if (pair.Key == null || pair.Value == null) gone.Add(pair.Key);
            foreach (var head in gone) grafts.Remove(head);
            unfit.RemoveWhere(h => h == null);
        }

        private static readonly FieldInfo LookIndex = AccessTools.Field(typeof(FaceCustomization), "_currentLookIdx");

        // In the creator, a Klingon picks his head with the head/look choice: the captain's on even looks, the
        // flagship captain's on odd ones (the game has these two Klingon heads).
        private static void FollowLook(GameObject[] avatars, IList profiles)
        {
            if (Chosen() <= 0 || LookIndex == null) return;
            for (int i = 0; i < avatars.Length && i < profiles.Count; i++)
            {
                if (avatars[i] == null || !avatars[i].activeInHierarchy || !MaleHuman(profiles[i] as CharacterProfile)) continue;
                var face = avatars[i].GetComponentInChildren<FaceCustomization>(true);
                if (face == null || !(LookIndex.GetValue(face) is int look)) continue;
                int want = 1 + Mathf.Abs(look) % 2;
                if (want != Plugin.KlingonHead.Value || want != Plugin.KlingonHeadChoice.Value)
                {
                    // Both: the one worn, and the one KLINGON (and the Revival settings) pick.
                    Plugin.KlingonHeadChoice.Value = want;
                    Plugin.KlingonHead.Value = want;
                    Plugin.Log.LogInfo($"Klingon head: look {look} in the creator, so the {Names[want]}'s head");
                }
                return;
            }
        }

        private static int Chosen() => Mathf.Clamp(Plugin.KlingonHead.Value, 0, Characters.Length - 1);

        private static bool MaleHuman(CharacterProfile profile) =>
            profile != null && profile.CharacterSpecies.ToString() == "Human" && profile.CharacterGender.ToString() == "Male";

        // As RomulanFace.Wants: ours from the setting, others' from their player property, AI crew never.
        private static int Wants(PlayerInfo info, bool inRoom)
        {
            if (!MaleHuman(info.Profile)) return 0;
            var player = info.PKIBEAIHJNE;
            if (player == null) return inRoom ? 0 : Chosen();
            if (RomulanFace.IsLocal(player)) return Chosen();
            return RomulanFace.Properties(player) is IDictionary props && props.Contains(PlayerKey) && props[PlayerKey] is int v && v > 0 && v < Characters.Length ? v : 0;
        }

        private static bool Publish()
        {
            var room = Patches.PhotonNetworkType?.GetMethod("get_room", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
            if (room == null) return false;
            var local = Patches.PhotonNetworkType.GetMethod("get_player", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
            if (local == null) return true;
            int chosen = Chosen();
            var props = RomulanFace.Properties(local) as IDictionary;
            if (props != null && props.Contains(PlayerKey) && props[PlayerKey] is int current && current == chosen) return true;
            if (props == null || !props.Contains(PlayerKey)) { if (chosen == 0) return true; } // nothing to say
            if (PhotonProps.Set(local, PlayerKey, chosen)) Plugin.Log.LogInfo($"Klingon head: told the crew {Names[chosen]}");
            return true;
        }

        private static void Apply(GameObject avatar, int want)
        {
            var face = avatar == null ? null : avatar.GetComponentInChildren<FaceCustomization>(true);
            var head = face == null ? null : face.GetComponent<SkinnedMeshRenderer>();
            if (head == null) return;
            grafts.TryGetValue(head, out var graft);
            if (graft != null && graft.Variant == want) return;
            if (graft != null)
            {
                graft.Remove();
                grafts.Remove(head);
                Plugin.Log.LogInfo($"Klingon head: off {avatar.name}");
            }
            if (want <= 0 || unfit.Contains(head)) return;
            if (sources[want] == null)
            {
                if (!toldNotLoaded) Plugin.Log.LogInfo($"Klingon head: {Names[want]} wanted on {avatar.name}, but the heads aren't loaded (yet: they load as the game starts, or with a bridge)");
                toldNotLoaded = true;
                return;
            }
            graft = Graft(avatar, head, want);
            if (graft != null) grafts[head] = graft;
        }

        // The Klingon head on this avatar's Head bone: their mesh in their Head bone's space (its bind pose), then
        // moved and scaled so their eyes (eye bones, in Head space) land on the avatar's. The two rigs share
        // their bone layout (the eye bones sit at the same place in both Heads), so no turn is needed.
        private static KlingonHeadGraft Graft(GameObject avatar, SkinnedMeshRenderer head, int variant)
        {
            var source = sources[variant];
            try
            {
                // The head's Head bone; an optimised rig (no bones on the renderer) still exposes Head and the eyes.
                var bones = head.bones;
                int index = bones == null ? -1 : Array.FindIndex(bones, b => b != null && b.name == "Head");
                var bone = index >= 0 ? bones[index] : Find(avatar.transform, "Head");
                var left = bone == null ? null : Find(bone, "Eye_L") ?? Find(avatar.transform, "Eye_L");
                var right = bone == null ? null : Find(bone, "Eye_R") ?? Find(avatar.transform, "Eye_R");
                if (bone == null || left == null || right == null)
                {
                    if (unfit.Add(head)) Plugin.Log.LogInfo($"Klingon head: {avatar.name} has no {(bone == null ? "Head bone" : "eye bones")}; left alone");
                    return null;
                }
                var mid = bone.InverseTransformPoint((left.position + right.position) * 0.5f);
                float spacing = (bone.InverseTransformPoint(left.position) - bone.InverseTransformPoint(right.position)).magnitude;
                float scale = spacing / source.HeadEyeSpacing;
                var local = Matrix4x4.TRS(mid, Quaternion.identity, Vector3.one * scale)
                            * Matrix4x4.TRS(-source.HeadEyeMid, Quaternion.identity, Vector3.one) * source.MeshToHead;
                string how = $"eyes {F(mid)} apart {spacing:F4}, scale {scale:F3}";

                var go = new GameObject("Klingon head");
                go.transform.SetParent(bone, false);
                Place(go.transform, local);
                go.layer = head.gameObject.layer;
                var worn = go.AddComponent<SkinnedMeshRenderer>();
                // Its own bones, one per bind pose, each placed at that bind pose's inverse: every vertex lands
                // where the head's mesh space puts it, so the whole head moves rigidly with the Head bone.
                var poses = source.Mesh.bindposes;
                if (poses.Length > 0)
                {
                    var anchors = new Transform[poses.Length];
                    for (int i = 0; i < poses.Length; i++)
                    {
                        anchors[i] = new GameObject("Klingon head bone " + i).transform;
                        anchors[i].SetParent(go.transform, false);
                        Place(anchors[i], poses[i].inverse);
                    }
                    worn.bones = anchors;
                    worn.rootBone = go.transform;
                }
                worn.sharedMesh = source.Mesh;
                worn.sharedMaterials = source.Materials;
                worn.updateWhenOffscreen = true;
                var graft = go.AddComponent<KlingonHeadGraft>();
                graft.Set(head, worn, variant, Hidden(avatar));
                KlingonFace.Request(graft, head, index, source.Mesh, local, spacing);
                ForgetRenderers(bone);
                Plugin.Log.LogInfo($"Klingon head: {Names[variant]} on {avatar.name} ({how}; at {F(go.transform.localPosition)} turned {F(go.transform.localEulerAngles)} x{go.transform.localScale.x:F3} on {bone.name}; drawn at {F(worn.bounds.center)} size {F(worn.bounds.size)}, avatar eyes at {F(left.position)})");
                return graft;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Klingon head: couldn't put it on {avatar.name}: {e}");
                return null;
            }
        }

        // The avatar's CharacterLightingInterface lists its renderers once (AOAGAPLFIEO) and lights each one
        // (SetPropertyBlock); with the Klingon head added or taken off, it lists them again.
        private static readonly FieldInfo LitRenderers = AccessTools.Field(typeof(CharacterLightingInterface), "AOAGAPLFIEO");

        internal static void ForgetRenderers(Transform inAvatar)
        {
            if (inAvatar == null || LitRenderers == null) return;
            foreach (var lighting in inAvatar.GetComponentsInParent<CharacterLightingInterface>(true)) LitRenderers.SetValue(lighting, null);
        }

        private static string F(Vector3 v) => $"({v.x:F3}, {v.y:F3}, {v.z:F3})";

        private static Transform Find(Transform parent, string name)
        {
            foreach (var t in parent.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t;
            return null;
        }

        private static void Place(Transform t, Matrix4x4 m)
        {
            Vector3 x = m.GetColumn(0), y = m.GetColumn(1), z = m.GetColumn(2);
            t.localPosition = m.GetColumn(3);
            t.localRotation = Quaternion.LookRotation(z, y);
            float s = x.magnitude;
            t.localScale = new Vector3(Vector3.Dot(Vector3.Cross(x, y), z) < 0 ? -s : s, y.magnitude, z.magnitude);
        }

        // The game's own invisible material (the hidden uniform badge), else a fully cut-out one.
        private static Material Hidden(GameObject avatar)
        {
            if (hidden != null) return hidden;
            foreach (var r in avatar.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                    if (m != null && m.name == "uniform_badge_hidden") return hidden = m;
            var texture = new Texture2D(1, 1);
            texture.SetPixel(0, 0, new Color(0, 0, 0, 0));
            texture.Apply();
            hidden = new Material(Shader.Find("Unlit/Transparent Cutout")) { name = "Klingon head: hidden", mainTexture = texture };
            hidden.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return hidden;
        }

        internal static bool IsHidden(Material m) => m != null && hidden != null && m == hidden;

        // The hail views' character prefabs, on a bridge; looked for every 5 s until both heads are found.
        private static bool Found => sources[1] != null && sources[2] != null;

        private static void FindSources(bool now = false)
        {
            if (Found) return;
            MakeFromFiles();
            if (Found) return;
            if ((!now && Time.unscaledTime < nextSearch) || CharacterPrefab == null) return;
            nextSearch = Time.unscaledTime + 5f;
            foreach (var view in Resources.FindObjectsOfTypeAll(CharacterPrefab.DeclaringType))
            {
                if (!(CharacterPrefab.GetValue(view) is GameObject prefab)) continue;
                int i = Array.IndexOf(Characters, prefab.name);
                if (i > 0 && sources[i] == null) sources[i] = Build(prefab);
            }
        }

        private static Source Build(GameObject character)
        {
            try
            {
                var root = character.transform;
                var head = root.Find("Klingon_Head")?.GetComponent<SkinnedMeshRenderer>();
                var eyes = root.Find("Klingon_Eyes")?.GetComponent<SkinnedMeshRenderer>();
                var headBone = root.Find("Head");
                var left = root.Find("Eye_L");
                var right = root.Find("Eye_R");
                if (head == null || head.sharedMesh == null || eyes == null || eyes.sharedMesh == null || headBone == null || left == null || right == null)
                {
                    var names = new List<string>();
                    foreach (Transform child in root) names.Add(child.name);
                    Plugin.Log.LogWarning($"Klingon head: {character.name} isn't laid out as expected ({string.Join(", ", names.ToArray())})");
                    return null;
                }
                // Their rig is optimised away (no bone transforms on the renderer), so the mesh's own bind poses
                // relate it to the bones; the Head's is the one that puts the head mesh's middle nearest the eyes
                // (the exposed eye transforms, in Head space). (The eye mesh is no guide: it sits off to one side.)
                var headEyeMid = headBone.InverseTransformPoint((left.position + right.position) * 0.5f);
                var spacing = (headBone.InverseTransformPoint(left.position) - headBone.InverseTransformPoint(right.position)).magnitude;
                var source = Make(character.name, head.sharedMesh, head.sharedMaterials, headEyeMid, spacing);
                // Kept for the session: the bridge's assets are unloaded with it.
                if (source != null)
                {
                    source.Mesh.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                    foreach (var m in source.Materials) if (m != null) m.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                }
                return source;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Klingon head: reading {character.name}: {e.Message}");
                return null;
            }
        }

        // A head to wear: the mesh, its materials, and where the Head bone and eyes are. Shared by the heads taken
        // from a loaded bridge and the ones read from the game's files.
        private static Source Make(string character, Mesh mesh, Material[] materials, Vector3 headEyeMid, float spacing)
        {
            {
                var headMid = mesh.bounds.center;
                var poses = mesh.bindposes;
                int best = -1;
                float bestMiss = float.MaxValue;
                var misses = new List<string>();
                for (int i = 0; i < poses.Length; i++)
                {
                    float miss = (poses[i].MultiplyPoint3x4(headMid) - headEyeMid).magnitude;
                    misses.Add(miss.ToString("F3"));
                    if (miss < bestMiss) { bestMiss = miss; best = i; }
                }
                if (best < 0 || spacing < 1e-4f)
                {
                    Plugin.Log.LogWarning($"Klingon head: {character}: {poses.Length} bind poses, eye spacing {spacing}; can't place it");
                    return null;
                }
                var source = new Source
                {
                    Mesh = mesh,
                    Materials = materials,
                    MeshToHead = poses[best],
                    HeadEyeMid = headEyeMid,
                    HeadEyeSpacing = spacing,
                };
                if (bestMiss > 0.15f)
                    Plugin.Log.LogWarning($"Klingon head: {character}: no bind pose puts the head by the eyes (misses {string.Join(", ", misses.ToArray())} m); using the closest");
                Plugin.Log.LogInfo($"Klingon head: found {character} ({source.Mesh.name}, {source.Mesh.vertexCount} vertices, {source.Mesh.blendShapeCount} face shapes; "
                                   + $"head bind pose {best} of {poses.Length} (head middle from the eyes {string.Join(", ", misses.ToArray())} m); eye bones {F(headEyeMid)} apart {spacing:F4}; mesh bounds {F(source.Mesh.bounds.center)} size {F(source.Mesh.bounds.size)})");
                return source;
            }
        }

        // ---- At launch: the heads without a bridge -----------------------------------------------------------
        // (The mesh can't be read back, so there's no saved copy: this is how the heads are had without a bridge.)
        // Every launch (anyone may pick KLINGON in the creator, or have a crewmate wearing one), the Aegis bridge scene (it owns the hail characters' assets) is loaded
        // alongside once, as the game starts: the moment it's in (after its objects' Awake, before any Start or
        // Update) the heads are taken and every one of its objects switched off, anything it put in
        // DontDestroyOnLoad is destroyed, and it's unloaded the next frame. At launch the game has set up few of
        // its own managers for the scene to trip over, and its own first full scene load clears the rest.
        private const string PreloadScene = "Bridge_scene_02";
        private static bool preloading;

        // ---- At launch: the heads from the game's files ---------------------------------------------------------
        // Read on a background thread as the game starts (KlingonHeadFiles); made once their shaders are loaded,
        // which they are by the shuttle. If the files can't be read as expected, the bridge preload below runs.
        private static Dictionary<string, KlingonHeadFiles.Head> fromFiles;
        private static readonly Dictionary<string, Mesh> fileMeshes = new Dictionary<string, Mesh>();
        private static bool toldWaitingForShaders;

        internal static System.Collections.IEnumerator PreloadAtLaunch()
        {
            if (Found || preloading) yield break;
            float started = Time.realtimeSinceStartup;
            Dictionary<string, KlingonHeadFiles.Head> read = null;
            Exception error = null;
            yield return KlingonHeadFiles.ReadInBackground(new[] { Characters[1], Characters[2] }, (heads, e) => { read = heads; error = e; });
            if (error == null && read != null && read.ContainsKey(Characters[1]) && read.ContainsKey(Characters[2]))
            {
                fromFiles = read;
                Plugin.Log.LogInfo($"Klingon head: read both heads from the game's files in {Time.realtimeSinceStartup - started:F2} s (no bridge to load)");
                MakeFromFiles();
                yield break;
            }
            Plugin.Log.LogWarning("Klingon head: couldn't read the heads from the game's files (" + (error != null ? error.Message : $"found {(read == null ? 0 : read.Count)} of 2")
                                  + "); loading the bridge for them instead");
            yield return PreloadBridge();
        }

        private static void MakeFromFiles()
        {
            if (fromFiles == null) return;
            for (int i = 1; i < Characters.Length; i++)
            {
                if (sources[i] != null || !fromFiles.TryGetValue(Characters[i], out var head)) continue;
                try
                {
                    var materials = new Material[head.Materials.Count];
                    for (int m = 0; m < materials.Length; m++)
                        if ((materials[m] = KlingonHeadFiles.MakeMaterial(head.Materials[m])) == null)
                        {
                            if (!toldWaitingForShaders) Plugin.Log.LogInfo($"Klingon head: waiting for the {head.Materials[m].Shader} shader to load");
                            toldWaitingForShaders = true;
                            return;
                        }
                    if (!fileMeshes.TryGetValue(head.Character, out var mesh) || mesh == null)
                    {
                        mesh = fileMeshes[head.Character] = KlingonHeadFiles.MakeMesh(head.Mesh);
                        mesh.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                    }
                    sources[i] = Make(head.Character, mesh, materials, head.HeadEyeMid, head.EyeSpacing);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Klingon head: making {head.Character} from the game's files: {e.Message}; it'll come from a bridge instead");
                    fromFiles.Remove(head.Character);
                }
            }
            if (Found) fromFiles = null;
        }

        private static System.Collections.IEnumerator PreloadBridge()
        {
            if (Found || preloading) yield break;
            preloading = true;
            float started = Time.realtimeSinceStartup;
            Scene loaded = default(Scene);
            bool arrived = false;
            var persistent = PersistentRoots();
            var killed = new List<string>();
            int switchedOff = 0;
            UnityEngine.Events.UnityAction<Scene, LoadSceneMode> onLoaded = (scene, mode) =>
            {
                if (arrived || scene.name != PreloadScene) return;
                arrived = true;
                loaded = scene;
                try { FindSources(true); }
                catch (Exception e) { Plugin.Log.LogWarning("Klingon head: taking the heads from the preloaded bridge: " + e.Message); }
                // Its roots by their scene: an additive scene isn't "loaded" yet when sceneLoaded fires (Unity 5.4),
                // so scene.GetRootGameObjects throws.
                try
                {
                    foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
                        if (t.parent == null && t.gameObject.scene == scene)
                        {
                            t.gameObject.SetActive(false);
                            switchedOff++;
                        }
                }
                catch (Exception e) { Plugin.Log.LogWarning("Klingon head: switching the preloaded bridge off: " + e.Message); }
                try
                {
                    foreach (var root in PersistentRoots())
                        if (!persistent.Contains(root))
                        {
                            killed.Add(root.name);
                            root.SetActive(false);
                            UnityEngine.Object.Destroy(root);
                        }
                }
                catch (Exception e) { Plugin.Log.LogWarning("Klingon head: removing what the preloaded bridge kept: " + e.Message); }
            };
            SceneManager.sceneLoaded += onLoaded;
            Plugin.Log.LogInfo($"Klingon head: loading the {PreloadScene} scene alongside, for the heads");
            AsyncOperation op = null;
            try { op = SceneManager.LoadSceneAsync(PreloadScene, LoadSceneMode.Additive); }
            catch (Exception e) { Plugin.Log.LogWarning("Klingon head: couldn't load the bridge: " + e.Message); }
            while (op != null && !arrived && Time.realtimeSinceStartup - started < 60f)
            {
                persistent = PersistentRoots(); // anything the game itself keeps while we wait is the game's
                yield return null;
            }
            SceneManager.sceneLoaded -= onLoaded;
            yield return null;
            try
            {
                if (loaded.IsValid() && loaded.isLoaded) SceneManager.UnloadScene(loaded);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Klingon head: unloading the preloaded bridge: " + e.Message);
            }
            Resources.UnloadUnusedAssets();
            preloading = false;
            Plugin.Log.LogInfo($"Klingon head: preload {(Found ? "found both heads" : arrived ? "found no heads" : "timed out")} in {Time.realtimeSinceStartup - started:F1} s, {switchedOff} of the bridge's objects switched off"
                               + (killed.Count > 0 ? $"; removed what it kept alive: {string.Join(", ", killed.ToArray())}" : ""));
        }

        // The roots in DontDestroyOnLoad (active ones; that's where a scene's singletons put themselves).
        private static HashSet<GameObject> PersistentRoots()
        {
            var roots = new HashSet<GameObject>();
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
                if (t.parent == null && t.gameObject.scene.buildIndex < 0 && t.gameObject.scene.name == "DontDestroyOnLoad") roots.Add(t.gameObject);
            return roots;
        }

        internal static void Patch(Harmony harmony)
        {
            // The creator swaps the face material when you change look, head or hair: hide the avatar's own
            // head again, keeping the new material for when the Klingon head comes off.
            foreach (var name in new[] { "DNNOHHKPGDA", "CAJFLMIJGLL" })
            {
                var method = AccessTools.Method(typeof(FaceCustomization), name);
                if (method != null) harmony.Patch(method, postfix: new HarmonyMethod(typeof(KlingonHead), nameof(Swapped_Postfix)));
            }
        }

        private static void Swapped_Postfix(FaceCustomization __instance)
        {
            var head = __instance.GetComponent<SkinnedMeshRenderer>();
            if (head != null && grafts.TryGetValue(head, out var graft) && graft != null) graft.Rehide();
        }
    }

    /// <summary>
    /// A grafted Klingon head: follows the avatar's own head each frame. That head keeps its materials (the
    /// character creator changes them: look, skin, hair colour) and is only left out while cameras draw; its skin
    /// and hair settings are copied onto the Klingon head's skin, and its face shapes (speech, blinks, and the
    /// creator's sliders once KlingonFace has made them) by name.
    /// </summary>
    internal sealed class KlingonHeadGraft : MonoBehaviour
    {
        internal int Variant;
        private SkinnedMeshRenderer original, worn;
        private int[] shapes; // the worn head's face shape for each of the original's, or -1
        private MaterialPropertyBlock block;
        private Material skin;  // the worn head's own copy of its skin material
        private Mesh madeMesh;  // its mesh with the face sliders (KlingonFace), destroyed with it
        private bool wasEnabled;

        private static readonly HashSet<KlingonHeadGraft> live = new HashSet<KlingonHeadGraft>();
        private static bool hooked;

        // The skin shader's settings the creator changes (FaceCustomization: SetSkinParams, SetSkinTexColor,
        // ChangeHairColor, SetHairColorMultiplier); the Klingon head has the same shader.
        private static readonly string[] skinColors = { "_MainTexColor", "_SkinColor1", "_SkinColor2", "_TransmissionColor", "_HairColor" };
        private static readonly string[] skinFloats = { "_HairIntensity", "_ScatteringSpread", "_NormalSoftening", "_SpecularIntensity", "_SmoothnessModifier" };

        internal void Set(SkinnedMeshRenderer head, SkinnedMeshRenderer klingon, int variant, Material hide)
        {
            original = head;
            worn = klingon;
            Variant = variant;
            block = new MaterialPropertyBlock();
            var materials = worn.sharedMaterials;
            if (materials.Length > 0 && materials[0] != null)
            {
                skin = new Material(materials[0]) { name = materials[0].name + " (worn)" };
                materials[0] = skin;
                worn.sharedMaterials = materials;
            }
            MapShapes();
            live.Add(this);
            Hook();
            LateUpdate();
        }

        /// <summary>The Klingon head's mesh with the creator's face sliders on it (from KlingonFace).</summary>
        internal void UseMesh(Mesh mesh)
        {
            if (worn == null) { Destroy(mesh); return; }
            worn.sharedMesh = mesh;
            if (madeMesh != null) Destroy(madeMesh);
            madeMesh = mesh;
            MapShapes();
        }

        private void MapShapes()
        {
            var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < worn.sharedMesh.blendShapeCount; i++) names[worn.sharedMesh.GetBlendShapeName(i)] = i;
            int count = original.sharedMesh != null ? original.sharedMesh.blendShapeCount : 0;
            shapes = new int[count];
            for (int i = 0; i < count; i++) shapes[i] = names.TryGetValue(original.sharedMesh.GetBlendShapeName(i), out var j) ? j : -1;
        }

        // The avatar's own head is left out of every camera's picture (and its shadows) while the Klingon head is on.
        private static void Hook()
        {
            if (hooked) return;
            hooked = true;
            // Whether it's shown is taken once a frame, before any camera (LateUpdate): a camera drawn in the middle
            // of another's picture would otherwise take "off" as the state to put back, and the head stayed off.
            Camera.onPreCull += c =>
            {
                foreach (var g in live)
                    if (g != null && g.original != null) g.original.enabled = false;
            };
            Camera.onPostRender += c =>
            {
                foreach (var g in live)
                    if (g != null && g.original != null) g.original.enabled = g.wasEnabled;
            };
        }

        // The creator used to hide the avatar's head by its materials; they're left alone now.
        internal void Rehide() { }

        private void OnDestroy()
        {
            live.Remove(this);
            if (skin != null) Destroy(skin);
            if (madeMesh != null) Destroy(madeMesh);
        }

        internal void Remove()
        {
            live.Remove(this);
            original = null;
            // Out of the avatar first (it's destroyed at the end of the frame), so its lighting lists it no more.
            var avatar = transform.parent;
            transform.SetParent(null, false);
            gameObject.SetActive(false);
            KlingonHead.ForgetRenderers(avatar);
            Destroy(gameObject);
        }

        private void LateUpdate()
        {
            if (original == null)
            {
                Destroy(gameObject);
                return;
            }
            wasEnabled = original.enabled;
            worn.enabled = wasEnabled;
            if (gameObject.layer != original.gameObject.layer) gameObject.layer = original.gameObject.layer;
            worn.shadowCastingMode = original.shadowCastingMode;
            worn.receiveShadows = original.receiveShadows;
            for (int i = 0; i < shapes.Length; i++)
                if (shapes[i] >= 0) worn.SetBlendShapeWeight(shapes[i], original.GetBlendShapeWeight(i));
            original.GetPropertyBlock(block);
            worn.SetPropertyBlock(block);
            var face = original.sharedMaterial;
            if (skin != null && face != null)
            {
                foreach (var p in skinColors) if (face.HasProperty(p) && skin.HasProperty(p)) skin.SetColor(p, face.GetColor(p));
                foreach (var p in skinFloats) if (face.HasProperty(p) && skin.HasProperty(p)) skin.SetFloat(p, face.GetFloat(p));
            }
        }
    }
}
