using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SVR;
using SVR.Network;

namespace StbcRevival
{
    /// <summary>
    /// All Harmony patches. Assembly-CSharp is name-obfuscated, so readable names are used where
    /// the obfuscator kept them (Unity messages, public API, property accessors) and obfuscated
    /// types are located structurally at runtime instead of by their scrambled names.
    /// Note: property *definitions* are renamed but their get_/set_ methods are not, so look up
    /// accessor methods, never PropertyInfo by name.
    /// </summary>
    internal static class Patches
    {
        // PUN's PhotonNetwork class (obfuscated as NDIOGIGMPBH in this build).
        private static Type photonNetwork;
        private static FieldInfo photonServerSettings;
        private static MethodInfo getAuthValues;

        internal static void Apply(Harmony harmony)
        {
            Patch(harmony, AccessTools.Method(typeof(ConfigFile), nameof(ConfigFile.GetValueString)),
                prefix: nameof(GetValueString_Prefix));
            Patch(harmony, AccessTools.Method(typeof(ConfigFile), nameof(ConfigFile.ContainsKey)),
                prefix: nameof(ContainsKey_Prefix));

            IntroSkip.Patch(harmony);
            LocalSave.Patch(harmony);
            UbisoftSaveImport.Patch(harmony);

            // A Ubisoft save import run needs the real Ubisoft login.
            if (Plugin.SkipUplay.Value && !UbisoftSaveImport.Active)
                Patch(harmony, AccessTools.Method(typeof(NetworkCore), nameof(NetworkCore.GetLoginMethod)),
                    postfix: nameof(GetLoginMethod_Postfix));

            if (Plugin.Diagnostics.Value)
                foreach (var setError in typeof(NetworkStatus).GetMethods(BindingFlags.Public | BindingFlags.Static)
                             .Where(m => m.Name == nameof(NetworkStatus.SetError)))
                    Patch(harmony, setError, prefix: nameof(SetError_Prefix));
        }

        // ---- Diagnostics -------------------------------------------------------------------

        // Every game-level network error (shown to the player as "connection error" popups and the like).
        private static void SetError_Prefix(object[] __args)
        {
            var detail = __args[0];
            if (detail != null && __args.Length == 1)
            {
                // SetError(PAFNPDODDCM error): describe its enum fields.
                detail = string.Join(", ", detail.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(f => f.FieldType.IsEnum || f.FieldType.IsPrimitive)
                    .Select(f => f.GetValue(__args[0])?.ToString()).ToArray());
            }
            Plugin.Log.LogWarning($"Game network error: {detail} (args: {string.Join(", ", __args.Select(a => a?.ToString() ?? "null").ToArray())})\n{Environment.StackTrace}");
        }

        private static FieldInfo peerField;

        // PhotonNetwork.SwitchToProtocol (UDP -> TCP) replaces the NetworkingPeer with a new one and copies
        // only some fields. The new peer's actor table (mActors) and PhotonView registry start empty, so:
        //  - PhotonPlayer.Find(localId) returns null, the local pawn spawns as "(remote)" without a camera,
        //    and the main menu console never leaves LOADING;
        //  - PhotonViews registered before the switch (persistent managers) can't receive RPCs.
        // Carry both over from the old peer, then let PUN re-register the local actor.
        private static void SwitchToProtocol_Prefix(out object __state) => __state = peerField.GetValue(null);

        private static void SwitchToProtocol_Postfix(object __state)
        {
            var oldPeer = __state;
            var newPeer = peerField.GetValue(null);
            if (oldPeer == null || newPeer == null || ReferenceEquals(oldPeer, newPeer))
                return;
            try
            {
                const BindingFlags inst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                int actors = CopyDictionary(newPeer.GetType().GetField("mActors", inst), oldPeer, newPeer);
                int views = 0;
                foreach (var f in newPeer.GetType().GetFields(inst).Where(f =>
                             f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>)
                             && f.FieldType.GetGenericArguments()[0] == typeof(int)
                             && f.FieldType.GetGenericArguments()[1].Name == "PhotonView"))
                    views += CopyDictionary(f, oldPeer, newPeer);

                var local = newPeer.GetType().GetMethod("get_mLocalActor", inst)?.Invoke(newPeer, null);
                if (local != null)
                {
                    var id = (int)local.GetType().GetMethod("get_ID", inst).Invoke(local, null);
                    newPeer.GetType().GetMethod("ChangeLocalID", inst).Invoke(newPeer, new object[] { id });
                }
                Plugin.Log.LogInfo($"Protocol switch: carried over {actors} actor(s) and {views} PhotonView(s); local actor re-registered");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Protocol switch state carry-over failed: " + e);
            }
        }

