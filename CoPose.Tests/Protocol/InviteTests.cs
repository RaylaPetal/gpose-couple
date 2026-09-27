using System.Net;
using CoPose.Protocol;

namespace CoPose.Tests.Protocol;

public class InviteTests
{
    private static readonly byte[] Secret = [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04];

    private static Invite Sample(params string[] endpoints) =>
        new(ProtocolInfo.Version, Secret, endpoints.Select(IPEndPoint.Parse).ToArray());

    [Theory]
    [InlineData("192.168.1.20:47715")]
    [InlineData("192.168.1.20:47715", "203.0.113.7:47715")]
    [InlineData("192.168.1.20:47715", "147.185.221.16:34567", "203.0.113.7:47715")]
    public void RoundTrips(params string[] endpoints)
    {
        var invite = Sample(endpoints);
        var code = invite.Encode();

        Assert.StartsWith("CP2-", code);
        var back = Invite.Decode(code);
        Assert.Equal(invite.Version, back.Version);
        Assert.Equal(invite.Secret, back.Secret);
        Assert.Equal(invite.Endpoints, back.Endpoints);
    }

    [Fact]
    public void EachEndpointKeepsItsOwnPort()
    {
        var back = Invite.Decode(Sample("10.0.0.5:47715", "147.185.221.16:34567").Encode());
        Assert.Equal([47715, 34567], back.Endpoints.Select(e => e.Port));
    }

    [Fact]
    public void ToleratesCaseWhitespaceAndLookalikes()
    {
        var code = Sample("10.0.0.5:47715", "198.51.100.1:1234").Encode();
        var messy = "  " + code.ToLowerInvariant().Replace("-", " - ") + "\n";
        Assert.Equal(Secret, Invite.Decode(messy).Secret);

        // O -> 0 and I/L -> 1 (only in the body, after the prefix).
        var lookalike = "CP2" + code[3..].Replace('0', 'O').Replace('1', 'L');
        Assert.Equal(Secret, Invite.Decode(lookalike).Secret);
    }

    [Fact]
    public void SingleCharacterTypoIsCaught()
    {
        var code = Sample("192.168.1.20:47715", "147.185.221.16:34567", "203.0.113.7:47715").Encode();
        var body = code[4..];

        // Every position except the last character (which may hold only padding bits) must be protected.
        for (var i = 0; i < body.Length - 1; i++)
        {
            if (body[i] == '-')
                continue;
            var replacement = body[i] == 'X' ? 'Y' : 'X';
            var typo = "CP2-" + body[..i] + replacement + body[(i + 1)..];
            Assert.False(Invite.TryDecode(typo, out _, out var error), $"typo at {i} was accepted");
            Assert.NotNull(error);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("XX2-ABCD")]
    [InlineData("CP2-AB")]
    [InlineData("CP2-UUUU-UUUU")]
    public void MalformedCodesAreRejected(string code)
    {
        Assert.Throws<InvalidInviteException>(() => Invite.Decode(code));
    }

    [Fact]
    public void OldFormatIsRejectedWithUpdateHint()
    {
        var ex = Assert.Throws<InvalidInviteException>(() => Invite.Decode("CP1-0000-0000-0000"));
        Assert.Contains("older", ex.Message);
    }

    [Fact]
    public void WrongVersionIsRejected()
    {
        var code = new Invite(ProtocolInfo.Version + 1, Secret, [new IPEndPoint(IPAddress.Loopback, 1)]).Encode();
        var ex = Assert.Throws<InvalidInviteException>(() => Invite.Decode(code));
        Assert.Contains("version", ex.Message);
    }

    [Fact]
    public void TooManyEndpointsAreRefused()
    {
        var invite = Sample("1.1.1.1:1", "2.2.2.2:2", "3.3.3.3:3", "4.4.4.4:4");
        Assert.Throws<ArgumentException>(() => invite.Encode());
    }
}
