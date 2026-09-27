using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CoPose.Core.Sync;
using CoPose.Core.Tags;
using CoPose.Protocol;
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
    private string debugMessage = string.Empty;

    public MainWindow(Plugin plugin)
        : base("CoPose##CoPoseMain")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 320),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
        this.plugin = plugin;
    }

    public void Dispose() { }

    private SessionManager Session => plugin.Session;

    public override void Draw()
    {
        DrawPrerequisites();
        ImGui.Separator();

        if (!plugin.Heels.Available)
        {
            ImGui.TextWrapped("CoPose sends poses through SimpleHeels tags, which your sync service (Player Sync or Lightless) delivers to your partner. Install and enable SimpleHeels to continue.");
        }
        else if (Session.Self is not { } me)
        {
            ImGui.TextUnformatted("Log in to a character to use CoPose.");
        }
        else
        {
            DrawPairing(me);
        }

        ImGui.Spacing();
        DrawDebug();
    }

    private void DrawPrerequisites()
    {
        var ktisis = plugin.Ktisis;
        StatusText(ktisis.Available, ktisis.ApiVersion is { } kv ? $"Ktisis {kv.Major}.{kv.Minor}" : "Ktisis", "Ktisis missing");
        ImGui.SameLine();
        var heels = plugin.Heels;
        if (heels.Available)
            ImGui.TextColored(Green, $"SimpleHeels {heels.ApiVersion!.Value.Major}.{heels.ApiVersion.Value.Minor}");
        else
            ImGui.TextColored(Red, heels.ApiVersion is { } hv ? $"SimpleHeels {hv.Major}.{hv.Minor} unsupported" : "SimpleHeels missing");
        ImGui.SameLine();
        if (plugin.Prerequisites.SyncService is { } sync)
            ImGui.TextColored(Green, sync);
        else
            ImGui.TextColored(Yellow, "No Player Sync/Lightless");

        StatusText(Plugin.ClientState.IsGPosing, "In GPose", "Not in GPose");
        ImGui.SameLine();
        StatusText(ktisis.IsPosing, "Posing on", "Posing off");

        if (plugin.Prerequisites.SyncService == null && heels.Available)
            ImGui.TextWrapped("Your partner only receives CoPose data through Player Sync or Lightless, and you must be paired with each other there.");
    }

    private void DrawPairing(ActorKey me)
    {
        var client = Session.Client;
        var pairing = client.Pairing;

        switch (client.Status)
        {
            case PairingStatus.Idle:
                DrawIdle(me, pairing);
                break;
            case PairingStatus.Waiting:
                ImGui.TextColored(Yellow, $"Waiting for {pairing.Chosen!.Value.Name} to accept...");
                ImGui.TextWrapped("They'll see your request in their CoPose window.");
                if (ImGui.Button("Cancel"))
                    Session.Stop();
                break;
            case PairingStatus.Paired:
                DrawPaired(me, pairing);
                break;
        }

        if (client.Status == PairingStatus.Idle && pairing.EndReason is { } reason)
            ImGui.TextColored(Yellow, reason);
    }

    private void DrawIdle(ActorKey me, Pairing pairing)
    {
        foreach (var request in pairing.Requests(me).ToList())
        {
            ImGui.TextColored(Green, $"{request.Key.Name} wants to pose with you.");
            ImGui.SameLine();
            if (ImGui.Button($"Accept##{request.Key}"))
                Session.Choose(request.Key);
        }

        var peers = pairing.Peers.ToList();
        if (peers.Count == 0)
        {
            ImGui.TextWrapped("Nobody nearby is running CoPose yet. Your partner needs CoPose too, you must be paired in Player Sync or Lightless, and you must be near each other.");
            return;
        }

        ImGui.TextUnformatted("Nearby CoPose players:");
        foreach (var peer in peers)
        {
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.TextUnformatted(peer.Key.Name);
            ImGui.SameLine();
            if (!peer.Compatible)
            {
                ImGui.TextColored(Grey, peer.IncompatibleVersion is { } v
                    ? $"needs the same CoPose version (they have {v}, you have {ProtocolInfo.Version})"
                    : "needs the same CoPose version");
                continue;
            }
            if (ImGui.SmallButton($"Pose with##{peer.Key}"))
                Session.Choose(peer.Key);
        }
    }

    private void DrawPaired(ActorKey me, Pairing pairing)
    {
        var client = Session.Client;
        var partner = pairing.ChosenPeer!;
        ImGui.TextColored(Green, $"Posing together with {partner.Key.Name}.");
        ImGui.SameLine();
        if (ImGui.SmallButton("Stop posing together"))
            Session.Stop();

        if (client.Session is not { } session)
            return;

        ImGui.TextColored(Grey, "Your changes reach your partner about a second after you make them.");
        ImGui.Spacing();

        StatusText(session.Ready, "You: ready", "You: not ready");
        ImGui.SameLine();
        StatusText(partner.State?.Ready == true, $"{partner.Key.Name}: ready", $"{partner.Key.Name}: not ready");
        if (!session.Ready)
            ImGui.TextWrapped("To sync, enter GPose near your partner and turn on Ktisis posing.");

        foreach (var key in new[] { me, partner.Key })
        {
            var resolved = session.Resolved.Contains(key);
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.TextUnformatted(key == me ? $"{key.Name} (you)" : key.Name);
            ImGui.SameLine();
            StatusText(resolved, "character found", "character absent");
            using (ImRaii.Disabled(!session.Ready || !resolved))
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"Push pose##{key}"))
                    Session.PushPose(key);
            }
        }
    }

    private void DrawDebug()
    {
        if (!ImGui.CollapsingHeader("Debug"))
            return;

        var stats = Session.Client.Stats;
        ImGui.TextUnformatted($"Tag publishes: {stats.Publishes}   last size: {stats.LastTagBytes / 1024.0:0.0} KB (budget {ProtocolInfo.TagBudgetBytes / 1024} KB)");
        var age = stats.LastPartnerTagAtMs is { } at ? $"{(Environment.TickCount64 - at) / 1000.0:0.0} s ago" : "never";
        ImGui.TextUnformatted($"Partner tags received: {stats.Receives}   last: {age}");
        ImGui.TextUnformatted($"Bones applied from partner: {stats.AppliedBones}   read + diff: {stats.SampleMs:0.000} ms");
        if (stats.LastError is { } error)
            ImGui.TextColored(Red, $"Last error: {error}");
        if (Session.Channel.LastError is { } channelError)
            ImGui.TextColored(Red, channelError);

        DrawDetection();

        if (ImGui.Button("Log bones"))
            LogBones();
        ImGui.SameLine();
        if (ImGui.Button("Copy my pose to GPose target"))
            CopyPoseToTarget();
        if (debugMessage.Length > 0)
            ImGui.TextWrapped(debugMessage);

        DrawChannelTest();
    }

    /// <summary>Which GPose actors CoPose sees, and why a session character is (or isn't) found.</summary>
    private void DrawDetection()
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Character detection");

        var keys = new List<ActorKey>();
        if (Session.Self is { } self)
            keys.Add(self);
        if (Session.Client.Pairing.Chosen is { } partner)
            keys.Add(partner);

        var buffer = new PoseBuffer();
        foreach (var key in keys)
        {
            var result = plugin.Registry.Explain(key, out var handle);
            var (color, text) = result switch
            {
                Interop.ActorLookup.NotInGpose => (Grey, "not in GPose"),
                Interop.ActorLookup.NoActorNamed => (Red, $"no GPose actor named \"{key.Name}\""),
                _ when !plugin.Reader.TryRead(handle, buffer) => (Yellow, $"found at #{handle.ObjectIndex}, but no skeleton yet"),
                _ => (Green, $"found at #{handle.ObjectIndex}, {buffer.Count} bones"),
            };
            ImGui.TextUnformatted($"{key}:");
            ImGui.SameLine();
            ImGui.TextColored(color, text);
        }

        if (ImGui.SmallButton("Rescan GPose actors"))
            plugin.Registry.Invalidate();

        var seen = plugin.Registry.LastScan;
        if (seen.Count == 0)
        {
            ImGui.TextColored(Grey, Plugin.ClientState.IsGPosing ? "No scan yet (or no GPose actors)." : "Not in GPose.");
            return;
        }
        ImGui.TextColored(Grey, $"GPose actors seen ({seen.Count}):");
        foreach (var actor in seen)
            ImGui.TextColored(Grey, $"  #{actor.Index}  {actor.Name}@{actor.World}");
    }

    /// <summary>Measures what the sync service carries: publish a test tag of a given size and watch the partner's list.</summary>
    private void DrawChannelTest()
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Channel test");
        ImGui.TextColored(Grey, "Publish test tags of increasing size; your partner sees what arrives, and how late.");

        var config = plugin.Configuration;
        var size = config.TestTagKilobytes;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("KB##TestSize", ref size) && size is >= 1 and <= 256)
        {
            config.TestTagKilobytes = size;
            config.Save();
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(!plugin.Heels.Available))
        {
            if (ImGui.Button("Publish test tag"))
                Session.Channel.PublishTestTag(config.TestTagKilobytes);
            ImGui.SameLine();
            if (ImGui.Button("Clear test tag"))
                Session.Channel.ClearTestTag();
        }

        foreach (var receipt in Session.Channel.TestReceipts)
        {
            var latency = receipt.SentAtUnixMs is { } sent
                ? $"{(receipt.ReceivedAt.ToUnixTimeMilliseconds() - sent) / 1000.0:0.0} s after sending (clocks may differ)"
                : "unknown send time";
            ImGui.TextUnformatted($"{receipt.ReceivedAt.ToLocalTime():HH:mm:ss}  {receipt.Owner.Name}: {receipt.Bytes / 1024.0:0.0} KB, {latency}");
        }
    }

    private void LogBones()
    {
        var keys = new List<ActorKey>();
        if (Session.Self is { } self)
            keys.Add(self);
        if (Session.Client.Session?.Partner is { } partner)
            keys.Add(partner);

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
        if (Session.Self is not { } self || !plugin.Registry.TryResolve(self, out var source))
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
