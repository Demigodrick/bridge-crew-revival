#if HAS_PHOTON_CLIENT
using System.Net;
using StbcServer.Game;
using StbcServer.Net;
using PhotonHashtable = ExitGames.Client.Photon.Hashtable;

namespace StbcServer.Tests;

/// <summary>
/// Real Photon client library against the server over TCP, following PUN's flows.
/// Runs an in-process server, or set STBC_TEST_SERVER=host:port to test a running one (e.g. the container).
/// </summary>
public sealed class EndToEndTests : IDisposable
{
    private readonly CancellationTokenSource cts = new();
    private readonly Task run = Task.CompletedTask;
    private readonly LoadBalancingServer? listenerServer;
    private readonly string address;
    private readonly List<PunClient> clients = [];

    // Bridge Crew's room properties and lobby list (OOLKIMEFIPC in the game).
    private static readonly string[] LobbyProps = ["C0", "C1", "C2", "C3", "C5", "C6", "C4", "C7", "C8", "C9"];
    private static PhotonHashtable GameProps(int mode = 2) => new()
    {
        ["C0"] = 1, ["C1"] = 0, ["C2"] = 4, ["C3"] = mode, ["C5"] = 1, ["NPID"] = 0,
        ["C6"] = 0, ["C4"] = 0, ["C7"] = "room-x", ["C8"] = 0, ["C9"] = 1,
    };
    private const string QuickJoinFilter = "C0 = 1 AND C3 = 2 AND C1 < 4 AND C4 = 0 AND C6 = 0 AND C8 < 5 AND C9 = 1";

    public EndToEndTests()
    {
        if (Environment.GetEnvironmentVariable("STBC_TEST_SERVER") is { Length: > 0 } external)
        {
            address = external;
            return;
        }
        Log.Level = LogLevel.Warn;
        var listener = new PhotonListener(new ServerConfig(), IPAddress.Loopback, 0, TimeSpan.FromMilliseconds(100));
        listener.Start();
        listenerServer = listener.Server;
        address = $"127.0.0.1:{listener.LocalEndPoint!.Port}";
        run = listener.RunAsync(cts.Token);
    }

    private PunClient Client(string name, string appVersion = "1.0.TEST")
    {
        var c = new PunClient(address, name, appVersion);
        clients.Add(c);
        c.ConnectToMaster();
        return c;
    }

    [Fact]
    public void EncryptedAuthenticationIssuesTokenAndServerTime()
    {
        var a = Client("kirk");
        Assert.False(string.IsNullOrEmpty(a.Token));
        Assert.NotEqual(0, a.ServerTime);
    }

    [Fact]
    public void CreateAndJoinRoomBySqlQuickJoin()
    {
        var host = Client("picard");
        host.JoinLobby("stbc", 2);
        var created = host.CreateRoom(null, GameProps(), LobbyProps, maxPlayers: 4, lobbyName: "stbc", lobbyType: 2);
        Assert.Equal(0, created.ReturnCode);
        Assert.Equal(1, host.ActorNr);
        var hostJoin = host.WaitEvent(255);
        Assert.Equal(1, hostJoin[254]);

        var guest = Client("riker");
        guest.JoinLobby("stbc", 2);
        var joined = guest.JoinRandom(QuickJoinFilter, "stbc", 2);
        Assert.Equal(0, joined.ReturnCode);
        Assert.Equal(host.RoomName, guest.RoomName);
        Assert.Equal(2, guest.ActorNr);

        // Join response: room properties and every actor's properties, as PUN reads them.
        var roomProps = (PhotonHashtable)joined[248];
        Assert.Equal((byte)4, roomProps[(byte)255]);
        Assert.Equal("room-x", roomProps["C7"]);
        var actors = (PhotonHashtable)joined[249];
        Assert.Equal("picard", ((PhotonHashtable)actors[1])[(byte)255]);
        Assert.Equal("riker", ((PhotonHashtable)actors[2])[(byte)255]);
        Assert.Equal(new[] { 1, 2 }, (int[])joined[252]);

        // Both get the Join event for actor 2 (PUN raises OnJoinedRoom / OnPhotonPlayerConnected from it).
        var seenByHost = host.WaitEvent(255, e => (int)e[254] == 2);
        Assert.Equal("riker", ((PhotonHashtable)seenByHost[249])[(byte)255]);
        Assert.Equal(new[] { 1, 2 }, (int[])seenByHost[252]);
        guest.WaitEvent(255, e => (int)e[254] == 2);
    }

