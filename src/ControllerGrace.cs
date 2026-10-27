using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// VR controllers that doze off aren't "disconnected": the game counts both controllers as connected only
    /// while the VR runtime reports them this very frame (SVR.InputManager.AADJJIOIJJA sets OMHIFCDEJLK from
    /// SteamVR's GetControllerState, or Oculus's connected controllers), and shows "controllers disconnected"
    /// the moment that fails. Many headsets put a resting controller to sleep within seconds, so setting them
    /// down brought the warning up at once. Here a controller seen recently still counts as connected for
    /// [VR] ControllerGraceSeconds; past that the warning shows as before (0 = the game's own behaviour).
    /// </summary>
    internal static class ControllerGrace
    {
        private static FieldInfo connected;
        private static float lastSeen = float.NegativeInfinity;
        private static bool holding;

        internal static void Patch(Harmony harmony)
        {
            var input = AccessTools.TypeByName("SVR.InputManager");
            var check = input == null ? null : AccessTools.Method(input, "AADJJIOIJJA", new[] { typeof(bool) });
            connected = input == null ? null : AccessTools.Field(input, "OMHIFCDEJLK");
            if (check == null || connected == null || connected.FieldType != typeof(bool))
            {
                Plugin.Log.LogWarning("Controller grace: the game's controller check wasn't found; left as it is");
                return;
            }
            harmony.Patch(check, postfix: new HarmonyMethod(typeof(ControllerGrace), nameof(Check_Postfix)));
        }

        private static void Check_Postfix(object __instance)
        {
            float grace = Plugin.ControllerGraceSeconds.Value;
            if (grace <= 0f) return;
            float now = Time.unscaledTime;
            if ((bool)connected.GetValue(__instance))
            {
                if (holding) Plugin.Log.LogInfo($"Controller grace: controllers back after {now - lastSeen:F1} s");
                holding = false;
                lastSeen = now;
                return;
            }
            if (now - lastSeen > grace)
            {
                if (holding) Plugin.Log.LogInfo($"Controller grace: no controllers for {grace:F0} s; the game's warning shows");
                holding = false;
                return;
            }
            if (!holding) Plugin.Log.LogInfo("Controller grace: controllers not reported (asleep?); still counted as connected");
            holding = true;
            connected.SetValue(__instance, true);
        }
    }
}
