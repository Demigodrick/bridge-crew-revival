using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StbcRevival
{
    /// <summary>
    /// PvP test, crews of up to four per ship: eight role slots in the briefing room, 0–3 ship A's (Captain,
    /// Helm, Tactical, Engineering as the game numbers them) and 4–7 ship B's.
    ///  - NovaRoleManager keeps eight slots (it had four), and sends all eight in its state (the count first:
    ///    everyone in a crew runs the same mod version). In a crew without the PvP test, 4–7 just stay empty.
    ///  - The briefing room's role list gets a "SHIP B" copy of its four rows, shown while the PvP test is
    ///    picked; picking one assigns slot 4–7 (the game's own toggle handling, by the row's role number).
    ///  - At launch the game sets each player's role to their slot number: slots 4–7 become the role 0–3, and
    ///    the slot says the ship (PvpShips: 4–7 is ship B).
    ///  - Seating: the game seats a player at "the" station for their role, which is ship A's; while it seats a
    ///    ship B player, that lookup (and the free-station fallback) gives ship B's copies (PvpConsoles).
    ///  - Room size: with the PvP test picked and four already in, the host opens the room to eight. Quick
    ///    Match only matches rooms of the size it asks for, so it finds the crew until it's full at four.
    /// </summary>
    internal static class PvpRoles
    {
        internal const int Slots = 8;

        private static readonly AccessTools.FieldRef<NovaRoleManager, CBBCCGMALMP[]> Assigned =
            AccessTools.FieldRefAccess<NovaRoleManager, CBBCCGMALMP[]>("JIOGBBNFGMJ"); // _assignedPlayerRoles
        private static readonly AccessTools.FieldRef<NovaRoleManager, List<CBBCCGMALMP>> Unassigned =
            AccessTools.FieldRefAccess<NovaRoleManager, List<CBBCCGMALMP>>("LPPEEFGBIID"); // _unassignedPlayerRoles
        private static readonly FieldInfo RoleInfos = AccessTools.Field(typeof(ConsoleBriefingRoom), "_playerRoleInfos");
        private static readonly MethodInfo RolesInstance = AccessTools.Method(typeof(NovaRoleManager), "get_Instance");
        private static NovaRoleManager Roles => RolesInstance?.Invoke(null, null) as NovaRoleManager;
        private static readonly PropertyInfo PlayerRole = AccessTools.Property(typeof(NovaPlayerInfo), "FJMHMOOJNJG"); // Role
        private static readonly FieldInfo Pawn = AccessTools.Field(typeof(NovaStation), "Pawn");

        internal static void Patch(Harmony harmony)
        {
            void Hook(Type type, string method, string prefix = null, string postfix = null, string finalizer = null, Type[] args = null)
            {
                var target = type == null ? null : args == null ? AccessTools.Method(type, method) : AccessTools.Method(type, method, args);
                if (target == null)
                {
                    Plugin.Log.LogWarning($"PvP: {type?.Name}.{method} not found; crews of more than one per ship won't work");
                    return;
                }
                harmony.Patch(target,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(PvpRoles), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(PvpRoles), postfix),
                    finalizer: finalizer == null ? null : new HarmonyMethod(typeof(PvpRoles), finalizer));
            }
            var roles = typeof(NovaRoleManager);
            Hook(roles, "Awake", postfix: nameof(Widen_Postfix));
            Hook(roles, "Start", postfix: nameof(Widen_Postfix));
            Hook(roles, "NetworkSerialize", prefix: nameof(Send_Prefix));
            Hook(roles, "NetworkDeserialize", prefix: nameof(Receive_Prefix));
            var briefing = typeof(ConsoleBriefingRoom);
            Hook(briefing, "Awake", postfix: nameof(BriefingAwake_Postfix));
            Hook(briefing, "BatchUpdate", postfix: nameof(BriefingRefresh_Postfix));
            Hook(briefing, "BJBJFCFIDOA", prefix: nameof(ShowSlot_Prefix));   // shows a slot's player on its row
            Hook(briefing, "GLFEOIFJEHE", prefix: nameof(Launch_Prefix), finalizer: nameof(Launch_Finalizer));
            Hook(briefing, "JJJBJCNIJBD", postfix: nameof(RolePicked_Postfix)); // a role toggle (slot, on)
            // What the mouse hovers and presses in the briefing room (ship B's rows not taking clicks).
            var pointer = AccessTools.TypeByName("SVR.LAOGNJEBFCP");
            foreach (var m in new[] { "OnEnter", "OnDown" })
            {
                var target = pointer == null ? null : AccessTools.Method(pointer, m, new[] { typeof(HandInteraction) });
                if (target != null) harmony.Patch(target, prefix: new HarmonyMethod(typeof(PvpRoles), m == "OnEnter" ? nameof(PointerEnter_Prefix) : nameof(PointerDown_Prefix)));
            }
            // The game's update loop swallows errors; the hands' are logged (ship B's rows froze them).
            foreach (var type in new[] { "HandAnimator", "HandInteractionGrid", "HandInteraction" })
            {
                var update = AccessTools.Method(AccessTools.TypeByName(type), "BatchUpdate");
                if (update != null) harmony.Patch(update, finalizer: new HarmonyMethod(typeof(PvpRoles), nameof(HandUpdate_Finalizer)));
            }
            var spawn = AccessTools.TypeByName("NovaSpawnManager");
            var seating = spawn == null ? null : AccessTools.Inner(spawn, "<SpawnNewlyAddedPlayerOnClients>c__Iterator1F3");
            seatPlayerField = seating == null ? null : AccessTools.Field(seating, "PNGLMGLMLJM");
            Hook(seating, "MoveNext", prefix: nameof(Seating_Prefix), finalizer: nameof(Seating_Finalizer));
            var registry = AccessTools.TypeByName("GGPMPCDFHAK");
            Hook(registry, "GetStationIndex", postfix: nameof(StationIndex_Postfix), args: new[] { typeof(FLCHMJJDKDL) });
            Hook(registry, "GetFreeStationIndex", postfix: nameof(FreeStation_Postfix));
        }

        // ---- Eight slots --------------------------------------------------------------------------

        private static void Widen_Postfix(NovaRoleManager __instance) => Widen(__instance);

        private static CBBCCGMALMP[] Widen(NovaRoleManager roles)
        {
            var slots = Assigned(roles);
            if (slots != null && slots.Length >= Slots) return slots;
            var wider = new CBBCCGMALMP[Slots];
            if (slots != null) Array.Copy(slots, wider, Math.Min(slots.Length, Slots));
            Assigned(roles) = wider;
            return wider;
        }

        // The game's state, with the slot count before the slots (it sent exactly four).
        private static bool Send_Prefix(NovaRoleManager __instance, SVR.Network.PLPKPDFOLDL __0, ref bool __result)
        {
            var stream = __0;
            var waiting = Unassigned(__instance) ?? new List<CBBCCGMALMP>();
            var slots = Widen(__instance);
            stream.WriteByte((byte)waiting.Count);
            foreach (var player in waiting) stream.WriteInt(player.KLNDGCGHADM);
            stream.WriteByte((byte)slots.Length);
            foreach (var player in slots) stream.WriteInt(player != null ? player.KLNDGCGHADM : -1);
            __result = !stream.OKCBBENHIBC;
            return false;
        }

        private static bool Receive_Prefix(NovaRoleManager __instance, SVR.Network.PLPKPDFOLDL __0, ref bool __result)
        {
            var stream = __0;
            __result = false;
            byte count = 0;
            if (!stream.ReadByte(ref count)) return false;
            var waiting = Unassigned(__instance);
            waiting.Clear();
            for (int i = 0; i < count; i++)
            {
                int id = 0;
                if (!stream.ReadInt(ref id)) return false;
                var player = CBBCCGMALMP.Find(id);
                if (player != null) waiting.Add(player);
            }
            byte slotCount = 0;
            if (!stream.ReadByte(ref slotCount)) return false;
            var slots = Widen(__instance);
            for (int i = 0; i < slotCount; i++)
            {
                int id = 0;
                if (!stream.ReadInt(ref id)) return false;
                if (i < slots.Length) slots[i] = id > 0 ? CBBCCGMALMP.Find(id) : null;
            }
            __result = !stream.OKCBBENHIBC;
            return false;
        }

        /// <summary>The slot a player holds (0–7), or -1.</summary>
        internal static int SlotOf(int actor)
        {
            var roles = Roles;
            var slots = roles == null ? null : Assigned(roles);
            if (slots == null) return -1;
            for (int i = 0; i < slots.Length; i++)
                if (slots[i] != null && slots[i].KLNDGCGHADM == actor) return i;
            return -1;
        }

        /// <summary>The ship a player's slot puts them on ("A" for 0–3, "B" for 4–7), or null without one.</summary>
        internal static string ShipOfSlot(int actor)
        {
            int slot = SlotOf(actor);
            return slot < 0 ? null : slot < 4 ? "A" : "B";
        }

        // ---- The briefing room's ship B rows ------------------------------------------------------

        // Each briefing console (one per station view) has its own rows: ship B's copies, ship A's labels to
        // rename, and the mission panel, which a PvP room doesn't need (the mission is PvP).
        private sealed class Layout
        {
            public readonly List<GameObject> ShipB = new List<GameObject>();
            public readonly List<KeyValuePair<TMP_Text, string>> ShipALabels = new List<KeyValuePair<TMP_Text, string>>();
            public List<GameObject> MissionPanel;
            public readonly List<Graphic> Cleared = new List<Graphic>();
            public bool Shown, Settled, PageWasShown;
            public float DiagnoseAt = -1f;
        }

        private static readonly Dictionary<ConsoleBriefingRoom, Layout> layouts = new Dictionary<ConsoleBriefingRoom, Layout>();

        // Before the console's Start, which wires each row's toggle to its role number (4–7 for these).
        private static void BriefingAwake_Postfix(ConsoleBriefingRoom __instance)
        {
            try
            {
                if (!(RoleInfos?.GetValue(__instance) is Array infos) || infos.Length != 4) return;
                var infoType = infos.GetType().GetElementType();
                var roleField = AccessTools.Field(infoType, "Role");
                var parts = AccessTools.GetDeclaredFields(infoType).Where(f => typeof(Component).IsAssignableFrom(f.FieldType)).ToList();
                var wider = Array.CreateInstance(infoType, 8);
                Array.Copy(infos, wider, 4);
                var layout = new Layout();
                for (int i = 0; i < 4; i++)
                {
                    var info = infos.GetValue(i);
                    var components = parts.Select(f => f.GetValue(info) as Component).ToList();
                    var row = RowOf(components, infos, parts, i);
                    if (row == null)
                    {
                        Plugin.Log.LogWarning("PvP: the briefing room's role rows aren't laid out as expected; no ship B rows");
                        return;
                    }
                    var copy = (GameObject)UnityEngine.Object.Instantiate(row.gameObject, row.parent, false);
                    copy.name = row.name + " (ship B)";
                    var rect = row as RectTransform;
                    float width = rect != null && rect.rect.width > 1f ? rect.rect.width : 400f;
                    copy.transform.localPosition = row.localPosition + new Vector3(width * 1.18f, 0f, 0f);
                    var clone = Activator.CreateInstance(infoType);
                    roleField.SetValue(clone, (FLCHMJJDKDL)((int)(FLCHMJJDKDL)roleField.GetValue(info) + 4));
                    foreach (var part in parts)
                        if (part.GetValue(info) is Component original && original != null)
                            part.SetValue(clone, Counterpart(row, copy.transform, original));
                    var role = RoleName((int)(FLCHMJJDKDL)roleField.GetValue(info));
                    foreach (var text in copy.GetComponentsInChildren<TMP_Text>(true))
                        if (text == (parts.FirstOrDefault(p => p.Name == "RoleText")?.GetValue(clone) as Component))
                            text.SetLocalizedText("SHIP B " + role);
                    if (parts.FirstOrDefault(p => p.Name == "RoleText")?.GetValue(info) is TMP_Text shipALabel)
                        layout.ShipALabels.Add(new KeyValuePair<TMP_Text, string>(shipALabel, role));
                    if (parts.FirstOrDefault(p => p.Name == "RoleToggle")?.GetValue(clone) is Toggle shipBToggle)
                    {
                        int slot = i + 4;
                        shipBToggle.onValueChanged.AddListener(on => Plugin.Log.LogInfo($"PvP: ship B {role} picked {(on ? "on" : "off")} (slot {slot})"));
                    }
                    // Each control is for one hand, by the side of the table it's on (the mouse uses the hand on
                    // that side); ship A's rows are on the other side, so either hand.
                    foreach (var interaction in copy.GetComponentsInChildren<HandInteraction>(true))
                        if (SideRestriction != null) SideRestriction.SetValue(interaction, Enum.Parse(SideRestriction.FieldType, "Either"));
                    copy.SetActive(false);
                    layout.ShipB.Add(copy);
                    wider.SetValue(clone, i + 4);
                }
                RoleInfos.SetValue(__instance, wider);
                layouts[__instance] = layout;
                Plugin.Log.LogInfo($"PvP: briefing room role list has ship B rows ({layout.ShipB.Count}, shown in a PvP room)");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("PvP: adding the briefing room's ship B rows: " + e.Message);
            }
        }

        private static string RoleName(int role) => ((FLCHMJJDKDL)role).ToString().ToUpperInvariant();

        // A row: the lowest object holding all of one role's parts and none of another role's.
        private static Transform RowOf(List<Component> components, Array infos, List<FieldInfo> parts, int index)
        {
            var mine = components.Where(c => c != null).Select(c => c.transform).ToList();
            if (mine.Count == 0) return null;
            var others = new List<Transform>();
            for (int i = 0; i < infos.Length; i++)
                if (i != index)
                    others.AddRange(parts.Select(f => f.GetValue(infos.GetValue(i)) as Component).Where(c => c != null).Select(c => c.transform));
            for (var candidate = mine[0]; candidate != null; candidate = candidate.parent)
            {
                if (!mine.All(t => t == candidate || t.IsChildOf(candidate))) continue;
                return others.Any(t => t == candidate || t.IsChildOf(candidate)) ? null : candidate;
            }
            return null;
        }

        private static Component Counterpart(Transform row, Transform copy, Component original)
        {
            var path = new List<int>();
            for (var t = original.transform; t != row; t = t.parent) path.Insert(0, t.GetSiblingIndex());
            var target = copy;
            foreach (int i in path) target = target.GetChild(i);
            return target.GetComponent(original.GetType());
        }

        // A slot with no row (rows not added): nothing to show, rather than the game's error every frame.
        private static bool ShowSlot_Prefix(ConsoleBriefingRoom __instance, int __0)
        {
            if (!(RoleInfos?.GetValue(__instance) is Array infos)) return true;
            var roleField = AccessTools.Field(infos.GetType().GetElementType(), "Role");
            foreach (var info in infos)
                if ((int)(FLCHMJJDKDL)roleField.GetValue(info) == __0) return true;
            return false;
        }

        /// <summary>Each frame: ship B's rows shown while the PvP test is picked; the room's size.</summary>
        internal static void Update()
        {
            if (layouts.Count > 0)
            {
                bool show = PvpPrototype.Active;
                foreach (var console in layouts.Keys.Where(c => c == null).ToList()) layouts.Remove(console);
                foreach (var entry in layouts)
                {
                    var layout = entry.Value;
                    if (show != layout.Shown)
                    {
                        foreach (var row in layout.ShipB) if (row != null) row.SetActive(show);
                        foreach (var label in layout.ShipALabels)
                            if (label.Key != null) label.Key.SetLocalizedText(show ? "SHIP A " + label.Value : label.Value);
                        if (layout.MissionPanel == null) layout.MissionPanel = MissionPanel(entry.Key);
                        if (!show)
                        {
                            foreach (var part in layout.MissionPanel) if (part != null) part.SetActive(true);
                            foreach (var graphic in layout.Cleared) if (graphic != null) graphic.enabled = true;
                            layout.Cleared.Clear();
                        }
                        layout.Shown = show;
                        layout.Settled = false;
                        if (show) TellHands(entry.Key);
                    }
                    if (!show) continue;
                    // The console switches some of these on itself (the lock icon, the experience warning).
                    foreach (var part in layout.MissionPanel) if (part != null && part.activeSelf) part.SetActive(false);
                    // The role and mission page (after a mission the console shows the results first): each time it
                    // appears, a frame later (the rows laid out), clear what's under ship B's rows.
                    var page = SelectionPage(entry.Key);
                    bool pageShown = page == null || page.activeInHierarchy;
                    if (pageShown && !layout.PageWasShown) layout.Settled = false;
                    layout.PageWasShown = pageShown;
                    if (pageShown && !layout.Settled && Time.frameCount % 2 == 0)
                    {
                        layout.Settled = true;
                        Settle(entry.Key, layout);
                        layout.DiagnoseAt = Time.unscaledTime + 3f;
                    }
                    if (layout.DiagnoseAt > 0f && Time.unscaledTime >= layout.DiagnoseAt)
                    {
                        layout.DiagnoseAt = -1f;
                        Diagnose(entry.Key, layout);
                    }
                }
            }
            RoomSize();
        }

        private static readonly MethodInfo GetText = AccessTools.Method(typeof(TMP_Text), "get_text");

        // Picking a role on the other table's ship, in a PvP room's briefing room: the game's own station swap
        // (a fade, then the host seats you; PvpConsoles.BriefingSeat picks the chair at that table).
        private static void RolePicked_Postfix(int __0, bool __1)
        {
            if (!__1 || !InBriefing || !PvpLobby.InPvpRoom) return;
            try
            {
                // The console also sets these toggles itself to show who holds a slot: only a slot that's free
                // (asked for just now) or this player's (the host assigns its own at once) is a pick.
                var roles = Roles;
                var slots = roles == null ? null : Assigned(roles);
                var holder = slots != null && __0 < slots.Length ? slots[__0] : null;
                if (holder != null && holder != NDIOGIGMPBH.DIMFCNEMMPD) return;
                var me = AccessTools.Method(typeof(NovaPlayerManager), "get_LocalPlayer")?.Invoke(NovaPlayerManager.IEFBKOMBFGO, null) as NovaPlayerInfo;
                var seat = me?.Station as NovaStation;
                if (seat == null || PvpConsoles.IsCopy(seat) == (__0 >= 4)) return;
                var spawn = AccessTools.Method(AccessTools.TypeByName("NovaSpawnManager"), "get_Instance")?.Invoke(null, null);
                var swap = spawn == null ? null : AccessTools.Method(spawn.GetType(), "SwapStations");
                if (swap == null) return;
                swap.Invoke(spawn, new object[] { (FLCHMJJDKDL)(__0 % 4), false });
                Plugin.Log.LogInfo($"PvP: moving to table {(__0 >= 4 ? "B" : "A")} for slot {__0}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("PvP: moving to the other table: " + e.Message);
            }
        }

        private static GameObject SelectionPage(ConsoleBriefingRoom console) =>
            AccessTools.Field(typeof(ConsoleBriefingRoom), "_selectionPanel")?.GetValue(console) as GameObject;

        // The console renames roles in its refresh (by ship: the Next Generation's names), so ship A's labels go back
        // on after it.
        private static void BriefingRefresh_Postfix(ConsoleBriefingRoom __instance)
        {
            if (!layouts.TryGetValue(__instance, out var layout) || !layout.Shown) return;
            foreach (var label in layout.ShipALabels)
                if (label.Key != null && GetText?.Invoke(label.Key, null) as string != "SHIP A " + label.Value)
                    label.Key.SetLocalizedText("SHIP A " + label.Value);
        }
        private static readonly FieldInfo SideRestriction = AccessTools.Field(typeof(HandInteraction), "_sideRestriction");

        // ---- Diagnostics: ship B's rows and the mouse ----------------------------------------------

        private static bool InPvpBriefing => layouts.Count > 0 && layouts.Values.Any(l => l.Shown);

        private static string Describe(HandInteraction interaction) =>
            interaction == null ? "nothing" : $"{interaction.transform.parent?.name}/{interaction.name}{(layouts.Values.Any(l => l.ShipB.Any(r => r != null && interaction.transform.IsChildOf(r.transform))) ? " (ship B)" : "")}";

        private static readonly HashSet<string> handErrors = new HashSet<string>();

        private static Exception HandUpdate_Finalizer(Exception __exception, object __instance)
        {
            if (__exception != null && InPvpBriefing && handErrors.Count < 6
                && handErrors.Add(__instance?.GetType().Name + __exception.GetType().Name + __exception.StackTrace))
                Plugin.Log.LogWarning($"PvP: [diag] {__instance?.GetType().Name} ({(__instance as Component)?.name}) update failed: {__exception}");
            return __exception;
        }

        private static void PointerEnter_Prefix(HandInteraction __0)
        {
            if (InPvpBriefing) Plugin.Log.LogInfo("PvP: [diag] mouse over " + Describe(__0));
        }

        private static void PointerDown_Prefix(HandInteraction __0)
        {
            if (InPvpBriefing) Plugin.Log.LogInfo("PvP: [diag] mouse pressed " + Describe(__0));
        }

        // Each ship B role control next to its ship A original: enabled, interactable, the mouse's collider, and
        // whether the local player's hands list it as one they can use.
        private static void Diagnose(ConsoleBriefingRoom console, Layout layout)
        {
            try
            {
                var manager = AccessTools.Method(AccessTools.TypeByName("HandAnimatorManager"), "get_Instance")?.Invoke(null, null);
                var animators = (AccessTools.Field(manager?.GetType(), "AFBJOJJNCEA")?.GetValue(manager) as IDictionary)?.Keys.Cast<object>().ToList() ?? new List<object>();
                var gathered = AccessTools.Field(AccessTools.TypeByName("HandAnimator"), "DPAFLMHJMEC");
                var available = AccessTools.Method(AccessTools.TypeByName("HandAnimator"), "IsInteractionAvailable");
                var animatorConsole = AccessTools.Field(AccessTools.TypeByName("HandAnimator"), "KHDENOMKIJE");
                Plugin.Log.LogInfo($"PvP: [diag] console {console.name}: {animators.Count} hand animators, on consoles "
                    + string.Join(", ", animators.Select(a => (animatorConsole?.GetValue(a) as Component)?.name ?? "none").ToArray()));
                if (!(RoleInfos?.GetValue(console) is Array infos)) return;
                var toggleField = AccessTools.Field(infos.GetType().GetElementType(), "RoleToggle");
                for (int i = 0; i < infos.Length; i++)
                {
                    var toggle = toggleField?.GetValue(infos.GetValue(i)) as Toggle;
                    if (toggle == null || (i % 4 != 1 && i < 4)) continue; // ship A's helm, for comparison, and all of ship B's
                    foreach (var interaction in toggle.GetComponentsInChildren<HandInteraction>(true).Concat(toggle.GetComponentsInParent<HandInteraction>(true)).Distinct())
                    {
                        var mouse = AccessTools.Field(typeof(HandInteraction), "JJGDKFLEHLH")?.GetValue(interaction) as Collider;
                        var lists = animators.Select(a =>
                            ((gathered?.GetValue(a) as IList)?.Contains(interaction) == true ? "gathered" : "not gathered")
                            + (available?.Invoke(a, new object[] { interaction }) is true ? "+available" : "")).ToArray();
                        Plugin.Log.LogInfo($"PvP: [diag] slot {i} {Describe(interaction)}: enabled {interaction.enabled}, interactable {interaction.Interactable}, hand {SideRestriction?.GetValue(interaction)}, "
                            + $"active {interaction.gameObject.activeInHierarchy}, console {(AccessTools.Field(typeof(HandInteraction), "KHDENOMKIJE")?.GetValue(interaction) as Component)?.name ?? "none"}, "
                            + $"mouse collider {(mouse == null ? "none" : $"{(mouse.enabled ? "on" : "off")} at {mouse.bounds.center} size {mouse.bounds.size}")}, "
                            + $"hands: {string.Join(" / ", lists)}");
                        var grid = AccessTools.Field(typeof(HandInteraction), "_grid")?.GetValue(interaction) as Component;
                        var poses = AccessTools.Field(typeof(HandInteraction), "_poses")?.GetValue(interaction) as Array;
                        var poseText = poses == null ? "none" : string.Join("; ", poses.Cast<object>().Select(pose =>
                        {
                            var t = Traverse.Create(pose);
                            var clips = t.Field("Animations").GetValue<AnimationClip[]>();
                            var turns = t.Field("LocalWristRotations").GetValue<Quaternion[]>();
                            var spots = t.Field("LocalWristPositions").GetValue<Vector3[]>();
                            return $"L {clips?[0]?.name ?? "-"} {spots?[0]} {turns?[0]} | R {clips?[1]?.name ?? "-"} {spots?[1]} {turns?[1]}";
                        }).ToArray());
                        Plugin.Log.LogInfo($"PvP: [diag]   grid {(grid == null ? "none" : grid.name + " on " + grid.transform.parent?.name)}, {poses?.Length ?? 0} poses: {poseText}");
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("PvP: [diag] ship B rows: " + e.Message);
            }
        }

        // Ship B's rows sit where the mission panel was, but in the role list's part of the console. With a mouse,
        // a console only takes clicks for the area the view has zoomed to (CameraFocusArea, which knows the
        // controls in its own children), so they move into the mission panel's area (where they're drawn). Then
        // the panel's leftovers under them go: small pictures and lines (the panel's frame, the arrows' boxes),
        // never a control, a row, or anything bigger than a row.
        private static void Settle(ConsoleBriefingRoom console, Layout layout)
        {
            try
            {
                var rows = layout.ShipB.Where(r => r != null).ToList();
                if (rows.Count == 0) return;
                var picture = AccessTools.Field(typeof(ConsoleBriefingRoom), "_missionPreviewImage")?.GetValue(console) as Component;
                var area = picture != null ? picture.GetComponentInParent<CameraFocusArea>() : null;
                var rowArea = rows[0].transform.parent.GetComponentInParent<CameraFocusArea>();
                Plugin.Log.LogInfo($"PvP: ship B rows in focus area {rowArea?.name ?? "none"}, mission panel's {area?.name ?? "none"}");
                if (area != null && area != rowArea && area.GetComponentInParent<Canvas>() == rows[0].GetComponentInParent<Canvas>())
                {
                    foreach (var row in rows) row.transform.SetParent(area.transform, true);
                    TellHands(console);
                    Plugin.Log.LogInfo($"PvP: ship B rows moved into focus area {area.name}");
                }

                var canvas = rows[0].GetComponentInParent<Canvas>()?.transform;
                if (canvas == null) return;
                var rects = rows.Select(r => Bounds(canvas, r)).ToList();
                var mine = new HashSet<Transform>(rows.SelectMany(r => r.GetComponentsInChildren<Transform>(true)));
                // Kept clear: the game type and ship boxes (around their toggles) and ship A's rows.
                var kept = new[] { "_proceduralMissionToggle", "_campaignMissionToggle", "_aegisToggle", "_enterpriseToggle", "_tngToggle" }
                    .Select(f => AccessTools.Field(typeof(ConsoleBriefingRoom), f)?.GetValue(console) as Component)
                    .Where(c => c != null).Select(c => Bounds(canvas, c.gameObject)).ToList();
                foreach (var label in layout.ShipALabels)
                    if (label.Key != null) kept.Add(Bounds(canvas, label.Key.rectTransform));
                var cleared = new List<string>();
                // Not the results (after a mission) or launch screens.
                var screens = new[] { "_missionRecapPanel", "_gameLaunchPanel" }
                    .Select(f => AccessTools.Field(typeof(ConsoleBriefingRoom), f)?.GetValue(console) as GameObject)
                    .Where(g => g != null).Select(g => g.transform).ToList();
                foreach (var graphic in console.GetComponentsInChildren<Graphic>(false))
                {
                    if (screens.Any(t => graphic.transform.IsChildOf(t))) continue;
                    if (!graphic.enabled || mine.Contains(graphic.transform) || graphic.GetComponentInParent<Selectable>() != null) continue;
                    if (rows.Any(r => r.transform.IsChildOf(graphic.transform))) continue;
                    var rect = Bounds(canvas, graphic.rectTransform);
                    if (kept.Any(k => k.Overlaps(rect))) continue;
                    for (int i = 0; i < rects.Count; i++)
                    {
                        if (!rect.Overlaps(rects[i]) || rect.width * rect.height > rects[i].width * rects[i].height) continue;
                        graphic.enabled = false;
                        layout.Cleared.Add(graphic);
                        cleared.Add(graphic.name);
                        break;
                    }
                }
                Plugin.Log.LogInfo($"PvP: under ship B's rows, {cleared.Count} hidden: {string.Join(", ", cleared.Distinct().ToArray())}");
                foreach (var row in rows)
                {
                    var toggle = row.GetComponentInChildren<Toggle>(true);
                    Plugin.Log.LogInfo($"PvP: {row.name}: toggle {(toggle == null ? "none" : $"interactable {toggle.interactable}, on {toggle.isOn}")}, "
                        + $"hand interactions {row.GetComponentsInChildren<HandInteraction>(true).Length}, at {Bounds(canvas, row)}");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("PvP: laying out ship B's rows: " + e.Message);
            }
        }

        // An object's extent on the canvas (all its rect transforms), in the canvas's own units.
        private static Rect Bounds(Transform canvas, GameObject row)
        {
            var parts = row.GetComponentsInChildren<RectTransform>(true).Select(t => Bounds(canvas, t)).Where(r => r.width > 0f && r.height > 0f).ToList();
            if (parts.Count == 0) return new Rect();
            float xMin = parts.Min(r => r.xMin), yMin = parts.Min(r => r.yMin), xMax = parts.Max(r => r.xMax), yMax = parts.Max(r => r.yMax);
            return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }

        private static readonly Vector3[] corners = new Vector3[4];

        private static Rect Bounds(Transform canvas, RectTransform t)
        {
            t.GetWorldCorners(corners);
            float xMin = float.MaxValue, yMin = float.MaxValue, xMax = float.MinValue, yMax = float.MinValue;
            foreach (var corner in corners)
            {
                var p = canvas.InverseTransformPoint(corner);
                xMin = Mathf.Min(xMin, p.x); yMin = Mathf.Min(yMin, p.y); xMax = Mathf.Max(xMax, p.x); yMax = Mathf.Max(yMax, p.y);
            }
            return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }

        // The mission panel (name, picture, description, next/previous): what holds all of them, unless that holds
        // anything else the console shows (roles, game type, ship, launch), else each part on its own.
        private static List<GameObject> MissionPanel(ConsoleBriefingRoom console)
        {
            Component Part(string name) => AccessTools.Field(typeof(ConsoleBriefingRoom), name)?.GetValue(console) as Component;
            var pieces = new[] { "_missionName", "_missionDescriptionText", "_missionPreviewImage", "_nextMissionButton", "_previousMissionButton" }
                .Select(Part).Where(c => c != null).Select(c => c.transform).ToList();
            var extras = new[] { "_lockIcon", "_experienceWarningText" }.Select(Part).Where(c => c != null).Select(c => c.gameObject);
            var keep = new[] { "_proceduralMissionToggle", "_campaignMissionToggle", "_aegisToggle", "_tngToggle", "_launchGameButton", "_hostNameText" }
                .Select(Part).Where(c => c != null).Select(c => c.transform).ToList();
            if (RoleInfos?.GetValue(console) is Array infos)
                foreach (var info in infos)
                    if (info != null)
                        keep.AddRange(AccessTools.GetDeclaredFields(info.GetType()).Select(f => f.GetValue(info) as Component).Where(c => c != null).Select(c => c.transform));
            if (pieces.Count == 0) return extras.ToList();
            for (var holder = pieces[0]; holder != null && holder != console.transform; holder = holder.parent)
            {
                if (!pieces.All(t => t == holder || t.IsChildOf(holder))) continue;
                if (keep.Any(t => t == holder || t.IsChildOf(holder))) break;
                Plugin.Log.LogInfo($"PvP: briefing room mission panel ({holder.name}) hidden in a PvP room");
                return new[] { holder.gameObject }.Concat(extras).ToList();
            }
            Plugin.Log.LogInfo("PvP: briefing room mission panel's parts hidden in a PvP room");
            return pieces.Select(t => t.gameObject).Concat(extras).ToList();
        }

        // The VR hands reach only the controls they collected when the console loaded.
        internal static void TellHands(ConsoleBase console)
        {
            try
            {
                foreach (var area in console.GetComponentsInChildren<CameraFocusArea>(true)) area.RecacheChildHandInteractions();
                var manager = AccessTools.Method(AccessTools.TypeByName("HandAnimatorManager"), "get_Instance")?.Invoke(null, null);
                if (manager != null) AccessTools.Method(manager.GetType(), "NotifyAnimatorsForConsole")?.Invoke(manager, new object[] { console });
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("PvP: telling the hands about the ship B rows: " + e.Message);
            }
        }

        // ---- Launch -------------------------------------------------------------------------------

        // The game sets each player's role to their slot number: ship B's slots become their role, and are
        // left out of the game's loop while it runs (then put back: the slot says the ship).
        private static void Launch_Prefix(out CBBCCGMALMP[] __state)
        {
            __state = null;
            var roles = Roles;
            if (roles == null) return;
            var slots = Widen(roles);
            __state = (CBBCCGMALMP[])slots.Clone();
            for (int i = 4; i < slots.Length; i++)
            {
                if (slots[i] == null) continue;
                var info = NovaPlayerManager.IEFBKOMBFGO?.GetInfoForPlayer(slots[i]);
                if (info != null && PlayerRole != null) PlayerRole.SetValue(info, (FLCHMJJDKDL)(i - 4), null);
                Plugin.Log.LogInfo($"PvP: {info?.FAJABLKBGHN ?? "a player"} launches as ship B's {RoleName(i - 4)}");
                slots[i] = null;
            }
        }

        private static Exception Launch_Finalizer(CBBCCGMALMP[] __state, Exception __exception)
        {
            var roles = Roles;
            if (__state != null && roles != null)
            {
                var slots = Widen(roles);
                for (int i = 4; i < slots.Length && i < __state.Length; i++) slots[i] = __state[i];
            }
            return __exception;
        }

        // ---- Seating ------------------------------------------------------------------------------

        private static FieldInfo seatPlayerField;
        private static string seatingShip; // the ship of the player the game is seating, while it does

        private static bool InBriefing => GamePhaseManager.IEFBKOMBFGO != null && GamePhaseManager.IEFBKOMBFGO.GetCurrentGamePhase() == typeof(GamePhase_BriefingRoom);

        private static void Seating_Prefix(object __instance, out string __state)
        {
            __state = seatingShip;
            seatingShip = null;
            if (!PvpPrototype.Active || seatPlayerField == null) return;
            if (seatPlayerField.GetValue(__instance) is int actor) seatingShip = PvpShips.Of(PvpShips.Player(actor));
        }

        private static Exception Seating_Finalizer(string __state, Exception __exception)
        {
            seatingShip = __state;
            return __exception;
        }

        // "The station for this role" is ship A's: for a ship B player, ship B's copy of it.
        private static void StationIndex_Postfix(ref int __result)
        {
            if (seatingShip != "B" || __result < 0) return;
            var stations = PvpConsoles.Stations;
            if (stations == null || __result >= stations.Count || !(stations[__result] is NovaStation station) || PvpConsoles.IsCopy(station)) return;
            var copy = PvpConsoles.TwinOf(station);
            int index = copy == null ? -1 : stations.IndexOf(copy);
            if (index >= 0) __result = index;
        }

        // A free crew station on the player's own ship.
        private static void FreeStation_Postfix(ref int __result)
        {
            if (seatingShip == null) return;
            var stations = PvpConsoles.Stations;
            if (stations == null || Pawn == null) return;
            bool onB = seatingShip == "B";
            for (int i = 0; i < stations.Count; i++)
            {
                if (!(stations[i] is NovaStation station) || PvpConsoles.IsCopy(station) != onB) continue;
                if (station.OGBAPMIPPID == FLCHMJJDKDL.Captain && !InBriefing) continue; // the briefing room's tables seat four each
                if (Pawn.GetValue(station) as UnityEngine.Object) continue;
                __result = i;
                return;
            }
            __result = -1;
        }

        // ---- Room size ----------------------------------------------------------------------------

        private static float nextRoomCheck;
        private static bool widened;
        private const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;

        // The host opens the room to eight once four are in with the PvP test picked, and back to four after.
        private static void RoomSize()
        {
            if (Time.unscaledTime < nextRoomCheck) return;
            nextRoomCheck = Time.unscaledTime + 1f;
            var photon = Patches.PhotonNetworkType;
            var room = photon?.GetMethod("get_room", Static)?.Invoke(null, null);
            var manager = NovaPlayerManager.IEFBKOMBFGO;
            if (room == null || manager == null)
            {
                widened = false;
                // Matchmaking for PvP (no room yet) looks for eight; back to four for anything else.
                if (manager != null && manager.MaxNumPlayers == Slots && !PvpLobby.Picked) manager.MaxNumPlayers = 4;
                return;
            }
            int players = Convert.ToInt32(room.GetType().GetMethod("get_playerCount")?.Invoke(room, null) ?? 0);
            bool want = PvpLobby.InPvpRoom || (PvpPrototype.Active && (players >= 4 || widened));
            if (!PvpPrototype.Active && players > 4) want = true; // can't shrink below who's in
            manager.MaxNumPlayers = want ? Slots : 4;
            if (!(photon.GetMethod("get_isMasterClient", Static)?.Invoke(null, null) is bool master) || !master) return;
            int max = Convert.ToInt32(room.GetType().GetMethod("get_maxPlayers")?.Invoke(room, null) ?? 4);
            int size = want ? Slots : 4;
            if (max == size) { widened = want; return; }
            var setter = room.GetType().GetMethod("set_maxPlayers");
            if (setter == null) return;
            setter.Invoke(room, new object[] { setter.GetParameters()[0].ParameterType == typeof(byte) ? (object)(byte)size : size });
            widened = want;
            Plugin.Log.LogInfo($"PvP: room opened to {size} players");
        }
    }
}
