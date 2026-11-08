using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace StbcRevival.Walking
{
    internal sealed class Pose
    {
        public float Phase;
        public float Amp;
        public float Time;
        public float Lift;
        public bool HasLift;

        public void Reset() { Phase = 0; Amp = 0; HasLift = false; }
    }

    /// <summary>
    /// The standing and walking pose, procedural: a standing pose in Mecanim muscle values, plus a walk cycle (hips,
    /// knees, feet, arm swing, spine twist and breathing) set through a HumanPoseHandler after the game's own
    /// animation each frame. The hips are then moved so the lower foot stays on the floor.
    /// </summary>
    internal static class BodyAnim
    {
        private static readonly Dictionary<Animator, HumanPoseHandler> handlers = new Dictionary<Animator, HumanPoseHandler>();
        private static readonly HashSet<Animator> warned = new HashSet<Animator>();
        private static Dictionary<string, int> muscles;
        private static Dictionary<int, float> standing;
        private static HumanPose pose = new HumanPose();

        private static readonly Dictionary<string, float> standingPose = new Dictionary<string, float>
        {
            { "Spine Front-Back", 0f }, { "Spine Left-Right", 0f }, { "Spine Twist Left-Right", 0f },
            { "Chest Front-Back", 0f }, { "Chest Left-Right", 0f }, { "Chest Twist Left-Right", 0f },
            { "Neck Nod Down-Up", 0f }, { "Neck Tilt Left-Right", 0f }, { "Neck Turn Left-Right", 0f },
            { "Head Nod Down-Up", 0f }, { "Head Tilt Left-Right", 0f }, { "Head Turn Left-Right", 0f },
            { "Upper Leg Front-Back", 0.02f }, { "Upper Leg In-Out", 0.05f }, { "Upper Leg Twist In-Out", 0f },
            { "Lower Leg Stretch", 0.95f }, { "Lower Leg Twist In-Out", 0f },
            { "Foot Up-Down", 0.05f }, { "Foot Twist In-Out", 0f }, { "Toes Up-Down", 0f },
            { "Shoulder Down-Up", 0f }, { "Shoulder Front-Back", 0f },
            { "Arm Down-Up", -0.6f }, { "Arm Front-Back", 0.25f }, { "Arm Twist In-Out", 0f },
            { "Forearm Stretch", 0.85f }, { "Forearm Twist In-Out", 0f },
            { "Hand Down-Up", 0f }, { "Hand In-Out", 0f },
        };

        private static void Init()
        {
            if (muscles != null) return;
            muscles = new Dictionary<string, int>();
            var names = HumanTrait.MuscleName;
            for (int i = 0; i < names.Length; i++) muscles[names[i]] = i;
            standing = new Dictionary<int, float>();
            foreach (var kv in standingPose)
            {
                Set(kv.Key, kv.Value);
                Set("Left " + kv.Key, kv.Value);
                Set("Right " + kv.Key, kv.Value);
            }
        }

        private static void Set(string name, float v) { if (muscles.TryGetValue(name, out var i)) standing[i] = v; }
        private static void M(string name, float v) { if (muscles.TryGetValue(name, out var i) && i < pose.muscles.Length) pose.muscles[i] = v; }
        private static float B(string name) => muscles.TryGetValue(name, out var i) && standing.TryGetValue(i, out var v) ? v : 0f;

        /// <summary>Runs after all the game's animation (the end of its batched LateUpdate).</summary>
        public static void ApplyAll()
        {
            if (Walker.Standing) Walker.PlaceCamera();
            if (!Plugin.BridgeWalkAnimate.Value) return;
            try
            {
                Init();
                if (Walker.Standing && Walker.Me != null && Walker.Me.VisualPawnObject != null && Walker.Me.PawnObject != null)
                    Apply(Walker.Me.VisualPawnObject, Walker.Me.PawnObject.transform, Walker.Pose, Walker.Speed, 0f);
                foreach (var r in Remotes.All.Values)
                    if (r.Standing && r.Info != null && r.Info.VisualPawnObject != null && r.Info.PawnObject != null)
                        Apply(r.Info.VisualPawnObject, r.Info.PawnObject.transform, r.Pose, r.Speed, r.Pitch);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Bridge walk: animation: " + e.Message); }
        }

        private static void Apply(GameObject visual, Transform root, Pose st, float speed, float pitch)
        {
            float dt = Time.deltaTime;
            float walk = Mathf.Max(0.1f, Plugin.BridgeWalkSpeed.Value);
            float targetAmp = Mathf.Clamp(speed / walk, 0f, 1.35f);
            st.Amp = Mathf.Lerp(st.Amp, targetAmp, 1f - Mathf.Exp(-6f * dt));
            // Stride grows with speed; one cycle is two steps.
            float stepLen = 0.55f + 0.18f * speed;
            st.Phase += 2f * Mathf.PI * (speed / (2f * stepLen)) * dt;
            // Coming to a stop, the legs ease back to standing.
            if (targetAmp < 0.05f) st.Phase = Mathf.Lerp(st.Phase, Mathf.Round(st.Phase / Mathf.PI) * Mathf.PI, 1f - Mathf.Exp(-4f * dt));
            if (st.Phase > 1000f) st.Phase -= 2f * Mathf.PI * 100f;
            st.Time += dt;

            foreach (var anim in visual.GetComponentsInChildren<Animator>())
            {
                if (anim == null || !anim.isActiveAndEnabled) continue;
                if (!anim.isHuman || anim.avatar == null)
                {
                    if (warned.Add(anim)) Plugin.Log.LogWarning($"Bridge walk: {Bridge.Path(anim.transform)} isn't humanoid; no walk animation");
                    continue;
                }
                if (!handlers.TryGetValue(anim, out var hph) || hph == null)
                {
                    hph = new HumanPoseHandler(anim.avatar, anim.transform);
                    handlers[anim] = hph;
                }
                hph.GetHumanPose(ref pose);
                if (pose.muscles == null) continue;

                foreach (var kv in standing) if (kv.Key < pose.muscles.Length) pose.muscles[kv.Key] = kv.Value;

                float a = st.Amp;
                float run = Mathf.Clamp01((a - 1f) / 0.35f);
                Leg("Left", st.Phase, a, run);
                Leg("Right", st.Phase + Mathf.PI, a, run);
                // Arms swing opposite to the leg on their side; the elbow bends on the forward swing.
                Arm("Left", st.Phase, a, run);
                Arm("Right", st.Phase + Mathf.PI, a, run);
                float cl = Mathf.Cos(st.Phase);
                float breathe = 0.015f * Mathf.Sin(st.Time * 1.6f) * (1f - Mathf.Clamp01(a));
                M("Spine Front-Back", B("Spine Front-Back") + 0.04f * a + 0.08f * run + breathe);
                M("Chest Front-Back", B("Chest Front-Back") + breathe);
                M("Spine Twist Left-Right", 0.05f * a * cl);
                M("Chest Twist Left-Right", -0.035f * a * cl);
                M("Spine Left-Right", 0.035f * a * Mathf.Sin(st.Phase));
                M("Head Nod Down-Up", Mathf.Clamp(-pitch / 60f, -1f, 1f) * 0.6f - 0.03f * a);
                M("Neck Turn Left-Right", -0.02f * a * cl);

                pose.bodyRotation = Quaternion.identity;
                pose.bodyPosition = new Vector3(0f, pose.bodyPosition.y, 0f);
                hph.SetHumanPose(ref pose);
                RestHelpers(anim, visual);
                Ground(anim, root, st);
            }
        }

        // A bell curve on the gait cycle (x and c in cycles, 0..1).
        private static float Bump(float x, float c, float w)
        {
            float d = x - c;
            d -= Mathf.Round(d);
            return Mathf.Exp(-(d * d) / (2f * w * w));
        }

        // Phase 0 = heel strike; stance until ~0.6, then swing.
        private static void Leg(string side, float phase, float a, float run)
        {
            float x = phase / (2f * Mathf.PI); x -= Mathf.Floor(x);
            float hip = 0.30f * Mathf.Cos(phase) + 0.04f + 0.08f * run * Mathf.Cos(phase);
            float knee = 0.12f * Bump(x, 0.12f, 0.07f) + (0.62f + 0.25f * run) * Bump(x, 0.72f, 0.09f);
            float foot = 0.12f * Bump(x, 0.0f, 0.06f) - 0.30f * Bump(x, 0.58f, 0.07f) + 0.10f * Bump(x, 0.82f, 0.1f);
            M(side + " Upper Leg Front-Back", B(side + " Upper Leg Front-Back") + a * hip);
            M(side + " Lower Leg Stretch", B(side + " Lower Leg Stretch") - a * knee * 1.5f);
            M(side + " Foot Up-Down", B(side + " Foot Up-Down") + a * foot);
            M(side + " Toes Up-Down", B(side + " Toes Up-Down") + a * 0.25f * Bump(x, 0.55f, 0.06f));
        }

        private static void Arm(string side, float phase, float a, float run)
        {
            float swing = -Mathf.Cos(phase); // forward while the leg on the same side is back
            M(side + " Arm Front-Back", B(side + " Arm Front-Back") + a * (0.26f + 0.12f * run) * swing);
            M(side + " Forearm Stretch", B(side + " Forearm Stretch") - a * (0.08f + 0.18f * Mathf.Max(0f, swing) + 0.35f * run));
            M(side + " Arm Down-Up", B(side + " Arm Down-Up") + 0.05f * a);
        }

        // Move the hips so the lower foot stands on the floor (the character root's height), centred over the root.
        private static void Ground(Animator anim, Transform root, Pose st)
        {
            var hips = anim.GetBoneTransform(HumanBodyBones.Hips);
            var lf = anim.GetBoneTransform(HumanBodyBones.LeftFoot);
            var rf = anim.GetBoneTransform(HumanBodyBones.RightFoot);
            if (hips == null || lf == null || rf == null) return;
            Vector3 up = root.up;
            float l = Vector3.Dot(lf.position - root.position, up);
            float r = Vector3.Dot(rf.position - root.position, up);
            float ankle = 0.085f * Mathf.Max(0.5f, anim.humanScale);
            float want = ankle - Mathf.Min(l, r);
            st.Lift = st.HasLift ? Mathf.Lerp(st.Lift, want, 1f - Mathf.Exp(-25f * Time.deltaTime)) : want;
            st.HasLift = true;
            hips.position += up * st.Lift;
            Vector3 feetMid = (lf.position + rf.position) * 0.5f - root.position; feetMid -= up * Vector3.Dot(feetMid, up);
            hips.position -= feetMid;
        }

        // The rig's helper bones (Sleeve_*, Stretchy_*, New*, *_Roll) are posed by the game for sitting and reaching
        // for consoles. Standing, they go back to their rest pose: taken from the character's prefab, or failing
        // that worked out from the meshes' bind poses relative to the parent bone.
        private sealed class Rest
        {
            public Transform[] Bones = new Transform[0];
            public Vector3[] Pos;
            public Quaternion[] Rot;
            public Vector3[] Scale;
        }

        private static readonly Dictionary<Animator, Rest> rests = new Dictionary<Animator, Rest>();
        private static Dictionary<string, GameObject> prefabs;

        /// <summary>Forgets the characters seen on the last bridge (on leaving it).</summary>
        public static void Clear()
        {
            handlers.Clear();
            warned.Clear();
            rests.Clear();
            prefabs = null;
        }

        private static void RestHelpers(Animator anim, GameObject visual)
        {
            if (!rests.TryGetValue(anim, out var r)) { r = BuildRest(anim, visual); rests[anim] = r; }
            for (int i = 0; i < r.Bones.Length; i++)
            {
                var b = r.Bones[i];
                if (b == null) continue;
                b.localPosition = r.Pos[i];
                b.localRotation = r.Rot[i];
                b.localScale = r.Scale[i];
            }
        }

        private static Rest BuildRest(Animator anim, GameObject visual)
        {
            var rest = new Rest();
            try
            {
                var human = new HashSet<Transform>();
                foreach (HumanBodyBones hb in Enum.GetValues(typeof(HumanBodyBones)))
                {
                    if (hb == HumanBodyBones.LastBone) continue;
                    var t = anim.GetBoneTransform(hb);
                    if (t != null) human.Add(t);
                }
                var list = new List<Transform>(); var pos = new List<Vector3>(); var rot = new List<Quaternion>(); var scl = new List<Vector3>();

                // First choice: the character's untouched prefab, where the helper bones are at rest.
                string baseName = visual.name.Replace("(Clone)", "").Trim();
                if (prefabs == null)
                {
                    // One look through all loaded objects per bridge, not one per character.
                    prefabs = new Dictionary<string, GameObject>();
                    foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                        if (go != null && go.transform.parent == null && !go.scene.IsValid() && !prefabs.ContainsKey(go.name)) prefabs[go.name] = go;
                }
                prefabs.TryGetValue(baseName, out var prefab);
                var hips = anim.GetBoneTransform(HumanBodyBones.Hips);
                if (prefab != null && hips != null)
                {
                    foreach (var t in hips.GetComponentsInChildren<Transform>(true))
                    {
                        if (human.Contains(t) || SkipHelper(t.name)) continue;
                        var pt = prefab.transform.Find(RelPath(t, visual.transform));
                        if (pt == null) continue;
                        list.Add(t); pos.Add(pt.localPosition); rot.Add(pt.localRotation); scl.Add(pt.localScale);
                    }
                }
                if (list.Count == 0)
                {
                    var bind = new Dictionary<Transform, Matrix4x4>();
                    var smrs = new List<SkinnedMeshRenderer>(visual.GetComponentsInChildren<SkinnedMeshRenderer>(true));
                    smrs.Sort((x, y) => y.gameObject.activeInHierarchy.CompareTo(x.gameObject.activeInHierarchy)); // visible ones first
                    foreach (var smr in smrs)
                    {
                        if (smr.sharedMesh == null) continue;
                        var bones = smr.bones; var poses = smr.sharedMesh.bindposes;
                        if (bones == null || poses == null) continue;
                        for (int i = 0; i < bones.Length && i < poses.Length; i++)
                            if (bones[i] != null && !bind.ContainsKey(bones[i])) bind[bones[i]] = poses[i];
                    }
                    foreach (var kv in bind)
                    {
                        var b = kv.Key;
                        if (human.Contains(b) || b.parent == null || !bind.ContainsKey(b.parent) || SkipHelper(b.name)) continue;
                        Matrix4x4 local = bind[b.parent] * kv.Value.inverse;
                        Vector3 c0 = local.GetColumn(0), c1 = local.GetColumn(1), c2 = local.GetColumn(2);
                        var s = new Vector3(c0.magnitude, c1.magnitude, c2.magnitude);
                        if (s.x < 1e-4f || s.y < 1e-4f || s.z < 1e-4f) continue;
                        list.Add(b);
                        pos.Add(local.GetColumn(3));
                        rot.Add(Quaternion.LookRotation(c2 / s.z, c1 / s.y));
                        scl.Add(s);
                    }
                }
                rest.Bones = list.ToArray(); rest.Pos = pos.ToArray(); rest.Rot = rot.ToArray(); rest.Scale = scl.ToArray();
                Plugin.Log.LogInfo($"Bridge walk: {list.Count} helper bones at rest (from {(prefab != null && hips != null && list.Count > 0 ? "prefab " + baseName : "bind poses")})");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Bridge walk: helper bones: " + e.Message); }
            return rest;
        }

        private static readonly string[] skipHelper = { "Finger", "Eye", "attach_point", "head parent", "head target", "POV", "eyesRoot" };

        private static bool SkipHelper(string n)
        {
            foreach (var s in skipHelper) if (n.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static string RelPath(Transform t, Transform root)
        {
            var sb = new StringBuilder();
            for (; t != null && t != root; t = t.parent) sb.Insert(0, sb.Length == 0 ? t.name : t.name + "/");
            return sb.ToString();
        }
    }
}
