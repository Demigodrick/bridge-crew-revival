using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Entry = StbcRevival.RoomBrowser.Entry;

namespace StbcRevival
{
    /// <summary>
    /// The room browser inside the game's own VR panels, so nobody needs the desktop F7 window:
    ///  - Shuttle: the "newsfeed" console (its Ubisoft news feed is gone) becomes "PUBLIC CREWS": two crews
    ///    per page with the panel's own previous/next buttons; tap a crew to join it.
    ///  - Escape menu, "More" pane, right-hand panel (shuttle, briefing room and bridge): your crew's code and
    ///    whether it's public or private, then the public crews, with the panel's own up/down buttons.
    /// Tapping a crew asks "Join crew …?" with the game's own popup. Lists refresh every few seconds.
    /// </summary>
    internal static class VrRoomPanels
    {
        private const float RefreshSeconds = 4f;
        private static float nextRefresh;

        internal static bool Enabled => Plugin.VrRoomPanels.Value;

        internal static void Patch(Harmony harmony)
        {
            if (!Enabled) return;
            void Prefix(Type type, string method, string handler)
            {
                var target = AccessTools.Method(type, method);
                if (target == null)
                {
                    Plugin.Log.LogWarning($"VR room panels: {type.Name}.{method} not found; that panel is left as it is.");
                    return;
                }
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(VrRoomPanels), handler));
            }
            // Shuttle newsfeed.
            Prefix(typeof(NewsPage), "OnEnable", nameof(News_Render));               // would fetch Ubisoft news
            Prefix(typeof(NewsPage), "FLBDMBHOBJG", nameof(News_Skip));              // reads the news list
            Prefix(typeof(NewsPage), "MLMAOLKJDHN", nameof(News_Render));            // draws the page
            Prefix(typeof(NewsPage), "OnTopNewsSelected", nameof(News_TopSelected));
            Prefix(typeof(NewsPage), "OnBottomNewsSelected", nameof(News_BottomSelected));
            // Escape menu invites panel.
            Prefix(typeof(InviteActionUI), "BAPLBKELENL", nameof(Invites_Render));   // draws the list
            Prefix(typeof(InviteActionEntry), "OnClicked", nameof(InviteEntry_Clicked));
            // Escape menu friend requests panel: the mod's details instead.
            Prefix(typeof(FriendActionUI), "BAPLBKELENL", nameof(Info_Render));
        }

        internal static void Update()
        {
            if (!Enabled || Time.realtimeSinceStartup < nextRefresh) return;
            nextRefresh = Time.realtimeSinceStartup + RefreshSeconds;
            foreach (var news in UnityEngine.Object.FindObjectsOfType<NewsPage>()) Render(news);
            foreach (var invites in UnityEngine.Object.FindObjectsOfType<InviteActionUI>()) invites.SetDirty();
        }

        // ---- Rows -------------------------------------------------------------------------------

        private sealed class Row
        {
            public string Title, Detail;
            public Entry Room;   // null: not joinable (your own crew, or the "no crews" line)
            public bool Mine;
        }

        private static List<Row> Rows(bool includeMine)
        {
            var rows = new List<Row>();
            string current = null;
            try { current = RoomBrowser.CurrentRoomName(); } catch { }
            if (includeMine && current != null)
                rows.Add(new Row
                {
                    Title = "YOUR CREW " + RoomBrowser.Code(current),
                    Detail = RoomBrowser.CurrentRoomVisible() ? "Public: anyone can join" : "Private: invite or code only",
                    Mine = true,
                });
            foreach (var e in RoomBrowser.RoomList().Where(e => e.Name != current))
                rows.Add(new Row
                {
                    Title = e.Code,
                    Detail = $"{e.Mode} - {e.Phase} - {e.Players}/{(e.MaxPlayers > 0 ? e.MaxPlayers.ToString() : "any")}{(e.Joinable ? "" : " - full")}",
                    Room = e,
                });
            return rows;
        }

        private static void Choose(Row row)
        {
            if (row == null) return;
            if (row.Mine)
            {
                Popup($"This is your crew, {row.Title.Substring("YOUR CREW ".Length)}.\n{row.Detail}", null, UI.PDHCCBFJODN.Notification);
                return;
            }
            if (row.Room == null) return;
            if (!row.Room.Joinable)
            {
                Popup($"Crew {row.Room.Code} is full or closed.", null, UI.PDHCCBFJODN.Notification);
                return;
            }
            var name = row.Room.Name;
            Popup($"Join crew {row.Room.Code}?\n{row.Detail}", ok =>
            {
                if (!ok) return;
                RoomBrowser.Join(name);
                Plugin.Log.LogInfo($"VR room panels: joining {name}: {RoomBrowser.Status}");
            }, UI.PDHCCBFJODN.Question);
        }

        // The game's localizer treats "id|text" specially, so no '|' in anything shown.
        private static void Popup(string text, Action<bool> onAnswer, UI.PDHCCBFJODN kind) =>
            UI.PopupMgr.ShowPopup(text.Replace('|', ' '), onAnswer != null ? new UI.LGNBEBILNCL(onAnswer) : null, kind, true);

        // TMP_Text's text property is renamed by the obfuscator; its accessor isn't.
        private static readonly System.Reflection.MethodInfo GetTextMethod = AccessTools.Method(typeof(TMP_Text), "get_text");

        private static string GetText(TMP_Text text) => GetTextMethod?.Invoke(text, null) as string ?? "";

        private static void SetText(TMP_Text text, string value)
        {
            value = value.Replace('|', ' ');
            if (text != null && GetText(text) != value) text.SetLocalizedText(value);
        }

        private static T Field<T>(object owner, string name) where T : class =>
            AccessTools.Field(owner.GetType(), name)?.GetValue(owner) as T;

        // ---- Shuttle newsfeed ---------------------------------------------------------------------

        private static readonly AccessTools.FieldRef<NewsPage, int> NewsPageIndex = AccessTools.FieldRefAccess<NewsPage, int>("BBPGHDDIBPO");
        private static readonly AccessTools.FieldRef<NewsPage, int> NewsPageCount = AccessTools.FieldRefAccess<NewsPage, int>("KLCKHJPAMFD");
        private static readonly Dictionary<NewsPage, Row[]> newsSlots = new Dictionary<NewsPage, Row[]>();

        private static bool News_Skip() => false;

        private static bool News_Render(NewsPage __instance)
        {
            Render(__instance);
            return false;
        }

        private static bool News_TopSelected(NewsPage __instance) => NewsSelected(__instance, 0);
        private static bool News_BottomSelected(NewsPage __instance) => NewsSelected(__instance, 1);

        private static bool NewsSelected(NewsPage page, int slot)
        {
            if (newsSlots.TryGetValue(page, out var rows)) Choose(rows[slot]);
            return false;
        }

        private static void Render(NewsPage page)
        {
            try
            {
                if (page == null || !page.isActiveAndEnabled) return;
                var rows = Rows(includeMine: false);
                if (rows.Count == 0)
                    rows.Add(new Row { Title = "No public crews right now", Detail = "Start a Quick match to host one" });
                int pages = (rows.Count + 1) / 2;
                ref int index = ref NewsPageIndex(page);
                index = Mathf.Clamp(index, 0, pages - 1);
                NewsPageCount(page) = pages;

                var slots = new[] { rows[index * 2], index * 2 + 1 < rows.Count ? rows[index * 2 + 1] : null };
                newsSlots[page] = slots;
                var banner = Field<Sprite>(page, "DefaultBanner");
                ShowSlot(Field<GameObject>(page, "TopNewsSlot"), Field<TextMeshProUGUI>(page, "TopSlotTitle"), Field<Image>(page, "TopSlotBanner"), banner, slots[0]);
                ShowSlot(Field<GameObject>(page, "BottomNewsSlot"), Field<TextMeshProUGUI>(page, "BottomSlotTitle"), Field<Image>(page, "BottomSlotBanner"), banner, slots[1]);
                Field<GameObject>(page, "NewsListPage")?.SetActive(true);
                Field<GameObject>(page, "NoNewsImage")?.SetActive(false);
                Field<GameObject>(page, "DetailsPage")?.SetActive(false);
                var prev = Field<Selectable>(page, "PrevPageButton");
                if (prev != null) prev.interactable = index > 0;
                var next = Field<Selectable>(page, "NextPageButton");
                if (next != null) next.interactable = index < pages - 1;
                RetitleNewsfeed(page);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("VR room panels: newsfeed: " + e.Message);
            }
        }

        private static void ShowSlot(GameObject slot, TextMeshProUGUI title, Image banner, Sprite defaultBanner, Row row)
        {
            if (slot == null) return;
            slot.SetActive(row != null);
            if (row == null) return;
            SetText(title, row.Title + "\n" + row.Detail);
            if (banner != null) banner.sprite = defaultBanner;
        }

        // The panel's "newsfeed" heading sits a few levels above the NewsPage component.
        private static readonly HashSet<NewsPage> retitled = new HashSet<NewsPage>();

        private static void RetitleNewsfeed(NewsPage page)
        {
            if (retitled.Contains(page)) return;
            retitled.Add(page);
            var root = page.transform;
            for (int i = 0; i < 3 && root.parent != null; i++) root = root.parent;
            foreach (var text in root.GetComponentsInChildren<TMP_Text>(true))
            {
                var key = AccessTools.Field(typeof(TMP_Text), "EEHDAODMMFJ")?.GetValue(text) as string ?? "";
                if (key.IndexOf("newsfeed", StringComparison.OrdinalIgnoreCase) >= 0
                    || string.Equals(GetText(text).Trim(), "newsfeed", StringComparison.OrdinalIgnoreCase))
                {
                    SetText(text, "PUBLIC CREWS");
                    Plugin.Log.LogInfo("VR room panels: newsfeed panel shows public crews");
                }
            }
        }

        // ---- Escape menu (InviteAction panel) -----------------------------------------------------

        private static readonly Dictionary<InviteActionEntry, Row> inviteRows = new Dictionary<InviteActionEntry, Row>();
        private static readonly AccessTools.FieldRef<InviteActionUI, int> InviteOffset = AccessTools.FieldRefAccess<InviteActionUI, int>("PPJBBELBAFH");

        private static bool Invites_Render(InviteActionUI __instance)
        {
            try
            {
                var entries = Field<InviteActionEntry[]>(__instance, "KJEOKFJEMIH");
                var entryObjects = Field<GameObject[]>(__instance, "_entryObjects");
                if (entries == null || entryObjects == null) return true;
                var rows = Rows(includeMine: true);

                ref int offset = ref InviteOffset(__instance);
                int perPage = entries.Length;
                offset = Mathf.Clamp(offset, 0, Math.Max(0, rows.Count - 1) / perPage * perPage);
                for (int i = 0; i < perPage; i++)
                {
                    var row = offset + i < rows.Count ? rows[offset + i] : null;
                    entryObjects[i].SetActive(row != null);
                    if (row == null || entries[i] == null) continue;
                    inviteRows[entries[i]] = row;
                    SetText(Field<TextMeshProUGUI>(entries[i], "_nameText"), row.Title);
                    SetText(Field<TextMeshProUGUI>(entries[i], "_actionText"), row.Detail);
                    var avatar = Field<Component>(entries[i], "_avatarIcon");
                    if (avatar != null) avatar.gameObject.SetActive(false);
                }

                bool any = rows.Count > 0;
                int page = offset / perPage + 1, pages = Math.Max(1, (rows.Count + perPage - 1) / perPage);
                var pageCount = Field<TextMeshProUGUI>(__instance, "KLCKHJPAMFD");
                SetText(pageCount, page + "/" + pages);
                if (pageCount != null) pageCount.gameObject.SetActive(any);
                PagingButton(__instance, "JNELKOHEPPF", "_upButtonInteraction", any, page > 1);
                PagingButton(__instance, "OACCJDCBMDP", "_downButtonInteraction", any, page < pages);
                var empty = Field<TextMeshProUGUI>(__instance, "DGJOHLEKBNJ");
                SetText(empty, "No public crews right now.\nStart a Quick match to host one.");
                if (empty != null) empty.gameObject.SetActive(!any);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("VR room panels: escape menu: " + e.Message);
            }
            return false;
        }

        // The left panel (Ubisoft friend requests) shows the mod's name, version and website: no list,
        // just its "no requests" line with our text.
        private static bool Info_Render(FriendActionUI __instance)
        {
            try
            {
                foreach (var entry in Field<GameObject[]>(__instance, "_entryObjects") ?? new GameObject[0])
                    if (entry != null) entry.SetActive(false);
                Field<GameObject>(__instance, "_pageCountObject")?.SetActive(false);
                foreach (var hand in new[] { "_upButtonInteraction", "_downButtonInteraction" })
                {
                    var interaction = Field<Component>(__instance, hand);
                    if (interaction != null) interaction.gameObject.SetActive(false);
                }
                var text = Field<TextMeshProUGUI>(__instance, "GOLGOCFPNGB");
                SetText(text, PauseMenuTweaks.ModInfo);
                if (text != null) text.gameObject.SetActive(true);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("VR room panels: mod info panel: " + e.Message);
            }
            return false;
        }

        private static void PagingButton(InviteActionUI ui, string button, string hand, bool shown, bool enabled)
        {
            var b = Field<Button>(ui, button);
            if (b != null) b.interactable = enabled;
            var interaction = Field<Component>(ui, hand);
            if (interaction != null) interaction.gameObject.SetActive(shown);
        }

        private static bool InviteEntry_Clicked(InviteActionEntry __instance)
        {
            if (!inviteRows.TryGetValue(__instance, out var row)) return true;
            Choose(row);
            return false;
        }
    }
}
