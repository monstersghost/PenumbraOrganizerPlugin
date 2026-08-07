# Piece 1: Two NPC Name Lists and an Index Matcher Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the per-category compiled alternation regex with a first-token index, ship a curated 827-name static list as the default, and demote the wiki scrape to an opt-in list that is off by default.

**Architecture:** `NpcNameMatcher` stops building `Regex` entirely. Names are normalized, tokenized, merged by category flags and bucketed by first token. The bundled static list is an embedded resource; the scraped list is a separate opt-in file in the config directory, and the two are unioned when opted in.

**Tech Stack:** C# / .NET 10, Dalamud plugin, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-07-two-list-npc-names-design.md`. Read it before Task 1 — particularly "What is NOT established".

## Global Constraints

- **This is not a proven crash fix and must not be described as one** in code comments, release notes, or commit messages. What is established: resetting the large list stops the observed crash, and the mechanism is unknown. Justify the work on correctness and classification quality.
- **No `Regex` in `NpcNameMatcher`.** Enforced by an architecture test in Task 3, so the giant alternation cannot return by accident.
- **The epoch of behaviour change is deliberate and documented.** Two semantics change: Rune-based tokenization (fixes inconsistent splitting of non-BMP characters) and separator loosening (`Y'shtola` will also match `Y-shtola`). Both must be asserted by tests, not discovered.
- **Category precedence stays NPC, then Boss, then Enemy**, matching `Match` today. `SubCategoryFor` and every caller downstream are unchanged.
- **The scraped-list toggle ships disabled.** It is gated on reproducing the crash and verifying the new matcher in-game against a full 20,115-name list. That gate is a release decision, not a task here.
- Baseline: **912 tests passing**.

---

### Task 1: The index matcher, behind the existing public surface

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Classification/NpcNameMatcher.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/NpcNameMatcherTests.cs` (create if absent)

**Interfaces:**
- Consumes: nothing.
- Produces: `NpcNameMatcher(IReadOnlyList<string> npcs, IReadOnlyList<string> enemies, IReadOnlyList<string> bosses)` and `NpcNameMatch? Match(string modName)` keep their exact current signatures, so `ScanProcessor` and `IndexProcessor` need no change in this task.

- [ ] **Step 1: Write the semantics tests, including the two deliberate changes**

```csharp
public class NpcNameMatcherTests
{
    private static NpcNameMatcher Make(string[]? npcs = null, string[]? enemies = null, string[]? bosses = null) =>
        new(npcs ?? [], enemies ?? [], bosses ?? []);

    [Fact]
    public void Match_IsCaseInsensitive()
        => Assert.NotNull(Make(npcs: ["Y'shtola"]).Match("YSHTOLA hair"));

    [Fact]
    public void Match_RequiresWholeTokenBoundaries_NotSubstrings()
    {
        var m = Make(npcs: ["Bert"]);
        Assert.Null(m.Match("Albert Hair"));      // substring must not match
        Assert.NotNull(m.Match("Bert's Jacket")); // whole token must
    }

    [Fact]
    public void Match_TreatsUnderscoreAsABoundary()
    {
        // Deliberate: the old regex used (?<![\p{L}\p{N}]) rather than \b precisely so that
        // _Zenos_ matches. Underscore is not a letter or digit.
        Assert.NotNull(Make(npcs: ["Zenos"]).Match("_Zenos_ Glam"));
    }

    [Fact]
    public void Match_PrefersTheLongestMatch()
    {
        var m = Make(npcs: ["Alka Zolka", "Alka Zolka the Slayer"]);
        Assert.Equal("Alka Zolka the Slayer", m.Match("Cool Alka Zolka the Slayer Mod")!.Name);
    }

    [Fact]
    public void Match_LongestIsByTokenCount_ThenLength()
    {
        // Both are two tokens; the longer string wins the tie-break.
        var m = Make(npcs: ["Foo Bar", "Foo Barbara"]);
        Assert.Equal("Foo Barbara", m.Match("A Foo Barbara Thing")!.Name);
    }

