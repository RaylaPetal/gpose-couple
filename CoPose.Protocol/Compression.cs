using System.IO.Compression;

namespace CoPose.Protocol;

public static class Compression
{
    public static byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            brotli.Write(data);
        }
        return output.ToArray();
    }

    /// <exception cref="InvalidDataException">The data is not valid Brotli or expands past <paramref name="maxBytes"/>.</exception>
    public static byte[] Decompress(byte[] compressed, int maxBytes)
    {
        using var input = new MemoryStream(compressed);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();

        var buffer = new byte[16 * 1024];
        int read;
        while ((read = brotli.Read(buffer)) > 0)
        {
            if (output.Length + read > maxBytes)
                throw new InvalidDataException("Data exceeds the decompressed size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
