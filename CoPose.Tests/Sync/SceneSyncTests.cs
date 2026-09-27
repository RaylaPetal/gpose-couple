using System.Numerics;
using CoPose.Core.Sync;
using CoPose.Protocol;
using static CoPose.Tests.Sync.TwoClients;

namespace CoPose.Tests.Sync;

public class SceneSyncTests
{
    private static TwoClients Settled(SceneSyncOptions? options = null, Action<TwoClients>? setup = null)
    {
        var c = new TwoClients(options);
        setup?.Invoke(c);
        c.Run(5);
        c.ClearSent();
        return c;
    }

    private static IEnumerable<BoneDelta> Deltas(FakeTransport t) => t.SentOf<BoneDelta>(MsgType.BoneDelta);

    private static IEnumerable<FullSnapshot> Snapshots(FakeTransport t) => t.SentOf<FullSnapshot>(MsgType.FullSnapshot);

    [Fact]
    public void BothClientsBecomeReadyAndSeeEachOther()
    {
        var c = Settled();

        Assert.True(c.A.IsReady);
        Assert.True(c.B.IsReady);
        Assert.All(c.A.Participants, p => Assert.True(p.Ready));
        Assert.All(c.B.Participants, p => Assert.True(p.Ready));
    }

    [Fact]
    public void PosingThePartner_SendsOnceAndMirrors()
    {
        var c = Settled();
        var target = new Vector3(1, 2, 3);

        c.WorldA.Pose(KeyB, 3, target);
        c.Run(3);

        var delta = Assert.Single(Deltas(c.TransportA));
        Assert.Equal(KeyB, delta.Actor);
        Assert.Equal("j_bone_003", Assert.Single(delta.Bones).Name);
        Assert.Equal(target, c.WorldB[KeyB].Values[3].Position);
    }

    [Fact]
    public void PosingSelf_Mirrors()
    {
        var c = Settled();

        c.WorldB.Pose(KeyB, 1, new Vector3(5, 5, 5));
        c.Run(3);

        Assert.Equal(new Vector3(5, 5, 5), c.WorldA[KeyB].Values[1].Position);
    }

    [Fact]
    public void RemoteApply_IsNotEchoedBack()
    {
        var c = Settled();

        c.WorldA.Pose(KeyA, 0, new Vector3(9, 9, 9));
        c.Run(5);

        Assert.Empty(Deltas(c.TransportB));
        Assert.DoesNotContain(c.WorldA.BoneApplies, a => a.Actor == KeyA); // own edit is never re-applied locally
    }

    [Fact]
    public void IdleScene_SendsNothing()
    {
        var c = Settled();

        c.Run(20);

        Assert.Empty(c.TransportA.Sent);
        Assert.Empty(c.TransportB.Sent);
    }

    [Fact]
    public void NotReady_SendsAndAppliesNothing()
    {
        var c = Settled();
        c.WorldA.CanSync = false; // e.g. Ktisis posing turned off
        c.Run(2);
        c.ClearSent();

        c.WorldA.Pose(KeyA, 1, new Vector3(4, 4, 4));
        c.WorldB.Pose(KeyB, 2, new Vector3(6, 6, 6));
        c.Run(3);

        Assert.False(c.A.IsReady);
        Assert.Empty(Deltas(c.TransportA));
        Assert.NotEqual(new Vector3(6, 6, 6), c.WorldA[KeyB].Values[2].Position);
        Assert.False(c.B.Participants.Single(p => !p.IsSelf).Ready);
    }

    [Fact]
    public void ConcurrentEdits_ConvergeOnLastSequenced()
    {
        var c = Settled();
        var fromA = new Vector3(1, 0, 0);
        var fromB = new Vector3(0, 1, 0);

        c.WorldA.Pose(KeyA, 2, fromA);
        c.WorldB.Pose(KeyA, 2, fromB);
        c.A.Tick(); // A's delta is sequenced first...
        c.B.Tick(); // ...B's second, so B's wins.
        c.Hub.Flush();
        c.Run(5);

        Assert.Equal(fromB, c.WorldA[KeyA].Values[2].Position);
        Assert.Equal(fromB, c.WorldB[KeyA].Values[2].Position);

        // B never applied A's older value over its own in-flight edit.
        Assert.DoesNotContain(c.WorldB.BoneApplies, a => a.Actor == KeyA && a.Bones.Contains("j_bone_002"));

        c.ClearSent();
        c.Run(5);
        Assert.Empty(c.TransportA.Sent);
        Assert.Empty(c.TransportB.Sent);
    }

    [Fact]
    public void InFlightApply_IsNotSentBackWhenItCompletes()
    {
        var c = Settled();
        c.WorldB.DeferWrites = true;

        c.WorldA.Pose(KeyB, 4, new Vector3(2, 2, 2));
        c.Run(3); // B starts the apply but it has not landed yet

        c.WorldB.CompleteDeferred();
        c.Run(3);

        Assert.Equal(new Vector3(2, 2, 2), c.WorldB[KeyB].Values[4].Position);
        Assert.Empty(Deltas(c.TransportB));
    }

