using System;
using HarmonyLib;

namespace StbcRevival
{
    /// <summary>
    /// With [VR] QuestControls in use (QuestControls.Active: SteamVR, a Meta/Oculus headset), the game's pictures and
    /// words for the controllers show Oculus Touch instead of Vive wands: the controls card on the shuttle and in the
    /// options, the button icons and their descriptions in prompts, and the recentre and out-of-bounds instructions.
    /// The game picks them from InputManager.GetCurrentControllerType, which says ViveHandTracker for any SteamVR
    /// headset (OculusTouch only on the Oculus runtime). Only those display methods are told OculusTouch, while they
    /// run: the input code keeps seeing the Vive wands, whose buttons QuestControls remaps to the Touch layout (the
    /// same layout the game's Touch card shows).
    /// </summary>
    internal static class QuestControlsArt
    {
        [ThreadStatic] private static int depth;

        // Type, method: the game's display code that chooses by controller type.
        private static readonly string[][] Displays =
        {
            new[] { "ControllerInputHighlighter", "PJMEJABMKEB" },          // the controls card's controller model
            new[] { "InputToGlyphMap", "GetImageForInput" },                // a button's icon in prompts
            new[] { "InputToGlyphMap", "GetDescriptionForInput" },          // a button's name in prompts
            new[] { "CenterViewIntroElement", "JCAANHKJECE" },              // the recentre instructions
            new[] { "TrackingAndCalibrationWarning", "SetText" },           // the out-of-bounds instructions
        };

        internal static void Patch(Harmony harmony)
        {
            if (!Plugin.QuestControlsOn.Value) return;
            var input = AccessTools.TypeByName("SVR.InputManager");
            var type = input == null ? null : AccessTools.Method(input, "GetCurrentControllerType");
            if (type == null) { Plugin.Log.LogWarning("Quest controls: the game's controller type wasn't found; the Vive pictures stay"); return; }
            int patched = 0;
            foreach (var d in Displays)
            {
                var m = AccessTools.Method(AccessTools.TypeByName(d[0]), d[1]);
                if (m == null) { Plugin.Log.LogWarning($"Quest controls: {d[0]}.{d[1]} not found; its Vive pictures stay"); continue; }
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(QuestControlsArt), nameof(Enter)),
                    finalizer: new HarmonyMethod(typeof(QuestControlsArt), nameof(Leave)));
                patched++;
            }
            if (patched > 0) harmony.Patch(type, postfix: new HarmonyMethod(typeof(QuestControlsArt), nameof(ControllerType)));
            Plugin.Log.LogInfo($"Quest controls: Touch pictures in {patched} of {Displays.Length} places");
        }

        private static void Enter() => depth++;

        private static Exception Leave(Exception __exception)
        {
            depth--;
            return __exception;
        }

        // SVR.IGLEGHPMPBK: the game's controller type (ViveHandTracker, OculusTouch, ...).
        private static void ControllerType(ref SVR.IGLEGHPMPBK __result)
        {
            if (depth > 0 && __result == SVR.IGLEGHPMPBK.ViveHandTracker && QuestControls.Active) __result = SVR.IGLEGHPMPBK.OculusTouch;
        }
    }
}
