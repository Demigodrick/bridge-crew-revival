// Bridge Crew Revival: a whole bridge scene as an asset bundle, for a ship class's bridge of its own (ship.json's
// "bridge": { "bundle": ..., "scene": ... }). Unity 5.4 (the game's engine).
//
// In the editor: select the scene in the Project window, then Bridge Crew Revival > Build bridge scene bundle.
// From a command line (batch mode):
//   Unity.exe -batchmode -quit -projectPath <project> -executeMethod RevivalSceneBundles.BuildFromCommandLine
//             -revivalScene Assets/Bridges/Bridge_scene_Intrepid.unity -revivalOut <folder> [-revivalBundle intrepid.bridge]
// The bundle is written to the folder under its name (default: the scene's name in lower case, ".bridge").
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class RevivalSceneBundles
{
    private const string Menu = "Bridge Crew Revival/";

    [MenuItem(Menu + "Build bridge scene bundle")]
    private static void BuildSelected()
    {
        var scene = AssetDatabase.GetAssetPath(Selection.activeObject);
        if (string.IsNullOrEmpty(scene) || !scene.EndsWith(".unity")) { EditorUtility.DisplayDialog("Bridge scene bundle", "Select a scene first.", "OK"); return; }
        string folder = EditorUtility.SaveFolderPanel("Where the bundle goes (the ship's folder)", EditorPrefs.GetString("RevivalSceneBundlesOut", ""), "");
        if (string.IsNullOrEmpty(folder)) return;
        EditorPrefs.SetString("RevivalSceneBundlesOut", folder);
        BuildScene(scene, folder, null);
    }

    public static void BuildFromCommandLine()
    {
        string scene = Arg("-revivalScene"), folder = Arg("-revivalOut"), name = Arg("-revivalBundle");
        if (scene == null || folder == null) throw new ArgumentException("needs -revivalScene <scene asset path> and -revivalOut <folder>");
        BuildScene(scene, folder, name);
    }

    private static string Arg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == name) return args[i + 1];
        return null;
    }

    public static void BuildScene(string scene, string folder, string name)
    {
        if (name == null) name = Path.GetFileNameWithoutExtension(scene).ToLowerInvariant() + ".bridge";
        string temp = Path.Combine(Path.GetTempPath(), "RevivalSceneBundle");
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        Directory.CreateDirectory(temp);
        var build = new AssetBundleBuild { assetBundleName = name, assetNames = new[] { scene } };
        // The game's own scripts can come in a project only under another file name (Unity keeps "Assembly-CSharp" for
        // what it compiles, and its compiler can't read the game's obfuscated one): Plugins/AssemblyXCSharp.dll. The
        // bundle then names that file for those scripts; built uncompressed, it's put back as the game's (the same
        // length, so the bytes just swap).
        bool renamed = File.Exists("Assets/Plugins/AssemblyXCSharp.dll");
        var options = renamed ? BuildAssetBundleOptions.UncompressedAssetBundle : BuildAssetBundleOptions.ChunkBasedCompression;
        var manifest = BuildPipeline.BuildAssetBundles(temp, new[] { build }, options, BuildTarget.StandaloneWindows64);
        if (manifest == null) throw new Exception("the bundle wasn't built (see the log above)");
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, name);
        File.Copy(Path.Combine(temp, name), target, true);
        if (renamed) Debug.Log("Bridge Crew Revival: " + Swap(target, "AssemblyXCSharp.dll", "Assembly-CSharp.dll") + " script reference(s) put back to Assembly-CSharp.dll");
        Debug.Log("Bridge Crew Revival: " + scene + " built into " + target + " (" + new FileInfo(target).Length / 1048576f + " MB)");
    }

    /// <summary>Every <paramref name="from"/> in the file becomes <paramref name="to"/> (the same length); how many.</summary>
    private static int Swap(string file, string from, string to)
    {
        byte[] a = System.Text.Encoding.ASCII.GetBytes(from), b = System.Text.Encoding.ASCII.GetBytes(to);
        if (a.Length != b.Length) throw new ArgumentException("not the same length");
        int count = 0;
        using (var s = new FileStream(file, FileMode.Open, FileAccess.ReadWrite))
        {
            var buffer = new byte[1 << 20];
            long at = 0;
            while (true)
            {
                s.Position = at;
                int n = s.Read(buffer, 0, buffer.Length);
                if (n < a.Length) break;
                for (int i = 0; i + a.Length <= n; i++)
                {
                    int k = 0;
                    while (k < a.Length && buffer[i + k] == a[k]) k++;
                    if (k < a.Length) continue;
                    s.Position = at + i;
                    s.Write(b, 0, b.Length);
                    count++;
                }
                if (n < buffer.Length) break;
                at += n - (a.Length - 1); // overlap, so a name across two reads is found
            }
        }
        return count;
    }
}
