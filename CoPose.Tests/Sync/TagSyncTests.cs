using System.Numerics;
using CoPose.Core;
using CoPose.Core.Sync;
using CoPose.Protocol;
using static CoPose.Tests.Sync.TwoPlayers;

namespace CoPose.Tests.Sync;

public class TagSyncTests
{
    /// <summary>Automatic resync effectively off, so a test proves recovery without new data.</summary>
    private static readonly CoPoseOptions NoResync = new() { FullIntervalMs = 600_000 };

    private static void AssertSame(FakeWorld a, FakeWorld b, ActorKey key)
    {
        for (var i = 0; i < a[key].Values.Length; i++)
            Assert.False(PoseDiff.Changed(a[key].Values[i], b[key].Values[i]), $"{key} bone {i} differs");
    }

    [Theory]
    [InlineData("false")]
    [InlineData("throw")]
    [InlineData("cancel")]
    public void FailedPartnerPose_RetriesWithoutLeavingGpose(string failure)
    {
        var p = new TwoPlayers(NoResync).Paired();
        var before = p.B.Stats.AppliedBones;
        p.WorldB.ApplyResult = () => failure switch
        {
            "throw" => throw new InvalidOperationException("Ktisis unavailable"),
            "cancel" => Task.FromCanceled<bool>(new CancellationToken(true)),
            _ => Task.FromResult(false),
        };
        p.WorldA.Pose(KeyB, 3, new(1, 2, 3));
        Assert.True(p.A.PushPose(KeyB));
        p.Run(20);

        Assert.Equal(before, p.B.Stats.AppliedBones);
        Assert.Contains("retrying", p.B.Stats.LastError);
        var receives = p.B.Stats.MessagesReceived;
        p.WorldB.ApplyResult = null;
        p.Run(10);

        Assert.True(p.B.Session!.Ready);
        Assert.Equal(receives, p.B.Stats.MessagesReceived); // no new message needed
        Assert.Equal(new Vector3(1, 2, 3), p.WorldB.PositionOf(KeyB, 3));
        Assert.True(p.B.Stats.AppliedBones > before);
    }

    [Fact]
    public void FailedApply_IsRateLimited_AndNewerLocalEditWins()
    {
        var p = new TwoPlayers(NoResync).Paired();
        p.WorldB.ApplyResult = () => Task.FromResult(false);
        var attempts = p.WorldB.Applies.Count;
        p.WorldA.Pose(KeyB, 3, new(1, 2, 3));
        p.Run(100, stepMs: 10);
        Assert.InRange(p.WorldB.Applies.Count - attempts, 1, 2);

        p.WorldB.Pose(KeyB, 3, new(4, 5, 6));
        p.Run(10);
        p.WorldB.ApplyResult = null;
        p.Run(10);
        Assert.Equal(new Vector3(4, 5, 6), p.WorldA.PositionOf(KeyB, 3));
        AssertSame(p.WorldA, p.WorldB, KeyB);
    }

    [Fact]
    public void ActorWrites_DoNotOverlap_WhenAnotherBoneArrives()
    {
        var p = new TwoPlayers(NoResync).Paired();
        var pending = new TaskCompletionSource<bool>();
        p.WorldB.ApplyResult = () => pending.Task;
        var attempts = p.WorldB.Applies.Count;
        p.WorldA.Pose(KeyB, 3, new(1, 2, 3));
        p.Run(10);
        p.WorldA.Pose(KeyB, 4, new(4, 5, 6));
        p.Run(10);
        Assert.Equal(attempts + 1, p.WorldB.Applies.Count);

        pending.SetResult(false);
        p.WorldB.ApplyResult = null;
        p.Run(10);
        AssertSame(p.WorldA, p.WorldB, KeyB);
    }

    [Fact]
    public void BothBecomeReady_AndSeeEachOtherReady()
    {
        var p = new TwoPlayers().Paired();

        Assert.True(p.A.Session!.Ready);
        Assert.True(p.B.Session!.Ready);
        Assert.Equal(2, p.A.Session.Resolved.Length);
        Assert.True(p.A.PartnerReady);
        Assert.True(p.B.PartnerReady);
        Assert.True(p.A.PartnerPresent);
    }