    [Fact]
    public void Match_FoldsCurlyApostrophes()
        => Assert.NotNull(Make(npcs: ["Y'shtola"]).Match("Y’shtola Redesign"));

    [Fact]
    public void Match_PrecedenceIsNpcThenBossThenEnemy()
    {
        var m = Make(npcs: ["Bahamut"], enemies: ["Bahamut"], bosses: ["Bahamut"]);
        Assert.Equal(NpcNameKind.Npc, m.Match("Bahamut Wings")!.Kind);

        var noNpc = Make(enemies: ["Bahamut"], bosses: ["Bahamut"]);
        Assert.Equal(NpcNameKind.Boss, noNpc.Match("Bahamut Wings")!.Kind);
    }

    [Fact]
    public void Match_DeliberateChange_SeparatorsBetweenTokensAreInterchangeable()
    {
        // NEW behaviour. The old regex matched the literal "Y'shtola" only. Token matching
        // compares token sequences and ignores which separator sat between them. This is an
        // improvement for real mod titles and is asserted so it is a decision, not a surprise.
        var m = Make(npcs: ["Y'shtola"]);
        Assert.NotNull(m.Match("Y-shtola Hair"));
        Assert.NotNull(m.Match("Y shtola Hair"));
    }

    [Fact]
    public void Match_DeliberateChange_TokenizesByRuneNotChar()
    {
        // NEW behaviour. char.IsLetterOrDigit works on UTF-16 units and mishandles non-BMP
        // characters; mod titles here routinely contain emoji. A non-BMP char is a separator,
        // consistently, rather than splitting into two unpaired surrogates.
        var m = Make(npcs: ["Zenos"]);
        Assert.NotNull(m.Match("\U0001F600 Zenos \U0001F600"));
    }

    [Fact]
    public void Match_ReturnsNullWhenNothingMatches()
        => Assert.Null(Make(npcs: ["Zenos"]).Match("Kawaii Outfit Bundle"));

    [Fact]
    public void Match_EmptyMatcher_ReturnsNull()
        => Assert.Null(NpcNameMatcher.Empty.Match("anything at all"));
}
```

- [ ] **Step 2: Run them against the current regex implementation**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests --filter "NpcNameMatcherTests"
```

Expected: most pass (they describe existing behaviour), and the two named `DeliberateChange` tests **fail**. That is the point — they are the specification of what this task changes. Record the actual output.

- [ ] **Step 3: Replace the implementation**

Rewrite `NpcNameMatcher.cs` entirely:

