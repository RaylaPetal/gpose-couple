using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using CoPose.Core.Net;
using CoPose.Core.Sync;
using CoPose.Interop;
using CoPose.Net;
using CoPose.Protocol;
using Dalamud.Plugin.Services;

namespace CoPose.Session;

public enum SessionMode
{
    Idle,
    Hosting,
    Joining,
    Joined,
}

/// <summary>
/// Owns the current session (hosting or joined), its <see cref="SceneSync"/> and the router mapping.
/// Everything except the background network/UPnP work runs on the framework thread.
/// </summary>
public sealed class SessionManager : IDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(3);

    private readonly Configuration configuration;
    private readonly IClientState clientState;
    private readonly IPluginLog log;
    private readonly KtisisIpc ktisis;
    private readonly GposeActorRegistry registry;
    private readonly HavokPoseReader reader;
    private readonly IPoseWriter writer;
    private readonly ISyncEnvironment environment;
    private readonly Guid clientId = Guid.NewGuid();

    private SessionHost? host;
    private ISessionTransport? transport;
    private Task<SessionClient>? joining;
    private System.Threading.CancellationTokenSource? joinCancel;
    private UpnpMapper? upnp;
    private volatile bool inviteDirty;
    private Task<IPEndPoint>? resolvingPublic;
    private bool wasGposing;
    private bool wasPosing;
    private int lastParticipantCount;
    private long lastKtisisRefreshMs;

    public SessionManager(Configuration configuration, IClientState clientState, IPluginLog log, KtisisIpc ktisis,
        GposeActorRegistry registry, HavokPoseReader reader, IPoseWriter writer, ISyncEnvironment environment)
    {
        this.configuration = configuration;
        this.clientState = clientState;
        this.log = log;
        this.ktisis = ktisis;
        this.registry = registry;
        this.reader = reader;
        this.writer = writer;
        this.environment = environment;
    }

    public SessionMode Mode { get; private set; }

    public SceneSync? Sync { get; private set; }

    public string? InviteCode { get; private set; }

    public int? HostedPort => host?.Port;

    public IPAddress? LanAddress { get; private set; }

    /// <summary>The user-entered public/tunnel address, once resolved.</summary>
    public IPEndPoint? PublicEndpoint { get; private set; }

    /// <summary>Why the user-entered public address could not be used, if it could not.</summary>
    public string? PublicAddressError { get; private set; }

    public bool ResolvingPublicAddress => resolvingPublic != null;

    public UpnpMapper? Upnp => upnp;

    /// <summary>The last user-facing error (connection failure, rejection, session ended...).</summary>
    public string? LastError { get; private set; }

    public bool InSession => Mode != SessionMode.Idle;

    public bool Host()
    {
        if (InSession)
            return Fail("Leave the current session first.");
        if (MakeSelf() is not { } self)
            return Fail("Log in to a character first.");

        try
        {
            host = SessionHost.Start(self, new SessionHostOptions { Port = configuration.HostPort });
        }
        catch (SessionException e)
        {
            return Fail(e.Message);
        }

        LastError = null;
        transport = host.Local;
        Mode = SessionMode.Hosting;
        StartSync(transport);

        LanAddress = NetworkInfo.GetLanAddress();
        upnp = new UpnpMapper();
        upnp.Changed += () => inviteDirty = true;
        _ = upnp.StartAsync(host.Port);
        if (!string.IsNullOrWhiteSpace(configuration.ManualPublicAddress))
            resolvingPublic = PublicAddress.ResolveAsync(configuration.ManualPublicAddress, host.Port);
        RebuildInvite();

        log.Information($"Hosting on port {host.Port}");
        return true;
    }

    public bool Join(string inviteText)
    {
        if (InSession)
            return Fail("Leave the current session first.");
        if (!Invite.TryDecode(inviteText, out var invite, out var error))
            return Fail(error!);
        if (MakeSelf() is not { } self)
            return Fail("Log in to a character first.");

        LastError = null;
        Mode = SessionMode.Joining;
        joinCancel = new System.Threading.CancellationTokenSource();
        joining = SessionClient.ConnectAsync(invite!, self, ct: joinCancel.Token);
        return true;
    }

    public void Leave()
    {
        var (h, t, u, j) = (host, transport, upnp, joining);
        Reset();
        _ = Task.Run(async () =>
        {
            await ShutDown(h, t, u, j).ConfigureAwait(false);
        });
    }

    public void RequestResync() => Sync?.RequestResync();

    public bool PushPose(ActorKey actor) => Sync?.PushPose(actor) ?? false;

    /// <summary>Framework-thread update: finishes joins, refreshes the invite, and runs the sync loop.</summary>
    public void Tick()
    {
        var now = Environment.TickCount64;
        if (now - lastKtisisRefreshMs > 1000)
        {
            lastKtisisRefreshMs = now;
            ktisis.Refresh();
        }

        var gposing = clientState.IsGPosing;
        if (gposing != wasGposing || ktisis.IsPosing != wasPosing)
        {
            wasGposing = gposing;
            wasPosing = ktisis.IsPosing;
            registry.Invalidate();
            if (!gposing)
                reader.ClearCache();
        }

        if (joining is { IsCompleted: true } join)
        {
            joining = null;
            if (join.IsCompletedSuccessfully)
            {
                transport = join.Result;
                Mode = SessionMode.Joined;
                StartSync(transport);
                log.Information("Joined session");
            }
            else
            {
                Mode = SessionMode.Idle;
                LastError = join.Exception?.InnerException?.Message ?? "Could not join the session.";
            }
        }

        if (resolvingPublic is { IsCompleted: true } resolve)
        {
            resolvingPublic = null;
            if (resolve.IsCompletedSuccessfully)
                PublicEndpoint = resolve.Result;
            else
                PublicAddressError = resolve.Exception?.InnerException?.Message ?? "Could not resolve the public address.";
            inviteDirty = true;
        }

        if (inviteDirty)
        {
            inviteDirty = false;
            RebuildInvite();
        }

        if (Sync is { } sync)
        {
            if (sync.Participants.Count != lastParticipantCount)
            {
                lastParticipantCount = sync.Participants.Count;
                registry.Invalidate();
            }

            sync.Tick();
            if (sync.Ended)
            {
                var reason = sync.EndReason;
                Leave();
                LastError = $"Session ended: {reason}";
            }
        }
    }

    public void RebuildInvite()
    {
        if (host == null)
            return;

        // Order matters: the guest tries LAN first (fast fail when not on the same network), then the rest.
        var endpoints = new List<IPEndPoint>();
        if (LanAddress != null)
            endpoints.Add(new IPEndPoint(LanAddress, host.Port));
        if (PublicEndpoint != null)
            endpoints.Add(PublicEndpoint);
        if (upnp?.PublicAddress is { } upnpAddress)
            endpoints.Add(new IPEndPoint(upnpAddress, host.Port));
        if (endpoints.Count == 0)
            endpoints.Add(new IPEndPoint(IPAddress.Loopback, host.Port));

        InviteCode = host.CreateInvite(endpoints).Encode();
    }

    private void StartSync(ISessionTransport t)
    {
        Sync = new SceneSync(t, registry, reader, writer, environment);
        lastParticipantCount = -1;
    }

    private PeerInfo? MakeSelf() =>
        registry.LocalKey is { } key ? new PeerInfo(clientId, key, key.Name) : null;

    private bool Fail(string message)
    {
        LastError = message;
        return false;
    }

    private void Reset()
    {
        joinCancel?.Cancel();
        joinCancel = null;
        Sync = null;
        transport = null;
        host = null;
        upnp = null;
        joining = null;
        InviteCode = null;
        LanAddress = null;
        PublicEndpoint = null;
        PublicAddressError = null;
        resolvingPublic = null;
        Mode = SessionMode.Idle;
    }

    private static async Task ShutDown(SessionHost? h, ISessionTransport? t, UpnpMapper? u, Task<SessionClient>? j)
    {
        try
        {
            if (h != null)
                await h.DisposeAsync().ConfigureAwait(false);
            else if (t != null)
                await t.DisposeAsync().ConfigureAwait(false);

            if (j != null)
            {
                // A join still in flight when the user left: close it once it resolves.
                try { await (await j.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false); }
                catch { /* the join failed anyway */ }
            }
        }
        finally
        {
            u?.Dispose();
        }
    }

    public void Dispose()
    {
        var (h, t, u, j) = (host, transport, upnp, joining);
        Reset();
        try
        {
            // Block briefly so the guest is told the session ended and the router mapping is removed before unload.
            Task.Run(() => ShutDown(h, t, u, j)).Wait(ShutdownTimeout);
        }
        catch (Exception e)
        {
            log.Warning(e, "Error while closing the CoPose session");
        }
    }
}
