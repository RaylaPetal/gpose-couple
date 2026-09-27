using System.Net;
using System.Net.Sockets;
using CoPose.Core.Net;
using CoPose.Protocol;

namespace CoPose.Tests.Net;

internal static class NetTestHelpers
{
    public static readonly ConnectionOptions FastConnection = new()
    {
        PingInterval = TimeSpan.FromMilliseconds(100),
        IdleTimeout = TimeSpan.FromMilliseconds(500),
        FlushTimeout = TimeSpan.FromMilliseconds(500),
    };

    public static PeerInfo Peer(string name) => new(Guid.NewGuid(), new ActorKey(name, 73), name);

    public static SessionHost StartHost(string name = "Host", ConnectionOptions? connection = null, TimeSpan? helloTimeout = null) =>
        SessionHost.Start(Peer(name), new SessionHostOptions
        {
            Port = 0,
            BindAddress = IPAddress.Loopback,
            HelloTimeout = helloTimeout ?? TimeSpan.FromMilliseconds(300),
            Connection = connection ?? FastConnection,
        });

    public static Invite InviteFor(SessionHost host) => host.CreateInvite([IPAddress.Loopback]);

    public static Task<SessionClient> Join(SessionHost host, string name = "Guest", ConnectionOptions? connection = null) =>
        SessionClient.ConnectAsync(InviteFor(host), Peer(name), new SessionClientOptions { Connection = connection ?? FastConnection });

    public static async Task<SessionEvent> Next(ISessionTransport transport, int timeoutMs = 3000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        return await transport.Events.ReadAsync(cts.Token);
    }

    public static async Task<T> Next<T>(ISessionTransport transport, int timeoutMs = 3000) where T : SessionEvent
    {
        var e = await Next(transport, timeoutMs);
        return Assert.IsType<T>(e);
    }

    /// <summary>A raw TCP connection to the host, for speaking (or not speaking) the protocol by hand.</summary>
    public static async Task<(Socket Socket, NetworkStream Stream)> Raw(SessionHost host)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, host.Port));
        return (socket, new NetworkStream(socket, ownsSocket: true));
    }

    /// <summary>Reads until the peer closes; returns the frames seen. Fails if the peer is still open after the timeout.</summary>
    public static async Task<List<Frame>> ReadUntilClosed(Stream stream, int timeoutMs = 3000)
    {
        var frames = new List<Frame>();
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            while (await FrameCodec.ReadAsync(stream, cts.Token) is { } frame)
                frames.Add(frame);
        }
        catch (IOException)
        {
            // reset by peer counts as closed
        }
        return frames;
    }
}
