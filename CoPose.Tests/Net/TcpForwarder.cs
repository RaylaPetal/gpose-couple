using System.Net;
using System.Net.Sockets;

namespace CoPose.Tests.Net;

/// <summary>A minimal TCP tunnel on 127.0.0.1: accepts on its own port and pipes each connection to a target port.</summary>
internal sealed class TcpForwarder : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource cts = new();

    public TcpForwarder(int targetPort)
    {
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(() => AcceptLoop(targetPort));
    }

    public int Port { get; }

    private async Task AcceptLoop(int targetPort)
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var inbound = await listener.AcceptTcpClientAsync(cts.Token);
                var outbound = new TcpClient();
                await outbound.ConnectAsync(IPAddress.Loopback, targetPort, cts.Token);
                _ = Pipe(inbound, outbound);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private async Task Pipe(TcpClient a, TcpClient b)
    {
        using (a)
        using (b)
        {
            var sa = a.GetStream();
            var sb = b.GetStream();
            try
            {
                await Task.WhenAny(sa.CopyToAsync(sb, cts.Token), sb.CopyToAsync(sa, cts.Token));
            }
            catch (Exception)
            {
                // one side closed
            }
        }
    }

    public void Dispose()
    {
        cts.Cancel();
        listener.Stop();
        cts.Dispose();
    }
}
