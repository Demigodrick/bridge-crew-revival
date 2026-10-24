using System;
using System.Collections.Generic;
using System.Linq;
using StbcServer.Net;
using StbcServer.Protocol;

namespace StbcServer.Game;

public sealed class ServerConfig
{
    /// <summary>
    /// Game server address handed to clients ("host:port"). Null = the address the client reached this
    /// server at (Connection.GameServerAddress). The Bridge Crew Revival mod reconnects to its configured
    /// server anyway in SelfHosted mode, so this only matters for unmodified clients.
    /// </summary>
    public string? PublicAddress { get; set; }

    /// <summary>
    /// Photon doesn't send room lists in SQL lobbies, and Bridge Crew only uses an SQL lobby, so by
    /// default it never sees one. Sending lists there too is what makes an in-game room browser possible;
    /// the unmodded game ignores them.
    /// </summary>
    public bool ListRoomsInAllLobbies { get; set; } = true;

    /// <summary>Close connections silent for this long (clients ping every second). 0 = never.</summary>
    public int IdleTimeoutMs { get; set; } = 30000;

    /// <summary>Rooms hosted elsewhere (e.g. Steam lobbies) that this master can list and hand out.</summary>
    public IRoomDirectory? Directory { get; set; }
}

/// <summary>A room hosted on another server, reachable at <see cref="Address"/>.</summary>
public sealed class ExternalRoom
{
    public ExternalRoom(string name, string address, string appVersion, PHashtable properties, int players)
    {
        Name = name;
        Address = address;
        AppVersion = appVersion;
        Properties = properties;
        Players = players;
    }

    public string Name { get; }
    public string Address { get; }

    /// <summary>Photon app version the room was created with; empty = any (e.g. a room known from an invite).</summary>
    public string AppVersion { get; }

    /// <summary>Room properties as listed (custom string keys and the well-known byte keys).</summary>
    public PHashtable Properties { get; }
    public int Players { get; }
    public DateTime Seen { get; } = DateTime.UtcNow;

    public byte MaxPlayers => Room.MaxPlayersOf(Properties);
    public bool IsOpen => Properties.GetOrDefault(GameProp.IsOpen) is not false;
    public bool Joinable => IsOpen && (MaxPlayers == 0 || Players < MaxPlayers);
}

public interface IRoomDirectory
{
    /// <summary>Current external rooms. Called under the server lock; return a cached list.</summary>
    IList<ExternalRoom> Rooms { get; }
}

/// <summary>Read-only view of a room for the web page / API.</summary>
public sealed class RoomSnapshot
{
    public RoomSnapshot(string name, string lobby, string appVersion, bool visible, bool open, int players, int maxPlayers,
        string[] playerNames, IDictionary<string, object?> customProperties, DateTime created)
    {
        Name = name; Lobby = lobby; AppVersion = appVersion; Visible = visible; Open = open; Players = players;
        MaxPlayers = maxPlayers; PlayerNames = playerNames; CustomProperties = customProperties; Created = created;
    }

    public string Name { get; }
    public string Lobby { get; }
    public string AppVersion { get; }
    public bool Visible { get; }
    public bool Open { get; }
    public int Players { get; }
    public int MaxPlayers { get; }
    public string[] PlayerNames { get; }
    public IDictionary<string, object?> CustomProperties { get; }
    public DateTime Created { get; }
}

/// <summary>Per-connection state.</summary>
public sealed class ClientSession
{
    public bool Authenticated { get; set; }
    public string UserId { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public string Token { get; set; } = "";
    public LobbyKey? Lobby { get; set; }
    public Room? Room { get; set; }
    public Actor? Actor { get; set; }
}

/// <summary>
/// Photon LoadBalancing (master server + game server roles in one process, one endpoint), covering what
/// PUN 1.71 / Star Trek: Bridge Crew uses. PUN always reconnects between the two roles, and the
/// requests themselves say which role they're for: game-server creates carry room options (248),
/// game-server joins carry player properties (249).
/// All state is guarded by one lock; connections only enqueue outgoing data, so holding it is cheap.
/// Call <see cref="Tick"/> about once a second (room list updates, idle timeouts).
/// </summary>
public sealed class LoadBalancingServer : IPeerHandler
{
    private sealed class PendingRoom
    {
        public PendingRoom(LobbyKey lobby) => Lobby = lobby;
        public LobbyKey Lobby { get; }
        public DateTime At { get; } = DateTime.UtcNow;
    }

    private sealed class TokenInfo
    {
        public TokenInfo(string userId, string appVersion) { UserId = userId; AppVersion = appVersion; }
        public string UserId { get; }
        public string AppVersion { get; }
        public DateTime At { get; } = DateTime.UtcNow;
    }

