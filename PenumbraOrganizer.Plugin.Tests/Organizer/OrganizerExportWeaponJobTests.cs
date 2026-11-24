using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class OrganizerExportWeaponJobTests
{
    private static OrganizerModRow Row(ModCategory category, string? job = null) => new()
    {
        Identifier = "id", Name = "Katana", Author = "A", CurrentPath = "Loose/Katana", ProposedPath = "Loose/Katana",
        Category = category, WeaponJob = job,
    };

    private static string Format(OrganizerModRow row) =>
        OrganizerExportFormatter.Format([row], new ReviewResult([], new Dictionary<string, List<string>>()));

    [Fact]
    public void AWeaponRow_ShowsItsJob_OrNone()
    {
        Assert.Contains("WeaponJob: SAM", Format(Row(ModCategory.Weapon, "SAM")));
        Assert.Contains("WeaponJob: (none)", Format(Row(ModCategory.Weapon)));
    }

    [Fact]
    public void EveryOtherRow_HasNoWeaponJobLine() =>
        Assert.DoesNotContain("WeaponJob", Format(Row(ModCategory.Gear)));
}
