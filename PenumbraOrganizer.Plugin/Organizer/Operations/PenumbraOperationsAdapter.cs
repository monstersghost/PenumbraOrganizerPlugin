using Dalamud.Plugin;
using Dalamud.Plugin.Ipc.Exceptions;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary>
/// The real IPenumbraOperations implementation, wrapping the actual Penumbra IPC subscribers -
/// design doc section 2. Deliberately the only file in Organizer/Operations that references
/// Penumbra.Api/Dalamud types directly, keeping every other file in this folder (and everything
/// they depend on) Dalamud-free and unit-testable, per Plan B1's Task 1 design intent.
///
/// Every method catches exactly Dalamud.Plugin.Ipc.Exceptions.IpcError (thrown when Penumbra
/// hasn't registered the IPC endpoint - not loaded, or unloaded mid-operation) and translates it
/// to the corresponding ProviderUnavailable status. Any OTHER exception is deliberately left to
/// propagate uncaught: PathMutationOperation.Advance's own boundary (already built, Plan B1)
/// classifies an uncaught exception as MutationStopReason.UnexpectedFatalException, the
/// conservative-by-default behavior this whole engine relies on - catching everything here would
/// silently reclassify genuine bugs as "the provider is unavailable."
/// </summary>
public sealed class PenumbraOperationsAdapter : IPenumbraOperations
{
    private readonly GetModListAdapter _getModListAdapterIpc;
    private readonly SetModPath _setModPathIpc;
    private readonly RedrawAll _redrawAllIpc;

    public PenumbraOperationsAdapter(IDalamudPluginInterface pluginInterface)
    {
        _getModListAdapterIpc = new GetModListAdapter(pluginInterface);
        _setModPathIpc = new SetModPath(pluginInterface);
        _redrawAllIpc = new RedrawAll(pluginInterface);
    }

    public LiveModReadResult GetLiveMods()
    {
        try
        {
            using var modList = _getModListAdapterIpc.Invoke();
            var mods = modList.Select(mod => new LiveMod(
                mod.Identifier, mod.Name, mod.FullPath,
                HeliosphereDetector.IsHeliosphereManaged(mod.Identifier, mod.ModPath)));
            return new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build(mods));
        }
        catch (IpcError)
        {
            return new LiveModReadResult(LiveModReadStatus.ProviderUnavailable, null);
        }
    }

    public SetModPathResult SetModPath(string identifier, string targetPath)
    {
        try
        {
            var ec = _setModPathIpc.Invoke(identifier, targetPath, "");
            return new SetModPathResult(
                SetModPathStatusMapper.Map(ec), ec.ToString(),
                ec == PenumbraApiEc.Success ? null : $"Penumbra returned {ec}.");
        }
        catch (IpcError ex)
        {
            return new SetModPathResult(SetModPathStatus.ProviderUnavailable, null, ex.Message);
        }
    }

    public RefreshResult RequestPostMutationRefresh()
    {
        try
        {
            _redrawAllIpc.Invoke(RedrawType.Redraw);
            return new RefreshResult(RefreshStatus.Success);
        }
        catch (IpcError)
        {
            return new RefreshResult(RefreshStatus.ProviderUnavailable);
        }
    }
}
