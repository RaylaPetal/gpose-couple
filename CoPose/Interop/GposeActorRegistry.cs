using System;
using System.Collections.Generic;
using CoPose.Core.Sync;
using CoPose.Protocol;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace CoPose.Interop;

/// <summary>
/// Maps <see cref="ActorKey"/>s to local GPose actors. GPose actors live at object index 200 and up
/// (the same range Mare's GPose lookup scans). Framework thread only.
/// </summary>
public sealed class GposeActorRegistry(IObjectTable objects, IClientState clientState, IPlayerState playerState) : IActorRegistry
{
    public const int GposeStartIndex = 200;
    private const long MissRescanIntervalMs = 1000;

    private readonly Dictionary<ActorKey, uint> byKey = [];
    private readonly Dictionary<string, uint?> byName = new(StringComparer.Ordinal); // null = ambiguous
    private long lastScanMs = long.MinValue;

    /// <summary>The local player's key, or null when not logged in.</summary>
    public ActorKey? LocalKey =>
        playerState.IsLoaded ? new ActorKey(playerState.CharacterName, (ushort)playerState.HomeWorld.RowId) : null;

    /// <summary>Forces a rescan on the next lookup (GPose entered, participants changed, posing toggled).</summary>
    public void Invalidate()
    {
        byKey.Clear();
        byName.Clear();
        lastScanMs = long.MinValue;
    }

    public bool TryResolve(ActorKey key, out ActorHandle handle)
    {
        handle = default;
        if (!clientState.IsGPosing)
            return false;

        if (Lookup(key, out var index) && Matches(index, key.Name))
        {
            handle = new ActorHandle(index);
            return true;
        }

        var now = Environment.TickCount64;
        if (now - lastScanMs < MissRescanIntervalMs)
            return false;

        Rescan(now);
        if (Lookup(key, out index))
        {
            handle = new ActorHandle(index);
            return true;
        }
        return false;
    }

    private bool Lookup(ActorKey key, out uint index)
    {
        if (byKey.TryGetValue(key, out index))
            return true;

        // GPose copies may not carry a usable home world; fall back to a unique name match.
        if (byName.TryGetValue(key.Name, out var unique) && unique is { } only)
        {
            index = only;
            return true;
        }
        return false;
    }

    private bool Matches(uint index, string name) =>
        index < objects.Length && objects[(int)index] is ICharacter c && c.Name.TextValue == name;

    private void Rescan(long now)
    {
        lastScanMs = now;
        byKey.Clear();
        byName.Clear();

        for (var i = GposeStartIndex; i < objects.Length; i++)
        {
            if (objects[i] is not ICharacter character)
                continue;

            var name = character.Name.TextValue;
            if (string.IsNullOrEmpty(name))
                continue;

            var world = character is IPlayerCharacter pc ? (ushort)pc.HomeWorld.RowId : (ushort)0;
            byKey.TryAdd(new ActorKey(name, world), (uint)i); // lowest index wins (the original over spawned clones)
            byName[name] = byName.ContainsKey(name) ? null : (uint)i;
        }
    }
}
