using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using StbcServer;
using UnityEngine;
using UnityEngine.UI;

namespace StbcRevival
{
    /// <summary>
    /// Public crews with a password (Steam mode).
    ///   - Host, in the briefing room: answering yes to the console's "Open the room for public matchmaking?"
    ///     (ConsoleBriefingRoom.CIBAPNHJCIF / OKIKIKEHPPC) asks whether to lock it with a password. The game hides
    ///     that button once the crew is public; for the host it stays, as "Crew password", to change or remove it.
    ///     The password stays in the host's in-game server (LoadBalancingServer.SetRoomPassword), which checks
    ///     joiners; the crew's Steam listing only says it's locked ("lock"), so Quick Match skips it.
    ///   - Joiner: the password goes with the join as the player property "PW" (the server strips it before the
    ///     crew sees it). Refused for a missing or wrong one, the game asks for it and tries again; the same hook
    ///     turns the server's other refusals (another mod version) into a plain message instead of "room closed".
    /// </summary>
    internal static class CrewPassword
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const byte JoinGame = 226;
        private const short ActorNrParam = 254;

        private static readonly Dictionary<string, string> known = new Dictionary<string, string>(); // room → password typed for it
        private static string joining;   // the room last asked to join
        private static bool relabelled;

        internal static void Patch(Harmony harmony)
        {
            var console = typeof(ConsoleBriefingRoom);
            var ask = AccessTools.Method(console, "CIBAPNHJCIF");
            var answered = AccessTools.Method(console, "OKIKIKEHPPC");
            var showButton = AccessTools.Method(console, "IACKCMNEEKK");
            var joinEvent = AccessTools.Constructor(typeof(SVR.Network.PBGHGLIFLAE), new[] { typeof(string) });
            var peer = Patches.FindNetworkingPeer();
            var response = peer?.GetMethod("OnOperationResponse", BindingFlags.Public | BindingFlags.Instance);
            if (ask == null || answered == null || showButton == null || joinEvent == null || response == null)
            {
                Plugin.Log.LogWarning("Crew password: the briefing console or join hooks weren't found; no crew passwords");
                return;
            }
            harmony.Patch(ask, prefix: new HarmonyMethod(typeof(CrewPassword), nameof(Ask_Prefix)));
            harmony.Patch(answered, postfix: new HarmonyMethod(typeof(CrewPassword), nameof(Answered_Postfix)));
            harmony.Patch(showButton, postfix: new HarmonyMethod(typeof(CrewPassword), nameof(ShowButton_Postfix)));
            harmony.Patch(joinEvent, postfix: new HarmonyMethod(typeof(CrewPassword), nameof(JoinEvent_Postfix)));
            harmony.Patch(response, prefix: new HarmonyMethod(typeof(CrewPassword), nameof(Response_Prefix)));
        }

        // ---- Host -------------------------------------------------------------------------------

        // The crew this player hosts and is in (Steam mode), or null.
        private static string HostedRoom()
        {
            if (!SteamMode.Enabled || SteamMode.Server == null) return null;
            var room = RoomBrowser.CurrentRoomName();
            return room != null && SteamMode.Server.HostedRooms().Any(r => r.Name == room) ? room : null;
        }

        internal static bool IsLocked(string room) => room != null && SteamMode.Server != null && SteamMode.Server.IsPassworded(room);

        // The console's button: before the crew is public, the game's question; after, the password menu.
        private static bool Ask_Prefix()
        {
            var room = HostedRoom();
            if (room == null || !RoomBrowser.CurrentRoomVisible()) return true;
            if (IsLocked(room))
                Question("The crew has a password. Remove it?", remove =>
                {
                    if (remove) Set(room, null);
                    else Question("Set a new password?", change => { if (change) AskNew(room); });
                });
            else Question("Lock the crew with a password?", lockIt => { if (lockIt) AskNew(room); });
            return false;
        }

        // Yes to "Open the room for public matchmaking?": offer a password.
        private static void Answered_Postfix(bool __0)
        {
            var room = HostedRoom();
            if (!__0 || room == null) return;
            Plugin.Instance.StartCoroutine(After(0.5f, () =>
                Question("Lock the crew with a password? Only players you give it to can join.", lockIt => { if (lockIt) AskNew(room); })));
        }

        private static void AskNew(string room) => TextPrompt.Ask("Crew password", false, password => Set(room, password));

        private static void Set(string room, string password)
        {
            if (!SteamMode.Server.SetRoomPassword(room, password)) return;
            Notify(password == null ? "The crew's password is removed: anyone can join." : $"Crew password set: \"{password}\". Give it to the players you want aboard.");
        }

        private static readonly AccessTools.FieldRef<ConsoleBriefingRoom, Button> OpenRoomButton =
            AccessTools.FieldRefAccess<ConsoleBriefingRoom, Button>("FEFMKFOMEPA");

