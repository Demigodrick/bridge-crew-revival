using System.Collections.Generic;

namespace StbcRevival.Galaxy
{
    /// <summary>
    /// The Trench's system list with or without a region's systems. The Trench asset stays loaded between missions,
    /// so its list is set before every mission on the Trench: the Trench's own systems in their own order (their
    /// list positions are their system ids), then the region's when it is on.
    /// </summary>
    public static class RegionLinks
    {
        /// <summary>Makes <paramref name="sectorList"/> exactly <paramref name="trench"/> followed by <paramref name="region"/>
        /// when on, exactly <paramref name="trench"/> when off, whatever it held before.</summary>
        public static void Apply<T>(IList<T> sectorList, IList<T> trench, IList<T> region, bool on)
        {
            // Copies first: the caller may pass the same list twice.
            var own = new List<T>(trench);
            var extra = on && region != null ? new List<T>(region) : new List<T>();
            sectorList.Clear();
            foreach (var t in own) sectorList.Add(t);
            foreach (var t in extra) sectorList.Add(t);
        }
    }
}
