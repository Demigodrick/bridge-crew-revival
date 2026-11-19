using System;
using BepInEx;
using HarmonyLib;

namespace Stbc.StartupSkip
{
    [BepInPlugin("stbc.startupskip", "STBC Startup Skip", "1.0.0")]
    public sealed class StartupSkipPlugin : BaseUnityPlugin
    {
        private void Awake()
        {
            var harmony = new Harmony("stbc.startupskip");

            Patch(harmony, typeof(ControllerCheckElement), "OnEnable", nameof(SkipControllerCheck));
            Patch(harmony, typeof(CenterViewIntroElement), "OnEnable", nameof(SkipRecenterPrompt));

            Logger.LogInfo("Startup Skip loaded: controller selection and VR recenter prompts will be skipped.");
        }

        private void Patch(Harmony harmony, Type type, string methodName, string prefixName)
        {
            var target = AccessTools.Method(type, methodName);
            var prefix = AccessTools.Method(typeof(StartupSkipPlugin), prefixName);
            if (target == null || prefix == null)
            {
                Logger.LogError("Could not patch " + type.Name + "." + methodName + ".");
                return;
            }
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
        }

        // Input switching already happens at runtime, so skip the wait for an input edge.
        private static bool SkipControllerCheck(ControllerCheckElement __instance)
        {
            __instance.Continue();
            return false;
        }

        // Send the normal recentered event, then continue without moving the headset.
        private static bool SkipRecenterPrompt(CenterViewIntroElement __instance)
        {
            CIFDOFLFGEA.Send(new JONMBEALFAK());
            __instance.Continue();
            return false;
        }
    }
}
