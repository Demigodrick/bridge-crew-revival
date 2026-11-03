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
            Hook(briefing, "BJBJFCFIDOA", prefix: nameof(ShowSlot_Prefix));   // shows a slot's player on its row
            Hook(briefing, "GLFEOIFJEHE", prefix: nameof(Launch_Prefix), finalizer: nameof(Launch_Finalizer));
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

        private static readonly List<GameObject> shipBRows = new List<GameObject>();
        private static ConsoleBriefingRoom rowsFor;
        private static bool rowsShown;

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
                shipBRows.Clear();
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
                    copy.transform.localPosition = row.localPosition + new Vector3(width * 1.05f, 0f, 0f);
                    var clone = Activator.CreateInstance(infoType);
                    roleField.SetValue(clone, (FLCHMJJDKDL)((int)(FLCHMJJDKDL)roleField.GetValue(info) + 4));
                    foreach (var part in parts)
                        if (part.GetValue(info) is Component original && original != null)
                            part.SetValue(clone, Counterpart(row, copy.transform, original));
                    foreach (var text in copy.GetComponentsInChildren<TMP_Text>(true))
                        if (text == (parts.FirstOrDefault(p => p.Name == "RoleText")?.GetValue(clone) as Component))
                            text.SetLocalizedText("SHIP B " + RoleName((int)(FLCHMJJDKDL)roleField.GetValue(info)));
                    copy.SetActive(false);
                    shipBRows.Add(copy);
                    wider.SetValue(clone, i + 4);
                }
                RoleInfos.SetValue(__instance, wider);
                rowsFor = __instance;
                rowsShown = false;
                Plugin.Log.LogInfo($"PvP: briefing room role list has ship B rows ({shipBRows.Count}, shown with the PvP test)");
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
            bool show = PvpPrototype.Active && rowsFor != null;
            if (show != rowsShown && shipBRows.Count > 0)
            {
                foreach (var row in shipBRows) if (row != null) row.SetActive(show);
                rowsShown = show;
                if (show) TellHands(rowsFor);
            }
            RoomSize();
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
                if (station.OGBAPMIPPID == FLCHMJJDKDL.Captain) continue;
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
