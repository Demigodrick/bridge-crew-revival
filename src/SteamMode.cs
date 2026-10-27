using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ExitGames.Client.Photon;
using HarmonyLib;
using StbcRevival.Net;
using StbcServer;
using StbcServer.Game;
using StbcServer.Net;
using StbcServer.Protocol;
using UnityEngine;
using CSteamID = Steamworks.IMHJDCPKKHM;
using EP2PSend = Steamworks.KKHEAIABKGB;
using ELobbyType = Steamworks.ADPOMIBAHID;
using ELobbyComparison = Steamworks.IPGOENKFFKB;
using ELobbyDistanceFilter = Steamworks.IMGKEIDCPKF;
using EFriendFlags = Steamworks.GLOAPNMOAHC;
using SteamAPICall = Steamworks.MCOKCNDBDFB;
using SteamNetworking = Steamworks.IJILPGFHMJG;  // client SteamNetworking (PCLHPGBGCPN is the game-server one)
using SteamMatchmaking = Steamworks.CFJCNLNHFMD;
using SteamFriends = Steamworks.GJMOCKOJNPH;
using SteamUser = Steamworks.KFPNOEDJHAI;
using P2PSessionRequest = Steamworks.LFNDDKJCKJG;       // P2PSessionRequest_t (1202)
using P2PSessionConnectFail = Steamworks.EBFAJMIDMJK;   // P2PSessionConnectFail_t (1203)
using LobbyCreated = Steamworks.NHMJMIHGCEB;            // LobbyCreated_t (513)
using LobbyMatchList = Steamworks.AHPJHOAIBHH;          // LobbyMatchList_t (510)
using LobbyJoinRequested = Steamworks.NDAMNFEGBFF;      // GameLobbyJoinRequested_t (333)
using P2PSessionState = Steamworks.JCPNDGFHCNC;          // P2PSessionState_t

namespace StbcRevival
{
    /// <summary>
    /// Mode = Steam: no server to run. Every player's game runs the Photon server in-process; the game
    /// connects to it as "local:1". Hosts publish their public rooms as Steam lobbies; everyone lists them
    /// (worldwide) into the local master's room directory, so the game's room list, quick join and join by
    /// code see them, pointing at "steam:&lt;host&gt;:1". Those connections are relayed over Steam P2P to the
    /// host's in-game server.
    /// </summary>
    internal static class SteamMode
    {
        internal const string LobbyTag = "stbc";
        internal const string LobbyProtocol = "1";
        private const int ToHostChannel = 1, ToClientChannel = 2;

        internal static bool Enabled => Plugin.Mode.Value == PhotonMode.Steam;

        internal static LoadBalancingServer Server { get; private set; }
        private static RelayHost relayHost;
        private static RelayClient relayClient;
        private static readonly SteamRoomDirectory directory = new SteamRoomDirectory();
        private static readonly Queue<KeyValuePair<ulong, byte[]>>[] outbox = { new Queue<KeyValuePair<ulong, byte[]>>(), new Queue<KeyValuePair<ulong, byte[]>>() };
        private static float nextTick;
        private static bool steamReady;

        /// <summary>Steam ID of the host of the room we're connected to (ourselves when hosting).</summary>
        // The host we're connected through: ourselves ("local", read live: our Steam ID is 0 until Steam is
        // ready, which can be after the game connects), or a remote host's Steam ID.
        internal static ulong CurrentHost => hostIsLocal ? MySteamId : remoteHost;
        private static bool hostIsLocal;
        private static ulong remoteHost;

        internal static ulong MySteamId => steamReady ? SteamUser.GetSteamID().m_SteamID : 0;

        // ---- Startup ------------------------------------------------------------------------

        /// <summary>From plugin Awake: the server and socket hook must exist before the game connects.</summary>
        internal static void Install(Harmony harmony)
        {
            if (!Enabled) return;
            Log.Sink = line => Plugin.Log.LogInfo("[server] " + line);
            Log.Level = LogLevel.Info;

            // Two minutes before a silent player is dropped: Steam traffic only moves on the main thread, which
            // stands still while a level loads (a slow PC's mission load can pass the usual 30 s).
            Server = new LoadBalancingServer(new ServerConfig { Directory = directory, IdleTimeoutMs = 120000 });
            Server.IsBlocked = Blocklist.IsBlocked; // players you've blocked (here or in Steam) can't join your crews
            relayHost = new RelayHost(Server, () => new PhotonCrypto(), (to, p) => Enqueue(ToClientChannel, to, p));
            relayClient = new RelayClient((to, p) => Enqueue(ToHostChannel, to, p));

            VirtualSockets.Install(harmony);
            VirtualSockets.Connect = Connect;
            Plugin.Log.LogInfo("Steam mode: in-game server ready (local:1)");
        }