    /// <summary>A room as a lobby list sees it, local or external.</summary>
    private sealed class Listed
    {
        public Listed(string name, PHashtable lobbyProperties) { Name = name; LobbyProperties = lobbyProperties; }
        public string Name { get; }
        public PHashtable LobbyProperties { get; }
    }

    private static readonly Random Rng = new();

    private readonly object sync = new();
    private readonly ServerConfig config;
    private readonly Dictionary<string, Room> rooms = new();
    private readonly Dictionary<string, PendingRoom> pendingRooms = new();
    private readonly Dictionary<string, TokenInfo> tokens = new();
    private readonly HashSet<Connection> connections = new();
    private readonly Dictionary<LobbyKey, HashSet<Connection>> lobbySubscribers = new();
    private readonly Dictionary<string, Dictionary<string, byte[]>> lobbySnapshots = new();

    public LoadBalancingServer(ServerConfig config) => this.config = config;

    // ---- Introspection (tests, logging, web page) ---------------------------------------------

    public int RoomCount { get { lock (sync) return rooms.Count; } }
    public int PeerCount { get { lock (sync) return connections.Count; } }

    public List<RoomSnapshot> SnapshotRooms()
    {
        lock (sync)
        {
            return rooms.Values.Select(r => new RoomSnapshot(
                r.Name, r.Lobby.ToString(), r.AppVersion, r.IsVisible, r.IsOpen, r.PlayerCount, r.MaxPlayers,
                r.Actors.Values.Select(a => a.Properties.GetOrDefault(ActorProp.PlayerName) as string ?? $"Player {a.ActorNr}").ToArray(),
                r.Properties.Where(kv => kv.Key is string).ToDictionary(kv => (string)kv.Key, kv => Plain(kv.Value)),
                r.Created)).ToList();
        }
    }

    /// <summary>The local rooms this server hosts (for publishing them elsewhere, e.g. as Steam lobbies).</summary>
    public List<RoomSnapshot> HostedRooms() => SnapshotRooms();

    /// <summary>
    /// Create an empty room, ready to be joined like any other: the first to join becomes its master. For a
    /// crew whose host dropped: the next host recreates the room under the same name and settings, and the
    /// rest of the crew joins it. Without an app version or lobby, those of a player connected to this server
    /// are used (the next host's own game). False if the room already exists.
    /// </summary>
    public bool ReserveRoom(string name, PHashtable properties, string? appVersion = null, LobbyKey? lobby = null)
    {
        lock (sync)
        {
            if (rooms.ContainsKey(name)) return false;
            var local = connections.Select(c => c.Session as ClientSession).FirstOrDefault(cs => cs is { Authenticated: true });
            var room = new Room(name, appVersion ?? local?.AppVersion ?? "", lobby ?? local?.Lobby ?? LobbyKey.Default);
            Room.Merge(room.Properties, properties);
            rooms[name] = room;
            Log.Info($"room '{name}' reserved (lobby {room.Lobby}, max {room.MaxPlayers}, version '{room.AppVersion}')");
            return true;
        }
    }

    /// <summary>Remove a reserved room nobody joined. False if it has players (or doesn't exist).</summary>
    public bool ReleaseRoom(string name)
    {
        lock (sync)
        {
            if (!rooms.TryGetValue(name, out var room) || room.Actors.Count > 0) return false;
            rooms.Remove(name);
            Log.Info($"room '{name}' released");
            return true;
        }
    }

    // Values for JSON: primitives and strings as-is, anything else described.
    private static object? Plain(object? v) => v switch
    {
        null or bool or byte or short or int or long or float or double or string => v,
        int[] a => a,
        string[] a => a,
        _ => v.ToString(),
    };

    // ---- IPeerHandler ------------------------------------------------------------------------

    public void OnConnected(Connection connection)
    {
        lock (sync)
        {
            connection.Session = new ClientSession();
            connections.Add(connection);
        }
    }

    public void OnDisconnected(Connection connection)
    {
        lock (sync)
        {
            connections.Remove(connection);
            if (connection.Session is not ClientSession s) return;
            if (s.Lobby != null) Unsubscribe(connection, s);
            if (s.Room != null && s.Actor != null) LeaveRoom(s, "disconnected");
        }
    }

