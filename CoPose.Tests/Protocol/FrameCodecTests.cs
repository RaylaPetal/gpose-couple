using System.Buffers.Binary;
using CoPose.Protocol;

namespace CoPose.Tests.Protocol;

public class FrameCodecTests
{
    [Fact]
    public async Task Frames_RoundTripOverStream()
    {
        var stream = new MemoryStream();
        await FrameCodec.WriteAsync(stream, new ByeFrame("a"));
        await FrameCodec.WriteAsync(stream, new SendFrame(MsgType.BoneDelta, new byte[1000]));
        stream.Position = 0;

        Assert.Equal(new ByeFrame("a"), await FrameCodec.ReadAsync(stream));
        var send = Assert.IsType<SendFrame>(await FrameCodec.ReadAsync(stream));
        Assert.Equal(1000, send.Body.Length);
        Assert.Null(await FrameCodec.ReadAsync(stream));
    }

    [Fact]
    public async Task Frames_SurvivePartialReads()
    {
        var bytes = FrameCodec.Encode(new SendFrame(MsgType.Presence, new byte[300]));
        var stream = new TrickleStream(bytes);

        var frame = Assert.IsType<SendFrame>(await FrameCodec.ReadAsync(stream));
        Assert.Equal(300, frame.Body.Length);
    }

    [Fact]
    public async Task OversizedLength_IsRejectedBeforeReadingBody()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, ProtocolInfo.MaxFrameBytes + 1);
        var stream = new MemoryStream([.. header, .. new byte[16]]);

        await Assert.ThrowsAsync<FrameTooLargeException>(async () => await FrameCodec.ReadAsync(stream));
        Assert.Equal(4, stream.Position);
    }

    [Fact]
    public void Encode_RefusesOversizedFrame()
    {
        Assert.Throws<FrameTooLargeException>(() => FrameCodec.Encode(new SendFrame(MsgType.FullSnapshot, new byte[ProtocolInfo.MaxFrameBytes + 1])));
    }

    [Fact]
    public async Task TruncatedHeader_Throws()
    {
        var stream = new MemoryStream([1, 0]);
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await FrameCodec.ReadAsync(stream));
    }

    /// <summary>Returns at most one byte per read.</summary>
    private sealed class TrickleStream(byte[] data) : Stream
    {
        private int position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (position >= data.Length || count == 0)
                return 0;
            buffer[offset] = data[position++];
            return 1;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (position >= data.Length || buffer.Length == 0)
                return ValueTask.FromResult(0);
            buffer.Span[0] = data[position++];
            return ValueTask.FromResult(1);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
