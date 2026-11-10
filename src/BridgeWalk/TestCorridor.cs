using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;

namespace StbcRevival.Walking
{
    /// <summary>
    /// [Bridge Walk] TestCorridor: a test of walking off the bridge. On the Aegis bridge (Bridge_scene_02) the door
    /// BridgeDoor_A opens, Star Trek style, when someone standing in Bridge Walk comes up to it (you or a crewmate),
    /// with the game's own Aegis door sounds (Wwise events the game has but never plays), and closes again once
    /// they're clear. Behind it is a corridor built here from the bridge's own materials: about 20 m long, with three
    /// doors down each side and one at the end, all scenery. Each player's game builds its own copy; Bridge Walk
    /// already shares where everyone is standing, so nothing new goes over the network.
    ///
    /// The game's doors never open (they're plain models), so the door is either moved (if it can be) or hidden and
    /// stood in for by two panels in its material that slide apart. Bridge Walk normally takes its walls and floors
    /// from the bridge's meshes, which have the door baked in where it was: in the doorway and the corridor (the
    /// "zone") only the corridor's own colliders count instead. While the setting is on, you can walk up to
    /// <see cref="TestDistance"/> m from your seat. Everything is logged, so a first look in game can put it right.
    /// </summary>
    internal static class TestCorridor
    {
        internal const float TestDistance = 40f;
        private const string SceneName = "Bridge_scene_02";
        private const string DoorName = "BridgeDoor_A";
        private const float Length = 20f, Width = 2.6f, MinHeight = 3f;
        private const float OpenRange = 1.8f, OpenSeconds = 0.45f, CloseDelay = 1f;
        private const int SoundOpen = 1117740432;  // AK.EVENTS.PLAY_SND_DOOR_OPEN_AEGIS_OPEN
        private const int SoundClose = 676009844;  // AK.EVENTS.PLAY_SND_DOOR_OPEN_AEGIS_CLOSE
        internal const string RootName = "RevivalTestCorridor";

        private static GameObject root;
        private static Transform door;
        private static float doorW, doorH, doorT, z0, height;
        private static float open, lastNear = -10f;
        private static bool opening;
        private static float nextTry;
        private static bool gaveUp;

        // How the door opens: its own parts moved aside, or stand-in panels (the original hidden).
        private sealed class Moved { public Transform T; public Vector3 Pos; public Vector3 Away; }
        private static readonly List<Moved> moved = new List<Moved>();
        private static readonly List<Renderer> hiddenRenderers = new List<Renderer>();
        private static readonly List<Collider> offColliders = new List<Collider>();
        private static Transform panelLeft, panelRight;
        private static GameObject blocker;
        private static readonly HashSet<Collider> mine = new HashSet<Collider>();
        // Meshes and materials made here: not freed with the objects that use them, so freed on Clear.
        private static readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();

        internal static bool On => Plugin.BridgeWalkTestCorridor != null && Plugin.BridgeWalkTestCorridor.Value;
        internal static float MaxDistance(float setting) => On ? Mathf.Max(setting, TestDistance) : setting;

        /// <summary>Every frame from BridgeWalk.Update: builds it on the Aegis bridge, and opens and closes the door.</summary>
        internal static void Tick(bool onBridge)
        {
            if (!On) return;
            if (!onBridge)
            {
                if (root != null || door != null) Clear("left the bridge");
                gaveUp = false;
                return;
            }
            if (root == null)
            {
                // The bridge was reloaded under it (a new mission): what was kept of the old one goes.
                if (door != null || mine.Count > 0 || moved.Count > 0 || hiddenRenderers.Count > 0) Clear("the bridge was reloaded");
                if (gaveUp || Time.unscaledTime < nextTry) return;
                nextTry = Time.unscaledTime + 2f;
                try { Build(); }
                catch (Exception e) { Plugin.Log.LogWarning("Test corridor: building: " + e); Clear("failed"); gaveUp = true; }
                return;
            }
            try { Door(); }
            catch (Exception e) { Plugin.Log.LogWarning("Test corridor: door: " + e.Message); }
        }

