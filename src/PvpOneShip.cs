using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace StbcRevival
{
    /// <summary>
    /// PvP: places where the game takes the room for one ship's crew (found in the 22 November review).
    ///   - The crew menu button (ConsoleBase.BatchUpdate, on InputAction.CrewBotMode) is refused when the room has
    ///     more than 3 players ("a full crew has no AI stations"), and with exactly 3 a non-captain's press looks for
    ///     "the captain" among everyone: in a 2v2 nobody could open the crew menu with it. Only your own ship's
    ///     players count.
    ///   - "Return To Briefing Room" in the pause menu is on for anyone with the Captain role and ends the mission for
    ///     the whole room: either captain could send both crews back to the lobby. In a PvP match only the host can.
    ///   - A targeting panel lets go of the transporter and hacking when "its station's player" changes station
    ///     (TargetingPanelUI.OnPlayerStationChanged, by role) and clears its hacking buttons on any player's console
    ///     change: a seat change on one ship freed the other ship's lock. A panel now only minds its own ship's seats.
    /// </summary>
    internal static class PvpOneShip
    {
        private static readonly HashSet<string> told = new HashSet<string>();
        private static FieldInfo returnButton;
        private static PropertyInfo previousStation;

        /// <summary>
        /// The crew menu button's rule: ConsoleBase.BatchUpdate rewritten. Applied after every other patch
        /// (Patches.ApplyDeferred's end): the rewrite is compiled at once, and Mono copies the one-line methods it
        /// calls into it (InputManager.GetAction, VoiceRecognitionManager.UsingVoiceRecognition, the voice button
        /// event's constructor) unless they're patched already, so patches made after it never ran from the console:
        /// the voice button and push-to-talk key were dead from 0.8.2 (issue 47).
        /// </summary>
        internal static void PatchConsoleUpdate(Harmony harmony)
        {
            var consoleUpdate = AccessTools.Method(typeof(ConsoleBase), "BatchUpdate");
            if (consoleUpdate != null) harmony.Patch(consoleUpdate, transpiler: new HarmonyMethod(typeof(PvpOneShip), nameof(CrewButton_Transpiler)));
            else Plugin.Log.LogWarning("PvP: ConsoleBase.BatchUpdate not found; the crew menu button stays off with 4 or more players in the room");
        }

        internal static void Patch(Harmony harmony)
        {
            var missing = new List<string>();

            var pause = AccessTools.TypeByName("UI.PauseMenuUI");
            var refresh = pause == null ? null : AccessTools.Method(pause, "PNOAELFOIPC");   // RefreshStationButtons
            var answer = pause == null ? null : AccessTools.Method(pause, "NHMJIMBLKJG");    // OnReturnToBriefingRoomAnswer
            returnButton = pause == null ? null : AccessTools.Field(pause, "HMPIEFLPGEF");   // _button3: Return To Briefing Room
            if (refresh != null && returnButton != null) harmony.Patch(refresh, postfix: new HarmonyMethod(typeof(PvpOneShip), nameof(ReturnButton_Postfix)));
            if (answer != null) harmony.Patch(answer, prefix: new HarmonyMethod(typeof(PvpOneShip), nameof(ReturnAnswer_Prefix)));
            if (refresh == null || answer == null || returnButton == null) missing.Add("the pause menu's return to the briefing room");

            var stationChanged = AccessTools.Method(typeof(TargetingPanelUI), "LKDCCNPGGOI");  // OnPlayerStationChanged
            var consoleChanged = AccessTools.Method(typeof(TargetingPanelUI), "NKFMHMLDCGA");  // OnPlayerConsoleChangedEvent
            previousStation = AccessTools.Property(AccessTools.TypeByName("HHMOGCCMABB"), "HLKBPPDDMGM"); // PlayerStationChangedEvent.PreviousStation
            if (stationChanged != null && previousStation != null) harmony.Patch(stationChanged, prefix: new HarmonyMethod(typeof(PvpOneShip), nameof(PanelStation_Prefix)));
            if (consoleChanged != null) harmony.Patch(consoleChanged, prefix: new HarmonyMethod(typeof(PvpOneShip), nameof(PanelConsole_Prefix)));
            if (stationChanged == null || consoleChanged == null || previousStation == null) missing.Add("the targeting panels' seat changes");

            if (missing.Count > 0) Plugin.Log.LogWarning("PvP: not found, so still for one ship only: " + string.Join(", ", missing.ToArray()));
            else Plugin.Log.LogInfo("PvP: the crew menu button, the pause menu's return and the targeting panels mind their own ship");
        }

        private static bool On => PvpLobby.InPvpRoom && PvpPrototype.Active;

        private static bool IsMaster => Patches.PhotonNetworkType?.GetMethod("get_isMasterClient", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) is true;

        // ---- The crew menu button ---------------------------------------------------------------------------------

        private static IEnumerable<CodeInstruction> CrewButton_Transpiler(IEnumerable<CodeInstruction> code)
        {
            var own = AccessTools.Method(typeof(PvpOneShip), nameof(OwnShipsPlayers));
            int replaced = 0;
            foreach (var instruction in code)
            {
                yield return instruction;
                if ((instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call)
                    && instruction.operand is MethodInfo m && m.Name == "GetPlayers" && m.ReturnType == typeof(List<NovaPlayerInfo>))
                {
                    yield return new CodeInstruction(OpCodes.Call, own);
                    replaced++;
                }
            }
            if (replaced == 0) Plugin.Log.LogWarning("PvP: ConsoleBase.BatchUpdate's player list wasn't found; the crew menu button stays off with 4 or more players in the room");
        }

        /// <summary>The players the crew menu button counts: in a PvP match, your own ship's (a new list: the game
        /// reuses the one it was given).</summary>
        public static List<NovaPlayerInfo> OwnShipsPlayers(List<NovaPlayerInfo> all)
        {
            if (!On || all == null) return all;
            string mine = PvpShips.Mine ?? "A";
            var own = all.Where(p => p != null && (p.AOLEJMABHPE || PvpShips.Of(PvpShips.Player(p.NJNHJKEPFAK)) == mine)).ToList();
            if (own.Count != all.Count && told.Add("crewbutton")) Plugin.Log.LogInfo($"PvP: the crew menu button counts your ship's {own.Count} players, not the room's {all.Count}");
            return own;
        }

        // ---- Return To Briefing Room ------------------------------------------------------------------------------

        private static bool OnBridge => GamePhaseManager.IEFBKOMBFGO != null && GamePhaseManager.IEFBKOMBFGO.GetCurrentGamePhase() == typeof(GamePhase_Bridge);

        private static void ReturnButton_Postfix(object __instance)
        {
            if (!On || !OnBridge || IsMaster) return;
            if (returnButton.GetValue(__instance) is UnityEngine.UI.Selectable button && button != null && button.interactable)
            {
                button.interactable = false;
                if (told.Add("return")) Plugin.Log.LogInfo("PvP: the pause menu's Return To Briefing Room is the host's only in a PvP match");
            }
        }

        private static bool ReturnAnswer_Prefix(bool __0)
        {
            if (!__0 || !On || !OnBridge || IsMaster) return true;
            Plugin.Log.LogInfo("PvP: Return To Briefing Room refused: only the host ends a PvP match");
            return false;
        }

        // ---- Targeting panels -------------------------------------------------------------------------------------

        private static bool PanelStation_Prefix(TargetingPanelUI __instance, object __0)
        {
            if (!On || __instance == null || __0 == null) return true;
            string set = PvpConsoles.SetOf(__instance);
            if (set == null || !(previousStation.GetValue(__0, null) is NovaStation previous) || previous == null) return true;
            if ((PvpConsoles.IsCopy(previous) ? "B" : "A") == set) return true;
            if (told.Add("panelstation" + set)) Plugin.Log.LogInfo($"PvP: ship {set}'s targeting panels left alone by the other ship's station change");
            return false;
        }

        private static bool PanelConsole_Prefix(TargetingPanelUI __instance, object __0)
        {
            if (!On || __instance == null || __0 == null) return true;
            string set = PvpConsoles.SetOf(__instance);
            var info = Traverse.Create(__0).Field("PlayerInfo").GetValue() as NovaPlayerInfo;
            if (set == null || info == null || info.AOLEJMABHPE || info.AOMGCCGDJJC) return true;
            string ship = PvpShips.Of(PvpShips.Player(info.NJNHJKEPFAK));
            return ship == null || ship == set;
        }
    }
}
