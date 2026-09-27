namespace CoPose.Protocol;

public static class ProtocolInfo
{
    /// <summary>Tag protocol version. Bump on any incompatible change to <see cref="TagState"/>; it is part of the tag prefix.</summary>
    public const int Version = 2;

    /// <summary>The SimpleHeels tag CoPose publishes on the local player's character.</summary>
    public const string TagKey = "CoPose";

    /// <summary>The SimpleHeels tag used by the channel-test debug panel.</summary>
    public const string TestTagKey = "CoPose-test";

    /// <summary>Target maximum size of an encoded tag, in characters.</summary>
    public const int TagBudgetBytes = 16 * 1024;

    /// <summary>Hard limit on a received tag, to refuse garbage before decoding.</summary>
    public const int MaxTagBytes = 256 * 1024;
}