    [Fact]
    public void PosingThePartner_MirrorsWithinOneLiveInterval()
    {
        var p = new TwoPlayers().Paired();

        p.WorldA.Pose(KeyB, 3, new(1, 2, 3));
        p.Run(2, stepMs: 100); // sample, send live, apply

        Assert.Equal(new Vector3(1, 2, 3), p.WorldB.PositionOf(KeyB, 3));
    }

    [Fact]
    public void PosingSelf_Mirrors()
    {
        var p = new TwoPlayers().Paired();

        p.WorldB.Pose(KeyB, 1, new(5, 5, 5));
        p.Run(3);

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
        Assert.Equal(0, p.SceneB.SentCount(FrameKind.Live));
    }

    [Fact]
    public void IdleScene_SendsOnlyAutomaticResync()
    {
        var p = new TwoPlayers().Paired();
        var (liveA, fullA) = (p.SceneA.SentCount(FrameKind.Live), p.SceneA.SentCount(FrameKind.Full));

        p.Run(90); // 9 seconds

        Assert.Equal(liveA, p.SceneA.SentCount(FrameKind.Live));
        Assert.InRange(p.SceneA.SentCount(FrameKind.Full) - fullA, 2, 4); // every 3 s
    }

    [Fact]
    public void ContinuousDrag_SendsAtMostTenLivePerSecond()
    {
        var p = new TwoPlayers().Paired();
        var before = p.SceneA.SentCount(FrameKind.Live);

        // Ten seconds of dragging, one change per 50 ms tick.
        for (var i = 0; i < 200; i++)
        {
            p.WorldA.Pose(KeyB, 2, new(i * 0.01f, 0, 0));
            p.Run(1, stepMs: 50);
        }

        var live = p.SceneA.SentCount(FrameKind.Live) - before;
        Assert.InRange(live, 60, 101);
    }

    [Fact]
    public void DragFollowsLiveDuringTheDrag()
    {
        var p = new TwoPlayers().Paired();

        for (var i = 1; i <= 40; i++)
        {
            p.WorldA.Pose(KeyB, 2, new(i, 0, 0));
            p.Run(1, stepMs: 50);
        }

        Assert.InRange(p.WorldB.PositionOf(KeyB, 2).X, 38, 40);
    }

    [Fact]
    public void NotReady_RecordsNothing_AndCatchesUpOnceReady()
    {
        var p = new TwoPlayers().Paired();
        p.WorldB.CanSync = false; // e.g. Ktisis posing off
        p.Run(3);
        Assert.False(p.A.PartnerReady);
        var bLive = p.SceneB.SentCount(FrameKind.Live);

        p.WorldB.Pose(KeyB, 7, new(3, 3, 3)); // not recorded while not ready
        p.WorldA.Pose(KeyA, 4, new(8, 8, 8));
        p.Run(10);
        Assert.False(p.B.Session!.Ready);
        Assert.NotEqual(new(8, 8, 8), p.WorldB.PositionOf(KeyA, 4));
        Assert.NotEqual(new(3, 3, 3), p.WorldA.PositionOf(KeyB, 7));
        Assert.Equal(bLive, p.SceneB.SentCount(FrameKind.Live));

        p.WorldB.CanSync = true;
        p.Run(5);
        Assert.Equal(new Vector3(8, 8, 8), p.WorldB.PositionOf(KeyA, 4));
        Assert.True(p.A.PartnerReady);
    }

    [Fact]
    public void EnterGposeAfterPartnerPosed_CatchesUpWithoutPush()
    {
        var p = new TwoPlayers();
        p.WorldA.CanSync = false; // A is not in GPose yet
        p.Paired();

        p.WorldB.Pose(KeyA, 1, new(1, 1, 1));
        p.WorldB.Pose(KeyB, 2, new(2, 2, 2));
        p.Run(10);

        p.WorldA.CanSync = true;
        p.Run(5);

        Assert.Equal(new Vector3(1, 1, 1), p.WorldA.PositionOf(KeyA, 1));
        Assert.Equal(new Vector3(2, 2, 2), p.WorldA.PositionOf(KeyB, 2));
    }

