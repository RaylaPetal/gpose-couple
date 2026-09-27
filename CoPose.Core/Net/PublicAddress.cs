using System.Net;
using System.Net.Sockets;

namespace CoPose.Core.Net;

/// <summary>
/// A user-entered way to reach the host from outside: <c>address</c> or <c>address:port</c>, where the address is an
/// IPv4 address or a host name (a Tailscale IP, a port-forwarded public IP, or a tunnel such as playit.gg or bore).
/// </summary>
public static class PublicAddress
{
    public static bool TryParse(string? text, int defaultPort, out string host, out int port, out string? error)
    {
        host = string.Empty;
        port = defaultPort;
        error = null;

        var s = text?.Trim() ?? string.Empty;
        if (s.Length == 0)
        {
            error = "No address entered.";
            return false;
        }

        // Tolerate a pasted scheme such as tcp:// and a trailing slash.
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
            s = s[(scheme + 3)..];
        s = s.TrimEnd('/');

        var colon = s.LastIndexOf(':');
        if (colon >= 0)
        {
            if (s.IndexOf(':') != colon)
            {
                error = "IPv6 addresses are not supported; use an IPv4 address or a host name.";
                return false;
            }
            if (!int.TryParse(s[(colon + 1)..], out port) || port is < 1 or > 65535)
            {
                error = $"'{s[(colon + 1)..]}' is not a valid port (1-65535).";
                return false;
            }
            s = s[..colon];
        }

        if (Uri.CheckHostName(s) is UriHostNameType.Unknown or UriHostNameType.IPv6)
        {
            error = $"'{s}' is not a valid IPv4 address or host name.";
            return false;
        }

        host = s;
        return true;
    }

    /// <summary>Parses and resolves to an IPv4 endpoint.</summary>
    /// <exception cref="FormatException">The text is not a valid address, or the name has no IPv4 address.</exception>
    public static async Task<IPEndPoint> ResolveAsync(string? text, int defaultPort, CancellationToken ct = default)
    {
        if (!TryParse(text, defaultPort, out var host, out var port, out var error))
            throw new FormatException(error);

        if (IPAddress.TryParse(host, out var literal))
        {
            if (literal.AddressFamily != AddressFamily.InterNetwork)
                throw new FormatException("Only IPv4 addresses are supported.");
            return new IPEndPoint(literal, port);
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct).ConfigureAwait(false);
        }
        catch (SocketException e)
        {
            throw new FormatException($"Could not resolve '{host}': {e.Message}", e);
        }

        return addresses.Length > 0
            ? new IPEndPoint(addresses[0], port)
            : throw new FormatException($"'{host}' has no IPv4 address.");
    }
}
