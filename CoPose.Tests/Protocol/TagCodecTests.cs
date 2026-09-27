using System.Text;
using CoPose.Protocol;

namespace CoPose.Tests.Protocol;

public class TagCodecTests
{
    private static readonly ActorKey Alice = new("Alice Example", 73);
    private static readonly ActorKey Bob = new("Bob Example", 40);

    /// <summary>A realistic tag: one character's full pose (body, face, hair) with scale mostly 1.</summary>
    internal static TagState RealisticState(int bones = 300, int seed = 7)
    {
        var random = new Random(seed);
        var names = Enumerable.Range(0, bones).Select(i => $"j_{(i % 3 == 0 ? "f_" : "")}bone_{i:000}_{(i % 2 == 0 ? "l" : "r")}").ToArray();
        var values = new float[bones * TagActor.FloatsPerBone];
        for (var i = 0; i < bones; i++)
        {
            var o = i * TagActor.FloatsPerBone;
            values[o] = random.NextSingle() * 0.2f;
            values[o + 1] = random.NextSingle() * 0.2f;
            values[o + 2] = random.NextSingle() * 0.05f;
            var q = System.Numerics.Quaternion.Normalize(new(random.NextSingle(), random.NextSingle(), random.NextSingle(), 1 + random.NextSingle()));
            values[o + 3] = q.X;
            values[o + 4] = q.Y;
            values[o + 5] = q.Z;
            values[o + 6] = q.W;
            values[o + 7] = values[o + 8] = values[o + 9] = 1f;
        }
        var clocks = Enumerable.Repeat(12u, bones).ToArray();
        return new TagState(ProtocolInfo.Version, Alice, Bob, true, [Alice, Bob], 12, [new TagActor(Alice, names, values, clocks)]);
    }

    [Fact]
    public void RoundTrips()
    {
        var state = RealisticState(bones: 20);

        var text = TagCodec.Encode(state);

        Assert.StartsWith($"CP{ProtocolInfo.Version}:", text);
        Assert.Equal(TagDecodeStatus.Ok, TagCodec.TryDecode(text, out var back, out _));
        Assert.Equal(state.Self, back!.Self);
        Assert.Equal(state.Partner, back.Partner);
        Assert.Equal(state.Ready, back.Ready);
        Assert.Equal(state.Resolved, back.Resolved);
        Assert.Equal(state.Clock, back.Clock);
        var actor = Assert.Single(back.Actors);
        Assert.Equal(state.Actors[0].Names, actor.Names);
        Assert.Equal(state.Actors[0].Values, actor.Values);
        Assert.Equal(state.Actors[0].Clocks, actor.Clocks);
    }

    [Fact]
    public void AnnouncementWithoutPartner_RoundTrips()
    {
        var text = TagCodec.Encode(new TagState(ProtocolInfo.Version, Alice, null, false, [], 0, []));
        Assert.Equal(TagDecodeStatus.Ok, TagCodec.TryDecode(text, out var back, out _));
        Assert.Null(back!.Partner);
        Assert.True(text.Length < 100, $"announcement is {text.Length} chars");
    }

    [Fact]
    public void FullCharacterPose_FitsTheBudget()
    {
        var text = TagCodec.Encode(RealisticState(bones: 300));
        Assert.True(text.Length <= ProtocolInfo.TagBudgetBytes, $"tag is {text.Length} chars");
    }

    [Fact]
    public void OtherVersion_IsRecognisedWithoutDecoding()
    {
        Assert.Equal(TagDecodeStatus.VersionMismatch, TagCodec.TryDecode("CP9:not-even-base64!", out var state, out var version));
        Assert.Null(state);
        Assert.Equal(9, version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("CP:abc")]
    [InlineData("CPx:abc")]
    [InlineData("CP2:%%%")]
    [InlineData("CP2:AAAA")]
    public void Garbage_IsMalformed(string? text)
    {
        Assert.Equal(TagDecodeStatus.Malformed, TagCodec.TryDecode(text?.Replace("CP2", TagCodec.Prefix.TrimEnd(':')), out _, out _));
    }

    [Fact]
    public void InconsistentArrays_AreMalformed()
    {
        var bad = new TagState(ProtocolInfo.Version, Alice, Bob, true, [], 1, [new TagActor(Alice, ["a", "b"], new float[10], [1, 1])]);
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
