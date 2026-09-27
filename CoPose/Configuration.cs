using System;
using Dalamud.Configuration;

namespace CoPose;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    /// <summary>Size, in KB, of the test tag published from the debug panel's channel test.</summary>
    public int TestTagKilobytes { get; set; } = 8;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
