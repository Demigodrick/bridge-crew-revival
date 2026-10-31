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
        internal const string FleetBattle = "FleetBattle";
        internal const string Patrol = "Patrol";
        internal const string Evacuation = "Evacuation";
        internal const string FreeRoam = "FreeRoam"; // the developers' own exploration mission, as shipped
        internal const string PvpTest = "PvpTest";   // debug: two player ships ([Debug] PvpTest)

        /// <summary>The Revival missions, in the order the briefing's next/previous buttons step through them.</summary>
        internal static string[] All => Plugin.PvpTest.Value ? WithPvp : Missions;

        private static readonly string[] Missions = { LastStand, FleetBattle, Patrol, Evacuation, FreeRoam };
        private static readonly string[] WithPvp = { LastStand, FleetBattle, Patrol, Evacuation, FreeRoam, PvpTest };

        /// <summary>The Revival mission the next launch should run, or null for the game's own missions.</summary>
        internal static Type Selected
        {
            get
            {
                // The debug shortcut is your own setting, so only for you on your own or hosting: a crew member's
                // must follow the host's choice (the room's), or they'd load another map than the host.
                if (Plugin.TestLastStand.Value && RevivalBriefing.HostingOrAlone()) return typeof(LastStandSequence);
                switch (RevivalBriefing.RoomSelection())
                {
                    case LastStand: return typeof(LastStandSequence);
                    case FleetBattle: return typeof(FleetBattleSequence);
                    case Patrol: return typeof(PatrolSequence);
                    case Evacuation: return typeof(StellarEvacuationSequence);
                    case FreeRoam: return typeof(Mission.DebugExplorationMode.DebugExplorationModeSequence);
                    case PvpTest: return typeof(PvpTestSequence);
                    default: return null;
                }
            }
        }

        // Missions that repair the ship mid-mission (so the bridge's damage effects are cleared too); checked
        // every frame by LastStandBridgeRepair, so cached per frame.
        private static int repairsCheckedFrame = -1;
        private static bool repairsShip;

        internal static bool RepairsShip
        {
            get
            {
                if (repairsCheckedFrame != UnityEngine.Time.frameCount)
                {
                    repairsCheckedFrame = UnityEngine.Time.frameCount;
                    try { var m = Selected; repairsShip = m == typeof(LastStandSequence) || m == typeof(PatrolSequence); }
                    catch { repairsShip = false; }
                }
                return repairsShip;
            }
        }

        internal static string Name(string mission) =>
            mission == LastStand ? "LAST STAND" : mission == FleetBattle ? "FLEET BATTLE" : mission == Patrol ? "PATROL"
            : mission == Evacuation ? "STELLAR EVACUATION"
            : mission == FreeRoam ? "FREE ROAM" : mission == PvpTest ? "PVP TEST" : "REVIVAL MISSION";

        internal static string Description(string mission)
        {
            switch (mission)
            {
                case LastStand:
                    return "Hold your ground against endless waves of Klingons, pirates and Romulans, each bigger than the last, "
                           + "with a flagship every fifth wave. How many waves can your crew survive?";
                case FleetBattle:
                    return $"Lead a Starfleet battle group of {Plugin.FleetBattleSize.Value} ships against a Klingon or Romulan fleet "
                           + "just as big. Destroy every enemy ship to win; keep your own ship in one piece.";
                case Patrol:
                    return "A patrol that never ends: Starfleet sends you from system to system to hunt raiders, defend "
                           + "freighters and rescue survivors. Repair and restock at space stations between assignments.";
                case Evacuation:
                    return $"Perseph's star is about to erupt. Ferry the people off the station a group at a time to a waiting "
                           + $"transport, then warp out before the blast. {Plugin.EvacuationMinutes.Value} minutes: how many can you save?";
                case FreeRoam:
                    var map = Plugin.MapOverride.Value;
                    return "No hostiles, no objectives: fly, scan and warp around a sector at your own pace "
                           + "(the developers' own exploration mission, left in the game). Sector: "
                           + (string.IsNullOrEmpty(map) ? "The Trench." : map.Substring(map.LastIndexOf('/') + 1) + ".");
                case PvpTest:
                    return "Testing player vs player: a second player ship, PvP B, appears ahead of yours as an enemy. "
                           + "Destroy it to win. The crew is split between the ships in joining order (the host on yours).";
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
            FleetBattleNoCloak.Patch(harmony);
            FleetBattleArmor.Patch(harmony);
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
