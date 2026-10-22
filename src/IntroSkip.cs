using HarmonyLib;

namespace StbcRevival
{
    /// <summary>
    /// [Startup] SkipIntro: go straight from launch to the shuttle without the logo and movie screens.
    /// The intro is a list of IntroElements run in order (IntroSequence). Plain IntroElements (logos,
    /// legal and splash screens) and MovieIntroElements (videos) are skipped; the ones that do work
    /// (platform and network connect, controller and headset checks, going to the shuttle) are subclasses
    /// and still run.
    /// </summary>
    internal static class IntroSkip
    {
        private static readonly AccessTools.FieldRef<IntroSequence, IntroElement[]> Elements =
            AccessTools.FieldRefAccess<IntroSequence, IntroElement[]>("_elements");
        private static readonly AccessTools.FieldRef<IntroSequence, int> Current =
            AccessTools.FieldRefAccess<IntroSequence, int>("_curNdx");
        private static readonly AccessTools.FieldRef<IntroSequence, bool> SkipIsOn =
            AccessTools.FieldRefAccess<IntroSequence, bool>("_skipIsOn");

        // Safe to patch from plugin Awake: IntroSequence's next-element lookup touches no Photon or
        // network types (see Patches.ApplyDeferred for why that matters).
        internal static void Patch(Harmony harmony)
        {
            if (!Plugin.SkipIntro.Value) return;
            var next = AccessTools.Method(typeof(IntroSequence), "GBDBONKGDOM"); // index of the next element to run
            if (next == null)
            {
                Plugin.Log.LogWarning("Intro sequence not found; SkipIntro has no effect.");
                return;
            }
            harmony.Patch(next, prefix: new HarmonyMethod(typeof(IntroSkip), nameof(Next_Prefix)));
        }

        private static bool Next_Prefix(IntroSequence __instance, ref int __result)
        {
            var elements = Elements(__instance);
            bool skipIsOn = SkipIsOn(__instance);
            int n = Current(__instance);
            while (++n < elements.Length)
            {
                var element = elements[n];
                if (!element.ShouldRun(skipIsOn)) continue;
                if (IsSplash(element))
                {
                    Plugin.Log.LogInfo($"Skipping intro screen '{element.name}' ({element.GetType().Name})");
                    continue;
                }
                break;
            }
            __result = n;
            return false;
        }

        private static bool IsSplash(IntroElement element) =>
            element.GetType() == typeof(IntroElement) || element.GetType().Name == "MovieIntroElement";
    }
}
