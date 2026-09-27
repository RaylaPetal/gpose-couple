using System.Net;
using CoPose.Core.Net;
using CoPose.Protocol;
using static CoPose.Tests.Net.NetTestHelpers;

namespace CoPose.Tests.Net;

public class SessionClientTests
{
    [Fact]
    public async Task UnreachableFirstAddress_FallsThroughToSecond()
    {
        await using var host = StartHost();

        // Nothing listens on 127.0.0.2 (the host is bound to 127.0.0.1 only).
        var invite = host.CreateInvite([IPAddress.Parse("127.0.0.2"), IPAddress.Loopback]);
        await using var guest = await SessionClient.ConnectAsync(invite, Peer("Guest"));

        Assert.Equal(IPAddress.Loopback, guest.HostEndPoint.Address);
    }

    [Fact]
    public async Task JoinsThroughTunnelOnDifferentPort()
    {
        await using var host = StartHost();
        using var tunnel = new TcpForwarder(host.Port);

        // Like a playit.gg/bore tunnel: a public endpoint whose port differs from the host's listen port.
        var invite = host.CreateInvite([
            new IPEndPoint(IPAddress.Parse("127.0.0.2"), host.Port), // unreachable "LAN" entry
            new IPEndPoint(IPAddress.Loopback, tunnel.Port),
        ]);
        await using var guest = await SessionClient.ConnectAsync(Invite.Decode(invite.Encode()), Peer("Guest"));

        Assert.Equal(tunnel.Port, guest.HostEndPoint.Port);
        Assert.NotEqual(host.Port, tunnel.Port);
        await Next<PeerJoinedEvent>(host.Local);
        guest.Send(MsgType.Presence, [1]);
        Assert.Equal(guest.Self.ClientId, (await Next<MessageEvent>(host.Local)).Envelope.SenderId);
    }

    [Fact]
    public async Task NoReachableAddress_ReportsHostUnreachableWithHint()
    {
        await using var host = StartHost();
        var invite = host.CreateInvite([IPAddress.Parse("127.0.0.2")]);

        var ex = await Assert.ThrowsAsync<SessionException>(() => SessionClient.ConnectAsync(invite, Peer("Guest")));
        Assert.Equal(SessionError.HostUnreachable, ex.Error);
        Assert.Contains("port-forward", ex.Message);
    }

    [Fact]
    public async Task RejectedJoin_SurfacesReason()
    {
        await using var host = StartHost();
        await using var first = await Join(host);

        var ex = await Assert.ThrowsAsync<SessionException>(() => Join(host, "Second"));
        Assert.Equal(SessionError.SessionFull, ex.Error);
        Assert.Contains("full", ex.Message);
    }

    [Fact]
    public async Task MessagesRightAfterWelcome_AreNotLost()
    {
        await using var host = StartHost();

        // The host announces itself as soon as the guest joins; that must reach the guest.
        var joinTask = Join(host);
        await Next<PeerJoinedEvent>(host.Local);
        host.Local.Send(MsgType.Presence, [1]);
        await using var guest = await joinTask;

        var message = await Next<MessageEvent>(guest);
        Assert.Equal(host.Local.Self.ClientId, message.Envelope.SenderId);
    }

    [Fact]
    public async Task OversizedBody_IsRefusedBeforeSending()
    {
        await using var host = StartHost();
        await using var guest = await Join(host);

        Assert.Equal(SendResult.TooLarge, guest.Send(MsgType.FullSnapshot, new byte[ProtocolInfo.MaxBodyBytes + 1]));
    }
}
