using System.Numerics;
using CoPose.Core.Net;
using CoPose.Core.Sync;
using CoPose.Protocol;
using CoPose.Tests.Net;
using static CoPose.Tests.Sync.TwoClients;

namespace CoPose.Tests.Sync;

public class ConvergenceTests
{
    [Fact]
    public async Task ConcurrentEditsOverTcp_ConvergeAndGoQuiet()
    {
        await using var host = SessionHost.Start(new PeerInfo(Guid.NewGuid(), KeyA, "Alice"), new SessionHostOptions
        {
            Port = 0,
            BindAddress = System.Net.IPAddress.Loopback,
        });
        await using var guest = await SessionClient.ConnectAsync(NetTestHelpers.InviteFor(host), new PeerInfo(Guid.NewGuid(), KeyB, "Bob"));

        var clock = new FakeClock();
        var worldA = new FakeWorld().Add(KeyA).Add(KeyB);
        var worldB = new FakeWorld().Add(KeyA).Add(KeyB);
        var a = new SceneSync(host.Local, worldA, worldA, worldA, worldA, clock);
        var b = new SceneSync(guest, worldB, worldB, worldB, worldB, clock);

        async Task Step()
        {
            a.Tick();
            b.Tick();
            clock.Advance(60);
            await Task.Delay(5);
        }

        async Task Settle(int maxSteps = 200)
        {
            for (var i = 0; i < maxSteps && !(a.IsReady && b.IsReady); i++)
                await Step();
            for (var i = 0; i < 20; i++)
                await Step();
        }

        await Settle();
        Assert.True(a.IsReady && b.IsReady);

        // Both players hammer the same few bones on both characters at the same time.
        var random = new Random(42);
        for (var round = 0; round < 60; round++)
        {
            foreach (var (world, bias) in new[] { (worldA, 0f), (worldB, 100f) })
            {
                var actor = random.Next(2) == 0 ? KeyA : KeyB;
                var bone = random.Next(3);
                world.Pose(actor, bone, new Vector3(bias + round, random.NextSingle(), 0));
            }
            a.Tick();
            b.Tick();
            clock.Advance(60);
            if (round % 3 == 0)
                await Task.Delay(1); // let some messages cross mid-flight
        }

        await Settle();

        foreach (var key in new[] { KeyA, KeyB })
        {
            for (var i = 0; i < worldA[key].Values.Length; i++)
                Assert.False(PoseDiff.Changed(worldA[key].Values[i], worldB[key].Values[i]), $"{key} bone {i} diverged");
        }

        // Quiet once edits stop: no pose traffic in either direction.
        var sentA = a.Stats.TotalSent;
        var sentB = b.Stats.TotalSent;
        for (var i = 0; i < 30; i++)
            await Step();
        Assert.Equal(sentA, a.Stats.TotalSent);
        Assert.Equal(sentB, b.Stats.TotalSent);
    }
}