    public void OnOperation(Connection connection, OperationRequest req)
    {
        lock (sync)
        {
            var s = (ClientSession)connection.Session!;
            if (Log.Level <= LogLevel.Debug)
                Log.Debug($"{connection} op {req.OperationCode} [{string.Join(", ", req.Parameters.Select(kv => $"{kv.Key}={Describe(kv.Value)}").ToArray())}]");
            try
            {
                if (req.OperationCode == OpCode.Authenticate)
                {
                    Authenticate(connection, s, req);
                    return;
                }
                if (!s.Authenticated)
                {
                    connection.SendResponse(new OperationResponse(req.OperationCode, ErrorCode.OperationNotAllowed, "not authenticated"));
                    return;
                }
                if (s.Room != null) GameOperation(connection, s, req);
                else MasterOperation(connection, s, req);
            }
            catch (Exception e)
            {
                Log.Error($"{connection} op {req.OperationCode} failed: {e}");
                connection.SendResponse(new OperationResponse(req.OperationCode, ErrorCode.InvalidOperation, "server error"));
            }
        }
    }

    private static string Describe(object? v) => v switch
    {
        null => "null",
        string s => $"'{s}'",
        PHashtable h => "{" + string.Join(", ", h.Select(kv => $"{Describe(kv.Key)}:{Describe(kv.Value)}").ToArray()) + "}",
        byte[] b => $"bytes[{b.Length}]",
        int[] a => "[" + string.Join(",", a.Select(x => x.ToString()).ToArray()) + "]",
        string[] a => "[" + string.Join(",", a.Select(x => Describe(x)).ToArray()) + "]",
        _ => v.ToString() ?? "",
    };

    // ---- Periodic work ------------------------------------------------------------------------

    /// <summary>Room list updates and idle timeouts. Call about once a second.</summary>
    public void Tick()
    {
        PublishLobbyUpdates();
        if (config.IdleTimeoutMs <= 0) return;
        List<Connection> idle;
        int now = Environment.TickCount;
        lock (sync)
            idle = connections.Where(c => unchecked(now - c.LastReceive) > config.IdleTimeoutMs).ToList();
        foreach (var c in idle)
        {
            Log.Info($"{c} timed out");
            c.Close();
        }
    }

    // ---- Authentication ------------------------------------------------------------------------

    private void Authenticate(Connection connection, ClientSession s, OperationRequest req)
    {
        PruneTokens();
        if (req[Param.Secret] is string token && !req.Parameters.ContainsKey(Param.AppVersion))
        {
            // Reconnect (master to game server and back) with a token. Tokens from another server (a
            // Steam-hosted room is reached with the token of the player's own local master) are accepted.
            if (tokens.TryGetValue(token, out var t))
            {
                s.UserId = t.UserId;
                s.AppVersion = t.AppVersion;
            }
            else
            {
                Log.Debug($"{connection} token not issued here; accepting");
                s.UserId = Guid.NewGuid().ToString();
            }
            s.Token = token;
        }
        else
        {
            s.AppVersion = req[Param.AppVersion] as string ?? "";
            s.UserId = req[Param.UserId] as string is { Length: > 0 } uid ? uid : Guid.NewGuid().ToString();
            s.Token = Guid.NewGuid().ToString("N");
        }
        tokens[s.Token] = new TokenInfo(s.UserId, s.AppVersion);
        s.Authenticated = true;

        var resp = new OperationResponse(OpCode.Authenticate);
        resp[Param.Secret] = s.Token;
        resp[Param.UserId] = s.UserId;
        connection.SendResponse(resp);
        Log.Debug($"{connection} authenticated as '{s.UserId}' (app version '{s.AppVersion}')");
    }

    private void PruneTokens()
    {
        var cutoff = DateTime.UtcNow.AddHours(-24);
        foreach (var key in tokens.Where(kv => kv.Value.At < cutoff).Select(kv => kv.Key).ToList())
            tokens.Remove(key);
        foreach (var key in pendingRooms.Where(kv => kv.Value.At < DateTime.UtcNow.AddMinutes(-2)).Select(kv => kv.Key).ToList())
            pendingRooms.Remove(key);
    }

    // ---- Master server role ---------------------------------------------------------------------

    private void MasterOperation(Connection connection, ClientSession s, OperationRequest req)
    {
        switch (req.OperationCode)
        {
            case OpCode.JoinLobby: JoinLobby(connection, s, req); break;
            case OpCode.LeaveLobby:
                Unsubscribe(connection, s);
                connection.SendResponse(new OperationResponse(OpCode.LeaveLobby));
                break;
            case OpCode.CreateGame when req.Parameters.ContainsKey(Param.GameProperties):
                CreateOnGameServer(connection, s, req);
                break;
            case OpCode.CreateGame: MasterCreate(connection, s, req); break;
            case OpCode.JoinGame when req.Parameters.ContainsKey(Param.PlayerProperties):
                JoinOnGameServer(connection, s, req);
                break;
            case OpCode.JoinGame: MasterJoin(connection, s, req); break;
            case OpCode.JoinRandomGame: JoinRandom(connection, s, req); break;
            case OpCode.FindFriends: FindFriends(connection, req); break;
            case OpCode.GetLobbyStats:
                connection.SendResponse(new OperationResponse(OpCode.GetLobbyStats));
                break;
            default:
                connection.SendResponse(new OperationResponse(req.OperationCode, ErrorCode.InvalidOperation,
                    $"operation {req.OperationCode} not supported on master"));
                break;
        }
    }

