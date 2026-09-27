using System.IO.Compression;
using System.Text;

namespace CoPose.Protocol;

public static class SnapshotCompression
{
    /// <summary>Upper bound on decompressed pose JSON, to refuse decompression bombs from a peer.</summary>
    public const int MaxJsonBytes = 4 * 1024 * 1024;

    public static byte[] Compress(string json)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            brotli.Write(Encoding.UTF8.GetBytes(json));
        }
        return output.ToArray();
    }

    public static string Decompress(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();

        var buffer = new byte[16 * 1024];
        int read;
        while ((read = brotli.Read(buffer)) > 0)
        {
            if (output.Length + read > MaxJsonBytes)
                throw new InvalidDataException("Snapshot exceeds the decompressed size limit.");
            output.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    }
}
