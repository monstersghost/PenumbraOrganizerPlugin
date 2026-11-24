using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;
using PenumbraOrganizer.Plugin.Organizer.Classification;
using PenumbraOrganizer.Plugin.Organizer.Templates;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Layouts;

// WHAT THIS IS. The equivalence net for the category-layout work. Every legacy Sort configuration (grouping x
// seven split flags x five race setups x two hair priorities) is migrated, turned into a selection and sorted over a
// fixture that exercises every split; the paths and the fingerprint are hashed per configuration and compared with a
// golden file CAPTURED FROM THE CODE BEFORE THE LAYOUT MODEL EXISTED. The template planner's fallback is pinned the
// same way.
//
// IT IS SAMPLED EQUIVALENCE, NOT EXHAUSTIVE. It covers four groupings x 128 flag combinations x five race setups x
// two hair settings on ONE fixture (with one protected row), plus the template fallback (four groupings x 32 flag
// combinations x two hair settings). It does not cover keyword rules, scoped sorts, tool placement or sort-inside
// anchors, and it does not hash the staged race/body annotations; those paths are unchanged as long as no category
// placement is configured.
//
// NEVER REGENERATE THE GOLDEN TO MAKE THIS PASS: a difference is a behaviour change, so fix the code. The file's
// SHA-256 is pinned below (GoldenSha256) so a silently regenerated file fails too. Legitimate regeneration happens
// only for an INTENDED behaviour change (for example spec 3), and only at the parent commit of that change, in a
// commit of its own: check out the parent, run once with PO_WRITE_SORT_GOLDEN=1 (the run writes the file next to
// this source and then fails on purpose with "golden written; unset PO_WRITE_SORT_GOLDEN"), update GoldenSha256 to
// the value that message prints, and commit both files alone.
public class SortEquivalenceGoldenTests
{
    private const string WriteVariable = "PO_WRITE_SORT_GOLDEN";
    private const string GoldenFileName = "sort-equivalence.golden.txt";

    // SHA-256 (uppercase hex) of the golden's lines joined with '\n' (line-ending independent, so a CRLF checkout
    // hashes the same as the LF blob in the repository).
    private const string GoldenSha256 = "124D27DE745246520E0C6C3BF3180FF0E0A6416B8F9319182832D232A983B8A8";

    private static readonly string[] Labels = ["Bibo+", "Rue+", "TBSE"];

    private static IReadOnlySet<T> Set<T>(params T[] items) => new HashSet<T>(items);

    private static RowRaceData Keys(string[]? face = null, string[]? iris = null, string[]? skin = null,
        string[]? tail = null, bool irisOnly = false) =>
        new(Set(face ?? []), Set(iris ?? []), Set(skin ?? []), Set(tail ?? []), irisOnly);

    private static OrganizerModRow Row(string id, ModCategory? category, string? sub = null, string author = "Ann",
        string? body = null, BodyPart part = BodyPart.Replacer, RowRaceData? keys = null,
        IReadOnlySet<int>? hairFlat = null, Dictionary<string, IReadOnlySet<int>>? hairByRace = null,
        IReadOnlySet<string>? emotes = null, bool racialScaling = false, RspEvidence? rsp = null,
        BodyCandidates? candidates = null, string? path = null) =>
        new()
        {
            Identifier = id, Name = $"Mod {id}", Author = author,
            CurrentPath = path ?? $"Loose/Mod {id}", ProposedPath = path ?? $"Loose/Mod {id}",
            Category = category, SubCategory = sub, BodyType = body, BodyPart = part, KeyRaces = keys,
            HairFlat = hairFlat, HairByRaceCode = hairByRace, EmoteNames = emotes,
            IsRacialScaling = racialScaling, Rsp = rsp, BodyCandidates = candidates,
        };

