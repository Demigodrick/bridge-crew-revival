using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using SVR;
using UnityEngine;

namespace StbcRevival.Walking
{
    /// <summary>
    /// [Bridge Walk] Experimental: stand up from your seat with ToggleKey (F6) and walk around the bridge, the shuttle
    /// or the briefing room (WASD/arrow keys, mouse to look, Shift to run, ToggleKey or Escape to sit down again). In VR
    /// (VrWalk.cs) the headset's tracking moves with you instead: sticks to walk and turn, B held to stand or sit.
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
            ShuttleView.Patch(harmony);
            SeatWarning.Patch(harmony);
            BridgeMeshes.StartIndex(Application.dataPath);
            Active = true;
        }

        private static Type lastPhase;
        private static float nextPrewarm;

        internal static void Update()
        {
            if (!Active) return;
            var phase = Bridge.Phase();
            bool walkable = Bridge.Walkable(phase);
            if (phase != lastPhase)
            {
                // Left the bridge, shuttle or briefing room: what was kept for its characters and geometry goes.
                BodyAnim.Clear();
                Walker.ClearCaches();
                WalkMesh.Clear();
            }
            lastPhase = phase;
            if (Walker.SwallowEscape && !Input.GetKey(KeyCode.Escape)) Walker.SwallowEscape = false;
            // Keys typed into the mod's text prompt aren't for walking.
            bool typing = TextPrompt.IsOpen;
            // In VR, holding B on the right controller does what the key does.
            bool held = Walker.VrOn && Walker.VrToggleHeld();
            if (!typing && (Input.GetKeyDown(Plugin.BridgeWalkKey.Value) || held))
            {
                if (Walker.Standing) Walker.SitDown("key");
                else Walker.StandUp();
            }
            if (!typing && Walker.Standing && Input.GetKeyDown(KeyCode.Escape)) Walker.SitDown("escape");
            if (walkable && !Walker.Standing && Time.unscaledTime >= nextPrewarm)
            {
                nextPrewarm = Time.unscaledTime + 2f;
                Walker.Prewarm();
            }
            Walker.Tick(typing);
            Remotes.Tick();
            TestCorridor.Tick(phase == typeof(GamePhase_Bridge));
        }
    }

    // ---------------------------------------------------------------- game access
    internal static class Bridge
    {
        /// <summary>The game phase now (GamePhase_Bridge, GamePhase_MainMenu: the shuttle, GamePhase_BriefingRoom...), or null.</summary>
        public static Type Phase()
        {
            try { var gpm = GamePhaseManager.IEFBKOMBFGO; return gpm == null ? null : gpm.GetCurrentGamePhase(); }
            catch { return null; }
        }

        /// <summary>Where you can stand up and walk: the bridge, the shuttle (main menu) and the briefing room.</summary>
        public static bool Walkable(Type phase) => phase == typeof(GamePhase_Bridge) || phase == typeof(GamePhase_MainMenu) || phase == typeof(GamePhase_BriefingRoom);

        public static string PlaceName(Type phase) => phase == typeof(GamePhase_Bridge) ? "the bridge" : phase == typeof(GamePhase_MainMenu) ? "the shuttle" : phase == typeof(GamePhase_BriefingRoom) ? "the briefing room" : (phase?.Name ?? "nowhere");

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
            if (id >= 0) foreach (var p in Players()) if (p != null && ActorOf(p) == id && p.PawnObject != null) return p;
            // Not matched by network ID (in the shuttle before a room is joined, say): the game's own local player.
            try { var local = NovaPlayerManager.IEFBKOMBFGO?.AJPNCBOKCLC; if (local != null && local.PawnObject != null) return local; } catch { }
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
    internal static partial class Walker
    {
        public static bool Standing;
        /// <summary>Standing on the bridge (not the shuttle or briefing room): the viewscreen and the other consoles join in.</summary>
        private static bool onBridge;
        private static Type standPhase;
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
                if ((fps == null || !fps.isActiveAndEnabled) && !VrOn) return;
                var t = me.PawnObject.transform;
                WalkMesh.Ensure(t.position, t.parent);
            }
            catch (Exception e) { Plugin.Log.LogDebug("Bridge walk: reading the bridge ahead: " + e.Message); }
        }

        public static void ClearCaches() => ignoreCache.Clear();

        public static void StandUp()
        {
            var phase = Bridge.Phase();
            if (!Bridge.Walkable(phase)) { Plugin.Log.LogInfo($"Bridge walk: not here ({Bridge.PlaceName(phase)})"); return; }
            var me = Bridge.Local();
            if (me == null || me.PawnObject == null) { Plugin.Log.LogWarning($"Bridge walk: your character wasn't found in {Bridge.PlaceName(phase)} (actor {Bridge.LocalActor()}, {Bridge.Players().Length} players)"); return; }
            bool vr = VrOn;
            Transform view;
            if (vr) { if (!FindVrRig(me, out view)) return; }
            else
            {
                var fps = me.PawnObject.GetComponentInChildren<FPSCamera>(true);
                if (fps == null || !fps.isActiveAndEnabled) { Plugin.Log.LogInfo($"Bridge walk: no 2D camera active on {Bridge.Path(me.PawnObject.transform)}{(fps == null ? "" : ", it's at " + Bridge.Path(fps.transform))}"); return; }
                view = fps.transform;
            }
            onBridge = phase == typeof(GamePhase_Bridge);
            standPhase = phase;

            Me = me;
            pawn = me.PawnObject.transform;
            cam = view;
            seatLocalPos = pawn.localPosition;
            seatLocalRot = pawn.localRotation;
            camLocalPos = cam.localPosition;
            camLocalRot = cam.localRotation;
            if (onBridge) try { ViewscreenRig.Capture(cam, Basis); } catch (Exception e) { Plugin.Log.LogWarning("Bridge walk: viewscreen: " + e.Message); }
            own.Clear();
            foreach (var c in me.PawnObject.GetComponentsInChildren<Collider>(true)) own.Add(c);

            // Walk off in the direction the camera looks.
            Vector3 f = Quaternion.Inverse(BasisRot) * cam.forward;
            yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            pitch = 0f;
            Speed = 0f;

            Vector3 pos = pawn.position;
            Vector3 seated = pos;
            if (Plugin.BridgeWalkCollisions.Value) { try { WalkMesh.Ensure(pos, Basis); } catch (Exception e) { Plugin.Log.LogWarning("Bridge walk: bridge geometry: " + e.Message); } }
            floorFromGeometry = false;
            if (Plugin.BridgeWalkCollisions.Value && FindFloor(pos, out var floor, nearestTo: true)) { pos = floor; floorFromGeometry = true; }
            flatHeight = Vector3.Dot(pos, Up);
            pawn.position = pos;
            // Out of the chair, onto free floor beside it.
            StepOut(seated);
            if (phase == typeof(GamePhase_MainMenu)) ShuttleView.Capture();
            ResetGameFreeLook();
            HookCamera();
            if (vr) StandVr();

            Standing = true;
            SwallowEscape = false;
            Pose.Reset();
            if (onBridge) OtherConsoles.Show();
            savedLockMode = Cursor.lockState;
            savedCursorVisible = Cursor.visible;
            if (!vr) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
            Plugin.Log.LogInfo($"Bridge walk: stood up{(vr ? " in VR" : "")} in {Bridge.PlaceName(phase)} at {Bridge.Path(pawn)}; floor {(floorFromGeometry ? "from the bridge geometry" : "flat (no floor found)")}");
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
                if (InVr) SitVr(); // the head is tracked: only its rig goes back
                else if (cam != null) { cam.localPosition = camLocalPos; cam.localRotation = camLocalRot; }
                if (onBridge) ViewscreenRig.Restore();
                ShuttleView.Restore();
            }
            catch (Exception e) { Plugin.Log.LogWarning("Bridge walk: sitting down: " + e.Message); }
            InVr = false;
            if (onBridge) OtherConsoles.Restore();
            if (why == "escape") SwallowEscape = true;
            Cursor.lockState = savedLockMode;
            Cursor.visible = savedCursorVisible;
            Plugin.Log.LogInfo("Bridge walk: sat down (" + why + ")");
            WalkNet.SendState(true);
        }

        public static void Tick(bool typing)
        {
            if (!Standing) return;
            if (pawn == null || cam == null || Me == null || Bridge.Phase() != standPhase) { SitDown("left " + Bridge.PlaceName(standPhase)); return; }
            // The bridge's meshes finished reading after you stood up: from now on they carry you.
            if (!floorFromGeometry && Plugin.BridgeWalkCollisions.Value && WalkMesh.ReadyFor(Basis)) floorFromGeometry = true;
            if (InVr) { TickVr(typing); return; }
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

        private static float lastHoldLog = -10f;

        /// <summary>
        /// Something of the game's turns the character back toward its seat between the walk's update and the
        /// end of the frame (only as far as the chair swivels), so the body didn't follow your heading: it's turned
        /// to your heading again after all the game's animation, right before the walk pose goes on.
        /// </summary>
        public static void HoldFacing()
        {
            if (!Standing || pawn == null) return;
            if (InVr) { if (cam != null) HeadingFromHead(); return; }
            Quaternion want = BasisRot * Quaternion.Euler(0, yaw, 0);
            float off = Quaternion.Angle(pawn.rotation, want);
            if (off > 5f && Time.unscaledTime - lastHoldLog > 5f) { lastHoldLog = Time.unscaledTime; Plugin.Log.LogInfo($"Bridge walk: your character had been turned {off:F0}° back toward the seat; turned to your heading again"); }
            pawn.rotation = want;
        }

        public static void PlaceCamera()
        {
            if (!Standing || pawn == null || cam == null) return;
            if (InVr) { PlaceVr(); return; }
            Quaternion yawRot = BasisRot * Quaternion.Euler(0, yaw, 0);
            cam.position = pawn.position + Up * Plugin.BridgeWalkEyeHeight.Value + yawRot * Vector3.forward * 0.12f;
            cam.rotation = yawRot * Quaternion.Euler(pitch, 0, 0);
            if (onBridge) ViewscreenRig.Follow(cam, Basis);
            ShuttleView.Follow(cam);
        }

        /// <summary>
        /// Standing up puts you on free floor beside the seat. Every spot around it (out to 1.5 m) with floor at about
        /// the seat's height, nothing in the way and not boxed in is scored by how open it is (how many of eight
        /// directions are clear for 0.8 m), then by how near it is and how close to the way you look; the best wins.
        /// Taking the first free spot the way you looked could leave you in a pocket in front of a console (the
        /// Aegis's engineering station) where every way out was walled.
        /// </summary>
        private static void StepOut(Vector3 seated)
        {
            Vector3 up = Up, seat = pawn.position;
            bool collisions = Plugin.BridgeWalkCollisions.Value;
            float bestScore = float.MinValue, bestR = 0f, bestTurn = 0f;
            int bestOpen = 0, noFloor = 0, badHeight = 0, blocked = 0, boxed = 0, inFront = 0, covered = 0;
            var blockers = new List<string>();
            Vector3 best = seat;
            // Around the character and around its station: looking at the briefing room's table, the game draws the
            // seated character in toward it, so around the character alone everything was table.
            var centers = new List<Vector3> { seat };
            Transform station = null;
            var heights = new List<Vector3> { seat, seated };
            try
            {
                var st = Me != null ? Me.Station as NovaStation : null;
                station = st != null ? st.transform : null;
                if (st != null && (st.transform.position - seat).sqrMagnitude > 0.01f) { centers.Add(st.transform.position); heights.Add(st.transform.position); }
            }
            catch { }
            foreach (var center in centers)
                foreach (float r in new[] { 0.55f, 0.75f, 0.95f, 1.2f, 1.5f, 1.9f })
                    for (int k = 0; k < 18; k++)
                    {
                        float turn = (k % 2 == 0 ? 1 : -1) * ((k + 1) / 2) * 20f;
                        Vector3 p = center + BasisRot * Quaternion.Euler(0, yaw + turn, 0) * Vector3.forward * r;
                        // Not in front of the station: that's where its console or the briefing room's table is.
                        if (station != null)
                        {
                            Vector3 away = p - station.position; away -= up * Vector3.Dot(away, up);
                            Vector3 ahead = station.forward; ahead -= up * Vector3.Dot(ahead, up);
                            if (away.sqrMagnitude > 1e-4f && ahead.sqrMagnitude > 1e-4f && Vector3.Dot(away.normalized, ahead.normalized) > 0.64f) { inFront++; continue; }
                        }
                        int open = 8;
                        if (collisions)
                        {
                            if (FindFloor(p, out var f)) p = f;
                            else if (floorFromGeometry) { noFloor++; continue; }
                            // About the height of the floor found under the seat, of where the seated character was, or
                            // of the station (the briefing room's floor under the seat is a lower one).
                            bool level = false;
                            foreach (var h in heights) { float step = Vector3.Dot(p - h, up); if (step <= 0.45f && step >= -0.6f) { level = true; break; } }
                            if (!level) { badHeight++; continue; }
                            if (Covered(p)) { covered++; continue; }
                            var hit = OverlapOf(p);
                            if (hit != null) { blocked++; if (blockers.Count < 3 && !blockers.Contains(hit)) blockers.Add(hit); continue; }
                            if (Cramped(p)) { boxed++; continue; }
                            open = 8 - WalledDirections(p, 0.8f);
                        }
                        else p += up * (flatHeight - Vector3.Dot(p, up));
                        float score = open * 10f - r * 4f - Mathf.Abs(turn) / 45f;
                        if (score > bestScore) { bestScore = score; best = p; bestR = r; bestTurn = turn; bestOpen = open; }
                    }
            if (bestScore > float.MinValue)
            {
                pawn.position = best;
                Plugin.Log.LogInfo($"Bridge walk: stepped out of the seat, {bestR:F2} m at {bestTurn:F0}° from where you looked ({bestOpen} of 8 directions open)");
                return;
            }
            Plugin.Log.LogInfo($"Bridge walk: no free floor beside the seat (around {centers.Count} point(s): {inFront} in front of the station, {noFloor} without floor, {badHeight} too high or low, {covered} under something, {blocked} in something{(blockers.Count > 0 ? " (" + string.Join(", ", blockers.ToArray()) + ")" : "")}, {boxed} boxed in); stepping ahead");
            TryMove(BasisRot * Quaternion.Euler(0, yaw, 0) * Vector3.forward * 0.45f);
        }

        /// <summary>Would a standing character here overlap a solid collider?</summary>
        private static readonly List<float> overhead = new List<float>();

        /// <summary>Something over this spot between 0.3 m and 1.3 m up (a table top): nowhere to stand. (Not higher: the shuttle's ceiling is low.)</summary>
        private static bool Covered(Vector3 pos)
        {
            Vector3 up = Up;
            foreach (var hit in Physics.RaycastAll(pos + up * 0.3f, up, 1.0f)) if (!Ignore(hit.collider)) return true;
            if (!WalkMesh.ReadyFor(Basis)) return false;
            overhead.Clear();
            Vector3 lp = ToLocal(pos);
            WalkMesh.Floors(lp, ToLocal(pos + up * 1.3f).y, ToLocal(pos + up * 0.3f).y, overhead);
            return overhead.Count > 0;
        }

        private static bool Overlaps(Vector3 pos) => OverlapOf(pos) != null;

        /// <summary>The first solid collider a standing character here would overlap (its path), or null.</summary>
        private static string OverlapOf(Vector3 pos)
        {
            const float r = 0.22f;
            Vector3 up = Up;
            foreach (var c in Physics.OverlapCapsule(pos + up * (0.45f + r), pos + up * (1.75f - r), r))
                if (!Ignore(c)) return Bridge.Path(c.transform);
            return null;
        }

        /// <summary>
        /// Boxed in: walls within 0.3 m in half or more of eight directions around you (inside a chair or a console,
        /// say). Walking, the walls don't stop you there, so you can always get out; out in the open they do.
        /// </summary>
        private static bool Cramped(Vector3 pos) => WalledDirections(pos, 0.3f) >= 4;

        /// <summary>Of eight directions around you, how many have a wall (mesh or collider) within this distance,
        /// at knee or hip height.</summary>
        private static int WalledDirections(Vector3 pos, float distance)
        {
            Vector3 up = Up;
            int blocked = 0;
            for (int i = 0; i < 8; i++)
            {
                Vector3 dir = BasisRot * Quaternion.Euler(0, i * 45f, 0) * Vector3.forward;
                bool hit = false;
                foreach (float h in new[] { 0.5f, 1.0f })
                {
                    Vector3 s = pos + up * h, e = s + dir * distance;
                    if (WalkMesh.ReadyFor(Basis) && WalkMesh.Hits(ToLocal(s), ToLocal(e))) { hit = true; break; }
                    foreach (var rh in Physics.RaycastAll(s, dir, distance)) if (!Ignore(rh.collider)) { hit = true; break; }
                    if (hit) break;
                }
                if (hit) blocked++;
            }
            return blocked;
        }

        // The game's right-click free look keeps its own state while you stand (its camera code is paused): cleared
        // on standing up, so its cursor and crew-menu handling don't act as if you were still looking around.
        private static void ResetGameFreeLook()
        {
            try { InputManager.IEFBKOMBFGO?.SetIsFreelook(false); } catch (Exception e) { Plugin.Log.LogDebug("Bridge walk: free look: " + e.Message); }
        }

        private static Camera.CameraCallback beforeRender;

        // Anything of the game's that still turns the 2D camera while you stand (its free look's events reset the
        // camera's rotation) would show for a frame as a snap or stutter: right before the camera draws, it's put
        // back where your eyes are.
        private static void HookCamera()
        {
            if (beforeRender != null) return;
            beforeRender = c =>
            {
                if (!Standing || c == null || cam == null || pawn == null) return;
                if (InVr) { BeforeVrCamera(c); return; }
                if (c.transform != cam && !c.transform.IsChildOf(cam)) return;
                Quaternion yawRot = BasisRot * Quaternion.Euler(0, yaw, 0);
                cam.position = pawn.position + Up * Plugin.BridgeWalkEyeHeight.Value + yawRot * Vector3.forward * 0.12f;
                cam.rotation = yawRot * Quaternion.Euler(pitch, 0, 0);
            };
            Camera.onPreCull += beforeRender;
        }

        private static string blockReason;
        private static float lastWhy = -10f;

        // What stopped a step, in the log (every 2 s at most), so a place where you get stuck can be found.
        private static void Why()
        {
            if (Time.unscaledTime - lastWhy < 2f) return;
            lastWhy = Time.unscaledTime;
            Plugin.Log.LogInfo($"Bridge walk: blocked at {ToLocal(pawn.position)}: {blockReason ?? "?"}{(Cramped(pawn.position) ? " (boxed in)" : "")}");
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
                if (Cramped(pos)) walls = false; // boxed in: walk out
                else
                {
                    // Slide along the wall: try each axis on its own.
                    Vector3 right = BasisRot * Vector3.right, fwd = BasisRot * Vector3.forward;
                    Vector3 a = right * Vector3.Dot(delta, right), b = fwd * Vector3.Dot(delta, fwd);
                    if (!Blocked(pos, a)) next = pos + a;
                    else if (!Blocked(pos, b)) next = pos + b;
                    else { Why(); return; }
                }
            }
            if (floorFromGeometry)
            {
                // Where the bridge has no floor, keep walking at the same height (MaxDistance keeps you on the bridge).
                if (!FindFloor(next, out var floor)) floor = next;
                float step = Vector3.Dot(floor - pos, up);
                if (step > 0.45f || step < -0.8f)
                {
                    if (walls) { blockReason = $"step of {step:F2} m"; Why(); return; } // too high a step
                    // Walls off: through the desk or wall at the same height.
                }
                else next = floor;
            }
            else next += up * (flatHeight - Vector3.Dot(next, up));
            Vector3 seatWorld = Basis != null ? Basis.TransformPoint(seatLocalPos) : seatLocalPos;
            Vector3 off = next - seatWorld; off -= up * Vector3.Dot(off, up);
            float maxDistance = TestCorridor.MaxDistance(Plugin.BridgeWalkMaxDistance.Value);
            if (off.magnitude > maxDistance) { blockReason = $"{off.magnitude:F1} m from the seat (max {maxDistance})"; Why(); return; }
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
            // In the test corridor's doorway or the corridor: only its own walls (the bridge's have the door baked in).
            bool corridor = TestCorridor.InZone(pos) && TestCorridor.InZone(pos + delta);
            foreach (var hit in Physics.CapsuleCastAll(p1, p2, r, delta / dist, dist + 0.05f))
            {
                if (Ignore(hit.collider)) continue;
                if (corridor && !TestCorridor.IsMine(hit.collider)) continue;
                if (hit.distance <= 0f && hit.point == Vector3.zero) continue; // already overlapping: don't get stuck
                blockReason = "collider " + Bridge.Path(hit.collider.transform);
                return true;
            }
            // Right at your own seat the mesh walls aren't checked, or you'd be stuck between the armrests.
            Vector3 seatW = Basis != null ? Basis.TransformPoint(seatLocalPos) : seatLocalPos;
            Vector3 fromSeat = pos - seatW; fromSeat -= up * Vector3.Dot(fromSeat, up);
            if (!corridor && WalkMesh.ReadyFor(Basis) && fromSeat.magnitude > 0.7f)
            {
                Vector3 dir = delta / dist, side = Vector3.Cross(up, dir).normalized * r;
                foreach (float h in wallHeights)
                {
                    Vector3 s = pos + up * h;
                    Vector3 e = s + dir * (dist + r);
                    if (WalkMesh.Hits(ToLocal(s), ToLocal(e)) || WalkMesh.Hits(ToLocal(s + side), ToLocal(e + side)) || WalkMesh.Hits(ToLocal(s - side), ToLocal(e - side))) { blockReason = $"bridge mesh wall at {h:F2} m height"; return true; }
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
            bool corridor = TestCorridor.InZone(at); // the test corridor's doorway or corridor: its own floor only
            foreach (var hit in Physics.RaycastAll(at + up * above, -up, 2.4f))
            {
                if (Ignore(hit.collider)) continue;
                if (corridor && !TestCorridor.IsMine(hit.collider)) continue;
                if (Vector3.Dot(hit.normal, up) < 0.5f) continue; // walls and steep faces aren't floor
                if (nearestTo && hit.distance > above + 1.0f) continue;
                float score = nearestTo ? -hit.distance : hit.distance;
                if (score < best) { best = score; floor = hit.point; found = true; }
            }
            if (!corridor && WalkMesh.ReadyFor(Basis))
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
            // Backdrops around the whole room (the briefing room's StarsCollider): everything is inside them, they're no wall.
            if (!v && c.bounds.size.magnitude > 60f) v = true;
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
            OwnHull.Forget();
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

        // Not the Enterprise-D: settled for this stand, so the scene isn't searched again every 2 s (ViewscreenRig.Follow).
        private static bool notThisShip;

        /// <summary>Standing up: whether this ship's hull needs hiding is worked out again.</summary>
        internal static void Forget() => notThisShip = false;

        internal static void Hide()
        {
            Restore();
            if (notThisShip) return;
            // Your own ship (in PvP there are two player ships: the first one found could be the other's).
            var ship = StbcRevival.PvpPrototype.Bound ?? UnityEngine.Object.FindObjectOfType<Sector.Spacecraft.PlayerSpacecraft>();
            if (ship == null) return;
            // The ship object is "Enterprise D_", its model "Body - 1701D(Clone)".
            bool enterpriseD = ship.name.IndexOf("Enterprise D", StringComparison.OrdinalIgnoreCase) >= 0 || ship.name.IndexOf("1701D", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!enterpriseD)
                foreach (Transform child in ship.transform)
                    if (child.name.IndexOf("1701D", StringComparison.OrdinalIgnoreCase) >= 0) { enterpriseD = true; break; }
            if (!enterpriseD) { notThisShip = true; return; }
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

        /// <summary>Is any crewmate standing? (A handful at most: cheap enough for the per-character animation hooks.)</summary>
        public static bool AnyStanding()
        {
            foreach (var r in All.Values) if (r.Standing) return true;
            return false;
        }

        /// <summary>Crewmates' characters are turned to their heading again after the game's animation (see Walker.HoldFacing).</summary>
        public static void HoldFacing()
        {
            foreach (var r in All.Values)
                if (r.Standing && r.HasCurrent && r.Info != null && r.Info.PawnObject != null)
                    r.Info.PawnObject.transform.localRotation = Quaternion.Euler(0, r.CurrentYaw, 0);
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
            // Seated, the game turns the character to the station's facing plus at most 45° toward where you look,
            // every frame: standing, that's skipped for the standing character, so it faces your heading.
            Try(h, AccessTools.Method(typeof(NovaAnimationController), "EEEKOHNIDBH"), prefix: nameof(NoSeatTurn));
            // The chair's backrest leans toward the seated character's shoulders: standing, it stays as it was.
            Try(h, AccessTools.Method(typeof(NovaAnimationController), "FIFMENKIJFI"), prefix: nameof(NoBackrestLean));
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

        // PawnComponent._playerInfo (NovaAnimationController's base class).
        private static readonly AccessTools.FieldRef<PawnComponent, NovaPlayerInfo> animPlayer = TryFieldRef();
        private static bool seatTurnLogged;

        private static AccessTools.FieldRef<PawnComponent, NovaPlayerInfo> TryFieldRef()
        {
            try { return AccessTools.FieldRefAccess<PawnComponent, NovaPlayerInfo>("HAMOMEFCFNB"); }
            catch (Exception e) { Plugin.Log.LogWarning("Bridge walk: a character's player wasn't found (" + e.Message + "); standing characters are found by place instead"); return null; }
        }

        // The controller's player (its own object isn't under the player's character), else its place in the scene.
        // Runs for every character every frame: with nobody standing (nearly always) it does nothing more.
        private static bool NoSeatTurn(Component __instance)
        {
            if (!Walker.Standing && !Remotes.AnyStanding()) return true;
            if (__instance == null) return true;
            var player = animPlayer != null && __instance is PawnComponent pc ? animPlayer(pc) : null;
            bool standing = player != null
                ? (Walker.Standing && player == Walker.Me) || IsStandingRemote(player)
                : IsStanding(__instance.transform);
            if (standing && !seatTurnLogged) { seatTurnLogged = true; Plugin.Log.LogInfo($"Bridge walk: the seat turn is off for standing characters (at {Bridge.Path(__instance.transform)})"); }
            return !standing;
        }


        // Standing, the backrest stays as it was when you got up (its rest angle isn't the same on every chair: forcing
        // 0° broke the shuttle's).
        private static bool NoBackrestLean(Component __instance) => NoSeatTurn(__instance);

        private static bool IsStandingRemote(NovaPlayerInfo p)
        {
            foreach (var r in Remotes.All.Values) if (r.Standing && r.Info == p) return true;
            return false;
        }

        private static bool IsStanding(Transform t)
        {
            if (Walker.Standing && Walker.Me != null && Walker.Me.PawnObject != null && Under(t, Walker.Me.PawnObject.transform)) return true;
            foreach (var r in Remotes.All.Values)
                if (r.Standing && r.Info != null && r.Info.PawnObject != null && Under(t, r.Info.PawnObject.transform)) return true;
            return false;
        }

        private static bool Under(Transform t, Transform pawn) => t == pawn || t.IsChildOf(pawn);

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
            if (Walker.ReadingInput) return false;
            if (Walker.Standing)
                return a != LONCAPHEAPA.VoiceCommand && a != LONCAPHEAPA.KeyboardAny && a != LONCAPHEAPA.MouseAny && a != LONCAPHEAPA.GamePadAny
                    && a != LONCAPHEAPA.TouchControllersAny && a != LONCAPHEAPA.ViveControllersAny && a != LONCAPHEAPA.MoveControllersAny;
            if (Walker.SwallowEscape)
                return a == LONCAPHEAPA.PauseMenuMode || a == LONCAPHEAPA.BackOrCancel || a == LONCAPHEAPA.CloseAllMenus;
            return false;
        }

        private static bool NoAxis(ref float __result)
        {
            if (!Walker.Standing || Walker.ReadingInput) return true;
            __result = 0f;
            return false;
        }

        private static void AfterLateUpdate()
        {
            if (Walker.Standing || Remotes.All.Count > 0) BodyAnim.ApplyAll();
        }
    }
}
