using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using SVR.Network;
using UnityEngine;
using CSteamID = Steamworks.IMHJDCPKKHM;
using EFriendFlags = Steamworks.GLOAPNMOAHC;
using EPersonaState = Steamworks.GEINEOFCHIC;
using FriendGameInfo = Steamworks.NFLENKICLPJ;    // FriendGameInfo_t
using SteamFriends = Steamworks.GJMOCKOJNPH;
using SteamUtils = Steamworks.IKEEMFCPMGL;        // client SteamUtils (MAHNAPEJHLD is the game-server one)
using Friend = SVR.Network.HPJNFNEKGNJ;           // the game's friend entry
using PcProfile = SVR.Network.HPJNFNEKGNJ.ANLJPHNANFL;
using FriendStatus = SVR.Network.HPJNFNEKGNJ.MHBDFAIKNGB;
using UplayStatus = SVR.Network.Uplay.BKELONMLOLL;
using UplayState = SVR.Network.Uplay.KMNOICIOCFG;
using UplayRelationship = SVR.Network.Uplay.EKONEMPJELD;

namespace StbcRevival
{
    /// <summary>
    /// The shuttle console's friends panel ("UBISOFT CLUB FRIENDS"), fed with Steam friends instead of Uplay:
    /// the game's own list, detail view, icons and paging, so it works in VR.
    ///  - lists Steam friends playing Bridge Crew first, then everyone online;
    ///  - "Join" joins a friend's crew (from their rich presence connect string, set by this mod);
    ///  - "Invite" sends a Steam game invite for your current room (friends without the game get a launch link);
    ///  - avatars come from Steam.
    /// Entries are the game's friend objects with a PC profile filled in from Steam; the Uplay code paths
    /// that would refresh them (presence polling, avatar download) are skipped for these entries.
    /// </summary>
    internal static class SteamFriendsPanel
    {
        private const uint BridgeCrewAppId = 527100;
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        private sealed class SteamInfo
        {
            public ulong Id;
            public FriendStatus Status;
            public string Connect = "";
        }

        private static readonly Dictionary<Friend, SteamInfo> info = new Dictionary<Friend, SteamInfo>();
        private static readonly Dictionary<ulong, Friend> byId = new Dictionary<ulong, Friend>();
        private static readonly Dictionary<ulong, Sprite> avatars = new Dictionary<ulong, Sprite>();
        private static float nextRefresh;

        // Private members of the game's friend entry (HPJNFNEKGNJ).
        private static readonly AccessTools.FieldRef<Friend, PcProfile> Profile = AccessTools.FieldRefAccess<Friend, PcProfile>("ANEGDFCKGEC");
        private static readonly AccessTools.FieldRef<Friend, FriendStatus> Status = AccessTools.FieldRefAccess<Friend, FriendStatus>("HKMMKFJNJBJ");
        private static readonly AccessTools.FieldRef<Friend, CFEAJIENMGO> Platform = AccessTools.FieldRefAccess<Friend, CFEAJIENMGO>("<JNPNEMLBCHA>k__BackingField");
        private static string lastSignature;

        internal static bool Enabled => SteamMode.Enabled && Plugin.SteamPresence.Value;

