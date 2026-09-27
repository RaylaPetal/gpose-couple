using MessagePack;

namespace CoPose.Protocol;

public static class Wire
{
    /// <summary>Serializer options for everything on the wire. Peer data is untrusted.</summary>
    public static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    public static byte[] Serialize<T>(T value) => MessagePackSerializer.Serialize(value, Options);

    public static T Deserialize<T>(ReadOnlyMemory<byte> bytes) => MessagePackSerializer.Deserialize<T>(bytes, Options);
}