        private static int CopyDictionary(FieldInfo field, object from, object to)
        {
            if (field == null) return 0;
            var source = field.GetValue(from) as System.Collections.IDictionary;
            var target = field.GetValue(to) as System.Collections.IDictionary;
            if (source == null || target == null) return 0;
            int n = 0;
            foreach (System.Collections.DictionaryEntry e in source)
            {
                if (target.Contains(e.Key)) continue;
                target[e.Key] = e.Value;
                n++;
            }
            return n;
        }

        private static readonly List<WeakReference> connectedPeers = new List<WeakReference>();

        /// <summary>
        /// PUN creates its peer with the asset's protocol (UDP). Connecting over TCP makes
        /// PhotonNetwork.SwitchToProtocol disconnect that never-connected UDP peer and replace it; the old
        /// peer still reports Disconnect, which PUN forwards as OnDisconnectedFromPhoton and the game turns
        /// into a LostConnectionToPhoton error. A peer that never connected can't lose its connection, so
        /// drop that status. Real disconnects always come from a peer that reached Connect.
        /// </summary>
        private static bool PeerStatus_Prefix(object __instance, object[] __args)
        {
            var status = __args[0]?.ToString();
            if (Plugin.Diagnostics.Value)
                Plugin.Log.LogInfo($"Photon status: {status}");

            connectedPeers.RemoveAll(w => !w.IsAlive);
            bool everConnected = connectedPeers.Any(w => ReferenceEquals(w.Target, __instance));
            if (status == "Connect" && !everConnected)
                connectedPeers.Add(new WeakReference(__instance));
            if (status == "Disconnect" && !everConnected)
            {
                Plugin.Log.LogInfo("Ignoring Disconnect from a peer that never connected (protocol switch)");
                return false;
            }
            return true;
        }

        private static void PeerResponse_Prefix(object[] __args)
        {
            var __0 = __args[0];
            if (__0 == null) return;
            var t = __0.GetType();
            var code = t.GetField("OperationCode")?.GetValue(__0);
            var ret = t.GetField("ReturnCode")?.GetValue(__0);
            var msg = t.GetField("DebugMessage")?.GetValue(__0);
            if (!Equals(ret, (short)0))
                Plugin.Log.LogWarning($"Photon op {code} failed: {ret} {msg}");
            else
                Plugin.Log.LogDebug($"Photon op {code} ok");
        }

        /// <summary>
        /// Must run after the first frame, never from plugin Awake: patching JIT-compiles the target,
        /// which runs PhotonNetwork's static constructor, which calls Resources.Load. During BepInEx's
        /// chainloader that is a native access violation in Unity 5.4. The game only connects after the
        /// intro sequence, so the first frame is still early enough.
        /// </summary>
        internal static void ApplyDeferred(Harmony harmony)
        {
            if (Plugin.Mode.Value != PhotonMode.Default || Plugin.StripSteamAuth.Value)
                PatchPhotonConnect(harmony);
            RoomBrowser.Patch(harmony);
            SteamFriendsPanel.Patch(harmony);
            PauseMenuTweaks.Patch(harmony);
            VrRoomPanels.Patch(harmony);
            Missions.RevivalMissions.Patch(harmony);
            RomulanCreatorOption.Patch(harmony);
            RevivalSettings.Patch(harmony);
            SteamFriendAdd.Patch(harmony);
            RomulanFace.Patch(harmony);
            VoiceCommands.Patch(harmony);
            if (Plugin.Diagnostics.Value)
                StbcRevival.Diagnostics.Apply(harmony, photonNetwork);
        }

        internal static Type PhotonNetworkType => photonNetwork;

