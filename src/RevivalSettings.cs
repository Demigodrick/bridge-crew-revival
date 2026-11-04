using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StbcRevival
{
    /// <summary>
    /// Steam mode: the shuttle console's "Select region" (Photon Cloud regions, meaningless without Ubisoft's
    /// servers) becomes "Revival settings", the mod's settings in the game, as a list on the whole screen:
    ///   - Two columns of the main menu's own GAME MODE toggles (copies), a row per setting. An on/off setting
    ///     is ticked while it's on, and a click switches it; a setting with a few values shows the one picked
    ///     and a click moves to the next; joining a crew by room code (SteamVR's keyboard, VrKeyboard) and
    ///     unblocking players act on a click.
    ///   - The region page's own parts (its background, arrows and value line) are hidden while it shows the
    ///     list; its heading says REVIVAL SETTINGS and its accept button (FMNEJELNGFC) is BACK.
    ///   - Changes are saved to stbc.revival.cfg straight away (BepInEx saves on set).
    /// Other modes keep the region page as it was (it picks the Photon Cloud region there).
    /// </summary>
    internal static class RevivalSettings
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const string Title = "Revival settings";

        // A row: its text, whether it's ticked (on/off settings only) and what a click does.
        private sealed class Entry
        {
            public Func<string> Text;
            public Func<bool> Ticked = () => false;
            public Action Press;
        }

        private static Entry Switch(string label, Func<ConfigEntry<bool>> setting, bool restart = false) => new Entry
        {
            Text = () => label + (restart ? " (next launch)" : ""),
            Ticked = () => setting().Value,
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
                Press = () => VrKeyboard.Open(Translation.T("Room or share code"), 24, code => RoomBrowser.Join(code)),
            },
            Switch("Skip intro videos", () => Plugin.SkipIntro, restart: true),
            Switch("Skip mic check", () => Plugin.SkipMicCheck, restart: true),
            Switch("Voice commands", () => Plugin.VoiceCommands, restart: true),
            Switch("Voice orders from any station", () => Plugin.VoiceAnyStation),
            // Which Klingon captain's head the creator's KLINGON wears (wearing one or not is the creator's).
            new Entry
            {
                Text = () => "Klingon head: " + KlingonHead.Names[HeadChoice()].ToUpperInvariant(),
                Press = () =>
                {
                    Plugin.KlingonHeadChoice.Value = HeadChoice() == 1 ? 2 : 1;
                    if (Plugin.KlingonHead.Value > 0) Plugin.KlingonHead.Value = Plugin.KlingonHeadChoice.Value; // wearing one: the new one
                    Plugin.Log.LogInfo($"Revival settings: Klingon head {KlingonHead.Names[HeadChoice()]}");
                },
            },
            Switch("Chime when someone joins", () => Plugin.PlayerJoinedAlert),
            Switch("Public crews in VR", () => Plugin.VrRoomPanels),
            new Entry
            {
                Text = () => "Crews listed: " + ContentName(Plugin.CrewContentFilter.Value),
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
            new Entry
            {
                Text = () => Blocklist.Count == 0 ? "Blocked players: none (block someone after kicking them)" : $"Blocked players: {Blocklist.Count} (unblock all)",
                Press = () => { if (Blocklist.Count > 0) Blocklist.Clear(); },
            },
        };

        private static int HeadChoice() => Mathf.Clamp(Plugin.KlingonHeadChoice.Value, 1, 2);

        private static string ContentName(CrewContent content) =>
            content == CrewContent.NextGenerationOnly ? "TNG only" : content == CrewContent.NoNextGeneration ? "no TNG" : "any";

        // The list's layout, in the page's own units (the shuttle screen is about 800 by 400): two columns, the
        // rows a toggle's height and a half apart, under the heading and above BACK.
        private const float ColumnLeft = -340f, ColumnRight = 30f, FirstRow = 112f, RowStep = 33f, LabelWidth = 300f;
        private static readonly Vector3 HeadingAt = new Vector3(0f, 158f, -1f), BackAt = new Vector3(0f, -160f, 0.01f);
        private static readonly Vector2 BackingSize = new Vector2(820f, 380f);

        private sealed class Page
        {
            public readonly List<Toggle> Rows = new List<Toggle>();
            public readonly List<TMP_Text> Labels = new List<TMP_Text>();
            public bool HidMain;
        }

        private static readonly Dictionary<ConsoleMainMenu, Page> pages = new Dictionary<ConsoleMainMenu, Page>();
        private static bool showing; // the list is being set to the settings: its toggles' changes aren't clicks
        private static float nextRetitle;

        private static bool Active => SteamMode.Enabled;

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
            harmony.Patch(left, prefix: new HarmonyMethod(typeof(RevivalSettings), nameof(Arrow_Prefix)));
            harmony.Patch(right, prefix: new HarmonyMethod(typeof(RevivalSettings), nameof(Arrow_Prefix)));
            harmony.Patch(accept, prefix: new HarmonyMethod(typeof(RevivalSettings), nameof(Accept_Prefix)));
        }

        private static void Open_Postfix(ConsoleMainMenu __instance)
        {
            if (!Active) return;
            if (!pages.ContainsKey(__instance)) Build(__instance);
            Show(__instance);
            CoverMainMenu(__instance);
        }

        // The main menu's page stays on under the region page (it's a popup): hidden while the list shows, back
        // once it's gone (checked every frame).
        private static void CoverMainMenu(ConsoleMainMenu menu)
        {
            if (!pages.TryGetValue(menu, out var page) || !(Field(menu, "_mainMenuGO") is GameObject main)) return;
            bool open = Field(menu, "_regionSelectGO") is GameObject region && region.activeInHierarchy;
            if (open && main.activeSelf)
            {
                main.SetActive(false);
                page.HidMain = true;
            }
            else if (!open && page.HidMain)
            {
                main.SetActive(true);
                page.HidMain = false;
            }
        }

        // The arrows are hidden: nothing to step through.
        private static bool Arrow_Prefix() => !Active;

        // Accept is BACK: back to the main menu (without touching the region).
        private static bool Accept_Prefix(ConsoleMainMenu __instance)
        {
            if (!Active) return true;
            if (pages.TryGetValue(__instance, out var page) && page.HidMain && Field(__instance, "_mainMenuGO") is GameObject mainPage)
            {
                mainPage.SetActive(true);
                page.HidMain = false;
            }
            var state = AccessTools.Field(typeof(ConsoleMainMenu), "CKGKIBEGPOA")?.GetValue(__instance);
            var setState = state?.GetType().GetMethod("SetState", All);
            var main = setState == null ? null : Enum.Parse(setState.GetParameters()[0].ParameterType, "Main");
            setState?.Invoke(state, new[] { main });
            return false;
        }

        // ---- The list ----------------------------------------------------------------------------------

        // A row per setting, made from the GAME MODE list's first toggle; the page's own parts put away.
        private static void Build(ConsoleMainMenu menu)
        {
            var page = new Page();
            pages[menu] = page;
            try
            {
                var region = Field(menu, "_regionSelectGO") as GameObject;
                var infos = Field(menu, "_gameModeInfos") as Array;
                var template = infos?.Cast<object>().Select(i => Traverse.Create(i).Field("ModeSelectionToggle").GetValue<Toggle>()).FirstOrDefault(t => t != null);
                if (region == null || template == null)
                {
                    Plugin.Log.LogWarning("Revival settings: the region page or a GAME MODE toggle wasn't found; no settings list");
                    return;
                }
                var keep = new[] { Field(menu, "_regionAcceptBtn") as Component }.Where(c => c != null).Select(c => c.transform).ToList();
                var heading = Heading(menu, region);
                if (heading != null) keep.Add(heading.transform);
                foreach (Transform part in region.transform)
                    if (!keep.Any(k => k == part || k.IsChildOf(part))) part.gameObject.SetActive(false);
                if (heading != null) heading.transform.localPosition = HeadingAt;
                // A backing for the list: the page's own panel, stretched over the whole screen, behind the rows.
                var panel = region.GetComponentsInChildren<Image>(true).FirstOrDefault(im => im.name == "MessageBG");
                if (panel != null)
                {
                    var backing = (GameObject)UnityEngine.Object.Instantiate(panel.gameObject, region.transform, false);
                    backing.name = "Revival settings backing";
                    backing.SetActive(true);
                    backing.transform.SetAsFirstSibling();
                    backing.transform.localPosition = new Vector3(0f, -2f, panel.transform.localPosition.z);
                    backing.transform.localScale = Vector3.one;
                    ((RectTransform)backing.transform).sizeDelta = BackingSize;
                }
                if (Field(menu, "_regionAcceptBtn") is Component back) back.transform.localPosition = BackAt;

                int perColumn = (Entries.Length + 1) / 2;
                for (int i = 0; i < Entries.Length; i++)
                {
                    var entry = Entries[i];
                    var go = (GameObject)UnityEngine.Object.Instantiate(template.gameObject, region.transform, false);
                    go.name = "Revival setting " + i;
                    go.SetActive(true);
                    go.transform.localPosition = new Vector3(i < perColumn ? ColumnLeft : ColumnRight, FirstRow - (i % perColumn) * RowStep, template.transform.localPosition.z);
                    var toggle = go.GetComponent<Toggle>();
                    toggle.onValueChanged.RemoveAllListeners();
                    toggle.group = null;
                    toggle.interactable = true;
                    toggle.onValueChanged.AddListener(_ => Clicked(menu, entry));
                    // The right column is on the other hand's side of the console from the toggle it copies.
                    foreach (var hand in go.GetComponentsInChildren<HandInteraction>(true))
                        AccessTools.Field(typeof(HandInteraction), "_sideRestriction")?.SetValue(hand, Enum.Parse(AccessTools.Field(typeof(HandInteraction), "_sideRestriction").FieldType, "Either"));
                    var label = go.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault();
                    if (label != null)
                    {
                        // Wider to the right only: it still starts just right of the tick box.
                        var rect = label.rectTransform;
                        float left = rect.localPosition.x - rect.rect.width * rect.pivot.x;
                        rect.sizeDelta = new Vector2(rect.sizeDelta.x + LabelWidth - rect.rect.width, rect.sizeDelta.y);
                        rect.localPosition = new Vector3(left + LabelWidth * rect.pivot.x, rect.localPosition.y, rect.localPosition.z);
                        label.AEDOGHDAPML = EGJGCPILBBH.Left; // alignment: TextAlignmentOptions.Left
                        // TextMeshPro's names are obfuscated here: enableAutoSizing, fontSizeMax / fontSizeMin
                        // (from fontSize), enableWordWrapping.
                        label.BMAIEFHKPMA = true;
                        label.LOBPGECEJCD = label.ACEGMMMIHCN;
                        label.CNPBHPONNJK = label.ACEGMMMIHCN * 0.5f;
                        label.GADKGKMKHCE = false;
                    }
                    page.Rows.Add(toggle);
                    page.Labels.Add(label);
                }
                PvpRoles.TellHands(menu);
                Plugin.Log.LogInfo($"Revival settings: {Entries.Length} settings listed in two columns");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Revival settings: building the list: " + e.Message);
            }
        }

        private static void Clicked(ConsoleMainMenu menu, Entry entry)
        {
            if (showing) return;
            entry.Press();
            Show(menu);
        }

        // Every row's text and tick from the settings (they can change elsewhere: the F7 window, the keyboard).
        private static void Show(ConsoleMainMenu menu)
        {
            if (!pages.TryGetValue(menu, out var page)) return;
            showing = true;
            try
            {
                for (int i = 0; i < page.Rows.Count; i++)
                {
                    if (page.Labels[i] != null) SetText(page.Labels[i], Entries[i].Text());
                    bool ticked = Entries[i].Ticked();
                    if (page.Rows[i] != null && page.Rows[i].isOn != ticked) page.Rows[i].isOn = ticked;
                }
                if (Field(menu, "_regionAcceptBtn") is Component back)
                    foreach (var tmp in Texts(back.gameObject)) SetText(tmp, "Back");
            }
            finally
            {
                showing = false;
            }
            Retitle(menu);
        }

        // The page's heading: the text on it that isn't its value line or a button's.
        private static TMP_Text Heading(ConsoleMainMenu menu, GameObject region)
        {
            var value = Field(menu, "_regionSelectRegionText") as Component;
            return region.GetComponentsInChildren<TMP_Text>(true)
                .Where(t => t != value && t.GetComponentInParent<Selectable>() == null)
                .OrderByDescending(t => t.transform.localPosition.y).FirstOrDefault();
        }

        // The main menu's button and the page's heading say "Revival settings", and the list keeps up with the
        // settings (checked once a second, as the game's localizer sets the texts when the console appears).
        internal static void Update()
        {
            if (!Active) return;
            foreach (var menu in pages.Keys.Where(m => m != null).ToList()) CoverMainMenu(menu);
            if (Time.unscaledTime < nextRetitle) return;
            nextRetitle = Time.unscaledTime + 1f;
            foreach (var menu in UnityEngine.Object.FindObjectsOfType<ConsoleMainMenu>())
            {
                if (pages.ContainsKey(menu)) Show(menu);
                else Retitle(menu);
            }
            foreach (var gone in pages.Keys.Where(m => m == null).ToList()) pages.Remove(gone);
        }

        private static void Retitle(ConsoleMainMenu menu)
        {
            if (Field(menu, "_regionSelectButton") is Component button)
                foreach (var tmp in Texts(button.gameObject)) SetText(tmp, Title);
            if (Field(menu, "_regionSelectGO") is GameObject region && Heading(menu, region) is TMP_Text heading)
                SetText(heading, Title.ToUpperInvariant());
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
