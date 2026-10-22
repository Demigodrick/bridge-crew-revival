using System.Net;
using System.Net.Sockets;
using StbcServer.Game;

namespace StbcServer.Net;

/// <summary>Accepts TCP clients and runs the LoadBalancing server until cancelled.</summary>
public sealed class PhotonListener(ServerConfig config, IPAddress bind, int port, TimeSpan? tickInterval = null)
{
    public LoadBalancingServer Server { get; } = new(config);
    private TcpListener? listener;

    /// <summary>The bound endpoint (useful when port = 0 in tests).</summary>
    public IPEndPoint? LocalEndPoint => listener?.LocalEndpoint as IPEndPoint;

    public void Start()
    {
        listener = new TcpListener(bind, port);
        if (bind.Equals(IPAddress.IPv6Any)) listener.Server.DualMode = true;
        listener.Start();
        Log.Info($"listening on {listener.LocalEndpoint} (TCP)" +
                 (string.IsNullOrEmpty(config.PublicAddress) ? "" : $", advertising {config.PublicAddress}"));
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (listener == null) Start();
        using var timer = new PeriodicTimer(tickInterval ?? TimeSpan.FromSeconds(1));
        var tickTask = Task.Run(async () =>
        {
            try
            {
                while (await timer.WaitForNextTickAsync(ct)) Server.Tick();
            }
            catch (OperationCanceledException) { }
        }, ct);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener!.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => TcpTransport.RunAsync(client, Server, LocalEndPoint?.Port ?? port, ct), ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            listener!.Stop();
            await tickTask;
        }
    }
}
