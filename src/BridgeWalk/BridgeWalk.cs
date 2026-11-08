using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using SVR;
using UnityEngine;

namespace StbcRevival.Walking
{
    /// <summary>
    /// [Bridge Walk] Experimental, 2D (monitor) mode only: stand up from your seat with ToggleKey (F6) and walk
    /// around the bridge (WASD/arrow keys, mouse to look, Shift to run, ToggleKey or Escape to sit down again).
    /// Crewmates with the mod see you stand and walk, with a procedural walk cycle on the character's humanoid rig.
    ///
    /// The game's own 2D camera and console input are paused while standing. Floors and walls come from the
    /// bridge's colliders and, since some bridges (the Enterprise-D) have hardly any, from the bridge's visible
    /// meshes as well (read from the game's asset files when they aren't readable in memory, see BridgeMeshes).
    /// Position, yaw, pitch and speed go out 15 times a second as event 168.
    /// Known limitation: the viewscreen still shows the picture for the seated view while you walk around.
    /// </summary>
    internal static class BridgeWalk
    {
        internal static bool Active;

        internal static void Patch(Harmony harmony)
        {
            if (!Plugin.BridgeWalkEnabled.Value) return;
            WalkPatches.Apply(harmony);
            WalkNet.Hook();
            BridgeMeshes.StartIndex(Application.dataPath);
            Active = true;
        }

        internal static void Update()
        {
            if (!Active) return;
            if (Input.GetKeyDown(Plugin.BridgeWalkKey.Value))
            {
                if (Walker.Standing) Walker.SitDown("key");
                else Walker.StandUp();
            }
            if (Walker.Standing && Input.GetKeyDown(KeyCode.Escape)) Walker.SitDown("escape");
            Walker.Tick();
            Remotes.Tick();
        }
    }

    // ---------------------------------------------------------------- game access
    internal static class Bridge
    {
        public static bool OnBridge()
        {
            try
            {
                var gpm = GamePhaseManager.IEFBKOMBFGO;
                return gpm != null && gpm.GetCurrentGamePhase() == typeof(GamePhase_Bridge);
            }
            catch { return false; }
        }

        public static int LocalActor()
        {
            try { var p = NDIOGIGMPBH.DIMFCNEMMPD; return p == null ? -1 : p.KLNDGCGHADM; } catch { return -1; }
        }

        private static NovaPlayerInfo[] cache = new NovaPlayerInfo[0];
        private static float cacheAt = -10f;

        public static NovaPlayerInfo[] Players()
        {
            if (Time.unscaledTime - cacheAt > 1f)
            {
                cache = UnityEngine.Object.FindObjectsOfType<NovaPlayerInfo>();
                cacheAt = Time.unscaledTime;
            }
            return cache;
        }

        public static int ActorOf(NovaPlayerInfo p)
        {
            try { var o = p.PKIBEAIHJNE; return o == null ? -1 : o.KLNDGCGHADM; } catch { return -1; }
        }

        public static NovaPlayerInfo Local()
        {
            int id = LocalActor();
            if (id < 0) return null;
            foreach (var p in Players()) if (p != null && ActorOf(p) == id && p.PawnObject != null) return p;
            return null;
        }

        public static NovaPlayerInfo ByActor(int id)
        {
            foreach (var p in Players()) if (p != null && ActorOf(p) == id) return p;
            return null;
        }

        /// <summary>Puts a player's character back in its seat the way the game places it.</summary>
        public static void Reseat(NovaPlayerInfo p)
        {
            var st = p.Station as NovaStation;
            if (st != null && NovaSpawnManager.IEFBKOMBFGO != null)
                NovaSpawnManager.IEFBKOMBFGO.ApplyCharacterSeatingOffset(null, st, p);
        }

        public static string Path(Transform t)
        {
            var sb = new StringBuilder();
            for (; t != null; t = t.parent) sb.Insert(0, "/" + t.name);
            return sb.ToString();
        }
    }

