using System.Numerics;
using CoPose.Protocol;
using static CoPose.Tests.Sync.TwoClients;

namespace CoPose.Tests.Sync;

public class SyncStatsTests
{
    [Fact]
    public void CountsSentAndReceivedPerSecond_ExcludingOwnEchoes()
    {
        var c = new TwoClients();
        c.Run(40); // settle and let a full quiet window pass

        Assert.Equal(0, c.A.Stats.SentPerSecond);
        Assert.Equal(0, c.B.Stats.ReceivedPerSecond);

        // One edit per tick for a second of fake time (60 ms per round).
        for (var i = 0; i < 20; i++)
        {
            c.WorldA.Pose(KeyA, 1, new Vector3(i + 1, 0, 0));
            c.Run(1);
        }

        Assert.InRange(c.A.Stats.SentPerSecond, 10, 20);
        Assert.InRange(c.B.Stats.ReceivedPerSecond, 10, 20);
        Assert.Equal(0, c.A.Stats.ReceivedPerSecond); // A's own echoes do not count
        Assert.Equal(1, c.A.Stats.AverageBonesPerDelta, 3);

        c.Run(40);
        Assert.Equal(0, c.A.Stats.SentPerSecond);
    }

    [Fact]
    public void RecordsLastError()
    {
        var c = new TwoClients();
        c.Run(5);

        c.Hub.Enqueue(c.TransportB.Self.ClientId, MsgType.BoneDelta, [0xC1]); // not valid MessagePack for a BoneDelta
        c.Hub.Flush();
        c.Run(1);

        Assert.Contains("BoneDelta", c.A.Stats.LastError);
    }
}