        private static IClientLink Connect(string host, Action<byte[]> deliver, Action closed)
        {
            if (host == "local")
            {
                hostIsLocal = true;
                return LocalLink.Connect(Server, () => new PhotonCrypto(), "local:1", deliver, closed);
            }
            if (host.StartsWith("steam:") && ulong.TryParse(host.Substring(6), out var steamId))
            {
                if (!steamReady)
                {
                    Plugin.Log.LogWarning("Steam mode: Steam isn't ready; can't reach " + host);
                    return null;
                }
                hostIsLocal = false;
                remoteHost = steamId;
                Plugin.Log.LogInfo($"Steam mode: connecting to host {steamId} over Steam P2P");
                // Closed from the host's end (their game went, or Steam lost them): the crew may regroup.
                return relayClient.Connect(steamId, deliver, () => { HostHandoff.HostLinkLost(steamId); closed(); });
            }
            return null;
        }

        // ---- Per frame -----------------------------------------------------------------------

        internal static void Update()
        {
            if (!Enabled || Server == null) return;
            if (!steamReady && SteamName.SteamInitialized()) InitSteam();
            if (steamReady)
            {
                Poll(ToHostChannel, relayHost.OnPacket);
                Poll(ToClientChannel, relayClient.OnPacket);
                Flush();
                PendingLobbyJoin();
                LinkReport();
            }
            LongerClientTimeout();
            if (Time.realtimeSinceStartup >= nextTick)
            {
                nextTick = Time.realtimeSinceStartup + 1f;
                Server.Tick();
                if (steamReady) directory.Tick(Server);
            }
        }

        private static Steamworks.KAFDLCLDHJI<P2PSessionRequest> sessionRequestCallback;
        private static Steamworks.KAFDLCLDHJI<P2PSessionConnectFail> sessionFailCallback;

        private static void InitSteam()
        {
            steamReady = true;
            try
            {
                sessionRequestCallback = Steamworks.KAFDLCLDHJI<P2PSessionRequest>.Create(r =>
                {
                    // Anyone may connect; what they can do is decided by the server (rooms, codes).
                    SteamNetworking.AcceptP2PSessionWithUser(r.m_steamIDRemote);
                });
                sessionFailCallback = Steamworks.KAFDLCLDHJI<P2PSessionConnectFail>.Create(f =>
                {
                    var peer = f.m_steamIDRemote.m_SteamID;
                    Plugin.Log.LogWarning($"Steam mode: P2P session with {peer} failed (error {f.m_eP2PSessionError})");
                    relayHost.PeerLost(peer);
                    relayClient.PeerLost(peer);
                });
                Plugin.Log.LogInfo($"Steam mode: Steam ready, my Steam ID is {MySteamId}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Steam mode: Steam callbacks failed: " + e);
            }
        }

        private static readonly byte[] receiveBuffer = new byte[1024 * 1024];

        private static void Poll(int channel, Action<ulong, byte[], int> handle)
        {
            for (int i = 0; i < 500 && SteamNetworking.IsP2PPacketAvailable(out uint size, channel); i++)
            {
                if (size > receiveBuffer.Length) { Plugin.Log.LogWarning($"Steam mode: dropping {size}-byte packet"); continue; }
                if (!SteamNetworking.ReadP2PPacket(receiveBuffer, size, out uint read, out CSteamID from, channel)) break;
                var packet = new byte[read];
                Buffer.BlockCopy(receiveBuffer, 0, packet, 0, (int)read);
                Link(from.m_SteamID).Received(read);
                try { handle(from.m_SteamID, packet, packet.Length); }
                catch (Exception e) { Plugin.Log.LogError("Steam mode: packet handling failed: " + e); }
            }
        }

        // Photon can send from its background thread; Steam calls happen on the main thread only.
        private static void Enqueue(int channel, ulong to, byte[] packet)
        {
            var q = outbox[channel - 1];
            lock (q) q.Enqueue(new KeyValuePair<ulong, byte[]>(to, packet));
        }

        private static void Flush()
        {
            for (int channel = ToHostChannel; channel <= ToClientChannel; channel++)
            {
                var q = outbox[channel - 1];
                while (true)
                {
                    KeyValuePair<ulong, byte[]> item;
                    lock (q)
                    {
                        if (q.Count == 0) break;
                        item = q.Dequeue();
                    }
                    if (!SteamNetworking.SendP2PPacket(new CSteamID(item.Key), item.Value, (uint)item.Value.Length, EP2PSend.k_EP2PSendReliable, channel))
                        Plugin.Log.LogWarning($"Steam mode: send to {item.Key} failed");
                    else Link(item.Key).Sent(item.Value.Length);
                }
            }
        }

        // ---- Rooms known from invites / friends ------------------------------------------------

        /// <summary>Make a room joinable by name: it's hosted by this Steam user (from an invite or friend).</summary>
        internal static void RememberRoom(string roomName, ulong host) => directory.Remember(roomName, host);

        /// <summary>This room is now hosted by this Steam user, whatever the lobby search still says (host hand-off).</summary>
        internal static void RedirectRoom(string roomName, ulong host) => directory.Redirect(roomName, host);

        private static string CodeKey(string code) => (code ?? "").Trim().ToUpperInvariant();