        private static void Patch(Harmony harmony, MethodBase target, string prefix = null, string postfix = null)
        {
            if (target == null)
            {
                Plugin.Log.LogError($"Patch target not found for {prefix ?? postfix}; this game build is not supported.");
                return;
            }
            harmony.Patch(target,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(Patches), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(Patches), postfix));
            Plugin.Log.LogDebug($"Patched {target.DeclaringType.FullName}.{target.Name}");
        }

        // ---- Developer config ------------------------------------------------------------

        // static bool ConfigFile.GetValueString(string key, out string value)
        private static bool GetValueString_Prefix(string __0, ref string __1, ref bool __result)
        {
            if (TryGetDevValue(__0, out var value))
            {
                __1 = value;
                __result = true;
                return false;
            }
            return true;
        }

        // static bool ConfigFile.ContainsKey(string key)
        private static bool ContainsKey_Prefix(string __0, ref bool __result)
        {
            if (TryGetDevValue(__0, out _))
            {
                __result = true;
                return false;
            }
            return true;
        }

        private static bool TryGetDevValue(string key, out string value)
        {
            key = key?.ToLower();
            if (key == null)
            {
                value = null;
                return false;
            }
            if (DevConfig.Values.TryGetValue(key, out value))
                return true;
            if (key == "name" && Plugin.SkipUplay.Value)
                return SteamName.TryGet(out value);
            return false;
        }

        // ---- Uplay ----------------------------------------------------------------------

        // NetworkCore.GetLoginMethod() reads the BuildStamp resource's UbiLoginMethod (1 = PCApp
        // in retail). Returning None (0) selects the developers' no-platform path: ConnectionManager
        // skips the Uplay start + Ubisoft login steps and goes straight to Photon, and
        // NetworkStatus treats "connected to Photon" as fully online.
        private static void GetLoginMethod_Postfix(ref DHCMJKJFCMJ __result)
        {
            if (__result != DHCMJKJFCMJ.None)
                Plugin.Log.LogInfo($"Login method {__result} -> None (Uplay skipped)");
            __result = DHCMJKJFCMJ.None;

            // Uplay.OnApplicationQuit calls UplayQuit() unless this is set; Uplay was never started.
            AccessTools.Field(typeof(Uplay), "s_skipQuit")?.SetValue(null, true);
        }

        // ---- Photon ---------------------------------------------------------------------

        private static void PatchPhotonConnect(Harmony harmony)
        {
            photonNetwork = typeof(ServerSettings).Assembly.GetTypes().FirstOrDefault(t =>
                t.GetField("PhotonServerSettings", BindingFlags.Public | BindingFlags.Static)?.FieldType == typeof(ServerSettings)
                && t.GetField("versionPUN", BindingFlags.Public | BindingFlags.Static) != null);
            if (photonNetwork == null)
            {
                Plugin.Log.LogError("Could not locate PhotonNetwork; Photon redirect disabled.");
                return;
            }
            photonServerSettings = photonNetwork.GetField("PhotonServerSettings", BindingFlags.Public | BindingFlags.Static);
            getAuthValues = photonNetwork.GetMethod("get_AuthValues", BindingFlags.Public | BindingFlags.Static);
            Plugin.Log.LogInfo($"PhotonNetwork is {photonNetwork.FullName}, PUN {photonNetwork.GetField("versionPUN").GetValue(null)}");

            // Every connection goes through one of these two (see NetworkCore.ConnectToRegion).
            Patch(harmony, AccessTools.Method(photonNetwork, "ConnectUsingSettings", new[] { typeof(string) }),
                prefix: nameof(Connect_Prefix));
            Patch(harmony, photonNetwork.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "ConnectToRegion" && m.GetParameters().Length == 2),
                prefix: nameof(Connect_Prefix));

            var networkingPeer = FindNetworkingPeer();
            if (networkingPeer == null)
            {
                Plugin.Log.LogError("Could not locate NetworkingPeer; protocol-switch fix disabled.");
            }
            else
            {
                peerField = photonNetwork.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .FirstOrDefault(f => f.FieldType == networkingPeer);
                var switchToProtocol = photonNetwork.GetMethod("SwitchToProtocol", BindingFlags.Public | BindingFlags.Static);
                if (peerField == null || switchToProtocol == null)
                    Plugin.Log.LogError("Could not locate PhotonNetwork.SwitchToProtocol; protocol-switch state fix disabled.");
                else
                    harmony.Patch(switchToProtocol,
                        prefix: new HarmonyMethod(typeof(Patches), nameof(SwitchToProtocol_Prefix)),
                        postfix: new HarmonyMethod(typeof(Patches), nameof(SwitchToProtocol_Postfix)));

                Patch(harmony, networkingPeer.GetMethod("OnStatusChanged", BindingFlags.Public | BindingFlags.Instance), prefix: nameof(PeerStatus_Prefix));
                if (Plugin.Diagnostics.Value)
                    Patch(harmony, networkingPeer.GetMethod("OnOperationResponse", BindingFlags.Public | BindingFlags.Instance), prefix: nameof(PeerResponse_Prefix));
            }

            if (Plugin.Mode.Value == PhotonMode.SelfHosted && Plugin.RewriteGameServerAddress.Value)
            {
                var peerConnect = FindNetworkingPeerConnect();
                if (peerConnect == null)
                    Plugin.Log.LogError("Could not locate NetworkingPeer.Connect; game server address rewrite disabled.");
                else
                    Patch(harmony, peerConnect, prefix: nameof(PeerConnect_Prefix));
            }
        }

        /// <summary>
        /// PUN's NetworkingPeer.Connect(string address, ServerConnection type). NetworkingPeer is the type
        /// of PhotonNetwork's peer field (obfuscated); ServerConnection is the enum with a GameServer member.
        /// </summary>
        internal static Type FindNetworkingPeer() => photonNetwork
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Select(f => f.FieldType)
            .FirstOrDefault(t => t.GetMethod("ReconnectToMaster", BindingFlags.Public | BindingFlags.Instance) != null);

        internal static MethodInfo FindNetworkingPeerConnect()
        {
            return FindNetworkingPeer()?.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m =>
            {
                var p = m.GetParameters();
                return m.Name == "Connect" && p.Length == 2 && p[0].ParameterType == typeof(string)
                       && p[1].ParameterType.IsEnum && Enum.IsDefined(p[1].ParameterType, "GameServer");
            });
        }

        // The master hands out the game server address it sees itself as (behind Docker or NAT that's an
        // internal IP). stbc-server serves both roles on one port, so go back to the configured address.
        private static void PeerConnect_Prefix(object[] __args)
        {
            if (__args[1]?.ToString() != "GameServer")
                return;
            var address = $"{Plugin.Server.Value.Trim()}:{Plugin.Port.Value}";
            if (!Equals(__args[0], address))
                Plugin.Log.LogInfo($"Game server {__args[0]} -> {address}");
            __args[0] = address;
        }

        private static void Connect_Prefix(MethodBase __originalMethod)
        {
            var settings = (ServerSettings)photonServerSettings.GetValue(null);
            if (settings == null)
                return;

            if (Plugin.Mode.Value == PhotonMode.Cloud)
            {
                if (string.IsNullOrEmpty(Plugin.AppId.Value))
                    Plugin.Log.LogError("Photon Mode is Cloud but AppId is empty; set it in BepInEx/config/stbc.revival.cfg.");
                else
                    settings.AppID = Plugin.AppId.Value.Trim();
            }

            if (Plugin.StripSteamAuth.Value)
                StripAuth();

            Plugin.Log.LogInfo($"{__originalMethod.Name}: host={settings.HostType} " +
                (settings.HostType.ToString() == "SelfHosted"
                    ? $"server={settings.ServerAddress}:{settings.ServerPort} ({settings.Protocol})"
                    : $"appId={Mask(settings.AppID)}"));
        }

        // NetworkCore.ConnectToRegion attaches a Steam session ticket as Photon custom auth.
        // A self-hosted server or a fresh cloud app has no Steam auth provider, so clear the
        // auth type but keep the UserId the game assigned.
        private static void StripAuth()
        {
            var auth = getAuthValues?.Invoke(null, null);
            if (auth == null)
                return;
            var setAuthType = auth.GetType().GetMethod("set_AuthType");
            setAuthType?.Invoke(auth, new[] { Enum.Parse(setAuthType.GetParameters()[0].ParameterType, "None") });
            auth.GetType().GetMethod("set_AuthGetParameters")?.Invoke(auth, new object[] { null });
        }

        private static string Mask(string appId) =>
            string.IsNullOrEmpty(appId) || appId.Length < 8 ? appId : appId.Substring(0, 4) + "…" + appId.Substring(appId.Length - 4);
    }

    /// <summary>Steam persona name via the game's bundled (renamed) Steamworks.NET.</summary>
    internal static class SteamName
    {
        private static MethodInfo steamInitialized;
        private static MethodInfo getPersonaName;
        private static bool searched;

        internal static bool SteamInitialized()
        {
            if (!searched) TryGet(out _);
            try { return steamInitialized != null && (bool)steamInitialized.Invoke(null, null); }
            catch { return false; }
        }

        internal static bool TryGet(out string name)
        {
            name = null;
            try
            {
                if (!searched)
                {
                    searched = true;
                    // SteamManager is internal to Assembly-CSharp.
                    steamInitialized = AccessTools.TypeByName("SteamManager")?.GetMethod("get_Initialized", BindingFlags.Public | BindingFlags.Static);
                    getPersonaName = typeof(ServerSettings).Assembly.GetTypes()
                        .Where(t => t.Namespace == "Steamworks")
                        .Select(t => t.GetMethod("GetPersonaName", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null))
                        .FirstOrDefault(m => m != null && m.ReturnType == typeof(string));
                }
                // Asked for early (PlayerManager.Awake); before Steam is up, let the game fall back.
                if (steamInitialized == null || !(bool)steamInitialized.Invoke(null, null))
                    return false;
                name = getPersonaName?.Invoke(null, null) as string;
                return !string.IsNullOrEmpty(name);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not read Steam persona name: " + e.Message);
                return false;
            }
        }
    }
}
