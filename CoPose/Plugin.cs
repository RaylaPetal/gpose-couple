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
    internal SimpleHeelsIpc Heels { get; }
    internal Prerequisites Prerequisites { get; }
    internal GposeActorRegistry Registry { get; }
    internal HavokPoseReader Reader { get; }
    internal KtisisIpcPoseWriter Writer { get; }
    internal HeelsTagChannel Channel { get; }
    internal SessionManager Session { get; }

    private MainWindow MainWindow { get; init; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        Ktisis = new KtisisIpc(PluginInterface);
        Heels = new SimpleHeelsIpc(PluginInterface);
        Prerequisites = new Prerequisites(PluginInterface);
        Registry = new GposeActorRegistry(ObjectTable, ClientState, PlayerState);
        Reader = new HavokPoseReader(ObjectTable);
        Writer = new KtisisIpcPoseWriter(Ktisis, Reader);
        Channel = new HeelsTagChannel(Heels, ObjectTable, Log);
        Session = new SessionManager(ClientState, Ktisis, Heels, Prerequisites, Channel, Registry, Reader, Writer,
            new SyncEnvironment(ClientState, Ktisis), Configuration.GetRelayUrl);

        MainWindow = new MainWindow(this);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the CoPose window. /copose stop · stop posing together"
        });

        // CoPose is used in GPose; Dalamud hides plugin UI there unless told not to.
        PluginInterface.UiBuilder.DisableGposeUiHide = true;
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

        // Removing our tag tells the partner the session ended.
        Session.Dispose();
        Channel.Dispose();
        Heels.Dispose();
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
            Log.Error(e, "CoPose tick failed");
        }
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "":
                MainWindow.Toggle();
                break;
            case "stop":
                Session.Stop();
                ChatGui.Print("[CoPose] Stopped posing together.");
                break;
            default:
                ChatGui.PrintError("[CoPose] Usage: /copose [stop]");
                break;
        }
    }

    public void ToggleMainUi() => MainWindow.Toggle();
}
