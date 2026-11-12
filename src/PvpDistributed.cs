using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using DSStation = FLCHMJJDKDL;
using DSTask = DistributedSystems.NEOBBFFDDAI;
using DSTaskType = DistributedSystems.ANOJNPAFLCB;

namespace StbcRevival
{
    /// <summary>
    /// PvP: each ship its own transporter and hacking. The game has one of each for the room (DistributedSystems,
    /// on the bridge): one lock (who's using the transporter, who's hacking: by station role, so both ships'
    /// tactical officers took and freed each other's) and one task list (hacks and cooldowns: a ship's panel picked
    /// up the other ship's hack, and cancelled it when its target was out of its own range). Here:
    ///   - Ship A's lock is the game's own (its fields, synced as ever); ship B's is kept here, on the host, and
    ///     shared as the room property "PVL". The lock's getters answer for the ship asking.
    ///   - A task belongs to the ship of whoever started it (the host's record; elsewhere its player's ship);
    ///     lookups find only the asking ship's, and a drop drops only the dropping ship's.
    ///   - The ship asking: the console set of the panel running (TargetingPanelUI, TransporterUI: either set's,
    ///     as PvpConsoles.SetOf tells), else PvpRouting's context (the AI crew under PvpScope), else yours.
    ///     A request from the other set's panel (its hidden twin on your bridge) isn't sent from a crew member's game.
    /// </summary>
    internal static class PvpDistributed
    {
        private const string Key = "PVL";
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        [ThreadStatic] private static string asking;   // the panel running's console set
        [ThreadStatic] private static string forced;   // the host acting for a ship itself (its AI): that ship
        private static readonly DSStation None = NoStation();

        private static DSStation NoStation()
        {
            try { return (DSStation)Enum.Parse(typeof(DSStation), "COUNT"); }
            catch { return (DSStation)7; } // Station.COUNT: no station
        }

        // Ship B's lock (the host's; others read it from the room).
        private static int bTransporter = -1, bHacking = -1;
        private static DSStation bTransporterStation = None, bHackingStation = None;
        private static string published, readFrame;
        private static int readAt = -1;

        private static readonly Dictionary<DSTask, string> shipOfTask = new Dictionary<DSTask, string>();
        private static FieldInfo tasksField;
        private static MethodInfo transporterRpc, hackingRpc;
        private static readonly HashSet<string> told = new HashSet<string>();

        internal static void Patch(Harmony harmony)
        {
            var ds = typeof(DistributedSystems);
            tasksField = AccessTools.Field(ds, "IAEABKMLNJH"); // _tasks
            transporterRpc = AccessTools.Method(ds, "RequestToUseTransporterRPC");
            hackingRpc = AccessTools.Method(ds, "RequestToUseHackingRPC");
            int patched = 0, missing = 0;
            void P(MethodBase m, string prefix, string finalizer = null, string postfix = null)
            {
                if (m == null) { missing++; return; }
                harmony.Patch(m, prefix: prefix == null ? null : new HarmonyMethod(typeof(PvpDistributed), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(PvpDistributed), postfix),
                    finalizer: finalizer == null ? null : new HarmonyMethod(typeof(PvpDistributed), finalizer));
                patched++;
            }
            P(AccessTools.Method(ds, "get_PlayerUsingTransporter"), nameof(PlayerT_Prefix));
            P(AccessTools.Method(ds, "get_StationUsingTransporter"), nameof(StationT_Prefix));
            P(AccessTools.Method(ds, "get_PlayerUsingHacking"), nameof(PlayerH_Prefix));
            P(AccessTools.Method(ds, "get_StationUsingHacking"), nameof(StationH_Prefix));
            P(transporterRpc, nameof(TransporterRpc_Prefix), nameof(Asking_Finalizer));
            P(hackingRpc, nameof(HackingRpc_Prefix), nameof(Asking_Finalizer));
            foreach (var send in new[] { "RequestToUseTransporter", "RequestToUseHacking", "BeginDSTask", "DropDSTask" })
                P(AccessTools.Method(ds, send), nameof(Send_Prefix), nameof(Forced_Finalizer));
            P(AccessTools.Method(ds, "GetTaskInfoForTask"), nameof(TaskForTask_Prefix));
            P(AccessTools.Method(ds, "GetTaskInfoForTarget"), nameof(TaskForTarget_Prefix));
            P(AccessTools.Method(ds, "CreateTaskInfoForTarget"), nameof(Create_Prefix), nameof(Create_Finalizer), nameof(Create_Postfix));
            P(AccessTools.Method(ds, "DropDSTaskRPC"), nameof(Drop_Prefix), nameof(Drop_Finalizer));
            // The panels: every method runs as its console set's.
            int panels = 0;
            foreach (var name in new[] { "TargetingPanelUI", "TransporterUI" })
            {
                var type = AccessTools.TypeByName(name);
                if (type == null) { missing++; continue; }
                foreach (var m in type.GetMethods(All | BindingFlags.DeclaredOnly))
                {
                    if (m.IsStatic || m.IsAbstract || m.IsGenericMethodDefinition || m.GetMethodBody() == null) continue;
                    try
                    {
                        harmony.Patch(m, prefix: new HarmonyMethod(typeof(PvpDistributed), nameof(Panel_Prefix)),
                            finalizer: new HarmonyMethod(typeof(PvpDistributed), nameof(Panel_Finalizer)));
                        panels++;
                    }
                    catch { } // a method Harmony can't patch: it runs as whoever's asking already
                }
            }
            if (missing > 0 || tasksField == null) Plugin.Log.LogWarning($"PvP: ship B's transporter and hacking: {missing} parts not found; both ships may share them");
            Plugin.Log.LogInfo($"PvP: each ship its own transporter and hacking ({patched} hooks, {panels} panel methods)");
        }

