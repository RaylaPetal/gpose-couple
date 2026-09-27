using CoPose.Core;

namespace CoPose.Tests.Sync;

public class IntervalGateTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(1_000L)]
    [InlineData(long.MaxValue - 10)]
    public void FirstCall_Passes(long now)
    {
        Assert.True(new IntervalGate(1000).TryPass(now));
    }

    [Fact]
    public void WithinInterval_IsBlocked_AtIntervalPasses()
    {
        var gate = new IntervalGate(1000);
        Assert.True(gate.TryPass(5_000));
        Assert.False(gate.TryPass(5_001));
        Assert.False(gate.TryPass(5_999));
        Assert.True(gate.TryPass(6_000));
        Assert.False(gate.TryPass(6_500));
    }

    [Fact]
    public void Reset_LetsNextCallPass()
    {
        var gate = new IntervalGate(1000);
        gate.TryPass(10_000);

        gate.Reset();

        Assert.True(gate.TryPass(10_001));
    }

    [Fact]
    public void LargeTimestamps_DoNotOverflow()
    {
        var gate = new IntervalGate(1000);
        var now = long.MaxValue - 5_000;
        Assert.True(gate.TryPass(now));
        Assert.False(gate.TryPass(now + 500));
        Assert.True(gate.TryPass(now + 1_000));
    }

    [Fact]
    public void IsOpen_DoesNotRecord_MarkDoes()
    {
        var gate = new IntervalGate(1000);
        Assert.True(gate.IsOpen(0));
        Assert.True(gate.IsOpen(0));

        gate.Mark(100);

        Assert.False(gate.IsOpen(600));
        Assert.True(gate.IsOpen(1_100));
    }
}