        // ------------------------------------------------------------------------------------------- zone
        /// <summary>In the doorway (its middle, on the bridge side) or the corridor: only the corridor's colliders count.</summary>
        internal static bool InZone(Vector3 world)
        {
            if (root == null) return false;
            Vector3 p = root.transform.InverseTransformPoint(world);
            if (p.y < -1.5f || p.y > height + 1f) return false;
            float lane = Mathf.Max(0.15f, doorW / 2f - 0.25f);
            if (Mathf.Abs(p.x) < lane && p.z > -1.2f && p.z <= z0 + 0.3f) return true;
            return Mathf.Abs(p.x) < Width / 2f + 0.3f && p.z > z0 - 0.05f && p.z < z0 + Length + 0.3f;
        }

        internal static void Own(UnityEngine.Object o) => owned.Add(o);

        internal static bool IsMine(Collider c) => c != null && mine.Contains(c);

        // ------------------------------------------------------------------------------------------- building
        private static void Build()
        {
            Transform found = null;
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount && found == null; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || scene.name != SceneName) continue;
                foreach (var top in scene.GetRootGameObjects())
                {
                    found = FindChild(top.transform, DoorName);
                    if (found != null) break;
                }
            }
            if (found == null) return; // not the Aegis bridge (or not loaded yet): tried again in 2 s
            // The door's own shape, read from the bridge's file (in the background) so a copy of it can slide.
            if (!DoorShape.Ready(found.gameObject.scene.buildIndex)) return;
            // Out of the bridge is worked out from where you sit: wait for your character (the first run built it
            // before you were in your seat, measuring from the bridge's origin instead).
            var me = Bridge.Local();
            if (me == null || me.PawnObject == null) return;

