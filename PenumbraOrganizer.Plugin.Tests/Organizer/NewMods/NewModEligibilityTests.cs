using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.NewMods;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.NewMods;

public class NewModEligibilityTests
{
    private static readonly IReadOnlySet<string> NoIds = new HashSet<string>();
    private static readonly IReadOnlyDictionary<string, bool> NoRules = new Dictionary<string, bool>();

    private static NewModCandidate C(string id, string path, string? author = null, bool hs = false) =>
        new(id, id, author ?? "someone", path, hs);

    private static IReadOnlyList<string> Run(
        IEnumerable<NewModCandidate> c,
        IReadOnlySet<string>? ids = null,
        IReadOnlyDictionary<string, bool>? rules = null,
        ToolPolicy? policy = null) =>
        NewModEligibility.Filter(c, ids ?? NoIds, rules ?? NoRules, policy ?? ToolPolicy.Default)
            .Select(r => r.Identifier).ToList();

    [Fact]
    public void Root_Mod_Is_Eligible() => Assert.Equal(new[] { "a" }, Run(new[] { C("a", "a") }));

    [Fact]
    public void Mod_In_A_Folder_Is_Excluded() => Assert.Empty(Run(new[] { C("a", "Gear/a") }));

    [Fact]
    public void Protected_By_Id_Is_Excluded() =>
        Assert.Empty(Run(new[] { C("a", "a") }, ids: new HashSet<string> { "a" }));

    [Fact]
    public void Protected_By_Folder_Rule_Is_Excluded()
    {
        // A root mod has no parent folder, so a folder rule cannot reach it; the rule is still honoured
        // through the shared ProtectionRule, which resolves from the mod's own path.
        var rules = new Dictionary<string, bool> { ["Gear"] = true };
        Assert.Equal(new[] { "a" }, Run(new[] { C("a", "a") }, rules: rules));
    }

    [Fact]
    public void Heliosphere_Excluded_In_Protected_Mode_Included_In_Normal_Mode()
    {
        var hs = C("hs-1", "hs-1", hs: true);
        Assert.Empty(Run(new[] { hs }));
        var normal = new ToolPolicy(ToolMode.Normal, ToolMode.Protected, false);
        Assert.Equal(new[] { "hs-1" }, Run(new[] { hs }, policy: normal));
    }

    [Fact]
    public void Proteus_Made_Is_Excluded() =>
        Assert.Empty(Run(new[] { C("p1", "p1", author: "Proteus") }));

    [Fact]
    public void IsHeliosphere_Uses_Known_Ids_And_Detector()
    {
        var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "no-such-dir-xyz"));
        var known = new HashSet<string> { "k1" };
        Assert.True(NewModEligibility.IsHeliosphere("k1", dir, "n", known));
        Assert.True(NewModEligibility.IsHeliosphere("hs-2", dir, "n", known));
        Assert.False(NewModEligibility.IsHeliosphere("plain", dir, "n", known));
    }
}
