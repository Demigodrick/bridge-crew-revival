using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// Prefabs from Unity asset bundles, for bridge layouts (BridgeParts): a layout's added part with "bundle" (a file
    /// in BepInEx/bridges/bundles) and "asset" (a prefab in it, by the name it was built under, e.g.
    /// "assets/bridges/mybridge.prefab", or its short name) is that prefab, made as it was built: its meshes,
    /// materials, colliders and lights. Scripts it had that the game doesn't have are left off (Unity says so in the
    /// log). Its renderers are lit by the bridge's light probes, as anything that moves (not the bridge's lightmaps).
    ///
    /// A bundle has to be built with Unity 5.4 (the game's own engine, 5.4.4p1; a newer one's bundles don't load) for
    /// Windows (StandaloneWindows64). Each is loaded once and kept, and loaded again when its file changes (a bundle
    /// built again while the game runs shows at the next layout change or bridge load).
    /// </summary>
    internal static class BundleParts
    {
        private static string Folder => Path.Combine(Path.Combine(Paths.BepInExRootPath, "bridges"), "bundles");

        private sealed class Loaded
        {
            public AssetBundle Bundle;
            public DateTime Stamp;
        }

        private static readonly Dictionary<string, Loaded> loaded = new Dictionary<string, Loaded>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The prefab made under <paramref name="parent"/> (its own position, turn and scale kept, as the designer shows
        /// it); how many things it draws (at least 1 when it's made: it may only bring colliders or lights).
        /// Throws, saying why, when it can't be made.
        /// </summary>
        internal static int Instantiate(string file, string asset, Transform parent)
        {
            var bundle = Load(file);
            var prefab = bundle.LoadAsset<GameObject>(asset);
            if (prefab == null) prefab = bundle.LoadAsset<GameObject>(Path.GetFileNameWithoutExtension(asset));
            if (prefab == null) throw new InvalidDataException($"no prefab {asset} in it (it has: {string.Join(", ", bundle.GetAllAssetNames())})");
            var go = UnityEngine.Object.Instantiate(prefab);
            go.name = prefab.name;
            go.transform.SetParent(parent, false);
            int drawn = 0;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                r.lightmapIndex = -1; // not in the bridge's baked light
                drawn++;
            }
            return Math.Max(1, drawn);
        }

        private static AssetBundle Load(string file)
        {
            string folder = Path.GetFullPath(Folder);
            string full = Path.GetFullPath(Path.Combine(folder, file));
            if (!full.StartsWith(folder + Path.DirectorySeparatorChar)) throw new InvalidDataException("not in the bundles folder");
            if (!File.Exists(full)) throw new FileNotFoundException("not in " + folder);
            var stamp = File.GetLastWriteTimeUtc(full);
            if (loaded.TryGetValue(full, out var had))
            {
                if (had.Bundle != null && had.Stamp == stamp) return had.Bundle;
                // Changed: the old one goes first (Unity won't load two bundles with the same contents). What was made
                // from it stays as it is until the layout makes its parts again.
                if (had.Bundle != null) had.Bundle.Unload(false);
                loaded.Remove(full);
            }
            var bundle = AssetBundle.LoadFromFile(full);
            if (bundle == null)
                throw new InvalidDataException("Unity couldn't load it: it has to be built with Unity 5.4 for Windows (the log above may say more)");
            loaded[full] = new Loaded { Bundle = bundle, Stamp = stamp };
            Plugin.Log.LogInfo($"Bridge bundles: {file} loaded ({bundle.GetAllAssetNames().Length} asset(s))");
            return bundle;
        }
    }
}
