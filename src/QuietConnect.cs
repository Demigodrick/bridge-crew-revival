using HarmonyLib;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// With [Startup] SkipIntro: the "Connecting..." box of the network connect screen (NetConnectIntroElement)
    /// stays invisible for its first second, so a quick connection doesn't flash it on the way to the shuttle. A
    /// slower one shows it as before. Only its look changes: the screen itself runs (it starts the connection).
    /// The platform connect screen already waits before showing its box.
    /// </summary>
    internal static class QuietConnect
    {
        private const float HiddenSeconds = 1f;

        private static readonly AccessTools.FieldRef<IntroElement, GameObject[]> Related =
            AccessTools.FieldRefAccess<IntroElement, GameObject[]>("_relatedObjects");

        private static float shownAt = -1f;

        // Deferred: NetConnectIntroElement's methods use Photon types.
        internal static void Patch(Harmony harmony)
        {
            if (!Plugin.SkipIntro.Value) return;
            var enable = AccessTools.Method(typeof(NetConnectIntroElement), "OnEnable");
            var update = AccessTools.Method(typeof(NetConnectIntroElement), "Update");
            var disable = AccessTools.Method(typeof(NetConnectIntroElement), "OnDisable");
            if (enable == null || update == null || disable == null)
            {
                Plugin.Log.LogWarning("Connecting box: the network connect screen wasn't found; it shows at once");
                return;
            }
            harmony.Patch(enable, postfix: new HarmonyMethod(typeof(QuietConnect), nameof(Enable_Postfix)));
            harmony.Patch(update, postfix: new HarmonyMethod(typeof(QuietConnect), nameof(Update_Postfix)));
            harmony.Patch(disable, postfix: new HarmonyMethod(typeof(QuietConnect), nameof(Disable_Postfix)));
        }

        private static void Enable_Postfix(NetConnectIntroElement __instance)
        {
            shownAt = Time.realtimeSinceStartup + HiddenSeconds;
            SetVisible(__instance, false);
        }

        private static void Update_Postfix(NetConnectIntroElement __instance)
        {
            if (shownAt < 0f || Time.realtimeSinceStartup < shownAt) return;
            shownAt = -1f;
            SetVisible(__instance, true);
        }

        private static void Disable_Postfix(NetConnectIntroElement __instance)
        {
            shownAt = -1f;
            SetVisible(__instance, true); // as the game left it, for a later connect (after an error, say)
        }

        private static void SetVisible(IntroElement element, bool visible)
        {
            try
            {
                Fade(element.gameObject, visible);
                var related = Related(element);
                if (related != null)
                    foreach (var o in related)
                        if (o != null) Fade(o, visible);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Connecting box: couldn't hide it: " + e.Message);
            }
        }

        private static void Fade(GameObject o, bool visible)
        {
            var group = o.GetComponent<CanvasGroup>();
            if (group == null)
            {
                if (visible) return; // never hidden
                group = o.AddComponent<CanvasGroup>();
            }
            group.alpha = visible ? 1f : 0f;
        }
    }
}
