using System;
using BepInEx.Configuration;
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
    /// Usually neither screen is shown at all: when the intro picks its next screen (IntroSequence.GBDBONKGDOM)
    /// and it's one of these with nothing to wait for (a controller found, no warning up, no network error), it
    /// sends that screen's event itself and moves past it. Only otherwise does the screen show, and finish as above.
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

        private static readonly AccessTools.FieldRef<IntroSequence, IntroElement[]> Elements =
            AccessTools.FieldRefAccess<IntroSequence, IntroElement[]>("_elements");
        private static readonly AccessTools.FieldRef<IntroSequence, bool> SkipIsOn =
            AccessTools.FieldRefAccess<IntroSequence, bool>("_skipIsOn");
        private static Func<ControllerCheckElement, bool> ControllersConnected; // AIPFLEFJDPA: the check's own test

        private static bool inControllerCheck;

        internal static void Patch(Harmony harmony)
        {
            if (!Plugin.SkipVrSetup.Value) return;
            var next = AccessTools.Method(typeof(IntroSequence), "GBDBONKGDOM"); // index of the next element to run
            var connected = AccessTools.Method(typeof(ControllerCheckElement), "AIPFLEFJDPA");
            if (next == null || connected == null)
                Plugin.Log.LogWarning("Skip VR setup: the intro's next-screen step wasn't found; the screens show for a moment");
            else
            {
                ControllersConnected = AccessTools.MethodDelegate<Func<ControllerCheckElement, bool>>(connected);
                harmony.Patch(next, postfix: new HarmonyMethod(typeof(VrSetupSkip), nameof(Next_Postfix)));
            }

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

        // After IntroSkip's prefix (or the game's own lookup): move past these screens while nothing needs them.
        private static void Next_Postfix(IntroSequence __instance, ref int __result)
        {
            try
            {
                var elements = Elements(__instance);
                bool skipIsOn = SkipIsOn(__instance);
                while (__result < elements.Length && PassedWithoutShowing(elements[__result]))
                {
                    do __result++;
                    while (__result < elements.Length && (!elements[__result].ShouldRun(skipIsOn)
                           || Plugin.SkipIntro.Value && IntroSkip.IsSplash(elements[__result])));
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Skip VR setup: couldn't check the next intro screen: " + e.Message);
            }
        }

        private static bool PassedWithoutShowing(IntroElement element)
        {
            var warnings = WarningManager.IEFBKOMBFGO; // Instance
            if (warnings != null && warnings.NCFJDDCECDA) return false; // ShouldRestrictInteraction: the screen waits
            if (element is ControllerCheckElement check)
            {
                if (!ControllersConnected(check)) return false; // shows "No supported controller detected"
                StartWithLastControls();
                NAMOPFFJHDB.Send(); // ControllerCheckPassedEvent: the VR reconnect warning starts watching
                Plugin.Log.LogInfo("Skip VR setup: controller screen skipped");
                return true;
            }
            if (element is CenterViewIntroElement)
            {
                if (SVR.Network.NetworkStatus.BPGHMFPAFCI) return false; // HadError: the prompt goes to the error screen
                CIFDOFLFGEA.Send(new JONMBEALFAK()); // IntroViewRecenteredEvent: the out-of-bounds warning starts
                Plugin.Log.LogInfo("Skip VR setup: recenter prompt skipped");
                return true;
            }
            return false;
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

        // Called by the check only once controllers are found, the timer is done and no warning holds the player;
        // and every frame by InputManager.Update, where true means an input switched the controls.
        private static void ActiveController_Postfix(SVR.InputManager __instance, ref bool __result)
        {
            if (__result) { Remember(__instance); return; }
            if (!inControllerCheck) return;
            __result = true;
            Plugin.Log.LogInfo("Skip VR setup: controller check passed without a button press");
            StartWithLastControls();
        }

        // InputManager: IHNHENLGKBO = keyboard and mouse in use (over a gamepad), FAKBNJECKGI = motion controllers in
        // use (VR), OMHIFCDEJLK = motion controllers can be used. The controller screen's button press normally sets
        // these; without it they're kept from last time, until another input switches them as it always does.
        private static readonly AccessTools.FieldRef<SVR.InputManager, bool> KeyboardMouse =
            AccessTools.FieldRefAccess<SVR.InputManager, bool>("IHNHENLGKBO");
        private static readonly AccessTools.FieldRef<SVR.InputManager, bool> MotionControllers =
            AccessTools.FieldRefAccess<SVR.InputManager, bool>("FAKBNJECKGI");
        private static readonly AccessTools.FieldRef<SVR.InputManager, bool> MotionControllersUsable =
            AccessTools.FieldRefAccess<SVR.InputManager, bool>("OMHIFCDEJLK");

        private static ConfigEntry<string> LastControls => CameraManager.EDPLGHHIGNF /* VRActive */ ? Plugin.LastControlsVr : Plugin.LastControlsDesktop;

        private static void Remember(SVR.InputManager input)
        {
            try
            {
                string now = MotionControllers(input) ? "MotionControllers" : KeyboardMouse(input) ? "KeyboardMouse" : "Gamepad";
                var last = LastControls;
                if (last.Value == now) return;
                last.Value = now;
                Plugin.Log.LogInfo($"Skip VR setup: controls now {now}, remembered for next launch");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Skip VR setup: couldn't remember the controls: " + e.Message);
            }
        }

        private static void StartWithLastControls()
        {
            try
            {
                var input = SVR.InputManager.IEFBKOMBFGO; // Instance
                string last = LastControls.Value;
                if (input == null || string.IsNullOrEmpty(last)) return;
                bool motion = last == "MotionControllers";
                if (motion && !MotionControllersUsable(input)) return;
                var typeBefore = input.GetCurrentControllerType();
                var schemeBefore = SVR.InputManager.GetScheme();
                MotionControllers(input) = motion;
                KeyboardMouse(input) = last == "KeyboardMouse";
                // As UpdateActiveController does on a switch: the mouse cursor (MouseCursorCustomize) and the hands
                // only learn the controls from these, and hide the cursor while they think there are none.
                var typeNow = input.GetCurrentControllerType();
                if (typeNow != typeBefore) SVR.DHKABHGLPGL.Send(typeBefore, typeNow); // ControllerTypeChangedEvent
                var schemeNow = SVR.InputManager.GetScheme();
                if (schemeNow != schemeBefore) SVR.LJIOLFEDPFP.Send(schemeBefore, schemeNow); // InputSchemeChangedEvent
                Plugin.Log.LogInfo($"Skip VR setup: starting with {last}, as last time");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Skip VR setup: couldn't start with last time's controls: " + e.Message);
            }
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
