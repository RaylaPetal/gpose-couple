using System.Numerics;
using CoPose.Core.Sync;

namespace CoPose.Tests.Sync;

public class PoseDiffTests
{
    private static BoneSample Identity => new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    [Theory]
    [InlineData(0.00009f, false)]
    [InlineData(0.00011f, true)]
    public void PositionThreshold(float delta, bool expected)
    {
        var b = Identity;
        b.Position.Y += delta;
        Assert.Equal(expected, PoseDiff.Changed(Identity, b));
    }

    [Theory]
    [InlineData(0.00009f, false)]
    [InlineData(0.00011f, true)]
    public void ScaleThreshold(float delta, bool expected)
    {
        var b = Identity;
        b.Scale.Z += delta;
        Assert.Equal(expected, PoseDiff.Changed(Identity, b));
    }

    [Theory]
    [InlineData(0.001f, false)] // 1 - cos(0.0005) ~ 1.25e-7
    [InlineData(0.02f, true)]   // 1 - cos(0.01) ~ 5e-5
    public void RotationThreshold(float radians, bool expected)
    {
        var b = Identity;
        b.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, radians);
        Assert.Equal(expected, PoseDiff.Changed(Identity, b));
    }

    [Fact]
    public void NegatedQuaternion_IsSameRotation()
    {
        var q = Quaternion.CreateFromYawPitchRoll(0.3f, 0.2f, 0.1f);
        var a = Identity;
        var b = Identity;
        a.Rotation = q;
        b.Rotation = Quaternion.Negate(q);
        Assert.False(PoseDiff.Changed(a, b));
    }

    [Fact]
    public void FindChanged_SkipsInFlightBones()
    {
        var committed = new[] { Identity, Identity, Identity };
        var live = new[] { Identity, Identity, Identity };
        live[0].Position.X = 1;
        live[2].Position.X = 1;
        var changed = new List<int>();

        PoseDiff.FindChanged(committed, live, [0, 0, 1], changed);

        Assert.Equal([0], changed);
    }

    [Fact]
    public void FindChanged_DoesNotAllocateInSteadyState()
    {
        var committed = Enumerable.Repeat(Identity, 250).ToArray();
        var live = Enumerable.Repeat(Identity, 250).ToArray();
        live[10].Position.X = 1;
        var skip = new int[250];
        var changed = new List<int>(16);

        long Measure()
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
            {
                changed.Clear();
                PoseDiff.FindChanged(committed, live, skip, changed);
            }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        // Warm up past tiered JIT recompilation, which can itself allocate on this thread.
        for (var i = 0; i < 5; i++)
            Measure();
        var allocated = Enumerable.Range(0, 5).Min(_ => Measure());

        Assert.Equal(0, allocated);
    }
}
