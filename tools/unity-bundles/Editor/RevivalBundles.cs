// Bridge Crew Revival: builds prefabs into an asset bundle the mod can load (BepInEx/bridges/bundles), with the
// settings the game needs. Put this file in an "Editor" folder of a Unity 5.4 project (the game is Unity 5.4.4p1,
// and only loads bundles from Unity 5.4), select one or more prefabs in the Project window, then use
// Bridge Crew Revival > Build bundle from selected prefabs.
//
// What it does:
//  - builds for Windows (StandaloneWindows64, the game's build) with chunk-based (LZ4) compression, which the
//    bridge designer can read (it can't read LZMA, Unity's default);
//  - builds into the project's Temp folder and copies only the bundle out, so no manifest or folder bundle ends up
//    beside it;
//  - remembers the folder it last saved to (point it at the game's BepInEx/bridges/bundles);
//  - says beforehand what won't come through: scripts the game doesn't have (left off in the game), and a Unity
//    version other than 5.4 (the game won't load the bundle at all).
//
// Written for Unity 5.4's C# (C# 4: no string interpolation or expression-bodied members).

using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class RevivalBundles
{
    private const string FolderKey = "BridgeCrewRevival.BundleFolder";
    private const string Menu = "Bridge Crew Revival/";

    [MenuItem(Menu + "Build bundle from selected prefabs")]
    private static void BuildSelected()
    {
        var prefabs = SelectedPrefabs();
        if (prefabs.Count == 0)
        {
            EditorUtility.DisplayDialog("Bridge Crew Revival", "Select one or more prefabs in the Project window first.", "OK");
            return;
        }
        if (!Application.unityVersion.StartsWith("5.4") &&
            !EditorUtility.DisplayDialog("Bridge Crew Revival",
                "This is Unity " + Application.unityVersion + ". The game is Unity 5.4.4 and only loads bundles built with Unity 5.4: " +
                "the bundle will be built, but the game won't load it.", "Build anyway", "Cancel"))
            return;

        var warnings = Warnings(prefabs);
        if (warnings.Count > 0 &&
            !EditorUtility.DisplayDialog("Bridge Crew Revival",
                "In the game:\n\n" + string.Join("\n", warnings.Take(12).ToArray()) + (warnings.Count > 12 ? "\n(and " + (warnings.Count - 12) + " more)" : ""),
                "Build", "Cancel"))
            return;

        string folder = EditorPrefs.GetString(FolderKey, "");
        string suggested = prefabs.Count == 1 ? prefabs[0].name.ToLowerInvariant() : "bridge";
        string target = EditorUtility.SaveFilePanel("Save the bundle (the game's BepInEx/bridges/bundles folder)", folder, suggested + ".bundle", "bundle");
        if (string.IsNullOrEmpty(target)) return;
        EditorPrefs.SetString(FolderKey, Path.GetDirectoryName(target));

        string name = Path.GetFileName(target).ToLowerInvariant();
        string temp = Path.Combine(Path.Combine(Directory.GetCurrentDirectory(), "Temp"), "RevivalBundles");
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        Directory.CreateDirectory(temp);

        var build = new AssetBundleBuild
        {
            assetBundleName = name,
            assetNames = prefabs.Select(p => AssetDatabase.GetAssetPath(p)).ToArray(),
        };
        var manifest = BuildPipeline.BuildAssetBundles(temp, new[] { build },
            BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
        string built = Path.Combine(temp, name);
        if (manifest == null || !File.Exists(built))
        {
            EditorUtility.DisplayDialog("Bridge Crew Revival", "The bundle wasn't built: the Console says why.", "OK");
            return;
        }
        File.Copy(built, target, true);
        Directory.Delete(temp, true);

        string list = string.Join("\n", build.assetNames.Select(a => "  " + a.ToLowerInvariant()).ToArray());
        Debug.Log("Bridge Crew Revival: built " + target + " with:\n" + list);
        EditorUtility.DisplayDialog("Bridge Crew Revival",
            "Built " + Path.GetFileName(target) + " (" + (new FileInfo(target).Length / 1024) + " KB) with:\n\n" + list +
            "\n\nIn the bridge designer: Add parts > Bundles.", "OK");
    }

    [MenuItem(Menu + "Build bundle from selected prefabs", true)]
    private static bool CanBuildSelected()
    {
        return SelectedPrefabs().Count > 0;
    }

    private static List<GameObject> SelectedPrefabs()
    {
        var list = new List<GameObject>();
        foreach (var o in Selection.GetFiltered(typeof(GameObject), SelectionMode.Assets))
        {
            var go = o as GameObject;
            if (go != null && PrefabUtility.GetPrefabType(go) == PrefabType.Prefab && !list.Contains(go)) list.Add(go);
        }
        return list;
    }

    // What won't come through to the game: scripts of this project's (the game has its own, not these), and
    // missing ones.
    private static List<string> Warnings(List<GameObject> prefabs)
    {
        var warnings = new List<string>();
        foreach (var prefab in prefabs)
            foreach (var behaviour in prefab.GetComponentsInChildren<MonoBehaviour>(true))
            {
                string where = prefab.name + "/" + PathIn(prefab.transform, behaviour == null ? null : behaviour.transform);
                if (behaviour == null) { warnings.Add("- " + prefab.name + ": a missing script is left off"); continue; }
                string ns = behaviour.GetType().Namespace ?? "";
                if (!ns.StartsWith("UnityEngine"))
                    warnings.Add("- " + where + ": script " + behaviour.GetType().Name + " is left off (the game doesn't have it)");
            }
        return warnings;
    }

    private static string PathIn(Transform root, Transform t)
    {
        if (t == null || t == root) return "";
        var names = new List<string>();
        for (var a = t; a != null && a != root; a = a.parent) names.Insert(0, a.name);
        return string.Join("/", names.ToArray());
    }
}
