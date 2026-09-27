using System;
using Dalamud.Configuration;

namespace CoPose;

[Serializable]
public class Configuration : IPluginConfiguration
{
    /// <summary>The deployed CoPose relay (Cloudflare Worker).</summary>
    public const string DefaultRelayUrl = "https://copose-relay-production.oathbound.workers.dev";

    public int Version { get; set; } = 3;

    /// <summary>Relay override for testing (e.g. a local <c>wrangler dev</c>); empty uses <see cref="DefaultRelayUrl"/>.</summary>
    public string RelayUrl { get; set; } = string.Empty;

    public string GetRelayUrl() => string.IsNullOrWhiteSpace(RelayUrl) ? DefaultRelayUrl : RelayUrl.Trim();

    /// <summary>Size, in KB, of the test tag published from the debug panel's channel test.</summary>
    public int TestTagKilobytes { get; set; } = 8;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