    // Each row is here to make some pair of settings produce different paths. Fresh objects per call: a sort
    // mutates ProposedPath and the staged fields.
    private static List<OrganizerModRow> Fixture() =>
    [
        Row("g-top-bibo", ModCategory.Gear, "Top", body: "Bibo+"),
        Row("g-hands-rue", ModCategory.Gear, "Hands", author: "Bob", body: "Rue+"),
        Row("g-feet", ModCategory.Gear, "Feet"),
        Row("g-head", ModCategory.Gear, "Head", author: "Cy"),
        Row("g-set-bibo", ModCategory.Gear, null, body: "Bibo+"),
        Row("g-cand", ModCategory.Gear, "Legs", body: "Bibo+", candidates: new BodyCandidates
        {
            Content = [new BodyCandidate("Rue+", BodyEvidenceSource.Option), new BodyCandidate("Bibo+", BodyEvidenceSource.Option)],
        }),
        Row("g-rsp", ModCategory.Gear, null, racialScaling: true, rsp: new RspEvidence(Set("Viera"), true, true)),
        Row("w", ModCategory.Weapon, author: ""),
        Row("npc-boss", ModCategory.NPC, "Bosses", author: "Dee"),
        Row("npc-flat", ModCategory.NPC),
        Row("h-one", ModCategory.Hair, "115", hairFlat: Set(115),
            hairByRace: new() { ["1801"] = Set(115) }),
        Row("h-multi", ModCategory.Hair, null, hairFlat: Set(3, 169),
            hairByRace: new() { ["0801"] = Set(169), ["1801"] = Set(3), ["0101"] = Set(169), ["0201"] = Set(169) }),
        Row("h-none", ModCategory.Hair),
        Row("b-repl-bibo", ModCategory.Body, body: "Bibo+"),
        Row("b-sfw-rue", ModCategory.Body, body: "Rue+", part: BodyPart.Sfw),
        Row("b-skel", ModCategory.Body, part: BodyPart.Skeleton),
        Row("b-teh", ModCategory.Body, part: BodyPart.TailsEarsHorns, keys: Keys(tail: ["Viera"])),
        Row("b-plain", ModCategory.Body),
        Row("f-viera", ModCategory.Face, keys: Keys(face: ["Viera"])),
        Row("f-multi", ModCategory.Face, keys: Keys(face: ["Viera", "Elezen"])),
        Row("f-iris", ModCategory.Face, keys: Keys(iris: ["Elezen"], irisOnly: true)),
        Row("s-miqo", ModCategory.Skin, keys: Keys(skin: ["Miqo'te"])),
        Row("a-dance", ModCategory.Animation, "Emotes", emotes: Set("Dance")),
        Row("a-expr", ModCategory.Animation, "Emotes", emotes: Set("Endure")),
        Row("a-battle", ModCategory.Animation, "Battle Animation"),
        Row("vfx", ModCategory.VFX, "VFX"),
        Row("minion", ModCategory.Minion, author: "Eve"),
        Row("others", ModCategory.Others),
        Row("unknown", null, author: "Fay"),
        Row("prot", ModCategory.Gear, "Top", path: "Kept/Mod prot"),
    ];

    private static OrganizerState Loaded()
    {
        var state = new OrganizerState();
        state.LoadScan(Fixture(), new HashSet<string> { "prot" }, bodyLabels: Labels);
        return state;
    }

    private static readonly (string Key, Action<Configuration> Apply)[] RaceSetups =
    [
        ("race-unmigrated-off", c => { }),
        ("race-legacy-hair", c => c.SortSplitHairByRace = true),
        ("race-all", c => c.RaceSplitEnabled = true),
        ("race-some-ranked", c =>
        {
            c.RaceSplitEnabled = true;
            c.RaceSplitRows = ["Face", "Eyes", "Skin", "RacialScaling"];
            c.SendMultiRaceToMultiRace = false;
            c.RacePriority = ["Viera", "Elezen"];
        }),
        ("race-master-off-hair", c => { c.RaceSplitEnabled = false; c.RaceSplitRows = ["Hair"]; }),
    ];