        private static bool On => PvpLobby.InPvpRoom && PvpPrototype.Active && PvpPrototype.Bound != null;
        private static bool IsMaster => Patches.PhotonNetworkType?.GetMethod("get_isMasterClient", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) is true;
        private static string Mine => PvpShips.Mine ?? "A";

        /// <summary>The ship whose transporter and hacking the running code means.</summary>
        private static string Asking => forced ?? asking ?? PvpRouting.Context ?? Mine;

        // ---- The panels ---------------------------------------------------------------------------

        private static readonly Dictionary<Component, string> setOfPanel = new Dictionary<Component, string>();
        private static float nextPanelClear;

        private static void Panel_Prefix(Component __instance, out string __state)
        {
            __state = asking;
            if (!On || __instance == null) return;
            if (Time.unscaledTime > nextPanelClear) { nextPanelClear = Time.unscaledTime + 5f; setOfPanel.Clear(); }
            if (!setOfPanel.TryGetValue(__instance, out var set)) setOfPanel[__instance] = set = PvpConsoles.SetOf(__instance);
            if (set != null) asking = set;
        }

        private static Exception Panel_Finalizer(string __state, Exception __exception)
        {
            asking = __state;
            return __exception;
        }

        // ---- The lock -----------------------------------------------------------------------------

        private static bool AskingB => On && Asking == "B";

        private static bool PlayerT_Prefix(ref CBBCCGMALMP __result)
        {
            if (!AskingB) return true;
            ReadShared();
            __result = PvpShips.Player(bTransporter) as CBBCCGMALMP;
            return false;
        }

        private static bool StationT_Prefix(ref DSStation __result)
        {
            if (!AskingB) return true;
            ReadShared();
            __result = bTransporterStation;
            return false;
        }

        private static bool PlayerH_Prefix(ref CBBCCGMALMP __result)
        {
            if (!AskingB) return true;
            ReadShared();
            __result = PvpShips.Player(bHacking) as CBBCCGMALMP;
            return false;
        }

        private static bool StationH_Prefix(ref DSStation __result)
        {
            if (!AskingB) return true;
            ReadShared();
            __result = bHackingStation;
            return false;
        }

        // The host taking a request: ship A's by the game (its own fields, read as ship A's), ship B's here.
        private static bool TransporterRpc_Prefix(CBBCCGMALMP __0, bool __1, DSStation __2, out string __state) => Request(__0, __1, __2, true, out __state);

        private static bool HackingRpc_Prefix(CBBCCGMALMP __0, bool __1, DSStation __2, out string __state) => Request(__0, __1, __2, false, out __state);

        private static bool Request(CBBCCGMALMP player, bool use, DSStation station, bool transporter, out string state)
        {
            state = asking;
            if (!On || !IsMaster) return true;
            string ship = forced ?? PvpShips.Of(player) ?? "A";
            if (ship == "A") { asking = "A"; return true; }
            int actor = PvpShips.Actor(player);
            if (transporter)
            {
                if (!use && station == bTransporterStation) { bTransporter = -1; bTransporterStation = None; }
                if (use && bHackingStation != station) { bTransporter = actor; bTransporterStation = station; }
            }
            else
            {
                if (!use && station == bHackingStation) { bHacking = -1; bHackingStation = None; }
                if (use && bTransporterStation != station) { bHacking = actor; bHackingStation = station; }
            }
            Publish();
            if (told.Add("lock" + transporter)) Plugin.Log.LogInfo($"PvP: ship B's {(transporter ? "transporter" : "hacking")} kept apart from ship A's");
            return false;
        }

        private static Exception Asking_Finalizer(string __state, Exception __exception)
        {
            asking = __state;
            return __exception;
        }

        // Sending a request (or a task): from a crew member's game only for their own ship (the other set's panel
        // is a hidden twin); on the host, for the asking ship (its AI's), carried through to the handlers.
        private static bool Send_Prefix(out string __state)
        {
            __state = forced;
            if (!On) return true;
            string ship = Asking;
            if (!IsMaster) return ship == Mine;
            forced = ship;
            return true;
        }

        private static Exception Forced_Finalizer(string __state, Exception __exception)
        {
            forced = __state;
            return __exception;
        }

