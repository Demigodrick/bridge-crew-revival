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
    /// The viewscreen's outside view follows your eyes while you walk (ViewscreenRig).
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

        private static bool wasOnBridge;
        private static float nextPrewarm;

        internal static void Update()
        {
            if (!Active) return;
            bool onBridge = Bridge.OnBridge();
            if (wasOnBridge && !onBridge)
            {
                // Left the bridge: what was kept for its characters and geometry goes.
                BodyAnim.Clear();
                Walker.ClearCaches();
                WalkMesh.Clear();
            }
            wasOnBridge = onBridge;
            if (Walker.SwallowEscape && !Input.GetKey(KeyCode.Escape)) Walker.SwallowEscape = false;
            // Keys typed into the mod's text prompt aren't for walking.
            bool typing = TextPrompt.IsOpen;
            if (!typing && Input.GetKeyDown(Plugin.BridgeWalkKey.Value))
            {
                if (Walker.Standing) Walker.SitDown("key");
                else Walker.StandUp();
            }
            if (!typing && Walker.Standing && Input.GetKeyDown(KeyCode.Escape)) Walker.SitDown("escape");
            if (onBridge && !Walker.Standing && Time.unscaledTime >= nextPrewarm)
            {
                nextPrewarm = Time.unscaledTime + 2f;
                Walker.Prewarm();
            }
            Walker.Tick(typing);
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
        private static CursorLockMode savedLockMode;
        private static bool savedCursorVisible;
        /// <summary>Sat down with Escape: the game's menu actions stay off until Escape is let go.</summary>
        public static bool SwallowEscape;

        // The bridge moves with the ship: everything is worked out relative to the character's parent.
        private static Transform Basis => pawn != null ? pawn.parent : null;
        private static Vector3 Up => Basis != null ? Basis.up : Vector3.up;
        private static Quaternion BasisRot => Basis != null ? Basis.rotation : Quaternion.identity;
        private static Vector3 ToLocal(Vector3 world) => Basis != null ? Basis.InverseTransformPoint(world) : world;
        private static Vector3 ToWorld(Vector3 local) => Basis != null ? Basis.TransformPoint(local) : local;

        /// <summary>On the bridge, seated in 2D mode: the bridge's meshes are read ahead, so they're there when you stand up.</summary>
        public static void Prewarm()
        {
            if (!Plugin.BridgeWalkCollisions.Value) return;
            try
            {
                var me = Bridge.Local();
                if (me == null || me.PawnObject == null) return;
                var fps = me.PawnObject.GetComponentInChildren<FPSCamera>(true);
                if (fps == null || !fps.isActiveAndEnabled) return;
                var t = me.PawnObject.transform;
                WalkMesh.Ensure(t.position, t.parent);
            }
            catch (Exception e) { Plugin.Log.LogDebug("Bridge walk: reading the bridge ahead: " + e.Message); }
        }

        public static void ClearCaches() => ignoreCache.Clear();

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
            try { ViewscreenRig.Capture(cam, Basis); } catch (Exception e) { Plugin.Log.LogWarning("Bridge walk: viewscreen: " + e.Message); }            own.Clear();
            foreach (var c in me.PawnObject.GetComponentsInChildren<Collider>(true)) own.Add(c);

            // Walk off in the direction the camera looks.
            Vector3 f = Quaternion.Inverse(BasisRot) * cam.forward;
            yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            pitch = 0f;
            Speed = 0f;

            Vector3 pos = pawn.position;
            if (Plugin.BridgeWalkCollisions.Value) { try { WalkMesh.Ensure(pos, Basis); } catch (Exception e) { Plugin.Log.LogWarning("Bridge walk: bridge geometry: " + e.Message); } }
            floorFromGeometry = false;
            if (Plugin.BridgeWalkCollisions.Value && FindFloor(pos, out var floor, nearestTo: true)) { pos = floor; floorFromGeometry = true; }
            flatHeight = Vector3.Dot(pos, Up);
            pawn.position = pos;
            // A step away from the chair, if there's room.
            TryMove(BasisRot * Quaternion.Euler(0, yaw, 0) * Vector3.forward * 0.45f);

            Standing = true;
            SwallowEscape = false;
            Pose.Reset();
            OtherConsoles.Show();
            savedLockMode = Cursor.lockState;
            savedCursorVisible = Cursor.visible;
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
                ViewscreenRig.Restore();
            }
            catch (Exception e) { Plugin.Log.LogWarning("Bridge walk: sitting down: " + e.Message); }
            OtherConsoles.Restore();
            if (why == "escape") SwallowEscape = true;
            Cursor.lockState = savedLockMode;
            Cursor.visible = savedCursorVisible;
            Plugin.Log.LogInfo("Bridge walk: sat down (" + why + ")");
            WalkNet.SendState(true);
        }

        public static void Tick(bool typing)
        {
            if (!Standing) return;
            if (pawn == null || cam == null || Me == null || !Bridge.OnBridge()) { SitDown("left the bridge"); return; }
            // The bridge's meshes finished reading after you stood up: from now on they carry you.
            if (!floorFromGeometry && Plugin.BridgeWalkCollisions.Value && WalkMesh.ReadyFor(Basis)) floorFromGeometry = true;
            if (typing)
            {
                Speed = Mathf.Lerp(Speed, 0f, 1f - Mathf.Exp(-10f * Time.deltaTime));
                PlaceCamera();
                if (Time.unscaledTime >= nextSend) { nextSend = Time.unscaledTime + 1f / 15f; WalkNet.SendState(false); }
                return;
            }

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
            ViewscreenRig.Follow(cam, Basis);
        }

        private static void TryMove(Vector3 delta)
        {
            Vector3 up = Up;
            delta -= up * Vector3.Dot(delta, up);
            if (delta.sqrMagnitude < 1e-8f) return;
            Vector3 pos = pawn.position;
            Vector3 next = pos + delta;
            bool walls = Plugin.BridgeWalkCollisions.Value && Plugin.BridgeWalkWalls.Value;
            if (walls && Blocked(pos, delta))
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
                if (step > 0.45f || step < -0.8f)
                {
                    if (walls) return; // too high a step
                    // Walls off: through the desk or wall at the same height.
                }
                else next = floor;
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
            if (WalkMesh.ReadyFor(Basis) && fromSeat.magnitude > 0.7f)
            {
                Vector3 dir = delta / dist, side = Vector3.Cross(up, dir).normalized * r;
                foreach (float h in wallHeights)
                {
                    Vector3 s = pos + up * h;
                    Vector3 e = s + dir * (dist + r);
                    if (WalkMesh.Hits(ToLocal(s), ToLocal(e)) || WalkMesh.Hits(ToLocal(s + side), ToLocal(e + side)) || WalkMesh.Hits(ToLocal(s - side), ToLocal(e - side))) return true;
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
            if (WalkMesh.ReadyFor(Basis))
            {
                // The mesh floors are in the bridge's space, where up is +y.
                meshHeights.Clear();
                Vector3 topW = at + up * above;
                Vector3 lp = ToLocal(at);
                float top = ToLocal(topW).y, bottom = ToLocal(topW - up * (nearestTo ? above + 1.0f : 2.4f)).y;
                WalkMesh.Floors(lp, top, bottom, meshHeights);
                foreach (float y in meshHeights)
                {
                    Vector3 fw = ToWorld(new Vector3(lp.x, y, lp.z));
                    float dist = Vector3.Dot(topW - fw, up);
                    float score = nearestTo ? -dist : dist;
                    if (score < best) { best = score; floor = fw; found = true; }
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

    // ---------------------------------------------------------------- the viewscreen
    /// <summary>
    /// In its normal mode the viewscreen is a window: the game's external camera rigs (OrientExternalCamera, the
    /// space view and its 3D-skybox copy) are placed for your seat (the pivot at (viewscreen - station) / 10, turned
    /// to the station's facing), and their camera copies the seated camera's local rotation each frame; a
    /// bridge-shaped proxy masks all but the glass. Standing, they'd stay at the seat, so the picture outside
    /// slid around as you walked. Here the pivot moves with your eyes by the same rule, and the camera takes your
    /// view relative to how the seat faced; sitting down puts the pivot back.
    /// </summary>
    internal static class ViewscreenRig
    {
        private sealed class Saved
        {
            public OrientExternalCamera Rig; public Transform Pivot; public Vector3 Pos; public Quaternion Rot; public MeshRenderer Proxy; public bool ProxyOn;
            public Camera[] Cameras; public float[] Near;
        }

        // Seated, nothing of your own ship is ever close to the outside view's camera, so its near plane sits far out
        // (1 in space units, 10 m on the bridge's scale). Walking up to the Aegis's window, the hull below is nearer
        // than that and gets cut off: standing, the near plane comes in close.
        private const float StandingNear = 0.05f;
        private static float nextHullCheck;

        // The rig draws a bridge-shaped mask first, depth only, with a hole cut where the viewscreen is as seen from
        // your seat: space shows only through that hole. Close up it's too small (a cropped rectangle inside the
        // viewscreen's frame), so standing it's switched off; the bridge itself covers everything but the viewscreen.
        private static readonly System.Reflection.FieldInfo proxyField = AccessTools.Field(typeof(OrientExternalCamera), "_bridgeViewOcclusionProxy");

        private static readonly System.Reflection.FieldInfo pivotField = AccessTools.Field(typeof(OrientExternalCamera), "_pivot");
        private static readonly System.Reflection.FieldInfo cameraField = AccessTools.Field(typeof(OrientExternalCamera), "BJGOCLMLBLD");     // _camera
        private static readonly System.Reflection.FieldInfo spaceViewField = AccessTools.Field(typeof(OrientExternalCamera), "CLJLBPKOEFE");  // _spaceViewOn
        private static readonly System.Reflection.FieldInfo spectatorField = AccessTools.Field(typeof(OrientExternalCamera), "DJPHKDPIDKJ");  // _spectatorExternalTarget
        private static readonly List<Saved> saved = new List<Saved>();
        private static Quaternion seatParentLocal = Quaternion.identity; // the seated camera's parent, in bridge space
        private static Vector3 seatEyeLocal;                               // the seated camera, in bridge space
        private static bool warned;

        public static void Capture(Transform cam, Transform basis)
        {
            saved.Clear();
            if (pivotField == null || cameraField == null)
            {
                if (!warned) { warned = true; Plugin.Log.LogWarning("Bridge walk: the viewscreen camera's fields weren't found; it stays set for your seat"); }
                return;
            }
            Quaternion basisRot = basis != null ? basis.rotation : Quaternion.identity;
            seatParentLocal = Quaternion.Inverse(basisRot) * (cam.parent != null ? cam.parent.rotation : Quaternion.identity);
            seatEyeLocal = basis != null ? basis.InverseTransformPoint(cam.position) : cam.position;
            foreach (var rig in UnityEngine.Object.FindObjectsOfType<OrientExternalCamera>())
            {
                var pivot = pivotField.GetValue(rig) as Transform;
                if (pivot == null) continue;
                var proxy = proxyField != null ? proxyField.GetValue(rig) as MeshRenderer : null;
                var cams = rig.GetComponentsInChildren<Camera>(true);
                var near = new float[cams.Length];
                for (int i = 0; i < cams.Length; i++) near[i] = cams[i].nearClipPlane;
                saved.Add(new Saved { Rig = rig, Pivot = pivot, Pos = pivot.localPosition, Rot = pivot.localRotation, Proxy = proxy, ProxyOn = proxy != null && proxy.enabled, Cameras = cams, Near = near });
            }
        }

        // After the game's own BatchLateUpdate (which sets the camera from the seated camera) and in Update.
        public static void Follow(Transform cam, Transform basis)
        {
            if (saved.Count == 0) return;
            Quaternion basisRot = basis != null ? basis.rotation : Quaternion.identity;
            Quaternion seatParent = basisRot * seatParentLocal;
            Vector3 seatEye = basis != null ? basis.TransformPoint(seatEyeLocal) : seatEyeLocal;
            Vector3 moved = cam.position - seatEye;
            Quaternion look = Quaternion.Inverse(seatParent) * cam.rotation;
            // Only the normal window view hides the own hull; the viewscreen's outside camera and other views show it.
            // The game itself switches the bridge mask on only for the normal view (OrientExternalCamera.DKONHDLFEBM:
            // view mode Normal, no space view, ...), so a rig whose mask is switched off isn't looking out the window.
            bool outside = false;
            foreach (var s in saved)
            {
                if (s.Rig == null) continue;
                if (s.Proxy != null) { if (!s.Proxy.gameObject.activeSelf) outside = true; }
                else if ((spaceViewField != null && spaceViewField.GetValue(s.Rig) is bool sv && sv)
                    || (spectatorField != null && spectatorField.GetValue(s.Rig) as Transform != null)) outside = true;
            }
            if (outside) { if (OwnHull.Hidden) OwnHull.Restore(); nextHullCheck = 0f; } // back to the window: hide again at once
            else if (!outside && !OwnHull.Hidden && Time.unscaledTime >= nextHullCheck) { nextHullCheck = Time.unscaledTime + 2f; OwnHull.Hide(); }
            foreach (var s in saved)
            {
                if (s.Rig == null || s.Pivot == null) continue;
                // The space view (pivot elsewhere) and spectator views aren't the seat's window: left alone.
                if (spaceViewField != null && spaceViewField.GetValue(s.Rig) is bool on && on) continue;
                if (spectatorField != null && spectatorField.GetValue(s.Rig) as Transform != null) continue;
                s.Pivot.localPosition = s.Pos - moved / 10f;
                s.Pivot.localRotation = s.Rot;
                if (s.Proxy != null && s.Proxy.enabled) s.Proxy.enabled = false;
                for (int i = 0; i < s.Cameras.Length; i++)
                    if (s.Cameras[i] != null && s.Cameras[i].nearClipPlane > StandingNear) s.Cameras[i].nearClipPlane = StandingNear;
                var c = cameraField.GetValue(s.Rig) as Camera;
                if (c != null) c.transform.localRotation = look;
            }
        }

        public static void Restore()
        {
            foreach (var s in saved)
            {
                if (s.Proxy != null && s.ProxyOn) s.Proxy.enabled = true;
                if (s.Cameras != null)
                    for (int i = 0; i < s.Cameras.Length; i++) if (s.Cameras[i] != null) s.Cameras[i].nearClipPlane = s.Near[i];
                if (s.Rig == null || s.Pivot == null) continue;
                if (spaceViewField != null && spaceViewField.GetValue(s.Rig) is bool on && on) continue;
                s.Pivot.localPosition = s.Pos;
                s.Pivot.localRotation = s.Rot;
            }
            saved.Clear();
            OwnHull.Restore();
        }
    }

    /// <summary>
    /// The Enterprise-D's viewscreen looks out from inside the saucer: seated, the rig's bridge-shaped mask hides the
    /// ship's own hull, but with the mask off for walking (ViewscreenRig) the saucer's top fills the view. On that
    /// bridge your own ship's model is hidden from your view while you stand. The Aegis looks out over its own hull
    /// through its window on purpose, so its hull stays.
    /// </summary>
    internal static class OwnHull
    {
        private static readonly List<Renderer> hidden = new List<Renderer>();
        internal static bool Hidden => hidden.Count > 0;

        internal static void Hide()
        {
            Restore();
            var ship = UnityEngine.Object.FindObjectOfType<Sector.Spacecraft.PlayerSpacecraft>();
            if (ship == null) return;
            // The ship object is "Enterprise D_", its model "Body - 1701D(Clone)".
            bool enterpriseD = ship.name.IndexOf("Enterprise D", StringComparison.OrdinalIgnoreCase) >= 0 || ship.name.IndexOf("1701D", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!enterpriseD)
                foreach (Transform child in ship.transform)
                    if (child.name.IndexOf("1701D", StringComparison.OrdinalIgnoreCase) >= 0) { enterpriseD = true; break; }
            if (!enterpriseD) return;
            foreach (var r in ship.GetComponentsInChildren<Renderer>(false))
            {
                if (r == null || !r.enabled || r is ParticleSystemRenderer) continue;
                string path = Bridge.Path(r.transform);
                if (path.IndexOf("CameraRig", StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf("Effect", StringComparison.OrdinalIgnoreCase) >= 0
                    || path.IndexOf("FX", StringComparison.Ordinal) >= 0 || path.IndexOf("Shield", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                r.enabled = false;
                hidden.Add(r);
            }
            if (hidden.Count > 0) Plugin.Log.LogInfo($"Bridge walk: your ship's hull hidden from the viewscreen while standing ({hidden.Count} parts of {ship.name})");
        }

        internal static void Restore()
        {
            foreach (var r in hidden) if (r != null) r.enabled = true;
            hidden.Clear();
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

        private const float MaxOffset = 500f;
        private static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);

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
                // Finite, and within reach of a bridge (positions are relative to the character's parent).
                if (!Finite(local.x) || !Finite(local.y) || !Finite(local.z) || !Finite(yaw) || !Finite(pitch) || !Finite(speed)) return;
                if (local.sqrMagnitude > MaxOffset * MaxOffset) return;
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

        private static bool NoAction(LONCAPHEAPA __0, ref bool __result)
        {
            if (!Blocks(__0)) return true;
            __result = false;
            return false;
        }

        // Standing: the station and menu actions are off (Escape is for sitting down), but push-to-talk and the
        // "which device is in use" checks still work. Just after sitting down with Escape, the menu actions stay
        // off until Escape is let go, so the same press doesn't open the game's menu.
        private static bool Blocks(LONCAPHEAPA a)
        {
            if (Walker.Standing)
                return a != LONCAPHEAPA.VoiceCommand && a != LONCAPHEAPA.KeyboardAny && a != LONCAPHEAPA.MouseAny && a != LONCAPHEAPA.GamePadAny
                    && a != LONCAPHEAPA.TouchControllersAny && a != LONCAPHEAPA.ViveControllersAny && a != LONCAPHEAPA.MoveControllersAny;
            if (Walker.SwallowEscape)
                return a == LONCAPHEAPA.PauseMenuMode || a == LONCAPHEAPA.BackOrCancel || a == LONCAPHEAPA.CloseAllMenus;
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