    private static LobbyKey LobbyFrom(OperationRequest req) =>
        req[Param.LobbyName] is string name
            ? new LobbyKey(name, req[Param.LobbyType] is byte t ? (LobbyType)t : LobbyType.Default)
            : LobbyKey.Default;

    private void JoinLobby(Connection connection, ClientSession s, OperationRequest req)
    {
        if (s.Lobby != null) Unsubscribe(connection, s);
        var lobby = LobbyFrom(req);
        s.Lobby = lobby;
        if (!lobbySubscribers.TryGetValue(lobby, out var subs)) lobbySubscribers[lobby] = subs = new HashSet<Connection>();
        subs.Add(connection);
        connection.SendResponse(new OperationResponse(OpCode.JoinLobby));

        var stats = new EventData(EvCode.AppStats);
        stats[Param.PeerCount] = rooms.Values.Sum(r => r.PlayerCount);
        stats[Param.MasterPeerCount] = connections.Count(c => c.Session is ClientSession { Room: null, Authenticated: true });
        stats[Param.GameCount] = rooms.Count;
        connection.SendEvent(stats);

        // Photon only lists rooms in default lobbies; see ServerConfig.ListRoomsInAllLobbies.
        if (ListsRooms(lobby))
        {
            var list = new EventData(EvCode.GameList);
            var games = new PHashtable();
            foreach (var r in ListedRooms(lobby, s.AppVersion)) games[r.Name] = r.LobbyProperties;
            list[Param.GameList] = games;
            connection.SendEvent(list);
        }
        Log.Debug($"{connection} joined lobby {lobby}");
    }

    private bool ListsRooms(LobbyKey lobby) => lobby.Type == LobbyType.Default || config.ListRoomsInAllLobbies;

    private void Unsubscribe(Connection connection, ClientSession s)
    {
        if (s.Lobby != null && lobbySubscribers.TryGetValue(s.Lobby, out var subs)) subs.Remove(connection);
        s.Lobby = null;
    }

    private IList<ExternalRoom> External(string appVersion) =>
        config.Directory?.Rooms.Where(r => (r.AppVersion == appVersion || r.AppVersion.Length == 0) && !rooms.ContainsKey(r.Name)).ToList()
        ?? (IList<ExternalRoom>)new List<ExternalRoom>();

    /// <summary>Visible rooms in this lobby for this app version: local ones, then external ones.</summary>
    private IEnumerable<Listed> ListedRooms(LobbyKey lobby, string appVersion)
    {
        foreach (var r in rooms.Values.Where(r => r.Lobby == lobby && r.AppVersion == appVersion && r.IsVisible))
            yield return new Listed(r.Name, r.LobbyProperties());
        // External rooms don't carry a Photon lobby; Bridge Crew only ever uses one, so list them everywhere.
        foreach (var r in External(appVersion))
            yield return new Listed(r.Name, Room.LobbyPropertiesOf(r.Properties, r.Players));
    }

    private string GameServerAddress(Connection connection) =>
        string.IsNullOrEmpty(config.PublicAddress) ? connection.GameServerAddress : config.PublicAddress!;

    private void MasterCreate(Connection connection, ClientSession s, OperationRequest req)
    {
        var name = req[Param.RoomName] as string is { Length: > 0 } n ? n : Guid.NewGuid().ToString();
        if (rooms.ContainsKey(name) || pendingRooms.ContainsKey(name) || External(s.AppVersion).Any(r => r.Name == name))
        {
            connection.SendResponse(new OperationResponse(OpCode.CreateGame, ErrorCode.GameIdAlreadyExists, "room already exists"));
            return;
        }
        pendingRooms[name] = new PendingRoom(req.Parameters.ContainsKey(Param.LobbyName) ? LobbyFrom(req) : s.Lobby ?? LobbyKey.Default);
        var resp = new OperationResponse(OpCode.CreateGame);
        resp[Param.RoomName] = name;
        resp[Param.Address] = GameServerAddress(connection);
        connection.SendResponse(resp);
    }

