using System.Collections.Generic;

namespace StbcRevival.Galaxy
{
    /// <summary>The sprite the impulse map's system overview draws for a hand-made family or Point of Interest that
    /// the file gives no icon: without one the game draws a plain white box.</summary>
    public static class MapIcons
    {
        public const string Planet = "spr_sys_map_icon_planet";

        /// <summary>The icon a node is drawn with: its own, else (a family) its first POI's, else a planet.</summary>
        public static string FallbackFor(GalaxyNode n, IList<GalaxyNode> all)
        {
            if (n.MapIcon != null) return n.MapIcon;
            if (n.Prefab == GalaxyCheck.FamilyPrefab && all != null)
                foreach (var k in all)
                    if (k.Parent == n.Id && k.Prefab == GalaxyCheck.PoiPrefab && k.MapIcon != null) return k.MapIcon;
            return Planet;
        }
    }
}
