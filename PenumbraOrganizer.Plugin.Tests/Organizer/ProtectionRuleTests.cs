using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class ProtectionRuleTests
{
    private static OrganizerModRow Row(string id, string path, bool hs = false, ProteusRole role = ProteusRole.None) =>
        new()
        {
            Identifier = id, Name = id, Author = "a", CurrentPath = path, ProposedPath = path,
            HeliosphereManaged = hs, ProteusRole = role,
        };

    private static readonly HashSet<string> NoIds = new(StringComparer.Ordinal);
    private static readonly HashSet<string> NoOverrides = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, bool> NoRules = new(StringComparer.Ordinal);

    [Fact]
    public void PlainRow_IsNotProtected() =>
        Assert.False(ProtectionRule.IsProtected(Row("a", "a"), NoIds, NoRules, ToolPolicy.Default, NoOverrides));

    [Fact]
    public void ExplicitIdentifier_Protects() =>
        Assert.True(ProtectionRule.IsProtected(
            Row("a", "a"), new HashSet<string> { "a" }, NoRules, ToolPolicy.Default, NoOverrides));

    [Fact]
    public void ProtectedFolderRule_Protects_AndDeeperExcludeFrees()
    {
        var rules = new Dictionary<string, bool> { ["F"] = true, ["F/Sub"] = false };

        Assert.True(ProtectionRule.IsProtected(Row("a", "F/a"), NoIds, rules, ToolPolicy.Default, NoOverrides));
        Assert.False(ProtectionRule.IsProtected(Row("b", "F/Sub/b"), NoIds, rules, ToolPolicy.Default, NoOverrides));
    }

    [Fact]
    public void Heliosphere_ProtectedByDefault_AndOverrideFrees()
    {
        var row = Row("h", "h", hs: true);

        Assert.True(ProtectionRule.IsProtected(row, NoIds, NoRules, ToolPolicy.Default, NoOverrides));
        Assert.False(ProtectionRule.IsProtected(
            row, NoIds, NoRules, ToolPolicy.Default, new HashSet<string> { "h" }));
    }

    [Fact]
    public void ProteusManaged_ProtectedUnlessAtRoot()
    {
        var row = Row("p", "p", role: ProteusRole.Managed);

        Assert.True(ProtectionRule.IsProtected(row, NoIds, NoRules, ToolPolicy.Default, NoOverrides));
        Assert.False(ProtectionRule.IsProtected(
            row, NoIds, NoRules, new ToolPolicy(ToolMode.Protected, ToolMode.Protected, true), NoOverrides));
    }
}
