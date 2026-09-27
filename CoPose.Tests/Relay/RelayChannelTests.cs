using CoPose.Core.Relay;
using CoPose.Protocol;

namespace CoPose.Tests.Relay;

public class RelayChannelTests
{
    private static readonly string Room = new('a', 64);
    private static readonly string Alice = new('1', 32);
    private static readonly string Bob = new('2', 32);

    private static async Task<T> Next<T>(RelayChannel channel, int timeoutMs = 5000) where T : ChannelEvent
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            while (channel.TryReceive(out var e))
            {
                if (e is T match)
                    return match;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException($"no {typeof(T).Name}");
    }

    private static async Task<byte[]> NextFrame(RelayChannel channel, FrameKind kind, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var frame = await Next<ChannelFrame>(channel, (int)Math.Max(1, deadline - Environment.TickCount64));
            if (frame.Frame[0] == (byte)kind)
                return frame.Frame;
        }
        throw new TimeoutException($"no {kind} frame");
    }

    [Fact]
    public async Task Connects_Forwards_AndReportsPresence()
    {
        await using var server = new LoopbackRelayServer();
        using var a = new RelayChannel(() => server.BaseUrl);
        using var b = new RelayChannel(() => server.BaseUrl);

        a.Join(Room, Alice);
        await Next<ChannelConnected>(a);
        Assert.Equal(RelayStatus.Connected, a.Status);
        b.Join(Room, Bob);
        await Next<ChannelConnected>(b);
        await NextFrame(a, FrameKind.PeerJoined);
        await NextFrame(b, FrameKind.PeerJoined);

        Assert.True(a.Send([(byte)FrameKind.Live, 1, 2, 3]));
        Assert.Equal(new byte[] { (byte)FrameKind.Live, 1, 2, 3 }, await NextFrame(b, FrameKind.Live));

        b.Leave();
        Assert.Equal(RelayStatus.Idle, b.Status);
        await NextFrame(a, FrameKind.PeerLeft);
    }

    [Fact]
    public async Task Reconnects_AfterTheServerDrops()
    {
        await using var server = new LoopbackRelayServer();
        using var a = new RelayChannel(() => server.BaseUrl, initialBackoff: TimeSpan.FromMilliseconds(50));

        a.Join(Room, Alice);
        await Next<ChannelConnected>(a);

        server.DropAll();
        var dropped = await Next<ChannelDisconnected>(a);
        Assert.False(string.IsNullOrEmpty(dropped.Reason));

        await Next<ChannelConnected>(a);
        Assert.Equal(RelayStatus.Connected, a.Status);
        Assert.True(server.Connections >= 2);
    }

    [Fact]
    public async Task Unreachable_ReportsErrorAndKeepsTrying()
    {
        int port;
        using (var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        }
        using var a = new RelayChannel(() => $"http://127.0.0.1:{port}", initialBackoff: TimeSpan.FromMilliseconds(50));

        a.Join(Room, Alice);
        await Next<ChannelDisconnected>(a);
        await Task.Delay(20);

        Assert.Equal(RelayStatus.Unreachable, a.Status);
        Assert.NotNull(a.LastError);
        Assert.False(a.Send([(byte)FrameKind.Live]));

        await Next<ChannelDisconnected>(a); // it tried again
        a.Leave();
        Assert.Equal(RelayStatus.Idle, a.Status);
    }
}
