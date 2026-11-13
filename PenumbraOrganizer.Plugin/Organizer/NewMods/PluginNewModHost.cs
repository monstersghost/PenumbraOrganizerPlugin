using PenumbraOrganizer.Core.Services;
using PenumbraOrganizer.Plugin.LibraryWork;
using PenumbraOrganizer.Plugin.Organizer.Collections;
using PenumbraOrganizer.Plugin.Organizer.Operations;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Organizer.NewMods;

/// <summary>
/// The plugin side of <see cref="INewModSortHost"/>: thin delegation, with every start method turning an
/// exception into its message. Apply goes only through Plugin.StartApplyOperation.
/// </summary>
internal sealed class PluginNewModHost : INewModSortHost
{
    private readonly Plugin _plugin;
    private readonly MainWindow _window;
    private readonly Action<string> _chat;
    private readonly CreatorCanonicalizer _canonicalizer = new();

    public PluginNewModHost(Plugin plugin, MainWindow window, Action<string> chat)
    {
        _plugin = plugin;
        _window = window;
        _chat = chat;
    }

    public NewModAction Setting => _plugin.Config.NewModAction;

    public bool IsWindowOpen => _window.IsOpen;

    public bool HasStagedProposals => _plugin.OrganizerState.Mods.Any(m =>
        !m.Protected && !PenumbraPathSemantics.AreEquivalent(m.CurrentPath, m.ProposedPath, m.Name));

    public bool HasSessionOverrides => _plugin.OrganizerState.HasSessionOverrides;

    public string? AdmissionRejection() => _plugin.OperationController.AdmissionRejectionReason();

    public string? StartScan()
    {
        try
        {
            _plugin.RunScan();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public int ScanGeneration => _plugin.OrganizerState.ScanGeneration;

    public LibraryWorkStateSnapshot ScanState => _plugin.ScanWork.State;

    public ScopedSortResult SortScoped(SortScope scope)
    {
        var labels = _plugin.OrganizerState.BodyLabels ?? _plugin.BundledBodyLabels;
        var selection = SortSelectionFactory.FromConfig(_plugin.Config, labels);
        var inScope = SortRunner.Run(_plugin.OrganizerState, selection, _canonicalizer.Canonicalize, scope);
        var changed = _plugin.OrganizerState.Mods.Count(m =>
            !m.Protected && !PenumbraPathSemantics.AreEquivalent(m.CurrentPath, m.ProposedPath, m.Name));
        return new ScopedSortResult(inScope, changed);
    }

    public string? StartApply()
    {
        try
        {
            _plugin.StartApplyOperation();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public long OperationCompletionGeneration => _plugin.OperationController.State.CompletionGeneration;

    public OperationStateSnapshot OperationState => _plugin.OperationController.State;

    public void OpenReview() => _window.OpenOnReview();

    public void Chat(string message) => _chat(message);
}