```csharp
using System.Globalization;
using System.Text;

namespace PenumbraOrganizer.Plugin.Organizer.Classification;

public enum NpcNameKind { Npc, Enemy, Boss }

public sealed record NpcNameMatch(string Name, NpcNameKind Kind);

[Flags]
internal enum NpcNameKinds { None = 0, Npc = 1, Enemy = 2, Boss = 4 }

internal sealed record NpcNameEntry(string Display, string[] Tokens, NpcNameKinds Kinds);

// Matches a mod's display name against known NPC/enemy/boss names using a first-token index.
//
// Deliberately NOT a regex. The previous implementation built one compiled alternation per
// category; at a full wiki scrape (20,115 distinct names) that is a 205KB pattern per category,
// costs seconds of JIT on first use and tens of megabytes, and is implicated in reports of the
// game closing during a scan. A dictionary keyed on first token turns "test 20,115 alternatives"
// into one lookup plus a median of one comparison: the real list has 9,886 distinct first tokens
// with a median bucket size of 1 and a p99 of 18.
public sealed class NpcNameMatcher
{
    private readonly Dictionary<string, NpcNameEntry[]> _byFirstToken;

    public static readonly NpcNameMatcher Empty = new([], [], []);

    public NpcNameMatcher(IReadOnlyList<string> npcs, IReadOnlyList<string> enemies, IReadOnlyList<string> bosses)
    {
        // Merged rather than three parallel structures: 848 of 857 bosses also appear in Enemies
        // and 372 names appear in both NPCs and Enemies, so the same name would otherwise occupy
        // several slots in one bucket and precedence would fall out of sort order by accident.
        var merged = new Dictionary<string, NpcNameEntry>(StringComparer.OrdinalIgnoreCase);

        void Add(IReadOnlyList<string> names, NpcNameKinds kind)
        {
            foreach (var raw in names)
            {
                var tokens = Tokenize(Normalize(raw));
                if (tokens.Length == 0)
                    continue;

                var key = string.Join(' ', tokens);
                merged[key] = merged.TryGetValue(key, out var existing)
                    ? existing with { Kinds = existing.Kinds | kind }
                    : new NpcNameEntry(raw.Trim(), tokens, kind);
            }
        }

        Add(npcs, NpcNameKinds.Npc);
        Add(enemies, NpcNameKinds.Enemy);
        Add(bosses, NpcNameKinds.Boss);

        _byFirstToken = merged.Values
            .GroupBy(e => e.Tokens[0], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                // Ordering is defined, not implied: a match consumes tokens, so the longest match
                // is the one consuming most of them. Character length breaks ties, and an ordinal
                // comparison makes it a total order so results never depend on input file order.
                g => g.OrderByDescending(e => e.Tokens.Length)
                      .ThenByDescending(e => string.Join(' ', e.Tokens).Length)
                      .ThenBy(e => string.Join(' ', e.Tokens), StringComparer.Ordinal)
                      .ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    public NpcNameMatch? Match(string modName)
    {
        var tokens = Tokenize(Normalize(modName));
        if (tokens.Length == 0)
            return null;

        for (var start = 0; start < tokens.Length; start++)
        {
            if (!_byFirstToken.TryGetValue(tokens[start], out var candidates))
                continue;

            foreach (var candidate in candidates)
            {
                if (MatchesAt(tokens, start, candidate.Tokens))
                    return new NpcNameMatch(candidate.Display, Resolve(candidate.Kinds));
            }
        }

        return null;
    }

    private static bool MatchesAt(string[] modTokens, int start, string[] nameTokens)
    {
        if (start + nameTokens.Length > modTokens.Length)
            return false;

        for (var i = 0; i < nameTokens.Length; i++)
        {
            if (!string.Equals(modTokens[start + i], nameTokens[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    // Same precedence the regex version applied by checking NPC, then Boss, then Enemy in order.
    private static NpcNameKind Resolve(NpcNameKinds kinds) =>
        kinds.HasFlag(NpcNameKinds.Npc) ? NpcNameKind.Npc
        : kinds.HasFlag(NpcNameKinds.Boss) ? NpcNameKind.Boss
        : NpcNameKind.Enemy;

    // Maximal runs of letters or digits, iterated by Rune rather than char: char.IsLetterOrDigit
    // works on UTF-16 code units and mishandles anything outside the BMP, and mod titles here
    // routinely contain emoji. A boundary is "not a letter or digit", which is what the old
    // regex's (?<![\p{L}\p{N}]) meant, so underscore remains a separator.
    private static string[] Tokenize(string value)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();

        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                current.Append(rune.ToString());
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return [.. tokens];
    }

    // NFC normalization + curly-to-straight apostrophe folding, so a wiki title and a mod title
    // using different apostrophe glyphs for the same name still compare equal. Unchanged from the
    // regex implementation.
    internal static string Normalize(string value) =>
        value.Trim().Normalize(NormalizationForm.FormC).Replace('’', '\'');
}
```

- [ ] **Step 4: Run the tests again**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests --filter "NpcNameMatcherTests"
```

Expected: all pass, including the two `DeliberateChange` tests that failed in Step 2.

- [ ] **Step 5: Run the full suite**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests
```