        /// <summary>Each frame (the host): ship B's lock published, and either lock freed if its holder left that ship.</summary>
        internal static void Update()
        {
            if (!On || !IsMaster) return;
            if (bTransporter >= 0 && PvpShips.Of(PvpShips.Player(bTransporter)) != "B") { bTransporter = -1; bTransporterStation = None; }
            if (bHacking >= 0 && PvpShips.Of(PvpShips.Player(bHacking)) != "B") { bHacking = -1; bHackingStation = None; }
            Publish();
        }

        private static void Publish()
        {
            string state = $"{bTransporter},{(int)bTransporterStation},{bHacking},{(int)bHackingStation}";
            if (state == published) return;
            var room = Patches.PhotonNetworkType?.GetMethod("get_room", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
            if (room != null && PhotonProps.Set(room, Key, state)) published = state;
        }

        // A crew member's game: ship B's lock from the room (once a frame).
        private static void ReadShared()
        {
            if (IsMaster || readAt == Time.frameCount) return;
            readAt = Time.frameCount;
            var room = Patches.PhotonNetworkType?.GetMethod("get_room", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
            var props = room?.GetType().GetMethod("get_customProperties")?.Invoke(room, null) as IDictionary;
            var value = props != null && props.Contains(Key) ? props[Key] as string : null;
            if (value == null || value == readFrame) return;
            readFrame = value;
            var parts = value.Split(',');
            if (parts.Length != 4) return;
            int.TryParse(parts[0], out bTransporter);
            bTransporterStation = int.TryParse(parts[1], out var ts) ? (DSStation)ts : None;
            int.TryParse(parts[2], out bHacking);
            bHackingStation = int.TryParse(parts[3], out var hs) ? (DSStation)hs : None;
        }

        /// <summary>Leaving the bridge: ship B's lock and the task record let go.</summary>
        internal static void Forget()
        {
            bTransporter = bHacking = -1;
            bTransporterStation = bHackingStation = None;
            shipOfTask.Clear();
            setOfPanel.Clear();
            published = readFrame = null;
        }

        // ---- The tasks ----------------------------------------------------------------------------

        private static IList Tasks(object ds) => tasksField?.GetValue(ds) as IList;

        private static string ShipOfTask(DSTask task)
        {
            if (task == null) return null;
            if (shipOfTask.TryGetValue(task, out var ship)) return ship;
            return PvpShips.Of(task.Player) ?? "A";
        }

        private static bool TaskForTask_Prefix(object __instance, DSTaskType __0, ref DSTask __result)
        {
            if (!On || Tasks(__instance) == null) return true;
            string ship = Asking;
            __result = Tasks(__instance).Cast<DSTask>().FirstOrDefault(t => t != null && t.Task == __0 && ShipOfTask(t) == ship);
            return false;
        }

        private static bool TaskForTarget_Prefix(object __instance, object __0, DSTaskType __1, ref DSTask __result)
        {
            if (!On || Tasks(__instance) == null) return true;
            string ship = Asking;
            __result = Tasks(__instance).Cast<DSTask>().FirstOrDefault(t => t != null && ReferenceEquals(t.Target, __0) && t.Task == __1 && ShipOfTask(t) == ship);
            return false;
        }

        // A task created: the ship it's for (the host's AI acting for its ship, else the sender's), recorded.
        private static void Create_Prefix(CBBCCGMALMP __3, out string __state)
        {
            __state = asking;
            if (!On) return;
            asking = forced ?? (PvpBridgeViews.RpcSender != null ? PvpShips.Of(PvpBridgeViews.RpcSender) : null) ?? PvpShips.Of(__3) ?? Mine;
        }

        private static void Create_Postfix(object __instance)
        {
            if (!On) return;
            var tasks = Tasks(__instance);
            if (tasks != null && tasks.Count > 0 && tasks[tasks.Count - 1] is DSTask task) shipOfTask[task] = asking ?? Mine;
        }

        private static Exception Create_Finalizer(string __state, Exception __exception)
        {
            asking = __state;
            return __exception;
        }

        // A task dropped: only from the dropping ship's (the others are set aside while the game's own drop runs).
        private static void Drop_Prefix(object __instance, out List<object> __state)
        {
            __state = null;
            if (!On) return;
            var tasks = Tasks(__instance);
            if (tasks == null) return;
            string ship = forced ?? (PvpBridgeViews.RpcSender != null ? PvpShips.Of(PvpBridgeViews.RpcSender) : null) ?? Asking;
            __state = new List<object>();
            for (int i = tasks.Count - 1; i >= 0; i--)
                if (tasks[i] is DSTask t && ShipOfTask(t) != ship) { __state.Add(t); tasks.RemoveAt(i); }
        }

        private static Exception Drop_Finalizer(object __instance, List<object> __state, Exception __exception)
        {
            if (__state != null && __state.Count > 0)
            {
                var tasks = Tasks(__instance);
                if (tasks != null) for (int i = __state.Count - 1; i >= 0; i--) tasks.Add(__state[i]);
            }
            return __exception;
        }
    }
}
