using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/porganizer";

    public readonly WindowSystem WindowSystem = new("Penumbra Organizer");

    private readonly MainWindow _mainWindow;

    internal readonly GetModList GetModListIpc;
    internal readonly GetModPath GetModPathIpc;

    private readonly EventSubscriber<string> _modAdded;
    private readonly EventSubscriber<string> _modDeleted;
    private readonly EventSubscriber<string, string> _modMoved;

    public Plugin()
    {
        _mainWindow = new MainWindow(this);
        WindowSystem.AddWindow(_mainWindow);

        GetModListIpc = new GetModList(PluginInterface);
        GetModPathIpc = new GetModPath(PluginInterface);

        // Read-only MVP: observe live changes, never call any write endpoint (e.g. SetModPath).
        _modAdded = ModAdded.Subscriber(PluginInterface, dir => _mainWindow.LogEvent($"Mod added: {dir}"));
        _modDeleted = ModDeleted.Subscriber(PluginInterface, dir => _mainWindow.LogEvent($"Mod deleted: {dir}"));
        _modMoved = ModMoved.Subscriber(PluginInterface,
            (oldDir, newDir) => _mainWindow.LogEvent($"Mod moved: {oldDir} -> {newDir}"));

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the Penumbra Organizer (MVP) window.",
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        Log.Information("Penumbra Organizer (MVP) plugin loaded.");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        _modAdded.Dispose();
        _modDeleted.Dispose();
        _modMoved.Dispose();

        WindowSystem.RemoveAllWindows();
        _mainWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args) => ToggleMainUi();

    private void ToggleMainUi() => _mainWindow.Toggle();
}
