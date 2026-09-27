using CoPose.Core;
using CoPose.Core.Relay;
using CoPose.Core.Tags;
using CoPose.Protocol;
using static CoPose.Tests.Sync.TwoPlayers;

namespace CoPose.Tests.Sync;

public class PairingTests
{
    [Fact]
    public void PlayersDiscoverEachOther()
    {
        var p = new TwoPlayers();
        p.Run(3);

        var peer = Assert.Single(p.A.Pairing.Peers);
        Assert.Equal(KeyB, peer.Key);
        Assert.True(peer.Compatible);
        Assert.Equal(PairingStatus.Idle, p.A.Status);
        Assert.Equal(RelayStatus.Idle, p.SceneA.Status); // no relay connection outside a session
    }

    [Fact]
    public void RequestThenAccept_PairsBoth_AndJoinsTheSameRoom()
    {
        var p = new TwoPlayers();
        p.Run(3);

        Assert.True(p.A.Choose(KeyB));
        p.Run(8);
        Assert.Equal(PairingStatus.Waiting, p.A.Status);
        Assert.Equal(KeyA, Assert.Single(p.B.Pairing.Requests(KeyB)).Key);

        Assert.True(p.B.Choose(KeyA));
        p.Run(8);
        Assert.Equal(PairingStatus.Paired, p.A.Status);
        Assert.Equal(PairingStatus.Paired, p.B.Status);
        Assert.Equal(p.SceneA.Room, p.SceneB.Room);
        Assert.Equal(2, p.Relay.Participants(p.SceneA.Room!).Count);
        Assert.True(p.A.PartnerPresent);
    }

    [Fact]
    public void Tag_CarriesNonce_AndNoBones()
    {
        var p = new TwoPlayers().Paired();

        Assert.Equal(TagDecodeStatus.Ok, TagCodec.TryDecode(p.ChannelA.Current, out var tag, out _));
        Assert.Equal(p.A.Nonce, tag!.Nonce);
        Assert.Equal(KeyB, tag.Partner);
        Assert.True(p.ChannelA.Current!.Length < 200);
    }

    [Fact]
    public void OneSidedChoice_AppliesNothing_AndOpensNoRelay()
    {
        var p = new TwoPlayers();
        p.Run(3);
        p.A.Choose(KeyB);
        p.Run(3);

        p.WorldA.Pose(KeyB, 1, new(5, 5, 5));
        p.Run(10);

        Assert.Null(p.A.Session);
        Assert.Equal(RelayStatus.Idle, p.SceneA.Status);
        Assert.Empty(p.WorldB.Applies);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StopOverRelay_EndsBoth_EvenWithStaleTags(bool aStops)
    {
        var p = new TwoPlayers().Paired();
        var (stopper, other) = aStops ? (p.A, p.B) : (p.B, p.A);

        p.Hub.Deliver = false; // both in GPose: tag updates are deferred
        stopper.Stop();
        p.Run(2);

        Assert.Equal(PairingStatus.Idle, stopper.Status);
        Assert.Equal(PairingStatus.Idle, other.Status);
        Assert.Null(other.Session);
        Assert.Contains("stopped posing together", other.Pairing.EndReason);
    }

    [Fact]
    public void PartnerOffRelay_EndsAfterTimeout_WhenTagsAlsoStop()
    {
        var p = new TwoPlayers(new CoPoseOptions { PartnerAbsentTimeoutMs = 5_000, PartnerGraceMs = 2_000 }).Paired();

        p.SceneB.Drop();
        p.Hub.Inject(KeyB, null); // their tag is gone too
        p.Run(20);
        Assert.Equal(PairingStatus.Paired, p.A.Status); // still within the timeout

        p.Run(60);
        Assert.Equal(PairingStatus.Idle, p.A.Status);
        Assert.Contains("gone", p.A.Pairing.EndReason);
    }

    [Fact]
    public void BriefRelayDrop_KeepsSession()
    {
        var p = new TwoPlayers(new CoPoseOptions { PartnerAbsentTimeoutMs = 5_000 }).Paired();

        p.SceneB.Drop();
        p.Run(10);
        p.SceneB.Reconnect();
        p.Run(60);

        Assert.Equal(PairingStatus.Paired, p.A.Status);
        Assert.True(p.A.PartnerPresent);
    }

    [Fact]
    public void RelayPresence_KeepsSessionWhileTagsAreStale()
    {
        var p = new TwoPlayers(new CoPoseOptions { PartnerGraceMs = 1_000 }).Paired();

        p.Hub.Inject(KeyB, null); // looks gone by tag (deferred in GPose), but is on the relay
        p.Run(100);

        Assert.Equal(PairingStatus.Paired, p.A.Status);
    }

    [Fact]
    public void RelayUnreachable_IsReported_AndRetried()
    {
        var p = new TwoPlayers();
        p.Relay.Online = false;
        p.Run(3);
        p.A.Choose(KeyB);
        p.Run(8);
        p.B.Choose(KeyA);
        p.Run(8);

        Assert.Equal(RelayStatus.Unreachable, p.SceneA.Status);
        Assert.NotNull(p.SceneA.LastError);

        p.Relay.Online = true;
        p.SceneA.Reconnect();
        p.SceneB.Reconnect();
        p.Run(5);
        Assert.Equal(RelayStatus.Connected, p.SceneA.Status);
        Assert.True(p.A.PartnerPresent);
    }

    [Fact]
    public void IncompatibleVersion_IsListedButCannotBeChosen()
    {
        var p = new TwoPlayers();
        var old = new ActorKey("Oldie", 73);
        p.Hub.Inject(old, "CP2:whatever");
        p.Run(2);

        var peer = p.A.Pairing.Peers.Single(x => x.Key == old);
        Assert.False(peer.Compatible);
        Assert.Equal(2, peer.IncompatibleVersion);
        Assert.False(p.A.Choose(old));
    }

    [Fact]
    public void MalformedTags_AreIgnored()
    {
        var p = new TwoPlayers();
        p.Hub.Inject(new ActorKey("Noise", 1), "CP3:%%%");
        p.Run(2);

        Assert.DoesNotContain(p.A.Pairing.Peers, x => x.Key.Name == "Noise");
    }

    [Fact]
    public void Dispose_SendsStop_ClosesRelay_AndRemovesTag()
    {
        var p = new TwoPlayers().Paired();
        Assert.NotNull(p.ChannelA.Current);

        p.A.Dispose();
        p.Run(1);

        Assert.Null(p.ChannelA.Current);
        Assert.Equal(RelayStatus.Idle, p.SceneA.Status);
        Assert.Equal(PairingStatus.Idle, p.B.Status);
    }
}