Expected: 912 plus the new tests. **If any existing classification test fails, stop and report rather than adjusting it.** An existing test failing here means a semantic change nobody intended, which is exactly what this task must not do.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Classification/NpcNameMatcher.cs PenumbraOrganizer.Plugin.Tests/Organizer/Classification/NpcNameMatcherTests.cs
git commit -m "perf: match NPC names by first-token index instead of a compiled alternation"
```

---

### Task 2: Equivalence corpus against the old matcher

**Files:**
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/NpcNameMatcherEquivalenceTests.cs` (create)

**Interfaces:**
- Consumes: `NpcNameMatcher` from Task 1.
- Produces: nothing. This is a temporary guard, deleted once the change has shipped and settled.

**Background:** unit tests check the cases someone thought of. This checks the cases nobody thought of, by running both implementations over a corpus. It cannot assert global equality, because Task 1 deliberately changed two things — so it uses **two corpora with different rules**.

- [ ] **Step 1: Write the test**

```csharp
using System.Text.RegularExpressions;

public class NpcNameMatcherEquivalenceTests
{
    private static readonly string[] Names =
        ["Y'shtola", "Alphinaud", "Zenos", "Alka Zolka", "Alka Zolka the Slayer", "Bert", "Art", "2B"];

    // The implementation this replaces, reproduced so both can be run over the same corpus.
    private static Regex OldRegex(IEnumerable<string> names)
    {
        var normalized = names
            .Select(n => n.Trim().Normalize().Replace('’', '\''))
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(n => n.Length)
            .Select(Regex.Escape);
        return new Regex(
            $@"(?<![\p{{L}}\p{{N}}])(?:{string.Join("|", normalized)})(?![\p{{L}}\p{{N}}])",
            RegexOptions.IgnoreCase);
    }

    [Theory]
    // Conventional punctuation: old and new must agree exactly.
    [InlineData("Y'shtola Hair Redesign", true)]
    [InlineData("_Zenos_ Glam", true)]
    [InlineData("Albert Hair", false)]
    [InlineData("Concept Art Pack", true)]     // "Art" is a real boss name and matches as a token
    [InlineData("Kawaii Outfit Bundle", false)]
    [InlineData("Alka Zolka the Slayer Mod", true)]
    [InlineData("2B Outfit", true)]
    public void LegacyCorpus_OldAndNewAgree(string modName, bool expectedMatch)
    {
        var oldMatched = OldRegex(Names).IsMatch(modName.Trim().Normalize().Replace('’', '\''));
        var newMatched = new NpcNameMatcher(Names, [], []).Match(modName) is not null;

        Assert.Equal(expectedMatch, oldMatched);
        Assert.Equal(expectedMatch, newMatched);
    }

    [Theory]
    // The two documented differences. Old must NOT match; new MUST. If either side flips, the
    // change is no longer the one that was designed.
    [InlineData("Y-shtola Hair")]
    [InlineData("Y shtola Hair")]
    public void IntentionalDifferenceCorpus_SeparatorsAreNowInterchangeable(string modName)
    {
        Assert.False(OldRegex(Names).IsMatch(modName));
        Assert.NotNull(new NpcNameMatcher(Names, [], []).Match(modName));
    }
}
```

- [ ] **Step 2: Run it**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests --filter "NpcNameMatcherEquivalenceTests"
```

Expected: all pass. A failure in `LegacyCorpus` means Task 1 changed something it should not have; a failure in `IntentionalDifferenceCorpus` means it did not change what it was supposed to.

- [ ] **Step 3: Commit**

```bash
git add PenumbraOrganizer.Plugin.Tests/Organizer/Classification/NpcNameMatcherEquivalenceTests.cs
git commit -m "test: pin old-vs-new matcher equivalence and the two intended differences"
```

---

### Task 3: Scale guard and a no-Regex architecture test

**Files:**
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/NpcNameMatcherScaleTests.cs` (create)

**Interfaces:**
- Consumes: `NpcNameMatcher` from Task 1.
- Produces: nothing.

