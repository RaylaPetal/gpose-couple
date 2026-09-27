using CoPose.Interop;
using CoPose.Session;
using CoPose.Windows;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace CoPose;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/copose";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("CoPose");

    internal KtisisIpc Ktisis { get; }
    internal GposeActorRegistry Registry { get; }
    internal HavokPoseReader Reader { get; }
    internal KtisisIpcPoseWriter Writer { get; }
    internal SessionManager Session { get; }

    private MainWindow MainWindow { get; init; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        Ktisis = new KtisisIpc(PluginInterface);
        Registry = new GposeActorRegistry(ObjectTable, ClientState, PlayerState);
        Reader = new HavokPoseReader(ObjectTable);
        Writer = new KtisisIpcPoseWriter(Ktisis, Reader);
        Session = new SessionManager(Configuration, ClientState, Log, Ktisis, Registry, Reader, Writer, new SyncEnvironment(ClientState, Ktisis));

        MainWindow = new MainWindow(this);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the CoPose window. /copose host · /copose join <invite> · /copose leave"
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;
        Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();
        MainWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);

        Session.Dispose();
        Ktisis.Dispose();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            Session.Tick();
        }
        catch (System.Exception e)
        {
            Log.Error(e, "CoPose sync tick failed");
        }
    }

    private void OnCommand(string command, string args)
    {
        var parts = args.Trim().Split(' ', 2, System.StringSplitOptions.RemoveEmptyEntries);
        var verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;

        switch (verb)
        {
            case "":
                MainWindow.Toggle();
                break;
            case "host":
                if (Session.Host())
                    ChatGui.Print("[CoPose] Hosting. Share the invite code from the CoPose window.");
                else
                    ChatGui.PrintError($"[CoPose] {Session.LastError}");
                MainWindow.IsOpen = true;
                break;
            case "join" when parts.Length > 1:
                if (Session.Join(parts[1]))
                    ChatGui.Print("[CoPose] Joining...");
                else
                    ChatGui.PrintError($"[CoPose] {Session.LastError}");
                MainWindow.IsOpen = true;
                break;
            case "leave":
                Session.Leave();
                ChatGui.Print("[CoPose] Left the session.");
                break;
            default:
                ChatGui.PrintError("[CoPose] Usage: /copose [host | join <invite> | leave]");
                break;
        }
    }

    public void ToggleMainUi() => MainWindow.Toggle();
}
