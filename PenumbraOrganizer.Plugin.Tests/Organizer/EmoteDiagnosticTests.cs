using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class EmoteDiagnosticTests
{
    private static OrganizerModRow Row(string id, params string[] names) => new()
    {
        Identifier = id, Name = id, Author = "A", CurrentPath = id, ProposedPath = id,
        Category = ModCategory.Animation, SubCategory = "Emotes",
        EmoteNames = names.Length == 0 ? null : names.ToHashSet(),
    };

    [Fact]
    public void Summary_CountsOutcomesAndEachDistinctName()
    {
        var lines = EmoteDiagnostic.Summarize([
            Row("a", "Sit"), Row("b", "Sit", "Big Grin"), Row("c", "Sit", "Groundsit"), Row("d", "Big Grin"), Row("e")]);

        Assert.Contains(lines, l => l.Contains("Multiple=1") && l.Contains("Expressions=1") && l.Contains("Undetermined=1") && l.Contains("Single=2"));
        Assert.Contains(lines, l => l.Contains("Sit") && l.Contains("3"));
    }
}
