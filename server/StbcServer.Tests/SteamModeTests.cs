#if HAS_PHOTON_CLIENT
using HarmonyLib;
using StbcRevival.Net;
using StbcServer.Game;
using StbcServer.Net;
using StbcServer.Protocol;
using PhotonHashtable = ExitGames.Client.Photon.Hashtable;

namespace StbcServer.Tests;

/// <summary>
/// The mod's Steam mode with the real Photon client: every player runs a server in-process ("local"),
/// hosts publish their rooms (here: a fake directory instead of Steam lobbies), and joiners reach the
/// host's server over a relay (here: an in-memory loopback instead of Steam P2P), through the same
/// Photon socket hook the mod uses.
/// </summary>
public sealed class SteamModeTests : IDisposable
{
    private const ulong HostSteamId = 1001, JoinerSteamId = 2002;

    private static readonly object InstallLock = new();
    private static bool installed;

    private readonly LoadBalancingServer hostServer;
    private readonly LoadBalancingServer joinerServer;
    private readonly FakeDirectory directory = new();
    private readonly RelayHost relayHost;
    private readonly RelayClient relayClient;
    private readonly List<PunClient> clients = [];

    private static readonly string[] LobbyProps = ["C0", "C1", "C2", "C3", "C5", "C6", "C4", "C7", "C8", "C9"];

    public SteamModeTests()
    {
        Log.Level = LogLevel.Warn;
        lock (InstallLock)
        {
            if (!installed)
            {
                VirtualSockets.Install(new Harmony("stbc.tests.virtualsockets"));
                installed = true;
            }
        }

        hostServer = new LoadBalancingServer(new ServerConfig());
        joinerServer = new LoadBalancingServer(new ServerConfig { Directory = directory });

        // The "Steam P2P" network between the two players.
        RelayClient? client = null;
        relayHost = new RelayHost(hostServer, () => new Encryption(), (to, packet) =>
        {
            if (to == JoinerSteamId) client!.OnPacket(HostSteamId, packet, packet.Length);
        });
        relayClient = client = new RelayClient((to, packet) =>
        {
            if (to == HostSteamId) relayHost.OnPacket(JoinerSteamId, packet, packet.Length);
        });

        // Each "game process" reaches its own server as local-X; steam:<id> goes over the relay.
        VirtualSockets.Connect = (host, deliver, closed) => host switch
        {
            "local-A" => LocalLink.Connect(hostServer, () => new Encryption(), "local-A:1", deliver, closed),
            "local-B" => LocalLink.Connect(joinerServer, () => new Encryption(), "local-B:1", deliver, closed),
            _ when host.StartsWith("steam:") => relayClient.Connect(ulong.Parse(host.Substring(6)), deliver, closed),
            _ => null,
        };
    }

    private PunClient Player(string address, string name)
    {
        var c = new PunClient(address, name);
        clients.Add(c);
        c.ConnectToMaster();
        return c;
    }

    private static PhotonHashtable GameProps(string roomName) => new()
    {
        ["C0"] = 8, ["C1"] = 1, ["C2"] = 4, ["C3"] = 0, ["C5"] = 1, ["NPID"] = 0,
        ["C6"] = 2, ["C4"] = 0, ["C7"] = roomName, ["C8"] = 0, ["C9"] = 1,
    };

    /// <summary>What the mod builds from a Steam lobby: listed props plus max/open, the host's address.</summary>
    private void PublishHostRooms()
    {
        directory.Set(hostServer.SnapshotRooms().Where(r => r.Visible).Select(r =>
        {
            var props = new PHashtable { [GameProp.MaxPlayers] = (byte)r.MaxPlayers, [GameProp.IsOpen] = r.Open, [GameProp.PropsListedInLobby] = LobbyProps };
            foreach (var kv in r.CustomProperties) props[kv.Key] = kv.Value;
            if (hostServer.IsPassworded(r.Name)) props[ModProp.Locked] = true; // the mod's "lock" lobby data
            return new ExternalRoom(r.Name, $"steam:{HostSteamId}:1", r.AppVersion, props, r.Players);
        }).ToList());
    }

    [Fact]
    public void JoinerQuickJoinsHostsRoomOverRelayAndTheyPlay()
    {
        var host = Player("local-A:1", "picard");
        host.JoinLobby("NovaLobby", 2);
        var created = host.CreateRoom("eu|STEAM1", GameProps("eu|STEAM1"), LobbyProps, 4, lobbyName: "NovaLobby", lobbyType: 2);
        Assert.Equal(0, created.ReturnCode);
        Assert.Equal(1, host.ActorNr);
        PublishHostRooms();

        var joiner = Player("local-B:1", "riker");
        joiner.JoinLobby("NovaLobby", 2);
        // The joiner's own master lists the host's room from the directory.
        var list = joiner.WaitEvent(230);
        Assert.True(((PhotonHashtable)list[222]).ContainsKey("eu|STEAM1"));

        // Quick join with the game's filter: the local master hands out the host's steam address.
        var joined = joiner.JoinRandom("C0 = 8 AND C3 = 0 AND C1 < 4 AND C4 = 0 AND C6 = 2 AND C8 < 1000 AND C9 = 1", "NovaLobby", 2);
        Assert.Equal(0, joined.ReturnCode);
        Assert.Equal(2, joiner.ActorNr);
        Assert.Equal(1, relayHost.ConnectionCount);
        host.WaitEvent(255, e => (int)e[254] == 2);

        // Game traffic both ways, custom types intact.
        host.RaiseEvent(200, new PhotonHashtable { [(byte)0] = 1001, [(byte)3] = "Engage", [(byte)4] = new object[] { new TestTypes.Vec3(1, 2, 3) } });
        var rpc = joiner.WaitEvent(200);
        Assert.Equal("Engage", ((PhotonHashtable)rpc[245])[(byte)3]);
        Assert.Equal(new TestTypes.Vec3(1, 2, 3), ((object[])((PhotonHashtable)rpc[245])[(byte)4])[0]);
        joiner.RaiseEvent(201, new PhotonHashtable { [(byte)0] = 7 }, reliable: false);
        host.WaitEvent(201);
        // Server time comes from ping replies (Photon pings about once a second).
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (joiner.ServerTime == 0 && DateTime.UtcNow < deadline) Thread.Sleep(50);
        Assert.NotEqual(0, joiner.ServerTime);

        // Leaving closes the relayed connection and the host sees it.
        joiner.LeaveRoom();
        host.WaitEvent(254, e => (int)e[254] == 2);
        Assert.Equal(0, relayHost.ConnectionCount);
    }