        internal static void Patch(Harmony harmony)
        {
            void Hook(Type type, string method, string prefix = null, string postfix = null, string transpiler = null)
            {
                var target = AccessTools.Method(type, method);
                if (target == null)
                {
                    Plugin.Log.LogWarning($"Steam friends panel: {type.Name}.{method} not found; panel left as is.");
                    return;
                }
                harmony.Patch(target,
                    prefix: prefix != null ? new HarmonyMethod(typeof(SteamFriendsPanel), prefix) : null,
                    postfix: postfix != null ? new HarmonyMethod(typeof(SteamFriendsPanel), postfix) : null,
                    transpiler: transpiler != null ? new HarmonyMethod(typeof(SteamFriendsPanel), transpiler) : null);
            }

            Plugin.Log.LogInfo("Steam friends panel: replacing the Uplay friends list");
            Hook(typeof(FriendsListUI), "IMEKMHIKHIC", transpiler: nameof(Refresh_Transpiler)); // list refresh
            Hook(typeof(FriendsListUI), "Start", postfix: nameof(ListStart_Postfix));
            Hook(typeof(FriendsListUI), "OnEnable", prefix: nameof(ListEnable_Prefix));
            Hook(typeof(Friend), "ReevaluateMyLife", prefix: nameof(Reevaluate_Prefix));
            Hook(typeof(Friend), "UpdateUplayPCPresence", prefix: nameof(SkipForSteam_Prefix));
            Hook(typeof(Friend), "get_CanBeInvited", postfix: nameof(CanBeInvited_Postfix));
            Hook(typeof(FriendDetailUI), "NGCPCFNFFNI", prefix: nameof(Join_Prefix));   // "Join friend" button
            Hook(typeof(FriendDetailUI), "JEJJLGIKFHF", prefix: nameof(Invite_Prefix)); // "Invite" button
            Hook(typeof(AvatarManager), "CKGEHAPOPEN", prefix: nameof(Avatar_Prefix));  // icon lookup
        }

        // ---- The list ---------------------------------------------------------------------------

