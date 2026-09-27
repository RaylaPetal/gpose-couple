using System.Buffers;
using System.Buffers.Binary;
using MessagePack;

namespace CoPose.Protocol;

public sealed class FrameTooLargeException(long length)
    : IOException($"Frame of {length} bytes exceeds the {ProtocolInfo.MaxFrameBytes} byte limit.")
{
    public long Length { get; } = length;
}

/// <summary>Frames are a uint32 little-endian length prefix followed by a MessagePack payload.</summary>
public static class FrameCodec
{
    public const int HeaderSize = 4;

    public static byte[] Encode(Frame frame)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        buffer.GetSpan(HeaderSize);
        buffer.Advance(HeaderSize);
        MessagePackSerializer.Serialize(buffer, frame, Wire.Options);

        var length = buffer.WrittenCount - HeaderSize;
        if (length > ProtocolInfo.MaxFrameBytes)
            throw new FrameTooLargeException(length);

        var bytes = buffer.WrittenSpan.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)length);
        return bytes;
    }

    public static async ValueTask WriteAsync(Stream stream, Frame frame, CancellationToken ct = default)
    {
        var bytes = Encode(frame);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    /// <summary>Reads one frame. Returns null on a clean end of stream between frames.</summary>
    /// <exception cref="FrameTooLargeException">The length prefix exceeds the limit; the body is not read.</exception>
    public static async ValueTask<Frame?> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[HeaderSize];
        var read = await stream.ReadAtLeastAsync(header, HeaderSize, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        if (read == 0)
            return null;
        if (read < HeaderSize)
            throw new EndOfStreamException("Connection closed inside a frame header.");

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length > ProtocolInfo.MaxFrameBytes)
            throw new FrameTooLargeException(length);
        if (length == 0)
            throw new InvalidDataException("Empty frame.");

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return MessagePackSerializer.Deserialize<Frame>(payload, Wire.Options)
               ?? throw new InvalidDataException("Null frame.");
    }
}
