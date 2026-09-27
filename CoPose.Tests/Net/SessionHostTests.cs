using System.Buffers.Binary;
using System.Diagnostics;
using CoPose.Core.Net;
using CoPose.Protocol;
using static CoPose.Tests.Net.NetTestHelpers;

namespace CoPose.Tests.Net;

public class SessionHostTests
{
    [Fact]
    public async Task HostStarted_ListsOnlyItself()
    {
        await using var host = StartHost();

        Assert.True(host.Port > 0);
        var self = Assert.Single(host.Local.InitialParticipants);
        Assert.Equal(host.Local.Self, self);
        Assert.False(host.HasGuest);
    }

    [Fact]
    public async Task PortInUse_FailsWithPortInMessage()
    {
        await using var first = StartHost();

        var ex = Assert.Throws<SessionException>(() => SessionHost.Start(Peer("B"), new SessionHostOptions
        {
            Port = first.Port,
            BindAddress = System.Net.IPAddress.Loopback,
        }));
        Assert.Equal(SessionError.PortInUse, ex.Error);
        Assert.Contains(first.Port.ToString(), ex.Message);
    }

    [Fact]
    public async Task SuccessfulJoin_WelcomesGuestAndNotifiesHost()
    {
        await using var host = StartHost();
        await using var guest = await Join(host);

        Assert.Equal([host.Local.Self.ClientId, guest.Self.ClientId], guest.InitialParticipants.Select(p => p.ClientId));
        var joined = await Next<PeerJoinedEvent>(host.Local);
        Assert.Equal(guest.Self, joined.Peer);
        Assert.True(host.HasGuest);
    }

    [Fact]
    public async Task WrongSecret_IsRejectedAsInvalidInvite()
    {
        await using var host = StartHost();
        var invite = InviteFor(host) with { Secret = new byte[ProtocolInfo.SecretLength] };

        var ex = await Assert.ThrowsAsync<SessionException>(() => SessionClient.ConnectAsync(invite, Peer("Mallory")));
        Assert.Equal(SessionError.InvalidInvite, ex.Error);
        Assert.False(host.HasGuest);
    }

    [Fact]
    public async Task NoHello_ConnectionIsClosed()
    {
        await using var host = StartHost(helloTimeout: TimeSpan.FromMilliseconds(300));
        var (_, stream) = await Raw(host);
        await using var _s = stream;

        var watch = Stopwatch.StartNew();
        await ReadUntilClosed(stream);
        Assert.True(watch.ElapsedMilliseconds < 2500, $"closed after {watch.ElapsedMilliseconds} ms");
        Assert.False(host.HasGuest);
    }

    [Fact]
    public async Task VersionMismatch_IsRejectedNamingBothVersions()
    {
        await using var host = StartHost();
        var (_, stream) = await Raw(host);
        await using var _s = stream;

        await FrameCodec.WriteAsync(stream, new HelloFrame(99, host.Secret, Peer("Old")));
        var frames = await ReadUntilClosed(stream);

        var reject = Assert.IsType<RejectFrame>(Assert.Single(frames));
        Assert.Equal(RejectReasons.VersionMismatch, reject.Reason);
        Assert.Contains(ProtocolInfo.Version.ToString(), reject.Detail);
        Assert.Contains("99", reject.Detail);
    }

    [Fact]
    public async Task SessionFull_RejectsSecondGuestAndKeepsFirst()
    {
        await using var host = StartHost();
        await using var first = await Join(host, "First");

        var ex = await Assert.ThrowsAsync<SessionException>(() => Join(host, "Second"));
        Assert.Equal(SessionError.SessionFull, ex.Error);

        await Next<PeerJoinedEvent>(host.Local);
        Assert.Equal(SendResult.Ok, first.Send(MsgType.Presence, [1]));
        var message = await Next<MessageEvent>(first);
        Assert.Equal(first.Self.ClientId, message.Envelope.SenderId);
    }

    [Fact]
    public async Task GuestLeaves_HostNotifiedAndSlotFreed()
    {
        await using var host = StartHost();
        var guest = await Join(host);
        await Next<PeerJoinedEvent>(host.Local);

        await guest.LeaveAsync();
        var left = await Next<PeerLeftEvent>(host.Local);
        Assert.Equal(guest.Self.ClientId, left.ClientId);
        await guest.DisposeAsync();

        await using var again = await Join(host, "Again");
        Assert.Equal(again.Self, (await Next<PeerJoinedEvent>(host.Local)).Peer);
    }

