using System.Text;
using CoPose.Protocol;

namespace CoPose.Tests.Protocol;

public class TagCodecTests
{
    private static readonly ActorKey Alice = new("Alice Example", 73);
    private static readonly ActorKey Bob = new("Bob Example", 40);

    private static TagState Tag(ActorKey? partner = null) => new(ProtocolInfo.Version, Alice, partner, RoomKeys.NewNonce());

    [Fact]
    public void RoundTrips()
    {
        var state = Tag(Bob);

        var text = TagCodec.Encode(state);

        Assert.StartsWith($"CP{ProtocolInfo.Version}:", text);
        Assert.Equal(TagDecodeStatus.Ok, TagCodec.TryDecode(text, out var back, out _));
        Assert.Equal(state.Self, back!.Self);
        Assert.Equal(state.Partner, back.Partner);
        Assert.Equal(state.Nonce, back.Nonce);
    }

    [Fact]
    public void Tag_IsSmall_AndCarriesNoBones()
    {
        var text = TagCodec.Encode(Tag(Bob));
        Assert.True(text.Length < 200, $"tag is {text.Length} chars");
    }

    [Fact]
    public void AnnouncementWithoutPartner_RoundTrips()
    {
        Assert.Equal(TagDecodeStatus.Ok, TagCodec.TryDecode(TagCodec.Encode(Tag()), out var back, out _));
        Assert.Null(back!.Partner);
    }

    [Theory]
    [InlineData("CP2:anything", 2)]
    [InlineData("CP9:not-even-base64!", 9)]
    public void OtherVersion_IsRecognisedWithoutDecoding(string text, int expected)
    {
        Assert.Equal(TagDecodeStatus.VersionMismatch, TagCodec.TryDecode(text, out var state, out var version));
        Assert.Null(state);
        Assert.Equal(expected, version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("CP:abc")]
    [InlineData("CPx:abc")]
    [InlineData("CP3:%%%")]
    [InlineData("CP3:AAAA")]
    public void Garbage_IsMalformed(string? text)
    {
        Assert.Equal(TagDecodeStatus.Malformed, TagCodec.TryDecode(text, out _, out _));
    }

    [Fact]
    public void WrongNonceLength_IsMalformed()
    {
        var bad = new TagState(ProtocolInfo.Version, Alice, Bob, [1, 2, 3]);
        Assert.Equal(TagDecodeStatus.Malformed, TagCodec.TryDecode(TagCodec.Encode(bad), out _, out _));
    }

    [Fact]
    public void DecompressionBomb_IsMalformed()
    {
        var bomb = TagCodec.Prefix + Convert.ToBase64String(Compression.Compress(Encoding.UTF8.GetBytes(new string(' ', TagCodec.MaxDecompressedBytes + 1))));
        Assert.Equal(TagDecodeStatus.Malformed, TagCodec.TryDecode(bomb, out _, out _));
    }

    [Fact]
    public void OversizedText_IsMalformed()
    {
        Assert.Equal(TagDecodeStatus.Malformed, TagCodec.TryDecode(TagCodec.Prefix + new string('A', ProtocolInfo.MaxTagBytes), out _, out _));
    }
}