        // ---- Share codes ---------------------------------------------------------------------------
        // A room code with its region and host: "YZZDK9-EU-1Q2W3E4". Whoever has it connects to that host
        // directly, so a private crew needs no Steam listing and no Steam friendship; nothing about it is
        // published. (The host part is the Steam account number in Crockford base 32.)

        private const ulong IndividualBase = 76561197960265728UL; // Steam IDs of individual accounts start here
        private const string Base32 = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        /// <summary>The code to share for a room this player is in (hosting or not), or its plain code.</summary>
        internal static string ShareCode(string roomName)
        {
            var code = RoomBrowser.Code(roomName);
            ulong host = Server.HostedRooms().Any(r => r.Name == roomName) ? MySteamId : CurrentHost;
            if (host <= IndividualBase || host - IndividualBase > uint.MaxValue || !roomName.Contains("|")) return code;
            var region = roomName.Substring(0, roomName.IndexOf('|'));
            uint account = (uint)(host - IndividualBase);
            var digits = new char[7];
            for (int i = 6; i >= 0; i--, account >>= 5) digits[i] = Base32[(int)(account & 31)];
            return $"{code}-{region.ToUpperInvariant()}-{new string(digits)}";
        }

        /// <summary>A share code's room name and host; false if it isn't one.</summary>
        internal static bool ParseShareCode(string input, out string roomName, out ulong host)
        {
            roomName = null;
            host = 0;
            var parts = (input ?? "").Trim().ToUpperInvariant().Split('-');
            if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length == 0 || parts[2].Length != 7) return false;
            ulong account = 0;
            foreach (var c in parts[2].Replace('O', '0').Replace('I', '1').Replace('L', '1'))
            {
                int digit = Base32.IndexOf(c);
                if (digit < 0) return false;
                account = (account << 5) | (uint)digit;
            }
            if (account > uint.MaxValue) return false;
            host = IndividualBase + account;
            roomName = parts[1].ToLowerInvariant() + "|" + parts[0];
            return true;
        }

        /// <summary>
        /// A public crew found by its room code in the last Steam search, on any mod version: its
        /// full room name and host are remembered for joining when it's on our version. Null if none.
        /// </summary>
        internal static string FindByCodeAnyVersion(string code, out string version)
        {
            version = null;
            var entry = steamReady ? directory.ByCode(code) : null;
            if (entry == null) return null;
            version = string.IsNullOrEmpty(entry.Mod) ? "an older version" : entry.Mod;
            var name = entry.Name;
            if (entry.Mod == Plugin.Version) RememberRoom(name, entry.Host);
            return name;
        }

        internal static bool Knows(string roomName) => directory.Rooms.Any(r => r.Name == roomName) || Server.HostedRooms().Any(r => r.Name == roomName);

