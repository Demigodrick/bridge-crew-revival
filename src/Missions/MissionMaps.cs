using System;
using System.Collections;
using System.Linq;
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

        // Host: off the Trench (or for a Revival mission), set up as GenerateProceduralMission and
        // INHGNBIPNHB.PopulateEncounters do (contract, sector, starting system, player spawn point), without
        // placing any encounters. The ship spawn (MissionManager.NJHADEBHMNN) needs that spawn point and contract.
        private static bool Generate_Prefix(MissionManager __instance, ref int __result)
        {
            // Off the Trench there are no encounters to place; a Revival mission brings its own (or, for Free
            // Roam, none), so skip the Ongoing Voyage's there too.
            var map = Loader.EJLNDBGLIFL.GetLoadedMap();
            if (map == null) return true;
            bool trench = map.name.IndexOf("Trench", StringComparison.OrdinalIgnoreCase) >= 0;
            if (trench && RevivalMissions.Selected == null) return true;
            Map.SystemRoot system = null;
            try
            {
                var manager = typeof(MissionManager);
                AccessTools.Method(manager, "AMAMFJDNMKB").Invoke(__instance, null); // the seeded Random
                var random = (System.Random)AccessTools.Field(manager, "GBEFMAACMFK").GetValue(__instance);
                var selection = (BHNEPPPPMKH)AccessTools.Field(manager, "DJFMMNBHJPC").GetValue(__instance);
                var sectorMap = AccessTools.Field(manager, "LFBBCGCLOKG").GetValue(__instance);
                system = (Map.SystemRoot)AccessTools.Method(sectorMap.GetType(), "ChooseStartingSystem").Invoke(sectorMap, new object[] { map, selection, random });
                // Patrol and Stellar Evacuation start anywhere: the game's choice always lands on the same system (on the Trench,
                // Sarza, which has no spawn point, so Venov).
                var selected = RevivalMissions.Selected;
                bool anywhere = selected == typeof(StbcRevival.Missions.PatrolSequence) || selected == typeof(StbcRevival.Missions.StellarEvacuationSequence);
                var spawn = FindSpawn(sectorMap, ref system, selection, random, anywhere);

                // The mission's contract, with the table's encounter settings, as the game applies them.
                var runner = AccessTools.Field(manager, "ENGIPFEKJAK").GetValue(__instance);
                var contract = Member(runner, "Contract");
                var entry = AccessTools.Field(manager, "ABBGEGBEKIF").GetValue(__instance);
                var settings = Member(entry, "Encounter");
                AccessTools.Method(settings.GetType(), "ApplyEncounterSettingsToContract").Invoke(settings, new[] { contract });

                // The encounter system's state, minus the encounters.
                var encounters = AccessTools.TypeByName("Mission.Scripting.Encounter.INHGNBIPNHB");
                AccessTools.Method(encounters, "JMEJBKFNKDA").Invoke(null, null); // reset
                AccessTools.Field(encounters, "MJDNNOGMKFG").SetValue(null, contract);
                AccessTools.Field(encounters, "AHNEIJMLBCH").SetValue(null, random);
                AccessTools.Field(encounters, "LFBBCGCLOKG").SetValue(null, sectorMap);
                var playerEncounters = Member(contract, "LNIBLGBKLGI");
                var startSystem = Member(playerEncounters, "JFAIKAOOPMO");
                Traverse.Create(startSystem).Field("System").SetValue(system);
                AccessTools.Field(encounters, "<HBMGPABJGIK>k__BackingField").SetValue(null, spawn);

                __result = system.SystemID;
                Plugin.Log.LogInfo($"Mission maps: {map.name}, starting in {system.SystemName} (no Ongoing Voyage encounters)");
                return false;
            }
            catch (Exception e)
            {
                // On the Trench the game's own set-up works. Anywhere else it retries forever (a frozen load),
                // so carry on with what we have: worst case the mission starts broken and can be left.
                Plugin.Log.LogError("Mission maps: couldn't set up the mission: " + e);
                if (trench || system == null) return true;
                __result = system.SystemID;
                return false;
            }
        }

        // A player spawn point: in the chosen system if it has one, else in another of the map's systems (which
        // then becomes the starting system), trying the default and the alternative spawn tags. Last resort:
        // any body in the chosen system.
        // With anywhere, the systems are tried in a random order instead.
        private static object FindSpawn(object sectorMap, ref Map.SystemRoot system, BHNEPPPPMKH selection, System.Random random, bool anywhere = false)
        {
            var find = AccessTools.Method(typeof(MissionManager), "FindSpawnPoint");
            var systems = new System.Collections.Generic.List<Map.SystemRoot> { system };
            if (Traverse.Create(sectorMap).Property("MLHNDAEKLHO").GetValue() is System.Collections.Generic.List<Map.SystemRoot> all)
                foreach (var other in all) if (other != null && !systems.Contains(other)) systems.Add(other);
            if (anywhere)
            {
                var shuffle = new System.Random(Guid.NewGuid().GetHashCode());
                systems = systems.OrderBy(_ => shuffle.Next()).ToList();
                system = systems[0];
            }
            var choices = new[] { selection.SpawnChoice, BHNEPPPPMKH.EIKCLNAPKBE.Default, BHNEPPPPMKH.EIKCLNAPKBE.UseAlternatives };
            foreach (var candidate in systems)
                foreach (var choice in choices)
                    if (find.Invoke(null, new object[] { sectorMap, candidate, choice, random }) is object spawn && !(spawn is UnityEngine.Object o && o == null))
                    {
                        if (candidate != system) Plugin.Log.LogInfo($"Mission maps: no spawn point in {system.SystemName}; starting in {candidate.SystemName}");
                        system = candidate;
                        return spawn;
                    }
            var bodies = AccessTools.Method(sectorMap.GetType(), "FindBodies").Invoke(sectorMap, null);
            var inSystem = AccessTools.Method(bodies.GetType(), "InThisSystem").Invoke(bodies, new object[] { system });
            var any = Traverse.Create(inSystem).Property("MFAHBBHBNLN").GetValue();
            AccessTools.Method(inSystem.GetType(), "Destroy")?.Invoke(inSystem, null);
            if (any == null) throw new InvalidOperationException($"no spawn point or body anywhere in {system.SystemName}");
            Plugin.Log.LogWarning($"Mission maps: no spawn points on this map; starting at a body in {system.SystemName}");
            return any;
        }

        // A field or property, by name, on the object's type or a base type.
        private static object Member(object target, string name)
        {
            var t = Traverse.Create(target);
            var field = t.Field(name);
            return field.FieldExists() ? field.GetValue() : t.Property(name).GetValue();
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
            if (!PhotonProps.Set(room, RoomKey, own)) return;
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
