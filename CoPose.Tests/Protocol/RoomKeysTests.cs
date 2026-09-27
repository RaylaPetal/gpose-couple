using System.Text.RegularExpressions;
using CoPose.Protocol;

namespace CoPose.Tests.Protocol;

public class RoomKeysTests
{
    private static readonly ActorKey Alice = new("Alice", 55);
    private static readonly ActorKey Bob = new("Bob", 55);
    private static readonly byte[] NonceA = Enumerable.Repeat((byte)1, 16).ToArray();
    private static readonly byte[] NonceB = Enumerable.Repeat((byte)2, 16).ToArray();

    [Fact]
    public void RoomId_IsTheSameForBothParticipants()
    {
        Assert.Equal(RoomKeys.RoomId(Alice, NonceA, Bob, NonceB), RoomKeys.RoomId(Bob, NonceB, Alice, NonceA));
    }

    [Fact]
    public void RoomId_DiffersForOtherPairsOrNonces()
    {
        var room = RoomKeys.RoomId(Alice, NonceA, Bob, NonceB);
        Assert.NotEqual(room, RoomKeys.RoomId(Alice, NonceA, new ActorKey("Carol", 55), NonceB));
        Assert.NotEqual(room, RoomKeys.RoomId(Alice, NonceA, Bob, RoomKeys.NewNonce()));
        Assert.NotEqual(room, RoomKeys.RoomId(Alice, NonceB, Bob, NonceA)); // nonces swapped between players
    }

    [Fact]
    public void Ids_HaveTheRelayFormat()
    {
        Assert.Matches(new Regex("^[0-9a-f]{64}$"), RoomKeys.RoomId(Alice, NonceA, Bob, NonceB));
        Assert.Matches(new Regex("^[0-9a-f]{32}$"), RoomKeys.ParticipantId(NonceA));
        Assert.NotEqual(RoomKeys.ParticipantId(NonceA), RoomKeys.ParticipantId(NonceB));
    }

    [Fact]
    public void NewNonce_IsRandom16Bytes()
    {
        var a = RoomKeys.NewNonce();
        Assert.Equal(16, a.Length);
        Assert.NotEqual(a, RoomKeys.NewNonce());
    }
}