    [Fact]
    public void LostLiveMessage_IsHealedByAutomaticResync()
    {
        var p = new TwoPlayers().Paired();

        p.Relay.Deliver = false;
        p.WorldA.Pose(KeyA, 3, new(1, 0, 0));
        p.Run(2);
        p.Relay.Deliver = true;
        Assert.NotEqual(new(1, 0, 0), p.WorldB.PositionOf(KeyA, 3));

        p.Run(35); // a little over one resync interval

        Assert.Equal(new Vector3(1, 0, 0), p.WorldB.PositionOf(KeyA, 3));
    }

    [Fact]
    public void Reconnect_CatchesUpFromRelayReplay()
    {
        var p = new TwoPlayers().Paired();

        p.SceneB.Drop();
        p.WorldA.Pose(KeyB, 6, new(6, 6, 6));
        p.Run(40); // A keeps sending full states into the room
        Assert.NotEqual(new(6, 6, 6), p.WorldB.PositionOf(KeyB, 6));

        p.SceneB.Reconnect();
        p.Run(3);

        Assert.Equal(new Vector3(6, 6, 6), p.WorldB.PositionOf(KeyB, 6));
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
    public void Reset_RestoresStartingPoseOnBothClients()
    {
        var p = new TwoPlayers().Paired();
        var start = p.WorldA[KeyB].Values[3].Position;

        p.WorldA.Pose(KeyB, 3, new(9, 9, 9));
        p.WorldB.Pose(KeyB, 4, new(8, 8, 8));
        p.Run(10);
        Assert.Equal(new Vector3(9, 9, 9), p.WorldB.PositionOf(KeyB, 3));

        Assert.True(p.A.Reset(KeyB));
        p.Run(10);

        Assert.Equal(start, p.WorldA.PositionOf(KeyB, 3));
        Assert.Equal(start, p.WorldB.PositionOf(KeyB, 3));
        AssertSame(p.WorldA, p.WorldB, KeyB);
    }

    [Fact]
    public void ResetBoth_RestoresBothCharacters()
    {
        var p = new TwoPlayers().Paired();
        var startA = p.WorldA[KeyA].Values[1].Position;
        var startB = p.WorldA[KeyB].Values[1].Position;

        p.WorldA.Pose(KeyA, 1, new(5, 5, 5));
        p.WorldB.Pose(KeyB, 1, new(6, 6, 6));
        p.Run(10);

        Assert.True(p.B.ResetBoth());
        p.Run(10);

        Assert.Equal(startA, p.WorldA.PositionOf(KeyA, 1));
        Assert.Equal(startB, p.WorldA.PositionOf(KeyB, 1));
        AssertSame(p.WorldA, p.WorldB, KeyA);
        AssertSame(p.WorldA, p.WorldB, KeyB);
    }

    [Fact]
    public void Reset_IsUnavailableOutsideSession()
    {
        var p = new TwoPlayers();
        p.Run(3);
        Assert.False(p.A.Reset(KeyA));
        Assert.False(p.A.ResetBoth());
    }

    [Fact]
    public void Diagnostics_Count()
    {
        var p = new TwoPlayers().Paired();
        var receivedB = p.B.Stats.MessagesReceived;

        p.WorldA.Pose(KeyB, 3, new(1, 1, 1));
        p.Run(10);

        Assert.True(p.A.Stats.TagPublishes > 0);
        Assert.True(p.A.Stats.MessagesSent > 0);
        Assert.True(p.A.Stats.LastMessageBytes > 0);
        Assert.True(p.B.Stats.MessagesReceived > receivedB);
        Assert.NotNull(p.B.Stats.LastPartnerMessageAtMs);
        Assert.True(p.B.Stats.AppliedBones > 0);
        Assert.Null(p.A.Stats.LastError);
    }
}
