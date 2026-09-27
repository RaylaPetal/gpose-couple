using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CoPose.Protocol;

public sealed class InvalidInviteException(string message) : FormatException(message);

/// <summary>
/// Everything a guest needs to reach and authenticate with a host, as one copyable string:
/// <c>CP2-XXXX-XXXX-...</c> (Crockford base32 of version, secret, 1-3 IPv4 endpoints and a CRC-8).
/// Each endpoint has its own port because tunnels (playit.gg, bore...) expose a different public port.
/// Endpoints are tried in order; the host lists its LAN endpoint first.
/// </summary>
public sealed record Invite(int Version, byte[] Secret, IPEndPoint[] Endpoints)
{
    public const string Prefix = "CP2";
    public const int MaxEndpoints = 3;

    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int GroupSize = 4;
    private const int EndpointSize = 6;

    public string Encode()
    {
        if (Version is < 0 or > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(Version));
        if (Secret.Length != ProtocolInfo.SecretLength)
            throw new ArgumentException($"Secret must be {ProtocolInfo.SecretLength} bytes.", nameof(Secret));
        if (Endpoints.Length is < 1 or > MaxEndpoints)
            throw new ArgumentException($"An invite needs 1 to {MaxEndpoints} endpoints.", nameof(Endpoints));

        var bytes = new List<byte>(32) { (byte)Version };
        bytes.AddRange(Secret);
        bytes.Add((byte)Endpoints.Length);
        Span<byte> port = stackalloc byte[2];
        foreach (var endpoint in Endpoints)
        {
            if (endpoint.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("Only IPv4 endpoints are supported.", nameof(Endpoints));
            bytes.AddRange(endpoint.Address.GetAddressBytes());
            BinaryPrimitives.WriteUInt16BigEndian(port, (ushort)endpoint.Port);
            bytes.AddRange(port.ToArray());
        }
        bytes.Add(Crc8(bytes.ToArray()));

        var body = ToBase32(bytes.ToArray());
        var text = new StringBuilder(Prefix);
        for (var i = 0; i < body.Length; i += GroupSize)
            text.Append('-').Append(body.AsSpan(i, Math.Min(GroupSize, body.Length - i)));
        return text.ToString();
    }

    public static bool TryDecode(string? text, out Invite? invite, out string? error)
    {
        try
        {
            invite = Decode(text);
            error = null;
            return true;
        }
        catch (InvalidInviteException e)
        {
            invite = null;
            error = e.Message;
            return false;
        }
    }

    /// <exception cref="InvalidInviteException">The text is not a valid invite for this protocol version.</exception>
    public static Invite Decode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidInviteException("Invalid invite code: it is empty.");

        var compact = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c))
                compact.Append(char.ToUpperInvariant(c));
        }

        var s = compact.ToString();
        if (s.StartsWith("CP1", StringComparison.Ordinal))
            throw new InvalidInviteException("This invite is from an older CoPose build. Ask the host to update and send a new one.");
        if (!s.StartsWith(Prefix, StringComparison.Ordinal))
            throw new InvalidInviteException($"Invalid invite code: it must start with {Prefix}-.");

        var body = s[Prefix.Length..].Replace("-", string.Empty);
        var bytes = FromBase32(body);

        // version(1) secret(8) count(1) endpoint(6*n) crc(1)
        const int fixedSize = 1 + ProtocolInfo.SecretLength + 1 + 1;
        if (bytes.Length < fixedSize + EndpointSize)
            throw new InvalidInviteException("Invalid invite code: it is too short.");

        var crc = bytes[^1];
        var payload = bytes.AsSpan(0, bytes.Length - 1);
        if (Crc8(payload) != crc)
            throw new InvalidInviteException("Invalid invite code: checksum mismatch (check for typos).");

        var version = payload[0];
        if (version != ProtocolInfo.Version)
            throw new InvalidInviteException(
                $"Invite is for CoPose protocol version {version}, but this plugin uses version {ProtocolInfo.Version}.");

        var secret = payload.Slice(1, ProtocolInfo.SecretLength).ToArray();
        var count = payload[1 + ProtocolInfo.SecretLength];
        var offset = 2 + ProtocolInfo.SecretLength;
        if (count is < 1 or > MaxEndpoints || payload.Length != offset + count * EndpointSize)
            throw new InvalidInviteException("Invalid invite code: malformed address list.");

        var endpoints = new IPEndPoint[count];
        for (var i = 0; i < count; i++)
        {
            var entry = payload.Slice(offset + i * EndpointSize, EndpointSize);
            var port = BinaryPrimitives.ReadUInt16BigEndian(entry[4..]);
            if (port == 0)
                throw new InvalidInviteException("Invalid invite code: an address has port 0.");
            endpoints[i] = new IPEndPoint(new IPAddress(entry[..4]), port);
        }

        return new Invite(version, secret, endpoints);
    }

    private static string ToBase32(ReadOnlySpan<byte> data)
    {
        var result = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                result.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0)
            result.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return result.ToString();
    }

    private static byte[] FromBase32(string text)
    {
        var result = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in text)
        {
            var value = DecodeChar(c);
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                result.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }
        return result.ToArray();
    }

    private static int DecodeChar(char c)
    {
        c = c switch
        {
            'O' => '0',
            'I' or 'L' => '1',
            _ => c,
        };
        var index = Alphabet.IndexOf(c);
        if (index < 0)
            throw new InvalidInviteException($"Invalid invite code: unexpected character '{c}'.");
        return index;
    }

    /// <summary>CRC-8 (polynomial 0x07).</summary>
    internal static byte Crc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 0x80) != 0 ? (byte)((crc << 1) ^ 0x07) : (byte)(crc << 1);
        }
        return crc;
    }
}