    // ---------------------------------------------------------------- local player
    internal static class Walker
    {
        public static bool Standing;
        public static NovaPlayerInfo Me;
        public static float Speed;
        public static readonly Pose Pose = new Pose();

        private static Transform pawn;
        private static Transform cam;
        private static Vector3 seatLocalPos;
        private static Quaternion seatLocalRot;
        private static Vector3 camLocalPos;
        private static Quaternion camLocalRot;
        private static float yaw, pitch;
        private static bool floorFromGeometry;
        private static float flatHeight;
        private static bool mouseAxisOk = true;
        private static float nextSend;
        private static readonly HashSet<Collider> own = new HashSet<Collider>();

        // The bridge moves with the ship: everything is worked out relative to the character's parent.
        private static Transform Basis => pawn != null ? pawn.parent : null;
        private static Vector3 Up => Basis != null ? Basis.up : Vector3.up;
        private static Quaternion BasisRot => Basis != null ? Basis.rotation : Quaternion.identity;

        public static void StandUp()
        {
            if (!Bridge.OnBridge()) return;
            var me = Bridge.Local();
            if (me == null || me.PawnObject == null) { Plugin.Log.LogWarning("Bridge walk: your character wasn't found"); return; }
            var fps = me.PawnObject.GetComponentInChildren<FPSCamera>(true);
            if (fps == null || !fps.isActiveAndEnabled) { Plugin.Log.LogInfo("Bridge walk: only in 2D mode (no 2D camera active)"); return; }

            Me = me;
            pawn = me.PawnObject.transform;
            cam = fps.transform;
            seatLocalPos = pawn.localPosition;
            seatLocalRot = pawn.localRotation;
            camLocalPos = cam.localPosition;
            camLocalRot = cam.localRotation;
            own.Clear();
            foreach (var c in me.PawnObject.GetComponentsInChildren<Collider>(true)) own.Add(c);

            // Walk off in the direction the camera looks.
            Vector3 f = Quaternion.Inverse(BasisRot) * cam.forward;
            yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            pitch = 0f;
            Speed = 0f;

            Vector3 pos = pawn.position;
            if (Plugin.BridgeWalkCollisions.Value) { try { WalkMesh.Ensure(pos); } catch (Exception e) { Plugin.Log.LogWarning("Bridge walk: bridge geometry: " + e.Message); } }
            floorFromGeometry = false;
            if (Plugin.BridgeWalkCollisions.Value && FindFloor(pos, out var floor, nearestTo: true)) { pos = floor; floorFromGeometry = true; }
            flatHeight = Vector3.Dot(pos, Up);
            pawn.position = pos;
            // A step away from the chair, if there's room.
            TryMove(BasisRot * Quaternion.Euler(0, yaw, 0) * Vector3.forward * 0.45f);

            Standing = true;
            Pose.Reset();
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            Plugin.Log.LogInfo($"Bridge walk: stood up at {Bridge.Path(pawn)}; floor {(floorFromGeometry ? "from the bridge geometry" : "flat (no floor found)")}");
            WalkNet.SendState(true);
        }

