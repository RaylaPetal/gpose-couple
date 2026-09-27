using CoPose.Core.Sync;
using Dalamud.Plugin.Services;

namespace CoPose.Interop;

/// <summary>Syncing needs GPose plus Ktisis available with posing mode on (otherwise animation overwrites our writes).</summary>
public sealed class SyncEnvironment(IClientState clientState, KtisisIpc ktisis) : ISyncEnvironment
{
    public bool CanSync => clientState.IsGPosing && ktisis.Available && ktisis.IsPosing;
}
