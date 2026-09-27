using System;
using CoPose.Protocol;
using Dalamud.Configuration;

namespace CoPose;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>TCP port used when hosting a session.</summary>
    public int HostPort { get; set; } = ProtocolInfo.DefaultPort;

    /// <summary>
    /// Optional extra way to reach the host, always put in invites: an IPv4 address or host name with an optional
    /// port (a manual port-forward, a Tailscale IP, or a tunnel such as playit.gg or bore, e.g. name.ply.gg:34567).
    /// </summary>
    public string ManualPublicAddress { get; set; } = string.Empty;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
