using System.Collections.Generic;
using HarmonyLib;

namespace StbcRevival
{
    /// <summary>
    /// PvP: the helm's warp map looks up its ship's system among the map's locations (WarpPanel.GetItem, a plain
    /// dictionary read) every frame to place the ship icon. On a crew member's game a warp map can be built before
    /// its ship's systems are known (their ship reaches them late), and the read threw every frame: 423 errors in one
    /// match. A system that isn't on the map is "no location", which both callers already handle (no icon moved).
    /// </summary>
    internal static class PvpWarpMap
    {
        private static readonly AccessTools.FieldRef<WarpPanel, Dictionary<int, WarpMapLocation>> Locations =
            AccessTools.FieldRefAccess<WarpPanel, Dictionary<int, WarpMapLocation>>("JBDJGIKEICO");
        private static bool told;

        internal static void Patch(Harmony harmony)
        {
            var getItem = AccessTools.Method(typeof(WarpPanel), "CKGNFMGNCAE"); // GetItem(system ID)
            if (getItem == null) { Plugin.Log.LogWarning("PvP: WarpPanel.GetItem not found; a warp map missing its ship's system keeps throwing"); return; }
            harmony.Patch(getItem, prefix: new HarmonyMethod(typeof(PvpWarpMap), nameof(GetItem_Prefix)));
        }

        private static bool GetItem_Prefix(WarpPanel __instance, int __0, ref WarpMapLocation __result)
        {
            var map = Locations(__instance);
            if (map == null || map.TryGetValue(__0, out __result)) return map == null;
            if (!told) { told = true; Plugin.Log.LogInfo($"PvP: a warp map hasn't system {__0} on it ({map.Count} systems); the ship icon stays where it is"); }
            return false;
        }
    }
}