    [Fact]
    public async Task SilentGuest_IsDroppedAfterIdleTimeout()
    {
        await using var host = StartHost();
        var (_, stream) = await Raw(host);
        await using var _s = stream;

        var peer = Peer("Silent");
        await FrameCodec.WriteAsync(stream, new HelloFrame(ProtocolInfo.Version, host.Secret, peer));
        await Next<PeerJoinedEvent>(host.Local);

        // Never answer or send pings; the host's 500 ms idle timeout must drop us.
        var left = await Next<PeerLeftEvent>(host.Local, 3000);
        Assert.Equal(peer.ClientId, left.ClientId);
    }

    [Fact]
    public async Task IdleButAliveConnection_StaysOpen()
    {
        await using var host = StartHost();
        await using var guest = await Join(host);
        await Next<PeerJoinedEvent>(host.Local);

        await Task.Delay(1500);

        Assert.False(host.Local.Events.TryRead(out var hostEvent), $"host saw {hostEvent}");
        Assert.False(guest.Events.TryRead(out var guestEvent), $"guest saw {guestEvent}");
        Assert.Equal(SendResult.Ok, guest.Send(MsgType.Presence, [1]));
        await Next<MessageEvent>(host.Local);
    }

    [Fact]
    public async Task StopHosting_EndsGuestSessionAndInvalidatesInvite()
    {
        var host = StartHost();
        var invite = InviteFor(host);
        await using var guest = await Join(host);

        await host.StopAsync();

        var ended = await Next<SessionEndedEvent>(guest);
        Assert.Contains("ended", ended.Reason);
        var ex = await Assert.ThrowsAsync<SessionException>(() => SessionClient.ConnectAsync(invite, Peer("Late")));
        Assert.Equal(SessionError.HostUnreachable, ex.Error);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task Messages_AreDeliveredToEveryoneIncludingSender()
    {
        await using var host = StartHost();
        await using var guest = await Join(host);
        await Next<PeerJoinedEvent>(host.Local);

        guest.Send(MsgType.Presence, [7]);

        var atHost = await Next<MessageEvent>(host.Local);
        var atGuest = await Next<MessageEvent>(guest);
        Assert.Equal(guest.Self.ClientId, atHost.Envelope.SenderId);
        Assert.Equal(atHost.Envelope.Seq, atGuest.Envelope.Seq);
        Assert.Equal([7], atGuest.Envelope.Body);
    }

    [Fact]
    public async Task ConcurrentSends_ArriveInSameStrictlyIncreasingOrderEverywhere()
    {
        const int perSide = 1000;
        await using var host = StartHost(connection: ConnectionOptions.Default);
        await using var guest = await Join(host, connection: ConnectionOptions.Default);
        await Next<PeerJoinedEvent>(host.Local);

        var body = new byte[16];
        var sends = new[]
        {
            Task.Run(() => { for (var i = 0; i < perSide; i++) host.Local.Send(MsgType.Presence, body); }),
            Task.Run(() => { for (var i = 0; i < perSide; i++) guest.Send(MsgType.Presence, body); }),
        };
        await Task.WhenAll(sends);

        async Task<List<Envelope>> Collect(ISessionTransport t)
        {
            var list = new List<Envelope>();
            while (list.Count < perSide * 2)
                list.Add((await Next<MessageEvent>(t, 10000)).Envelope);
            return list;
        }

        var atHost = await Collect(host.Local);
        var atGuest = await Collect(guest);

        Assert.Equal(atHost.Select(e => (e.Seq, e.SenderId)), atGuest.Select(e => (e.Seq, e.SenderId)));
        for (var i = 1; i < atHost.Count; i++)
            Assert.True(atHost[i].Seq > atHost[i - 1].Seq);
        Assert.Equal(perSide, atHost.Count(e => e.SenderId == host.Local.Self.ClientId));
        Assert.Equal(perSide, atGuest.Count(e => e.SenderId == guest.Self.ClientId));
    }

    [Fact]
    public async Task OversizedFrameFromGuest_DisconnectsWithoutReadingBody()
    {
        await using var host = StartHost();
        var (_, stream) = await Raw(host);
        await using var _s = stream;

        await FrameCodec.WriteAsync(stream, new HelloFrame(ProtocolInfo.Version, host.Secret, Peer("Big")));
        await Next<PeerJoinedEvent>(host.Local);

        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 200 * 1024);
        await stream.WriteAsync(header);

        await Next<PeerLeftEvent>(host.Local);
    }

    [Fact]
    public async Task OversizedLocalBody_IsRefusedBeforeSending()
    {
        await using var host = StartHost();
        Assert.Equal(SendResult.TooLarge, host.Local.Send(MsgType.FullSnapshot, new byte[ProtocolInfo.MaxBodyBytes + 1]));
    }
}