        // The refresh shows "UPLAY OFF - NO FRIENDS!" when the login method is None. Make it read the
        // login method through LoginForPanel, which reports PCApp while the Steam list is on.
        private static IEnumerable<CodeInstruction> Refresh_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var getLogin = AccessTools.Method(typeof(NetworkCore), "get_LoginMethod");
            int replaced = 0;
            foreach (var ins in instructions)
            {
                yield return ins;
                if (ins.operand is MethodInfo m && m == getLogin)
                {
                    replaced++;
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SteamFriendsPanel), nameof(LoginForPanel)));
                }
            }
            if (replaced == 0) Plugin.Log.LogWarning("Steam friends panel: login check not found in the list refresh.");
        }

        private static DHCMJKJFCMJ LoginForPanel(DHCMJKJFCMJ actual) =>
            actual == DHCMJKJFCMJ.None && Enabled ? DHCMJKJFCMJ.PCApp : actual;

        private static void ListStart_Postfix(FriendsListUI __instance)
        {
            if (!Enabled) return;
            foreach (var name in new[] { "_titleTextAegis", "_titleTextEnterprise" })
                SetText(typeof(FriendsListUI).GetField(name, All)?.GetValue(__instance), "STEAM FRIENDS");
        }

        // The panel caches its sorted list and only drops it on "list updated" events, which it misses
        // while hidden: drop it whenever the panel is shown.
        private static void ListEnable_Prefix(FriendsListUI __instance)
        {
            if (Enabled) typeof(FriendsListUI).GetField("LMHDDPCJIKO", All)?.SetValue(__instance, null);
        }

        private static void SetText(object tmp, string text) => tmp?.GetType().GetProperty("text")?.SetValue(tmp, text, null);

        /// <summary>Rebuild the friend list from Steam every few seconds; refresh the panel when it changes.</summary>
        internal static void Update()
        {
            if (!Enabled || Time.realtimeSinceStartup < nextRefresh || !SteamName.SteamInitialized()) return;
            nextRefresh = Time.realtimeSinceStartup + 3f;
            var manager = NetworkCore.IEFBKOMBFGO != null ? NetworkCore.IEFBKOMBFGO.IJPEEMOFCKD : null;
            if (manager == null) return;

            string myRoom = GameState.RoomName();
            var friends = new List<Friend>();
            var signature = new System.Text.StringBuilder(myRoom ?? "");
            int count = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            for (int i = 0; i < count; i++)
            {
                var id = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                var persona = SteamFriends.GetFriendPersonaState(id);
                if (persona == EPersonaState.k_EPersonaStateOffline) continue;
                bool inGame = SteamFriends.GetFriendGamePlayed(id, out FriendGameInfo game)
                              && (uint)(game.m_gameID.m_GameID & 0xFFFFFF) == BridgeCrewAppId;
                string connect = inGame ? SteamFriends.GetFriendRichPresence(id, "connect") ?? "" : "";
                string status = inGame ? SteamFriends.GetFriendRichPresence(id, "status") ?? "" : "";
                string group = inGame ? SteamFriends.GetFriendRichPresence(id, "steam_player_group") ?? "" : "";
                string name = SteamFriends.GetFriendPersonaName(id);

                FriendStatus state;
                string presenceText;
                if (inGame && myRoom != null && group == myRoom)
                {
                    state = FriendStatus.InMySession;
                    presenceText = "In your crew";
                }
                else if (inGame && connect.Contains(SteamIntegration.JoinArg))
                {
                    state = FriendStatus.PlayingThisGame; // joinable
                    presenceText = status.Length > 0 ? status : "Playing Star Trek Bridge Crew";
                }
                else if (inGame)
                {
                    state = FriendStatus.Online; // in game, but no room to join (or no mod)
                    presenceText = status.Length > 0 ? status : "Playing Star Trek Bridge Crew";
                }
                else
                {
                    state = persona == EPersonaState.k_EPersonaStateAway || persona == EPersonaState.k_EPersonaStateSnooze
                        ? FriendStatus.AFK : FriendStatus.Online;
                    presenceText = ""; // the game shows "Online" / "Away"
                }

                friends.Add(Entry(id.m_SteamID, name, state, presenceText, connect));
                signature.Append('|').Append(id.m_SteamID).Append(name).Append((int)state).Append(presenceText);
            }

            var list = manager.IMFBJCGNGPL; // FriendList (the panel sorts it itself)
            if (signature.ToString() == lastSignature && list.Count == friends.Count) return;
            lastSignature = signature.ToString();
            list.Clear();
            list.AddRange(friends);
            int playing = friends.Count(f => info[f].Status == FriendStatus.PlayingThisGame || info[f].Status == FriendStatus.InMySession);
            Plugin.Log.LogInfo($"Steam friends panel: {friends.Count} online, {playing} in a joinable Bridge Crew room or your crew");
            CIFDOFLFGEA.Send(new IJMDEBIIKDN()); // "friend list updated": the panel rebuilds
        }

        private static Friend Entry(ulong id, string name, FriendStatus status, string presenceText, string connect)
        {
            if (!byId.TryGetValue(id, out var friend))
            {
                friend = new Friend();
                Profile(friend) = new PcProfile
                {
                    UplayProfileId = "steam:" + id,
                    IdOnPlatform = id.ToString(),
                    UserId = "steam:" + id,
                    PlatformType = "PC",
                    Relationship = UplayRelationship.Friends,
                };
                byId[id] = friend;
                info[friend] = new SteamInfo { Id = id };
            }
            else if (avatars.ContainsKey(id) && avatars[id] == null) avatars.Remove(id); // retry a failed avatar
            var steam = info[friend];
            steam.Status = status;
            steam.Connect = connect;
            Profile(friend).NameOnPlatform = name;
            Profile(friend).SetPresence(
                status == FriendStatus.AFK ? UplayStatus.Away : UplayStatus.Online,
                status == FriendStatus.PlayingThisGame || status == FriendStatus.InMySession ? UplayState.InCurrentGame : UplayState.None,
                presenceText);
            friend.ReevaluateMyLife();
            return friend;
        }

        // ---- Keep the game's Uplay logic off Steam entries -------------------------------------

        private static bool Reevaluate_Prefix(Friend __instance)
        {
            if (!info.TryGetValue(__instance, out var steam)) return true;
            Status(__instance) = steam.Status;
            Platform(__instance) = CFEAJIENMGO.PC;
            return false;
        }

        private static bool SkipForSteam_Prefix(Friend __instance) => !info.ContainsKey(__instance);

        // Invite anyone online who isn't already in your crew; Steam handles friends without the game.
        private static void CanBeInvited_Postfix(Friend __instance, ref bool __result)
        {
            if (info.TryGetValue(__instance, out var steam))
                __result = steam.Status != FriendStatus.InMySession && steam.Status != FriendStatus.Offline;
        }

        // ---- Buttons ----------------------------------------------------------------------------

        private static Friend DetailFriend(FriendDetailUI detail) =>
            typeof(FriendDetailUI).GetField("IEEKEFCLGND", All)?.GetValue(detail) as Friend;

        private static bool Join_Prefix(FriendDetailUI __instance)
        {
            var friend = DetailFriend(__instance);
            if (friend == null || !info.TryGetValue(friend, out var steam)) return true;
            if (string.IsNullOrEmpty(steam.Connect))
            {
                UI.PopupMgr.ShowPopup("This friend isn't in a crew you can join right now.", null, UI.PDHCCBFJODN.Notification, true);
                return false;
            }
            Plugin.Log.LogInfo($"Steam friends panel: joining {Profile(friend).NameOnPlatform} ({steam.Connect})");
            SteamIntegration.JoinFromConnect(steam.Connect);
            return false;
        }

        private static bool Invite_Prefix(FriendDetailUI __instance)
        {
            var friend = DetailFriend(__instance);
            if (friend == null || !info.TryGetValue(friend, out var steam)) return true;
            var connect = SteamIntegration.CurrentConnect;
            if (string.IsNullOrEmpty(connect))
            {
                UI.PopupMgr.ShowPopup("Your crew isn't open to join right now.", null, UI.PDHCCBFJODN.Notification, true);
                return false;
            }
            bool sent = SteamFriends.InviteUserToGame(new CSteamID(steam.Id), connect);
            Plugin.Log.LogInfo($"Steam friends panel: invited {Profile(friend).NameOnPlatform}: {sent}");
            UI.PopupMgr.ShowPopup(sent ? $"Invite sent to {Profile(friend).NameOnPlatform}." : "Steam couldn't send the invite.",
                null, UI.PDHCCBFJODN.Notification, true);
            return false;
        }

        // ---- Avatars ----------------------------------------------------------------------------

        private static bool Avatar_Prefix(GGKEAHLPEEH NBHDALBLHHF, ref Sprite __result, ref bool BOJMJANONMK)
        {
            if (!(NBHDALBLHHF is Friend friend) || !info.TryGetValue(friend, out var steam)) return true;
            __result = SteamAvatar(steam.Id);
            // "Turn it over": the game sets it for Ubisoft's pictures, loaded top-down, and AvatarIcon flips the
            // image for them. Steam's are already turned the right way up (SteamAvatar), as is the default icon.
            BOJMJANONMK = false;
            if (__result == null) __result = NetworkCore.CNJOBBMNDMC; // the game's default icon
            return false;
        }

        private static Sprite SteamAvatar(ulong id)
        {
            if (avatars.TryGetValue(id, out var sprite) && sprite != null) return sprite;
            try
            {
                int image = SteamFriends.GetMediumFriendAvatar(new CSteamID(id));
                if (image <= 0 || !SteamUtils.GetImageSize(image, out uint w, out uint h) || w == 0 || h == 0) return null; // still loading
                var rgba = new byte[w * h * 4];
                if (!SteamUtils.GetImageRGBA(image, rgba, rgba.Length)) return null;
                // Steam rows are top-down, Unity's bottom-up.
                var flipped = new byte[rgba.Length];
                int row = (int)w * 4;
                for (int y = 0; y < h; y++) Buffer.BlockCopy(rgba, y * row, flipped, ((int)h - 1 - y) * row, row);
                var texture = new Texture2D((int)w, (int)h, TextureFormat.RGBA32, false);
                texture.LoadRawTextureData(flipped);
                texture.Apply();
                sprite = Sprite.Create(texture, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f));
                avatars[id] = sprite;
                return sprite;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Steam friends panel: avatar failed: " + e.Message);
                avatars[id] = null;
                return null;
            }
        }
    }
}