    private void MasterJoin(Connection connection, ClientSession s, OperationRequest req)
    {
        var name = req[Param.RoomName] as string ?? "";
        bool createIfMissing = req[Param.JoinMode] is byte mode && mode == 1;
        string address = GameServerAddress(connection);
        if (rooms.TryGetValue(name, out var room))
        {
            var error = JoinError(room);
            if (error != 0)
            {
                connection.SendResponse(new OperationResponse(OpCode.JoinGame, error, error == ErrorCode.GameFull ? "room full" : "room closed"));
                return;
            }
        }
        else if (External(s.AppVersion).FirstOrDefault(r => r.Name == name) is { } external)
        {
            if (!external.Joinable)
            {
                short error = external.IsOpen ? ErrorCode.GameFull : ErrorCode.GameClosed;
                connection.SendResponse(new OperationResponse(OpCode.JoinGame, error, external.IsOpen ? "room full" : "room closed"));
                return;
            }
            address = external.Address;
        }
        else if (createIfMissing)
        {
            if (!pendingRooms.ContainsKey(name))
                pendingRooms[name] = new PendingRoom(req.Parameters.ContainsKey(Param.LobbyName) ? LobbyFrom(req) : s.Lobby ?? LobbyKey.Default);
        }
        else
        {
            connection.SendResponse(new OperationResponse(OpCode.JoinGame, ErrorCode.GameDoesNotExist, "room does not exist"));
            return;
        }
        var resp = new OperationResponse(OpCode.JoinGame);
        resp[Param.RoomName] = name;
        resp[Param.Address] = address;
        connection.SendResponse(resp);
    }

    // A room with a mod version (set by the mod on the host) only takes players sending the same one; older
    // mods send none. Rooms without one (other clients, older hosts) take anyone. Returns the joiner's
    // version, or null when they may join.
    private static string? ModVersionMismatch(Room room, OperationRequest req)
    {
        if (!room.Properties.TryGetValue(ModProp.Version, out var w) || w is not string wanted || wanted.Length == 0) return null;
        object? t = null;
        (req[Param.PlayerProperties] as PHashtable)?.TryGetValue(ModProp.Version, out t);
        var theirs = t as string;
        return theirs == wanted ? null : (string.IsNullOrEmpty(theirs) ? "an older mod version" : "mod version " + theirs);
    }

    private static short JoinError(Room room) =>
        !room.IsOpen ? ErrorCode.GameClosed : room.IsFull ? ErrorCode.GameFull : (short)0;

    private void JoinRandom(Connection connection, ClientSession s, OperationRequest req)
    {
        var lobby = req.Parameters.ContainsKey(Param.LobbyName) ? LobbyFrom(req) : s.Lobby ?? LobbyKey.Default;
        var expected = req[Param.GameProperties] as PHashtable;
        var sql = req[Param.Data] as string;
        SqlFilter? filter = null;
        if (!string.IsNullOrEmpty(sql) && sql!.Trim().Length > 0)
        {
            try { filter = SqlFilter.Parse(sql); }
            catch (FormatException e)
            {
                Log.Warn($"{connection} bad SQL filter '{sql}': {e.Message}");
                connection.SendResponse(new OperationResponse(OpCode.JoinRandomGame, ErrorCode.InvalidOperation, "invalid sql filter"));
                return;
            }
        }

        bool Matches(PHashtable props) =>
            (expected == null || MatchesExpectedProperties(props, expected))
            && (filter == null || filter.Matches(key => props.GetOrDefault(key)));

        // Candidates as (name, address, created): local rooms first-come, then external ones.
        var candidates = new List<KeyValuePair<string, string>>();
        foreach (var r in rooms.Values
                     .Where(r => r.Lobby == lobby && r.AppVersion == s.AppVersion && r.IsVisible && JoinError(r) == 0 && Matches(r.Properties))
                     .OrderBy(r => r.Created))
            candidates.Add(new KeyValuePair<string, string>(r.Name, GameServerAddress(connection)));
        foreach (var r in External(s.AppVersion).Where(r => r.Joinable && Matches(r.Properties)).OrderBy(r => r.Seen))
            candidates.Add(new KeyValuePair<string, string>(r.Name, r.Address));

        if (candidates.Count == 0)
        {
            connection.SendResponse(new OperationResponse(OpCode.JoinRandomGame, ErrorCode.NoRandomMatchFound, "no match found"));
            return;
        }
        var mode = req[Param.MatchMakingType] is byte m ? (MatchmakingMode)m : MatchmakingMode.FillRoom;
        KeyValuePair<string, string> pick;
        lock (Rng) pick = mode == MatchmakingMode.RandomMatching ? candidates[Rng.Next(candidates.Count)] : candidates[0];

        var resp = new OperationResponse(OpCode.JoinRandomGame);
        resp[Param.RoomName] = pick.Key;
        resp[Param.Address] = pick.Value;
        connection.SendResponse(resp);
    }

