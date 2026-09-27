using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Mono.Nat;

namespace CoPose.Net;

public enum Reachability
{
    MappingInProgress,
    Internet,
    LanOnly,
}

/// <summary>
/// Best-effort router port mapping via UPnP / NAT-PMP so a guest on the internet can reach the host.
/// Status fields are written from background tasks and read by the UI.
/// </summary>
public sealed class UpnpMapper : IDisposable
{
    private const int LeaseSeconds = 60 * 60;
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RenewInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RemoveTimeout = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource cts = new();
    private INatDevice? device;
    private Mapping? mapping;
    private int disposed;

    public volatile Reachability Status = Reachability.MappingInProgress;

    /// <summary>The router's public address, set only when it is really public (not private/CGNAT).</summary>
    public volatile IPAddress? PublicAddress;

    /// <summary>Human-readable explanation of the current status.</summary>
    public volatile string Detail = "Looking for a UPnP/NAT-PMP router...";

    /// <summary>Raised (on a background thread) whenever the status changes.</summary>
    public event Action? Changed;

    public async Task StartAsync(int port)
    {
        try
        {
            var found = await DiscoverAsync().ConfigureAwait(false);
            if (found == null)
            {
                SetLanOnly("No UPnP/NAT-PMP router responded. Forward the port manually, or play over the same LAN or a VPN such as Tailscale.");
                return;
            }

            device = found;
            mapping = await found.CreatePortMapAsync(new Mapping(Mono.Nat.Protocol.Tcp, port, port, LeaseSeconds, "CoPose")).ConfigureAwait(false);

            IPAddress? external = null;
            try
            {
                external = await found.GetExternalIPAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Some routers map fine but refuse to report the external address.
            }

            if (external == null)
            {
                SetLanOnly("The router opened the port but did not report its public address. Enter your public address manually to include it in the invite.");
            }
            else if (NetworkInfo.IsPrivateOrCarrierNat(external))
            {
                SetLanOnly(NetworkInfo.IsCarrierNat(external)
                    ? $"Your ISP uses carrier-grade NAT ({external}), which blocks direct connections. Let your partner host, or both use a VPN such as Tailscale."
                    : $"The router reports a private address ({external}); there is another router in front of it. Forward the port on that router too, or use a VPN.");
            }
            else
            {
                PublicAddress = external;
                Status = Reachability.Internet;
                Detail = $"Port {port} opened on the router via UPnP. Public address {external}.";
                Changed?.Invoke();
                _ = RenewLoop(port);
            }
        }
        catch (Exception e)
        {
            SetLanOnly($"Could not open the port on the router ({e.Message}). Forward it manually, or use the same LAN or a VPN.");
        }
    }

    private async Task<INatDevice?> DiscoverAsync()
    {
        var tcs = new TaskCompletionSource<INatDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFound(object? sender, DeviceEventArgs e) => tcs.TrySetResult(e.Device);

        NatUtility.DeviceFound += OnFound;
        try
        {
            NatUtility.StartDiscovery();
            var first = await Task.WhenAny(tcs.Task, Task.Delay(DiscoveryTimeout, cts.Token)).ConfigureAwait(false);
            return first == tcs.Task ? tcs.Task.Result : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            NatUtility.StopDiscovery();
            NatUtility.DeviceFound -= OnFound;
        }
    }

    private async Task RenewLoop(int port)
    {
        try
        {
            using var timer = new PeriodicTimer(RenewInterval);
            while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false))
            {
                if (device != null)
                    mapping = await device.CreatePortMapAsync(new Mapping(Mono.Nat.Protocol.Tcp, port, port, LeaseSeconds, "CoPose")).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Cancelled on dispose, or the router went away; the lease simply expires.
        }
    }

    private void SetLanOnly(string detail)
    {
        Status = Reachability.LanOnly;
        Detail = detail;
        Changed?.Invoke();
    }

    /// <summary>Removes the mapping we created (waits up to 2 s).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        cts.Cancel();
        var d = device;
        var m = mapping;
        if (d != null && m != null)
        {
            try
            {
                d.DeletePortMapAsync(m).Wait(RemoveTimeout);
            }
            catch (Exception)
            {
                // Best effort; the lease expires on its own.
            }
        }
        cts.Dispose();
    }
}
