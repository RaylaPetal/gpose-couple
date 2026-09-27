using System;
using System.Linq;
using System.Numerics;
using CoPose.Core.Sync;
using CoPose.Net;
using CoPose.Session;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace CoPose.Windows;

public class MainWindow : Window, IDisposable
{
    private static readonly Vector4 Green = new(0.4f, 0.9f, 0.4f, 1f);
    private static readonly Vector4 Yellow = new(0.95f, 0.8f, 0.3f, 1f);
    private static readonly Vector4 Red = new(1f, 0.4f, 0.4f, 1f);
    private static readonly Vector4 Grey = new(0.6f, 0.6f, 0.6f, 1f);

    private readonly Plugin plugin;
    private string inviteInput = string.Empty;
    private string debugMessage = string.Empty;

    public MainWindow(Plugin plugin)
        : base("CoPose##CoPoseMain")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
        this.plugin = plugin;
    }

    public void Dispose() { }

    private SessionManager Session => plugin.Session;

    public override void Draw()
    {
        DrawEnvironment();
        ImGui.Separator();
        DrawSession();

        if (Session.Sync is { } sync)
        {
            ImGui.Separator();
            DrawParticipants(sync);
        }

        ImGui.Spacing();
        DrawDebug();
    }

    private void DrawEnvironment()
    {
        var ktisis = plugin.Ktisis;
        if (!ktisis.Available)
        {
            ImGui.TextColored(Red, ktisis.ApiVersion is { } v
                ? $"Ktisis API {v.Major}.{v.Minor} is not supported (need {Interop.KtisisIpc.SupportedMajorVersion}.x)."
                : "Ktisis not available. Install and enable Ktisis.");
        }
        else
        {
            ImGui.TextColored(Green, $"Ktisis API {ktisis.ApiVersion!.Value.Major}.{ktisis.ApiVersion.Value.Minor}");
        }

        ImGui.SameLine();
        StatusText(Plugin.ClientState.IsGPosing, "In GPose", "Not in GPose");
        ImGui.SameLine();
        StatusText(ktisis.IsPosing, "Posing on", "Posing off");
    }

    private void DrawSession()
    {
        switch (Session.Mode)
        {
            case SessionMode.Idle:
                DrawIdle();
                break;
            case SessionMode.Hosting:
                DrawHosting();
                break;
            case SessionMode.Joining:
                ImGui.TextColored(Yellow, "Connecting to host...");
                if (ImGui.Button("Cancel"))
                    Session.Leave();
                break;
            case SessionMode.Joined:
                ImGui.TextColored(Green, "Connected to the host's session.");
                if (ImGui.Button("Leave"))
                    Session.Leave();
                break;
        }

        if (Session.LastError is { } error)
            ImGui.TextColored(Red, error);
    }

    private void DrawIdle()
    {
        var config = plugin.Configuration;

        ImGui.TextUnformatted("Host a session");
        var port = config.HostPort;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Port##HostPort", ref port) && port is > 0 and <= 65535)
        {
            config.HostPort = port;
            config.Save();
        }

        var manual = config.ManualPublicAddress;
        ImGui.SetNextItemWidth(260);
        if (ImGui.InputTextWithHint("Public address (optional)##Manual", "Tailscale IP, or tunnel e.g. name.ply.gg:34567", ref manual, 128))
        {
            config.ManualPublicAddress = manual;
            config.Save();
        }

        if (ImGui.Button("Host"))
            Session.Host();

        ImGui.Spacing();
        ImGui.TextUnformatted("Join a session");
        ImGui.SetNextItemWidth(320);
        ImGui.InputTextWithHint("##Invite", "Paste invite code (CP2-...)", ref inviteInput, 128);
        ImGui.SameLine();
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(inviteInput)))
        {
            if (ImGui.Button("Join"))
                Session.Join(inviteInput);
        }
    }

    private void DrawHosting()
    {
        ImGui.TextColored(Green, $"Hosting on port {Session.HostedPort}.");

        var invite = Session.InviteCode ?? string.Empty;
        ImGui.TextUnformatted("Invite code:");
        ImGui.SetNextItemWidth(320);
        ImGui.InputText("##InviteCode", ref invite, 128, ImGuiInputTextFlags.ReadOnly);
        ImGui.SameLine();
        if (ImGui.Button("Copy"))
            ImGui.SetClipboardText(invite);

        if (Session.Upnp is { } upnp)
        {
            var (color, label) = upnp.Status switch
            {
                Reachability.Internet => (Green, "Reachable from internet (UPnP)"),
                Reachability.LanOnly => (Yellow, "LAN/VPN only"),
                _ => (Grey, "Mapping in progress..."),
            };
            if (Session.PublicEndpoint != null)
            {
                // A tunnel/VPN/port-forward address makes the router's UPnP result secondary.
                ImGui.TextColored(Green, $"Reachable through {plugin.Configuration.ManualPublicAddress}");
                using (ImRaii.PushColor(ImGuiCol.Text, Grey))
                    ImGui.TextWrapped($"Router (UPnP): {label}. {upnp.Detail} This doesn't affect your public/tunnel address.");
            }
            else
            {
                ImGui.TextColored(color, label);
                ImGui.TextWrapped(upnp.Detail);
            }
        }
        if (Session.LanAddress is { } lan)
            ImGui.TextColored(Grey, $"LAN address: {lan}:{Session.HostedPort}");
        if (Session.ResolvingPublicAddress)
            ImGui.TextColored(Grey, $"Resolving {plugin.Configuration.ManualPublicAddress}...");
        else if (Session.PublicEndpoint is { } publicEndpoint)
            ImGui.TextColored(Green, $"Public/tunnel address in invite: {plugin.Configuration.ManualPublicAddress} ({publicEndpoint})");
        else if (Session.PublicAddressError is { } publicError)
            ImGui.TextColored(Red, $"Public address not used: {publicError}");
        ImGui.TextWrapped("If Windows Firewall asks about FINAL FANTASY XIV, allow it, or your partner will not be able to connect.");

        if (ImGui.Button("Stop hosting"))
            Session.Leave();
    }

    private void DrawParticipants(SceneSync sync)
    {
        ImGui.TextUnformatted($"Participants ({(sync.IsReady ? "you are ready" : "you are not ready")})");
        foreach (var participant in sync.Participants)
        {
            var key = participant.Info.Actor;
            var resolved = sync.IsResolved(key);

            StatusText(participant.Ready, "Ready", "Not ready");
            ImGui.SameLine();
            ImGui.TextUnformatted($"{participant.Info.DisplayName}{(participant.IsSelf ? " (you)" : "")}");
            ImGui.SameLine();
            StatusText(resolved, "character found", "character absent");

            using (ImRaii.Disabled(!sync.IsReady || !resolved))
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"Push pose##{key}"))
                    Session.PushPose(key);
            }
        }

        using (ImRaii.Disabled(!sync.IsReady || sync.Participants.Count < 2))
        {
            if (ImGui.Button("Request resync"))
                Session.RequestResync();
        }
    }

    private void DrawDebug()
    {
        if (!ImGui.CollapsingHeader("Debug"))
            return;

        if (Session.Sync is { } sync)
        {
            var stats = sync.Stats;
            ImGui.TextUnformatted($"Sent: {stats.SentPerSecond}/s ({stats.TotalSent} total)");
            ImGui.TextUnformatted($"Received: {stats.ReceivedPerSecond}/s ({stats.TotalReceived} total)");
            ImGui.TextUnformatted($"Bones per delta: {stats.AverageBonesPerDelta:0.0}");
            ImGui.TextUnformatted($"Read + diff: {stats.SampleMs:0.000} ms");
            if (stats.LastError is { } error)
                ImGui.TextColored(Red, $"Last error: {error}");
        }

        if (ImGui.Button("Log bones"))
            LogBones();
        ImGui.SameLine();
        if (ImGui.Button("Copy my pose to GPose target"))
            CopyPoseToTarget();
        if (debugMessage.Length > 0)
            ImGui.TextWrapped(debugMessage);
    }

    private void LogBones()
    {
        var keys = Session.Sync?.Participants.Select(p => p.Info.Actor).ToList() ?? [];
        if (plugin.Registry.LocalKey is { } self && !keys.Contains(self))
            keys.Insert(0, self);

        var buffer = new PoseBuffer();
        var lines = keys.Select(key =>
        {
            if (!plugin.Registry.TryResolve(key, out var handle))
                return $"{key}: not found in GPose";
            if (!plugin.Reader.TryRead(handle, buffer))
                return $"{key} (#{handle.ObjectIndex}): no skeleton";
            var sample = string.Join(", ", Enumerable.Range(0, Math.Min(3, buffer.Count))
                .Select(i => $"{buffer.Names[i]} {buffer.Samples[i].Position:0.000}"));
            return $"{key} (#{handle.ObjectIndex}): {buffer.Count} bones, {sample}";
        }).ToList();

        foreach (var line in lines)
            Plugin.Log.Information(line);
        debugMessage = lines.Count > 0 ? string.Join("\n", lines) : "No actors to read.";
    }

    private void CopyPoseToTarget()
    {
        if (plugin.Registry.LocalKey is not { } self || !plugin.Registry.TryResolve(self, out var source))
        {
            debugMessage = "Your character was not found in GPose.";
            return;
        }
        if (Plugin.TargetManager.GPoseTarget is not { } target)
        {
            debugMessage = "Select a GPose target first.";
            return;
        }

        var buffer = new PoseBuffer();
        if (!plugin.Reader.TryRead(source, buffer))
        {
            debugMessage = "Could not read your skeleton.";
            return;
        }

        var values = Enumerable.Range(0, buffer.Count).Select(i => new BoneValue(buffer.Names[i], buffer.Samples[i])).ToList();
        var task = plugin.Writer.ApplyBonesAsync(new ActorHandle(target.ObjectIndex), values);
        debugMessage = task.IsCompletedSuccessfully && task.Result
            ? $"Copied {values.Count} bones onto {target.Name}."
            : $"Applying to {target.Name} {(task.IsCompleted ? "failed" : "is in progress")}.";
    }

    private static void StatusText(bool ok, string yes, string no) => ImGui.TextColored(ok ? Green : Grey, ok ? yes : no);
}
