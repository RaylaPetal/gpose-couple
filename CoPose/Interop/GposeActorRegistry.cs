using System;
using System.Collections.Generic;
using CoPose.Core;
using CoPose.Core.Sync;
using CoPose.Protocol;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace CoPose.Interop;

public enum ActorLookup
{
    Found,
    NotInGpose,
    NoActorNamed,
}

/// <summary>
/// Maps <see cref="ActorKey"/>s to local GPose actors. GPose actors live at object index 200 and up
/// (the same range Mare's GPose lookup scans). Framework thread only.
/// </summary>
public sealed class GposeActorRegistry(IObjectTable objects, IClientState clientState, IPlayerState playerState) : IActorRegistry
{
    public const int GposeStartIndex = 200;
    private const long MissRescanIntervalMs = 1000;

    private readonly IntervalGate rescanGate = new(MissRescanIntervalMs);
    private ActorIndex index = ActorIndex.Empty;

    /// <summary>The local player's key, or null when not logged in.</summary>
    public ActorKey? LocalKey =>
        playerState.IsLoaded ? new ActorKey(playerState.CharacterName, (ushort)playerState.HomeWorld.RowId) : null;

    /// <summary>The GPose actors seen by the last scan (for diagnostics).</summary>
    public IReadOnlyList<SeenActor> LastScan => index.Actors;

    /// <summary>Forces a rescan on the next lookup (GPose entered, participants changed, posing toggled).</summary>
    public void Invalidate()
    {
        index = ActorIndex.Empty;
        rescanGate.Reset();
    }

    public bool TryResolve(ActorKey key, out ActorHandle handle) => Explain(key, out handle) == ActorLookup.Found;

    /// <summary>Resolves <paramref name="key"/>, and says why not when it can't.</summary>
    public ActorLookup Explain(ActorKey key, out ActorHandle handle)
    {
        handle = default;
        if (!clientState.IsGPosing)
            return ActorLookup.NotInGpose;

        // A cached hit must still be that character (indices get reused as GPose actors come and go).
        if (index.TryFind(key, out var found) && Matches(found, key.Name))
        {
            handle = new ActorHandle(found);
            return ActorLookup.Found;
        }

        if (rescanGate.TryPass(Environment.TickCount64))
        {
            Rescan();
            if (index.TryFind(key, out found))
            {
                handle = new ActorHandle(found);
                return ActorLookup.Found;
            }
        }
        return ActorLookup.NoActorNamed;
    }

    private bool Matches(uint i, string name) =>
        i < objects.Length && objects[(int)i] is ICharacter c && c.Name.TextValue == name;

    private void Rescan()
    {
        var seen = new List<SeenActor>();
        for (var i = GposeStartIndex; i < objects.Length; i++)
        {
            if (objects[i] is not ICharacter character)
                continue;
            var world = character is IPlayerCharacter pc ? (ushort)pc.HomeWorld.RowId : (ushort)0;
            seen.Add(new SeenActor((uint)i, character.Name.TextValue, world));
        }
        index = ActorIndex.Build(seen);
    }
}