- [ ] **Step 1: Write both tests**

```csharp
using System.Diagnostics;
using System.Reflection;

public class NpcNameMatcherScaleTests
{
    [Fact]
    public void Build_And_MatchAtFullWikiScale_CompletesQuickly()
    {
        // 25,000 names, above the 20,115 a full scrape produces. The old implementation needed
        // ~240ms to build and ~4s of JIT on first match at this scale; this asserts a budget
        // generous enough not to be flaky on CI but tight enough to catch a return to that shape.
        var names = Enumerable.Range(0, 25_000).Select(i => $"Synthetic Name {i}").ToArray();

        var sw = Stopwatch.StartNew();
        var matcher = new NpcNameMatcher(names, [], []);
        var built = sw.ElapsedMilliseconds;

        sw.Restart();
        for (var i = 0; i < 2_000; i++)
            matcher.Match($"Some Mod About Synthetic Name {i} And Things");
        var matched = sw.ElapsedMilliseconds;

        Assert.True(built < 2_000, $"build took {built}ms");
        Assert.True(matched < 2_000, $"2,000 matches took {matched}ms");
    }

    [Fact]
    public void NpcNameMatcher_DoesNotUseRegex()
    {
        // The giant compiled alternation must not come back by accident. Checking the referenced
        // types rather than the source text means a using-directive change cannot defeat it.
        var type = typeof(NpcNameMatcher);
        var referenced = type.Assembly.GetTypes()
            .Where(t => t.Namespace == type.Namespace && t.Name.Contains("NpcName"))
            .SelectMany(t => t.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            .Select(f => f.FieldType.FullName ?? "");

        Assert.DoesNotContain(referenced, n => n.Contains("System.Text.RegularExpressions"));
    }
}
```

- [ ] **Step 2: Run and commit**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests --filter "NpcNameMatcherScaleTests"
```

```bash
git add PenumbraOrganizer.Plugin.Tests/Organizer/Classification/NpcNameMatcherScaleTests.cs
git commit -m "test: guard matcher scale and forbid Regex in NpcNameMatcher"
```

---

### Task 4: Ship the static list as an embedded resource

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/NpcNames/npc-name-list-static.json` (copy from `docs/superpowers/specs/2026-08-07-npc-name-list-static.json`)
- Modify: `PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj`
- Modify: `PenumbraOrganizer.Plugin/Organizer/NpcNames/NpcNameListStore.cs`
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs` (the `ReadEmbeddedNpcNameSeed` accessor)
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/NpcNames/NpcNameListStoreTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: the static list is the default matcher source. Task 5 adds the scraped list beside it.

- [ ] **Step 1: Copy the list and register it**

```bash
cp docs/superpowers/specs/2026-08-07-npc-name-list-static.json PenumbraOrganizer.Plugin/Organizer/NpcNames/npc-name-list-static.json
```

In `PenumbraOrganizer.Plugin.csproj`, beside the existing seed entry:

```xml
<EmbeddedResource Include="Organizer\NpcNames\npc-name-list-static.json" />
```

- [ ] **Step 2: Write the test**

```csharp
[Fact]
public void StaticList_LoadsFromTheEmbeddedResource_AndHasTheExpectedShape()
{
    var json = Plugin.ReadEmbeddedStaticNpcNameList();
    var parsed = NpcNameListCodec.Parse(json);

    Assert.Equal(NpcNameListParseStatus.Ok, parsed.Status);
    Assert.Equal(133, parsed.Data!.NPCs.Count);
    Assert.Equal(15, parsed.Data.Enemies.Count);
    Assert.Equal(679, parsed.Data.Bosses.Count);
}

