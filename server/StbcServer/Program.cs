using System.Net;
using StbcServer;
using StbcServer.Game;
using StbcServer.Net;
using StbcServer.Web;

// Configuration: environment variables, overridable with --key=value arguments.
//   PORT            TCP port (default 4530, Photon's TCP default)
//   BIND            listen address (default 0.0.0.0)
//   PUBLIC_ADDRESS  game server address handed to clients, host:port (default: the address they connected to)
//   HTTP_PORT       room browser web page / JSON API port (default 8080, 0 = off)
//   LIST_ROOMS      send room lists in the game's SQL lobby too, for the in-game browser (default true)
//   LOG_LEVEL       debug | info | warn | error (default info)
var settings = Environment.GetEnvironmentVariables().Keys.Cast<string>()
    .ToDictionary(k => k.ToUpperInvariant(), k => Environment.GetEnvironmentVariable(k)!);
foreach (var arg in args)
{
    var parts = arg.TrimStart('-').Split('=', 2);
    if (parts.Length == 2) settings[parts[0].Replace('-', '_').ToUpperInvariant()] = parts[1];
}
string? Setting(string key) => settings.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

Log.Level = Enum.TryParse<LogLevel>(Setting("LOG_LEVEL"), ignoreCase: true, out var level) ? level : LogLevel.Info;

var config = new ServerConfig
{
    PublicAddress = Setting("PUBLIC_ADDRESS"),
    ListRoomsInAllLobbies = !bool.TryParse(Setting("LIST_ROOMS"), out var listRooms) || listRooms,
};
int port = int.TryParse(Setting("PORT"), out var p) ? p : 4530;
var bindAddress = Setting("BIND") is { } bind ? IPAddress.Parse(bind) : IPAddress.Any;
int httpPort = int.TryParse(Setting("HTTP_PORT"), out var hp) ? hp : 8080;

// Not disposed: ProcessExit (docker stop) can fire after Main returns.
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

Log.Info("Star Trek: Bridge Crew community server (Photon LoadBalancing compatible, PUN 1.71)");
var listener = new PhotonListener(config, bindAddress, port);
listener.Start();
var tasks = new List<Task> { listener.RunAsync(cts.Token) };
if (httpPort > 0)
{
    try
    {
        var web = new WebStatus(listener.Server, Setting("BIND") ?? "0.0.0.0", httpPort);
        web.Start();
        tasks.Add(web.RunAsync(cts.Token));
    }
    catch (Exception e)
    {
        Log.Warn($"room browser web page disabled: {e.Message}");
    }
}
await Task.WhenAll(tasks);
Log.Info("stopped");
