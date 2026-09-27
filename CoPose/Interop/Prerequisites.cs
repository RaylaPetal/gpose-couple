using System;
using System.Linq;
using Dalamud.Plugin;

namespace CoPose.Interop;

/// <summary>Whether a sync service that carries SimpleHeels data (Player Sync or Lightless) is loaded.</summary>
public sealed class Prerequisites(IDalamudPluginInterface pluginInterface)
{
    // Player Sync's internal name is its assembly name; Lightless has used both names.
    private static readonly string[] SyncInternalNames = ["MareSempiterne", "LightlessSync", "Lightless", "MareSynchronos"];
    private static readonly string[] SyncDisplayNames = ["Player Sync", "Lightless"];

    /// <summary>Display name of the loaded sync service, or null when none is loaded.</summary>
    public string? SyncService { get; private set; }

    public void Refresh()
    {
        try
        {
            var plugin = pluginInterface.InstalledPlugins.FirstOrDefault(p => p.IsLoaded
                && (SyncInternalNames.Contains(p.InternalName, StringComparer.OrdinalIgnoreCase)
                    || SyncDisplayNames.Any(n => p.Name.Contains(n, StringComparison.OrdinalIgnoreCase))));
            SyncService = plugin?.Name;
        }
        catch (Exception)
        {
            SyncService = null;
        }
    }
}