[Fact]
public void StaticList_ContainsBothScionsAndPrimals()
{
    var doc = NpcNameListCodec.Parse(Plugin.ReadEmbeddedStaticNpcNameList()).Data!;

    Assert.Contains("Y'shtola", doc.NPCs);
    Assert.Contains("Alphinaud", doc.NPCs);
    Assert.Contains("Leveilleur", doc.NPCs);   // surname carries the whole family
    Assert.Contains("Titan", doc.Bosses);
    Assert.Contains("Shiva", doc.Bosses);
}
```

- [ ] **Step 3: Add the accessor**

In `Plugin.cs`, beside the existing `ReadEmbeddedNpcNameSeed`:

```csharp
internal static string ReadEmbeddedStaticNpcNameList()
{
    var assembly = typeof(Plugin).Assembly;
    const string resourceName = "PenumbraOrganizer.Plugin.Organizer.NpcNames.npc-name-list-static.json";
    using var stream = assembly.GetManifestResourceStream(resourceName)
        ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
}
```

- [ ] **Step 4: Run, then commit**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests --filter "NpcNameListStoreTests"
```

If the counts differ from 133/15/679, the list changed since the spec was written. **Update the test to the real numbers and say so in the report** — do not assume the list is wrong.

```bash
git add PenumbraOrganizer.Plugin/Organizer/NpcNames/npc-name-list-static.json PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj PenumbraOrganizer.Plugin/Plugin.cs PenumbraOrganizer.Plugin.Tests/Organizer/NpcNames/NpcNameListStoreTests.cs
git commit -m "feat: ship the curated static NPC name list as an embedded resource"
```

---

### Task 5: Two lists, migration, and the disabled opt-in

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/NpcNames/NpcNameListStore.cs`
- Modify: `PenumbraOrganizer.Plugin/Organizer/NpcNames/NpcNameRefreshService.cs`
- Modify: `PenumbraOrganizer.Plugin/Configuration.cs`
- Modify: `PenumbraOrganizer.Plugin/LibraryWork/Pure/ScanProcessor.cs`, `IndexProcessor.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/NpcNames/NpcNameListStoreTests.cs`

**Interfaces:**
- Consumes: `ReadEmbeddedStaticNpcNameList()` (Task 4), `NpcNameMatcher` (Task 1).
- Produces: `NpcNameListStore.LoadForMatching(string configDir, bool useScraped)` returning `NpcNameListLoadResult`. Piece 2 renders the checkbox that supplies `useScraped`.

- [ ] **Step 1: Write the migration and union tests**

```csharp
[Fact]
public void Migration_LegacyPresentScrapedAbsent_RenamesLegacy()
{
    var dir = MakeTempDir();
    File.WriteAllText(Path.Combine(dir, "npc-name-list.json"), ListJson(5));

    NpcNameListStore.MigrateLegacyList(dir);

    Assert.False(File.Exists(Path.Combine(dir, "npc-name-list.json")));
    Assert.True(File.Exists(Path.Combine(dir, "npc-name-list-scraped.json")));
}

[Fact]
public void Migration_BothPresent_LeavesBothUntouched()
{
    var dir = MakeTempDir();
    File.WriteAllText(Path.Combine(dir, "npc-name-list.json"), ListJson(5));
    File.WriteAllText(Path.Combine(dir, "npc-name-list-scraped.json"), ListJson(7));

    NpcNameListStore.MigrateLegacyList(dir);

    // Nothing is overwritten or deleted: both-present is reachable via an interrupted migration
    // or a downgrade cycle, which is exactly when the user has no backup.
    Assert.True(File.Exists(Path.Combine(dir, "npc-name-list.json")));
    Assert.Contains("\"Npc Name 6\"", File.ReadAllText(Path.Combine(dir, "npc-name-list-scraped.json")));
}

[Fact]
public void LoadForMatching_OptedOut_IgnoresTheScrapedFileEntirely()
{
    var dir = MakeTempDir();
    File.WriteAllText(Path.Combine(dir, "npc-name-list-scraped.json"), ListJson(5000));

    var result = NpcNameListStore.LoadForMatching(dir, useScraped: false);

    Assert.DoesNotContain("Npc Name 0", result.Document.NPCs);
    Assert.Contains("Y'shtola", result.Document.NPCs);   // static list only
}

