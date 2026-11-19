using System;
using System.Collections.Generic;

namespace StbcRevival
{
    /// <summary>Reading SteamVR's settings (steamvr.vrsettings): no Unity here, so it can be tested.</summary>
    internal static class SteamVrSettings
    {
        internal const string QuestLayout = "BepInEx/vr/quest-binding.json";

        /// <summary>
        /// Whether SteamVR's settings point Bridge Crew's (app 527100) Touch layout at the mod's Quest layout, as the
        /// installer's Quest question sets it: an oculus_touch…CurrentURL_legacy key ending in BepInEx/vr/quest-binding.json.
        /// </summary>
        internal static bool UsesQuestLayout(string json)
        {
            if (!(MiniJson.Parse(json) is Dictionary<string, object> settings)) return false;
            if (!settings.TryGetValue("steam.app.527100", out var appValue) || !(appValue is Dictionary<string, object> app)) return false;
            foreach (var kv in app)
                if (kv.Key.StartsWith("oculus_touch", StringComparison.Ordinal) && kv.Key.EndsWith("CurrentURL_legacy", StringComparison.Ordinal)
                    && kv.Value is string url && url.Replace('\\', '/').EndsWith(QuestLayout, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }
}
