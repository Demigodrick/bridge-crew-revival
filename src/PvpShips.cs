using System;
using System.Collections;
using System.Linq;
using System.Reflection;

namespace StbcRevival
{
    /// <summary>
    /// PvP test: which player ship each player crews. The host is on the first ship (A); the player who joined
    /// first after the host (the lowest Photon actor number that isn't the host's) is on the second (B,
    /// "PvP B"); anyone else on A. [Debug] PvpTestShip = A or B overrides it for your own game (Auto: the rule).
    /// Each game publishes its ship as the player property "PVP", so the host knows who is where even with
    /// an override.
    /// </summary>
    internal static class PvpShips
    {
        internal const string Key = "PVP";
        private const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        /// <summary>"A" or "B": the ship this game crews.</summary>
        internal static string Mine
        {
            get
            {
                var setting = Plugin.PvpTestShip.Value?.Trim().ToUpperInvariant();
                if (setting == "A" || setting == "B") return setting;
                return Auto(Me);
            }
        }

        /// <summary>The ship a Photon player crews: what their game published, else the rule.</summary>
        internal static string Of(object player)
        {
            if (player == null) return "A";
            if (Props(player)?[Key] is string published && (published == "A" || published == "B")) return published;
            return Auto(player);
        }

        /// <summary>The Photon player with this actor number, or null.</summary>
        internal static object Player(int actor) => Players.FirstOrDefault(p => Actor(p) == actor);

        internal static int Actor(object player) => player != null && Get(player, "get_ID") is int id ? id : -1;

        internal static object Me => Patches.PhotonNetworkType?.GetMethod("get_player", Static)?.Invoke(null, null);

        private static object[] Players =>
            Patches.PhotonNetworkType?.GetMethod("get_playerList", Static)?.Invoke(null, null) is Array list ? list.Cast<object>().Where(p => p != null).ToArray() : new object[0];

        private static string Auto(object player)
        {
            if (player == null || IsMaster(player)) return "A";
            int first = Players.Where(p => !IsMaster(p)).Select(Actor).Where(a => a >= 0).DefaultIfEmpty(-1).Min();
            return Actor(player) == first ? "B" : "A";
        }

        private static bool IsMaster(object player) => Get(player, "get_isMasterClient") is bool master && master;

        private static string published;

        /// <summary>While the PvP test runs: publish this game's ship (again whenever it changes).</summary>
        internal static void Update()
        {
            var me = Me;
            if (me == null) return;
            var mine = Mine;
            if (published == mine && Equals(Props(me)?[Key], mine)) return;
            if (PhotonProps.Set(me, Key, mine))
            {
                if (published != mine) Plugin.Log.LogInfo($"PvP: you crew ship {mine}");
                published = mine;
            }
        }

        private static object Get(object target, string getter) => target.GetType().GetMethod(getter, All)?.Invoke(target, null);

        private static IDictionary Props(object owner) => Get(owner, "get_customProperties") as IDictionary;
    }
}
