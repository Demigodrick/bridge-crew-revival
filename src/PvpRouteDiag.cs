using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// PvP: what goes to which ship, to find what still reaches the wrong one. On the bridge, all "[route]" lines:
    ///   - Checks every 5 s, each problem logged when it starts and when it clears (not every time):
    ///       • a player at a station of the other ship's set; a crew bot at the other set's station;
    ///       • a crew bot off while none of its consoles has a player, or on while a player sits there;
    ///       • a console of one set driving the other ship (its ship interface), or crewed by the other ship's bot;
    ///       • an AI crew working the other set's consoles;
    ///       • your bridge's consoles (Bridge.Captain and the rest) or its ship not your own;
    ///       • a ship's view or code left switched on outside it (PvpScope, PvpRouting: a leak sends what follows
    ///         to that ship).
    ///   - Every 30 s, what the routing did since: RPCs, sounds, console messages, voice and orders kept off or
    ///     sent to a ship (Count, from each routing point), the most frequent first.
    ///   - Once per bridge, and whenever it changes, who sits where (players and bots, each ship's set).
    /// </summary>
    internal static class PvpRouteDiag
    {
        private static readonly Dictionary<string, int> counts = new Dictionary<string, int>();
        private static readonly Dictionary<string, string> problems = new Dictionary<string, string>();
        private static float nextCheck, nextSummary;
        private static string lastSeats;
        private static readonly PropertyInfo NpcEnabled = AccessTools.Property(typeof(PlayerInfo), "GALIAPOJHAI"); // IsNPCEnabled
        private static readonly FieldInfo BridgeCraft = AccessTools.Field(typeof(Bridge), "<BAPNEMLKIOK>k__BackingField"); // craft

        private static bool On => PvpLobby.InPvpRoom && PvpPrototype.Active && PvpPrototype.Bound != null;

        /// <summary>One routing decision, for the 30 s summary.</summary>
        internal static void Count(string what)
        {
            if (!On || string.IsNullOrEmpty(what)) return;
            counts.TryGetValue(what, out int n);
            counts[what] = n + 1;
        }

        internal static void Update()
        {
            if (!On)
            {
                if (lastSeats != null || problems.Count > 0 || counts.Count > 0) { lastSeats = null; problems.Clear(); counts.Clear(); }
                return;
            }
            float now = Time.unscaledTime;
            if (now >= nextSummary)
            {
                if (nextSummary > 0f) Summary();
                nextSummary = now + 30f;
            }
            if (now < nextCheck) return;
            nextCheck = now + 5f;
            try { Check(); }
            catch (Exception e) { Report("check failed", "the route check: " + e.Message); }
        }

        private static void Summary()
        {
            if (counts.Count == 0) { Plugin.Log.LogInfo("PvP: [route] last 30 s: nothing routed"); return; }
            var top = counts.OrderByDescending(p => p.Value).Take(25).Select(p => $"{p.Key} ×{p.Value}").ToArray();
            Plugin.Log.LogInfo($"PvP: [route] last 30 s ({counts.Values.Sum()} decisions, {counts.Count} kinds): {string.Join("; ", top)}");
            counts.Clear();
        }

        private static string Ship(NovaStation station) => station == null ? "none" : PvpConsoles.IsCopy(station) ? "B" : "A";
        private static string Ship(ConsoleBase console) => console == null ? "none" : PvpConsoles.IsCopy(console) ? "B" : "A";

        private static string Name(PlayerInfo p) => p == null ? "nobody" : p.FAJABLKBGHN;

        private static void Check()
        {
            var found = new Dictionary<string, string>();
            string mine = PvpShips.Mine ?? "A";
            bool ownBots = PvpCrewBots.ShipBHasBots();
            var seats = new List<string>();

            // Leaks: this runs from Update, outside any ship's code.
            if (PvpScope.Applied != null) found["scope"] = $"ship {PvpScope.Applied}'s view is still switched on outside its code (what follows goes to that ship)";
            if (PvpRouting.Context != null) found["context"] = $"ship {PvpRouting.Context}'s code context is still set outside its code";

            // Players and bots.
            foreach (var player in Tracked.All<NovaPlayerInfo>())
            {
                if (player == null) continue;
                var station = player.Station as NovaStation;
                string at = Ship(station);
                if (player.AOMGCCGDJJC)
                {
                    string own = PvpCrewBots.IsShipBBot(player) ? "B" : "A";
                    bool on = NpcEnabled?.GetValue(player, null) is bool b && b;
                    var console = station == null ? null : station.GHJLPNMIOAJ;
                    bool taken = console != null && console.FKKJLMLCKJI != null && !console.FKKJLMLCKJI.AOMGCCGDJJC;
                    seats.Add($"bot {Name(player)} (ship {own}'s) at {(station == null ? "nothing" : station.name)} ({at}), {(on ? "on" : "off")}");
                    if (ownBots && station != null && at != own) found["botseat" + Name(player)] = $"ship {own}'s bot {Name(player)} sits at ship {at}'s {station.name}";
                    if (console != null && !on && !taken) found["botoff" + Name(player)] = $"bot {Name(player)} is off but its console {console.name} (ship {at}'s) has no player";
                    if (console != null && on && taken) found["boton" + Name(player)] = $"bot {Name(player)} is on while {Name(console.FKKJLMLCKJI)} sits at {console.name}";
                    continue;
                }
                string ship = PvpShips.Of(PvpShips.Player(player.NJNHJKEPFAK)) ?? "?";
                seats.Add($"{Name(player)} (ship {ship}) at {(station == null ? "nothing" : station.name)} ({at})");
                if (station != null && ship != "?" && at != ship) found["seat" + player.NJNHJKEPFAK] = $"{Name(player)} crews ship {ship} but sits at ship {at}'s {station.name}";
            }

            // Consoles: which ship each drives, and whose bot crews it.
            foreach (var station in (PvpConsoles.Stations ?? new NovaStation[0]).OfType<NovaStation>().Where(st => st != null))
            {
                var console = station.GHJLPNMIOAJ;
                if (console == null) continue;
                string set = Ship(station);
                var drives = DrivenShip(console);
                if (drives != null && drives != set) found["drive" + station.name + set] = $"ship {set}'s {console.name} drives ship {drives}";
                var bot = console.LPMMGGIAOCL;
                if (ownBots && bot != null)
                {
                    string botShip = PvpCrewBots.IsShipBBot(bot) ? "B" : "A";
                    if (botShip != set) found["ai" + station.name + set] = $"ship {set}'s {console.name} is crewed by ship {botShip}'s bot {Name(bot)}";
                }
            }

            // Each AI crew's consoles.
            foreach (var crew in UnityEngine.Object.FindObjectsOfType<AI.USSAegisCrew>())
            {
                if (crew == null) continue;
                var craft = crew.GetComponent<Sector.Spacecraft.PlayerSpacecraft>();
                if (craft == null) continue;
                string ship = PvpPrototype.Letter(craft);
                foreach (var field in new[] { "GELDBDOEIBN", "IJFDAJGLKLF", "LMFOFJEALLE" })
                {
                    var console = AccessTools.Field(typeof(AI.USSAegisCrew), field)?.GetValue(crew) as ConsoleBase;
                    if (console != null && Ship(console) != ship) found["crew" + ship + field] = $"ship {ship}'s AI crew works ship {Ship(console)}'s {console.name}";
                }
            }

            // Your bridge.
            foreach (var console in new ConsoleBase[] { Bridge.PKCFGMELINM, Bridge.AGAFPEFMEGB, Bridge.KHFJCHHMFHN, Bridge.LJFGEPOKONO })
                if (console != null && Ship(console) != mine) found["bridge" + console.name] = $"your bridge's {console.name} is ship {Ship(console)}'s (you crew ship {mine})";
            var bridge = Traverse.Create(typeof(Bridge)).Property("IEFBKOMBFGO").GetValue();
            if (bridge != null && BridgeCraft?.GetValue(bridge) is Sector.Spacecraft.PlayerSpacecraft bridgeShip && PvpPrototype.Letter(bridgeShip) != mine)
                found["bridgecraft"] = $"your bridge's ship is ship {PvpPrototype.Letter(bridgeShip)} (you crew ship {mine})";

            // Report what started and what cleared.
            foreach (var p in found)
                if (!problems.TryGetValue(p.Key, out var was) || was != p.Value) Report(p.Key, p.Value);
            foreach (var gone in problems.Keys.Where(k => !found.ContainsKey(k)).ToList())
            {
                Plugin.Log.LogInfo($"PvP: [route] cleared: {problems[gone]}");
                problems.Remove(gone);
            }

            string now = string.Join("; ", seats.OrderBy(s => s).ToArray());
            if (now != lastSeats)
            {
                lastSeats = now;
                Plugin.Log.LogInfo($"PvP: [route] seats (you crew ship {mine}{(ownBots ? ", ship B has its own bots" : ", bots shared")}): {now}");
            }
        }

        private static void Report(string key, string text)
        {
            problems[key] = text;
            Plugin.Log.LogWarning("PvP: [route] wrong ship: " + text);
        }

        // The player ship a console's ship interface belongs to ("A", "B"), or null if it has none.
        private static string DrivenShip(ConsoleBase console)
        {
            var shipInterface = console.BJLCFFKCFFN as Component;
            if (shipInterface == null) return null;
            var craft = shipInterface.GetComponentInParent<Sector.Spacecraft.PlayerSpacecraft>();
            return craft == null ? null : PvpPrototype.Letter(craft);
        }
    }
}