            door = found;
            var parent = door.parent;
            var renderers = door.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) { Plugin.Log.LogWarning($"Test corridor: {Bridge.Path(door)} has nothing drawn"); gaveUp = true; door = null; return; }

            // The door's size, in its parent's space (the bridge's: y up).
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), max = -min;
            foreach (var r in renderers)
            {
                Bounds b = r.bounds;
                for (int k = 0; k < 8; k++)
                {
                    Vector3 corner = new Vector3((k & 1) == 0 ? b.min.x : b.max.x, (k & 2) == 0 ? b.min.y : b.max.y, (k & 4) == 0 ? b.min.z : b.max.z);
                    Vector3 l = parent != null ? parent.InverseTransformPoint(corner) : corner;
                    min = Vector3.Min(min, l); max = Vector3.Max(max, l);
                }
            }
            Vector3 size = max - min, center = (min + max) / 2f;
            bool thinX = size.x < size.z;
            doorT = Mathf.Max(0.02f, thinX ? size.x : size.z);
            doorW = Mathf.Max(0.8f, thinX ? size.z : size.x);
            doorH = Mathf.Max(1.9f, size.y);
            height = Mathf.Max(MinHeight, doorH + 0.5f);

            // Out of the bridge: away from your seat (the door is in the bridge's outer wall).
            Vector3 normal = thinX ? Vector3.right : Vector3.forward;
            Vector3 seat = parent != null ? parent.InverseTransformPoint(me.PawnObject.transform.position) : me.PawnObject.transform.position;
            if (Vector3.Dot(center - seat, normal) < 0f) normal = -normal;

            root = new GameObject(RootName);
            if (parent != null) root.transform.SetParent(parent, false);
            else UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, door.gameObject.scene);
            root.transform.localPosition = new Vector3(center.x, min.y, center.z);
            root.transform.localRotation = Quaternion.LookRotation(normal, Vector3.up);
            root.transform.localScale = Vector3.one;
            z0 = doorT / 2f + 0.02f;

            var mats = Materials(renderers);
            BuildRoom(mats);
            BuildDoor(renderers, mats);
            Vector3 far = root.transform.TransformPoint(new Vector3(0, 0, z0 + Length));
            Plugin.Log.LogInfo($"Test corridor: door at {center}, you at {seat} (in {(parent != null ? parent.name : "the scene")}); the corridor runs from {root.transform.position} to {far} (world), "
                               + $"{Vector3.Distance(far, me.PawnObject.transform.position):F1} m from you at its end");
            Plugin.Log.LogInfo($"Test corridor: built behind {Bridge.Path(door)}: door {doorW:F2} x {doorH:F2} m, {doorT:F2} m thick, facing {normal} in {(parent != null ? parent.name : "the scene")} "
                               + $"(rotation {(parent != null ? parent.rotation.eulerAngles : Vector3.zero)}); you at {seat}; corridor {Length} x {Width} x {height:F1} m; "
                               + $"materials: wall {Name(mats.Wall)}, floor {Name(mats.Floor)}, ceiling {Name(mats.Ceiling)}, strip {Name(mats.Strip)}, door {Name(mats.Door)}, frame {Name(mats.Frame)}");
        }

        private sealed class Mats { public Material Wall, Floor, Ceiling, Strip, Door, Frame, Glow, Panel; }

        // The bridge's own materials, by the objects that wear them (or failing that by name).
        private static Mats Materials(Renderer[] doorRenderers)
        {
            var scene = door.gameObject.scene;
            var byObject = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
            foreach (var top in scene.GetRootGameObjects())
                foreach (var r in top.GetComponentsInChildren<Renderer>(true))
                    if (Surface(r.sharedMaterial) && !byObject.ContainsKey(r.name)) byObject[r.name] = r.sharedMaterial;
            var all = Resources.FindObjectsOfTypeAll<Material>();
            Func<string[], string[], Material> pick = (objects, names) =>
            {
                foreach (var o in objects) if (byObject.TryGetValue(o, out var m)) return m;
                foreach (var n in names) foreach (var m in all) if (Surface(m) && m.name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return m;
                return null;
            };
            var mats = new Mats
            {
                Wall = pick(new[] { "Bridge_Walls_New", "BridgeWallsNew", "Bridge_Walls" }, new[] { "BridgeWallsNew", "BridgeWalls", "Bridge_Walls" }),
                Floor = pick(new[] { "BridgeFloorFull", "Floor", "Floor1", "FloorLeft" }, new[] { "BridgeFloor" }),
                Ceiling = pick(new[] { "Ceiling - Front Middle", "Ceiling - Front Left", "BridgeCeiling" }, new[] { "BridgeCeiling", "Bridge_Ceiling" }),
                Strip = pick(new[] { "EmissiveFloorStrip", "EmissiveCeilingStripGlow", "CeilingEmissive_A" }, new[] { "EmissiveFloorStrip", "GlowStrip", "Emissive_Ceiling" }),
                Door = doorRenderers[0].sharedMaterial,
                Frame = pick(new[] { "BridgeDoorFrame_A", "BridgeDoorFrame_B" }, new[] { "BridgeDoorFrame" }),
            };
            var fallback = mats.Wall ?? mats.Door ?? new Material(Shader.Find("Standard"));
            if (mats.Wall == null) mats.Wall = fallback;
            if (mats.Floor == null) mats.Floor = fallback;
            if (mats.Ceiling == null) mats.Ceiling = fallback;
            if (mats.Frame == null) mats.Frame = mats.Wall;
            if (mats.Door == null) mats.Door = mats.Wall;
            // Made here: a soft white light panel for the ceiling (the bridge's glowing strip read as a band of blue
            // sky), and a door panel without the bridge door's AIR LOCK lettering for the scenery doors.
            mats.Glow = Lit(new Color(0.75f, 0.78f, 0.8f), new Color(0.9f, 0.93f, 1f) * 0.9f);
            mats.Panel = new Material(mats.Wall) { name = RootName + " door panel" };
            owned.Add(mats.Glow); owned.Add(mats.Panel);
            if (mats.Panel.HasProperty("_Color")) mats.Panel.color = new Color(0.55f, 0.57f, 0.6f);
            return mats;
        }

        private static Material Lit(Color color, Color glow)
        {
            var m = new Material(Shader.Find("Standard")) { name = RootName + " light" };
            m.color = color;
            m.SetColor("_EmissionColor", glow);
            m.EnableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            return m;
        }

        // A material for a wall or floor: not a particle or additive effect (the first run's ceiling got 'EmissiveSparks').
        private static bool Surface(Material m)
        {
            if (m == null || m.shader == null) return false;
            string sh = m.shader.name;
            return sh.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) < 0 && sh.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) < 0
                   && m.name.IndexOf("Spark", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static void BuildRoom(Mats m)
        {
            var look = Child(root.transform, "Look");
            float w = Width / 2f, z1 = z0 + Length, zm = (z0 + z1) / 2f;
            // Floor, ceiling, walls (all facing in).
            Quad(look, "Floor", new Vector3(0, 0, zm), Vector3.up, Vector3.forward, Width, Length, m.Floor);
            Quad(look, "Ceiling", new Vector3(0, height, zm), Vector3.down, Vector3.forward, Width, Length, m.Ceiling);
            Quad(look, "Wall left", new Vector3(-w, height / 2f, zm), Vector3.right, Vector3.up, Length, height, m.Wall);
            Quad(look, "Wall right", new Vector3(w, height / 2f, zm), Vector3.left, Vector3.up, Length, height, m.Wall);
            Quad(look, "Wall end", new Vector3(0, height / 2f, z1), Vector3.back, Vector3.up, Width, height, m.Wall);
            // The wall around the doorway, seen from the corridor.
            float side = w - doorW / 2f;
            Quad(look, "Doorway left", new Vector3(-(doorW / 2f + side / 2f), height / 2f, z0), Vector3.forward, Vector3.up, side, height, m.Wall);
            Quad(look, "Doorway right", new Vector3(doorW / 2f + side / 2f, height / 2f, z0), Vector3.forward, Vector3.up, side, height, m.Wall);
            Quad(look, "Doorway top", new Vector3(0, (doorH + height) / 2f, z0), Vector3.forward, Vector3.up, doorW, height - doorH, m.Wall);
            // Glowing strips along the floor's edges and down the middle of the ceiling.
            if (m.Strip != null)
            {
                Quad(look, "Strip left", new Vector3(-w + 0.12f, 0.004f, zm), Vector3.up, Vector3.forward, 0.08f, Length, m.Strip);
                Quad(look, "Strip right", new Vector3(w - 0.12f, 0.004f, zm), Vector3.up, Vector3.forward, 0.08f, Length, m.Strip);
                Quad(look, "Light ceiling", new Vector3(0, height - 0.004f, zm), Vector3.down, Vector3.forward, 0.5f, Length - 1f, m.Glow);
            }
            // Doors down each side and one at the end: scenery.
            for (int i = 1; i <= 3; i++)
            {
                float z = z0 + Length * i / 4f;
                SideDoor(look, $"Side door left {i}", new Vector3(-w, 0, z), Vector3.right, m);
                SideDoor(look, $"Side door right {i}", new Vector3(w, 0, z), Vector3.left, m);
            }
            SideDoor(look, "End door", new Vector3(0, 0, z1), Vector3.back, m);
            // Lights along the ceiling: the corridor is outside the bridge's baked lighting.
            for (float z = z0 + 2.5f; z < z1; z += 5f)
            {
                var l = Child(look, "Light").gameObject.AddComponent<Light>();
                l.transform.localPosition = new Vector3(0, height - 0.35f, z);
                l.type = LightType.Point;
                l.range = 6.5f;
                l.intensity = 1.1f;
                l.color = new Color(0.85f, 0.92f, 1f);
                l.shadows = LightShadows.None;
            }

            // What you walk on and into.
            var solid = Child(root.transform, "Colliders");
            Box(solid, "Floor", new Vector3(0, -0.1f, zm), new Vector3(Width, 0.2f, Length));
            Box(solid, "Floor (doorway)", new Vector3(0, -0.1f, z0 - 0.6f), new Vector3(doorW, 0.2f, 1.2f));
            Box(solid, "Wall left", new Vector3(-w - 0.1f, height / 2f, zm), new Vector3(0.2f, height, Length));
            Box(solid, "Wall right", new Vector3(w + 0.1f, height / 2f, zm), new Vector3(0.2f, height, Length));
            Box(solid, "Wall end", new Vector3(0, height / 2f, z1 + 0.1f), new Vector3(Width + 0.4f, height, 0.2f));
            Box(solid, "Doorway left", new Vector3(-(doorW / 2f + side / 2f), height / 2f, z0 + 0.1f), new Vector3(side, height, 0.2f));
            Box(solid, "Doorway right", new Vector3(doorW / 2f + side / 2f, height / 2f, z0 + 0.1f), new Vector3(side, height, 0.2f));
            blocker = Box(solid, "Door (closed)", new Vector3(0, doorH / 2f, 0), new Vector3(doorW, doorH, Mathf.Max(0.2f, doorT))).gameObject;
        }

        // A door and its frame on a wall, standing on the floor, facing into the corridor.
        private static void SideDoor(Transform parent, string name, Vector3 at, Vector3 facing, Mats m)
        {
            // A frame, a plain panel (no AIR LOCK lettering: that's the bridge door's texture) and a light down its middle.
            Quad(parent, name + " frame", at + facing * 0.006f + Vector3.up * (doorH / 2f + 0.05f), facing, Vector3.up, doorW + 0.3f, doorH + 0.15f, m.Frame, cutout: true);
            Quad(parent, name, at + facing * 0.012f + Vector3.up * (doorH / 2f), facing, Vector3.up, doorW, doorH, m.Panel);
            Quad(parent, name + " light", at + facing * 0.016f + Vector3.up * (doorH / 2f), facing, Vector3.up, 0.06f, doorH * 0.6f, m.Glow);
        }

        private static void BuildDoor(Renderer[] renderers, Mats m)
        {
            // Off for good while the corridor's here: the door's own colliders (the corridor's "closed" box stands in).
            foreach (var c in door.GetComponentsInChildren<Collider>(true)) if (c.enabled) { c.enabled = false; offColliders.Add(c); }

            bool batched = false;
            foreach (var r in renderers) if (r.isPartOfStaticBatch) batched = true;
            if (!batched)
            {
                // Its own parts slide aside: two halves (a part each side of the middle) apart, or the whole door one way.
                var parts = new List<Transform>();
                foreach (Transform child in door) if (child.GetComponentInChildren<Renderer>(true) != null) parts.Add(child);
                bool halves = false;
                if (parts.Count >= 2)
                {
                    bool left = false, right = false;
                    foreach (var p in parts) { float x = root.transform.InverseTransformPoint(Center(p)).x; if (x < -0.05f) left = true; if (x > 0.05f) right = true; }
                    halves = left && right;
                }
                if (halves)
                    foreach (var p in parts)
                    {
                        float x = root.transform.InverseTransformPoint(Center(p)).x;
                        moved.Add(new Moved { T = p, Pos = p.position, Away = root.transform.TransformDirection(Vector3.right * Mathf.Sign(x) * doorW / 2f) });
                    }
                else moved.Add(new Moved { T = door, Pos = door.position, Away = root.transform.TransformDirection(Vector3.right * doorW) });
                Plugin.Log.LogInfo($"Test corridor: the door opens by moving {(halves ? parts.Count + " parts apart" : "aside in one piece")}");
                return;
            }

            // Statically batched: it can't move. Hidden, and a copy of it slides instead: its own shape from the
            // bridge's file (exactly where it was, so no gap), or failing that two panels in its material.
            foreach (var r in renderers) if (r.enabled) { r.enabled = false; hiddenRenderers.Add(r); }
            var copy = DoorShape.Copy(door, renderers);
            if (copy != null)
            {
                copy.name = RootName + " door";
                copy.transform.SetParent(root.transform, true);
                moved.Add(new Moved { T = copy.transform, Pos = copy.transform.position, Away = root.transform.TransformDirection(Vector3.right * (doorW + 0.05f)) });
                Plugin.Log.LogInfo("Test corridor: the door is part of the bridge's batched model: hidden, and a copy of it from the bridge's file slides aside");
                return;
            }
            var look = root.transform.Find("Look");
            panelLeft = Panel(look, "Door half left", -1, m.Door);
            panelRight = Panel(look, "Door half right", 1, m.Door);
            Plugin.Log.LogInfo("Test corridor: the door is part of the bridge's batched model: hidden, two sliding panels in its place");
        }

        // One half of the stand-in door: both faces, in the door's middle plane, with that half of the door's texture.
        private static Transform Panel(Transform parent, string name, int side, Material mat)
        {
            var t = Child(parent, name);
            t.localPosition = new Vector3(side * doorW / 4f, doorH / 2f, 0);
            Rect uv = side < 0 ? new Rect(0, 0, 0.5f, 1) : new Rect(0.5f, 0, 0.5f, 1);
            Quad(t, "Bridge side", new Vector3(0, 0, -0.01f), Vector3.back, Vector3.up, doorW / 2f, doorH, mat, uv: uv);
            Quad(t, "Corridor side", new Vector3(0, 0, 0.01f), Vector3.forward, Vector3.up, doorW / 2f, doorH, mat, uv: new Rect(uv.xMax, 0, -uv.width, 1));
            return t;
        }

        // ------------------------------------------------------------------------------------------- the door
        private static void Door()
        {
            bool near = Near(Walker.Standing ? Walker.Me : null);
            if (!near) foreach (var r in Remotes.All.Values) if (r.Standing && Near(r.Info)) { near = true; break; }
            if (near) lastNear = Time.unscaledTime;
            bool want = near || Time.unscaledTime - lastNear < CloseDelay;
            if (want != opening)
            {
                opening = want;
                Sound(want ? SoundOpen : SoundClose);
                Plugin.Log.LogInfo($"Test corridor: door {(want ? "opening" : "closing")}");
            }
            float before = open;
            open = Mathf.MoveTowards(open, opening ? 1f : 0f, Time.deltaTime / OpenSeconds);
            if (open == before) return;
            float eased = open * open * (3f - 2f * open);
            foreach (var mv in moved) if (mv.T != null) mv.T.position = mv.Pos + mv.Away * eased;
            if (panelLeft != null) panelLeft.localPosition = new Vector3(-doorW / 4f - eased * doorW / 2f, doorH / 2f, 0);
            if (panelRight != null) panelRight.localPosition = new Vector3(doorW / 4f + eased * doorW / 2f, doorH / 2f, 0);
            if (blocker != null) blocker.SetActive(open < 0.6f); // passable once it's mostly open
        }

        private static bool Near(NovaPlayerInfo p)
        {
            if (p == null || p.PawnObject == null || root == null) return false;
            Vector3 l = root.transform.InverseTransformPoint(p.PawnObject.transform.position);
            return Mathf.Abs(l.y) < 2f && new Vector2(l.x, l.z).magnitude < OpenRange;
        }

        private static void Sound(int id)
        {
            var at = panelLeft != null ? panelLeft.gameObject : door != null ? door.gameObject : null;
            try { WwiseSoundUtil.PostEvent(id, at); }
            catch (Exception e)
            {
                try { WwiseSoundUtil.PostEvent(id); }
                catch { Plugin.Log.LogInfo("Test corridor: no door sound: " + e.Message); }
            }
        }

        // ------------------------------------------------------------------------------------------- clearing
        internal static void Clear(string why)
        {
            foreach (var mv in moved) if (mv.T != null) mv.T.position = mv.Pos;
            foreach (var r in hiddenRenderers) if (r != null) r.enabled = true;
            foreach (var c in offColliders) if (c != null) c.enabled = true;
            moved.Clear(); hiddenRenderers.Clear(); offColliders.Clear(); mine.Clear();
            if (root != null) UnityEngine.Object.Destroy(root);
            foreach (var o in owned) if (o != null) UnityEngine.Object.Destroy(o);
            owned.Clear();
            root = null; door = null; panelLeft = panelRight = null; blocker = null;
            open = 0f; opening = false;
            Plugin.Log.LogInfo("Test corridor: removed (" + why + ")");
        }

        // ------------------------------------------------------------------------------------------- helpers
        private static Transform FindChild(Transform t, string name)
        {
            if (t.name == name && t.gameObject.activeInHierarchy) return t;
            foreach (Transform c in t) { var f = FindChild(c, name); if (f != null) return f; }
            return null;
        }

        private static Vector3 Center(Transform t)
        {
            var r = t.GetComponentInChildren<Renderer>(true);
            return r != null ? r.bounds.center : t.position;
        }

        private static string Name(Material m) => m == null ? "none" : $"'{m.name}' ({(m.shader != null ? m.shader.name : "?")})";

        private static Transform Child(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        private static BoxCollider Box(Transform parent, string name, Vector3 center, Vector3 size)
        {
            var t = Child(parent, name);
            t.localPosition = center;
            var b = t.gameObject.AddComponent<BoxCollider>();
            b.size = size;
            mine.Add(b);
            return b;
        }

        // A one-sided rectangle facing `normal`, `up` along its height. Repeating textures are laid out a metre and a
        // half to a repeat, unless a part of the texture is given (uv) or it's a door or frame (the whole texture once).
        private static void Quad(Transform parent, string name, Vector3 center, Vector3 normal, Vector3 up, float w, float h, Material mat, Rect? uv = null, bool cutout = false)
        {
            var t = Child(parent, name);
            t.localPosition = center;
            Vector3 right = Vector3.Cross(normal, up);
            Vector3 hr = right * (w / 2f), hu = up * (h / 2f);
            Rect r = uv ?? (cutout ? new Rect(0, 0, 1, 1) : new Rect(0, 0, w / 1.5f, h / 1.5f));
            var mesh = new Mesh { name = RootName + " " + name };
            owned.Add(mesh);
            mesh.vertices = new[] { -hr - hu, hr - hu, -hr + hu, hr + hu };
            mesh.uv = new[] { new Vector2(r.xMin, r.yMin), new Vector2(r.xMax, r.yMin), new Vector2(r.xMin, r.yMax), new Vector2(r.xMax, r.yMax) };
            mesh.normals = new[] { normal, normal, normal, normal };
            var tangent = new Vector4(right.x, right.y, right.z, -1f);
            mesh.tangents = new[] { tangent, tangent, tangent, tangent };
            mesh.triangles = new[] { 0, 3, 1, 3, 0, 2 };
            mesh.RecalculateBounds();
            t.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = t.gameObject.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }
    }
}

