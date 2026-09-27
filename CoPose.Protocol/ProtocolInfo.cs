namespace CoPose.Protocol;

public static class ProtocolInfo
{
    /// <summary>Wire protocol version. Bump on any incompatible change to frames or message bodies.</summary>
    public const int Version = 1;

    public const int DefaultPort = 47715;

    /// <summary>Largest frame (after the length prefix) accepted from a peer.</summary>
    public const int MaxFrameBytes = 128 * 1024;

    /// <summary>Largest envelope body a participant may send.</summary>
    public const int MaxBodyBytes = 64 * 1024;

    public const int SecretLength = 8;
}
