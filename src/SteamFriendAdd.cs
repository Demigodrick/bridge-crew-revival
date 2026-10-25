using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine.UI;
using CSteamID = Steamworks.IMHJDCPKKHM;
using SteamFriends = Steamworks.GJMOCKOJNPH; // Steamworks.NET SteamFriends (obfuscated)

namespace StbcRevival
{
    /// <summary>
    /// The escape menu's player options, "Add as Friend": a Uplay friend invite in the game
    /// (PlayerListUI.EOJBLIEBGKE → SendFriendInviteRequest), greyed out unless Uplay allows one, so never now.
    /// In Steam mode it opens Steam's own add-friend page for that player (the overlay: Steam has no call that
    /// sends a request by itself), from the Steam ID every mod player shares in their player properties
    /// (HostHandoff's "SID"). The button is available for a player with one who isn't you or already a friend.
    /// The ID is what the player says about themselves; Steam's page shows whose profile it is before you
    /// send anything, so a wrong one only opens someone else's page.
    /// </summary>
    internal static class SteamFriendAdd
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly FieldInfo Selected = AccessTools.Field(typeof(PlayerListUI), "PCPBPMKFJOE"); // NovaPlayerInfo
        private static readonly FieldInfo AddButton = AccessTools.Field(typeof(PlayerListUI), "JEFHDNFCPLD");

        internal static void Patch(Harmony harmony)
        {
            var add = AccessTools.Method(typeof(PlayerListUI), "EOJBLIEBGKE");
            var open = AccessTools.Method(typeof(PlayerListUI), "IIGLKLICKDJ"); // a player picked: their options page
            if (add == null || open == null || Selected == null || AddButton == null)
            {
                Plugin.Log.LogWarning("Steam friend add: the player menu's methods weren't found; Add as Friend left as it is");
                return;
            }
            harmony.Patch(add, prefix: new HarmonyMethod(typeof(SteamFriendAdd), nameof(Add_Prefix)));
            harmony.Patch(open, postfix: new HarmonyMethod(typeof(SteamFriendAdd), nameof(Open_Postfix)));
        }

        private static void Open_Postfix(PlayerListUI __instance)
        {
            if (!SteamMode.Enabled) return;
            try
            {
                if (AddButton.GetValue(__instance) is Button button)
                    button.interactable = CanAdd(SteamIdOf(Selected.GetValue(__instance)));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Steam friend add: " + e.Message);
            }
        }

        private static bool Add_Prefix(PlayerListUI __instance)
        {
            if (!SteamMode.Enabled) return true;
            try
            {
                ulong id = SteamIdOf(Selected.GetValue(__instance));
                if (!CanAdd(id)) return false;
                SteamFriends.ActivateGameOverlayToUser("friendadd", new CSteamID(id));
                Plugin.Log.LogInfo($"Steam friend add: opened Steam's add-friend page for {id}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Steam friend add: " + e.Message);
            }
            return false; // never the Uplay invite
        }

        // A Steam ID we can send a request to: known, not ours, not already a friend.
        private static bool CanAdd(ulong id)
        {
            if (id == 0 || id == SteamMode.MySteamId) return false;
            try { return SteamFriends.GetFriendRelationship(new CSteamID(id)).ToString() != "k_EFriendRelationshipFriend"; }
            catch { return false; } // can't tell: leave it off
        }

        // NovaPlayerInfo → its network player (PKIBEAIHJNE) → the "SID" player property.
        private static ulong SteamIdOf(object info)
        {
            var player = info == null ? null : AccessTools.Field(info.GetType(), "PKIBEAIHJNE")?.GetValue(info)
                                                ?? AccessTools.Property(info.GetType(), "PKIBEAIHJNE")?.GetValue(info, null);
            var props = player?.GetType().GetMethod("get_customProperties", All)?.Invoke(player, null) as IDictionary;
            return props != null && props.Contains(HostHandoff.SteamIdKey) && ulong.TryParse(props[HostHandoff.SteamIdKey] as string, out var id) ? id : 0;
        }
    }
}
