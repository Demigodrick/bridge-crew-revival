using System;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// Steam mode: the shuttle console's "Select region" (Photon Cloud regions, meaningless without Ubisoft's
    /// servers) becomes "Revival settings", the mod's on/off settings in the game:
    ///   - The region page's arrows (ConsoleMainMenu GLFCDJFLJDK / BJNFFMDAOGH) step through the settings, its
    ///     text shows the one picked and whether it's on, and its accept button (FMNEJELNGFC, which set the
    ///     region) switches it. The last entry, "Back", returns to the main menu.
    ///   - Changes are saved to stbc.revival.cfg straight away (BepInEx saves on set).
    ///   - Also on the page: joining a crew by room code (SteamVR's keyboard, VrKeyboard) and which crews are
    ///     listed (any, The Next Generation only, none).
    /// Other modes keep the region page as it was (it picks the Photon Cloud region there).
    /// </summary>
    internal static class RevivalSettings
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const string Title = "Revival settings";

        // A line on the page: its text (label and value) and what the accept button says and does.
        private sealed class Entry
        {
            public Func<string> Text;
            public Func<string> Button;
            public Action Press;
        }

        private static Entry Switch(string label, Func<ConfigEntry<bool>> setting, bool restart = false) => new Entry
        {
            Text = () => $"{label}: {(setting().Value ? "ON" : "OFF")}{(restart ? " (next launch)" : "")}",
            Button = () => setting().Value ? "Turn off" : "Turn on",
            Press = () =>
            {
                setting().Value = !setting().Value;
                Plugin.Log.LogInfo($"Revival settings: {label} {(setting().Value ? "on" : "off")}");
            },
        };

        private static readonly Entry[] Entries =
        {
            new Entry
            {
                Text = () => VrKeyboard.Status ?? "Join a crew by its room code",
                Button = () => "Type code",
                Press = () => VrKeyboard.Open("Room or share code", 24, code => RoomBrowser.Join(code)),
            },
            Switch("Skip intro videos", () => Plugin.SkipIntro, restart: true),
            Switch("Skip mic check", () => Plugin.SkipMicCheck, restart: true),
            Switch("Voice commands", () => Plugin.VoiceCommands, restart: true),
            Switch("Romulan face", () => Plugin.RomulanFace),
            Switch("Chime when someone joins", () => Plugin.PlayerJoinedAlert),
            Switch("Public crews in VR", () => Plugin.VrRoomPanels),
            new Entry
            {
                Text = () => "Crews listed: " + ContentName(Plugin.CrewContentFilter.Value),
                Button = () => "Change",
                Press = () =>
                {
                    var next = (CrewContent)(((int)Plugin.CrewContentFilter.Value + 1) % 3);
                    Plugin.CrewContentFilter.Value = next;
                    Plugin.Log.LogInfo($"Revival settings: crews listed {next}");
                },
            },
            Switch("Hide full crews", () => Plugin.HideFullCrews),
            Switch("Hide crews on a mission", () => Plugin.HideCrewsInMission),
            Switch("Show my crew in Steam", () => Plugin.SteamPresence),
            Switch("Check for updates", () => Plugin.CheckForUpdates),
            Switch("Excelsior, holodeck bridge (test, when you host)", () => Plugin.ExcelsiorTest),
            new Entry
            {
                Text = () => Blocklist.Count == 0 ? "Blocked players: none (block someone after kicking them)" : $"Blocked players: {Blocklist.Count}",
                Button = () => Blocklist.Count == 0 ? "-" : "Unblock all",
                Press = () => { if (Blocklist.Count > 0) Blocklist.Clear(); },
            },
        };

        private static string ContentName(CrewContent content) =>
            content == CrewContent.NextGenerationOnly ? "TNG only" : content == CrewContent.NoNextGeneration ? "no TNG" : "any";

        private static int index;
        private static float nextRetitle;

        private static bool Active => SteamMode.Enabled;
        private static bool OnBack => index == Entries.Length;

        internal static void Patch(Harmony harmony)
        {
            var menu = typeof(ConsoleMainMenu);
            var open = AccessTools.Method(menu, "PKOIDGNDHFO");
            var left = AccessTools.Method(menu, "GLFCDJFLJDK");
            var right = AccessTools.Method(menu, "BJNFFMDAOGH");
            var accept = AccessTools.Method(menu, "FMNEJELNGFC");
            if (open == null || left == null || right == null || accept == null)
            {
                Plugin.Log.LogWarning("Revival settings: the shuttle's region page wasn't found; region select left as it is");
                return;
            }
            harmony.Patch(open, postfix: new HarmonyMethod(typeof(RevivalSettings), nameof(Open_Postfix)));
            harmony.Patch(left, prefix: new HarmonyMethod(typeof(RevivalSettings), nameof(Left_Prefix)));
            harmony.Patch(right, prefix: new HarmonyMethod(typeof(RevivalSettings), nameof(Right_Prefix)));
            harmony.Patch(accept, prefix: new HarmonyMethod(typeof(RevivalSettings), nameof(Accept_Prefix)));
        }

        private static void Open_Postfix(ConsoleMainMenu __instance)
        {
            if (!Active) return;
            index = 0;
            Show(__instance);
        }

        private static bool Left_Prefix(ConsoleMainMenu __instance)
        {
            if (!Active) return true;
            index = (index + Entries.Length) % (Entries.Length + 1);
            Show(__instance);
            return false;
        }

        private static bool Right_Prefix(ConsoleMainMenu __instance)
        {
            if (!Active) return true;
            index = (index + 1) % (Entries.Length + 1);
            Show(__instance);
            return false;
        }

        // Accept: switch the setting shown, or on "Back", back to the main menu (without touching the region).
        private static bool Accept_Prefix(ConsoleMainMenu __instance)
        {
            if (!Active) return true;
            if (OnBack)
            {
                index = 0;
                var state = AccessTools.Field(typeof(ConsoleMainMenu), "CKGKIBEGPOA")?.GetValue(__instance);
                var setState = state?.GetType().GetMethod("SetState", All);
                var main = setState == null ? null : Enum.Parse(setState.GetParameters()[0].ParameterType, "Main");
                setState?.Invoke(state, new[] { main });
                return false;
            }
            Entries[index].Press();
            Show(__instance);
            return false;
        }

        private static void Show(ConsoleMainMenu menu)
        {
            string text, button;
            if (OnBack)
            {
                text = "Back to the main menu";
                button = "Back";
            }
            else
            {
                text = Entries[index].Text();
                button = Entries[index].Button();
            }
            if (Field(menu, "_regionSelectRegionText") is Component label)
            {
                SetText(label, text);
                label.GetType().GetProperty("enableAutoSizing")?.SetValue(label, true, null);
            }
            if (Field(menu, "_regionAcceptBtn") is Component accept)
                foreach (var tmp in Texts(accept.gameObject)) SetText(tmp, button);
            Retitle(menu);
        }

        // The main menu's button and the page's heading say "Revival settings" (checked once a second, as the
        // game's localizer sets them when the console appears).
        internal static void Update()
        {
            if (!Active || Time.unscaledTime < nextRetitle) return;
            nextRetitle = Time.unscaledTime + 1f;
            foreach (var menu in UnityEngine.Object.FindObjectsOfType<ConsoleMainMenu>())
                Retitle(menu);
        }

        private static void Retitle(ConsoleMainMenu menu)
        {
            if (Field(menu, "_regionSelectButton") is Component button)
                foreach (var tmp in Texts(button.gameObject)) SetText(tmp, Title);
            // The page's heading: any text on it that isn't the value line or the accept button's.
            if (Field(menu, "_regionSelectGO") is GameObject page)
            {
                var value = Field(menu, "_regionSelectRegionText") as Component;
                var accept = Field(menu, "_regionAcceptBtn") as Component;
                foreach (var tmp in Texts(page))
                {
                    if (tmp == value || (accept != null && tmp.transform.IsChildOf(accept.transform))) continue;
                    var current = tmp.GetType().GetProperty("text")?.GetValue(tmp, null) as string ?? "";
                    if (current.IndexOf("region", StringComparison.OrdinalIgnoreCase) >= 0 || current == Title.ToUpperInvariant())
                        SetText(tmp, Title.ToUpperInvariant());
                    else if (current.IndexOf("Warning", StringComparison.OrdinalIgnoreCase) >= 0 || current.IndexOf("perform", StringComparison.OrdinalIgnoreCase) >= 0)
                        SetText(tmp, "Saved straight away. Arrows pick a setting.");
                }
            }
        }

        private static object Field(ConsoleMainMenu menu, string name) => AccessTools.Field(typeof(ConsoleMainMenu), name)?.GetValue(menu);

        private static Component[] Texts(GameObject root) =>
            root.GetComponentsInChildren<Component>(true).Where(c => c != null && c.GetType().Name == "TextMeshProUGUI").ToArray();

        // SetLocalizedText keeps the game's localizer from putting the original label back.
        private static void SetText(Component tmp, string text)
        {
            if ((tmp.GetType().GetProperty("text")?.GetValue(tmp, null) as string) == text) return;
            var setLocalized = tmp.GetType().GetMethod("SetLocalizedText", new[] { typeof(string) });
            if (setLocalized != null) setLocalized.Invoke(tmp, new object[] { text });
            else tmp.GetType().GetProperty("text")?.SetValue(tmp, text, null);
        }
    }
}
