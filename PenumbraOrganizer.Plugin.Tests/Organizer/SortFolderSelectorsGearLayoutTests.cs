using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;
using PenumbraOrganizer.Plugin.Organizer.Templates;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class SortFolderSelectorsGearLayoutTests
{
    private static OrganizerModRow Gear(string id, string? slot, string? body) => new()
    {
        Identifier = id, Name = id, Author = "A", CurrentPath = id, ProposedPath = id,
        Category = ModCategory.Gear, SubCategory = slot, BodyType = body,
    };

    [Fact]
    public void BuildGearLayout_IsNullUnlessBothFlagsAreOn()
    {
        var rows = new[] { Gear("g", "Top", "Rue+") };

        Assert.Null(SortFolderSelectors.BuildGearLayout(rows, splitGear: true, splitGearBody: false, null));
        Assert.Null(SortFolderSelectors.BuildGearLayout(rows, splitGear: false, splitGearBody: true, null));
        Assert.NotNull(SortFolderSelectors.BuildGearLayout(rows, splitGear: true, splitGearBody: true, null));
    }

    [Fact]
    public void Select_WithALayout_UsesTheNumberedFolder()
    {
        var row = Gear("Dress", "Top", "Rue+");
        var layout = SortFolderSelectors.BuildGearLayout([row], true, true, null)!;

        var (primary, _) = SortFolderSelectors.Select(
            SortStrategy.TypeOnly, true, true, row, gearLayout: layout, splitGearBody: true);

        Assert.Equal("Gear/10. Rue+/02. Top", primary);
    }

    [Fact]
    public void Select_TemplateRename_AppliesToTheNumberedPath()
    {
        var row = Gear("Dress", "Top", "Rue+");
        var layout = SortFolderSelectors.BuildGearLayout([row], true, true, null)!;
        var rename = TemplateFolderLabels.Create(new Dictionary<string, string> { ["Gear"] = "Equipment" });

        var (primary, _) = SortFolderSelectors.Select(
            SortStrategy.TypeOnly, true, true, row, renameFolder: rename, gearLayout: layout, splitGearBody: true);

        Assert.Equal("Equipment/10. Rue+/02. Top", primary);
    }

    [Fact]
    public void Select_NonGearRows_IgnoreTheLayout()
    {
        var npc = new OrganizerModRow
        {
            Identifier = "n", Name = "n", Author = "A", CurrentPath = "n", ProposedPath = "n",
            Category = ModCategory.NPC, SubCategory = "Bosses",
        };
        var layout = GearFolderLayout.Build([], null);

        var (primary, _) = SortFolderSelectors.Select(SortStrategy.TypeOnly, true, true, npc, gearLayout: layout);

        Assert.Equal("NPC/Bosses", primary);
    }
}
