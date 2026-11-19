using System.Collections;
using HarmonyLib;

namespace StbcRevival
{
    /// <summary>
    /// The game's PlatformSpecs telemetry (TrackingEvents.PlatformSpecs, sent once the network starts) doesn't run.
    /// It pinged two Ubisoft servers, waited 5 s, then sent the PC's specs and the ping time to Ubisoft's
    /// tracking, which is gone. Its last step held the game up for a whole second, on the shuttle just after
    /// launch: the pings never answer, and getting rid of an unanswered Unity Ping waits about a second.
    /// </summary>
    internal static class NoPlatformSpecs
    {
        // Deferred: the tracking types are network types.
        internal static void Patch(Harmony harmony)
        {
            var specs = AccessTools.Inner(AccessTools.TypeByName("SVR.Network.KNJHLNDOOGP"), "LPIKHEPODPN"); // TrackingEvents.PlatformSpecs
            var coroutine = specs == null ? null : AccessTools.Method(specs, "PIKCINNNNPA"); // PlatformSpecsCoroutine
            if (coroutine == null)
            {
                Plugin.Log.LogWarning("Platform specs telemetry not found; it still runs (a 1 s pause on the shuttle after launch)");
                return;
            }
            harmony.Patch(coroutine, prefix: new HarmonyMethod(typeof(NoPlatformSpecs), nameof(Coroutine_Prefix)));
        }

        private static bool Coroutine_Prefix(ref IEnumerator __result)
        {
            __result = Nothing();
            Plugin.Log.LogInfo("Platform specs telemetry for Ubisoft: not sent (no pings, no pause)");
            return false;
        }

        private static IEnumerator Nothing()
        {
            yield break;
        }
    }
}
