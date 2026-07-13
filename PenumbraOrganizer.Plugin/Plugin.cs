using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;
using PenumbraOrganizer.Plugin.Organizer.Classification;
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

    internal readonly Penumbra.Api.IpcSubscribers.GetModListAdapter GetModListAdapterIpc;
    public readonly Organizer.OrganizerState OrganizerState = new();
    internal Configuration Config = null!;

    private readonly EventSubscriber<string> _modAdded;
    private readonly EventSubscriber<string> _modDeleted;
    private readonly EventSubscriber<string, string> _modMoved;

    public Plugin()
    {
        _mainWindow = new MainWindow(this);
        WindowSystem.AddWindow(_mainWindow);

        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        GetModListAdapterIpc = new Penumbra.Api.IpcSubscribers.GetModListAdapter(PluginInterface);

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
        // No separate settings window; the installer's config button opens the main window.
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;

        Log.Information("Penumbra Organizer (MVP) plugin loaded.");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;

        _modAdded.Dispose();
        _modDeleted.Dispose();
        _modMoved.Dispose();

        WindowSystem.RemoveAllWindows();
        _mainWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args) => ToggleMainUi();

    private void ToggleMainUi() => _mainWindow.Toggle();

    public void RunScan()
    {
        // One bulk call for all mods' changed items (Approach B in the Phase 1c spec).
        // Plain dictionary, not disposable. If Penumbra is unavailable this throws and
        // surfaces through MainWindow's existing scan error handling.
        var allChangedItems = new Penumbra.Api.IpcSubscribers.GetChangedItemAdapterDictionary(PluginInterface).Invoke();

        using var modList = GetModListAdapterIpc.Invoke();

        var rows = modList.Select(mod =>
        {
            var classification = allChangedItems.TryGetValue(mod.Identifier, out var changedItems)
                ? ModTypeClassifier.Classify(changedItems.Keys)
                : ClassificationResult.Unknown;

            return new Organizer.OrganizerModRow
            {
                Identifier = mod.Identifier,
                Name = mod.Name,
                Author = mod.Author,
                CurrentPath = mod.FullPath,
                ProposedPath = mod.FullPath,
                HeliosphereManaged = Organizer.HeliosphereDetector.IsHeliosphereManaged(mod.Identifier, mod.ModPath),
                Category = classification.Category,
                SubCategory = classification.SubCategory,
            };
        }).ToList();

        OrganizerState.LoadScan(rows, Config.ProtectedModIdentifiers);
        SaveProtectionState();
    }

    internal void SaveProtectionState()
    {
        Config.ProtectedModIdentifiers = OrganizerState.Mods
            .Where(m => m.Protected)
            .Select(m => m.Identifier)
            .ToHashSet();
        PluginInterface.SavePluginConfig(Config);
    }
}
