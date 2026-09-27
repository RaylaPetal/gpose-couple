using System.Numerics;
using CoPose.Core;
using CoPose.Core.Relay;
using CoPose.Core.Tags;
using CoPose.Tests.Sync;
using static CoPose.Tests.Sync.TwoPlayers;

namespace CoPose.Tests.Relay;

/// <summary>
/// End to end against a real CoPose relay: two clients pair over fake tags and pose over the real Worker.
/// Runs only when COPOSE_RELAY_URL is set (e.g. http://127.0.0.1:8787 with `npm run dev` in CoPose.Relay).
/// </summary>
public class LiveRelayTests
{
    private static readonly string? RelayUrl = Environment.GetEnvironmentVariable("COPOSE_RELAY_URL");

    [Fact]
    public async Task TwoClients_PoseLiveOverTheRealRelay()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(RelayUrl), "COPOSE_RELAY_URL not set");

        var hub = new FakeTagHub();
        var worldA = new FakeWorld().Add(KeyA).Add(KeyB);
        var worldB = new FakeWorld().Add(KeyA).Add(KeyB);
        using var relayA = new RelayChannel(() => RelayUrl!);
        using var relayB = new RelayChannel(() => RelayUrl!);
        using var a = new CoPoseClient(hub.Join(KeyA), relayA, () => KeyA, worldA, worldA, worldA, worldA);
        using var b = new CoPoseClient(hub.Join(KeyB), relayB, () => KeyB, worldB, worldB, worldB, worldB);

        async Task Run(int ms)
        {
            var until = Environment.TickCount64 + ms;
            while (Environment.TickCount64 < until)
            {
                a.Tick();
                b.Tick();
                hub.Flush();
                await Task.Delay(20);
            }
        }

        await Run(1200);
        Assert.True(a.Choose(KeyB));
        await Run(1200);
        Assert.True(b.Choose(KeyA));

        var deadline = Environment.TickCount64 + 10_000;
        while (!(a.PartnerPresent && b.PartnerPresent && a.PartnerReady && b.PartnerReady) && Environment.TickCount64 < deadline)
            await Run(100);
        Assert.Equal(PairingStatus.Paired, a.Status);
        Assert.Equal(RelayStatus.Connected, relayA.Status);
        Assert.True(a.PartnerPresent && b.PartnerPresent);

        worldA.Pose(KeyB, 3, new(1, 2, 3));
        worldB.Pose(KeyA, 4, new(4, 5, 6));
        await Run(1000);

        Assert.Equal(new Vector3(1, 2, 3), worldB.PositionOf(KeyB, 3));
        Assert.Equal(new Vector3(4, 5, 6), worldA.PositionOf(KeyA, 4));

        Assert.True(a.Reset(KeyB));
        await Run(1000);
        Assert.Equal(worldA.PositionOf(KeyB, 3), worldB.PositionOf(KeyB, 3));
        Assert.NotEqual(new Vector3(1, 2, 3), worldB.PositionOf(KeyB, 3));

        a.Stop();
        await Run(1000);
        Assert.Equal(PairingStatus.Idle, b.Status);
    }
}
