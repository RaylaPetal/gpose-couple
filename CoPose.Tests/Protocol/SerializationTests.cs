using CoPose.Protocol;

namespace CoPose.Tests.Protocol;

public class SerializationTests
{
    private static readonly ActorKey Actor = new("Rayla Petal", 73);
    private static readonly PeerInfo Peer = new(Guid.NewGuid(), Actor, "Rayla");

    private static T RoundTrip<T>(T value) => Wire.Deserialize<T>(Wire.Serialize(value));

    [Fact]
    public void ActorKey_RoundTrips() => Assert.Equal(Actor, RoundTrip(Actor));

    [Fact]
    public void PeerInfo_RoundTrips() => Assert.Equal(Peer, RoundTrip(Peer));

    [Fact]
    public void BoneDelta_RoundTrips()
    {
        var delta = new BoneDelta(Actor, 42, [new BoneTransform("j_kosi", 1, 2, 3, 0.1f, 0.2f, 0.3f, 0.9f, 1, 1.5f, 2)]);
        var back = RoundTrip(delta);
        Assert.Equal(delta.Actor, back.Actor);
        Assert.Equal(delta.LocalSeq, back.LocalSeq);
        Assert.Equal(delta.Bones, back.Bones);
    }

    [Fact]
    public void FullSnapshot_RoundTrips()
    {
        var snapshot = new FullSnapshot(Actor, 7, [1, 2, 3]);
        var back = RoundTrip(snapshot);
        Assert.Equal(snapshot.Actor, back.Actor);
        Assert.Equal(snapshot.LocalSeq, back.LocalSeq);
        Assert.Equal(snapshot.PoseJsonBrotli, back.PoseJsonBrotli);
    }

    [Fact]
    public void Presence_RoundTrips()
    {
        var presence = new Presence(true, [Actor, new ActorKey("Other", 1)]);
        var back = RoundTrip(presence);
        Assert.True(back.Ready);
        Assert.Equal(presence.Resolved, back.Resolved);
    }

    [Fact]
    public void SnapshotRequest_RoundTrips() => Assert.Equal(new SnapshotRequest(Actor), RoundTrip(new SnapshotRequest(Actor)));

    [Fact]
    public void Envelope_RoundTrips()
    {
        var envelope = new Envelope(MsgType.BoneDelta, Guid.NewGuid(), 99, [9, 8]);
        var back = RoundTrip(envelope);
        Assert.Equal(envelope.Type, back.Type);
        Assert.Equal(envelope.SenderId, back.SenderId);
        Assert.Equal(envelope.Seq, back.Seq);
        Assert.Equal(envelope.Body, back.Body);
    }

    public static TheoryData<Frame> Frames() => new()
    {
        new HelloFrame(ProtocolInfo.Version, [1, 2, 3, 4, 5, 6, 7, 8], Peer),
        new WelcomeFrame(Guid.NewGuid(), [Peer]),
        new RejectFrame(RejectReasons.SessionFull, "session full"),
        new PeerJoinedFrame(Peer),
        new PeerLeftFrame(Guid.NewGuid()),
        new SendFrame(MsgType.Presence, [1]),
        new DeliverFrame(new Envelope(MsgType.Presence, Guid.NewGuid(), 1, [1])),
        new PingFrame(),
        new ByeFrame(ByeReasons.Left),
    };

    [Theory]
    [MemberData(nameof(Frames))]
    public void Frame_RoundTripsThroughUnion(Frame frame)
    {
        var back = RoundTrip(frame);
        Assert.IsType(frame.GetType(), back);
        // Records with array members compare arrays by reference, so compare the serialized form.
        Assert.Equal(Wire.Serialize(frame), Wire.Serialize(back));
    }
}
