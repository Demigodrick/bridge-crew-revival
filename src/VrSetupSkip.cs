using System;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// [Startup] SkipVrSetup: no "press any button" controller screen and no VR recenter prompt at launch, so the
    /// game goes straight on to the shuttle. Based on Cleo's (Orloe) StartupSkip plugin.
    ///
    /// Both screens still finish through the game's own code, only without waiting for the player, so what they
    /// set up still happens:
    ///  - The controller check (ControllerCheckElement) still waits until a controller is found, showing "No
    ///    supported controller detected" until then, and while a warning holds the player
    ///    (WarningManager.ShouldRestrictInteraction). Its "press any button" wait is skipped: the 3 second timer
    ///    flag is set at once and that frame's input check counts as a press. The game then sends
    ///    ControllerCheckPassedEvent itself, which is what starts the VR "please reconnect input device" warning.
    ///  - The recenter prompt (CenterViewIntroElement) is finished by calling its own recentered handler once the
    ///    prompt is set up: it waits out warnings the same way, goes to the network error screen after a failed
    ///    connection, and sends IntroViewRecenteredEvent, which starts the out-of-bounds warning. The view isn't
    ///    recentred: SteamVR's or Oculus's own centre is kept (a recentre now could catch a headset not yet on),
    ///    and the recenter button works as always.
    /// </summary>
    internal static class VrSetupSkip
    {
        // ControllerCheckElement: MNBMEFCBKKH = controllers found and the message coroutine started,
        // NLEGMCICILA = the coroutine's timer ran out ("press any button" is showing).
        private static readonly AccessTools.FieldRef<ControllerCheckElement, bool> Found =
            AccessTools.FieldRefAccess<ControllerCheckElement, bool>("MNBMEFCBKKH");
        private static readonly AccessTools.FieldRef<ControllerCheckElement, bool> TimerDone =
            AccessTools.FieldRefAccess<ControllerCheckElement, bool>("NLEGMCICILA");
        // CenterViewIntroElement: NDDFIMFHMKI = the camera, set together with the prompt's event listeners.
        private static readonly AccessTools.FieldRef<CenterViewIntroElement, Transform> PromptCamera =
            AccessTools.FieldRefAccess<CenterViewIntroElement, Transform>("NDDFIMFHMKI");

        private static bool inControllerCheck;

        internal static void Patch(Harmony harmony)
        {
            if (!Plugin.SkipVrSetup.Value) return;
            var check = AccessTools.Method(typeof(ControllerCheckElement), "Update");
            var activeController = AccessTools.Method(typeof(SVR.InputManager), "UpdateActiveController");
            if (check == null || activeController == null)
                Plugin.Log.LogWarning("Skip VR setup: the controller check wasn't found; it still waits for a button");
            else
            {
                harmony.Patch(check, prefix: new HarmonyMethod(typeof(VrSetupSkip), nameof(Check_Prefix)),
                    finalizer: new HarmonyMethod(typeof(VrSetupSkip), nameof(Check_Finalizer)));
                harmony.Patch(activeController, postfix: new HarmonyMethod(typeof(VrSetupSkip), nameof(ActiveController_Postfix)));
            }

            var prompt = AccessTools.Method(typeof(CenterViewIntroElement), "LateUpdate");
            var recentered = AccessTools.Method(typeof(CenterViewIntroElement), "ANDHFMCAMMM"); // OnVRRecenteredEvent
            if (prompt == null || recentered == null)
                Plugin.Log.LogWarning("Skip VR setup: the recenter prompt wasn't found; it still shows");
            else
            {
                Recentered = AccessTools.MethodDelegate<Action<CenterViewIntroElement, MDKKOKIMCEN>>(recentered);
                harmony.Patch(prompt, postfix: new HarmonyMethod(typeof(VrSetupSkip), nameof(Prompt_Postfix)));
            }
        }

        private static void Check_Prefix(ControllerCheckElement __instance)
        {
            if (Found(__instance) && !TimerDone(__instance)) TimerDone(__instance) = true;
            inControllerCheck = true;
        }

        private static Exception Check_Finalizer(Exception __exception)
        {
            inControllerCheck = false;
            return __exception;
        }

        // Called by the check only once controllers are found, the timer is done and no warning holds the player.
        private static void ActiveController_Postfix(ref bool __result)
        {
            if (!inControllerCheck || __result) return;
            __result = true;
            Plugin.Log.LogInfo("Skip VR setup: controller check passed without a button press");
        }

        private static Action<CenterViewIntroElement, MDKKOKIMCEN> Recentered;

        // Every frame until it finishes (a warning can hold it), once the prompt has its camera and listeners.
        private static void Prompt_Postfix(CenterViewIntroElement __instance)
        {
            if (__instance.LLMBJDBFGPK /* IsFinished */ || PromptCamera(__instance) == null) return;
            try
            {
                Recentered(__instance, null); // the handler doesn't read the event
                if (__instance.LLMBJDBFGPK) Plugin.Log.LogInfo("Skip VR setup: recenter prompt skipped");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Skip VR setup: couldn't skip the recenter prompt: " + e.Message);
                Recentered = (_, __) => { }; // the prompt then waits for the player, as normal
            }
        }
    }
}
