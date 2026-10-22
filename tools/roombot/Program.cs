// roombot: creates a Bridge Crew-style public room on a stbc-server with the game's own Photon client
// library, so the room list / join-by-code can be tested with one copy of the game. It can't host a
// game, so by default it leaves as soon as a player joins, which hands the room (master client) to them.
//
// Usage: dotnet run -- [server:port] [code] [--stay]
//   defaults: 127.0.0.1:4530, TEST42
using ExitGames.Client.Photon;
using Hashtable = ExitGames.Client.Photon.Hashtable;

var address = args.FirstOrDefault(a => a.Contains(':')) ?? "127.0.0.1:4530";
var code = args.FirstOrDefault(a => !a.Contains(':') && !a.StartsWith("--"))?.ToUpperInvariant() ?? "TEST42";
bool stay = args.Contains("--stay");
var roomName = "eu|" + code;

// Must match the game: matchmaking version "1.<minor>.<env>" + "_" + PUN version, seen in the server log.
const string AppVersion = "1.5._1.71";
const string Lobby = "NovaLobby";
const byte SqlLobby = 2;

var bot = new Bot();
Log($"connecting to {address}");
bot.Connect(address);
bot.EstablishEncryption();
var auth = bot.Op(230, new() { [220] = AppVersion, [224] = "", [210] = "eu", [225] = "roombot" }, encrypt: true);
var token = (string)auth[221];
Log("authenticated");

var created = bot.Op(227, new() { [255] = roomName, [213] = Lobby, [212] = SqlLobby });
if (created.ReturnCode != 0) { Log($"create failed on master: {created.ReturnCode} {created.DebugMessage}"); return 1; }
var gameServer = (string)created[230];

bot.Disconnect();
bot.Connect(gameServer);
bot.Op(230, new() { [221] = token });

// Room options exactly as the game's OOLKIMEFIPC.MakeRoomOptions / DHIBADKNCEB build them, for a public
// match waiting in the briefing room (C6 = 2) with English (C0 = 8) and TNG enabled (C9 = 1).
string[] lobbyProps = ["C0", "C1", "C2", "C3", "C5", "C6", "C4", "C7", "C8", "C9"];
var gameProps = new Hashtable
{
    [(byte)253] = true, [(byte)254] = true, [(byte)250] = lobbyProps, [(byte)255] = (byte)4, [(byte)249] = true,
    ["C0"] = 8, ["C1"] = 1, ["C2"] = 4, ["C3"] = 0, ["C5"] = 1, ["NPID"] = 0, ["C6"] = 2, ["C4"] = 0,
    ["C7"] = roomName, ["C8"] = 0, ["C9"] = 1,
};
var gs = bot.Op(227, new()
{
    [255] = roomName, [213] = Lobby, [212] = SqlLobby,
    [249] = new Hashtable { [(byte)255] = "RoomBot" }, [250] = true, [248] = gameProps, [241] = true,
});
if (gs.ReturnCode != 0) { Log($"create failed: {gs.ReturnCode} {gs.DebugMessage}"); return 1; }
Log($"room {roomName} is up. Join it with code {code} (F7) or from the list. Waiting for a player...");

var deadline = DateTime.UtcNow.AddMinutes(30);
while (DateTime.UtcNow < deadline)
{
    bot.Service();
    foreach (var ev in bot.TakeEvents())
    {
        int actor = ev.Parameters.ContainsKey(254) ? (int)ev[254] : 0;
        switch (ev.Code)
        {
            case 255 when actor != 1:
                var props = ev[249] as Hashtable;
                Log($"player joined: actor {actor} '{props?[(byte)255]}'");
                if (!stay)
                {
                    Log("leaving, so the joining player becomes the host");
                    bot.Op(254, new());
                    bot.Disconnect();
                    Log("done");
                    return 0;
                }
                break;
            case 254:
                Log($"actor {actor} left");
                break;
            case 255:
                break;
            default:
                Log($"event {ev.Code} from actor {actor}");
                break;
        }
    }
    Thread.Sleep(10);
}
Log("nobody joined in 30 minutes; closing");
bot.Disconnect();
return 0;

static void Log(string m) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {m}");

sealed class Bot : IPhotonPeerListener
{
    private PhotonPeer peer;
    private readonly List<StatusCode> statuses = [];
    private readonly List<OperationResponse> responses = [];
    private readonly List<EventData> events = [];

    public void Connect(string address)
    {
        peer = new PhotonPeer(this, ConnectionProtocol.Tcp);
        statuses.Clear();
        if (!peer.Connect(address, "stbc")) throw new Exception("connect failed");
        Wait(() => statuses.Contains(StatusCode.Connect), "connect");
    }

    public void EstablishEncryption()
    {
        peer.EstablishEncryption();
        Wait(() => statuses.Contains(StatusCode.EncryptionEstablished), "encryption");
    }

    public void Disconnect()
    {
        peer.Disconnect();
        Wait(() => statuses.Contains(StatusCode.Disconnect), "disconnect");
    }

    public OperationResponse Op(byte code, Dictionary<byte, object> parameters, bool encrypt = false)
    {
        int before = responses.Count;
        if (!peer.OpCustom(code, parameters, true, 0, encrypt)) throw new Exception($"op {code} not sent");
        OperationResponse result = null;
        Wait(() => (result = responses.Skip(before).FirstOrDefault(r => r.OperationCode == code)) != null, $"op {code}");
        return result;
    }

    public void Service() => peer.Service();

    public List<EventData> TakeEvents()
    {
        var list = events.ToList();
        events.Clear();
        return list;
    }

    private void Wait(Func<bool> done, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"timed out waiting for {what}");
            peer.Service();
            Thread.Sleep(5);
        }
    }

    public void DebugReturn(DebugLevel level, string message)
    {
        if (level <= DebugLevel.WARNING) Console.WriteLine($"[photon] {message}");
    }

    public void OnOperationResponse(OperationResponse r) => responses.Add(r);
    public void OnStatusChanged(StatusCode s) => statuses.Add(s);
    public void OnEvent(EventData e) => events.Add(e);
}