namespace StbcRevival.Walking
{
    /// <summary>
    /// The Aegis bridge's door as the bridge's file has it. The door is statically batched: drawn as part of one big
    /// combined mesh, so it can't be moved. Its pieces' sub-meshes of that mesh are read from the scene's file (as
    /// HolodeckRoom does) on a background thread, and copied out into a mesh of its own in the same place, which can.
    /// </summary>
    internal static class DoorShape
    {
        private const string DoorName = "BridgeDoor_A";

        private sealed class Piece
        {
            public string Name;
            public long Combined;
            public bool HasRoot;
            public readonly List<int> SubMeshes = new List<int>();
        }

        private static readonly List<Piece> pieces = new List<Piece>();
        private static readonly Dictionary<long, MeshAsset> combined = new Dictionary<long, MeshAsset>();
        private static int readScene = -1;
        private static volatile bool reading, done;
        private static string error;

        /// <summary>Starts reading (once per scene); true once there's an answer (the shape, or why not).</summary>
        internal static bool Ready(int buildIndex)
        {
            if (buildIndex < 0) return true;
            if (readScene == buildIndex) return done;
            if (reading) return false;
            readScene = buildIndex;
            reading = true;
            done = false;
            error = null;
            pieces.Clear();
            combined.Clear();
            string path = Path.Combine(Application.dataPath, "level" + buildIndex);
            new Thread(() =>
            {
                try { Read(path); }
                catch (Exception e) { error = e.Message; }
                reading = false;
                done = true;
            }) { IsBackground = true, Name = "Test corridor door" }.Start();
            return false;
        }

