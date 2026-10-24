using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using UnityEngine;
using PhotonHashtable = ExitGames.Client.Photon.Hashtable;

namespace StbcRevival
{
    /// <summary>
    /// Crews only mix players on the same Bridge Crew Revival version (missions and their sync differ between
    /// versions). Three layers:
    ///   - Our player properties carry our version ("MV"), so it goes with every join; the host's room carries
    ///     theirs. The host's in-game server refuses joins from other versions, older mods included
    ///     (StbcCore: LoadBalancingServer.ModVersionMismatch).
    ///   - Steam lobbies list the version ("mod") and our searches only ask for our own (SteamMode), so crews on
    ///     other versions don't show up in Quick Play or the crew lists.
    ///   - If we still end up in a crew on another version (hosted by a mod from before this check), we leave
    ///     and say why.
    /// </summary>
    internal static class ModVersionCheck
    {
        internal const string Key = "MV";
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;

        private static float nextCheck;
        private static string checkedRoom;
        private static float joinedAt;

        internal static void Update()
        {
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 1f;
            var photon = Patches.PhotonNetworkType;
            if (photon == null) return;

            // Our version in our player properties: kept locally out of a room, sent with the next join.
            var me = photon.GetMethod("get_player", Static)?.Invoke(null, null);
            if (me != null && Value(Props(me)) != Plugin.Version) Set(me, Plugin.Version);

            var room = photon.GetMethod("get_room", Static)?.Invoke(null, null);
            if (room == null)
            {
                checkedRoom = null;
                return;
            }
            var roomName = room.GetType().GetMethod("get_name", All)?.Invoke(room, null) as string;
            bool master = photon.GetMethod("get_isMasterClient", Static)?.Invoke(null, null) is bool b && b;
            if (master)
            {
                if (Value(Props(room)) != Plugin.Version) Set(room, Plugin.Version);
                checkedRoom = roomName;
                return;
            }

            // Joined someone else's crew: give their version a moment to arrive, then compare once.
            if (checkedRoom != roomName)
            {
                checkedRoom = roomName;
                joinedAt = Time.unscaledTime;
                return;
            }
            if (joinedAt < 0 || Time.unscaledTime - joinedAt < 3f) return;
            joinedAt = -1;
            var theirs = Value(Props(room));
            if (theirs == Plugin.Version) return;

            string what = string.IsNullOrEmpty(theirs) ? "an older version of Bridge Crew Revival" : "Bridge Crew Revival " + theirs;
            Plugin.Log.LogWarning($"Mod version check: crew {roomName} runs {what}; we run {Plugin.Version}. Leaving.");
            Leave(photon);
            var message = $"This crew runs {what}; you have {Plugin.Version}. Everyone in a crew needs the same version: "
                          + "update from the Bridge Crew Revival website, or ask the host to.";
            RoomBrowser.Show(message);
            try { UI.PopupMgr.ShowPopup(message.Replace('|', ' '), null, UI.PDHCCBFJODN.Notification, true); }
            catch { /* no popups in this scene */ }
        }

        private static IDictionary Props(object owner) =>
            owner.GetType().GetMethod("get_customProperties", All)?.Invoke(owner, null) as IDictionary;

        private static string Value(IDictionary props) => props != null && props.Contains(Key) ? props[Key] as string : null;

        private static void Set(object owner, string version) =>
            owner.GetType().GetMethod("SetCustomProperties", All)?.Invoke(owner, new object[] { new PhotonHashtable { [Key] = version }, null, false });

        private static void Leave(Type photon)
        {
            var leave = photon.GetMethods(Static).Where(m => m.Name == "LeaveRoom").OrderBy(m => m.GetParameters().Length).FirstOrDefault();
            if (leave == null) return;
            var args = leave.GetParameters()
                .Select(p => p.IsOptional ? p.DefaultValue : p.ParameterType == typeof(bool) ? (object)true : null).ToArray();
            leave.Invoke(null, args);
        }
    }
}
