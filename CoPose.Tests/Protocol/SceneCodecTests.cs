using System.Numerics;
using CoPose.Protocol;

namespace CoPose.Tests.Protocol;

public class SceneCodecTests
{
    private static readonly ActorKey Alice = new("Alice Example", 73);
    private static readonly ActorKey Bob = new("Bob Example", 40);

    /// <summary>A realistic full state: one character's whole pose (body, face, hair) with scale mostly 1.</summary>
    internal static StateMessage RealisticState(int bones = 300, int seed = 7)
    {
        var random = new Random(seed);
        var names = Enumerable.Range(0, bones).Select(i => $"j_{(i % 3 == 0 ? "f_" : "")}bone_{i:000}_{(i % 2 == 0 ? "l" : "r")}").ToArray();
        var values = new float[bones * TagActor.FloatsPerBone];
        for (var i = 0; i < bones; i++)
        {
            var o = i * TagActor.FloatsPerBone;
            values[o] = random.NextSingle() * 0.2f;
            values[o + 1] = random.NextSingle() * 0.2f;
            values[o + 2] = random.NextSingle() * 0.05f;
            var q = Quaternion.Normalize(new(random.NextSingle(), random.NextSingle(), random.NextSingle(), 1 + random.NextSingle()));
            values[o + 3] = q.X;
            values[o + 4] = q.Y;
            values[o + 5] = q.Z;
            values[o + 6] = q.W;
            values[o + 7] = values[o + 8] = values[o + 9] = 1f;
        }
        var clocks = Enumerable.Repeat(12u, bones).ToArray();
        return new StateMessage(Alice, true, [Alice, Bob], 12, [new TagActor(Alice, names, values, clocks)]);
    }

    [Fact]
    public void StateMessage_RoundTrips()
    {
        var state = RealisticState(bones: 20);

        var frame = SceneCodec.Encode(FrameKind.Full, state);

        Assert.Equal((byte)FrameKind.Full, frame[0]);
        Assert.True(SceneCodec.TryDecode(frame, out var kind, out var message));
        Assert.Equal(FrameKind.Full, kind);
        var back = Assert.IsType<StateMessage>(message);
        Assert.Equal(state.Self, back.Self);
        Assert.True(back.Ready);
        Assert.Equal(state.Resolved, back.Resolved);
        Assert.Equal(state.Clock, back.Clock);
        Assert.Equal(state.Actors[0].Names, back.Actors[0].Names);
        Assert.Equal(state.Actors[0].Values, back.Actors[0].Values);
        Assert.Equal(state.Actors[0].Clocks, back.Actors[0].Clocks);
    }

    [Fact]
    public void StopMessage_RoundTrips()
    {
        var frame = SceneCodec.Encode(FrameKind.Live, new StopMessage("stopped"));
        Assert.True(SceneCodec.TryDecode(frame, out var kind, out var message));
        Assert.Equal(FrameKind.Live, kind);
        Assert.Equal("stopped", Assert.IsType<StopMessage>(message).Reason);
    }

    [Fact]
    public void FullCharacterPose_FitsUnder32KB()
    {
        var frame = SceneCodec.Encode(FrameKind.Full, RealisticState(bones: 300));
        Assert.True(frame.Length < 32 * 1024, $"frame is {frame.Length} bytes");
    }

    [Theory]
    [InlineData(0x10)]
    [InlineData(0x11)]
    public void PresenceFrames_HaveNoBody(byte kind)
    {
        Assert.True(SceneCodec.TryDecode([kind], out var decoded, out var message));
        Assert.Equal((FrameKind)kind, decoded);
        Assert.Null(message);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x07, 1, 2 })]
    [InlineData(new byte[] { 0x01, 1, 2, 3 })]
    public void Garbage_IsRejected(byte[] frame)
    {
        Assert.False(SceneCodec.TryDecode(frame, out _, out _));
    }

    [Fact]
    public void InconsistentArrays_AreRejected()
    {
        var bad = new StateMessage(Alice, true, [], 1, [new TagActor(Alice, ["a", "b"], new float[10], [1, 1])]);
        Assert.False(SceneCodec.TryDecode(SceneCodec.Encode(FrameKind.Full, bad), out _, out _));
    }

    [Fact]
    public void OversizedFrame_IsRejected()
    {
        var frame = new byte[ProtocolInfo.MaxFrameBytes + 1];
        frame[0] = (byte)FrameKind.Full;
        Assert.False(SceneCodec.TryDecode(frame, out _, out _));
    }
}