[Fact]
public void LoadForMatching_OptedIn_UnionsStaticAndScraped()
{
    var dir = MakeTempDir();
    File.WriteAllText(Path.Combine(dir, "npc-name-list-scraped.json"), ListJson(5));

    var result = NpcNameListStore.LoadForMatching(dir, useScraped: true);

    Assert.Contains("Y'shtola", result.Document.NPCs);
    Assert.Contains("Npc Name 0", result.Document.NPCs);
}

[Fact]
public void LoadForMatching_CorruptScrapedFile_DegradesToStaticWithAWarning()
{
    var dir = MakeTempDir();
    File.WriteAllText(Path.Combine(dir, "npc-name-list-scraped.json"), "{ not json");

    var result = NpcNameListStore.LoadForMatching(dir, useScraped: true);

    Assert.NotNull(result.Warning);
    Assert.Contains("Y'shtola", result.Document.NPCs);
}
```

- [ ] **Step 2: Implement**

`MigrateLegacyList(string configDir)` implements the four-case table from the spec: legacy-only renames; both-present leaves both and logs; neither or scraped-only is a no-op. Wrap the rename in `try`/`catch (IOException or UnauthorizedAccessException)` and degrade to a warning — a failed migration must not prevent the plugin loading.

`LoadForMatching(string configDir, bool useScraped)` parses the embedded static list, and when `useScraped` is true additionally parses `npc-name-list-scraped.json`, unions the three name collections, and honours `Excluded` from the scraped document. A malformed or oversized scraped file degrades to static-only with a warning, reusing the `MaxSafeNameCount` guard already in this file.

`Configuration` gains `public bool UseScrapedNpcNameList { get; set; }` — default `false`.

`ScanProcessor` and `IndexProcessor` change their `Prepare` to call `LoadForMatching(configDir, useScraped)`; both take the two values through their constructors, which `ScanJob` and `IndexJob` supply from `Plugin.Config` on the framework thread.

- [ ] **Step 3: Refresh writes a snapshot, not a merge**

In `NpcNameRefreshService`, replace the `MergeAdditive` call so the scraped file is **this run's results minus exclusions**, with no reference to the previous file's names. That is the change that removes unbounded growth. Write to `npc-name-list-scraped.json`, never to `npc-name-list.json`.

Add:

```csharp
[Fact]
public void Refresh_ReplacesRatherThanGrowing()
{
    // Two refreshes returning the same wiki data must not double the file.
}
```

with the fake scraper the existing `NpcNameRefreshServiceTests` already uses.

- [ ] **Step 4: Full suite, then commit**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests
```

```bash
git add PenumbraOrganizer.Plugin/Organizer/NpcNames/ PenumbraOrganizer.Plugin/Configuration.cs PenumbraOrganizer.Plugin/LibraryWork/Pure/ PenumbraOrganizer.Plugin.Tests/Organizer/NpcNames/
git commit -m "feat: split the NPC name list into a bundled static list and an opt-in scraped list"
```

---

## Self-review notes

- **Spec coverage:** the index matcher with defined bucket ordering, merged category flags and Rune tokenization (Task 1); the equivalence corpora split by intent (Task 2); scale and the no-Regex guard (Task 3); the embedded static list (Task 4); two lists, the four-case migration, snapshot refresh and the config flag (Task 5).
- **The two deliberate behaviour changes each have a test that fails against the old implementation**, so they are decisions rather than accidents.
- **The opt-in checkbox itself is not in this plan.** It is a control on the Sort tab and belongs to piece 2, which owns `SortPanel.cs`. This plan produces the `useScraped` parameter it feeds.
- **Not covered here, by design:** reproducing the crash and verifying the new matcher in-game against a full 20,115-name list. That is the release gate on enabling the toggle, and it is a decision, not a task.
