using System.Numerics;
using CoPose.Core.Sync;
using CoPose.Protocol;

namespace CoPose.Tests.Sync;

public class SceneStateTests
{
    private static readonly ActorKey A = new("Alice", 1);
    private static readonly ActorKey B = new("Bob", 1);

    private static BoneSample At(float x) => new(new Vector3(x, 0, 0), Quaternion.Identity, Vector3.One);

    private static TagActor[] Authored(SceneState state, ActorKey author) => state.ExportAuthored(author);

    [Fact]
    public void NewerVersionWins_OlderIsIgnored()
    {
        var mine = new SceneState();
        mine.Record(A, "j", At(1), new BoneVersion(5, A));

        var older = new SceneState();
        older.Record(A, "j", At(2), new BoneVersion(4, B));
        Assert.Empty(mine.Merge(B, Authored(older, B)));

        var newer = new SceneState();
        newer.Record(A, "j", At(3), new BoneVersion(6, B));
        var change = Assert.Single(mine.Merge(B, Authored(newer, B)));
        Assert.Equal(3, change.Value.Position.X);
    }

    [Fact]
    public void EqualClock_TieBreaksOnAuthorKey()
    {
        var alice = new SceneState();
        alice.Record(A, "j", At(1), new BoneVersion(5, A));
        var bob = new SceneState();
        bob.Record(A, "j", At(2), new BoneVersion(5, B));

        alice.Merge(B, Authored(bob, B));
        bob.Merge(A, Authored(alice, A));

        // "Bob" > "Alice" ordinally, so Bob's value wins on both.
        Assert.True(alice.TryGet(A, "j", out var ra));
        Assert.True(bob.TryGet(A, "j", out var rb));
        Assert.Equal(2, ra.Value.Position.X);
        Assert.Equal(ra, rb);
    }

    [Fact]
    public void Merge_AdvancesClockPastEverythingSeen()
    {
        var mine = new SceneState();
        var theirs = new SceneState();
        theirs.Record(B, "j", At(1), new BoneVersion(41, B));

        mine.Merge(B, Authored(theirs, B));

        Assert.True(mine.NextVersion(A).Clock > 41);
    }

    [Fact]
    public void Merge_IsIdempotent()
    {
        var theirs = new SceneState();
        theirs.Record(B, "j", At(1), theirs.NextVersion(B));
        var mine = new SceneState();

        Assert.Single(mine.Merge(B, Authored(theirs, B)));
        var revision = mine.Revision;
        Assert.Empty(mine.Merge(B, Authored(theirs, B)));
        Assert.Equal(revision, mine.Revision);
    }

    [Fact]
    public void ExportAuthored_ContainsOnlyTheAuthorsNewestBones()
    {
        var state = new SceneState();
        state.Record(A, "x", At(1), new BoneVersion(1, A));
        state.Record(A, "y", At(2), new BoneVersion(2, A));
        var theirs = new SceneState();
        theirs.Record(A, "y", At(3), new BoneVersion(9, B));
        state.Merge(B, Authored(theirs, B));

        var mine = Assert.Single(state.ExportAuthored(A));
        Assert.Equal(["x"], mine.Names);
    }

    [Fact]
    public void RandomEditsWithLostAndRepeatedTags_Converge()
    {
        var random = new Random(1234);
        var alice = new SceneState();
        var bob = new SceneState();
        string[] bones = ["a", "b", "c", "d"];
        ActorKey[] actors = [A, B];

        for (var round = 0; round < 500; round++)
        {
            // Each side makes an edit...
            foreach (var (state, author) in new[] { (alice, A), (bob, B) })
            {
                if (random.Next(3) == 0)
                    continue;
                state.Record(actors[random.Next(2)], bones[random.Next(bones.Length)], At(random.NextSingle()), state.NextVersion(author));
            }

            // ...and some tags are lost (the next one supersedes them), some delivered twice, in either order.
            var toBob = random.Next(3) != 0;
            var toAlice = random.Next(3) != 0;
            if (random.Next(2) == 0)
            {
                if (toBob) bob.Merge(A, Authored(alice, A));
                if (toAlice) alice.Merge(B, Authored(bob, B));
            }
            else
            {
                if (toAlice) alice.Merge(B, Authored(bob, B));
                if (toBob) bob.Merge(A, Authored(alice, A));
            }
            if (random.Next(5) == 0)
                bob.Merge(A, Authored(alice, A));
        }

        // Once both latest tags get through, the replicas are identical.
        bob.Merge(A, Authored(alice, A));
        alice.Merge(B, Authored(bob, B));
        bob.Merge(A, Authored(alice, A));

        foreach (var actor in actors)
        {
            foreach (var bone in bones)
            {
                Assert.Equal(alice.TryGet(actor, bone, out var ra), bob.TryGet(actor, bone, out var rb));
                Assert.Equal(ra, rb);
            }
        }
    }
}