        public static void SitDown(string why)
        {
            if (!Standing) return;
            Standing = false;
            Speed = 0f;
            try
            {
                if (pawn != null)
                {
                    pawn.localPosition = seatLocalPos;
                    pawn.localRotation = seatLocalRot;
                    try { Bridge.Reseat(Me); } catch (Exception e) { Plugin.Log.LogWarning("Bridge walk: reseating: " + e.Message); }
                }
                if (cam != null) { cam.localPosition = camLocalPos; cam.localRotation = camLocalRot; }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Bridge walk: sitting down: " + e.Message); }
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            Plugin.Log.LogInfo("Bridge walk: sat down (" + why + ")");
            WalkNet.SendState(true);
        }

        public static void Tick()
        {
            if (!Standing) return;
            if (pawn == null || cam == null || Me == null || !Bridge.OnBridge()) { SitDown("left the bridge"); return; }

            // Looking around
            float mx = 0, my = 0;
            if (mouseAxisOk)
            {
                try { mx = Input.GetAxisRaw("Mouse X"); my = Input.GetAxisRaw("Mouse Y"); }
                catch { mouseAxisOk = false; Plugin.Log.LogWarning("Bridge walk: no mouse axes; look around with Q/E and Page Up/Down"); }
            }
            float s = Plugin.BridgeWalkMouseSensitivity.Value;
            yaw += mx * s;
            pitch = Mathf.Clamp(pitch - my * s, -80f, 80f);
            if (Input.GetKey(KeyCode.Q)) yaw -= 90f * Time.deltaTime;
            if (Input.GetKey(KeyCode.E)) yaw += 90f * Time.deltaTime;
            if (Input.GetKey(KeyCode.PageUp)) pitch = Mathf.Max(-80f, pitch - 60f * Time.deltaTime);
            if (Input.GetKey(KeyCode.PageDown)) pitch = Mathf.Min(80f, pitch + 60f * Time.deltaTime);

            // Walking
            float v = (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow) ? 1 : 0) - (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow) ? 1 : 0);
            float h = (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow) ? 1 : 0) - (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow) ? 1 : 0);
            bool run = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            Quaternion yawRot = BasisRot * Quaternion.Euler(0, yaw, 0);
            Vector3 dir = yawRot * new Vector3(h, 0, v);
            if (dir.sqrMagnitude > 1f) dir.Normalize();
            float target = dir.sqrMagnitude > 0.01f ? (run ? Plugin.BridgeWalkRunSpeed.Value : Plugin.BridgeWalkSpeed.Value) : 0f;
            Vector3 before = pawn.position;
            if (target > 0f) TryMove(dir * target * Time.deltaTime);
            float actual = Time.deltaTime > 0 ? (pawn.position - before).magnitude / Time.deltaTime : 0f;
            Speed = Mathf.Lerp(Speed, actual, 1f - Mathf.Exp(-10f * Time.deltaTime));

            pawn.rotation = yawRot;
            PlaceCamera();

            if (Time.unscaledTime >= nextSend) { nextSend = Time.unscaledTime + 1f / 15f; WalkNet.SendState(false); }
        }

        public static void PlaceCamera()
        {
            if (!Standing || pawn == null || cam == null) return;
            Quaternion yawRot = BasisRot * Quaternion.Euler(0, yaw, 0);
            cam.position = pawn.position + Up * Plugin.BridgeWalkEyeHeight.Value + yawRot * Vector3.forward * 0.12f;
            cam.rotation = yawRot * Quaternion.Euler(pitch, 0, 0);
        }

        private static void TryMove(Vector3 delta)
        {
            Vector3 up = Up;
            delta -= up * Vector3.Dot(delta, up);
            if (delta.sqrMagnitude < 1e-8f) return;
            Vector3 pos = pawn.position;
            Vector3 next = pos + delta;
            if (Plugin.BridgeWalkCollisions.Value && Blocked(pos, delta))
            {
                // Slide along the wall: try each axis on its own.
                Vector3 right = BasisRot * Vector3.right, fwd = BasisRot * Vector3.forward;
                Vector3 a = right * Vector3.Dot(delta, right), b = fwd * Vector3.Dot(delta, fwd);
                if (!Blocked(pos, a)) next = pos + a;
                else if (!Blocked(pos, b)) next = pos + b;
                else return;
            }
            if (floorFromGeometry)
            {
                // Where the bridge has no floor, keep walking at the same height (MaxDistance keeps you on the bridge).
                if (!FindFloor(next, out var floor)) floor = next;
                float step = Vector3.Dot(floor - pos, up);
                if (step > 0.45f || step < -0.8f) return; // too high a step
                next = floor;
            }
            else next += up * (flatHeight - Vector3.Dot(next, up));
            Vector3 seatWorld = Basis != null ? Basis.TransformPoint(seatLocalPos) : seatLocalPos;
            Vector3 off = next - seatWorld; off -= up * Vector3.Dot(off, up);
            if (off.magnitude > Plugin.BridgeWalkMaxDistance.Value) return;
            pawn.position = next;
        }

        private static readonly float[] wallHeights = { 0.55f, 1.0f, 1.55f };

        private static bool Blocked(Vector3 pos, Vector3 delta)
        {
            float dist = delta.magnitude;
            if (dist < 1e-5f) return false;
            Vector3 up = Up;
            // The capsule's bottom is ~0.45 m above the feet: ramps and steps below it don't stop you, the floor
            // check in TryMove lifts the character onto them.
            const float r = 0.22f;
            Vector3 p1 = pos + up * (0.45f + r), p2 = pos + up * (1.75f - r);
            foreach (var hit in Physics.CapsuleCastAll(p1, p2, r, delta / dist, dist + 0.05f))
            {
                if (Ignore(hit.collider)) continue;
                if (hit.distance <= 0f && hit.point == Vector3.zero) continue; // already overlapping: don't get stuck
                return true;
            }
            // Right at your own seat the mesh walls aren't checked, or you'd be stuck between the armrests.
            Vector3 seatW = Basis != null ? Basis.TransformPoint(seatLocalPos) : seatLocalPos;
            Vector3 fromSeat = pos - seatW; fromSeat -= up * Vector3.Dot(fromSeat, up);
            if (WalkMesh.Ready && fromSeat.magnitude > 0.7f)
            {
                Vector3 dir = delta / dist, side = Vector3.Cross(up, dir).normalized * r;
                foreach (float h in wallHeights)
                {
                    Vector3 s = pos + up * h;
                    Vector3 e = s + dir * (dist + r);
                    if (WalkMesh.Hits(s, e) || WalkMesh.Hits(s + side, e + side) || WalkMesh.Hits(s - side, e - side)) return true;
                }
            }
            return false;
        }

        private static readonly List<float> meshHeights = new List<float>();

        /// <summary>nearestTo (standing up): the lowest walkable floor up to 1 m below, so you don't end up on the
        /// chair. Otherwise (walking): the floor nearest below knee height, so ramps and steps up are found.</summary>
        public static bool FindFloor(Vector3 at, out Vector3 floor, bool nearestTo = false)
        {
            Vector3 up = pawn != null ? Up : Vector3.up;
            floor = at;
            float best = float.MaxValue;
            bool found = false;
            const float above = 0.9f;
            foreach (var hit in Physics.RaycastAll(at + up * above, -up, 2.4f))
            {
                if (Ignore(hit.collider)) continue;
                if (Vector3.Dot(hit.normal, up) < 0.5f) continue; // walls and steep faces aren't floor
                if (nearestTo && hit.distance > above + 1.0f) continue;
                float score = nearestTo ? -hit.distance : hit.distance;
                if (score < best) { best = score; floor = hit.point; found = true; }
            }
            if (WalkMesh.Ready)
            {
                meshHeights.Clear();
                float top = at.y + above;
                WalkMesh.Floors(at, top, top - (nearestTo ? above + 1.0f : 2.4f), meshHeights);
                foreach (float y in meshHeights)
                {
                    float dist = top - y;
                    float score = nearestTo ? -dist : dist;
                    if (score < best) { best = score; floor = new Vector3(at.x, y, at.z); found = true; }
                }
            }
            return found;
        }

        private static readonly Dictionary<int, bool> ignoreCache = new Dictionary<int, bool>();
        private static readonly string[] ignoreParts = { "HandGuide", "Hand Guide", "Interface", "UIParent", "/FX/", "Particle", "LookAtCollider", "Interaction", "Trigger", "/Players/", "Canvas" };

        /// <summary>Colliders that are only there for the consoles' hand guides, UI or effects are neither walls nor floor.</summary>
        public static bool Ignore(Collider c)
        {
            if (c == null || c.isTrigger || own.Contains(c)) return true;
            int id = c.GetInstanceID();
            if (ignoreCache.TryGetValue(id, out var v)) return v;
            string path = Bridge.Path(c.transform);
            v = false;
            foreach (var p in ignoreParts) if (path.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) { v = true; break; }
            if (!v && c.attachedRigidbody != null && !c.attachedRigidbody.isKinematic) v = true; // moving parts
            ignoreCache[id] = v;
            return v;
        }

        public static void Pack(out Vector3 local, out float y, out float p)
        {
            local = Basis != null ? Basis.InverseTransformPoint(pawn.position) : pawn.position;
            y = yaw; p = pitch;
        }
    }

    // ---------------------------------------------------------------- crewmates
    internal sealed class Remote
    {
        public int Actor;
        public NovaPlayerInfo Info;
        public bool Standing;
        public Vector3 Target, Current;
        public float TargetYaw, CurrentYaw, Speed, Pitch;
        public bool HasCurrent;
        public float LastHeard;
        public readonly Pose Pose = new Pose();
    }

    internal static class Remotes
    {
        public static readonly Dictionary<int, Remote> All = new Dictionary<int, Remote>();

        public static void Receive(int actor, bool standing, Vector3 local, float yaw, float pitch, float speed)
        {
            if (!All.TryGetValue(actor, out var r)) { r = new Remote { Actor = actor }; All[actor] = r; }
            r.LastHeard = Time.unscaledTime;
            r.Target = local; r.TargetYaw = yaw; r.Speed = speed; r.Pitch = pitch;
            if (r.Standing && !standing)
            {
                r.Standing = false;
                var info = r.Info ?? Bridge.ByActor(actor);
                if (info != null) { try { Bridge.Reseat(info); } catch (Exception e) { Plugin.Log.LogWarning("Bridge walk: reseating a crewmate: " + e.Message); } }
            }
            else if (!r.Standing && standing)
            {
                r.Standing = true; r.HasCurrent = false; r.Pose.Reset();
            }
        }

        public static void Tick()
        {
            if (All.Count == 0) return;
            List<int> gone = null;
            foreach (var r in All.Values)
            {
                if (r.Info == null || r.Info.PawnObject == null) r.Info = Bridge.ByActor(r.Actor);
                if (r.Info == null) { if (Time.unscaledTime - r.LastHeard > 30f) (gone ?? (gone = new List<int>())).Add(r.Actor); continue; }
                if (!r.Standing) continue;
                var pawn = r.Info.PawnObject.transform;
                if (!r.HasCurrent) { r.Current = r.Target; r.CurrentYaw = r.TargetYaw; r.HasCurrent = true; }
                float k = 1f - Mathf.Exp(-12f * Time.deltaTime);
                r.Current = Vector3.Lerp(r.Current, r.Target, k);
                r.CurrentYaw = Mathf.LerpAngle(r.CurrentYaw, r.TargetYaw, k);
                pawn.localPosition = r.Current;
                pawn.localRotation = Quaternion.Euler(0, r.CurrentYaw, 0);
            }
            if (gone != null) foreach (var id in gone) All.Remove(id);
        }
    }

    // ---------------------------------------------------------------- network
    internal static class WalkNet
    {
        // Free: the game uses 0-125, 131, 179, 188, 193 and 200+; the mod 72, 86, 125, 150-152, 155, 226.
        internal const byte Code = 168;
        private const byte Proto = 1;

        public static void Hook() => NDIOGIGMPBH.OnEventCall += OnEvent;

        /// <summary>proto, standing, local position (3 floats), yaw, pitch, speed.</summary>
        public static void SendState(bool reliable)
        {
            try
            {
                var buf = new byte[2 + 6 * 4];
                buf[0] = Proto;
                buf[1] = (byte)(Walker.Standing ? 1 : 0);
                Vector3 local = Vector3.zero; float yaw = 0, pitch = 0;
                if (Walker.Standing) Walker.Pack(out local, out yaw, out pitch);
                int o = 2;
                Put(buf, ref o, local.x); Put(buf, ref o, local.y); Put(buf, ref o, local.z);
                Put(buf, ref o, yaw); Put(buf, ref o, pitch); Put(buf, ref o, Walker.Speed);
                NDIOGIGMPBH.RaiseEvent(Code, buf, reliable, new CGJCFMCOBMJ());
            }
            catch (Exception e) { Plugin.Log.LogDebug("Bridge walk: sending: " + e.Message); }
        }

        private static void Put(byte[] b, ref int o, float f) { Buffer.BlockCopy(BitConverter.GetBytes(f), 0, b, o, 4); o += 4; }
        private static float Get(byte[] b, ref int o) { float f = BitConverter.ToSingle(b, o); o += 4; return f; }

        private static void OnEvent(byte code, object content, int sender)
        {
            if (code != Code) return;
            try
            {
                var b = content as byte[];
                if (b == null || b.Length < 26 || b[0] != Proto) return;
                int o = 2;
                var local = new Vector3(Get(b, ref o), Get(b, ref o), Get(b, ref o));
                float yaw = Get(b, ref o), pitch = Get(b, ref o), speed = Get(b, ref o);
                if (float.IsNaN(local.x + local.y + local.z + yaw + pitch + speed)) return;
                Remotes.Receive(sender, b[1] == 1, local, yaw, pitch, Mathf.Clamp(speed, 0f, 10f));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Bridge walk: receiving: " + e.Message); }
        }
    }

    // ---------------------------------------------------------------- Harmony
    internal static class WalkPatches
    {
        public static void Apply(Harmony h)
        {
            // While standing: the game's 2D camera and its console/station input pause.
            Try(h, AccessTools.Method(typeof(FPSCamera), "BatchUpdate"), prefix: nameof(SkipWhileStanding));
            Try(h, AccessTools.Method(typeof(InputManager), "GetAction", new[] { typeof(LONCAPHEAPA) }), prefix: nameof(NoAction));
            Try(h, AccessTools.Method(typeof(InputManager), "PCADJIICOJC", new[] { typeof(LONCAPHEAPA) }), prefix: nameof(NoAction));
            Try(h, AccessTools.Method(typeof(InputManager), "GetAxis", new[] { typeof(FEKBGPPHGEP) }), prefix: nameof(NoAxis));
            Try(h, AccessTools.Method(typeof(InputManager), "FPHOJIKAACG", new[] { typeof(FEKBGPPHGEP) }), prefix: nameof(NoAxis));
            // The batched LateUpdate that runs the game's animation: the walk pose goes on after it.
            Try(h, AccessTools.Method(typeof(FOMLMDGCIAF), "LateUpdate"), postfix: nameof(AfterLateUpdate));
        }

        private static void Try(Harmony h, System.Reflection.MethodBase m, string prefix = null, string postfix = null)
        {
            if (m == null) { Plugin.Log.LogWarning($"Bridge walk: method for {prefix ?? postfix} not found"); return; }
            try
            {
                h.Patch(m,
                    prefix == null ? null : new HarmonyMethod(typeof(WalkPatches), prefix),
                    postfix == null ? null : new HarmonyMethod(typeof(WalkPatches), postfix));
            }
            catch (Exception e) { Plugin.Log.LogError($"Bridge walk: patching {m.DeclaringType?.Name}.{m.Name}: {e.Message}"); }
        }

        private static bool SkipWhileStanding() => !Walker.Standing;

        private static bool NoAction(ref bool __result)
        {
            if (!Walker.Standing) return true;
            __result = false;
            return false;
        }

        private static bool NoAxis(ref float __result)
        {
            if (!Walker.Standing) return true;
            __result = 0f;
            return false;
        }

        private static void AfterLateUpdate()
        {
            if (Walker.Standing || Remotes.All.Count > 0) BodyAnim.ApplyAll();
        }
    }
}
