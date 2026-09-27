using System.Security.Cryptography;
using System.Text;

namespace CoPose.Protocol;

/// <summary>
/// Relay room and participant ids, derived from the secret session nonces the two players exchange in their tags.
/// Only players who can read both tags (paired in the sync service, and nearby) can compute a room.
/// </summary>
public static class RoomKeys
{
    public static byte[] NewNonce() => RandomNumberGenerator.GetBytes(TagState.NonceLength);

    /// <summary>64 hex characters; the same for both participants.</summary>
    public static string RoomId(ActorKey a, byte[] nonceA, ActorKey b, byte[] nonceB)
    {
        var (low, lowNonce, high, highNonce) = a.CompareTo(b) <= 0 ? (a, nonceA, b, nonceB) : (b, nonceB, a, nonceA);

        using var buffer = new MemoryStream();
        Write(buffer, "copose-room-v1");
        Write(buffer, low);
        Write(buffer, lowNonce);
        Write(buffer, high);
        Write(buffer, highNonce);
        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    /// <summary>32 hex characters; stable for one plugin load, so a reconnect replaces the old connection.</summary>
    public static string ParticipantId(byte[] ownNonce)
    {
        using var buffer = new MemoryStream();
        Write(buffer, "copose-participant-v1");
        Write(buffer, ownNonce);
        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()))[..32];
    }

    // Length-prefixed fields, so no two different inputs hash the same bytes.
    private static void Write(Stream s, string text) => Write(s, Encoding.UTF8.GetBytes(text));

    private static void Write(Stream s, ActorKey key)
    {
        Write(s, key.Name);
        Span<byte> world = stackalloc byte[2];
        world[0] = (byte)(key.HomeWorldId >> 8);
        world[1] = (byte)key.HomeWorldId;
        s.Write(world);
    }

    private static void Write(Stream s, byte[] bytes)
    {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        s.Write(length);
        s.Write(bytes);
    }
}
