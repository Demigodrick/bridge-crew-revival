#if HAS_PHOTON_CLIENT
using System.Collections.Concurrent;
using ExitGames.Client.Photon;
using PhotonHashtable = ExitGames.Client.Photon.Hashtable;

namespace StbcServer.Tests;

/// <summary>
/// Drives the game's real PhotonPeer (TCP) through the same operation sequences PUN 1.71's
/// NetworkingPeer uses: key exchange + encrypted auth on master, token auth on the game server,
/// master create/join returning an address, then the game-server create/join with full options.
/// All PhotonPeer calls run on one service thread, like PUN's main thread.
/// </summary>
public sealed class PunClient : IPhotonPeerListener, IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly string masterAddress;
    private readonly ConcurrentQueue<Action> actions = new();
    private readonly CancellationTokenSource cts = new();
    private readonly Thread thread;
    private readonly object gate = new();
    private readonly List<StatusCode> statuses = [];
    private readonly List<OperationResponse> responses = [];
    private readonly List<EventData> events = [];
    private PhotonPeer peer = null!;

    public string UserId { get; }
    public string Name { get; }
    public string AppVersion { get; }
    public string? Token { get; private set; }
    public int ActorNr { get; private set; }
    public string? RoomName { get; private set; }

    public PunClient(string masterAddress, string name, string appVersion = "1.0.TEST")
    {
        TestTypes.Register();
        this.masterAddress = masterAddress;
        Name = name;
        UserId = "user-" + name;
        AppVersion = appVersion;
        thread = new Thread(ServiceLoop) { IsBackground = true, Name = "photon-" + name };
        thread.Start();
    }

    private void ServiceLoop()
    {
        while (!cts.IsCancellationRequested)
        {
            while (actions.TryDequeue(out var a)) a();
            peer?.Service();
            Thread.Sleep(2);
        }
    }

    private void Run(Action action)
    {
        var done = new ManualResetEventSlim();
        actions.Enqueue(() => { action(); done.Set(); });
        if (!done.Wait(Timeout)) throw new TimeoutException("service thread stuck");
    }

    // ---- Connection lifecycle (mirrors NetworkingPeer.OnStatusChanged / OnOperationResponse) ----

    public void ConnectToMaster()
    {
        Connect(masterAddress);
        if (Token == null)
        {
            // First connection: EstablishEncryption, wait for EncryptionEstablished, encrypted OpAuthenticate.
            Run(() => peer.EstablishEncryption());
            WaitStatus(StatusCode.EncryptionEstablished);
            var auth = new Dictionary<byte, object> { [220] = AppVersion, [224] = "", [210] = "none", [225] = UserId };
            var resp = Op(230, auth, encrypt: true);
            Assert.Equal(0, resp.ReturnCode);
            Token = (string)resp[221];
            Assert.Equal(UserId, resp[225]);
        }
        else
        {
            AuthWithToken();
        }
    }

    private void Connect(string address)
    {
        Run(() =>
        {
            peer?.Disconnect();
            peer = new PhotonPeer(this, ConnectionProtocol.Tcp);
            lock (gate) { statuses.Clear(); responses.Clear(); events.Clear(); }
            Assert.True(peer.Connect(address, "stbc"));
        });
        WaitStatus(StatusCode.Connect);
    }

    private void AuthWithToken()
    {
        // With a token PUN authenticates immediately and unencrypted (EstablishEncryption still runs).
        Run(() => peer.EstablishEncryption());
        var resp = Op(230, new Dictionary<byte, object> { [221] = Token! });
        Assert.Equal(0, resp.ReturnCode);
    }

    public void Disconnect()
    {
        Run(() => peer.Disconnect());
        WaitStatus(StatusCode.Disconnect);
    }

    public void JoinLobby(string? lobbyName = null, byte lobbyType = 0)
    {
        var p = new Dictionary<byte, object>();
        if (lobbyName != null) { p[213] = lobbyName; p[212] = lobbyType; }
        Assert.Equal(0, Op(229, p).ReturnCode);
    }

    /// <summary>Master CreateGame, then reconnect to the returned game server and create there.</summary>
    public OperationResponse CreateRoom(string? name, PhotonHashtable customProps, string[] lobbyProps, byte maxPlayers,
        bool isOpen = true, bool isVisible = true, string? lobbyName = null, byte lobbyType = 0)
    {
        var master = new Dictionary<byte, object>();
        if (name != null) master[255] = name;
        if (lobbyName != null) { master[213] = lobbyName; master[212] = lobbyType; }
        var resp = Op(227, master);
        if (resp.ReturnCode != 0) return resp;
        RoomName = (string)resp[255];
        var address = (string)resp[230];

        Disconnect();
        Connect(address);
        AuthWithToken();

        // LoadBalancingPeer.OpCreateRoom on the game server, with HNFBDBEDOPB's room options.
        var gameProps = new PhotonHashtable { [(byte)253] = isOpen, [(byte)254] = isVisible, [(byte)250] = lobbyProps };
        foreach (var key in customProps.Keys) gameProps[key] = customProps[key];
        if (maxPlayers > 0) gameProps[(byte)255] = maxPlayers;
        gameProps[(byte)249] = true;
        var gs = new Dictionary<byte, object>
        {
            [255] = RoomName, [249] = PlayerProps(), [250] = true, [248] = gameProps, [241] = true,
        };
        if (lobbyName != null) { gs[213] = lobbyName; gs[212] = lobbyType; }
        resp = Op(227, gs);
        if (resp.ReturnCode == 0) ActorNr = (int)resp[254];
        return resp;
    }

    public OperationResponse JoinRandom(string? sqlFilter = null, string? lobbyName = null, byte lobbyType = 0)
    {
        var p = new Dictionary<byte, object>();
        if (lobbyName != null) { p[213] = lobbyName; p[212] = lobbyType; }
        if (sqlFilter != null) p[245] = sqlFilter;
        var resp = Op(225, p);
        if (resp.ReturnCode != 0) return resp;
        return JoinOnGameServer((string)resp[255], (string)resp[230]);
    }

    public OperationResponse JoinRoom(string name)
    {
        var resp = Op(226, new Dictionary<byte, object> { [255] = name });
        if (resp.ReturnCode != 0) return resp;
        return JoinOnGameServer(name, (string)resp[230]);
    }

    private OperationResponse JoinOnGameServer(string name, string address)
    {
        RoomName = name;
        Disconnect();
        Connect(address);
        AuthWithToken();
        var resp = Op(226, new Dictionary<byte, object> { [255] = name, [249] = PlayerProps(), [250] = true });
        if (resp.ReturnCode == 0) ActorNr = (int)resp[254];
        return resp;
    }

    /// <summary>Leave the room and go back to the master server, like PUN does.</summary>
    public void LeaveRoom()
    {
        Assert.Equal(0, Op(254, new Dictionary<byte, object>()).ReturnCode);
        Disconnect();
        ConnectToMaster();
        RoomName = null;
        ActorNr = 0;
    }

    private PhotonHashtable PlayerProps() => new() { [(byte)255] = Name };

    // ---- Operations and waiting ------------------------------------------------------------

    public void Send(byte opCode, Dictionary<byte, object> parameters, bool reliable = true) =>
        Run(() => Assert.True(peer.OpCustom(opCode, parameters, reliable, 0, false)));

    public OperationResponse Op(byte opCode, Dictionary<byte, object> parameters, bool encrypt = false)
    {
        int before;
        lock (gate) before = responses.Count;
        Run(() => Assert.True(peer.OpCustom(opCode, parameters, true, 0, encrypt)));
        return WaitFor(() => responses.Skip(before).FirstOrDefault(r => r.OperationCode == opCode), $"response to op {opCode}");
    }

    public void RaiseEvent(byte code, object? data, byte cache = 0, byte receivers = 0, int[]? targets = null, bool reliable = true)
    {
        var p = new Dictionary<byte, object> { [244] = code };
        if (data != null) p[245] = data;
        if (cache != 0) p[247] = cache;
        if (receivers != 0) p[246] = receivers;
        if (targets != null) p[252] = targets;
        Send(253, p, reliable);
    }

    public EventData WaitEvent(byte code, Func<EventData, bool>? match = null) =>
        WaitFor(() => events.FirstOrDefault(e => e.Code == code && (match == null || match(e))), $"event {code} for {Name}");

    public List<EventData> Events(byte code)
    {
        lock (gate) return events.Where(e => e.Code == code).ToList();
    }

    public void ClearEvents()
    {
        lock (gate) events.Clear();
    }

    public void WaitStatus(StatusCode status) =>
        WaitFor(() => statuses.Contains(status) ? (object)status : null, $"status {status} for {Name}");

    private T WaitFor<T>(Func<T?> probe, string what) where T : class
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (gate)
            {
                var r = probe();
                if (r != null) return r;
            }
            Thread.Sleep(5);
        }
        lock (gate)
            throw new TimeoutException($"timed out waiting for {what}; statuses: {string.Join(",", statuses)}; " +
                                       $"responses: {string.Join(",", responses.Select(r => $"{r.OperationCode}:{r.ReturnCode}"))}");
    }

    public int ServerTime
    {
        get { int t = 0; Run(() => t = peer.ServerTimeInMilliSeconds); return t; }
    }

    // ---- IPhotonPeerListener ----------------------------------------------------------------

    public void DebugReturn(DebugLevel level, string message)
    {
        if (level <= DebugLevel.WARNING) Console.WriteLine($"[{Name}] {level}: {message}");
    }

    public void OnOperationResponse(OperationResponse operationResponse)
    {
        lock (gate) responses.Add(operationResponse);
    }

    public void OnStatusChanged(StatusCode statusCode)
    {
        lock (gate) statuses.Add(statusCode);
    }

    public void OnEvent(EventData eventData)
    {
        lock (gate) events.Add(eventData);
    }

    public void Dispose()
    {
        try { Run(() => peer?.Disconnect()); } catch { /* best effort */ }
        cts.Cancel();
        thread.Join(1000);
    }
}
#endif