    private static bool MatchesExpectedProperties(PHashtable props, PHashtable expected)
    {
        foreach (var kv in expected)
        {
            if (kv.Key is byte b && b == GameProp.MaxPlayers)
            {
                if (Convert.ToInt32(kv.Value) != Room.MaxPlayersOf(props)) return false;
                continue;
            }
            props.TryGetValue(kv.Key, out var actual);
            if (!Protocol16.SameValue(actual, kv.Value)) return false;
        }
        return true;
    }

    private void FindFriends(Connection connection, OperationRequest req)
    {
        var ids = req[Param.FindFriendsRequestList] as string[] ?? new string[0];
        var online = new bool[ids.Length];
        var roomNames = new string[ids.Length];
        var sessions = connections.Select(c => c.Session).OfType<ClientSession>().Where(x => x.Authenticated).ToList();
        for (int i = 0; i < ids.Length; i++)
        {
            var match = sessions.Where(x => x.UserId == ids[i]).ToList();
            online[i] = match.Count > 0;
            roomNames[i] = match.Select(x => x.Room?.Name).FirstOrDefault(n => n != null) ?? "";
        }
        var resp = new OperationResponse(OpCode.FindFriends);
        resp[Param.FindFriendsResponseOnlineList] = online;
        resp[Param.FindFriendsResponseRoomIdList] = roomNames;
        connection.SendResponse(resp);
    }

    // ---- Game server role: entering a room ------------------------------------------------------

    private void CreateOnGameServer(Connection connection, ClientSession s, OperationRequest req)
    {
        var name = req[Param.RoomName] as string is { Length: > 0 } n ? n : Guid.NewGuid().ToString();
        if (rooms.ContainsKey(name))
        {
            connection.SendResponse(new OperationResponse(OpCode.CreateGame, ErrorCode.GameIdAlreadyExists, "room already exists"));
            return;
        }
        var room = NewRoom(name, s, req);
        EnterRoom(connection, s, room, req, OpCode.CreateGame);
    }

    private void JoinOnGameServer(Connection connection, ClientSession s, OperationRequest req)
    {
        var name = req[Param.RoomName] as string ?? "";
        if (!rooms.TryGetValue(name, out var room))
        {
            if (req[Param.JoinMode] is byte mode && mode == 1)
            {
                room = NewRoom(name, s, req);
            }
            else
            {
                connection.SendResponse(new OperationResponse(OpCode.JoinGame, ErrorCode.GameDoesNotExist, "room does not exist"));
                return;
            }
        }
        else
        {
            var error = JoinError(room);
            if (error != 0)
            {
                connection.SendResponse(new OperationResponse(OpCode.JoinGame, error, error == ErrorCode.GameFull ? "room full" : "room closed"));
                return;
            }
            if (ModVersionMismatch(room, req) is string mismatch)
            {
                Log.Info($"room '{name}': refused a player on {mismatch}");
                connection.SendResponse(new OperationResponse(OpCode.JoinGame, ErrorCode.GameClosed, "different Bridge Crew Revival version"));
                return;
            }
        }
        EnterRoom(connection, s, room, req, OpCode.JoinGame);
    }

    private Room NewRoom(string name, ClientSession s, OperationRequest req)
    {
        var lobby = req.Parameters.ContainsKey(Param.LobbyName) ? LobbyFrom(req)
            : pendingRooms.TryGetValue(name, out var pending) ? pending.Lobby
            : LobbyKey.Default;
        pendingRooms.Remove(name);

        var room = new Room(name, s.AppVersion, lobby);
        if (req[Param.GameProperties] is PHashtable props) Room.Merge(room.Properties, props);
        room.CleanupCacheOnLeave = req[Param.CleanupCacheOnLeave] as bool? ?? true;
        room.SuppressRoomEvents = req[Param.SuppressRoomEvents] as bool? ?? false;
        room.PublishUserId = req[Param.PublishUserId] as bool? ?? false;
        rooms[name] = room;
        Log.Info($"room '{name}' created (lobby {lobby}, max {room.MaxPlayers}, version '{s.AppVersion}')");
        return room;
    }

