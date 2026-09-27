using CoPose.Core;
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
    }

    [Fact]
    public void RequestThenAccept_PairsBoth()
    {
        var p = new TwoPlayers();
        p.Run(3);

        Assert.True(p.A.Choose(KeyB));
        p.Run(8); // the choice goes out with the next (throttled) publish
        Assert.Equal(PairingStatus.Waiting, p.A.Status);
        Assert.Equal(KeyA, Assert.Single(p.B.Pairing.Requests(KeyB)).Key);

        Assert.True(p.B.Choose(KeyA));
        p.Run(8);
        Assert.Equal(PairingStatus.Paired, p.A.Status);
        Assert.Equal(PairingStatus.Paired, p.B.Status);
        Assert.NotNull(p.A.Session);
        Assert.NotNull(p.B.Session);
        Assert.Empty(p.B.Pairing.Requests(KeyB));
    }

    [Fact]
    public void OneSidedChoice_AppliesNothing()
    {
        var p = new TwoPlayers();
        p.Run(3);
        p.A.Choose(KeyB);
        p.Run(3);

        p.WorldA.Pose(KeyB, 1, new(5, 5, 5));
        p.Run(10);

        Assert.Null(p.A.Session);
        Assert.Empty(p.WorldB.Applies);
        Assert.NotEqual(new(5, 5, 5), p.WorldB.PositionOf(KeyB, 1));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StopFromEitherSide_EndsBoth(bool aStops)
    {
        var p = new TwoPlayers().Paired();
        var (stopper, other) = aStops ? (p.A, p.B) : (p.B, p.A);

        stopper.Stop();
        p.Run(3);

        Assert.Equal(PairingStatus.Idle, stopper.Status);
        Assert.Equal(PairingStatus.Idle, other.Status);
        Assert.Null(other.Session);
        Assert.Contains("stopped posing together", other.Pairing.EndReason);
    }

    [Fact]
    public void PartnerTagGone_EndsAfterGracePeriod()
    {
        var p = new TwoPlayers(new CoPoseOptions { PartnerGraceMs = 2000 }).Paired();

        p.B.Dispose(); // removes B's tag
        p.Run(5, stepMs: 100);
        Assert.Equal(PairingStatus.Paired, p.A.Status); // still inside the grace period

        p.Run(20, stepMs: 100);
        Assert.Equal(PairingStatus.Idle, p.A.Status);
        Assert.Contains("gone", p.A.Pairing.EndReason);
        Assert.Empty(p.A.Pairing.Peers);
    }

    [Fact]
    public void BriefTagGap_DoesNotEndSession()
    {
        var p = new TwoPlayers(new CoPoseOptions { PartnerGraceMs = 2000 }).Paired();
        var tag = p.ChannelB.Current;

        p.Hub.Inject(KeyB, null);
        p.Run(3);
        p.Hub.Inject(KeyB, tag);
        p.Run(30);

        Assert.Equal(PairingStatus.Paired, p.A.Status);
    }

    [Fact]
    public void PartnerChoosingSomeoneElse_EndsSession()
    {
        var p = new TwoPlayers().Paired();
        var carol = new ActorKey("Carol", 73);
        p.Hub.Inject(carol, TagCodec.Encode(new TagState(ProtocolInfo.Version, carol, null, false, [], 0, [])));
        p.Run(2);

        p.B.Stop();
        Assert.True(p.B.Choose(carol));
        p.Run(3);

        Assert.Equal(PairingStatus.Idle, p.A.Status);
        Assert.NotNull(p.A.Pairing.EndReason);
    }

    [Fact]
    public void IncompatibleVersion_IsListedButCannotBeChosen()
    {
        var p = new TwoPlayers();
        var old = new ActorKey("Oldie", 73);
        p.Hub.Inject(old, "CP1:whatever");
        p.Run(2);

        var peer = p.A.Pairing.Peers.Single(x => x.Key == old);
        Assert.False(peer.Compatible);
        Assert.Equal(1, peer.IncompatibleVersion);
        Assert.False(p.A.Choose(old));
    }

    [Fact]
    public void MalformedTags_AreIgnored()
    {
        var p = new TwoPlayers();
        p.Hub.Inject(new ActorKey("Noise", 1), "CP2:%%%");
        p.Run(2);

        Assert.DoesNotContain(p.A.Pairing.Peers, x => x.Key.Name == "Noise");
    }

    [Fact]
    public void PartnerTags_AreCountedFromChoosing_NotOnlyOnceSessionStarts()
    {
        var p = new TwoPlayers();
        p.Run(3);

        Assert.True(p.A.Choose(KeyB));
        Assert.NotNull(p.A.Stats.LastPartnerTagAtMs); // B's announcement was already known
        Assert.Equal(0, p.A.Stats.Receives);

        // While A waits (no session yet), B's tag changes: that must count.
        p.Hub.Inject(KeyB, TagCodec.Encode(new TagState(ProtocolInfo.Version, KeyB, null, true, [], 0, [])));
        p.Hub.Flush();
        p.A.Tick();
        Assert.Equal(1, p.A.Stats.Receives);
        Assert.Null(p.A.Session);

        Assert.True(p.B.Choose(KeyA));
        p.Run(10);
        Assert.Equal(PairingStatus.Paired, p.A.Status);
        Assert.True(p.A.Stats.Receives >= 1);
        Assert.NotNull(p.A.Stats.LastPartnerTagAtMs);
    }

    [Fact]
    public void Dispose_RemovesTag()
    {
        var p = new TwoPlayers();
        p.Run(2);
        Assert.NotNull(p.ChannelA.Current);

        p.A.Dispose();

        Assert.Null(p.ChannelA.Current);
    }
}
