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
    /// PvP test: what a ship's AI crew says and its bridge messages, for that ship's players. Both crews run on
    /// the host, and the game sends their voice lines and the bridge's messages to every player with no ship in
    /// them, so the host keeps the other ship's off its bridge (PvpPrototype, PvpRouting, PvpConsoles). Those
    /// go to the other ship's players instead: the host lists them in a room property ("PVR": a numbered
    /// line each, with the ship), and each player's game plays the lines for its own ship on its own bridge.
    /// A game that has just joined skips what was there before it.
    /// </summary>
    internal static class PvpRelay
    {
        private const string RoomKey = "PVR";
        private const char Entries = '\u001e', Fields = '\u001f', Items = '\u001d';
        private const int Kept = 16;
        private const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;

        private static readonly List<string> sent = new List<string>();
        private static int sequence;
        private static int applied = -1; // the last line played here; -1: none read yet
        private static float nextCheck;

        /// <summary>A crew's voice line the host keeps off its bridge: for that ship's players.</summary>
        internal static void Voice(string ship, string evt, object voice, float delay)
        {
            if (string.IsNullOrEmpty(evt) || !OthersOn(ship)) return;
            Send(ship, "V", evt, Convert.ToInt32(voice).ToString(), delay.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>A bridge message the host keeps off its bridge: for that ship's players.</summary>
        internal static void Message(string ship, object role, string text, float duration, bool a, bool b, string[] args)
        {
            if (string.IsNullOrEmpty(text) || !OthersOn(ship)) return;
            Send(ship, "M", Convert.ToInt32(role).ToString(), text, duration.ToString(System.Globalization.CultureInfo.InvariantCulture),
                 a ? "1" : "0", b ? "1" : "0", string.Join(Items.ToString(), args ?? new string[0]));
        }

        // Someone besides the host is on that ship.
        private static bool OthersOn(string ship)
        {
            if (ship == null) return false;
            var players = Patches.PhotonNetworkType?.GetMethod("get_otherPlayers", Static)?.Invoke(null, null) as Array;
            return players != null && players.Cast<object>().Any(p => p != null && PvpShips.Of(p) == ship);
        }

        private static void Send(string ship, string kind, params string[] values)
        {
            var room = Patches.PhotonNetworkType?.GetMethod("get_room", Static)?.Invoke(null, null);
            if (room == null) return;
            var line = string.Join(Fields.ToString(), new[] { (++sequence).ToString(), ship, kind }.Concat(values.Select(Clean)).ToArray());
            sent.Add(line);
            if (sent.Count > Kept) sent.RemoveAt(0);
            PhotonProps.Set(room, RoomKey, string.Join(Entries.ToString(), sent.ToArray()));
        }

        private static string Clean(string value) => (value ?? "").Replace(Entries, ' ').Replace(Fields, ' ');

        // ---- Voice chat per ship ------------------------------------------------------------------

        /// <summary>On the bridge, each crew hears its own ship's players only: voice chat from the other ship's
        /// players is let go before it plays (VoIpManager's voice event, 125, from that player).</summary>
        internal static void Patch(Harmony harmony)
        {
            var received = AccessTools.Method(typeof(VoIpManager), "GMFCCIFGKDN");
            if (received == null) Plugin.Log.LogWarning("PvP: VoIpManager's voice event not found; both crews hear each other");
            else harmony.Patch(received, prefix: new HarmonyMethod(typeof(PvpRelay), nameof(VoiceChat_Prefix)));
        }

        private static readonly HashSet<int> toldOtherShip = new HashSet<int>();

        private static bool VoiceChat_Prefix(byte __0, object __1, int __2)
        {
            if (__0 != 125 || GamePhaseManager.IEFBKOMBFGO == null) return true;
            var phase = GamePhaseManager.IEFBKOMBFGO.GetCurrentGamePhase();
            if (phase == typeof(GamePhase_BriefingRoom) && PvpLobby.InPvpRoom) return LobbyVoice(__1, __2);
            if (!PvpPrototype.Active || phase != typeof(GamePhase_Bridge)) return true;
            var player = PvpShips.Player(__2);
            if (player == null || PvpShips.Of(player) == PvpShips.Mine) return true;
            if (__1 is SVR.Network.PLPKPDFOLDL packet) SVR.Network.PLPKPDFOLDL.Release(packet); // back to its pool, as the game does
            if (toldOtherShip.Add(__2)) Plugin.Log.LogInfo($"PvP: voice chat from player {__2} (the other ship) not played here");
            return false;
        }

        // The PvP lobby: a player at the other table isn't heard when either of you chose TABLE CHAT.
        private static bool LobbyVoice(object packet, int actor)
        {
            var player = PvpShips.Player(actor);
            bool theirs = player != null && PvpShips.Props(player)?[PvpRoles.ChatKey] as string == "T";
            if (!PvpRoles.TableChat && !theirs) return true;
            var mine = TableOf(PvpShips.Actor(PvpShips.Me));
            var their = TableOf(actor);
            if (mine == null || their == null || mine == their) return true;
            if (packet is SVR.Network.PLPKPDFOLDL voice) SVR.Network.PLPKPDFOLDL.Release(voice);
            return false;
        }

        // A player's table in the PvP lobby: the second set of chairs (PvpConsoles' copies) is table B.
        private static string TableOf(int actor)
        {
            var info = NovaPlayerManager.IEFBKOMBFGO?.GetInfoForPlayer(actor);
            var seat = info?.Station as NovaStation;
            return seat == null ? null : PvpConsoles.IsCopy(seat) ? "B" : "A";
        }

        /// <summary>Each frame (checked a few times a second): play this ship's new lines here.</summary>
        internal static void Update()
        {
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 0.2f;
            var photon = Patches.PhotonNetworkType;
            var room = photon?.GetMethod("get_room", Static)?.Invoke(null, null);
            if (room == null || !PvpPrototype.Active)
            {
                applied = -1;
                if (room == null) { sent.Clear(); sequence = 0; }
                return;
            }
            if (photon.GetMethod("get_isMasterClient", Static)?.Invoke(null, null) is true) return;
            var props = room.GetType().GetMethod("get_customProperties")?.Invoke(room, null) as IDictionary;
            var value = props != null && props.Contains(RoomKey) ? props[RoomKey] as string : null;
            if (string.IsNullOrEmpty(value)) return;
            int newest = applied;
            foreach (var entry in value.Split(Entries))
            {
                var f = entry.Split(Fields);
                if (f.Length < 3 || !int.TryParse(f[0], out int number)) continue;
                if (applied >= 0 && number > applied && f[1] == PvpShips.Mine) Play(f);
                newest = Math.Max(newest, number);
            }
            applied = newest; // the first read only notes where things are
        }

        private static void Play(string[] f)
        {
            try
            {
                var culture = System.Globalization.CultureInfo.InvariantCulture;
                if (f[2] == "V" && f.Length >= 6)
                {
                    var manager = AccessTools.Property(typeof(VOPlaybackManager), "IEFBKOMBFGO")?.GetValue(null, null)
                                  ?? AccessTools.Field(typeof(VOPlaybackManager), "IEFBKOMBFGO")?.GetValue(null);
                    var play = AccessTools.Method(typeof(VOPlaybackManager), "PlayVO");
                    if (manager == null || play == null) return;
                    var p = play.GetParameters();
                    play.Invoke(manager, new object[]
                    {
                        f[3], Enum.ToObject(p[1].ParameterType, int.Parse(f[4])), float.Parse(f[5], culture), false,
                        p[4].DefaultValue is DBNull ? Activator.CreateInstance(p[4].ParameterType) : p[4].DefaultValue, true,
                    });
                }
                else if (f[2] == "M" && f.Length >= 9 && Bridge.Instance != null)
                {
                    var show = AccessTools.Method(typeof(Bridge), "NewConsoleMessageRPC");
                    var args = f[8].Length == 0 ? new string[0] : f[8].Split(Items);
                    show?.Invoke(Bridge.Instance, new object[] { int.Parse(f[3]), f[4], float.Parse(f[5], culture), f[6] == "1", f[7] == "1", args });
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("PvP: playing your ship's crew line: " + e.Message);
            }
        }
    }
}
