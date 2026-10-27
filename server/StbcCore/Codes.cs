namespace StbcServer;

// Photon LoadBalancing codes as used by PUN 1.71 (values read from the game's Assembly-CSharp).

public static class OpCode
{
    public const byte Authenticate = 230, JoinLobby = 229, LeaveLobby = 228, CreateGame = 227, JoinGame = 226,
        JoinRandomGame = 225, FindFriends = 222, GetLobbyStats = 221, GetRegions = 220, WebRpc = 219,
        Leave = 254, RaiseEvent = 253, SetProperties = 252, GetProperties = 251, ChangeGroups = 248;
}

public static class EvCode
{
    public const byte GameList = 230, GameListUpdate = 229, AppStats = 226,
        Join = 255, Leave = 254, PropertiesChanged = 253;
}

public static class Param
{
    public const byte RoomName = 255, ActorNr = 254, TargetActorNr = 253, ActorList = 252, Properties = 251,
        Broadcast = 250, PlayerProperties = 249, GameProperties = 248, Cache = 247, ReceiverGroup = 246,
        Data = 245, Code = 244, CleanupCacheOnLeave = 241, Group = 240, Remove = 239, PublishUserId = 239,
        Add = 238, SuppressRoomEvents = 237, EmptyRoomTtl = 236, PlayerTtl = 235, EventForward = 234,
        IsInactive = 233, CheckUserOnJoin = 232, ExpectedValues = 231, Address = 230, PeerCount = 229,
        GameCount = 228, MasterPeerCount = 227, UserId = 225, ApplicationId = 224, MatchMakingType = 223,
        GameList = 222, Secret = 221, AppVersion = 220, JoinMode = 215, LobbyName = 213, LobbyType = 212,
        MasterClientId = 203, NickName = 202, FindFriendsRequestList = 1, FindFriendsResponseOnlineList = 1,
        FindFriendsResponseRoomIdList = 2;
}

/// <summary>Well-known byte keys inside the room properties hashtable.</summary>
public static class GameProp
{
    public const byte MaxPlayers = 255, IsVisible = 254, IsOpen = 253, PlayerCount = 252, Removed = 251,
        PropsListedInLobby = 250, CleanupCacheOnLeave = 249, MasterClientId = 248, ExpectedUsers = 247;
}

public static class ActorProp
{
    public const byte PlayerName = 255, IsInactive = 254, UserId = 253;
}

/// <summary>
/// Bridge Crew Revival's own string keys. The mod puts its version in the room (by the host) and in each
/// player's properties (sent with the join): a room that has a version only takes players on the same one.
/// </summary>
public static class ModProp
{
    public const string Version = "MV";
    /// <summary>Player property: the crew password a joiner sends (stripped before anyone else sees it).</summary>
    public const string Password = "PW";
    /// <summary>Listed room property: the crew has a password (Quick Match skips it).</summary>
    public const string Locked = "LK";
}

public static class ErrorCode
{
    public const short Ok = 0, OperationNotAllowed = -3, InvalidOperation = -2,
        GameIdAlreadyExists = 32766, GameFull = 32765, GameClosed = 32764, NoRandomMatchFound = 32760,
        GameDoesNotExist = 32758;
}

public enum EventCaching : byte
{
    DoNotCache = 0, MergeCache = 1, ReplaceCache = 2, RemoveCache = 3, AddToRoomCache = 4,
    AddToRoomCacheGlobal = 5, RemoveFromRoomCache = 6, RemoveFromRoomCacheForActorsLeft = 7,
}

public enum ReceiverGroup : byte { Others = 0, All = 1, MasterClient = 2 }

public enum MatchmakingMode : byte { FillRoom = 0, SerialMatching = 1, RandomMatching = 2 }

public enum LobbyType : byte { Default = 0, SqlLobby = 2, AsyncRandomLobby = 3 }