        /// <summary>
        /// The full name ("eu|K7QX2M") of a room by its code alone, whatever region its host had set: among the public
        /// crews, our own rooms and our Steam friends' rooms. Null if none.
        /// </summary>
        internal static string FindByCode(string code)
        {
            bool Same(string name) => name != null && string.Equals(RoomBrowser.Code(name), code, StringComparison.OrdinalIgnoreCase);
            var known = directory.Rooms.Select(r => r.Name).Concat(Server.HostedRooms().Select(r => r.Name)).FirstOrDefault(Same);
            if (known != null || !steamReady) return known;
            try
            {
                int n = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
                for (int i = 0; i < n; i++)
                {
                    var friend = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                    var parts = (SteamFriends.GetFriendRichPresence(friend, "connect") ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int j = 0; j < parts.Length - 1; j++)
                        if (parts[j] == SteamIntegration.JoinArg && Same(parts[j + 1]))
                            return parts[j + 1];
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Steam mode: friend lookup failed: " + e.Message);
            }
            return null;
        }

        /// <summary>Find a friend whose rich presence advertises this room (private matches have no lobby).</summary>
        internal static bool ResolveFromFriends(string roomName)
        {
            if (!steamReady) return false;
            try
            {
                int n = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
                for (int i = 0; i < n; i++)
                {
                    var friend = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                    var connect = SteamFriends.GetFriendRichPresence(friend, "connect") ?? "";
                    if (connect.IndexOf(SteamIntegration.JoinArg + " " + roomName, StringComparison.Ordinal) < 0) continue;
                    var host = SteamIntegration.HostFromConnect(connect) ?? friend.m_SteamID;
                    RememberRoom(roomName, host);
                    return true;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Steam mode: friend lookup failed: " + e.Message);
            }
            return false;
        }

        internal static ulong CurrentLobby => directory.MyLobby;

        /// <summary>Bridge Crew Revival players online (in the shared "online" Steam lobbies), or 0 while unknown.</summary>
        internal static int OnlineCount => directory.OnlineCount;

        /// <summary>"12 players online", or "" while unknown: for the crew lists.</summary>
        internal static string OnlineText => Enabled && OnlineCount > 0 ? $"{OnlineCount} Revival player{(OnlineCount == 1 ? "" : "s")} online" : "";

        // ---- For the room browser -------------------------------------------------------------

        internal sealed class Listing
        {
            public string Name;
            public Dictionary<object, object> Properties;
            public int Players, MaxPlayers;
            public bool Open, Mine;
        }

        /// <summary>Public rooms: everyone's Steam lobbies, plus the one this player hosts (once published).</summary>
        internal static List<Listing> PublicRooms()
        {
            var list = new List<Listing>();
            if (directory == null) return list;
            var mine = directory.PublishedRoom;
            if (mine != null && Server.HostedRooms().FirstOrDefault(r => r.Name == mine) is { } own)
                list.Add(new Listing
                {
                    Name = own.Name,
                    Properties = own.CustomProperties.ToDictionary(kv => (object)kv.Key, kv => kv.Value),
                    Players = own.Players, MaxPlayers = own.MaxPlayers, Open = own.Open, Mine = true,
                });
            foreach (var r in directory.Lobbies)
                list.Add(new Listing
                {
                    Name = r.Name,
                    Properties = r.Properties.Keys.ToDictionary(k => k, k => r.Properties[k]),
                    Players = r.Players, MaxPlayers = r.MaxPlayers, Open = r.IsOpen,
                });
            return list;
        }

        /// <summary>How the room this player is in can be reached, for the room browser.</summary>
        internal static string RoomStatus(string roomName, bool visible)
        {
            bool hosting = Server.HostedRooms().Any(r => r.Name == roomName);
            if (!hosting)
            {
                var host = CurrentHost != 0 && steamReady ? SteamFriends.GetFriendPersonaName(new CSteamID(CurrentHost)) : null;
                return $"Hosted by {(string.IsNullOrEmpty(host) ? "another player" : host)} · {(visible ? "public" : "private")}";
            }
            if (!visible)
                return "You're hosting · PRIVATE: only Steam invites, Join Game and your share code get in";
            return directory.PublishedRoom == roomName && directory.MyLobby != 0
                ? "You're hosting · PUBLIC: listed on Steam, Quick match players can join"
                : "You're hosting · PUBLIC: publishing to Steam…";
        }

        // ---- Link quality (the log, for choppy voice and lag) ------------------------------------------
        // Everything goes over Steam reliably and in order, so a delayed or lost packet holds up all behind it
        // (voice too). Per player: direct or relayed by Steam's servers, traffic, and Steam's send queue (it
        // grows while Steam re-sends or the link can't keep up); for a crew member, Photon's round trip.

        private sealed class LinkStats
        {
            public int SentPackets, ReceivedPackets, PeakQueue;
            public long SentBytes, ReceivedBytes;
            public float LastSeen;
            public bool? Relayed;
            public void Sent(int bytes) { SentPackets++; SentBytes += bytes; LastSeen = Time.realtimeSinceStartup; }
            public void Received(uint bytes) { ReceivedPackets++; ReceivedBytes += bytes; LastSeen = Time.realtimeSinceStartup; }
        }

        private static readonly Dictionary<ulong, LinkStats> links = new Dictionary<ulong, LinkStats>();
        private static float nextLinkSample, nextLinkReport;
        private const float LinkReportSeconds = 30f;

        private static LinkStats Link(ulong peer)
        {
            if (!links.TryGetValue(peer, out var link)) links[peer] = link = new LinkStats();
            return link;
        }

        private static void LinkReport()
        {
            float now = Time.realtimeSinceStartup;
            if (now < nextLinkSample) return;
            nextLinkSample = now + 2f;
            bool report = now >= nextLinkReport;
            if (report) nextLinkReport = now + LinkReportSeconds;
            foreach (var kv in links.ToList())
            {
                var link = kv.Value;
                if (now - link.LastSeen > LinkReportSeconds * 2) { links.Remove(kv.Key); continue; }
                if (!SteamNetworking.GetP2PSessionState(new CSteamID(kv.Key), out P2PSessionState state)) continue;
                link.PeakQueue = Math.Max(link.PeakQueue, state.m_nPacketsQueuedForSend);
                bool relayed = state.m_bUsingRelay != 0;
                if (link.Relayed != relayed)
                {
                    link.Relayed = relayed;
                    Plugin.Log.LogInfo($"Steam link with {kv.Key}: {(relayed ? "relayed through Steam's servers" : "direct")}");
                }
                if (!report) continue;
                string rtt = kv.Key == CurrentHost && !hostIsLocal ? $", Photon round trip {RoundTrip()}" : "";
                Plugin.Log.LogInfo($"Steam link with {kv.Key}: {(relayed ? "relayed" : "direct")}, last {LinkReportSeconds:0} s "
                                   + $"sent {link.SentPackets} packets ({link.SentBytes / 1024} KB), received {link.ReceivedPackets} ({link.ReceivedBytes / 1024} KB); "
                                   + $"Steam send queue {state.m_nPacketsQueuedForSend} packets ({state.m_nBytesQueuedForSend / 1024} KB), peak {link.PeakQueue}{rtt}");
                link.SentPackets = link.ReceivedPackets = link.PeakQueue = 0;
                link.SentBytes = link.ReceivedBytes = 0;
            }
        }

        private static string RoundTrip()
        {
            try
            {
                var peerField = Patches.PhotonNetworkType?.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .FirstOrDefault(f => typeof(PhotonPeer).IsAssignableFrom(f.FieldType));
                return peerField?.GetValue(null) is PhotonPeer peer ? $"{peer.RoundTripTime} ms ± {peer.RoundTripTimeVariance}" : "unknown";
            }
            catch (Exception) { return "unknown"; }
        }

        private static float nextTimeoutCheck;

        // Our Photon client waits a minute before giving up on the host: the host's server is part of its game,
        // and silent while the host loads a level (PUN's own default gives up after about ten seconds).
        private static void LongerClientTimeout()
        {
            if (Time.realtimeSinceStartup < nextTimeoutCheck) return;
            nextTimeoutCheck = Time.realtimeSinceStartup + 2f;
            try
            {
                var photon = Patches.PhotonNetworkType;
                var peerField = photon?.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .FirstOrDefault(f => typeof(PhotonPeer).IsAssignableFrom(f.FieldType));
                if (peerField?.GetValue(null) is PhotonPeer peer && peer.DisconnectTimeout < ClientTimeoutMs)
                {
                    peer.DisconnectTimeout = ClientTimeoutMs;
                    Plugin.Log.LogInfo($"Steam mode: Photon disconnect timeout {ClientTimeoutMs / 1000} s");
                }
            }
            catch (Exception e)
            {
                nextTimeoutCheck = float.MaxValue; // once is enough to know it won't work
                Plugin.Log.LogWarning("Steam mode: couldn't lengthen the Photon timeout: " + e.Message);
            }
        }

        private const int ClientTimeoutMs = 60000;

        // ---- Lobby join requests (Steam overlay "Invite to lobby" / "Join game" on a lobby) ------

        private static Steamworks.KAFDLCLDHJI<LobbyJoinRequested> lobbyJoinCallback;
        private static Action<string, ulong> lobbyJoin;
        private static CSteamID pendingLobby;
        private static float pendingLobbyUntil;

        // A lobby we haven't searched for has no data or owner yet: Steam fetches them on RequestLobbyData,
        // and PendingLobbyJoin (in Update) joins once they're in, giving up after ten seconds.
        internal static void RegisterLobbyJoin(Action<string, ulong> join)
        {
            lobbyJoin = join;
            lobbyJoinCallback = Steamworks.KAFDLCLDHJI<LobbyJoinRequested>.Create(r =>
            {
                pendingLobby = r.m_steamIDLobby;
                pendingLobbyUntil = Time.realtimeSinceStartup + 10f;
                if (!TryLobbyJoin())
                {
                    SteamMatchmaking.RequestLobbyData(r.m_steamIDLobby);
                    Plugin.Log.LogInfo($"Steam mode: lobby join request for {r.m_steamIDLobby.m_SteamID}; fetching its details");
                }
            });
        }

        // A crew lobby's host. Steam's own record of the owner wins whenever Steam gives it (only to the lobby's
        // members); otherwise the "host" the owner wrote into the lobby's data, which anyone can read. Only the
        // owner can write lobby data, so at worst an advertiser points joiners at another account, whose game
        // refuses a room it doesn't host. 0 when unknown.
        private static ulong LobbyHost(CSteamID lobby)
        {
            ulong owner = SteamMatchmaking.GetLobbyOwner(lobby).m_SteamID;
            if (owner != 0) return owner;
            return ulong.TryParse(SteamMatchmaking.GetLobbyData(lobby, "host"), out var host) ? host : 0;
        }

        private static bool TryLobbyJoin()
        {
            var room = SteamMatchmaking.GetLobbyData(pendingLobby, "room");
            var owner = LobbyHost(pendingLobby);
            if (string.IsNullOrEmpty(room) || owner == 0) return false;
            Plugin.Log.LogInfo($"Steam mode: lobby join request for '{room}' hosted by {owner}");
            pendingLobbyUntil = 0;
            lobbyJoin?.Invoke(room, owner);
            return true;
        }

        private static void PendingLobbyJoin()
        {
            if (pendingLobbyUntil == 0) return;
            if (Time.realtimeSinceStartup > pendingLobbyUntil)
            {
                pendingLobbyUntil = 0;
                Plugin.Log.LogWarning("Steam mode: couldn't get the details of the lobby to join");
                RoomBrowser.Show("Couldn't reach that crew's lobby on Steam. Try again, or join with their room code.");
                return;
            }
            TryLobbyJoin();
        }

        // ---- Lobbies --------------------------------------------------------------------------

        /// <summary>
        /// Publishes the public room this player hosts as a Steam lobby, and lists everyone's lobbies
        /// (worldwide, this mod's protocol only) as external rooms for the in-game master server.
        /// Lobby data carries the Photon room properties with type prefixes ("i:8", "s:eu|K7QX2M").
        /// </summary>
        private sealed class SteamRoomDirectory : IRoomDirectory
        {
            private readonly object sync = new object();
            private List<ExternalRoom> lobbies = new List<ExternalRoom>();
            private readonly Dictionary<string, ulong> remembered = new Dictionary<string, ulong>();
            private Steamworks.KLNNLNGKHKC<LobbyCreated> createResult;
            private Steamworks.KLNNLNGKHKC<LobbyMatchList> listResult;
            private bool creating, listing;
            private string publishedRoom, lastData;
            private float nextList;

            public ulong MyLobby { get; private set; }

            /// <summary>The hosted room currently advertised in <see cref="MyLobby"/>.</summary>
            public string PublishedRoom => MyLobby != 0 ? publishedRoom : null;

            /// <summary>Other players' public rooms from the last lobby search.</summary>
            public List<ExternalRoom> Lobbies
            {
                get { lock (sync) return new List<ExternalRoom>(lobbies); }
            }

            public IList<ExternalRoom> Rooms
            {
                get
                {
                    lock (sync)
                    {
                        // Rooms known from an invite, a friend or a hand-off are only needed while joining: after
                        // a while they'd be dead crews in the lists.
                        foreach (var old in noted.Where(kv => DateTime.UtcNow - kv.Value > NoteLifetime).Select(kv => kv.Key).ToList())
                        {
                            noted.Remove(old);
                            remembered.Remove(old);
                            redirected.Remove(old);
                        }
                        var all = lobbies.Where(r => !redirected.ContainsKey(r.Name)).ToList();
                        foreach (var kv in redirected)
                            all.Add(new ExternalRoom(kv.Key, $"steam:{kv.Value}:1", "", new PHashtable { [GameProp.IsOpen] = true }, 0));
                        foreach (var kv in remembered)
                            if (all.All(r => r.Name != kv.Key))
                                all.Add(new ExternalRoom(kv.Key, $"steam:{kv.Value}:1", "",
                                    new PHashtable { [GameProp.IsOpen] = true }, 0));
                        return all;
                    }
                }
            }

            internal sealed class CodeEntry
            {
                public string Name, Mod;
                public ulong Host;
            }

            private Dictionary<string, CodeEntry> codeIndex = new Dictionary<string, CodeEntry>();

            /// <summary>A public crew by room code from the last search (any version), or null.</summary>
            public CodeEntry ByCode(string code)
            {
                lock (sync) return codeIndex.TryGetValue(CodeKey(code), out var entry) ? entry : null;
            }

            private static readonly TimeSpan NoteLifetime = TimeSpan.FromMinutes(10);
            private readonly Dictionary<string, DateTime> noted = new Dictionary<string, DateTime>();

            public void Remember(string room, ulong host)
            {
                lock (sync)
                {
                    remembered[room] = host;
                    noted[room] = DateTime.UtcNow;
                }
            }

            // Rooms that moved to a new host: these win over lobby search results (the old host's lobby can
            // linger on Steam for a while after they drop).
            private readonly Dictionary<string, ulong> redirected = new Dictionary<string, ulong>();

            public void Redirect(string room, ulong host)
            {
                lock (sync)
                {
                    redirected[room] = host;
                    noted[room] = DateTime.UtcNow;
                }
            }

            public void Tick(LoadBalancingServer server)
            {
                Publish(server);
                if (listing) return;
                // One search at a time (Steam keeps one result list): the players-online search takes a turn
                // every 30 seconds, the crew search the rest.
                if (Time.realtimeSinceStartup >= nextOnline)
                {
                    nextOnline = Time.realtimeSinceStartup + 30f;
                    RequestOnline();
                }
                else if (Time.realtimeSinceStartup >= nextList)
                {
                    nextList = Time.realtimeSinceStartup + 5f;
                    RequestList();
                }
            }

            // ---- Players online -----------------------------------------------------------------
            // Every player's game sits in one shared public Steam lobby tagged "stbcOnline" (members up to 250;
            // Steam hands it on when its owner leaves). Its member count is the number of players online.
            // If two get made at once (or one fills), everyone drifts to the biggest, and counts are summed.

            private const string OnlineTag = "stbcOnline";
            private const int OnlineMax = 250;
            private float nextOnline = 10f;
            private ulong onlineLobby;
            private int otherOnline;
            private bool creatingOnline;
            private Steamworks.KLNNLNGKHKC<LobbyCreated> onlineCreateResult;

            public int OnlineCount
            {
                get
                {
                    int mine = 0;
                    try { if (onlineLobby != 0) mine = SteamMatchmaking.GetNumLobbyMembers(new CSteamID(onlineLobby)); }
                    catch { /* Steam not ready */ }
                    return mine + otherOnline;
                }
            }

            private void RequestOnline()
            {
                listing = true;
                SteamMatchmaking.AddRequestLobbyListStringFilter(OnlineTag, "1", ELobbyComparison.k_ELobbyComparisonEqual);
                SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
                var call = SteamMatchmaking.RequestLobbyList();
                listResult = Steamworks.KLNNLNGKHKC<LobbyMatchList>.Create((r, ioFailure) =>
                {
                    listing = false;
                    if (ioFailure) return;
                    ulong best = 0;
                    int bestCount = -1, others = 0;
                    for (int i = 0; i < r.m_nLobbiesMatching; i++)
                    {
                        var lobby = SteamMatchmaking.GetLobbyByIndex(i);
                        int count = SteamMatchmaking.GetNumLobbyMembers(lobby);
                        if (lobby.m_SteamID != onlineLobby) others += count;
                        if (count < OnlineMax && (count > bestCount || (count == bestCount && lobby.m_SteamID < best)))
                        {
                            best = lobby.m_SteamID;
                            bestCount = count;
                        }
                    }
                    otherOnline = others;
                    int ours = onlineLobby != 0 ? SteamMatchmaking.GetNumLobbyMembers(new CSteamID(onlineLobby)) : 0;
                    if (onlineLobby != 0 && ours > 0 && (best == 0 || best == onlineLobby || bestCount <= ours)) return;

                    // Not in one (or ours is the smaller): move to the biggest, or start one.
                    if (onlineLobby != 0)
                    {
                        SteamMatchmaking.LeaveLobby(new CSteamID(onlineLobby));
                        onlineLobby = 0;
                    }
                    if (best != 0)
                    {
                        SteamMatchmaking.JoinLobby(new CSteamID(best));
                        onlineLobby = best;
                        otherOnline = Math.Max(0, others - bestCount);
                        Plugin.Log.LogInfo($"Steam mode: joined the players-online lobby ({bestCount + 1} online there)");
                    }
                    else CreateOnline();
                });
                listResult.Set(call, null);
            }

            private void CreateOnline()
            {
                if (creatingOnline) return;
                creatingOnline = true;
                var call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, OnlineMax);
                onlineCreateResult = Steamworks.KLNNLNGKHKC<LobbyCreated>.Create((r, ioFailure) =>
                {
                    creatingOnline = false;
                    if (ioFailure || r.m_ulSteamIDLobby == 0)
                    {
                        Plugin.Log.LogWarning($"Steam mode: players-online lobby creation failed ({r.m_eResult})");
                        return;
                    }
                    onlineLobby = r.m_ulSteamIDLobby;
                    SteamMatchmaking.SetLobbyData(new CSteamID(onlineLobby), OnlineTag, "1");
                    Plugin.Log.LogInfo($"Steam mode: started the players-online lobby {onlineLobby}");
                });
                onlineCreateResult.Set(call, null);
            }

            private void Publish(LoadBalancingServer server)
            {
                // The crew this player is in, if it's public. Not just any public room on their server: a crew they
                // left can linger there (a guest's connection not yet timed out) and was advertised instead of the
                // one they were in. Private crews are never listed: a short room code (or any hash of it) is
                // guessed in seconds; friends find them through Steam presence.
                var current = GameState.RoomName();
                var room = current == null ? null : server.HostedRooms().FirstOrDefault(r => r.Name == current && r.Visible && r.Players > 0);
                if (room == null)
                {
                    if (MyLobby != 0)
                    {
                        SteamMatchmaking.LeaveLobby(new CSteamID(MyLobby));
                        Plugin.Log.LogInfo($"Steam mode: closed lobby for {publishedRoom}");
                        MyLobby = 0;
                        publishedRoom = null;
                        lastData = null;
                    }
                    return;
                }
                if (MyLobby == 0)
                {
                    if (creating) return;
                    creating = true;
                    // Room for many members, though nobody joins it (players connect to the host, not the lobby): Steam's
                    // lobby search leaves out full lobbies, so with room for only its owner it was never found.
                    var call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, 250);
                    createResult = Steamworks.KLNNLNGKHKC<LobbyCreated>.Create((r, ioFailure) =>
                    {
                        creating = false;
                        if (ioFailure || r.m_ulSteamIDLobby == 0)
                        {
                            Plugin.Log.LogWarning($"Steam mode: lobby creation failed ({r.m_eResult})");
                            return;
                        }
                        MyLobby = r.m_ulSteamIDLobby;
                        lastData = null;
                        Plugin.Log.LogInfo($"Steam mode: lobby {MyLobby} created");
                    });
                    createResult.Set(call, null);
                    return;
                }
                // Lobby data: room name, version, counts, and the listed room properties.
                var data = new Dictionary<string, string>
                {
                    [LobbyTag] = LobbyProtocol,
                    ["room"] = room.Name,
                    ["ver"] = room.AppVersion,
                    ["mod"] = Plugin.Version, // searches only find crews on the searcher's mod version
                    ["players"] = room.Players.ToString(),
                    ["max"] = room.MaxPlayers.ToString(),
                    ["open"] = room.Open ? "1" : "0",
                    ["lock"] = server.IsPassworded(room.Name) ? "1" : "0", // a crew password (Quick Match skips it)
                    // Who to connect to: Steam only tells a lobby's members who owns it, and nobody joins these.
                    ["host"] = MySteamId.ToString(),
                };
                foreach (var kv in room.CustomProperties)
                    if (kv.Key.Length <= 4 && Encode(kv.Value) is string v) data[kv.Key] = v;
                var signature = string.Join(";", data.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value).ToArray());
                if (signature == lastData) return;
                var lobby = new CSteamID(MyLobby);
                foreach (var kv in data) SteamMatchmaking.SetLobbyData(lobby, kv.Key, kv.Value);
                lastData = signature;
                if (publishedRoom != room.Name)
                {
                    publishedRoom = room.Name;
                    Plugin.Log.LogInfo($"Steam mode: listing {room.Name} in Steam lobby {MyLobby}");
                }
            }

            private void RequestList()
            {
                listing = true;
                // Every version (lists show only crews on ours; room codes find the rest, to say why they can't join).
                SteamMatchmaking.AddRequestLobbyListStringFilter(LobbyTag, LobbyProtocol, ELobbyComparison.k_ELobbyComparisonEqual);
                SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
                SteamMatchmaking.AddRequestLobbyListResultCountFilter(200);
                var call = SteamMatchmaking.RequestLobbyList();
                listResult = Steamworks.KLNNLNGKHKC<LobbyMatchList>.Create((r, ioFailure) =>
                {
                    listing = false;
                    if (ioFailure) return;
                    var found = new List<ExternalRoom>();
                    var codes = new Dictionary<string, CodeEntry>();
                    ulong me = MySteamId;
                    for (int i = 0; i < r.m_nLobbiesMatching; i++)
                    {
                        var lobby = SteamMatchmaking.GetLobbyByIndex(i);
                        var owner = LobbyHost(lobby);
                        if (owner == 0 || owner == me) continue; // 0: a host we can't reach
                        var mod = SteamMatchmaking.GetLobbyData(lobby, "mod");
                        var name = SteamMatchmaking.GetLobbyData(lobby, "room");
                        if (string.IsNullOrEmpty(name)) continue;
                        codes[CodeKey(RoomBrowser.Code(name))] = new CodeEntry { Name = name, Host = owner, Mod = mod };
                        if (mod != Plugin.Version) continue; // lists: only crews on our version
                        var props = new PHashtable();
                        int count = SteamMatchmaking.GetLobbyDataCount(lobby);
                        for (int k = 0; k < count; k++)
                        {
                            if (!SteamMatchmaking.GetLobbyDataByIndex(lobby, k, out string key, 256, out string value, 8192)) continue;
                            if (key.StartsWith("C") || key == "NPID")
                            {
                                var decoded = Decode(value);
                                if (decoded != null) props[key] = decoded;
                            }
                        }
                        int.TryParse(SteamMatchmaking.GetLobbyData(lobby, "max"), out int max);
                        int.TryParse(SteamMatchmaking.GetLobbyData(lobby, "players"), out int players);
                        props[GameProp.MaxPlayers] = (byte)Math.Max(0, Math.Min(max, 255));
                        props[GameProp.IsOpen] = SteamMatchmaking.GetLobbyData(lobby, "open") != "0";
                        if (SteamMatchmaking.GetLobbyData(lobby, "lock") == "1") props[ModProp.Locked] = true;
                        props[GameProp.PropsListedInLobby] = props.Keys.OfType<string>().ToArray();
                        found.Add(new ExternalRoom(name, $"steam:{owner}:1", SteamMatchmaking.GetLobbyData(lobby, "ver"), props, players));
                    }
                    lock (sync)
                    {
                        lobbies = found;
                        codeIndex = codes;
                    }
                });
                listResult.Set(call, null);
            }

            private static string Encode(object value) => value switch
            {
                int i => "i:" + i,
                byte b => "i:" + b,
                short s => "i:" + s,
                bool b => "b:" + (b ? 1 : 0),
                string s => "s:" + s,
                _ => null,
            };

            private static object Decode(string value)
            {
                if (value == null || value.Length < 2 || value[1] != ':') return null;
                var rest = value.Substring(2);
                switch (value[0])
                {
                    case 'i': return int.TryParse(rest, out int i) ? (object)i : null;
                    case 'b': return rest == "1";
                    case 's': return rest;
                    default: return null;
                }
            }
        }
    }

    /// <summary>
    /// Server side of Photon's key exchange using Photon's own (internal) DiffieHellmanCryptoProvider:
    /// the same group, key derivation and AES as the client, so no big-integer code is needed in-game.
    /// </summary>
    internal sealed class PhotonCrypto : ICryptoSession
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly Type ProviderType = typeof(PhotonPeer).Assembly.GetType("Photon.SocketServer.Security.DiffieHellmanCryptoProvider");
        private object provider;

        public byte[] Establish(byte[] clientPublicKey)
        {
            provider = Activator.CreateInstance(ProviderType, true);
            var publicKey = (byte[])ProviderType.GetProperty("PublicKey", All).GetValue(provider, null);
            ProviderType.GetMethod("DeriveSharedKey", All).Invoke(provider, new object[] { clientPublicKey });
            return publicKey;
        }

        public byte[] Decrypt(byte[] data, int offset, int count) =>
            (byte[])ProviderType.GetMethod("Decrypt", All, null, new[] { typeof(byte[]), typeof(int), typeof(int) }, null)
                .Invoke(provider, new object[] { data, offset, count });
    }
}
