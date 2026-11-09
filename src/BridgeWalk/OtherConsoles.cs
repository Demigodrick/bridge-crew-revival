using System;
using System.Collections.Generic;
using UnityEngine;

namespace StbcRevival.Walking
{
    /// <summary>
    /// Seated, only your own station's console runs its real screens; the others are switched off for a stand-in
    /// (ConsoleBase.UseFakeUI), and some bridges have no stand-in for some stations (the Enterprise-D's tactical rail),
    /// so walking up to them you'd find bare consoles. Standing, the other stations' real screens are switched on to
    /// look at (station input stays blocked while you stand), and back to their stand-ins when you sit down.
    /// </summary>
    internal static class OtherConsoles
    {
        private static readonly List<ConsoleBase> shown = new List<ConsoleBase>();

        internal static void Show()
        {
            Restore();
            try
            {
                foreach (var console in Resources.FindObjectsOfTypeAll<ConsoleBase>())
                {
                    if (console == null || !console.gameObject.scene.IsValid()) continue;
                    if (console.gameObject.activeSelf) continue; // your own station's, or already showing
                    var parent = console.transform.parent;
                    if (parent != null && !parent.gameObject.activeInHierarchy) continue; // a station this bridge doesn't use
                    try { console.UseFakeUI(false); shown.Add(console); }
                    catch (Exception e) { Plugin.Log.LogWarning($"Bridge walk: showing {console.name}: {e.Message}"); }
                }
                if (shown.Count > 0) Plugin.Log.LogInfo($"Bridge walk: showing the other stations' screens ({string.Join(", ", shown.ConvertAll(c => c.name).ToArray())})");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Bridge walk: other stations' screens: " + e.Message); }
        }

        internal static void Restore()
        {
            foreach (var console in shown)
            {
                if (console == null) continue;
                try { console.UseFakeUI(true); }
                catch (Exception e) { Plugin.Log.LogWarning($"Bridge walk: hiding {console.name} again: {e.Message}"); }
            }
            shown.Clear();
        }
    }
}
