using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;
using PenumbraOrganizer.Plugin.Organizer.Classification;
using Xunit;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public sealed class ActiveCollectionBodyDetectorTests
{
    private static readonly BodyPriority Priority = BodyPriority.Default(["Rue+", "Bibo+", "YAB"]);

    private static OrganizerModRow Row(string id, ModCategory cat, string? body, BodyPart part = BodyPart.Replacer) => new()
    {
        Identifier = id, Name = id, Author = "a", CurrentPath = id, ProposedPath = id,
        Category = cat, BodyType = body, BodyPart = part,
    };

    private static HashSet<string> Enabled(params string[] ids) => new(ids, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void One_enabled_body_is_returned() =>
        Assert.Equal("Rue+", ActiveCollectionBodyDetector.Detect(
            [Row("a", ModCategory.Body, "Rue+"), Row("b", ModCategory.Body, "Bibo+")], Enabled("a"), Priority));

    [Fact]
    public void Two_enabled_bodies_give_null() =>
        Assert.Null(ActiveCollectionBodyDetector.Detect(
            [Row("a", ModCategory.Body, "Rue+"), Row("b", ModCategory.Body, "Bibo+")], Enabled("a", "b"), Priority));

    [Fact]
    public void No_enabled_body_mods_give_null() =>
        Assert.Null(ActiveCollectionBodyDetector.Detect([Row("a", ModCategory.Body, "Rue+")], Enabled(), Priority));

    [Fact]
    public void Gear_and_non_replacer_parts_do_not_count() =>
        Assert.Equal("Rue+", ActiveCollectionBodyDetector.Detect(
            [Row("a", ModCategory.Body, "Rue+"),
             Row("g", ModCategory.Gear, "Bibo+"),
             Row("s", ModCategory.Body, "Bibo+", BodyPart.Skeleton)],
            Enabled("a", "g", "s"), Priority));

    [Fact]
    public void Body_mod_without_a_resolved_body_is_ignored() =>
        Assert.Equal("Rue+", ActiveCollectionBodyDetector.Detect(
            [Row("a", ModCategory.Body, "Rue+"), Row("u", ModCategory.Body, null)], Enabled("a", "u"), Priority));

    [Fact]
    public void Identifier_match_ignores_case() =>
        Assert.Equal("Rue+", ActiveCollectionBodyDetector.Detect(
            [Row("Mod-A", ModCategory.Body, "Rue+")], Enabled("mod-a"), Priority));
}