        // The game hides the button once the crew is public; the host keeps it, as "Crew password".
        private static void ShowButton_Postfix(ConsoleBriefingRoom __instance)
        {
            var button = OpenRoomButton(__instance);
            if (button == null) return;
            bool mine = HostedRoom() != null && RoomBrowser.CurrentRoomVisible();
            if (!mine)
            {
                relabelled = false;
                return;
            }
            if (!button.gameObject.activeSelf) button.gameObject.SetActive(true);
            if (relabelled) return;
            relabelled = true;
            foreach (var text in button.GetComponentsInChildren<Component>(true).Where(c => c != null && c.GetType().Name.StartsWith("TextMeshPro")))
            {
                var setLocalized = text.GetType().GetMethod("SetLocalizedText", new[] { typeof(string) });
                if (setLocalized != null) setLocalized.Invoke(text, new object[] { "CREW PASSWORD" });
                else text.GetType().GetProperty("text")?.SetValue(text, "CREW PASSWORD", null);
            }
        }

        // ---- Joiner -----------------------------------------------------------------------------

        // Every join goes through this event (codes, lists, friends, invites): remember where, and send a
        // password already typed for it.
        private static void JoinEvent_Postfix(SVR.Network.PBGHGLIFLAE __instance)
        {
            joining = __instance.RoomName;
            if (joining != null && known.TryGetValue(joining, out var password)) SetOwn(password);
        }

        private static void Response_Prefix(object[] __args)
        {
            var response = __args[0];
            if (response == null) return;
            var t = response.GetType();
            if (!(t.GetField("OperationCode")?.GetValue(response) is byte code) || code != JoinGame) return;
            short result = t.GetField("ReturnCode")?.GetValue(response) is short r ? r : (short)0;
            var message = t.GetField("DebugMessage")?.GetValue(response) as string ?? "";
            // The master's answer (where the room is) comes first; the game server's (in the room, or refused) ends it.
            bool joined = result == 0 && (t.GetField("Parameters")?.GetValue(response) as IDictionary)?.Contains((byte)ActorNrParam) == true;
            if (result == 0 && !joined) return;
            ClearOwn();
            if (result == 0) return;
            var room = joining;
            switch (message)
            {
                case "password required":
                    Later(() => Question("This crew has a password. Enter it?", yes => { if (yes) AskAndRetry(room); }));
                    break;
                case "wrong password":
                    if (room != null) known.Remove(room);
                    Later(() => Question("Wrong password. Try again?", yes => { if (yes) AskAndRetry(room); }));
                    break;
                case "different Bridge Crew Revival version":
                    Later(() => Notify($"This crew is on a different Bridge Crew Revival version than yours ({Plugin.Version}). Everyone in a crew needs the same version: update from the website."));
                    break;
                default:
                    if (message.StartsWith("too many wrong passwords")) Later(() => Notify("Too many wrong passwords. Wait a minute, then try again."));
                    break;
            }
        }

        private static void AskAndRetry(string room)
        {
            if (room == null) return;
            TextPrompt.Ask("Crew password", true, password =>
            {
                known[room] = password;
                CIFDOFLFGEA.Send(new SVR.Network.PBGHGLIFLAE(room)); // JoinEvent_Postfix sends the password
            });
        }

        // The password as this player's property, sent with the join (local only: not in a room yet).
        private static void SetOwn(string password)
        {
            var me = PvpShipsMe();
            if (me == null) return;
            PhotonProps.Set(me, ModProp.Password, password);
        }

        private static void ClearOwn()
        {
            var me = PvpShipsMe();
            if (me?.GetType().GetMethod("get_customProperties", All)?.Invoke(me, null) is IDictionary props && props.Contains(ModProp.Password))
                props.Remove(ModProp.Password);
        }

        private static object PvpShipsMe() => Patches.PhotonNetworkType?.GetMethod("get_player", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);

        // ---- Popups ------------------------------------------------------------------------------

        private static void Question(string text, Action<bool> answer)
        {
            try { UI.PopupMgr.ShowPopup(text.Replace('|', ' '), new UI.LGNBEBILNCL(answer), UI.PDHCCBFJODN.Question, true); }
            catch (Exception e) { Plugin.Log.LogWarning("Crew password: popup: " + e.Message); }
        }

        private static void Notify(string text)
        {
            Plugin.Log.LogInfo("Crew password: " + text);
            try { UI.PopupMgr.ShowPopup(text.Replace('|', ' '), null, UI.PDHCCBFJODN.Notification, true); }
            catch { /* no popups here */ }
        }

        // After the game's own error popup.
        private static void Later(Action action) => Plugin.Instance.StartCoroutine(After(1.5f, action));

        private static IEnumerator After(float seconds, Action action)
        {
            yield return new WaitForSecondsRealtime(seconds);
            action();
        }
    }
}
