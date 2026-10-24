using System;
using System.Reflection;
using HarmonyLib;
using Mission;

namespace StbcRevival.Missions
{
    /// <summary>
    /// Revival Missions: missions made for this mod, run by the game's own mission framework.
    /// They launch like an Ongoing Voyage, so the game generates a sector and places the ship; right after
    /// that (MissionManager.GenerateProceduralMission, host only), the Ongoing Voyage's mission script is
    /// replaced by the Revival mission's before it starts.
    /// </summary>
    internal static class RevivalMissions
    {
        internal const string LastStand = "LastStand";
        internal const string FreeRoam = "FreeRoam"; // the developers' own exploration mission, as shipped

        /// <summary>The Revival missions, in the order the briefing's next/previous buttons step through them.</summary>
        internal static readonly string[] All = { LastStand, FreeRoam };

        /// <summary>The Revival mission the next launch should run, or null for the game's own missions.</summary>
        internal static Type Selected
        {
            get
            {
                if (Plugin.TestLastStand.Value) return typeof(LastStandSequence);
                switch (RevivalBriefing.RoomSelection())
                {
                    case LastStand: return typeof(LastStandSequence);
                    case FreeRoam: return typeof(Mission.DebugExplorationMode.DebugExplorationModeSequence);
                    default: return null;
                }
            }
        }

        internal static string Name(string mission) =>
            mission == LastStand ? "LAST STAND" : mission == FreeRoam ? "FREE ROAM" : "REVIVAL MISSION";

        internal static string Description(string mission)
        {
            switch (mission)
            {
                case LastStand:
                    return "Hold your ground against endless waves of Klingons, pirates and Romulans, each bigger than the last, "
                           + "with a flagship every fifth wave. How many waves can your crew survive?";
                case FreeRoam:
                    var map = Plugin.MapOverride.Value;
                    return "No hostiles, no objectives: fly, scan and warp around a sector at your own pace "
                           + "(the developers' own exploration mission, left in the game). Sector: "
                           + (string.IsNullOrEmpty(map) ? "The Trench." : map.Substring(map.LastIndexOf('/') + 1) + ".");
                default:
                    return "";
            }
        }

        // MissionManager's script runner (internal type Mission.Scripting.Framework.HHJAGACLBKA).
        private static readonly FieldInfo ScriptField = AccessTools.Field(typeof(MissionManager), "ENGIPFEKJAK");

        internal static void Patch(Harmony harmony)
        {
            var generate = AccessTools.Method(typeof(MissionManager), nameof(MissionManager.GenerateProceduralMission));
            if (generate == null)
            {
                Plugin.Log.LogWarning("Revival missions: MissionManager.GenerateProceduralMission not found; Revival missions disabled.");
                return;
            }
            harmony.Patch(generate, postfix: new HarmonyMethod(typeof(RevivalMissions), nameof(Generated_Postfix)));
            RevivalBriefing.Patch(harmony);
            LastStandLocks.Patch(harmony);
            LastStandBridgeRepair.Patch(harmony);
            MissionMaps.Patch(harmony);
        }

        // The sector now exists (generated from the Ongoing Voyage's encounters); swap the script before
        // the bridge preloads and runs it.
        private static void Generated_Postfix(MissionManager __instance)
        {
            var mission = Selected;
            if (mission == null) return;
            try
            {
                var old = ScriptField.GetValue(__instance);
                if (old != null) AccessTools.Method(old.GetType(), "Destroy")?.Invoke(old, null);
                var runner = Activator.CreateInstance(ScriptField.FieldType,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { mission, __instance }, null);
                ScriptField.SetValue(__instance, runner);
                Plugin.Log.LogInfo($"Revival missions: running {mission.Name}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Revival missions: couldn't start the mission: " + e);
            }
        }
    }
}
