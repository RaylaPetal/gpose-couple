using CoPose.Core.Sync;
using CoPose.Protocol;

namespace CoPose.Tests.Sync;

public class ActorIndexTests
{
    private static readonly ActorKey Rayla = new("Rayla Velvet", 55);

    [Fact]
    public void ExactKey_IsFound()
    {
        var index = ActorIndex.Build([new(201, "Rayla Velvet", 55), new(202, "Sadonia Velvet", 55)]);

        Assert.True(index.TryFind(Rayla, out var found));
        Assert.Equal(201u, found);
    }

    [Fact]
    public void DifferentWorld_FallsBackToName()
    {
        var index = ActorIndex.Build([new(203, "Rayla Velvet", 0)]);

        Assert.True(index.TryFind(Rayla, out var found));
        Assert.Equal(203u, found);
    }

    [Fact]
    public void Clone_DoesNotHideTheOriginal()
    {
        // The GPose copy (201) and a Brio clone (240), both without the overworld home world.
        var index = ActorIndex.Build([new(240, "Rayla Velvet", 0), new(201, "Rayla Velvet", 0)]);

        Assert.True(index.TryFind(Rayla, out var found));
        Assert.Equal(201u, found);
    }

    [Fact]
    public void ExactKeyBeatsLowerIndexNameMatch()
    {
        var index = ActorIndex.Build([new(201, "Rayla Velvet", 0), new(205, "Rayla Velvet", 55)]);

        Assert.True(index.TryFind(Rayla, out var found));
        Assert.Equal(205u, found);
    }

    [Fact]
    public void EmptyNames_AreIgnored_AndUnknownNamesMiss()
    {
        var index = ActorIndex.Build([new(201, "", 55), new(202, "Eos", 0)]);

        Assert.False(index.TryFind(Rayla, out _));
        Assert.False(index.TryFind(new ActorKey("", 55), out _));
        Assert.Equal(2, index.Actors.Count);
    }
}