    private void EnterRoom(Connection connection, ClientSession s, Room room, OperationRequest req, byte opCode)
    {
        Unsubscribe(connection, s);
        var props = req[Param.PlayerProperties] is PHashtable p ? new PHashtable(p) : new PHashtable();
        if (room.PublishUserId) props[ActorProp.UserId] = s.UserId;

        var actor = new Actor(room.TakeActorNr(), connection, s, props);
        room.Actors.Add(actor.ActorNr, actor);
        s.Room = room;
        s.Actor = actor;

        var resp = new OperationResponse(opCode);
        resp[Param.ActorNr] = actor.ActorNr;
        resp[Param.GameProperties] = new PHashtable(room.Properties);
        resp[Param.PlayerProperties] = room.ActorPropertiesTable();
        resp[Param.ActorList] = room.ActorList;
        connection.SendResponse(resp);

        // PUN raises OnJoinedRoom from the joiner's own Join event, so the joiner always gets it.
        var join = new EventData(EvCode.Join);
        join[Param.ActorNr] = actor.ActorNr;
        join[Param.PlayerProperties] = props;
        join[Param.ActorList] = room.ActorList;
        if (room.SuppressRoomEvents) connection.SendEvent(join);
        else room.Broadcast(join);

        foreach (var cached in room.EventCache)
        {
            var ev = new EventData(cached.Code);
            if (cached.Data != null) ev[Param.Data] = cached.Data;
            ev[Param.ActorNr] = cached.ActorNr;
            connection.SendEvent(ev);
        }

        Log.Info($"'{props.GetOrDefault(ActorProp.PlayerName)}' ({s.UserId}) joined '{room.Name}' as actor {actor.ActorNr} ({room.PlayerCount} players)");
    }

    // ---- Game server role: in a room -------------------------------------------------------------

    private void GameOperation(Connection connection, ClientSession s, OperationRequest req)
    {
        var room = s.Room!;
        var actor = s.Actor!;
        switch (req.OperationCode)
        {
            case OpCode.RaiseEvent: RaiseEvent(room, actor, req); break;
            case OpCode.SetProperties: SetProperties(connection, room, actor, req); break;
            case OpCode.GetProperties:
            {
                var resp = new OperationResponse(OpCode.GetProperties);
                resp[Param.GameProperties] = new PHashtable(room.Properties);
                resp[Param.PlayerProperties] = room.ActorPropertiesTable();
                connection.SendResponse(resp);
                break;
            }
            case OpCode.ChangeGroups:
                ChangeGroups(actor, req);
                connection.SendResponse(new OperationResponse(OpCode.ChangeGroups));
                break;
            case OpCode.Leave:
                LeaveRoom(s, "left");
                connection.SendResponse(new OperationResponse(OpCode.Leave));
                break;
            default:
                connection.SendResponse(new OperationResponse(req.OperationCode, ErrorCode.InvalidOperation,
                    $"operation {req.OperationCode} not supported in a room"));
                break;
        }
    }

    private static void RaiseEvent(Room room, Actor sender, OperationRequest req)
    {
        byte code = req[Param.Code] is byte c ? c : (byte)0;
        object? data = req[Param.Data];
        var caching = req[Param.Cache] is byte cache ? (EventCaching)cache : EventCaching.DoNotCache;
        var targets = req[Param.ActorList] as int[];

        switch (caching)
        {
            case EventCaching.RemoveFromRoomCache:
                room.RemoveCachedEvents(code, targets, data);
                return;
            case EventCaching.RemoveFromRoomCacheForActorsLeft:
                room.RemoveCachedEventsOfLeftActors();
                return;
            case EventCaching.RemoveCache:
                room.EventCache.RemoveAll(e => e.ActorNr == sender.ActorNr && e.Code == code && !e.Global);
                return;
            case EventCaching.MergeCache:
            case EventCaching.ReplaceCache:
                room.EventCache.RemoveAll(e => e.ActorNr == sender.ActorNr && e.Code == code && !e.Global);
                room.EventCache.Add(new CachedEvent(sender.ActorNr, code, data, false));
                break;
            case EventCaching.AddToRoomCache:
                room.EventCache.Add(new CachedEvent(sender.ActorNr, code, data, false));
                break;
            case EventCaching.AddToRoomCacheGlobal:
                room.EventCache.Add(new CachedEvent(sender.ActorNr, code, data, true));
                break;
            case EventCaching.DoNotCache:
                break;
            default:
                return; // cache slice operations: not used by PUN 1.71
        }

        var ev = new EventData(code);
        if (data != null) ev[Param.Data] = data;
        ev[Param.ActorNr] = sender.ActorNr;
        var body = Room.Serialize(ev);
        var receivers = req[Param.ReceiverGroup] is byte g ? (ReceiverGroup)g : ReceiverGroup.Others;
        byte interestGroup = req[Param.Group] is byte ig ? ig : (byte)0;
        foreach (var a in room.Receivers(sender, receivers, targets, interestGroup))
            a.Connection.SendEventBody(body);
    }

