namespace CoPose.Protocol;

/// <summary>Relay frames: one <see cref="FrameKind"/> byte, then Brotli-compressed MessagePack <see cref="SceneMessage"/>.</summary>
public static class SceneCodec
{
    /// <summary>Largest decompressed body accepted.</summary>
    public const int MaxDecompressedBytes = 1024 * 1024;

    public static byte[] Encode(FrameKind kind, SceneMessage message)
    {
        var body = Compression.Compress(Wire.Serialize(message));
        var frame = new byte[body.Length + 1];
        frame[0] = (byte)kind;
        body.CopyTo(frame, 1);
        return frame;
    }

    /// <summary>
    /// Reads a frame. Presence frames decode with a null <paramref name="message"/>. Returns false for empty,
    /// unknown, oversized or malformed frames.
    /// </summary>
    public static bool TryDecode(byte[]? frame, out FrameKind kind, out SceneMessage? message)
    {
        kind = default;
        message = null;
        if (frame is not { Length: > 0 } || frame.Length > ProtocolInfo.MaxFrameBytes)
            return false;

        kind = (FrameKind)frame[0];
        switch (kind)
        {
            case FrameKind.PeerJoined or FrameKind.PeerLeft:
                return true;
            case FrameKind.Full or FrameKind.Live:
                break;
            default:
                return false;
        }

        try
        {
            var packed = Compression.Decompress(frame[1..], MaxDecompressedBytes);
            message = Wire.Deserialize<SceneMessage>(packed);
            return message switch
            {
                StateMessage s => s.Resolved is not null && s.Actors is not null
                    && s.Actors.All(a => a is { Names: not null, Values: not null, Clocks: not null } && a.IsWellFormed),
                StopMessage s => s.Reason is not null,
                _ => false,
            };
        }
        catch (Exception e) when (e is InvalidDataException or MessagePack.MessagePackSerializationException)
        {
            message = null;
            return false;
        }
    }
}
