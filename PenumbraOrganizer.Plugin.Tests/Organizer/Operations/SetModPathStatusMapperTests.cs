using Penumbra.Api.Enums;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class SetModPathStatusMapperTests
{
    [Theory]
    [InlineData(PenumbraApiEc.Success, SetModPathStatus.Success)]
    [InlineData(PenumbraApiEc.NothingChanged, SetModPathStatus.NothingChanged)]
    [InlineData(PenumbraApiEc.ModMissing, SetModPathStatus.ModMissing)]
    [InlineData(PenumbraApiEc.InvalidGamePath, SetModPathStatus.InvalidArgument)]
    [InlineData(PenumbraApiEc.InvalidManipulation, SetModPathStatus.InvalidArgument)]
    [InlineData(PenumbraApiEc.InvalidArgument, SetModPathStatus.InvalidArgument)]
    [InlineData(PenumbraApiEc.PathRenameFailed, SetModPathStatus.PathRenameFailed)]
    [InlineData(PenumbraApiEc.SystemDisposed, SetModPathStatus.ProviderUnavailable)]
    [InlineData(PenumbraApiEc.CollectionMissing, SetModPathStatus.Rejected)]
    [InlineData(PenumbraApiEc.OptionGroupMissing, SetModPathStatus.Rejected)]
    [InlineData(PenumbraApiEc.OptionMissing, SetModPathStatus.Rejected)]
    [InlineData(PenumbraApiEc.CharacterCollectionExists, SetModPathStatus.Rejected)]
    [InlineData(PenumbraApiEc.LowerPriority, SetModPathStatus.Rejected)]
    [InlineData(PenumbraApiEc.FileMissing, SetModPathStatus.Rejected)]
    [InlineData(PenumbraApiEc.CollectionExists, SetModPathStatus.Rejected)]
    [InlineData(PenumbraApiEc.AssignmentCreationDisallowed, SetModPathStatus.Rejected)]
    [InlineData(PenumbraApiEc.AssignmentDeletionDisallowed, SetModPathStatus.Rejected)]
    [InlineData(PenumbraApiEc.InvalidIdentifier, SetModPathStatus.Rejected)]
    [InlineData(PenumbraApiEc.AssignmentDeletionFailed, SetModPathStatus.Rejected)]
    [InlineData(PenumbraApiEc.TemporarySettingDisallowed, SetModPathStatus.Rejected)]
    [InlineData(PenumbraApiEc.TemporarySettingImpossible, SetModPathStatus.Rejected)]
    [InlineData(PenumbraApiEc.InvalidCredentials, SetModPathStatus.Rejected)]
    [InlineData(PenumbraApiEc.CollectionInactive, SetModPathStatus.Rejected)]
    [InlineData(PenumbraApiEc.UnknownError, SetModPathStatus.Rejected)]
    public void Map_EveryPenumbraApiEcMember_ReturnsTheDocumentedStatus(PenumbraApiEc ec, SetModPathStatus expected)
    {
        Assert.Equal(expected, SetModPathStatusMapper.Map(ec));
    }

    [Fact]
    public void Map_CoversEveryDefinedPenumbraApiEcMember()
    {
        // Regression guard: if a future Penumbra.Api version adds a new enum member, this test
        // fails loudly (falls through to Rejected via the switch's default arm, which is safe,
        // but the count mismatch below forces a human to consciously add a dedicated test case
        // and confirm Rejected is really the right default for the new member).
        var allMembers = Enum.GetValues<PenumbraApiEc>();
        Assert.Equal(24, allMembers.Length);
    }
}
