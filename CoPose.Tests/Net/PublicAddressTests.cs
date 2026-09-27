using System.Net;
using CoPose.Core.Net;

namespace CoPose.Tests.Net;

public class PublicAddressTests
{
    [Theory]
    [InlineData("100.101.102.103", "100.101.102.103", 47715)]           // Tailscale IP, host port
    [InlineData("203.0.113.7:50000", "203.0.113.7", 50000)]             // port-forward on another port
    [InlineData("name.gl.at.ply.gg:34567", "name.gl.at.ply.gg", 34567)] // playit.gg
    [InlineData("bore.pub:41234", "bore.pub", 41234)]                   // bore
    [InlineData("tcp://0.tcp.ngrok.io:12345/", "0.tcp.ngrok.io", 12345)] // pasted with scheme
    [InlineData("  example.com  ", "example.com", 47715)]
    public void Parses(string text, string host, int port)
    {
        Assert.True(PublicAddress.TryParse(text, 47715, out var h, out var p, out var error), error);
        Assert.Equal(host, h);
        Assert.Equal(port, p);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("host:0")]
    [InlineData("host:70000")]
    [InlineData("host:abc")]
    [InlineData("bad host!")]
    [InlineData("::1")]
    [InlineData("[::1]:80")]
    public void RejectsInvalid(string text)
    {
        Assert.False(PublicAddress.TryParse(text, 47715, out _, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public async Task ResolvesIpLiteralWithoutDns()
    {
        var endpoint = await PublicAddress.ResolveAsync("198.51.100.4:3000", 47715);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("198.51.100.4"), 3000), endpoint);
    }

    [Fact]
    public async Task ResolvesHostNameToIpv4()
    {
        var endpoint = await PublicAddress.ResolveAsync("localhost:5555", 47715);
        Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, endpoint.AddressFamily);
        Assert.True(IPAddress.IsLoopback(endpoint.Address));
        Assert.Equal(5555, endpoint.Port);
    }

    [Fact]
    public async Task UnresolvableNameFailsWithReadableError()
    {
        var ex = await Assert.ThrowsAsync<FormatException>(() => PublicAddress.ResolveAsync("no-such-host.invalid:1", 47715));
        Assert.Contains("no-such-host.invalid", ex.Message);
    }
}
