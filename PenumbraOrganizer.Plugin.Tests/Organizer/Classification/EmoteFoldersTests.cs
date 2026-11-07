using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class EmoteFoldersTests
{
    private static IReadOnlySet<string> Set(params string[] names) => names.ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void OnePrimaryEmote_UsesItsExactName() =>
        Assert.Equal("Dance 74", EmoteFolders.Tail(Set("Dance 74")));

    [Fact]
    public void ExpressionsAreIgnoredWhenARealEmoteIsPresent() =>
        Assert.Equal("Sit", EmoteFolders.Tail(Set("Sit", "Big Grin", "Ponder")));

    [Fact]
    public void SeveralPrimaryEmotes_GoToMultiple() =>
        Assert.Equal("Multiple", EmoteFolders.Tail(Set("Sit", "Groundsit", "Big Grin")));

    [Fact]
    public void OnlyExpressions_GoToExpressions() =>
        Assert.Equal("Expressions", EmoteFolders.Tail(Set("Big Grin", "Endure")));

    [Fact]
    public void NoNames_GoToUndetermined()
    {
        Assert.Equal("Undetermined", EmoteFolders.Tail(null));
        Assert.Equal("Undetermined", EmoteFolders.Tail(Set()));
    }

    [Fact]
    public void ExpressionMatchingIgnoresCase() =>
        Assert.Equal("Expressions", EmoteFolders.Tail(Set("big grin")));

    [Fact]
    public void NameWithSlashIsMadeStorable_NotShortened() =>
        Assert.Equal(PenumbraPathSemantics.FixName("A/B"), EmoteFolders.Tail(Set("A/B")));

    private static OrganizerModRow Emote(IReadOnlySet<string>? names) => new()
    {
        Identifier = "e", Name = "e", Author = "A", CurrentPath = "e", ProposedPath = "e",
        Category = ModCategory.Animation, SubCategory = "Emotes", EmoteNames = names,
    };

    [Fact]
    public void Select_SplitsEmotesOnlyWhenTheOptionIsOn()
    {
        var row = Emote(Set("Dance 74"));
        Assert.Equal("Animation and VFX/Emotes",
            SortFolderSelectors.Select(SortStrategy.TypeOnly, false, false, row).Primary);
        Assert.Equal("Animation and VFX/Emotes/Dance 74",
            SortFolderSelectors.Select(SortStrategy.TypeOnly, false, false, row,
                options: new LayoutOptions(SplitEmotes: true)).Primary);
    }

    [Fact]
    public void Select_NoEmoteNamesGoesToUndetermined() =>
        Assert.Equal("Animation and VFX/Emotes/Undetermined",
            SortFolderSelectors.Select(SortStrategy.TypeOnly, false, false, Emote(null),
                options: new LayoutOptions(SplitEmotes: true)).Primary);

    [Fact]
    public void Select_DoesNotTouchOtherAnimationRows()
    {
        var battle = new OrganizerModRow
        {
            Identifier = "b", Name = "b", Author = "A", CurrentPath = "b", ProposedPath = "b",
            Category = ModCategory.Animation, SubCategory = "Battle Animation",
        };
        Assert.Equal("Animation and VFX/Battle Animation",
            SortFolderSelectors.Select(SortStrategy.TypeOnly, false, false, battle,
                options: new LayoutOptions(SplitEmotes: true)).Primary);
    }

    [Fact]
    public void Select_TemplateRenameStillApplies() =>
        Assert.Equal("Moves/Sit",
            SortFolderSelectors.Select(SortStrategy.TypeOnly, false, false, Emote(Set("Sit")),
                renameFolder: f => f == "Animation and VFX/Emotes" ? "Moves" : f,
                options: new LayoutOptions(SplitEmotes: true)).Primary);
}