    private static IEnumerable<(string Key, Configuration Config)> Configurations()
    {
        foreach (var (strategy, _) in SortPanel.Groupings)
        for (var bits = 0; bits < 128; bits++)
        foreach (var (raceKey, applyRace) in RaceSetups)
        foreach (var hairPriority in new[] { false, true })
        {
            var config = new Configuration
            {
                SortGrouping = strategy.ToString(),
                SortSplitGear = (bits & 1) != 0,
                SortSplitNpc = (bits & 2) != 0,
                SortSplitHair = (bits & 4) != 0,
                SortSplitGearBody = (bits & 8) != 0,
                SortSplitBodyMods = (bits & 16) != 0,
                SortSharedHandsFeet = (bits & 32) != 0,
                SortSplitEmotes = (bits & 64) != 0,
                HairRaceCode = hairPriority ? "1801" : null,
                MidlanderFallback = hairPriority,
                BodyPriority = ["Rue+"],
            };
            applyRace(config);
            yield return ($"{strategy}|{Convert.ToString(bits, 2).PadLeft(7, '0')}|{raceKey}|hp={(hairPriority ? 1 : 0)}", config);
        }
    }

    private static string Hash(IEnumerable<string> lines)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines)));
        return Convert.ToHexString(bytes, 0, 8);
    }

    private static string FileHash(IEnumerable<string> lines) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));

    private static List<string> SortLines()
    {
        var lines = new List<string>();
        foreach (var (key, config) in Configurations())
        {
            config.MigrateToCurrentVersion();
            var selection = SortSelectionFactory.FromConfig(config, Labels);
            var state = Loaded();
            SortRunner.Run(state, selection, name => name.ToUpperInvariant(), scope: null);
            var paths = state.Mods.OrderBy(m => m.Identifier, StringComparer.Ordinal)
                .Select(m => $"{m.Identifier}={m.ProposedPath}");
            lines.Add($"sort|{key}|fp={Hash([selection.Fingerprint()])}|paths={Hash(paths)}");
        }

        return lines;
    }

    private static List<string> TemplateLines()
    {
        var lines = new List<string>();
        var bodyPriority = BodyPriority.Repair(["Rue+"], Labels, out _);
        foreach (var (strategy, _) in SortPanel.Groupings)
        for (var bits = 0; bits < 32; bits++)
        foreach (var hairPriority in new[] { false, true })
        {
            var fallback = new TemplateFallback(strategy, (bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0,
                (bits & 8) != 0, (bits & 16) != 0);
            var template = new ValidatedOrganizationTemplate("T", null, null, fallback,
                new Dictionary<string, string> { ["Gear"] = "Equipment" }, [],
                new Dictionary<string, string>());
            var rows = Loaded().Mods;
            var plan = TemplatePlanner.Plan(template, rows.ToList(), name => name, null,
                hairPriority ? new HairPriority("1801", true) : null, bodyPriority);
            var folders = plan.DestinationFolders.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => $"{p.Key}={p.Value}");
            lines.Add($"template|{strategy}|{Convert.ToString(bits, 2).PadLeft(5, '0')}|hp={(hairPriority ? 1 : 0)}|folders={Hash(folders)}");
        }

        return lines;
    }

    // Write mode only: the source folder, so the regenerated file lands in the repository.
    private static string SourceGoldenPath([CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, GoldenFileName);

    // Compare mode: the copy in the test output directory (CopyToOutputDirectory in the test project).
    private static string OutputGoldenPath() =>
        Path.Combine(AppContext.BaseDirectory, "Organizer", "Layouts", GoldenFileName);

    // The run must not depend on the machine's culture (sorting, casing, number formatting).
    private static T Invariant<T>(Func<T> body)
    {
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            return body();
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }

    [Fact]
    public void EveryLegacySelection_SortsExactlyAsBeforeTheLayoutModel()
    {
        var actual = Invariant(() => SortLines().Concat(TemplateLines()).ToList());

        if (Environment.GetEnvironmentVariable(WriteVariable) == "1")
        {
            var source = SourceGoldenPath();
            File.WriteAllLines(source, actual);
            Assert.Fail($"golden written; unset {WriteVariable} (file {source}, {actual.Count} lines, SHA-256 {FileHash(actual)})");
        }

        var path = OutputGoldenPath();
        Assert.True(File.Exists(path), $"Golden file missing: {path}. It is captured once, from the pre-layout code.");
        var expected = File.ReadAllLines(path);
        Assert.Equal(expected.Length, actual.Count);
        var firstDifference = Enumerable.Range(0, expected.Length).FirstOrDefault(i => expected[i] != actual[i], -1);
        Assert.True(firstDifference < 0,
            firstDifference < 0 ? "" : $"First difference at line {firstDifference + 1}:\n  golden: {expected[firstDifference]}\n  actual: {actual[firstDifference]}");
        Assert.True(FileHash(expected) == GoldenSha256,
            $"The golden file's SHA-256 is {FileHash(expected)}, pinned {GoldenSha256}: the golden was regenerated. Never regenerate it to make a test pass.");
    }

    [Fact]
    public void TheFixtureSeparatesTheSplits()
    {
        // A golden over a fixture that every setting maps to the same output would prove nothing: each flag, each
        // race setup and each hair-priority value must change at least one path for at least one combination.
        var all = Invariant(SortLines);
        string PathsOf(string line) => line.Split("paths=")[1];
        var byKey = all.ToDictionary(l => l.Split("|fp=")[0], PathsOf);

        var lines = all.Where(l => l.Contains("|race-unmigrated-off|hp=0")).ToList();
        string PathsFor(string strategy, int bits) =>
            PathsOf(lines.Single(l => l.StartsWith($"sort|{strategy}|{Convert.ToString(bits, 2).PadLeft(7, '0')}|")));

        // Base: slot split on (bit 0) so the shared hands-and-feet flag (bit 5) can take effect.
        foreach (var bit in new[] { 0, 1, 2, 3, 4, 6 })
            Assert.NotEqual(PathsFor("TypeOnly", 0), PathsFor("TypeOnly", 1 << bit));
        Assert.NotEqual(PathsFor("TypeOnly", 1 | 8), PathsFor("TypeOnly", 1 | 8 | 32));
        Assert.NotEqual(PathsFor("TypeOnly", 0), PathsFor("TypeThenCreator", 0));
        Assert.NotEqual(PathsFor("TypeThenCreator", 0), PathsFor("CreatorThenType", 0));

        // Each race setup sorts the fixture differently from every other one somewhere (same grouping, flags and
        // hair value). One pair is the exception by design: race-master-off-hair and race-unmigrated-off both have
        // the race master off, and with the master off no stored race row can change a path. That setup is in the
        // golden to pin that migrating saved rows with the master off keeps race splitting off, so its paths must
        // EQUAL the unmigrated-off baseline everywhere (and it still differs from every master-on setup).
        bool Differs(string a, string b) =>
            byKey.Any(p => p.Key.Contains($"|{a}|") && p.Value != byKey[p.Key.Replace($"|{a}|", $"|{b}|")]);
        var raceKeys = RaceSetups.Select(r => r.Key).ToList();
        foreach (var a in raceKeys)
        foreach (var b in raceKeys.Where(b => string.CompareOrdinal(a, b) < 0))
        {
            if ((a, b) is ("race-master-off-hair", "race-unmigrated-off"))
                Assert.False(Differs(a, b), "With the race master off, saved race rows changed a path.");
            else
                Assert.True(Differs(a, b), $"Race setups {a} and {b} sort the fixture identically for every combination.");
        }

        // The hair-priority value changes some path.
        Assert.Contains(byKey, p => p.Key.EndsWith("|hp=1") && p.Value != byKey[p.Key[..^1] + "0"]);

        // The template fallback lines are not all equal.
        var templates = Invariant(TemplateLines).Select(l => l.Split("folders=")[1]).Distinct().Count();
        Assert.True(templates > 1, "Every template fallback combination plans the same folders.");
    }
}
