using System.Globalization;
using System.Text;
using CoPose.Protocol;

namespace CoPose.Tests.Protocol;

public class SnapshotCompressionTests
{
    /// <summary>A pose file shaped like a Ktisis .pose export with random bone values.</summary>
    internal static string SamplePoseJson(int targetBytes = 80 * 1024)
    {
        var random = new Random(1234);
        string F() => (random.NextDouble() * 2 - 1).ToString("R", CultureInfo.InvariantCulture);

        var sb = new StringBuilder("{\"FileExtension\":\".pose\",\"TypeName\":\"Ktisis Pose\",\"FileVersion\":2,\"Bones\":{");
        var i = 0;
        while (sb.Length < targetBytes)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append($"\"j_bone_{i++:000}\":{{\"Position\":\"{F()}, {F()}, {F()}\",\"Rotation\":\"{F()}, {F()}, {F()}, {F()}\",\"Scale\":\"1, 1, 1\"}}");
        }
        sb.Append("}}");
        return sb.ToString();
    }

    [Fact]
    public void RoundTripsAndFitsUnderBodyLimit()
    {
        var json = SamplePoseJson();
        Assert.True(json.Length >= 80 * 1024);

        var compressed = SnapshotCompression.Compress(json);
        Assert.True(compressed.Length < ProtocolInfo.MaxBodyBytes, $"compressed to {compressed.Length} bytes");
        Assert.Equal(json, SnapshotCompression.Decompress(compressed));
    }

    [Fact]
    public void RefusesDecompressionBombs()
    {
        var bomb = SnapshotCompression.Compress(new string(' ', SnapshotCompression.MaxJsonBytes + 1));
        Assert.Throws<InvalidDataException>(() => SnapshotCompression.Decompress(bomb));
    }
}
