using System.Net;
using System.Text;
using System.Text.Json;
using StbcServer.Game;

namespace StbcServer.Web;

/// <summary>
/// Read-only HTTP status: a room browser page at /, JSON at /api/rooms and /api/status.
/// Uses HttpListener so the server has no web framework dependency.
/// </summary>
public sealed class WebStatus(LoadBalancingServer server, string bind, int port)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private readonly DateTime started = DateTime.UtcNow;
    private HttpListener? listener;

    public void Start()
    {
        listener = new HttpListener();
        listener.Prefixes.Add($"http://{(bind is "0.0.0.0" or "::" or "" ? "*" : bind)}:{port}/");
        listener.Start();
        Log.Info($"room browser on http://{(bind is "0.0.0.0" or "::" or "" ? "localhost" : bind)}:{port}/");
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (listener == null) Start();
        using var reg = ct.Register(() => listener!.Stop());
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await listener!.GetContextAsync(); }
            catch (Exception) when (ct.IsCancellationRequested) { break; }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            _ = Task.Run(() => Handle(ctx), ct);
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            switch (path)
            {
                case "":
                case "/index.html":
                    Respond(ctx, 200, "text/html; charset=utf-8", Page);
                    break;
                case "/api/rooms":
                    Respond(ctx, 200, "application/json", JsonSerializer.Serialize(PublicRooms(), Json));
                    break;
                case "/api/status":
                    Respond(ctx, 200, "application/json", JsonSerializer.Serialize(Status(), Json));
                    break;
                default:
                    Respond(ctx, 404, "text/plain", "not found");
                    break;
            }
        }
        catch (Exception e)
        {
            Log.Warn($"http {ctx.Request.Url}: {e.Message}");
            try { Respond(ctx, 500, "text/plain", "error"); } catch { /* client gone */ }
        }
    }

    public List<RoomInfo> PublicRooms() =>
        server.SnapshotRooms().Where(r => r.Visible).Select(RoomInfo.From).OrderByDescending(r => r.Open).ThenBy(r => r.AgeMinutes).ToList();

    public object Status()
    {
        var rooms = server.SnapshotRooms();
        return new
        {
            uptimeMinutes = (int)(DateTime.UtcNow - started).TotalMinutes,
            connections = server.PeerCount,
            playersInRooms = rooms.Sum(r => r.Players),
            publicRooms = rooms.Count(r => r.Visible),
            privateRooms = rooms.Count(r => !r.Visible),
        };
    }

    private static void Respond(HttpListenerContext ctx, int status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType;
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    private const string Page = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Bridge Crew Server</title>
<style>
  :root { --bg:#0b1020; --panel:#141b33; --text:#e6e9f2; --muted:#8b93ad; --accent:#f2a93b; --ok:#5ec27a; --line:#232c4d; }
  @media (prefers-color-scheme: light) { :root { --bg:#f4f5fa; --panel:#ffffff; --text:#1a1f33; --muted:#5d6480; --accent:#b86e00; --ok:#1f8a44; --line:#e1e4ee; } }
  * { box-sizing: border-box; }
  body { margin:0; background:var(--bg); color:var(--text); font:15px/1.45 system-ui, sans-serif; }
  main { max-width: 960px; margin: 0 auto; padding: 24px 16px 48px; }
  h1 { font-size: 22px; margin: 0 0 4px; letter-spacing: .02em; }
  .sub { color: var(--muted); margin-bottom: 20px; }
  .stats { display:flex; gap:12px; flex-wrap:wrap; margin-bottom: 20px; }
  .stat { background:var(--panel); border:1px solid var(--line); border-radius:10px; padding:10px 14px; min-width:120px; }
  .stat b { display:block; font-size: 22px; }
  .stat span { color: var(--muted); font-size: 13px; }
  .room { background:var(--panel); border:1px solid var(--line); border-radius:10px; padding:14px 16px; margin-bottom:10px;
          display:grid; grid-template-columns: 1fr auto; gap: 6px 16px; }
  .code { font: 600 18px ui-monospace, monospace; letter-spacing: .08em; color: var(--accent); }
  .meta { color: var(--muted); font-size: 13px; }
  .players { grid-column: 1 / -1; font-size: 14px; }
  .seats { font-weight: 600; text-align: right; }
  .open { color: var(--ok); }
  button { background: transparent; color: var(--text); border: 1px solid var(--line); border-radius: 6px; padding: 2px 8px; cursor: pointer; font-size: 12px; margin-left: 8px; }
  .empty { color: var(--muted); padding: 24px; text-align: center; background: var(--panel); border-radius: 10px; border: 1px dashed var(--line); }
  footer { color: var(--muted); font-size: 13px; margin-top: 24px; }
</style>
</head>
<body>
<main>
  <h1>Bridge Crew Server</h1>
  <div class="sub">Public rooms on this server. Join from the game's room browser (F7) or by room code.</div>
  <div class="stats" id="stats"></div>
  <div id="rooms"></div>
  <footer>Private rooms are not listed; ask the host for their room code. Refreshes every 5 seconds.</footer>
</main>
<script>
const esc = s => String(s).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
async function refresh() {
  try {
    const [rooms, status] = await Promise.all([fetch('api/rooms').then(r => r.json()), fetch('api/status').then(r => r.json())]);
    document.getElementById('stats').innerHTML =
      [['Players in rooms', status.playersInRooms], ['Public rooms', status.publicRooms], ['Private rooms', status.privateRooms], ['Connections', status.connections]]
        .map(([k, v]) => `<div class="stat"><b>${v}</b><span>${k}</span></div>`).join('');
    document.getElementById('rooms').innerHTML = rooms.length === 0
      ? '<div class="empty">No public rooms right now.</div>'
      : rooms.map(r => `
        <div class="room">
          <div><span class="code">${esc(r.code)}</span><button onclick="navigator.clipboard.writeText('${esc(r.code)}')">copy</button>
            <div class="meta">${esc(r.mode)} · ${esc(r.phase)} · ${esc(r.language)}${r.nextGeneration ? ' · TNG' : ''} · ${r.ageMinutes} min</div></div>
          <div class="seats ${r.open && r.players < r.maxPlayers ? 'open' : ''}">${r.players} / ${r.maxPlayers || '∞'}${r.open ? '' : ' · closed'}</div>
          <div class="players">${r.playerNames.map(esc).join(', ')}</div>
        </div>`).join('');
  } catch (e) {
    document.getElementById('rooms').innerHTML = '<div class="empty">Server not reachable.</div>';
  }
}
refresh();
setInterval(refresh, 5000);
</script>
</body>
</html>
""";
}