    [Fact]
    public void JoinByCodeAndLosingTheHostDisconnectsTheJoiner()
    {
        var host = Player("local-A:1", "sisko");
        host.CreateRoom("eu|K7QX2M", GameProps("eu|K7QX2M"), LobbyProps, 4);
        PublishHostRooms();

        var joiner = Player("local-B:1", "kira");
        Assert.Equal(0, joiner.JoinRoom("eu|K7QX2M").ReturnCode);
        host.WaitEvent(255, e => (int)e[254] == 2);

        // Unknown codes still fail cleanly on the joiner's own master.
        var other = Player("local-B:1", "odo");
        Assert.Equal(ErrorCode.GameDoesNotExist, other.Op(226, new Dictionary<byte, object> { [255] = "eu|NOPE00" }).ReturnCode);

        // The host's Steam connection drops: the joiner's Photon client gets a disconnect.
        relayClient.PeerLost(HostSteamId);
        joiner.WaitStatus(ExitGames.Client.Photon.StatusCode.DisconnectByServer);
    }

    [Fact]
    public void PasswordedCrewTakesOnlyTheRightPasswordAndKeepsItFromTheCrew()
    {
        var host = Player("local-A:1", "janeway");
        host.JoinLobby("NovaLobby", 2);
        Assert.Equal(0, host.CreateRoom("eu|LOCK01", GameProps("eu|LOCK01"), LobbyProps, 4, lobbyName: "NovaLobby", lobbyType: 2).ReturnCode);
        Assert.True(hostServer.SetRoomPassword("eu|LOCK01", "tea-earl-grey"));
        PublishHostRooms();

        var none = Player("local-B:1", "neelix");
        var refused = none.JoinRoom("eu|LOCK01");
        Assert.Equal(ErrorCode.GameClosed, refused.ReturnCode);
        Assert.Equal("password required", refused.DebugMessage);

        var wrong = Player("local-B:1", "kes");
        wrong.Password = "coffee";
        Assert.Equal("wrong password", wrong.JoinRoom("eu|LOCK01").DebugMessage);

        var right = Player("local-B:1", "chakotay");
        right.Password = "tea-earl-grey";
        Assert.Equal(0, right.JoinRoom("eu|LOCK01").ReturnCode);
        // The host sees the new player, without the password among their properties.
        var joined = host.WaitEvent(255, e => (int)e[254] == right.ActorNr);
        Assert.False(((PhotonHashtable)joined[249]).ContainsKey("PW"));

        // Quick Match never offers a passworded crew.
        var lister = Player("local-B:1", "tuvok");
        lister.JoinLobby("NovaLobby", 2);
        lister.WaitEvent(230);
        Assert.Equal(ErrorCode.NoRandomMatchFound, lister.JoinRandom("C0 = 8", "NovaLobby", 2).ReturnCode);
    }

    [Fact]
    public void BlockedPlayerIsRefusedAndDisconnected()
    {
        var host = Player("local-A:1", "picard");
        Assert.Equal(0, host.CreateRoom("eu|BLOCK1", GameProps("eu|BLOCK1"), LobbyProps, 4).ReturnCode);
        PublishHostRooms();

        var joiner = Player("local-B:1", "q");
        Assert.Equal(0, joiner.JoinRoom("eu|BLOCK1").ReturnCode);
        host.WaitEvent(255, e => (int)e[254] == 2);

        // Blocked while aboard: their connection to the host closes, and they're gone from the crew.
        hostServer.IsBlocked = id => id == JoinerSteamId.ToString();
        Assert.Equal(1, hostServer.DisconnectPeer(JoinerSteamId.ToString()));
        host.WaitEvent(254, e => (int)e[254] == 2);

        // And can't come back.
        var again = Player("local-B:1", "q-again");
        var refused = again.JoinRoom("eu|BLOCK1");
        Assert.Equal(ErrorCode.GameClosed, refused.ReturnCode);
        Assert.Equal("blocked", refused.DebugMessage);
    }

    [Fact]
    public void HostServerTimesOutSilentJoiners()
    {
        var server = new LoadBalancingServer(new ServerConfig { IdleTimeoutMs = 200 });
        var frames = new List<byte[]>();
        bool closed = false;
        var link = LocalLink.Connect(server, () => new Encryption(), "local:1", f => frames.Add(f), () => closed = true);
        Assert.Equal(1, server.PeerCount);
        Thread.Sleep(400);
        server.Tick();
        Assert.Equal(0, server.PeerCount);
        Assert.True(closed);
    }

    public void Dispose()
    {
        foreach (var c in clients) c.Dispose();
        VirtualSockets.Connect = null;
    }

    private sealed class FakeDirectory : IRoomDirectory
    {
        private IList<ExternalRoom> rooms = new List<ExternalRoom>();
        public IList<ExternalRoom> Rooms => rooms;
        public void Set(IList<ExternalRoom> r) => rooms = r;
    }
}
#endif
