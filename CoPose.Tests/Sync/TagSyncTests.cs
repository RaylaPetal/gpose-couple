using System.Numerics;
using CoPose.Core;
using CoPose.Core.Sync;
using CoPose.Protocol;
using static CoPose.Tests.Sync.TwoPlayers;

namespace CoPose.Tests.Sync;

public class TagSyncTests
{
    private static void AssertSame(FakeWorld a, FakeWorld b, ActorKey key)
    {
        for (var i = 0; i < a[key].Values.Length; i++)
            Assert.False(PoseDiff.Changed(a[key].Values[i], b[key].Values[i]), $"{key} bone {i} differs");
    }

    [Fact]
    public void BothBecomeReady()
    {
        var p = new TwoPlayers().Paired();

        Assert.True(p.A.Session!.Ready);
        Assert.True(p.B.Session!.Ready);
        Assert.Equal(2, p.A.Session.Resolved.Length);
    }

    [Fact]
    public void PosingThePartner_Mirrors()
    {
        var p = new TwoPlayers().Paired();

        p.WorldA.Pose(KeyB, 3, new(1, 2, 3));
        p.Run(10);

        Assert.Equal(new Vector3(1, 2, 3), p.WorldB.PositionOf(KeyB, 3));
    }

    [Fact]
    public void PosingSelf_Mirrors()
    {
        var p = new TwoPlayers().Paired();

        p.WorldB.Pose(KeyB, 1, new(5, 5, 5));
        p.Run(10);

        Assert.Equal(new Vector3(5, 5, 5), p.WorldA.PositionOf(KeyB, 1));
    }

    [Fact]
    public void RemoteApply_CreatesNoLocalEdit()
    {
        var p = new TwoPlayers().Paired();

        p.WorldA.Pose(KeyA, 0, new(9, 9, 9));
        p.Run(10);

        Assert.True(p.B.Session!.Scene.TryGet(KeyA, "j_bone_000", out var register));
        Assert.Equal(KeyA, register.Version.Author);
        Assert.DoesNotContain(p.B.Session.Scene.ExportAuthored(KeyB), a => a.Key == KeyA && a.Names.Contains("j_bone_000"));
    }

    [Fact]
    public void IdleScene_PublishesNothing()
    {
        var p = new TwoPlayers().Paired();
        var (a, b) = (p.ChannelA.PublishCount, p.ChannelB.PublishCount);

        p.Run(30);

        Assert.Equal(a, p.ChannelA.PublishCount);
        Assert.Equal(b, p.ChannelB.PublishCount);
    }

    [Fact]
    public void ContinuousDrag_PublishesAtMostTwicePerSecond()
    {
        var p = new TwoPlayers().Paired();
        var before = p.ChannelA.PublishCount;

        // Ten seconds of dragging, one change per 50 ms tick.
        for (var i = 0; i < 200; i++)
        {
            p.WorldA.Pose(KeyB, 2, new(i * 0.01f, 0, 0));
            p.Run(1, stepMs: 50);
        }

        var publishes = p.ChannelA.PublishCount - before;
        Assert.InRange(publishes, 15, 21);
    }

    [Fact]
    public void DragArrivesInStepsDuringTheDrag()
    {
        var p = new TwoPlayers().Paired();

        for (var i = 1; i <= 40; i++)
        {
            p.WorldA.Pose(KeyB, 2, new(i, 0, 0));
            p.Run(1, stepMs: 50);
        }

        // Mid-drag the partner already has a recent (not necessarily final) value.
        Assert.InRange(p.WorldB.PositionOf(KeyB, 2).X, 30, 40);
    }

    [Fact]
    public void NotReady_RecordsNothing_AndAppliesOnceReady()
    {
        var p = new TwoPlayers().Paired();
        p.WorldB.CanSync = false; // e.g. Ktisis posing off
        p.Run(3);
        var bPublishes = p.ChannelB.PublishCount;

        p.WorldB.Pose(KeyB, 7, new(3, 3, 3)); // not recorded while not ready
        p.WorldA.Pose(KeyA, 4, new(8, 8, 8));
        p.Run(10);
        Assert.False(p.B.Session!.Ready);
        Assert.NotEqual(new(8, 8, 8), p.WorldB.PositionOf(KeyA, 4));
        Assert.NotEqual(new(3, 3, 3), p.WorldA.PositionOf(KeyB, 7));
        Assert.Equal(bPublishes, p.ChannelB.PublishCount - 0); // only a readiness change, no edits

        p.WorldB.CanSync = true;
        p.Run(10);
        Assert.Equal(new Vector3(8, 8, 8), p.WorldB.PositionOf(KeyA, 4));
    }

