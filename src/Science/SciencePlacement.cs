using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StbcRevival.Science
{
    /// <summary>
    /// Where the science console stands on each bridge: BepInEx/bridges/science/&lt;layout name&gt;.json (made with the
    /// bridge designer, shipped with the mod; format in SciencePlacementFile). A class bridge of ours
    /// (Bridge_scene_02-Nova) without a file of its own takes the Aegis's (Bridge_scene_02.json). Everyone reads their
    /// own shipped copy: a host's layout (BridgeShare) never replaces it, and your own layouts (BridgeLayout) never read
    /// that folder. [Debug] SciencePlacement, when it names a file, is put on whatever bridge is loaded instead.
    ///
    /// The file's added parts (the console's piece) are built as a set of their own (BridgeParts "science"): a layout
    /// put on again leaves them where they are, and the other way round. The screen and the stand point are two plain
    /// objects under one of ours at the scene's root, placed in the scene's space as the file has them.
    /// </summary>
    internal static class SciencePlacement
    {
        internal const string PartsSet = "science";
        internal const string HolderName = "RevivalSciencePlacement";

        // The game's bridges a console can stand on (a class of ours flies the Aegis's: Bridge_scene_02).
        private static readonly string[] BridgeScenes = { "Bridge_scene_02", "Retro_Bridge_Scene", "Bridge_scene_1701D", "Bridge_scene_Holodeck" };
        private const string ClassBridge = "Bridge_scene_02";

        private static BridgeParts Parts => BridgeParts.For(PartsSet);
        private static string Folder => Path.Combine(Path.Combine(Paths.BepInExRootPath, "bridges"), "science");

        private static Scene scene;
        private static int boundRoot;       // the scene's first root object: changes when it's loaded again
        private static string boundKey;     // which file (and which version of it) is on
        private static GameObject holder;
        private static Transform anchor, stand;
        private static Vector2 size;
        private static float nextLook;

        /// <summary>
        /// The loaded bridge has a science console: one is placed, or a placement file for a loaded bridge reads well
        /// (asked as a Patrol starts, maybe before this has put it on).
        /// </summary>
        internal static bool HasForCurrentBridge
        {
            get
            {
                if (anchor != null) return true;
                try
                {
                    foreach (var s in LoadedBridges())
                    {
                        var file = FileFor(LayoutName(s));
                        if (file != null) { SciencePlacementFile.Parse(File.ReadAllText(file)); return true; }
                    }
                }
                catch { }
                return false;
            }
        }

        /// <summary>
        /// Where the screen goes, or null if no console is placed. A plain object at the scene's root: its middle is
        /// the screen's middle, its up the screen's top, and its forward (+z) points into the screen, away from the
        /// person reading it (as a world-space Canvas under it, at no local turn, reads). Size: ScreenSize.
        /// </summary>
        internal static Transform ScreenAnchor => anchor != null ? anchor : null;

        /// <summary>The screen's width and height in metres (zero if none).</summary>
        internal static Vector2 ScreenSize => anchor != null ? size : Vector2.zero;

        /// <summary>
        /// Where the person at the console stands (on the floor), in the space Bridge Walk sends positions in
        /// (Walker.Pack, BridgeWalkNet): relative to the player characters' parent, the bridge's own basis that moves
        /// with the ship (the scene's space if there's no character yet). Compare a crewmate's sent position with it
        /// as it is (DistanceFromBridgeLocal); for a position in the world, DistanceFrom. Zero if no console is placed.
        /// </summary>
        internal static Vector3 StandPoint
        {
            get
            {
                if (stand == null) return Vector3.zero;
                var basis = Basis();
                return basis != null ? basis.InverseTransformPoint(stand.position) : stand.position;
            }
        }

        /// <summary>How far a point in the world is from the stand point, along the floor (height left out); infinity if no console is placed.</summary>
        internal static float DistanceFrom(Vector3 worldPos)
        {
            if (stand == null) return float.PositiveInfinity;
            var basis = Basis();
            Vector3 up = basis != null ? basis.up : Vector3.up;
            return Vector3.ProjectOnPlane(worldPos - stand.position, up).magnitude;
        }

        /// <summary>How far a position in Bridge Walk's space (as StandPoint, Walker.Pack) is from the stand point, along the floor; infinity if none.</summary>
        internal static float DistanceFromBridgeLocal(Vector3 local)
        {
            if (stand == null) return float.PositiveInfinity;
            var d = local - StandPoint;
            d.y = 0f;
            return d.magnitude;
        }

        // The player characters' parent: the space Bridge Walk's positions are in.
        private static Transform Basis()
        {
            try
            {
                var me = StbcRevival.Walking.Bridge.Local();
                return me != null && me.PawnObject != null ? me.PawnObject.transform.parent : null;
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------------------------------- every frame
        /// <summary>Every frame (from BridgeLayout.Update): builds what's been read; once a second, binds the loaded bridge.</summary>
        internal static void Update()
        {
            Parts.Update();
            if (Time.unscaledTime < nextLook) return;
            nextLook = Time.unscaledTime + 1f;

            if (boundRoot != 0)
            {
                int root = scene.IsValid() && scene.isLoaded ? RootId(scene) : 0;
                if (root == boundRoot && KeyFor(scene, out _, out _) == boundKey) return;
                if (root == boundRoot) Plugin.Log.LogInfo($"Science console: {scene.name}'s placement changed: put on again");
                Forget();
            }
            foreach (var s in LoadedBridges())
            {
                int root = RootId(s);
                if (root == 0) continue;
                string key = KeyFor(s, out var file, out var name);
                if (file == null) continue;
                scene = s;
                boundRoot = root;
                boundKey = key;     // a file that doesn't read isn't tried again until it changes
                Load(file, name);
                return;
            }
        }

        private static IEnumerable<Scene> LoadedBridges()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.isLoaded && Array.IndexOf(BridgeScenes, s.name) >= 0) yield return s;
            }
        }

        private static string LayoutName(Scene s) => BridgeLayout.LayoutName(s.name);

        // Which file is on for a scene, and its version (its time): changes when it's saved again.
        private static string KeyFor(Scene s, out string file, out string name)
        {
            name = LayoutName(s);
            file = FileFor(name);
            if (file == null) return name + ":none";
            try { return name + ":" + file + ":" + File.GetLastWriteTimeUtc(file).Ticks; }
            catch { return name + ":" + file; }
        }

        /// <summary>The placement file for a layout name: the testing one if set, its own, or for a class's bridge the Aegis's; null if none.</summary>
        private static string FileFor(string name)
        {
            string test = Plugin.ScienceTestPlacement != null ? Plugin.ScienceTestPlacement.Value : null;
            if (!string.IsNullOrEmpty(test)) return File.Exists(test) ? test : null;
            string folder = Path.GetFullPath(Folder);
            string file = Path.GetFullPath(Path.Combine(folder, name + ".json"));
            // Only a file right in the science folder.
            if (!string.Equals(Path.GetDirectoryName(file), folder.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return null;
            if (File.Exists(file)) return file;
            if (!name.StartsWith(ClassBridge + "-", StringComparison.Ordinal)) return null;
            string aegis = Path.Combine(folder, ClassBridge + ".json");
            return File.Exists(aegis) ? aegis : null;
        }

        private static int RootId(Scene s)
        {
            var roots = s.GetRootGameObjects();
            return roots.Length > 0 ? roots[0].GetInstanceID() : 0;
        }

        // ------------------------------------------------------------------------------------------- the file
        private static void Load(string file, string name)
        {
            SciencePlacementFile placement;
            List<BridgeParts.Spec> added;
            try
            {
                string text = File.ReadAllText(file);
                placement = SciencePlacementFile.Parse(text);
                BridgeLayout.ParseLayout(text, out added); // the added parts, read as a layout's
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Science console: {file} for {name} isn't a placement this version can read: {e.Message}"); return; }

            holder = new GameObject(HolderName);
            SceneManager.MoveGameObjectToScene(holder, scene);
            anchor = new GameObject("ScreenAnchor").transform;
            anchor.SetParent(holder.transform, false);
            anchor.localPosition = Vec(placement.ScreenPivot);
            anchor.localRotation = Quaternion.AngleAxis(placement.ScreenTurn, Vector3.up) * Quaternion.AngleAxis(placement.ScreenTilt, Vector3.right);
            stand = new GameObject("StandPoint").transform;
            stand.SetParent(holder.transform, false);
            stand.localPosition = Vec(placement.Stand);
            size = new Vector2(placement.ScreenWidth, placement.ScreenHeight);
            Parts.Start(added, scene);
            Plugin.Log.LogInfo($"Science console: {Path.GetFileName(file)} on {name}: screen {size.x:0.##} x {size.y:0.##} m, {added.Count} added part(s)");
        }

        private static Vector3 Vec(float[] a) => new Vector3(a[0], a[1], a[2]);

        /// <summary>The scene's gone, or its file changed: what we made goes.</summary>
        private static void Forget()
        {
            Parts.Clear();
            if (holder != null) UnityEngine.Object.Destroy(holder);
            holder = null;
            anchor = stand = null;
            size = Vector2.zero;
            boundRoot = 0;
            boundKey = null;
        }
    }
}