    [Fact]
    public void UnknownBones_AreSkipped()
    {
        var c = new TwoClients();
        c.WorldB.Remove(KeyB);
        c.WorldB.Add(KeyB, bones: 5); // B's local skeleton lacks bones 5..9
        c.Run(5);
        c.ClearSent();

        c.WorldA.Pose(KeyB, 1, new Vector3(3, 3, 3));
        c.WorldA.Pose(KeyB, 8, new Vector3(3, 3, 3));
        c.Run(3);

        Assert.Equal(new Vector3(3, 3, 3), c.WorldB[KeyB].Values[1].Position);
        var applied = c.WorldB.BoneApplies.Where(a => a.Actor == KeyB).SelectMany(a => a.Bones).ToList();
        Assert.DoesNotContain("j_bone_008", applied);
        Assert.Null(c.B.Stats.LastError);
    }

    [Fact]
    public void AbsentPartner_IsReportedAndNothingApplies()
    {
        var c = new TwoClients();
        c.WorldB.Remove(KeyA);
        c.Run(5);

        c.WorldA.Pose(KeyA, 1, new Vector3(3, 3, 3));
        c.Run(3);

        Assert.False(c.B.IsResolved(KeyA));
        Assert.False(c.B.IsReady);
        Assert.Empty(c.WorldB.BoneApplies);
    }

    [Fact]
    public void JoinMidScene_ReceivesOwnersPose()
    {
        var c = Settled(setup: c =>
        {
            // A posed their own character before B was ready.
            c.WorldA.Pose(KeyA, 5, new Vector3(7, 7, 7));
        });

        Assert.Equal(new Vector3(7, 7, 7), c.WorldB[KeyA].Values[5].Position);
    }

    [Fact]
    public void BecomingReady_SendsOwnSnapshot_AndPartnerReadyTriggersSnapshot()
    {
        var c = new TwoClients();
        c.WorldB.CanSync = false;
        c.Run(3);
        Assert.Contains(Snapshots(c.TransportA), s => s.Actor == KeyA);
        Assert.Empty(Snapshots(c.TransportB));
        c.ClearSent();

        c.WorldB.CanSync = true;
        c.Run(3);

        Assert.Contains(Snapshots(c.TransportB), s => s.Actor == KeyB); // B became ready
        Assert.Contains(Snapshots(c.TransportA), s => s.Actor == KeyA); // A saw B become ready
        Assert.DoesNotContain(Snapshots(c.TransportA), s => s.Actor == KeyB);
    }

    [Fact]
    public void RequestResync_OnlyOwnerAnswers()
    {
        var c = Settled();

        c.B.RequestResync();
        c.Run(3);

        Assert.Equal([KeyA], Snapshots(c.TransportA).Select(s => s.Actor));
        Assert.Empty(Snapshots(c.TransportB));
        Assert.Contains(KeyA, c.WorldB.SnapshotApplies);
    }

    [Fact]
    public void PushPose_WorksForAnyActor()
    {
        var c = Settled();
        c.WorldA.DeferWrites = false;

        // A loads a pose onto B's character without it being diffed yet, then pushes it.
        c.WorldA.Pose(KeyB, 6, new Vector3(8, 8, 8));
        Assert.True(c.A.PushPose(KeyB));
        c.Run(3);

        Assert.Contains(Snapshots(c.TransportA), s => s.Actor == KeyB);
        Assert.Equal(new Vector3(8, 8, 8), c.WorldB[KeyB].Values[6].Position);
    }

    [Fact]
    public void LargeChange_IsSentAsSnapshot()
    {
        var c = Settled(new SceneSyncOptions { SnapshotThresholdBones = 5 });

        for (var i = 0; i < 8; i++)
            c.WorldA.Pose(KeyB, i, new Vector3(i, 1, 1));
        c.Run(3);

        Assert.Empty(Deltas(c.TransportA));
        Assert.Single(Snapshots(c.TransportA));
        for (var i = 0; i < 8; i++)
            Assert.Equal(new Vector3(i, 1, 1), c.WorldB[KeyB].Values[i].Position);

        c.ClearSent();
        c.Run(5);
        Assert.Empty(c.TransportA.Sent);
        Assert.Empty(c.TransportB.Sent);
    }

    [Fact]
    public void LargeChange_FallsBackToDeltasWhenExportFails()
    {
        var c = Settled(new SceneSyncOptions { SnapshotThresholdBones = 5 });
        c.WorldA.FailExports = true;

        for (var i = 0; i < 8; i++)
            c.WorldA.Pose(KeyB, i, new Vector3(i, 2, 2));
        c.Run(3);

        Assert.Equal(8, Deltas(c.TransportA).Sum(d => d.Bones.Length));
        Assert.Equal(new Vector3(7, 2, 2), c.WorldB[KeyB].Values[7].Position);
        Assert.NotNull(c.A.Stats.LastError);
    }

    [Fact]
    public void EarlierSnapshot_DoesNotOverrideInFlightLocalEdit()
    {
        var c = Settled();
        var mine = new Vector3(4, 4, 4);

        c.WorldB.Pose(KeyB, 4, mine);
        c.A.PushPose(KeyB); // A's snapshot of B (with the old bone 4)...
        c.A.Tick();         // ...is sequenced before...
        c.B.Tick();         // ...B's edit.
        c.Hub.Flush();
        c.Run(5);

        Assert.Equal(mine, c.WorldB[KeyB].Values[4].Position);
        Assert.Equal(mine, c.WorldA[KeyB].Values[4].Position);
    }

    [Fact]
    public void PeerLeaving_RemovesParticipant()
    {
        var c = Settled();

        c.TransportB.LeaveAsync();
        c.Run(2);

        Assert.Single(c.A.Participants);
    }
}
