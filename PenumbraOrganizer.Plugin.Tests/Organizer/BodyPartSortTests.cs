using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Classification;
using Xunit;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public sealed class BodyPartSortTests
{
    private static OrganizerModRow Row(ModCategory cat, string? body, BodyPart part) => new()
    {
        Identifier = "m", Name = "n", Author = "a", CurrentPath = "n", ProposedPath = "n",
        Category = cat, BodyType = body, BodyPart = part,
    };

    private static string? Type(OrganizerModRow row, bool splitBodyMods, Func<string, string>? rename = null) =>
        SortFolderSelectors.Select(SortStrategy.TypeOnly, false, false, row,
            renameFolder: rename, splitBodyMods: splitBodyMods).Primary;

    [Theory]
    [InlineData(BodyPart.Replacer, "Body/Rue+/Replacer")]
    [InlineData(BodyPart.Skeleton, "Body/Rue+/Skeleton")]
    [InlineData(BodyPart.Physics, "Body/Rue+/Physics")]
    [InlineData(BodyPart.HandsAndFeet, "Body/Rue+/Hands and Feet")]
    public void Body_mod_gets_its_part_folder_when_the_flag_is_on(BodyPart part, string expected) =>
        Assert.Equal(expected, Type(Row(ModCategory.Body, "Rue+", part), true));

    [Fact]
    public void Sfw_part_folder() =>
        Assert.Equal("Body/Rue+/SFW", Type(Row(ModCategory.Body, "Rue+", BodyPart.Sfw), true));

    [Theory]
    [InlineData("Rue+")]
    [InlineData(null)]
    public void Tails_ears_and_horns_ignore_the_body(string? body) =>
        Assert.Equal("Body/Tails, Ears and Horns",
            Type(Row(ModCategory.Body, body, BodyPart.TailsEarsHorns), true));

    [Fact]
    public void Tails_stay_flat_when_the_flag_is_off() =>
        Assert.Equal("Body", Type(Row(ModCategory.Body, null, BodyPart.TailsEarsHorns), false));

    [Fact]
    public void Tails_folder_takes_the_template_rename_of_Body() =>
        Assert.Equal("Bodies/Tails, Ears and Horns",
            Type(Row(ModCategory.Body, null, BodyPart.TailsEarsHorns), true, f => f.Replace("Body", "Bodies")));

    [Fact]
    public void Flag_off_leaves_the_flat_body_folder() =>
        Assert.Equal("Body", Type(Row(ModCategory.Body, "Rue+", BodyPart.Skeleton), false));

    [Fact]
    public void No_resolved_body_means_no_part_folder() =>
        Assert.Equal("Body", Type(Row(ModCategory.Body, null, BodyPart.Skeleton), true));

    [Fact]
    public void Gear_never_gets_a_part_folder() =>
        Assert.Equal("Gear", Type(Row(ModCategory.Gear, "Rue+", BodyPart.Skeleton), true));

    [Fact]
    public void Template_rename_applies_before_the_part_is_appended() =>
        Assert.Equal("Bodies/Rue+/Physics",
            Type(Row(ModCategory.Body, "Rue+", BodyPart.Physics), true,
                f => f.Replace("Body/", "Bodies/")));
}
