using System.Globalization;

namespace CoPose.Protocol;

public enum TagDecodeStatus
{
    Ok,

    /// <summary>A CoPose tag from another protocol version.</summary>
    VersionMismatch,

    Malformed,
}

/// <summary>
/// Tag text is <c>CP{version}:</c> followed by base64 of Brotli-compressed MessagePack <see cref="TagState"/>.
/// The version sits in the prefix so an incompatible tag is recognised without decoding its body.
/// </summary>
public static class TagCodec
{
    public const string PrefixStart = "CP";

    /// <summary>Largest decompressed payload accepted.</summary>
    public const int MaxDecompressedBytes = 1024 * 1024;

    public static string Prefix => $"{PrefixStart}{ProtocolInfo.Version}:";

    public static string Encode(TagState state)
    {
        var packed = Wire.Serialize(state);
        return Prefix + Convert.ToBase64String(Compression.Compress(packed));
    }

    /// <param name="otherVersion">The protocol version of a mismatched tag, when it could be read.</param>
    public static TagDecodeStatus TryDecode(string? text, out TagState? state, out int? otherVersion)
    {
        state = null;
        otherVersion = null;

        if (string.IsNullOrEmpty(text) || text.Length > ProtocolInfo.MaxTagBytes || !text.StartsWith(PrefixStart, StringComparison.Ordinal))
            return TagDecodeStatus.Malformed;

        var colon = text.IndexOf(':');
        if (colon < 0 || !int.TryParse(text.AsSpan(PrefixStart.Length, colon - PrefixStart.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            return TagDecodeStatus.Malformed;

        if (version != ProtocolInfo.Version)
        {
            otherVersion = version;
            return TagDecodeStatus.VersionMismatch;
        }

        try
        {
            var compressed = Convert.FromBase64String(text[(colon + 1)..]);
            var packed = Compression.Decompress(compressed, MaxDecompressedBytes);
            var decoded = Wire.Deserialize<TagState>(packed);
            if (decoded is null || decoded.Version != ProtocolInfo.Version || decoded.Actors is null || decoded.Resolved is null
                || decoded.Actors.Any(a => a is null || a.Names is null || a.Values is null || a.Clocks is null || !a.IsWellFormed))
                return TagDecodeStatus.Malformed;

            state = decoded;
            return TagDecodeStatus.Ok;
        }
        catch (Exception e) when (e is FormatException or InvalidDataException or MessagePack.MessagePackSerializationException)
        {
            return TagDecodeStatus.Malformed;
        }
    }
}