    [Fact]
    public void UnknownBones_AreSkipped()
    {
        var p = new TwoPlayers();
        p.WorldB.Remove(KeyB);
        p.WorldB.Add(KeyB, bones: 5); // B's local skeleton lacks bones 5..9
        p.Paired();

        p.WorldA.Pose(KeyB, 1, new(3, 3, 3));
        p.WorldA.Pose(KeyB, 8, new(3, 3, 3));
        p.Run(10);

        Assert.Equal(new Vector3(3, 3, 3), p.WorldB.PositionOf(KeyB, 1));
        Assert.DoesNotContain(p.WorldB.Applies.Where(a => a.Actor == KeyB).SelectMany(a => a.Bones), b => b == "j_bone_008");
    }

    [Fact]
    public void ConcurrentEdits_Converge()
    {
        var p = new TwoPlayers().Paired();

        p.WorldA.Pose(KeyA, 2, new(1, 0, 0));
        p.WorldB.Pose(KeyA, 2, new(0, 1, 0));
        p.Run(15);

        Assert.Equal(p.WorldA.PositionOf(KeyA, 2), p.WorldB.PositionOf(KeyA, 2));
        AssertSame(p.WorldA, p.WorldB, KeyA);
        AssertSame(p.WorldA, p.WorldB, KeyB);
    }

    [Fact]
    public void OngoingDrag_Wins()
    {
        var p = new TwoPlayers().Paired();

        p.WorldA.Pose(KeyB, 5, new(1, 1, 1)); // A's one-off edit...
        for (var i = 0; i < 20; i++)
        {
            p.WorldB.Pose(KeyB, 5, new(2, 2, 2)); // ...while B keeps holding the bone
            p.Run(1);
        }
        p.Run(15);

        Assert.Equal(new Vector3(2, 2, 2), p.WorldA.PositionOf(KeyB, 5));
        Assert.Equal(new Vector3(2, 2, 2), p.WorldB.PositionOf(KeyB, 5));
    }

    [Fact]
    public void LostIntermediateUpdates_DoNotMatter()
    {
        var p = new TwoPlayers().Paired();

        p.Hub.Deliver = false;
        p.WorldA.Pose(KeyA, 3, new(1, 0, 0));
        p.Run(8);
        p.WorldA.Pose(KeyA, 3, new(2, 0, 0));
        p.Run(8);
        p.Hub.Deliver = true;
        p.WorldA.Pose(KeyA, 6, new(3, 0, 0)); // forces a fresh publish that gets through
        p.Run(10);

        Assert.Equal(new Vector3(2, 0, 0), p.WorldB.PositionOf(KeyA, 3));
        Assert.Equal(new Vector3(3, 0, 0), p.WorldB.PositionOf(KeyA, 6));
    }

    [Fact]
    public void PairMidScene_ShowsOwnersPose()
    {
        var p = new TwoPlayers();
        p.WorldA.Pose(KeyA, 5, new(7, 7, 7)); // posed before pairing
        p.WorldB.Pose(KeyB, 2, new(4, 4, 4));
        p.Paired();

        Assert.Equal(new Vector3(7, 7, 7), p.WorldB.PositionOf(KeyA, 5));
        Assert.Equal(new Vector3(4, 4, 4), p.WorldA.PositionOf(KeyB, 2));
        AssertSame(p.WorldA, p.WorldB, KeyA);
        AssertSame(p.WorldA, p.WorldB, KeyB);
    }

    [Fact]
    public void Seeding_DoesNotOverwriteExistingEdits()
    {
        var p = new TwoPlayers();
        p.WorldB.CanSync = false; // B pairs but isn't ready yet
        p.Paired();

        p.WorldA.Pose(KeyB, 4, new(6, 6, 6)); // A poses B's character first
        p.Run(10);
        p.WorldB.CanSync = true; // B becomes ready and seeds its own character
        p.Run(15);

        Assert.Equal(new Vector3(6, 6, 6), p.WorldB.PositionOf(KeyB, 4));
        Assert.Equal(new Vector3(6, 6, 6), p.WorldA.PositionOf(KeyB, 4));
    }

    [Fact]
    public void PushPose_WinsOnBothSides()
    {
        var p = new TwoPlayers().Paired();
        p.WorldB.Pose(KeyB, 1, new(2, 2, 2));
        p.Run(10);

        Assert.True(p.A.PushPose(KeyB));
        p.Run(10);

        Assert.All(p.A.Session!.Scene.Bones(KeyB), b => Assert.Equal(KeyA, b.Register.Version.Author));
        AssertSame(p.WorldA, p.WorldB, KeyB);
    }

    [Fact]
    public void Diagnostics_Count()
    {
        var p = new TwoPlayers().Paired();
        var receivesB = p.B.Stats.Receives;

        p.WorldA.Pose(KeyB, 3, new(1, 1, 1));
        p.Run(10);

        Assert.True(p.A.Stats.Publishes > 0);
        Assert.True(p.A.Stats.LastTagBytes > 0);
        Assert.True(p.B.Stats.Receives > receivesB);
        Assert.NotNull(p.B.Stats.LastPartnerTagAtMs);
        Assert.True(p.B.Stats.AppliedBones > 0);
        Assert.Null(p.A.Stats.LastError);
    }
}
