using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// The pause (escape) menu has two Ubisoft Club side panels that can't work any more:
    ///  - "Friend Requests" (left) is hidden;
    ///  - "Ubisoft Club Game Invites" (right) becomes a "Bridge Crew Revival" panel with the version and website.
    /// Their texts come from the menu prefab's localized labels, not code, so they're found by their
    /// localization key or English text each time a menu pane is shown. With Diagnostics on, the menu's
    /// texts are logged (again whenever more of them exist), which shows what to match if one is missed.
    /// </summary>
    internal static class PauseMenuTweaks
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const string Website = "demigodrick.github.io/bridge-crew-revival";

        // Localization keys and their English text (StreamingAssets/Localization/english.txt).
        private static readonly string[] GameInvites = { "UplayGameInvites", "Ubisoft Club Game Invites" };
        private static readonly string[] NoGameInvites = { "UplayGameInviteLB", "No Ubisoft Club Game Invites" };

        private static int dumpedCount;
        private static bool loggedHide, loggedRetitle;
        private static FieldInfo localizedKey; // TMP_Text's stored localization key (set by SetLocalizedText)

        internal static void Patch(Harmony harmony)
        {
            var switchPane = AccessTools.Method(typeof(UI.PauseMenuUI), "NLDKOENAFNJ"); // shows a pane: Main, Options, ...
            if (switchPane == null)
            {
                Plugin.Log.LogWarning("Pause menu pane switch not found; pause menu tabs left as they are.");
                return;
            }
            harmony.Patch(switchPane, postfix: new HarmonyMethod(typeof(PauseMenuTweaks), nameof(Switch_Postfix)));
        }

        private static void Switch_Postfix(UI.PauseMenuUI __instance)
        {
            try
            {
                // The whole menu, not just the centre pane, plus the friend/invite roots it references
                // (in case they sit outside the menu's own hierarchy).
                var roots = new List<GameObject> { __instance.gameObject };
                foreach (var name in new[] { "_friendListRoot", "_friendActionRoot", "_inviteActionRoot", "_playerListRoot" })
                    if (typeof(UI.PauseMenuUI).GetField(name, All)?.GetValue(__instance) is GameObject extra) roots.Add(extra);
                var texts = roots.SelectMany(r => r.GetComponentsInChildren<Component>(true))
                    .Where(c => c != null && c.GetType().Name == "TextMeshProUGUI").Distinct().ToList();
                if (Plugin.Diagnostics.Value && texts.Count > dumpedCount)
                {
                    dumpedCount = texts.Count;
                    foreach (var t in texts)
                        Plugin.Log.LogInfo($"[diag] pause menu text {Path(t.transform)}: key='{Key(t)}' text='{Text(t)}'");
                }

                // Layout (from the Diagnostics dump): Root/Canvas/FriendAction (friend requests) and
                // Root/Canvas/InviteAction (game invites), each with _Aegis/_Enterprise/_Tng variants.
                foreach (var panel in __instance.GetComponentsInChildren<Transform>(true).Where(t => t.name == "FriendAction"))
                    if (panel.gameObject.activeSelf)
                    {
                        panel.gameObject.SetActive(false);
                        if (!loggedHide) Plugin.Log.LogInfo($"Pause menu: hid {Path(panel)}");
                        loggedHide = true;
                    }
                foreach (var t in texts)
                {
                    string path = Path(t.transform);
                    if (path.Contains("/InviteAction/Title/") || Matches(t, GameInvites)) SetText(t, "BRIDGE CREW REVIVAL");
                    else if (path.Contains("/InviteAction/NoInvites/") || Matches(t, NoGameInvites))
                        SetText(t, $"Version {Plugin.Version}\nSteam multiplayer mod\n{Website}");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Pause menu tweak failed: " + e.Message);
            }
        }

        private static bool Matches(Component tmp, string[] names)
        {
            string key = Key(tmp), text = Text(tmp);
            return names.Any(n => key.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0
                                  || string.Equals(text.Trim(), n, StringComparison.OrdinalIgnoreCase));
        }

        // SetLocalizedText keeps the game's localizer from putting the original label back.
        private static void SetText(Component tmp, string text)
        {
            if (Text(tmp) == text) return;
            if (!loggedRetitle) Plugin.Log.LogInfo($"Pause menu: retitling {Path(tmp.transform)}");
            loggedRetitle = true;
            var setLocalized = tmp.GetType().GetMethod("SetLocalizedText", new[] { typeof(string) });
            if (setLocalized != null) setLocalized.Invoke(tmp, new object[] { text });
            else tmp.GetType().GetProperty("text")?.SetValue(tmp, text, null);
        }
        private static string Text(Component tmp) => tmp.GetType().GetProperty("text")?.GetValue(tmp, null) as string ?? "";

        private static string Key(Component tmp)
        {
            if (localizedKey == null)
                for (var type = tmp.GetType(); type != null && localizedKey == null; type = type.BaseType)
                    localizedKey = type.GetField("EEHDAODMMFJ", All | BindingFlags.DeclaredOnly);
            return localizedKey?.GetValue(tmp) as string ?? "";
        }

        private static string Path(Transform t)
        {
            var parts = new List<string>();
            for (; t != null; t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }
    }
}
