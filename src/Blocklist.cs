using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using CSteamID = Steamworks.IMHJDCPKKHM;
using SteamFriends = Steamworks.GJMOCKOJNPH; // Steamworks.NET SteamFriends (obfuscated)

namespace StbcRevival
{
    /// <summary>
    /// Blocking players from your crews (Steam mode, as a host). After you kick a player from the escape menu's
    /// player options (the game's "beam them into the sun", PlayerListUI.HIMNNIBCBPA), you're asked whether to
    /// block them too: they're disconnected from your in-game server (so a game that ignores the kick is gone
    /// as well) and refused whenever they try to join a crew you host. Players you've blocked or ignored in
    /// Steam itself are refused the same way. The list is [Players] Blocked in stbc.revival.cfg (Steam IDs);
    /// REVIVAL SETTINGS on the shuttle console can unblock everyone.
    /// </summary>
    internal static class Blocklist
    {
        private static readonly AccessTools.FieldRef<PlayerListUI, NovaPlayerInfo> Selected =
            AccessTools.FieldRefAccess<PlayerListUI, NovaPlayerInfo>("PCPBPMKFJOE");

        internal static void Patch(Harmony harmony)
        {
            var kicked = AccessTools.Method(typeof(PlayerListUI), "HIMNNIBCBPA");
            if (kicked == null)
            {
                Plugin.Log.LogWarning("Block list: the kick question wasn't found; no blocking from the player menu");
                return;
            }
            harmony.Patch(kicked, prefix: new HarmonyMethod(typeof(Blocklist), nameof(Kicked_Prefix)),
                postfix: new HarmonyMethod(typeof(Blocklist), nameof(Kicked_Postfix)));
        }

        // ---- The list ---------------------------------------------------------------------------

        // "steamid=name;steamid=name"
        private static Dictionary<string, string> Entries()
        {
            var list = new Dictionary<string, string>();
            foreach (var item in (Plugin.BlockedPlayers.Value ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = item.IndexOf('=');
                var id = (eq < 0 ? item : item.Substring(0, eq)).Trim();
                if (ulong.TryParse(id, out _)) list[id] = eq < 0 ? "" : item.Substring(eq + 1).Trim();
            }
            return list;
        }

        private static void Save(Dictionary<string, string> list) =>
            Plugin.BlockedPlayers.Value = string.Join(";", list.Select(kv => kv.Key + "=" + kv.Value.Replace(";", ",").Replace("=", "-")).ToArray());

        internal static int Count => Entries().Count;

        internal static void Add(ulong steamId, string name)
        {
            var list = Entries();
            list[steamId.ToString()] = name ?? "";
            Save(list);
            Plugin.Log.LogInfo($"Block list: blocked {name} ({steamId})");
        }

        internal static void Clear()
        {
            int n = Count;
            Plugin.BlockedPlayers.Value = "";
            Plugin.Log.LogInfo($"Block list: unblocked {n} players");
        }

        /// <summary>Is this player (Steam ID) kept out of your crews: on the list, or blocked/ignored in Steam.</summary>
        internal static bool IsBlocked(string steamId)
        {
            if (Entries().ContainsKey(steamId)) return true;
            if (!ulong.TryParse(steamId, out var id)) return false;
            try
            {
                var relation = SteamFriends.GetFriendRelationship(new CSteamID(id)).ToString();
                return relation == "k_EFriendRelationshipBlocked" || relation == "k_EFriendRelationshipIgnored" || relation == "k_EFriendRelationshipIgnoredFriend";
            }
            catch
            {
                return false; // Steam can't tell: not blocked
            }
        }

        // ---- After a kick ----------------------------------------------------------------------

        private sealed class Kick
        {
            public ulong Id;
            public string Name;
        }

        private static void Kicked_Prefix(PlayerListUI __instance, bool __0, out Kick __state)
        {
            __state = null;
            if (!__0 || !SteamMode.Enabled) return;
            var player = Selected(__instance);
            if (player == null) return;
            __state = new Kick { Id = SteamFriendAdd.SteamIdOf(player), Name = player.FAJABLKBGHN };
        }

        private static void Kicked_Postfix(Kick __state)
        {
            if (__state == null || __state.Id == 0 || __state.Id == SteamMode.MySteamId || SteamMode.Server == null) return;
            var kick = __state;
            Plugin.Instance.StartCoroutine(AskLater(kick));
        }

        private static IEnumerator AskLater(Kick kick)
        {
            yield return new WaitForSecondsRealtime(1f);
            try
            {
                UI.PopupMgr.ShowPopup($"Also block {kick.Name} from your crews? They won't be able to join again.".Replace('|', ' '),
                    new UI.LGNBEBILNCL(yes =>
                    {
                        if (!yes) return;
                        Add(kick.Id, kick.Name);
                        // If their game ignored the kick, it's gone now; either way they can't come back.
                        int closed = SteamMode.Server.DisconnectPeer(kick.Id.ToString());
                        Plugin.Log.LogInfo($"Block list: {kick.Name}'s connections closed: {closed}");
                    }), UI.PDHCCBFJODN.Question, true);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Block list: " + e.Message);
            }
        }
    }
}
