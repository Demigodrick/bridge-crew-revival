using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Sector;
using Sector.Spacecraft;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// Test: the crew flies an Excelsior. Until it has a bridge of its own, the bridge is the training holodeck
    /// (the game's Bridge_scene_Holodeck, with the Aegis consoles), and outside the player ship wears the
    /// game's own Excelsior-class model (from whichever Starfleet ship prefab carries it).
    ///   - [Ships] Excelsior (the config file only, for testing): the host's choice, put on the room as "SHP" so the whole crew
    ///     agrees; every game picks its bridge itself (Loader.get_CurrentBridgeScene), so all read the room.
    ///   - On the holodeck the game behaves a little as in training (a few console messages are skipped).
    ///   - The model swap: when a player ship is announced (SectorSpawnManager.CJDNEDMKCJJ), the Excelsior's
    ///     model (its renderers only, no scripts or colliders) is added in place of the Aegis hull, which is
    ///     hidden; collisions and effects stay the Aegis's.
    /// </summary>
    internal static class Excelsior
    {
        internal const string Key = "SHP";
        internal const string Name = "Excelsior";
        private const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static readonly string[] Candidates =
        {
            "Raleigh", "Raleigh TNG", "Huxley", "Huxley TNG", "Saladin", "Saladin TNG", "Nicholson", "Kobayashi Maru",
        };

        private static readonly MethodInfo HolodeckGetter = AccessTools.Method(typeof(Loader), "get_HolodeckBridgeScene");
        private static GameObject model;      // the Excelsior's model, from a ship prefab (not instantiated)
        private static bool searched;

        internal static void Patch(Harmony harmony)
        {
            var bridge = AccessTools.PropertyGetter(typeof(Loader), "CurrentBridgeScene") ?? AccessTools.Method(typeof(Loader), "get_CurrentBridgeScene");
            var announce = AccessTools.Method(typeof(SectorSpawnManager), "CJDNEDMKCJJ", new[] { typeof(SectorBody) });
            if (bridge == null || announce == null)
            {
                Plugin.Log.LogWarning("Excelsior: the bridge choice or ship announcement wasn't found; no Excelsior");
                return;
            }
            harmony.Patch(bridge, prefix: new HarmonyMethod(typeof(Excelsior), nameof(Bridge_Prefix)));
            harmony.Patch(announce, postfix: new HarmonyMethod(typeof(Excelsior), nameof(Announce_Postfix)));
        }

        // ---- The crew's choice ------------------------------------------------------------------

        private static object Room => Patches.PhotonNetworkType?.GetMethod("get_room", Static)?.Invoke(null, null);

        /// <summary>The room this game is in flies the Excelsior.</summary>
        internal static bool Active
        {
            get
            {
                var room = Room;
                return room != null && (room.GetType().GetMethod("get_customProperties", All)?.Invoke(room, null) as IDictionary)?[Key] as string == Name;
            }
        }

        /// <summary>Each frame: the host puts their choice on the room.</summary>
        internal static void Update()
        {
            var room = Room;
            if (room == null || !(Patches.PhotonNetworkType.GetMethod("get_isMasterClient", Static)?.Invoke(null, null) is bool master) || !master) return;
            bool want = Plugin.ExcelsiorTest.Value;
            if (want == Active) return;
            if (PhotonProps.Set(room, Key, want ? Name : ""))
                Plugin.Log.LogInfo($"Excelsior: the crew flies {(want ? "the Excelsior (holodeck bridge)" : "the game's own ship")}");
        }

        private static bool Bridge_Prefix(ref string __result)
        {
            if (!Active) return true;
            __result = HolodeckGetter?.Invoke(null, null) as string ?? "Bridge_scene_Holodeck";
            return false;
        }

        // ---- The model ----------------------------------------------------------------------------

        private static void Announce_Postfix(SectorBody __0)
        {
            if (!(__0 is PlayerSpacecraft ship) || !Active) return;
            try
            {
                Dress(ship);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Excelsior: model swap failed: " + e);
            }
        }

        private static GameObject FindModel()
        {
            if (searched) return model;
            searched = true;
            foreach (var name in Candidates)
            {
                var prefab = Resources.Load<GameObject>("Sector/Ships/Ship - " + name);
                if (prefab == null) continue;
                var renderers = prefab.GetComponentsInChildren<MeshFilter>(true)
                    .Where(f => f.sharedMesh != null && f.sharedMesh.name.IndexOf("Excelsior", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (renderers.Count == 0) continue;
                model = CommonParent(renderers.Select(r => r.transform).ToList(), prefab.transform).gameObject;
                Plugin.Log.LogInfo($"Excelsior: model found in Ship - {name} ('{model.name}', {renderers.Count} meshes)");
                return model;
            }
            Plugin.Log.LogWarning("Excelsior: no ship prefab with the Excelsior model found");
            return null;
        }

        // The deepest transform holding all of them (the model's root, not the ship's).
        private static Transform CommonParent(System.Collections.Generic.List<Transform> parts, Transform stop)
        {
            var candidate = parts[0];
            while (candidate != null && candidate != stop && !parts.All(p => p == candidate || p.IsChildOf(candidate))) candidate = candidate.parent;
            return candidate ?? stop;
        }

        private static void Dress(PlayerSpacecraft ship)
        {
            if (ship.GetComponentsInChildren<Transform>(true).Any(t => t.name == "Excelsior (Revival)")) return;
            var source = FindModel();
            if (source == null) return;
            // The Aegis hull: the deepest transform holding the ship's mesh renderers.
            var hull = ship.GetComponentsInChildren<MeshRenderer>(true).Where(r => r.enabled && r.GetComponent<MeshFilter>() != null).Select(r => r.transform).ToList();
            if (hull.Count == 0) return;
            var hullRoot = CommonParent(hull, ship.transform);
            var copy = (GameObject)UnityEngine.Object.Instantiate(source, hullRoot.parent, false);
            copy.name = "Excelsior (Revival)";
            copy.transform.localPosition = hullRoot.localPosition;
            copy.transform.localRotation = hullRoot.localRotation;
            // A model only: no scripts (an AI ship's ShipFX and the like) and no colliders (the Aegis's stay).
            foreach (var script in copy.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.Destroy(script);
            foreach (var collider in copy.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.Destroy(collider);
            foreach (var renderer in hull.Select(t => t.GetComponent<MeshRenderer>())) renderer.enabled = false;
            Plugin.Log.LogInfo($"Excelsior: {ship.name} wears the Excelsior ({hull.Count} Aegis parts hidden under '{hullRoot.name}')");
        }
    }
}