    [Fact]
    public void RoomWithModVersionOnlyTakesTheSameVersion()
    {
        var host = Client("janeway");
        host.ModVersion = "0.6.9";
        Assert.Equal(0, host.CreateRoom("voyager", new PhotonHashtable { ["MV"] = "0.6.9" }, [], maxPlayers: 4).ReturnCode);

        var same = Client("chakotay");
        same.ModVersion = "0.6.9";
        Assert.Equal(0, same.JoinRoom("voyager").ReturnCode);

        var older = Client("tuvok");
        older.ModVersion = "0.6.8";
        Assert.Equal(ErrorCode.GameClosed, older.JoinRoom("voyager").ReturnCode);

        var noMod = Client("neelix"); // a mod from before versions were sent
        Assert.Equal(ErrorCode.GameClosed, noMod.JoinRoom("voyager").ReturnCode);

        // A room without a version (an older host, or no mod) takes anyone.
        var oldHost = Client("paris");
        Assert.Equal(0, oldHost.CreateRoom("delta flyer", new PhotonHashtable(), [], maxPlayers: 4).ReturnCode);
        Assert.Equal(0, older.JoinRoom("delta flyer").ReturnCode);
    }

    [Fact]
    public void QuickJoinRespectsFilterAndFullRooms()
    {
        var host = Client("sisko");
        host.JoinLobby("stbc", 2);
        Assert.Equal(0, host.CreateRoom(null, GameProps(mode: 3), LobbyProps, maxPlayers: 4, lobbyName: "stbc", lobbyType: 2).ReturnCode);

        var guest = Client("dax");
        guest.JoinLobby("stbc", 2);
        Assert.Equal(ErrorCode.NoRandomMatchFound, guest.JoinRandom(QuickJoinFilter, "stbc", 2).ReturnCode);

        // A different lobby or app version never matches either.
        Assert.Equal(ErrorCode.NoRandomMatchFound, guest.JoinRandom("C3 = 3", "other", 2).ReturnCode);
        var oldVersion = Client("worf", appVersion: "1.0.OLD");
        oldVersion.JoinLobby("stbc", 2);
        Assert.Equal(ErrorCode.NoRandomMatchFound, oldVersion.JoinRandom("C3 = 3", "stbc", 2).ReturnCode);

        Assert.Equal(0, guest.JoinRandom("C3 = 3", "stbc", 2).ReturnCode);

        // Room of 2 is now full.
        var host2 = Client("odo");
        Assert.Equal(0, host2.CreateRoom("tiny", new PhotonHashtable(), [], maxPlayers: 1).ReturnCode);
        var late = Client("quark");
        Assert.Equal(ErrorCode.GameFull, late.Op(226, new Dictionary<byte, object> { [255] = "tiny" }).ReturnCode);
        Assert.Equal(ErrorCode.GameDoesNotExist, late.Op(226, new Dictionary<byte, object> { [255] = "nope" }).ReturnCode);
    }

    [Fact]
    public void DefaultLobbyListsRoomsAndSendsUpdates()
    {
        var watcher = Client("uhura");
        watcher.JoinLobby();
        var initial = watcher.WaitEvent(230);
        Assert.Empty((PhotonHashtable)initial[222]);

        var host = Client("sulu");
        host.CreateRoom("bridge", GameProps(), LobbyProps, maxPlayers: 4);

        var update = watcher.WaitEvent(229, e => ((PhotonHashtable)e[222]).ContainsKey("bridge"));
        var info = (PhotonHashtable)((PhotonHashtable)update[222])["bridge"];
        Assert.Equal((byte)4, info[(byte)255]);
        Assert.Equal((byte)1, info[(byte)252]);
        Assert.Equal(1, info["C0"]);
        Assert.False(info.ContainsKey("NPID")); // not listed in lobby

        host.LeaveRoom();
        var removed = watcher.WaitEvent(229, e => ((PhotonHashtable)e[222]).ContainsKey("bridge")
                                                 && ((PhotonHashtable)((PhotonHashtable)e[222])["bridge"]).ContainsKey((byte)251));
        Assert.Equal(true, ((PhotonHashtable)((PhotonHashtable)removed[222])["bridge"])[(byte)251]);
    }