    private void SetProperties(Connection connection, Room room, Actor sender, OperationRequest req)
    {
        if (req[Param.Properties] is not PHashtable props)
        {
            connection.SendResponse(new OperationResponse(OpCode.SetProperties, ErrorCode.InvalidOperation, "missing properties"));
            return;
        }
        int targetActor = req[Param.ActorNr] is int n ? n : 0;
        PHashtable target;
        if (targetActor > 0)
        {
            if (!room.Actors.TryGetValue(targetActor, out var a))
            {
                connection.SendResponse(new OperationResponse(OpCode.SetProperties, ErrorCode.InvalidOperation, "no such actor"));
                return;
            }
            target = a.Properties;
        }
        else
        {
            target = room.Properties;
        }

        var expected = req[Param.ExpectedValues] as PHashtable;
        if (expected is { Count: > 0 } && !Room.MatchesExpected(target, expected))
        {
            connection.SendResponse(new OperationResponse(OpCode.SetProperties, ErrorCode.InvalidOperation, "expected values did not match"));
            return;
        }

        var changes = new PHashtable(props);
        Room.Merge(target, changes);
        connection.SendResponse(new OperationResponse(OpCode.SetProperties));

        if (req[Param.Broadcast] is true)
        {
            var ev = new EventData(EvCode.PropertiesChanged);
            ev[Param.TargetActorNr] = targetActor;
            ev[Param.Properties] = changes;
            ev[Param.ActorNr] = sender.ActorNr;
            // With compare-and-swap the sender hasn't applied the change locally yet.
            room.Broadcast(ev, except: expected is { Count: > 0 } ? null : sender);
        }
    }

    private static void ChangeGroups(Actor actor, OperationRequest req)
    {
        if (req[Param.Remove] is byte[] remove)
        {
            if (remove.Length == 0) { actor.Groups.Clear(); actor.AllGroups = false; }
            else foreach (var g in remove) actor.Groups.Remove(g);
        }
        if (req[Param.Add] is byte[] add)
        {
            if (add.Length == 0) actor.AllGroups = true;
            else foreach (var g in add) actor.Groups.Add(g);
        }
    }

    private void LeaveRoom(ClientSession s, string why)
    {
        var room = s.Room!;
        var actor = s.Actor!;
        bool wasMaster = room.MasterClientId == actor.ActorNr;
        bool serverSideMaster = room.ExplicitMasterClientId != null;
        room.Actors.Remove(actor.ActorNr);
        s.Room = null;
        s.Actor = null;

        if (room.CleanupCacheOnLeave)
            room.EventCache.RemoveAll(e => e.ActorNr == actor.ActorNr && !e.Global);

        if (room.Actors.Count == 0)
        {
            rooms.Remove(room.Name);
            Log.Info($"actor {actor.ActorNr} {why}; room '{room.Name}' closed");
            return;
        }

        if (wasMaster && serverSideMaster)
            room.Properties[GameProp.MasterClientId] = room.Actors.Keys.First();

        if (!room.SuppressRoomEvents)
        {
            var ev = new EventData(EvCode.Leave);
            ev[Param.ActorNr] = actor.ActorNr;
            ev[Param.ActorList] = room.ActorList;
            if (wasMaster && serverSideMaster) ev[Param.MasterClientId] = room.MasterClientId;
            room.Broadcast(ev);
        }
        Log.Info($"actor {actor.ActorNr} {why} '{room.Name}' ({room.PlayerCount} players left)");
    }

    // ---- Lobby room list updates ---------------------------------------------------------------

    /// <summary>Send GameListUpdate diffs to lobby subscribers.</summary>
    public void PublishLobbyUpdates()
    {
        lock (sync)
        {
            foreach (var pair in lobbySubscribers)
            {
                var lobby = pair.Key;
                if (!ListsRooms(lobby) || pair.Value.Count == 0) continue;
                // Per app version, since clients only see rooms of their own version.
                foreach (var group in pair.Value.GroupBy(c => ((ClientSession)c.Session!).AppVersion))
                {
                    var key = lobby + "\0" + group.Key;
                    if (!lobbySnapshots.TryGetValue(key, out var last)) lobbySnapshots[key] = last = new Dictionary<string, byte[]>();
                    var current = new Dictionary<string, PHashtable>();
                    foreach (var r in ListedRooms(lobby, group.Key)) current[r.Name] = r.LobbyProperties;

                    var update = new PHashtable();
                    foreach (var kv in current)
                    {
                        var bytes = Protocol16.Encode(kv.Value);
                        if (!last.TryGetValue(kv.Key, out var prev) || !Compat.BytesEqual(prev, bytes))
                        {
                            update[kv.Key] = kv.Value;
                            last[kv.Key] = bytes;
                        }
                    }
                    foreach (var gone in last.Keys.Where(k => !current.ContainsKey(k)).ToList())
                    {
                        update[gone] = new PHashtable { [GameProp.Removed] = true };
                        last.Remove(gone);
                    }
                    if (update.Count == 0) continue;
                    var ev = new EventData(EvCode.GameListUpdate);
                    ev[Param.GameList] = update;
                    var body = Room.Serialize(ev);
                    foreach (var c in group) c.SendEventBody(body);
                }
            }
        }
    }
}
