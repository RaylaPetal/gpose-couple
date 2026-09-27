namespace CoPose.Protocol;

public static class ProtocolInfo
{
    /// <summary>Tag protocol version. Bump on any incompatible change to <see cref="TagState"/>; it is part of the tag prefix.</summary>
    public const int Version = 3;

    /// <summary>The SimpleHeels tag CoPose publishes on the local player's character.</summary>
    public const string TagKey = "CoPose";

    /// <summary>The SimpleHeels tag used by the channel-test debug panel.</summary>
    public const string TestTagKey = "CoPose-test";

    /// <summary>Hard limit on a received tag, to refuse garbage before decoding.</summary>
    public const int MaxTagBytes = 64 * 1024;

    /// <summary>Largest relay frame a client sends (the relay closes connections that exceed it).</summary>
    public const int MaxFrameBytes = 64 * 1024;
}