        private static void Read(string path)
        {
            using (var scene = new AssetFile(path))
            {
                GameObjectAsset Go(PPtr p) => GameObjectAsset.Read(scene.At(scene.Find(p.PathId)));
                TransformAsset Tr(PPtr p) => TransformAsset.Read(scene.At(scene.Find(p.PathId)));
                void Collect(TransformAsset t)
                {
                    var go = Go(t.GameObject);
                    var rendererRef = go.Component(23);
                    var filterRef = go.Component(33);
                    if (!rendererRef.IsNull && !filterRef.IsNull)
                    {
                        var renderer = MeshRendererAsset.Read(scene.At(scene.Find(rendererRef.PathId)));
                        var filter = MeshFilterAsset.Read(scene.At(scene.Find(filterRef.PathId)));
                        if (filter.Mesh.FileId == 0 && renderer.SubsetIndices.Count > 0)
                        {
                            var piece = new Piece { Name = go.Name, Combined = filter.Mesh.PathId, HasRoot = !renderer.StaticBatchRoot.IsNull };
                            foreach (var i in renderer.SubsetIndices) piece.SubMeshes.Add((int)i);
                            pieces.Add(piece);
                        }
                    }
                    foreach (var child in t.Children) Collect(Tr(child));
                }
                foreach (var entry in scene.Objects)
                {
                    if (entry.ClassId != 4) continue;
                    var t = TransformAsset.Read(scene.At(entry));
                    if (Go(t.GameObject).Name != DoorName) continue;
                    Collect(t);
                    if (pieces.Count > 0) break;
                }
                if (pieces.Count == 0) throw new InvalidDataException($"{DoorName} isn't batched in {Path.GetFileName(path)}");
                foreach (var p in pieces)
                    if (!combined.ContainsKey(p.Combined)) combined[p.Combined] = MeshAsset.Read(scene.At(scene.Find(p.Combined)));
            }
        }

