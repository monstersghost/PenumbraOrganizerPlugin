using Penumbra.Api.Enums;

namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary>
/// Pure translation from the raw Penumbra.Api.Enums.PenumbraApiEc (23 members, no per-member XML
/// docs in the shipped package - see this plan's Global Constraints for the authoritative mapping
/// table, confirmed against Penumbra.Api 5.15.1's metadata directly) into the plugin's own
/// SetModPathStatus. Kept as its own pure static class - unlike PenumbraOperationsAdapter, which
/// wraps real IPC and cannot be unit-tested in this repo, this translation has zero Dalamud
/// dependency and is fully covered by SetModPathStatusMapperTests.
/// </summary>
public static class SetModPathStatusMapper
{
    public static SetModPathStatus Map(PenumbraApiEc ec) => ec switch
    {
        PenumbraApiEc.Success => SetModPathStatus.Success,
        PenumbraApiEc.NothingChanged => SetModPathStatus.NothingChanged,
        PenumbraApiEc.ModMissing => SetModPathStatus.ModMissing,
        PenumbraApiEc.InvalidGamePath => SetModPathStatus.InvalidArgument,
        PenumbraApiEc.InvalidManipulation => SetModPathStatus.InvalidArgument,
        PenumbraApiEc.InvalidArgument => SetModPathStatus.InvalidArgument,
        PenumbraApiEc.PathRenameFailed => SetModPathStatus.PathRenameFailed,
        PenumbraApiEc.SystemDisposed => SetModPathStatus.ProviderUnavailable,
        _ => SetModPathStatus.Rejected,
    };
}
