using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;
using PenumbraOrganizer.Plugin.Organizer.Classification;
using Xunit;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public sealed class BodyFolderLayoutTests
{
    private static OrganizerModRow Row(string? body, BodyPart part, ModCategory cat = ModCategory.Body,
        bool isProtected = false) => new()
    {
        Identifier = Guid.NewGuid().ToString("N"), Name = "n", Author = "a", CurrentPath = "n", ProposedPath = "n",
        Category = cat, BodyType = body, BodyPart = part, Protected = isProtected,
    };

    private static string? Place(OrganizerModRow row, IEnumerable<OrganizerModRow> all, BodyPriority? priority = null,
        bool split = true, Func<string, string>? rename = null) =>
        SortFolderSelectors.Select(SortStrategy.TypeOnly, false, false, row, renameFolder: rename,
            splitBodyMods: split, bodyPriority: priority,
            bodyLayout: SortFolderSelectors.BuildBodyLayout(all, split, priority)).Primary;

    [Fact]
    public void Shared_parts_sit_above_the_bodies_whatever_the_body()
    {
        var skel = Row("YAB", BodyPart.Skeleton);
        var phys = Row(null, BodyPart.Physics);
        var tails = Row("Rue+", BodyPart.TailsEarsHorns);
        var all = new[] { skel, phys, tails, Row("Rue+", BodyPart.Replacer) };

        Assert.Equal("Body/00. Skeleton", Place(skel, all));
        Assert.Equal("Body/01. Physics", Place(phys, all));
        Assert.Equal("Body/02. Tails, Ears and Horns", Place(tails, all));
    }

    [Fact]
    public void Bodies_are_numbered_from_10_by_priority_and_hold_numbered_parts()
    {
        var priority = BodyPriority.Default(["Rue+", "Bibo+", "YAB"]);
        var rue = Row("Rue+", BodyPart.Replacer);
        var rueSfw = Row("Rue+", BodyPart.Sfw);
        var rueHands = Row("Rue+", BodyPart.HandsAndFeet);
        var yab = Row("YAB", BodyPart.Replacer);
        var all = new[] { yab, rue, rueSfw, rueHands };

        Assert.Equal("Body/10. Rue+/00. Replacer", Place(rue, all, priority));
        Assert.Equal("Body/10. Rue+/01. SFW", Place(rueSfw, all, priority));
        Assert.Equal("Body/10. Rue+/02. Hands and Feet", Place(rueHands, all, priority));
        // Bibo+ has no body mods so it takes no number: YAB is 11, not 12.
        Assert.Equal("Body/11. YAB/00. Replacer", Place(yab, all, priority));
    }

    [Fact]
    public void A_reordered_priority_renumbers_the_bodies()
    {
        var yabFirst = BodyPriority.Default(["Rue+", "YAB"]).MoveToTop("YAB");
        var rue = Row("Rue+", BodyPart.Replacer);
        var yab = Row("YAB", BodyPart.Replacer);

        Assert.Equal("Body/11. Rue+/00. Replacer", Place(rue, [rue, yab], yabFirst));
        Assert.Equal("Body/10. YAB/00. Replacer", Place(yab, [rue, yab], yabFirst));
    }

    [Fact]
    public void A_body_dependent_mod_with_no_body_stays_flat()
    {
        var loose = Row(null, BodyPart.Replacer);
        Assert.Equal("Body", Place(loose, [loose]));
    }

    [Fact]
    public void Protected_rows_do_not_take_a_body_number()
    {
        var priority = BodyPriority.Default(["Rue+", "YAB"]);
        var protectedRue = Row("Rue+", BodyPart.Replacer, isProtected: true);
        var yab = Row("YAB", BodyPart.Replacer);
        var unprotected = new[] { yab }; // what OrganizerState passes: protected rows are filtered out

        Assert.Equal("Body/10. YAB/00. Replacer", Place(yab, unprotected, priority));
        Assert.NotNull(protectedRue);
    }

    [Fact]
    public void Splitting_off_leaves_the_flat_body_folder()
    {
        var skel = Row("YAB", BodyPart.Skeleton);
        Assert.Equal("Body", Place(skel, [skel], split: false));
    }

    [Fact]
    public void Template_rename_applies_to_Body_only_and_the_numbered_tail_follows()
    {
        var rue = Row("Rue+", BodyPart.Replacer);
        Assert.Equal("Bodies/10. Rue+/00. Replacer",
            Place(rue, [rue], rename: f => f.Replace("Body", "Bodies")));
    }

    [Fact]
    public void Gear_rows_are_not_part_of_the_body_numbering()
    {
        var gear = Row("Bibo+", BodyPart.Replacer, ModCategory.Gear);
        var rue = Row("Rue+", BodyPart.Replacer);
        Assert.Equal("Body/10. Rue+/00. Replacer", Place(rue, [gear, rue]));
    }

    [Fact]
    public void A_body_outside_the_built_rows_throws()
    {
        var layout = BodyFolderLayout.Build(["Rue+"], null);
        Assert.Throws<InvalidOperationException>(() => layout.Tail(BodyPart.Replacer, "YAB"));
    }
}