        /// <summary>Main thread: the door's copy, drawn where it is, in the live door's materials; null if not read.</summary>
        internal static GameObject Copy(Transform door, Renderer[] live)
        {
            if (!done || error != null || pieces.Count == 0)
            {
                Plugin.Log.LogInfo("Test corridor: the door's shape wasn't read from the bridge's file" + (error != null ? ": " + error : ""));
                return null;
            }
            if (pieces.Exists(p => p.HasRoot))
            {
                Plugin.Log.LogInfo("Test corridor: the door is batched under a root of its own; not copied");
                return null;
            }
            try
            {
                var root = new GameObject("door copy");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, door.gameObject.scene);
                int made = 0;
                foreach (var piece in pieces)
                {
                    Renderer match = null;
                    foreach (var r in live) if (r.name == piece.Name) { match = r; break; }
                    if (match == null) continue;
                    var go = new GameObject(piece.Name);
                    go.transform.SetParent(root.transform, false);
                    var sliced = Slice(combined[piece.Combined], piece);
                    TestCorridor.Own(sliced);
                    go.AddComponent<MeshFilter>().sharedMesh = sliced;
                    var mr = go.AddComponent<MeshRenderer>();
                    mr.sharedMaterials = match.sharedMaterials;
                    mr.shadowCastingMode = match.shadowCastingMode;
                    mr.receiveShadows = match.receiveShadows;
                    // The bridge's baked light on it, as on the door it copies (the combined mesh's second UVs already
                    // point into the lightmap).
                    mr.lightmapIndex = match.lightmapIndex;
                    mr.lightmapScaleOffset = match.lightmapScaleOffset;
                    made++;
                }
                if (made == 0) { UnityEngine.Object.Destroy(root); return null; }
                return root;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Test corridor: copying the door: " + e.Message);
                return null;
            }
        }

