using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace CoPose.Interop;

/// <summary>
/// SimpleHeels' tag IPC (API 2.x). Tags on object 0 (the local player) are reported to sync services such as
/// Player Sync, which deliver them to paired players; tags received that way are raised through <see cref="TagChanged"/>.
/// </summary>
public sealed class SimpleHeelsIpc : IDisposable
{
    public const int SupportedMajorVersion = 2;

    private readonly ICallGateSubscriber<(int, int)> apiVersion;
    private readonly ICallGateSubscriber<int, string, string, object?> setTag;
    private readonly ICallGateSubscriber<int, string, string?> getTag;
    private readonly ICallGateSubscriber<int, string, object?> removeTag;
    private readonly ICallGateSubscriber<int, string, string?, object?> tagChanged;

    public SimpleHeelsIpc(IDalamudPluginInterface pluginInterface)
    {
        apiVersion = pluginInterface.GetIpcSubscriber<(int, int)>("SimpleHeels.ApiVersion");
        setTag = pluginInterface.GetIpcSubscriber<int, string, string, object?>("SimpleHeels.SetTag");
        getTag = pluginInterface.GetIpcSubscriber<int, string, string?>("SimpleHeels.GetTag");
        removeTag = pluginInterface.GetIpcSubscriber<int, string, object?>("SimpleHeels.RemoveTag");
        tagChanged = pluginInterface.GetIpcSubscriber<int, string, string?, object?>("SimpleHeels.TagChanged");

        tagChanged.Subscribe(OnTagChanged);
        Refresh();
    }

    public bool Available { get; private set; }

    public (int Major, int Minor)? ApiVersion { get; private set; }

    /// <summary>(object index, tag, value or null when removed). May be raised off the framework thread.</summary>
    public event Action<int, string, string?>? TagChanged;

    public void Refresh()
    {
        try
        {
            var version = apiVersion.InvokeFunc();
            ApiVersion = version;
            Available = version.Item1 == SupportedMajorVersion;
        }
        catch (Exception)
        {
            // IpcNotReadyError when SimpleHeels is not loaded.
            ApiVersion = null;
            Available = false;
        }
    }

    public void SetTag(int objectIndex, string tag, string value) => setTag.InvokeAction(objectIndex, tag, value);

    public string? GetTag(int objectIndex, string tag) => getTag.InvokeFunc(objectIndex, tag);

    public void RemoveTag(int objectIndex, string tag) => removeTag.InvokeAction(objectIndex, tag);

    private void OnTagChanged(int objectIndex, string tag, string? value) => TagChanged?.Invoke(objectIndex, tag, value);

    public void Dispose() => tagChanged.Unsubscribe(OnTagChanged);
}
