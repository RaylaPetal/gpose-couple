using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace CoPose.Interop;

/// <summary>Typed access to Ktisis's IPC (API 1.x). All calls must be made on the framework thread.</summary>
public sealed class KtisisIpc : IDisposable
{
    public const int SupportedMajorVersion = 1;

    private readonly ICallGateSubscriber<(int, int)> apiVersion;
    private readonly ICallGateSubscriber<bool> isPosing;
    private readonly ICallGateSubscriber<bool, bool> posingChanged;
    private readonly ICallGateSubscriber<uint, Task<string?>> savePose;
    private readonly ICallGateSubscriber<uint, string, bool, bool, bool, Task<bool>> loadPoseExtended;
    private readonly ICallGateSubscriber<uint, Dictionary<string, Matrix4x4>, Task<bool>> applyAbsolutePoses;

    public KtisisIpc(IDalamudPluginInterface pluginInterface)
    {
        apiVersion = pluginInterface.GetIpcSubscriber<(int, int)>("Ktisis.ApiVersion");
        isPosing = pluginInterface.GetIpcSubscriber<bool>("Ktisis.IsPosing");
        posingChanged = pluginInterface.GetIpcSubscriber<bool, bool>("Ktisis.PosingChanged");
        savePose = pluginInterface.GetIpcSubscriber<uint, Task<string?>>("Ktisis.SavePose");
        loadPoseExtended = pluginInterface.GetIpcSubscriber<uint, string, bool, bool, bool, Task<bool>>("Ktisis.LoadPoseExtended");
        applyAbsolutePoses = pluginInterface.GetIpcSubscriber<uint, Dictionary<string, Matrix4x4>, Task<bool>>("Ktisis.ApplyAbsolutePoses");

        posingChanged.Subscribe(OnPosingChanged);
        Refresh();
    }

    /// <summary>True when Ktisis is loaded and reports a supported API major version.</summary>
    public bool Available { get; private set; }

    public (int Major, int Minor)? ApiVersion { get; private set; }

    public bool IsPosing { get; private set; }

    /// <summary>Raised on the framework thread when Ktisis posing mode is toggled.</summary>
    public event Action<bool>? PosingChanged;

    /// <summary>Re-checks availability, version and posing state.</summary>
    public void Refresh()
    {
        try
        {
            var version = apiVersion.InvokeFunc();
            ApiVersion = version;
            Available = version.Item1 == SupportedMajorVersion;
            IsPosing = Available && isPosing.InvokeFunc();
        }
        catch (Exception)
        {
            // IpcNotReadyError when Ktisis is not loaded.
            ApiVersion = null;
            Available = false;
            IsPosing = false;
        }
    }

    public Task<string?> SavePose(uint objectIndex) => savePose.InvokeFunc(objectIndex);

    public Task<bool> LoadPose(uint objectIndex, string json) =>
        loadPoseExtended.InvokeFunc(objectIndex, json, true, true, true);

    public Task<bool> ApplyAbsolutePoses(uint objectIndex, Dictionary<string, Matrix4x4> bones) =>
        applyAbsolutePoses.InvokeFunc(objectIndex, bones);

    private void OnPosingChanged(bool posing)
    {
        IsPosing = posing && Available;
        PosingChanged?.Invoke(posing);
    }

    public void Dispose() => posingChanged.Unsubscribe(OnPosingChanged);
}