        // The piece's sub-meshes of the combined mesh (in the scene's space), with only the vertices they use.
        private static Mesh Slice(MeshAsset m, Piece piece)
        {
            var map = new Dictionary<int, int>();
            var vertices = new List<Vector3>();
            var normals = m.Has(1) ? new List<Vector3>() : null;
            var uvs = m.Has(3) ? new List<Vector2>() : null;
            var uv2s = m.Has(4) ? new List<Vector2>() : null;
            var tangents = m.Has(7) ? new List<Vector4>() : null;
            var triangles = new List<int[]>();
            foreach (var s in piece.SubMeshes)
            {
                if (s < 0 || s >= m.SubMeshes.Count) continue;
                var sub = m.SubMeshes[s];
                var tri = new int[sub.IndexCount];
                for (int i = 0; i < tri.Length; i++)
                {
                    int v = BitConverter.ToUInt16(m.IndexBuffer, (int)sub.FirstByte + i * 2);
                    if (!map.TryGetValue(v, out var at))
                    {
                        at = map[v] = vertices.Count;
                        var p = m.Get(0, v);
                        vertices.Add(new Vector3(p[0], p[1], p[2]));
                        if (normals != null) { var n = m.Get(1, v); normals.Add(new Vector3(n[0], n[1], n[2])); }
                        if (uvs != null) { var u = m.Get(3, v); uvs.Add(new Vector2(u[0], u[1])); }
                        if (uv2s != null) { var u = m.Get(4, v); uv2s.Add(new Vector2(u[0], u[1])); }
                        if (tangents != null) { var t = m.Get(7, v); tangents.Add(new Vector4(t[0], t[1], t[2], t[3])); }
                    }
                    tri[i] = at;
                }
                triangles.Add(tri);
            }
            var mesh = new Mesh { name = TestCorridor.RootName + " " + piece.Name };
            mesh.vertices = vertices.ToArray();
            if (normals != null) mesh.normals = normals.ToArray();
            if (uvs != null) mesh.uv = uvs.ToArray();
            if (uv2s != null) mesh.uv2 = uv2s.ToArray();
            if (tangents != null) mesh.tangents = tangents.ToArray();
            mesh.subMeshCount = triangles.Count;
            for (int i = 0; i < triangles.Count; i++) mesh.SetTriangles(triangles[i], i);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
