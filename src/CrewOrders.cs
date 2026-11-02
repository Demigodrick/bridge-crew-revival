using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// Voice orders to the AI crew from anyone on the bridge (issue #10), and from a captain who isn't the host.
    ///  - Who: the captain always; with [Voice] AnyStation, players at the other stations too. The game's voice
    ///    button is read at every console (ConsoleBase.BatchUpdate), only the mod's listening was the captain's.
    ///    A non-captain's orders go to the other stations' AI crew: orders for your own station, the captain's
    ///    playbook (offense, defense, ...) and the captain's console (red alert, hails, status, plotting a course)
    ///    are left out.
    ///  - Where: crew orders (CommandManager.TriggerCommand) only change the game they're given in (its orders
    ///    list isn't sent anywhere), and the AI crew runs on the host (USSAegisCrew.BatchUpdate: master client
    ///    only). So the host carries them out: anyone else puts the order in their player properties ("VO",
    ///    "number:command"), and the host runs each new one it sees, the crew's answer playing for everyone
    ///    as usual. The captain's console actions stay local, as the console's state is shared already.
    ///  - In the PvP test the host runs each order as the sender's ship's (PvpRouting.RunAs), for that ship's crew.
    ///  - The host runs only the crew order names the voice commands use (anything else is ignored), and only
    ///    orders given after it first saw the player, so nothing old is replayed after a join or a new host.
    /// </summary>
    internal static class CrewOrders
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const string PlayerKey = "VO";

        private static int sent;                                                // this game's last order number
        private static readonly Dictionary<int, string> seen = new Dictionary<int, string>(); // actor → last "number:command"
        private static float nextCheck;

        /// <summary>A non-captain may give voice orders: [Voice] AnyStation, and sitting at a console.</summary>
        internal static bool AnyStation => Plugin.VoiceAnyStation.Value && MyStation() != null;

        /// <summary>The captain's console's own actions, never sent to the host.</summary>
        internal static bool AtConsole(string one) =>
            one == "RedAlertOn" || one == "RedAlertOff" || one == "AnswerHail" || one == "StatusReport"
            || one.StartsWith("Course:", StringComparison.Ordinal);

        /// <summary>The orders a non-captain can give: the other stations' AI crew's. Null when none are left.</summary>
        internal static string ForMyStation(string command)
        {
            string mine = MyStation();
            var kept = new List<string>();
            foreach (var one in command.Split(','))
            {
                string station = AtConsole(one) ? "Captain" : StationOf(one);
                if (station == null || station == "Captain" || station == mine)
                {
                    Plugin.Log.LogInfo($"Voice commands: {one} isn't an order {mine ?? "your station"} gives the crew; left out");
                    continue;
                }
                kept.Add(one);
            }
            return kept.Count == 0 ? null : string.Join(",", kept.ToArray());
        }

        /// <summary>This game runs the AI crew: the host, or no one else in an online room (offline play too).</summary>
        internal static bool IAmHost
        {
            get
            {
                var photon = Patches.PhotonNetworkType;
                if (!(photon?.GetMethod("get_inRoom", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) is bool inRoom) || !inRoom) return true;
                return photon.GetMethod("get_isMasterClient", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) is bool master && master;
            }
        }

        internal static void SendToHost(string command)
        {
            var me = Patches.PhotonNetworkType?.GetMethod("get_player", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
            if (me == null || !PhotonProps.Set(me, PlayerKey, (++sent) + ":" + command))
            {
                Plugin.Log.LogWarning($"Voice commands: couldn't send {command} to the host");
                return;
            }
            Plugin.Log.LogInfo($"Voice commands: {command} (sent to the host, who runs the AI crew)");
        }

        // ---- Crew menu orders (PvP test) -------------------------------------------------------------

        internal const string Playbook = "Playbook:";

        /// <summary>The crew menus too: a menu or playbook order picked on a game that isn't the host's only changed
        /// that game's own copy of the orders, which no AI crew reads. In the PvP test (each ship's crew run by
        /// the host as that ship's) they're sent to the host instead, like voice orders.</summary>
        internal static void Patch(Harmony harmony)
        {
            var manager = AccessTools.TypeByName("AI.CommandManager");
            var apply = manager == null ? null : AccessTools.Method(manager, "ApplyNewOrder");
            var item = AccessTools.TypeByName("AI.CHJEFCBPHGN");
            var execute = item == null ? null : AccessTools.Method(item, "OnExecute");
            menuCommand = item == null ? null : AccessTools.Field(item, "<ONOKMLAGHHI>k__BackingField");
            if (apply == null || execute == null || menuCommand == null)
            {
                Plugin.Log.LogWarning("Crew orders: the crew menu's order methods weren't found; in the PvP test, a crewmate's menu orders stay on their game");
                return;
            }
            harmony.Patch(apply, prefix: new HarmonyMethod(typeof(CrewOrders), nameof(Playbook_Prefix)));
            harmony.Patch(execute, prefix: new HarmonyMethod(typeof(CrewOrders), nameof(MenuOrder_Prefix)));
        }

        private static FieldInfo menuCommand;

        private static bool Forwarding => PvpPrototype.Active && !IAmHost;

        private static bool Playbook_Prefix(int __0)
        {
            if (!Forwarding) return true;
            SendToHost(Playbook + __0);
            return false;
        }

        private static bool MenuOrder_Prefix(object __instance)
        {
            if (!Forwarding || __instance == null) return true;
            var command = menuCommand.GetValue(__instance)?.ToString();
            if (string.IsNullOrEmpty(command) || command == "Invalid") return true;
            SendToHost(command);
            return false;
        }

        // ---- The host ---------------------------------------------------------------------------

        internal static void Update()
        {
            if (Time.realtimeSinceStartup < nextCheck) return;
            nextCheck = Time.realtimeSinceStartup + 0.2f;
            var photon = Patches.PhotonNetworkType;
            if (photon == null) return;
            if (!(photon.GetMethod("get_inRoom", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) is bool inRoom) || !inRoom)
            {
                seen.Clear();
                return;
            }
            bool host = IAmHost;
            var players = photon.GetMethod("get_otherPlayers", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) as Array;
            if (players == null) return;
            var present = new HashSet<int>();
            foreach (var player in players)
            {
                if (player == null || !(Get(player, "get_ID") is int actor)) continue;
                present.Add(actor);
                var value = (Get(player, "get_customProperties") as IDictionary)?[PlayerKey] as string;
                if (value == null) continue;
                // Only the host acts, but everyone keeps track: a new host doesn't replay what the old one ran.
                bool known = seen.TryGetValue(actor, out var last);
                seen[actor] = value;
                if (!host || !known || last == value) continue;
                int colon = value.IndexOf(':');
                if (colon < 0) continue;
                var orders = value.Substring(colon + 1).Split(',').Where(Valid).ToArray();
                if (orders.Length == 0) continue;
                string command = string.Join(",", orders);
                Plugin.Log.LogInfo($"Voice commands: {Get(player, "get_name") ?? "a crewmate"} ordered {command}");
                // As that player's ship's code: in the PvP test, a ship B captain's orders are for ship B's crew.
                try { PvpRouting.RunAs(PvpShips.Of(player), () => VoiceCommands.Run(command)); }
                catch (Exception e) { Plugin.Log.LogWarning($"Voice commands: {command} from a crewmate: {(e.InnerException ?? e).Message}"); }
            }
            foreach (var gone in seen.Keys.Where(a => !present.Contains(a)).ToList()) seen.Remove(gone);
        }

        // A crew order the voice commands give (AI.BHPPPGKNAHK, CrewCommand), never the captain's console's.
        private static bool Valid(string one)
        {
            if (string.IsNullOrEmpty(one) || AtConsole(one)) return false;
            if (one.StartsWith(Playbook, StringComparison.Ordinal)) return int.TryParse(one.Substring(Playbook.Length), out int n) && n >= 0 && n < 256;
            var commands = AccessTools.TypeByName("AI.BHPPPGKNAHK");
            return commands != null && Enum.GetNames(commands).Contains(one);
        }

        // ---- Stations ---------------------------------------------------------------------------

        // The station you sit at (Captain, Helm, Tactical, Engineering), or null.
        private static string MyStation()
        {
            var info = NovaPlayerManager.IEFBKOMBFGO?.AJPNCBOKCLC as NovaPlayerInfo;
            var console = info == null ? null : info.GHJLPNMIOAJ; // Console
            if (console == null) return null;
            var name = console.GetType().Name;
            foreach (var station in new[] { "Captain", "Helm", "Tactical", "Engineering" })
                if (name.IndexOf(station, StringComparison.Ordinal) >= 0) return station;
            return null;
        }

        private static readonly MethodInfo StationOfCommand = AccessTools.Method(AccessTools.TypeByName("AI.CommandManager"), "LHPHCBLOOLE");

        // The station that carries an order out (CommandManager's own table), as a name.
        private static string StationOf(string one)
        {
            var commands = AccessTools.TypeByName("AI.BHPPPGKNAHK");
            if (commands == null || StationOfCommand == null || !Enum.GetNames(commands).Contains(one)) return null;
            return StationOfCommand.Invoke(null, new[] { Enum.Parse(commands, one) })?.ToString();
        }

        private static object Get(object target, string getter) => target.GetType().GetMethod(getter, All)?.Invoke(target, null);
    }
}
