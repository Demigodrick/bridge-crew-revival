using System;
using System.Collections;
using System.Linq;
using System.Reflection;

namespace StbcRevival
{
    /// <summary>
    /// PvP test: which player ship each player crews. The crew is split in joining order: the host (master) on
    /// the first ship (A), then the others by Photon actor number, alternating B, A, B (2 players: one a ship;
    /// 3: two on A, one on B; 4: two each). [Debug] PvpTestShip = A or B overrides it for your own game (Auto:
    /// the rule).
    /// Each game publishes its ship as the player property "PVP", so the host knows who is where even with
    /// an override. A player whose game hasn't published one (the PvP test off: [Debug] PvpTest) counts as on A,
    /// where their game puts them as the normal game does, and the host is told when the test starts.
    /// </summary>
    internal static class PvpShips
    {
        internal const string Key = "PVP";
        private const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static int mineFrame = -1;
        private static string mine;

        /// <summary>"A" or "B": the ship this game crews (worked out once a frame: event routing asks often).</summary>
        internal static string Mine
        {
            get
            {
                if (mineFrame == UnityEngine.Time.frameCount) return mine;
                var setting = Plugin.PvpTestShip.Value?.Trim().ToUpperInvariant();
                mine = setting == "A" || setting == "B" ? setting : Auto(Me);
                mineFrame = UnityEngine.Time.frameCount;
                return mine;
            }
        }

        /// <summary>The ship a Photon player crews: what their game published; else yours by the rule, and anyone
        /// else's on A (their game has no PvP test, so it binds to the first ship as the game does).</summary>
        internal static string Of(object player)
        {
            if (player == null) return "A";
            if (Props(player)?[Key] is string published && (published == "A" || published == "B")) return published;
            return Actor(player) == Actor(Me) ? Mine : "A";
        }

        /// <summary>The other players whose game hasn't published a ship: no PvP test there.</summary>
        internal static string[] WithoutPvp() =>
            Players.Where(p => Actor(p) != Actor(Me) && !(Props(p)?[Key] is string))
                .Select(p => Get(p, "get_name") as string ?? "a player").ToArray();

        /// <summary>The Photon player with this actor number, or null.</summary>
        internal static object Player(int actor) => Players.FirstOrDefault(p => Actor(p) == actor);

        internal static int Actor(object player) => player != null && Get(player, "get_ID") is int id ? id : -1;

        internal static object Me => Patches.PhotonNetworkType?.GetMethod("get_player", Static)?.Invoke(null, null);

        private static object[] Players =>
            Patches.PhotonNetworkType?.GetMethod("get_playerList", Static)?.Invoke(null, null) is Array list ? list.Cast<object>().Where(p => p != null).ToArray() : new object[0];

        private static string Auto(object player)
        {
            if (player == null || IsMaster(player)) return "A";
            // The host is 0 (A); the others in actor order: 1 B, 2 A, 3 B.
            var order = Players.Where(p => !IsMaster(p)).Select(Actor).Where(a => a >= 0).OrderBy(a => a).ToList();
            int place = order.IndexOf(Actor(player)) + 1;
            return place % 2 == 1 ? "B" : "A";
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
