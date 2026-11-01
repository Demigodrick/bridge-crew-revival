using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// [Avatar] KlingonHead: wear a Klingon captain's head (one of the two hail-screen characters) on a male
    /// Human avatar. Unlike the Romulan captain, who has the player's female head with her own face material,
    /// the Klingons have their own head mesh (Klingon_Head: ridges, hair, 18 face shapes), so it's grafted on:
    /// a copy of their head rides on the avatar's Head bone, placed so its eyes are where the avatar's eyes
    /// are (scaled to the same eye width), and the avatar's own head is drawn invisible. The avatar's face
    /// animation (speech, blinks, expressions) is copied onto it by shape name each frame, and its lighting,
    /// layer, shadows and visibility follow the avatar's head.
    /// The heads are in the bridge scenes' assets (the hail views' character prefabs), so they're picked up
    /// the first time a bridge loads and kept for the session; until then avatars keep their own heads.
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

        internal static void Update()
        {
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 1f;
            FindSources();

            bool inRoom = Publish();
            foreach (var info in UnityEngine.Object.FindObjectsOfType<PlayerInfo>())
                Apply(info.VisualPawnObject, Wants(info, inRoom));
            // The character creator's own copies of the presets (as RomulanFace).
            var creator = UnityEngine.Object.FindObjectOfType<CustomizationManager>();
            if (creator != null && RomulanFace.CreatorAvatars?.GetValue(creator) is GameObject[] avatars && RomulanFace.Profiles() is IList profiles)
                for (int i = 0; i < avatars.Length && i < profiles.Count; i++)
                    if (avatars[i] != null && avatars[i].activeInHierarchy)
                        Apply(avatars[i], MaleHuman(profiles[i] as CharacterProfile) ? Chosen() : 0);

            var gone = new List<SkinnedMeshRenderer>();
            foreach (var pair in grafts) if (pair.Key == null || pair.Value == null) gone.Add(pair.Key);
            foreach (var head in gone) grafts.Remove(head);
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
            if (want <= 0) return;
            if (sources[want] == null)
            {
                if (!toldNotLoaded) Plugin.Log.LogInfo($"Klingon head: {Names[want]} wanted on {avatar.name}, but the heads load with the bridge: it'll show once you've been on one");
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
                var bones = head.bones;
                int index = bones == null ? -1 : Array.FindIndex(bones, b => b != null && b.name == "Head");
                if (index < 0)
                {
                    Plugin.Log.LogWarning($"Klingon head: {avatar.name}'s head has no Head bone");
                    return null;
                }
                var bone = bones[index];
                var left = Find(bone, "Eye_L");
                var right = Find(bone, "Eye_R");
                if (left == null || right == null)
                {
                    Plugin.Log.LogWarning($"Klingon head: {avatar.name} has no eye bones; left alone");
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
        private static void FindSources()
        {
            if (sources[1] != null && sources[2] != null) return;
            if (Time.unscaledTime < nextSearch || CharacterPrefab == null) return;
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
                var headMid = head.sharedMesh.bounds.center;
                var headEyeMid = headBone.InverseTransformPoint((left.position + right.position) * 0.5f);
                var spacing = (headBone.InverseTransformPoint(left.position) - headBone.InverseTransformPoint(right.position)).magnitude;
                var poses = head.sharedMesh.bindposes;
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
                    Plugin.Log.LogWarning($"Klingon head: {character.name}: {poses.Length} bind poses, eye spacing {spacing}; can't place it");
                    return null;
                }
                var source = new Source
                {
                    Mesh = head.sharedMesh,
                    Materials = head.sharedMaterials,
                    MeshToHead = poses[best],
                    HeadEyeMid = headEyeMid,
                    HeadEyeSpacing = spacing,
                };
                if (bestMiss > 0.15f)
                    Plugin.Log.LogWarning($"Klingon head: {character.name}: no bind pose puts the head by the eyes (misses {string.Join(", ", misses.ToArray())} m); using the closest");
                // Kept for the session: the bridge's assets are unloaded with it.
                source.Mesh.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                foreach (var m in source.Materials) if (m != null) m.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                Plugin.Log.LogInfo($"Klingon head: found {character.name} ({source.Mesh.name}, {source.Mesh.vertexCount} vertices, {source.Mesh.blendShapeCount} face shapes; "
                                   + $"head bind pose {best} of {poses.Length} (head middle from the eyes {string.Join(", ", misses.ToArray())} m); eye bones {F(headEyeMid)} apart {spacing:F4}; mesh bounds {F(source.Mesh.bounds.center)} size {F(source.Mesh.bounds.size)})");
                return source;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Klingon head: reading {character.name}: {e.Message}");
                return null;
            }
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

    /// <summary>A grafted Klingon head: follows the avatar's own (hidden) head each frame.</summary>
    internal sealed class KlingonHeadGraft : MonoBehaviour
    {
        internal int Variant;
        private SkinnedMeshRenderer original, worn;
        private Material[] originalMaterials;
        private Material hidden;
        private int[] shapes; // the worn head's face shape for each of the original's, or -1
        private MaterialPropertyBlock block;

        internal void Set(SkinnedMeshRenderer head, SkinnedMeshRenderer klingon, int variant, Material hide)
        {
            original = head;
            worn = klingon;
            Variant = variant;
            hidden = hide;
            block = new MaterialPropertyBlock();
            var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < klingon.sharedMesh.blendShapeCount; i++) names[klingon.sharedMesh.GetBlendShapeName(i)] = i;
            int count = head.sharedMesh != null ? head.sharedMesh.blendShapeCount : 0;
            shapes = new int[count];
            for (int i = 0; i < count; i++) shapes[i] = names.TryGetValue(head.sharedMesh.GetBlendShapeName(i), out var j) ? j : -1;
            originalMaterials = head.sharedMaterials;
            Rehide();
            LateUpdate();
        }

        // Hide the avatar's head again; a material the game put on since is kept for taking the graft off.
        internal void Rehide()
        {
            if (original == null) return;
            var current = original.sharedMaterials;
            if (originalMaterials == null || originalMaterials.Length != current.Length) originalMaterials = (Material[])current.Clone();
            for (int i = 0; i < current.Length; i++)
            {
                if (current[i] != hidden) originalMaterials[i] = current[i];
                current[i] = hidden;
            }
            original.sharedMaterials = current;
        }

        internal void Remove()
        {
            if (original != null && originalMaterials != null) original.sharedMaterials = originalMaterials;
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
            worn.enabled = original.enabled;
            if (gameObject.layer != original.gameObject.layer) gameObject.layer = original.gameObject.layer;
            worn.shadowCastingMode = original.shadowCastingMode;
            worn.receiveShadows = original.receiveShadows;
            for (int i = 0; i < shapes.Length; i++)
                if (shapes[i] >= 0) worn.SetBlendShapeWeight(shapes[i], original.GetBlendShapeWeight(i));
            original.GetPropertyBlock(block);
            worn.SetPropertyBlock(block);
        }
    }
}
