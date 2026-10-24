using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Mission;
using UnityEngine;
using PhotonHashtable = ExitGames.Client.Photon.Hashtable;

namespace StbcRevival.Missions
{
    /// <summary>
    /// Which sector map a mission loads. The game always loads one map per mission (Loader.GetMapName, on
    /// every player's machine); Ongoing Voyages and the campaign use "The Trench". Here:
    ///   - Last Stand is fought in the E3 demo sector.
    ///   - [Debug] MapOverride loads any map from the game's Resources ("Mission Data/Maps/...") for an
    ///     Ongoing Voyage, to look around the leftover test and demo sectors.
    /// The host's choice goes in the room ("RM"), so everyone loads the same map. Off the Trench there are no
    /// Ongoing Voyage encounters to place, so the host just picks a starting system (the game would otherwise
    /// keep retrying the encounter set-up).
    /// </summary>
    internal static class MissionMaps
    {
        internal const string E3 = "Mission Data/Maps/Sector - E3";
        private const string RoomKey = "RM";
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        internal static void Patch(Harmony harmony)
        {
            var mapName = AccessTools.Method(typeof(Loader), nameof(Loader.GetMapName));
            var generate = AccessTools.Method(typeof(MissionManager), nameof(MissionManager.GenerateProceduralMission));
            if (mapName == null || generate == null)
            {
                Plugin.Log.LogWarning("Mission maps: Loader.GetMapName or GenerateProceduralMission not found; missions use their own maps");
                return;
            }
            harmony.Patch(mapName, postfix: new HarmonyMethod(typeof(MissionMaps), nameof(MapName_Postfix)));
            harmony.Patch(generate, prefix: new HarmonyMethod(typeof(MissionMaps), nameof(Generate_Prefix)) { priority = Priority.First });
        }

        /// <summary>The map this launch should load instead of the game's choice, or null.</summary>
        private static string Override()
        {
            if (Loader.GetMissionType() != Loader.DAPMGBKPDNM.Procedural) return null;
            var fromRoom = RoomValue();
            if (!string.IsNullOrEmpty(fromRoom)) return fromRoom;
            var own = Plugin.MapOverride.Value;
            if (!string.IsNullOrEmpty(own) && Room() == null) return own; // on your own, out of a room
            return RevivalMissions.Selected == typeof(LastStandSequence) ? E3 : null;
        }

        private static void MapName_Postfix(ref string __result)
        {
            if (__result == null || __result.IndexOf("Training", StringComparison.OrdinalIgnoreCase) >= 0) return;
            var map = Override();
            if (map == null || string.Equals(map, __result, StringComparison.OrdinalIgnoreCase)) return;
            if (!IsMap(map))
            {
                Plugin.Log.LogWarning($"Mission maps: '{map}' isn't a sector map in the game; loading {__result}");
                return;
            }
            Plugin.Log.LogInfo($"Mission maps: loading {map} instead of {__result}");
            __result = map;
        }

        // Only real sector maps: the game waits for the map forever, so a typo (or a room value from someone
        // else's game) must never get through. Loading it here is no extra cost: the game loads it next.
        private static readonly System.Collections.Generic.Dictionary<string, bool> checkedMaps =
            new System.Collections.Generic.Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private static bool IsMap(string path)
        {
            if (!path.StartsWith("Mission Data/Maps/", StringComparison.OrdinalIgnoreCase) || path.IndexOf("..", StringComparison.Ordinal) >= 0) return false;
            if (!checkedMaps.TryGetValue(path, out var ok))
                checkedMaps[path] = ok = Resources.Load(path) is Map.SectorRoot;
            return ok;
        }

        // Host: off the Trench, choose a starting system without placing Ongoing Voyage encounters.
        private static bool Generate_Prefix(MissionManager __instance, ref int __result)
        {
            // Off the Trench there are no encounters to place; a Revival mission brings its own (or, for Test
            // Flight, none), so skip the Ongoing Voyage's there too.
            var map = Loader.EJLNDBGLIFL.GetLoadedMap();
            if (map == null || (map.name.IndexOf("Trench", StringComparison.OrdinalIgnoreCase) >= 0 && RevivalMissions.Selected == null)) return true;
            try
            {
                AccessTools.Method(typeof(MissionManager), "AMAMFJDNMKB").Invoke(__instance, null); // the seeded Random
                var random = AccessTools.Field(typeof(MissionManager), "GBEFMAACMFK").GetValue(__instance) as System.Random ?? new System.Random();
                var selection = AccessTools.Field(typeof(MissionManager), "DJFMMNBHJPC").GetValue(__instance) ?? new BHNEPPPPMKH();
                var sectorMap = AccessTools.Field(typeof(MissionManager), "LFBBCGCLOKG").GetValue(__instance);
                var system = (Map.SystemRoot)AccessTools.Method(sectorMap.GetType(), "ChooseStartingSystem")
                    .Invoke(sectorMap, new[] { map, selection, random });
                __result = system.SystemID;
                Plugin.Log.LogInfo($"Mission maps: {map.name}, starting in {system.SystemName} (no Ongoing Voyage encounters)");
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Mission maps: couldn't choose a starting system, using the game's set-up: " + e);
                return true;
            }
        }

        // ---- The room's copy of the host's MapOverride -------------------------------------------------

        private static float nextPublish;

        internal static void Update()
        {
            if (Time.unscaledTime < nextPublish) return;
            nextPublish = Time.unscaledTime + 1f;
            var room = Room();
            if (room == null || !IsMasterClient()) return;
            var own = Plugin.MapOverride.Value ?? "";
            if ((RoomValue() ?? "") == own) return;
            room.GetType().GetMethod("SetCustomProperties", All)?.Invoke(room, new object[] { new PhotonHashtable { [RoomKey] = own }, null, false });
            Plugin.Log.LogInfo(own == "" ? "Mission maps: map override cleared for the crew" : $"Mission maps: crew will load {own}");
        }

        private static object Room() =>
            Patches.PhotonNetworkType?.GetMethod("get_room", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);

        private static bool IsMasterClient() =>
            Patches.PhotonNetworkType?.GetMethod("get_isMasterClient", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) is bool b && b;

        private static string RoomValue()
        {
            var room = Room();
            var props = room?.GetType().GetMethod("get_customProperties", All)?.Invoke(room, null) as IDictionary;
            return props != null && props.Contains(RoomKey) ? props[RoomKey] as string : null;
        }
    }
}