    [Fact]
    public void SqlLobbyAlsoGetsRoomListsForTheInGameBrowser()
    {
        // Bridge Crew sits in "NovaLobby" (SQL lobby). Photon wouldn't list rooms there; we do.
        var watcher = Client("chekov");
        watcher.JoinLobby("NovaLobby", 2);
        watcher.WaitEvent(230);

        var host = Client("scotty");
        host.JoinLobby("NovaLobby", 2);
        host.CreateRoom("eu|ABC123", GameProps(), LobbyProps, maxPlayers: 4, lobbyName: "NovaLobby", lobbyType: 2);
        var update = watcher.WaitEvent(229, e => ((PhotonHashtable)e[222]).ContainsKey("eu|ABC123"));
        var info = (PhotonHashtable)((PhotonHashtable)update[222])["eu|ABC123"];
        Assert.Equal(2, info["C3"]);
        Assert.Equal((byte)1, info[(byte)252]);

        // Private (invisible) rooms are never listed.
        var priv = Client("mccoy");
        priv.CreateRoom("eu|SECRET", GameProps(), LobbyProps, maxPlayers: 4, isVisible: false, lobbyName: "NovaLobby", lobbyType: 2);
        Thread.Sleep(300);
        Assert.DoesNotContain(watcher.Events(229), e => ((PhotonHashtable)e[222]).ContainsKey("eu|SECRET"));

        // ...but can be joined by name.
        var friend = Client("spock");
        Assert.Equal(0, friend.JoinRoom("eu|SECRET").ReturnCode);
    }

    [Fact]
    public void WebApiDecodesPublicRoomsAndHidesPrivateOnes()
    {
        if (listenerServer == null) return; // only meaningful for the in-process server
        var host = Client("archer");
        host.CreateRoom("eu|K7QX2M", GameProps(mode: 0), LobbyProps, maxPlayers: 4);
        var other = Client("tpol");
        other.CreateRoom("eu|HIDDEN", GameProps(mode: 2), LobbyProps, maxPlayers: 4, isVisible: false);

        var web = new StbcServer.Web.WebStatus(listenerServer, "127.0.0.1", 0);
        var rooms = web.PublicRooms();
        var room = Assert.Single(rooms);
        Assert.Equal("K7QX2M", room.Code);
        Assert.Equal("Public", room.Mode);
        Assert.Equal("Arabic", room.Language); // C0 = 1 in GameProps
        Assert.Equal(new[] { "archer" }, room.PlayerNames);
        Assert.Equal(1, room.Players);
        Assert.Equal(4, room.MaxPlayers);
    }

