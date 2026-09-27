using System.Numerics;
using CoPose.Core.Sync;

namespace CoPose.Tests.Sync;

public class ActorStateTests
{
    private static PoseBuffer Buffer(long layout, params string[] names)
    {
        var buffer = new PoseBuffer();
        buffer.EnsureCapacity(names.Length);
        for (var i = 0; i < names.Length; i++)
        {
            buffer.Names[i] = names[i];
            buffer.Samples[i] = new BoneSample(new Vector3(i, 0, 0), Quaternion.Identity, Vector3.One);
        }
        buffer.Count = names.Length;
        buffer.LayoutId = layout;
        return buffer;
    }

    [Fact]
    public void Rebuild_TakesBufferAsBaseline()
    {
        var state = new ActorState();
        state.Rebuild(Buffer(7, "a", "b"));

        Assert.True(state.Initialized);
        Assert.Equal(7, state.LayoutId);
        Assert.True(state.TryGetIndex("b", out var i));
        Assert.Equal(1, i);
        Assert.Equal(new Vector3(1, 0, 0), state.Committed[1].Position);
        Assert.False(state.AnyPending());
    }

    [Fact]
    public void Acknowledge_ClearsOnlyMatchingSequence()
    {
        var state = new ActorState();
        state.Rebuild(Buffer(1, "a"));

        state.Pending[0] = 5;
        state.Acknowledge(0, 4); // an older message's echo: a newer edit is still in flight
        Assert.Equal(5, state.Pending[0]);

        state.Acknowledge(0, 5);
        Assert.Equal(0, state.Pending[0]);
    }

    [Fact]
    public void Rebuild_ResetsPendingInFlightAndBumpsVersion()
    {
        var state = new ActorState();
        state.Rebuild(Buffer(1, "a"));
        state.Pending[0] = 3;
        state.InFlight[0] = 2;
        state.WholeInFlight = 1;
        var version = state.Version;

        state.Rebuild(Buffer(2, "a", "c"));

        Assert.Equal(0, state.Pending[0]);
        Assert.Equal(0, state.InFlight[0]);
        Assert.Equal(0, state.WholeInFlight);
        Assert.NotEqual(version, state.Version);
        Assert.False(state.TryGetIndex("b", out _));
    }
}
