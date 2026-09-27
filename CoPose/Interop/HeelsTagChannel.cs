using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using CoPose.Core;
using CoPose.Core.Tags;
using CoPose.Protocol;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;

namespace CoPose.Interop;

/// <summary>A test tag seen on another player (channel-test debug panel).</summary>
public sealed record TestTagReceipt(ActorKey Owner, int Bytes, long? SentAtUnixMs, DateTimeOffset ReceivedAt);

/// <summary>
/// CoPose tags over SimpleHeels. Publishes on object 0 (the only object SimpleHeels reports to sync services),
/// and sees other players' tags through <c>TagChanged</c> plus a periodic scan of nearby players, which also
/// catches tags delivered before CoPose loaded and players who left. Framework thread only, except the IPC event.
/// </summary>
public sealed class HeelsTagChannel : ITagChannel, IDisposable
{
    public const int OverworldObjectLimit = 200;
    private const long ScanIntervalMs = 2000;
    private const int MaxTestReceipts = 20;

    private readonly SimpleHeelsIpc heels;
    private readonly IObjectTable objects;
    private readonly IPluginLog log;
    private readonly ConcurrentQueue<(int Index, string Tag, string? Value)> events = new();
    private readonly Dictionary<ActorKey, string?> known = [];
    private readonly Queue<RemoteTag> inbox = new();
    private readonly IntervalGate scanGate = new(ScanIntervalMs);

    public HeelsTagChannel(SimpleHeelsIpc heels, IObjectTable objects, IPluginLog log)
    {
        this.heels = heels;
        this.objects = objects;
        this.log = log;
        heels.TagChanged += OnTagChanged;
    }

    /// <summary>Recent test tags from other players, newest first.</summary>
    public List<TestTagReceipt> TestReceipts { get; } = [];

    public string? LastError { get; private set; }

    public void Publish(string? value)
    {
        if (!heels.Available)
            return;
        try
        {
            if (value == null)
                heels.RemoveTag(0, ProtocolInfo.TagKey);
            else
                heels.SetTag(0, ProtocolInfo.TagKey, value);
        }
        catch (Exception e)
        {
            LastError = $"Publishing the tag failed: {e.Message}";
            log.Warning(e, "Publishing the CoPose tag failed");
        }
    }

    public bool TryReceive(out RemoteTag tag)
    {
        if (inbox.Count == 0)
            Pump();
        return inbox.TryDequeue(out tag);
    }

    /// <summary>Publishes a test tag of about <paramref name="kilobytes"/> KB carrying its send time.</summary>
    public void PublishTestTag(int kilobytes)
    {
        var header = $"T{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}:";
        var padding = new string('x', Math.Max(0, kilobytes * 1024 - header.Length));
        heels.SetTag(0, ProtocolInfo.TestTagKey, header + padding);
    }

    public void ClearTestTag() => heels.RemoveTag(0, ProtocolInfo.TestTagKey);

    private void OnTagChanged(int index, string tag, string? value)
    {
        if (index != 0 && (tag == ProtocolInfo.TagKey || tag == ProtocolInfo.TestTagKey))
            events.Enqueue((index, tag, value));
    }

    private void Pump()
    {
        if (!heels.Available)
            return;

        while (events.TryDequeue(out var e))
        {
            if (KeyAt(e.Index) is not { } owner)
                continue;
            if (e.Tag == ProtocolInfo.TagKey)
                Emit(owner, e.Value);
            else if (e.Value != null)
                RecordTest(owner, e.Value);
        }

        if (scanGate.TryPass(Environment.TickCount64))
            Scan();
    }

    private void Scan()
    {
        var seen = new HashSet<ActorKey>();
        var limit = Math.Min(OverworldObjectLimit, objects.Length);
        for (var i = 1; i < limit; i++)
        {
            if (KeyAt(i) is not { } owner)
                continue;
            string? value;
            try
            {
                value = heels.GetTag(i, ProtocolInfo.TagKey);
            }
            catch (Exception e)
            {
                LastError = $"Reading tags failed: {e.Message}";
                return;
            }
            seen.Add(owner);
            Emit(owner, value);
        }

        // Players we knew about who are no longer around: their tag is gone.
        foreach (var gone in known.Where(k => k.Value != null && !seen.Contains(k.Key)).Select(k => k.Key).ToList())
            Emit(gone, null);
    }

    private void Emit(ActorKey owner, string? value)
    {
        if (known.TryGetValue(owner, out var previous) && previous == value)
            return;
        if (previous == null && value == null)
            return;
        known[owner] = value;
        inbox.Enqueue(new RemoteTag(owner, value));
    }

    private void RecordTest(ActorKey owner, string value)
    {
        long? sentAt = null;
        var colon = value.IndexOf(':');
        if (value.StartsWith('T') && colon > 1 && long.TryParse(value.AsSpan(1, colon - 1), out var ms))
            sentAt = ms;
        TestReceipts.Insert(0, new TestTagReceipt(owner, value.Length, sentAt, DateTimeOffset.UtcNow));
        if (TestReceipts.Count > MaxTestReceipts)
            TestReceipts.RemoveAt(TestReceipts.Count - 1);
    }

    private ActorKey? KeyAt(int index) =>
        index > 0 && index < objects.Length && objects[index] is IPlayerCharacter pc && pc.Name.TextValue is { Length: > 0 } name
            ? new ActorKey(name, (ushort)pc.HomeWorld.RowId)
            : null;

    public void Dispose()
    {
        heels.TagChanged -= OnTagChanged;
        if (!heels.Available)
            return;
        try
        {
            heels.RemoveTag(0, ProtocolInfo.TagKey);
            heels.RemoveTag(0, ProtocolInfo.TestTagKey);
        }
        catch (Exception)
        {
            // SimpleHeels is going away too.
        }
    }
}
