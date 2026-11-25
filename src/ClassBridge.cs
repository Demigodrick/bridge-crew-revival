using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StbcRevival
{
    /// <summary>
    /// A bridge of its own for a ship class of ours (CustomShip: its ship.json names a bundle and a scene): a Unity 5.4
    /// asset bundle in the ship's folder holding a whole bridge scene, loaded instead of the game's.
    ///
    /// The game picks its bridge scene by name (Loader.CurrentBridgeScene), and its override (Loader._overrideBridgeScene)
    /// wins over the Aegis's, the original Enterprise's and the Next Generation's; the host sends that name to everyone
    /// (NetworkLoadLevel.LoadAdditiveLevelRPC), and each loads it with Unity's own scene loading, which finds a scene in a
    /// loaded bundle by its name. So: while the crew flies a class with a bridge of its own (not a campaign mission), its
    /// bundle is loaded and the override set to its scene, on every PC that has the bundle; it's only changed while no
    /// bridge is loaded (the game finds the loaded bridge by that name). A crewmate without the bundle can't load that
    /// scene: the name the host sends is swapped for the class's game ship's own bridge (the Aegis's or the Next
    /// Generation's), and their override stays off, so they get that bridge.
    ///
    /// The background crew (RedShirtStation) are placed by their animations, relative to their seat objects, which the
    /// bridge moves to where its own consoles are: the scene marks where each should end up with an object named
    /// "RevivalCrewSpot|&lt;seat object's name&gt;|stand" (or "|seat", or "|hide"), on the floor, facing their console.
    /// Once a seat's crew member has spawned, their body (hips, feet) is measured and the seat object moved and turned so
    /// they stand or sit there. One whose pose doesn't fit the spot (seated at a standing spot, or the other way round),
    /// or marked "hide", isn't drawn.
    /// </summary>
    internal static class ClassBridge
    {
        private static readonly System.Reflection.FieldInfo OverrideField = AccessTools.Field(typeof(Loader), "EMGOKPOCDAJ"); // _overrideBridgeScene
        private static readonly System.Reflection.FieldInfo TngSceneField = AccessTools.Field(typeof(Loader), "_tngBridgeScene");
        private static readonly System.Reflection.FieldInfo AegisSceneField = AccessTools.Field(typeof(Loader), "_aegisBridgeScene");

        private sealed class Loaded { public AssetBundle Bundle; public DateTime Stamp; public string[] Scenes; }
        private static readonly Dictionary<string, Loaded> bundles = new Dictionary<string, Loaded>(StringComparer.OrdinalIgnoreCase);

        private static string ours;   // the override we set (null: none of ours)
        private static float nextCheck;

        internal static void Patch(Harmony harmony)
        {
            if (OverrideField == null) { Plugin.Log.LogWarning("Class bridges: the game's bridge override wasn't found; our classes fly their game ship's bridge"); return; }
            // The bridge comes as the main level (LoadMainLevelRPC); additive levels are swapped too, should one be a bridge.
            var loadLevel = AccessTools.TypeByName("NetworkLoadLevel");
            foreach (var name in new[] { "LoadMainLevelRPC", "LoadAdditiveLevelRPC" })
            {
                var rpc = AccessTools.Method(loadLevel, name);
                if (rpc != null) harmony.Patch(rpc, prefix: new HarmonyMethod(typeof(ClassBridge), nameof(LoadAdditive_Prefix)));
                else Plugin.Log.LogWarning($"Class bridges: NetworkLoadLevel.{name} wasn't found; a crewmate without a class's bridge can't fall back there");
            }
        }

        /// <summary>Every frame: the background crew onto the bridge's spots; twice a second: the override follows the class the crew flies (only while no bridge is loaded).</summary>
        internal static void Update()
        {
            try { PlaceCrew(); } catch (Exception e) { Plugin.Log.LogWarning("Class bridges: placing the crew: " + e.Message); spots = new List<Spot>(); }
            if (OverrideField == null || Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 0.5f;
            if (BridgeLoaded()) return;
            var ship = CustomShip.InUse;
            string want = null;
            if (ship != null && ship.BridgeScene != null && Ready(ship)) want = ship.BridgeScene;
            if (want == ours) return;
            var current = OverrideField.GetValue(null) as string;
            if (want != null)
            {
                if (current != null && current != ours) { Plugin.Log.LogInfo($"Class bridges: the game's own override ({current}) is set; {ship.Name}'s bridge left off"); return; }
                OverrideField.SetValue(null, want);
                Plugin.Log.LogInfo($"Class bridges: the {ship.Name}'s bridge ({want}) is the one loaded");
            }
            else if (current == ours)
            {
                OverrideField.SetValue(null, null);
                Plugin.Log.LogInfo("Class bridges: back to the game's own bridges");
            }
            ours = want;
        }

        /// <summary>
        /// A class bridge's own art (its models' mesh colliders, in its scene): the bundle puts them on Ignore Raycast so
        /// the game's pointer and hand rays pass them (a chair arm over the captain's menu); Bridge Walk still walks on them.
        /// </summary>
        internal static bool IsOwnArt(Collider c)
        {
            if (!(c is MeshCollider)) return false;
            string name = c.gameObject.scene.name;
            foreach (var had in bundles.Values)
                if (had.Scenes != null) foreach (var scene in had.Scenes)
                    if (string.Equals(Path.GetFileNameWithoutExtension(scene), name, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>The class's bundle is loaded (loading it if needed, again if its file changed) and holds its scene.</summary>
        private static bool Ready(CustomShip ship)
        {
            string path = ship.BridgeBundlePath;
            try
            {
                if (path == null || !File.Exists(path)) return false;
                var stamp = File.GetLastWriteTimeUtc(path);
                if (!bundles.TryGetValue(path, out var had) || had.Bundle == null || had.Stamp != stamp)
                {
                    if (had?.Bundle != null) had.Bundle.Unload(true);
                    bundles.Remove(path);
                    var bundle = AssetBundle.LoadFromFile(path);
                    if (bundle == null) { Plugin.Log.LogWarning($"Class bridges: {path}: Unity couldn't load it (it has to be built with Unity 5.4 for Windows)"); bundles[path] = new Loaded { Stamp = stamp }; return false; }
                    had = bundles[path] = new Loaded { Bundle = bundle, Stamp = stamp, Scenes = bundle.GetAllScenePaths() };
                    Plugin.Log.LogInfo($"Class bridges: {Path.GetFileName(path)} loaded: {string.Join(", ", had.Scenes)}");
                }
                if (had.Bundle == null) return false;
                foreach (var scene in had.Scenes)
                    if (string.Equals(Path.GetFileNameWithoutExtension(scene), ship.BridgeScene, StringComparison.Ordinal)) return true;
                return false;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Class bridges: {path}: {e.Message}"); return false; }
        }

        private static bool BridgeLoaded()
        {
            try { return GamePhaseManager.IEFBKOMBFGO != null && GamePhaseManager.IEFBKOMBFGO.HLANJBENLKE; } catch { return false; }
        }

        /// <summary>A scene the host sends that this PC can't load (a class's bridge whose bundle isn't here): its game ship's bridge instead.</summary>
        private static void LoadAdditive_Prefix(ref string __0)
        {
            try
            {
                if (string.IsNullOrEmpty(__0)) return;
                foreach (var had in bundles.Values)
                    if (had.Scenes != null) foreach (var scene in had.Scenes)
                        if (string.Equals(Path.GetFileNameWithoutExtension(scene), __0, StringComparison.Ordinal)) return; // ours, loaded here
                if (Application.CanStreamedLevelBeLoaded(__0)) return;
                foreach (var ship in CustomShip.All)
                {
                    if (ship.BridgeScene != __0) continue;
                    var loader = Loader.EJLNDBGLIFL;
                    var field = ship.Base == CustomShip.ShipBase.Tng ? TngSceneField : AegisSceneField;
                    var instead = loader != null ? field?.GetValue(loader) as string : null;
                    if (instead == null) return;
                    Plugin.Log.LogWarning($"Class bridges: the host's {ship.Name} bridge ({__0}) isn't on this PC: loading {instead} instead");
                    __0 = instead;
                    return;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Class bridges: " + e.Message); }
        }
    
        // ------------------------------------------------------------------------------------------- the crew

        private sealed class Spot
        {
            public Transform Marker, Seat;
            public string Mode;              // stand, seat or hide
            public float SpawnedAt = -1f, WaitingSince = -1f;
            public bool Placed, Hidden;
        }

        private static Scene crewScene;
        private static List<Spot> spots;
        private static float nextHide;

        private static void PlaceCrew()
        {
            var ship = CustomShip.InUse;
            var scene = ship != null && ship.BridgeScene != null ? SceneManager.GetSceneByName(ship.BridgeScene) : default(Scene);
            if (!scene.IsValid() || !scene.isLoaded) { spots = null; return; }
            if (spots == null || crewScene != scene)
            {
                crewScene = scene;
                spots = new List<Spot>();
                var all = new List<Transform>();
                foreach (var root in scene.GetRootGameObjects()) all.AddRange(root.GetComponentsInChildren<Transform>(true));
                foreach (var t in all)
                {
                    if (!t.name.StartsWith("RevivalCrewSpot|", StringComparison.Ordinal)) continue;
                    var parts = t.name.Split('|');
                    if (parts.Length < 3) continue;
                    var seat = all.Find(x => x.name == parts[1]);
                    if (seat == null) { Plugin.Log.LogWarning($"Class bridges: crew spot for {parts[1]}: no such seat in {scene.name}"); continue; }
                    spots.Add(new Spot { Marker = t, Seat = seat, Mode = parts[2] });
                }
                if (spots.Count > 0) Plugin.Log.LogInfo($"Class bridges: {spots.Count} background crew spot(s) on {scene.name}");
            }
            bool hideNow = Time.unscaledTime >= nextHide;
            if (hideNow) nextHide = Time.unscaledTime + 1f;
            foreach (var spot in spots)
            {
                if (spot.Seat == null) continue;
                if (spot.Hidden) { if (hideNow) foreach (var r in spot.Seat.GetComponentsInChildren<Renderer>(true)) r.enabled = false; continue; } // a new one may spawn
                if (spot.Placed) continue;
                // The game drives these animators itself (switched off as components): any found, on or off.
                var animator = spot.Seat.GetComponentInChildren<Animator>(true);
                if (spot.WaitingSince < 0f) spot.WaitingSince = Time.unscaledTime;
                if (animator == null || !animator.isHuman)
                {
                    if (Time.unscaledTime - spot.WaitingSince > 20f && spot.WaitingSince > 0f)
                    {
                        Plugin.Log.LogInfo($"Class bridges: no crew member under {spot.Seat.name} after 20 s" + (animator != null ? " (its animator isn't humanoid)" : ""));
                        spot.WaitingSince = 0f; // said once
                    }
                    continue;
                }
                if (spot.SpawnedAt < 0f) spot.SpawnedAt = Time.unscaledTime;
                if (Time.unscaledTime - spot.SpawnedAt < 1f) continue; // its idle under way
                // Hips and feet; some of the game's crew have bones left out (it strips a few for speed), so short of feet
                // the lower legs, short of those a sitting or standing body's usual height over the floor.
                var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                Vector3 hipsAt = hips != null ? hips.position : animator.bodyPosition;
                var left = animator.GetBoneTransform(HumanBodyBones.LeftFoot) ?? animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
                var right = animator.GetBoneTransform(HumanBodyBones.RightFoot) ?? animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
                float floor; string from;
                if (left != null || right != null)
                {
                    var low = left != null && right != null ? (left.position.y < right.position.y ? left : right) : (left ?? right);
                    bool foot = low == animator.GetBoneTransform(HumanBodyBones.LeftFoot) || low == animator.GetBoneTransform(HumanBodyBones.RightFoot);
                    floor = low.position.y - (foot ? 0.09f : 0.45f); // the ankle bones stand about 9 cm up, the knees about 45
                    from = foot ? "feet" : "lower legs";
                }
                else
                {
                    floor = hipsAt.y - (spot.Mode == "seat" ? 0.5f : 0.95f);
                    from = "no leg bones: the spot's pose taken";
                }
                bool seated = hipsAt.y - floor < 0.75f;
                if (spot.Mode != "hide" && left == null && right == null) seated = spot.Mode == "seat";
                string pose = seated ? "seat" : "stand";
                if (spot.Mode == "hide" || spot.Mode != pose)
                {
                    spot.Hidden = true;
                    foreach (var r in spot.Seat.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                    Plugin.Log.LogInfo($"Class bridges: {spot.Seat.name}'s crew member ({pose}ing) left out" + (spot.Mode == "hide" ? "" : $": the spot is to {spot.Mode} at"));
                    continue;
                }
                // Turned about the hips to the spot's facing, then moved: hips over the spot, feet on its floor.
                // Facing: square to the line from the left hip to the right one (the animator's own body rotation isn't
                // kept up while the game runs it).
                var leftHip = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
                var rightHip = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
                var across = rightHip != null && leftHip != null ? rightHip.position - leftHip.position : (hips != null ? hips.right : animator.bodyRotation * Vector3.right);
                across.y = 0f;
                var facing = Vector3.Cross(across.normalized, Vector3.up);
                float turnBy = Mathf.DeltaAngle(Mathf.Atan2(facing.x, facing.z) * Mathf.Rad2Deg, spot.Marker.eulerAngles.y);
                var turn = Quaternion.Euler(0f, turnBy, 0f);
                var pivot = hipsAt;
                spot.Seat.position = pivot + turn * (spot.Seat.position - pivot);
                spot.Seat.rotation = turn * spot.Seat.rotation;
                var at = spot.Marker.position;
                spot.Seat.position += new Vector3(at.x - pivot.x, at.y - floor, at.z - pivot.z);
                spot.Placed = true;
                Plugin.Log.LogInfo($"Class bridges: {spot.Seat.name}'s crew member ({pose}ing; floor from {from}) moved {Vector3.Distance(pivot, spot.Marker.position):0.0} m to their spot, turned {turnBy:0}°");
            }
        }
}
}