    [Fact]
    public async Task WebPageAndJsonServeOverHttp()
    {
        if (listenerServer == null) return;
        var host = Client("sato");
        host.CreateRoom("eu|WEB123", GameProps(mode: 0), LobbyProps, maxPlayers: 4);

        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var web = new StbcServer.Web.WebStatus(listenerServer, "127.0.0.1", port);
        web.Start();
        _ = web.RunAsync(cts.Token);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        var page = await http.GetStringAsync("");
        Assert.Contains("Bridge Crew Server", page);
        var json = await http.GetStringAsync("api/rooms");
        Assert.Contains("\"code\": \"WEB123\"", json);
        Assert.Contains("\"playerNames\": [\n      \"sato\"", json.Replace("\r", ""));
        var status = await http.GetStringAsync("api/status");
        Assert.Contains("\"publicRooms\": 1", status);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await http.GetAsync("nope")).StatusCode);
    }

    [Fact]
    public void EventsRelayExactlyWithReceiverGroupsAndTargets()
    {
        var (a, b, c) = ThreePlayerRoom();

        // PUN RPC-style payload with custom types (Vector3/Quaternion), sent to Others.
        var rpc = new PhotonHashtable { [(byte)0] = 1001, [(byte)2] = 12345, [(byte)3] = "FirePhasers",
            [(byte)4] = new object[] { new TestTypes.Vec3(1, 2, 3), new TestTypes.Quat(0, 0, 0, 1), 42 } };
        a.RaiseEvent(200, rpc);
        foreach (var receiver in new[] { b, c })
        {
            var ev = receiver.WaitEvent(200);
            Assert.Equal(1, ev[254]);
            var data = (PhotonHashtable)ev[245];
            Assert.Equal("FirePhasers", data[(byte)3]);
            var args = (object[])data[(byte)4];
            Assert.Equal(new TestTypes.Vec3(1, 2, 3), args[0]);
            Assert.Equal(new TestTypes.Quat(0, 0, 0, 1), args[1]);
        }
        Thread.Sleep(100);
        Assert.Empty(a.Events(200)); // Others excludes the sender

        b.RaiseEvent(201, new PhotonHashtable { [(byte)0] = 7 }, receivers: 2); // MasterClient = lowest actor (a)
        a.WaitEvent(201);
        b.RaiseEvent(202, 1, targets: [3]);
        c.WaitEvent(202);
        b.RaiseEvent(203, 2, receivers: 1); // All, including sender
        b.WaitEvent(203);
        Thread.Sleep(100);
        Assert.Empty(c.Events(201));
        Assert.Empty(a.Events(202));

        // Unreliable sends (PUN's SendSerialize) work too over TCP.
        a.RaiseEvent(201, new PhotonHashtable { [(byte)0] = 99 }, reliable: false);
        c.WaitEvent(201, e => (int)((PhotonHashtable)e[245])[(byte)0] == 99);
    }

    [Fact]
    public void EventCacheReplaysToLateJoinersAndHonoursRemoval()
    {
        var host = Client("janeway");
        host.CreateRoom("voyager", GameProps(), LobbyProps, maxPlayers: 4);

        // PUN Instantiate (202) is AddToRoomCache; buffered RPCs (200) too; scene objects use the global cache.
        host.RaiseEvent(202, new PhotonHashtable { [(byte)0] = "ShipPrefab", [(byte)7] = 1001 }, cache: 4);
        host.RaiseEvent(200, new PhotonHashtable { [(byte)0] = 1001, [(byte)3] = "SetAlert" }, cache: 4);
        host.RaiseEvent(200, new PhotonHashtable { [(byte)0] = 2002, [(byte)3] = "Other" }, cache: 4);
        host.RaiseEvent(202, new PhotonHashtable { [(byte)0] = "Station", [(byte)7] = 3003 }, cache: 5);
        // OpCleanRpcBuffer(view 2002): RemoveFromRoomCache with a data filter.
        host.RaiseEvent(200, new PhotonHashtable { [(byte)0] = 2002 }, cache: 6);

        var late = Client("chakotay");
        Assert.Equal(0, late.JoinRoom("voyager").ReturnCode);
        late.WaitEvent(202, e => (string)((PhotonHashtable)e[245])[(byte)0] == "ShipPrefab");
        late.WaitEvent(202, e => (string)((PhotonHashtable)e[245])[(byte)0] == "Station");
        var rpc = late.WaitEvent(200);
        Assert.Equal("SetAlert", ((PhotonHashtable)rpc[245])[(byte)3]);
        Assert.Equal(1, rpc[254]);
        Thread.Sleep(100);
        Assert.Single(late.Events(200));

        // Host leaves: its own cached events go (CleanupCacheOnLeave), the global one stays.
        host.LeaveRoom();
        late.WaitEvent(254, e => (int)e[254] == 1);
        var third = Client("tuvok");
        Assert.Equal(0, third.JoinRoom("voyager").ReturnCode);
        third.WaitEvent(202, e => (string)((PhotonHashtable)e[245])[(byte)0] == "Station");
        Thread.Sleep(100);
        Assert.Single(third.Events(202));
        Assert.Empty(third.Events(200));
    }

    [Fact]
    public void PropertiesBroadcastAndCompareAndSwap()
    {
        var (a, b, _) = ThreePlayerRoom();

        // Room property, as PUN's OpSetCustomPropertiesOfRoom: [251] props, [250] broadcast.
        a.Op(252, new Dictionary<byte, object> { [251] = new PhotonHashtable { ["C1"] = 3 }, [250] = true });
        var ev = b.WaitEvent(253);
        Assert.Equal(0, ev[253]);
        Assert.Equal(3, ((PhotonHashtable)ev[251])["C1"]);
        Assert.Equal(1, ev[254]);

        // Player property.
        b.Op(252, new Dictionary<byte, object> { [251] = new PhotonHashtable { ["station"] = "helm" }, [254] = 2, [250] = true });
        var pev = a.WaitEvent(253, e => (int)e[253] == 2);
        Assert.Equal("helm", ((PhotonHashtable)pev[251])["station"]);

        // Compare-and-swap: wrong expectation fails, right one succeeds and is also sent to the sender.
        var fail = a.Op(252, new Dictionary<byte, object>
            { [251] = new PhotonHashtable { ["C1"] = 4 }, [250] = true, [231] = new PhotonHashtable { ["C1"] = 99 } });
        Assert.NotEqual(0, fail.ReturnCode);
        var ok = a.Op(252, new Dictionary<byte, object>
            { [251] = new PhotonHashtable { ["C1"] = 4 }, [250] = true, [231] = new PhotonHashtable { ["C1"] = 3 } });
        Assert.Equal(0, ok.ReturnCode);
        a.WaitEvent(253, e => (int)e[253] == 0 && ((PhotonHashtable)e[251])["C1"] is 4);

        // Later joiners see the merged state.
        var get = b.Op(251, new Dictionary<byte, object>());
        Assert.Equal(4, ((PhotonHashtable)get[248])["C1"]);
        Assert.Equal("helm", ((PhotonHashtable)((PhotonHashtable)get[249])[2])["station"]);
    }

    [Fact]
    public void LeavingAndDisconnectingNotifyOthers()
    {
        var (a, b, c) = ThreePlayerRoom();
        b.LeaveRoom();
        var left = a.WaitEvent(254, e => (int)e[254] == 2);
        Assert.Equal(new[] { 1, 3 }, (int[])left[252]);
        c.WaitEvent(254, e => (int)e[254] == 2);

        c.Dispose(); // drop the connection without leaving
        a.WaitEvent(254, e => (int)e[254] == 3);

        // The leaver is back on master and can rejoin; it gets a new actor number.
        Assert.Equal(0, b.JoinRoom(a.RoomName!).ReturnCode);
        Assert.Equal(4, b.ActorNr);
        a.WaitEvent(255, e => (int)e[254] == 4);
    }

    [Fact]
    public void InterestGroupsFilterEvents()
    {
        var (a, b, c) = ThreePlayerRoom();
        b.Op(248, new Dictionary<byte, object> { [238] = new byte[] { 5 } });
        a.Send(253, new Dictionary<byte, object> { [244] = (byte)50, [245] = 1, [240] = (byte)5 });
        b.WaitEvent(50);
        Thread.Sleep(100);
        Assert.Empty(c.Events(50));
    }

    [Fact]
    public void FindFriendsReportsOnlineAndRoom()
    {
        var (a, _, _) = ThreePlayerRoom();
        var finder = Client("guinan");
        var resp = finder.Op(222, new Dictionary<byte, object> { [1] = new[] { a.UserId, "user-nobody" } });
        Assert.Equal(new[] { true, false }, (bool[])resp[1]);
        Assert.Equal(new[] { a.RoomName!, "" }, (string[])resp[2]);
    }

    [Fact]
    public void CreateWithExistingNameFails()
    {
        var a = Client("data");
        Assert.Equal(0, a.CreateRoom("enterprise", new PhotonHashtable(), [], 4).ReturnCode);
        var b = Client("lore");
        Assert.Equal(ErrorCode.GameIdAlreadyExists, b.Op(227, new Dictionary<byte, object> { [255] = "enterprise" }).ReturnCode);
    }

    private (PunClient a, PunClient b, PunClient c) ThreePlayerRoom()
    {
        var a = Client("a");
        Assert.Equal(0, a.CreateRoom("room-" + Guid.NewGuid().ToString("N")[..8], GameProps(), LobbyProps, 4).ReturnCode);
        var b = Client("b");
        Assert.Equal(0, b.JoinRoom(a.RoomName!).ReturnCode);
        var c = Client("c");
        Assert.Equal(0, c.JoinRoom(a.RoomName!).ReturnCode);
        a.WaitEvent(255, e => (int)e[254] == 3);
        b.WaitEvent(255, e => (int)e[254] == 3);
        foreach (var x in new[] { a, b, c }) x.ClearEvents();
        return (a, b, c);
    }

    public void Dispose()
    {
        foreach (var c in clients) c.Dispose();
        cts.Cancel();
        run.Wait(2000);
    }
}
#endif
