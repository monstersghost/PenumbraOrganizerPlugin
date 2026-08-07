# Piece 1: Two NPC Name Lists and an Index Matcher Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the per-category compiled alternation regex with a first-token index, ship a curated 827-name static list as the default, and demote the wiki scrape to an opt-in list that is off by default.

**Architecture:** `NpcNameMatcher` stops building `Regex` entirely. Names are normalized, tokenized, merged by category flags and bucketed by first token. The bundled static list is an embedded resource; the scraped list is a separate opt-in file in the config directory, and the two are unioned when opted in.

**Tech Stack:** C# / .NET 10, Dalamud plugin, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-07-two-list-npc-names-design.md`. Read it before Task 1 — particularly "What is NOT established".

## Global Constraints

- **This is not a proven crash fix and must not be described as one** in code comments, release notes, or commit messages. What is established: resetting the large list stops the observed crash, and the mechanism is unknown. Justify the work on correctness and classification quality.
- **No `Regex` in `NpcNameMatcher`.** Task 3's test proves only that no `Regex` is *stored in a field* — a method-local `new Regex(...)` would pass it. The real regression defence is the timing test beside it. Do not describe the architecture test as stronger than that.
- **`NpcNameMatch.Name` changes meaning.** The regex returned the text as it appeared in the mod title; the index returns the list's canonical spelling. Nothing consumes it today (`ModTypeClassifier` uses only `.Kind`), but the spec's entry-pruning argument turns on exactly this, so it must be recorded rather than discovered.
- **Case folding moves from culture-sensitive to ordinal.** `RegexOptions.IgnoreCase` without `CultureInvariant` used the current culture; the index uses `OrdinalIgnoreCase` throughout. This diverges for Turkish dotted/dotless I. It is an improvement, and it is a third behaviour change.
- **The epoch of behaviour change is deliberate and documented.** Two semantics change: Rune-based tokenization (fixes inconsistent splitting of non-BMP characters) and separator loosening (`Y'shtola` will also match `Y-shtola`). Both must be asserted by tests, not discovered.
- **Category precedence stays NPC, then Boss, then Enemy**, matching `Match` today. `SubCategoryFor` and every caller downstream are unchanged.
- **The scraped-list toggle ships disabled.** It is gated on reproducing the crash and verifying the new matcher in-game against a full 20,115-name list. That gate is a release decision, not a task here.
- Baseline: **912 tests passing**.

---

### Task 1: The index matcher, behind the existing public surface

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Classification/NpcNameMatcher.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/NpcNameMatcherTests.cs` — **this file already exists and holds 13 tests. APPEND to it. Do not create, do not overwrite.** Overwriting deletes regression guards including `Match_RegexMetacharactersInName_DoNotBreakMatching` and `Match_UnicodeBoundary_RejectsAdjacentLetterOrDigit`, which are exactly what Step 5's "no existing test may fail" gate relies on. All 13 pass under the new implementation.

**Interfaces:**
- Consumes: nothing.
- Produces: `NpcNameMatcher(IReadOnlyList<string> npcs, IReadOnlyList<string> enemies, IReadOnlyList<string> bosses)` and `NpcNameMatch? Match(string modName)` keep their exact current signatures, so `ScanProcessor` and `IndexProcessor` need no change in this task.

- [ ] **Step 1: Write the semantics tests, including the two deliberate changes**

Append these into the existing class in the existing file, which is already in
`namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification`. Add a `Make` helper only if
one is not already present.

```csharp
    private static NpcNameMatcher Make(string[]? npcs = null, string[]? enemies = null, string[]? bosses = null) =>
        new(npcs ?? [], enemies ?? [], bosses ?? []);

    [Fact]
    public void Match_IsCaseInsensitive()
        // The apostrophe matters: the list entry is "Y'shtola", so the mod title must contain one
        // too. "YSHTOLA" would fail against BOTH implementations - the old regex matches an escaped
        // literal, and the new tokenizer splits on the apostrophe so the bucket key is "Y".
        => Assert.NotNull(Make(npcs: ["Y'shtola"]).Match("Y'SHTOLA hair"));

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
    public void Match_PrefersMoreTokensOverFewer()
    {
        // Three tokens beats two at the same start position.
        var m = Make(npcs: ["Alka Zolka", "Alka Zolka Junior"]);
        Assert.Equal("Alka Zolka Junior", m.Match("Alka Zolka Junior Hair")!.Name);
    }

    [Fact]
    public void Match_CategoryOrderBeatsPosition()
    {
        // THE regression guard for this rewrite. The regex version ran three regexes in category
        // order, so an NPC anywhere beat a Boss anywhere. A position-first loop would answer Boss
        // here, and with 679 bosses against 133 NPCs in the shipped list that would silently
        // reclassify a lot of mods.
        var m = Make(npcs: ["Y'shtola"], bosses: ["Titan"]);
        Assert.Equal(NpcNameKind.Npc, m.Match("Titan Slaying Y'shtola")!.Kind);
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
    public void Match_DeliberateChange_NonBmpLettersAreLetters()
    {
        // NEW behaviour, and it is a TIGHTENING, not a loosening. An earlier draft of this plan
        // asserted the opposite using an emoji, which proves nothing: an emoji is not a letter or
        // digit under either implementation, so both treat it as a separator and both match.
        //
        // The only inputs where char-vs-Rune actually diverge are non-BMP characters that ARE
        // letters or digits, such as U+1D400 (MATHEMATICAL BOLD CAPITAL A). The old regex tests
        // each UTF-16 surrogate, neither of which is \p{L}, so it sees a boundary and matches.
        // Rune sees a letter, so the character joins the token and "Zenos" is no longer a whole
        // token. New behaviour is correct; old behaviour was an accident of UTF-16.
        var m = Make(npcs: ["Zenos"]);
        Assert.Null(m.Match("\U0001D400Zenos"));
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

Expected, precisely:

- The **13 existing tests** in this file pass, as they do today.
- Most new tests pass, because they describe behaviour the regex version already has.
- **`Match_DeliberateChange_SeparatorsAreInterchangeable` fails.** The old regex matches the escaped literal `Y'shtola` only.
- **`Match_DeliberateChange_NonBmpLettersAreLetters` fails.** The old regex sees two non-letter surrogates, finds a boundary, and matches; the assertion is `Assert.Null`.
- **`Match_CategoryOrderBeatsPosition` passes**, because the regex version is already category-first. It is a regression guard for the rewrite, not a new behaviour.

Record the actual output. If anything else fails, a test is wrong — stop and report rather than editing the implementation to suit it.

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

        // CATEGORY ORDER IS THE OUTER LOOP. This is not a style choice.
        //
        // The regex version ran three separate regexes in category order, so any NPC match
        // anywhere in the title beat any Boss match anywhere. Scanning token positions outermost
        // instead would make the earliest-positioned name win regardless of category:
        // "Titan Slaying Y'shtola" would classify as Boss rather than NPC. With 679 bosses
        // against 133 NPCs in the shipped list, boss tokens usually appear first, so that would
        // silently reclassify a large number of mods into different folders.
        foreach (var kind in (ReadOnlySpan<NpcNameKinds>)[NpcNameKinds.Npc, NpcNameKinds.Boss, NpcNameKinds.Enemy])
        {
            for (var start = 0; start < tokens.Length; start++)
            {
                if (!_byFirstToken.TryGetValue(tokens[start], out var candidates))
                    continue;

                foreach (var candidate in candidates)
                {
                    if (candidate.Kinds.HasFlag(kind) && MatchesAt(tokens, start, candidate.Tokens))
                        return new NpcNameMatch(candidate.Display, ToKind(kind));
                }
            }
        }

        return null;
    }

    private static NpcNameKind ToKind(NpcNameKinds kind) => kind switch
    {
        NpcNameKinds.Npc => NpcNameKind.Npc,
        NpcNameKinds.Boss => NpcNameKind.Boss,
        _ => NpcNameKind.Enemy,
    };

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
        // 25,000 names, above the 20,115 a full scrape produces.
        //
        // The FIRST token must vary. "Synthetic Name {i}" puts all 25,000 into a single bucket,
        // which is the exact opposite of the real distribution (9,886 buckets, median 1, max 185)
        // and turns every Match into a linear scan of 25,000 candidates. That measures nothing
        // useful and would likely blow this test's own budget.
        var names = Enumerable.Range(0, 25_000).Select(i => $"Synth{i} Name {i}").ToArray();

        var sw = Stopwatch.StartNew();
        var matcher = new NpcNameMatcher(names, [], []);
        var built = sw.ElapsedMilliseconds;

        sw.Restart();
        for (var i = 0; i < 2_000; i++)
            matcher.Match($"Some Mod About Synth{i} Name {i} And Things");
        var matched = sw.ElapsedMilliseconds;

        Assert.True(built < 2_000, $"build took {built}ms");
        Assert.True(matched < 2_000, $"2,000 matches took {matched}ms");
    }

    [Fact]
    public void NpcNameMatcher_StoresNoRegexState()
    {
        // Named for what it actually proves. It inspects FIELD TYPES, so it catches a stored
        // Regex - the shape that caused the original problem - but a method-local `new Regex(...)`
        // or a static `Regex.IsMatch` call would pass. The real defence against a return to
        // pattern matching at scale is the timing test above, not this.
        var referenced = typeof(NpcNameMatcher)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
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

**`public`, not `internal`.** There is no `InternalsVisibleTo` anywhere in this repo — verified — so
an `internal` accessor is unreachable from the test project and Step 2's test will not compile. The
neighbouring `ReadEmbeddedNpcNameSeed` is `internal` and has no test, which is why nobody has hit
this before. Do not add `InternalsVisibleTo` as a side effect of this task.

```csharp
public static string ReadEmbeddedStaticNpcNameList()
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
- Modify: `PenumbraOrganizer.Plugin/LibraryWork/ScanJob.cs`, `IndexJob.cs` — they construct the processors and must pass the new arguments
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs` — the `MigrateLegacyList` call site, and `NpcNameListPath` if it becomes unused
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/NpcNames/NpcNameListStoreTests.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/NpcNames/NpcNameRefreshServiceTests.cs`

An earlier draft's prose described changes to `ScanJob`, `IndexJob` and `Plugin.cs` while omitting
all three from this list and from the `git add`. The plan would have described edits its own commit
excluded.

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

`LoadForMatching(string configDir, bool useScraped)` parses the embedded static list, and when
`useScraped` is true additionally parses `npc-name-list-scraped.json` and unions the three name
collections.

**Every cell of the matrix is defined. Do not leave any to judgement:**

| Scraped file state | `useScraped` | Behaviour |
|---|---|---|
| any | `false` | static only; the scraped file is not opened, not created, not touched |
| missing | `true` | static only, no warning. **Do not write a seed file** — `Load`'s existing missing-file branch does exactly that, which would create the file the migration table assumes is absent |
| `Ok`, within cap | `true` | union of static and scraped |
| `Ok`, over cap | `true` | **warn only, do not rewrite** — see below |
| `MalformedJson` | `true` | static only, warning naming the file |
| `UnsupportedVersion` | `true` | static only, warning naming the file |

**The oversize guard must NOT be reused as-is.** `MaxSafeNameCount` is 2,000 and the scraped list
this feature exists to load is ~20,115 names, so the existing path would trip on every single
opted-in load — and it does not merely warn: it copies the file to `.oversized-<timestamp>.json`
and **overwrites the original with the seed** (`NpcNameListStore.cs:51-54`). Reusing it would
silently destroy the file the refresh just wrote, every time, for a list the user deliberately
enabled.

For the opted-in path, add a separate, higher ceiling (25,000, above a full scrape) and make
exceeding it **warn and fall back in memory only**, writing nothing. The 2,000 guard stays exactly
as it is on the legacy `Load` path, which 0.5.3.1 relies on.

**`Excluded` applies to the scraped names only**, not to the static list. The static list is
curated and the user did not choose its contents through the refresh UI; letting a stale exclusion
silently remove `Shiva` (present in both `Enemies` and `Bosses`) from the bundled list would be
surprising. State this in a comment.

**Union duplicates are harmless.** `Sanitize` de-dups within an array but not across, so a name in
static-NPCs and scraped-Bosses appears twice; the matcher merges them into one entry with both
flags. No de-dup pass is needed.

**Two warnings can occur at once** (migration failed, and the scraped file is corrupt). Join them
with a space into the single `Warning` slot rather than dropping one.

`Configuration` gains `public bool UseScrapedNpcNameList { get; set; }` — default `false`.

`ScanProcessor` and `IndexProcessor` change their `Prepare` to call `LoadForMatching(configDir, useScraped)`; both take the two values through their constructors, which `ScanJob` and `IndexJob` supply from `Plugin.Config` on the framework thread.

**`MigrateLegacyList` needs a production call site, and this is the step that gives it one.** Without
it every migration test in Step 1 can pass while the plugin never migrates anything. Call it once
during plugin construction, after the config directory is known and **before any library work can be
admitted**, so no scan or index build can read the lists first:

```csharp
// In Plugin's constructor, near the other config-directory setup.
var migrationWarning = Organizer.NpcNames.NpcNameListStore.MigrateLegacyList(
    PluginInterface.ConfigDirectory.FullName);
if (migrationWarning is not null)
    Log.Warning(migrationWarning);
```

`MigrateLegacyList` returns `string?` so a failed rename surfaces without throwing during plugin
construction, which would take the whole plugin down over a diagnostic concern.

**The scraped-list opt-in is gated in the backend, not only in the UI.** Add to `Configuration`:

```csharp
public bool UseScrapedNpcNameList { get; set; }
```

and, alongside it, a compile-time feature gate:

```csharp
// 0.6.0 ships with the scraped list unavailable: the crash whose correlation motivated this work
// has not been reproduced, so nothing may load a 20,000-name list yet. Flipping this to true is a
// deliberate release decision, made after that verification, not a config edit.
internal const bool ScrapedNpcListFeatureEnabled = false;
```

Every consumer reads the **conjunction**, never the config value alone:

```csharp
var useScraped = Configuration.ScrapedNpcListFeatureEnabled && Config.UseScrapedNpcNameList;
```

This matters because greying out a checkbox does not enforce anything. A `true` left in config by
testing or hand-editing would otherwise load the full list while the UI claims the feature is off,
which breaks the one guarantee this release makes.

- [ ] **Step 3: Refresh writes a snapshot, not a merge**

In `NpcNameRefreshService`, replace the `MergeAdditive` call so the scraped file is **this run's results minus exclusions**, with no reference to the previous file's names. That is the change that removes unbounded growth. Write to `npc-name-list-scraped.json`, never to `npc-name-list.json`.

Add:

```csharp
[Fact]
public async Task Refresh_ReplacesRatherThanGrowing()
{
    // The unbounded growth this whole piece exists to stop. Two refreshes returning identical
    // wiki data must leave the file the same size, not double it.
    var dir = MakeTempDir();
    var path = Path.Combine(dir, "npc-name-list-scraped.json");
    var service = MakeServiceReturning(npcs: ["Alpha", "Beta"], enemies: [], bosses: []);

    await service.RefreshAsync(path, SeedJson, CancellationToken.None);
    var afterFirst = NpcNameListCodec.Parse(File.ReadAllText(path)).Data!;

    await service.RefreshAsync(path, SeedJson, CancellationToken.None);
    var afterSecond = NpcNameListCodec.Parse(File.ReadAllText(path)).Data!;

    Assert.Equal(afterFirst.NPCs.Count, afterSecond.NPCs.Count);
    Assert.Equal(["Alpha", "Beta"], afterSecond.NPCs.Order());
}

[Fact]
public async Task Refresh_DropsNamesTheWikiNoLongerReturns()
{
    // The other half of snapshot semantics, and the direction MergeAdditive could never do.
    var dir = MakeTempDir();
    var path = Path.Combine(dir, "npc-name-list-scraped.json");

    await MakeServiceReturning(npcs: ["Alpha", "Beta"], [], []).RefreshAsync(path, SeedJson, CancellationToken.None);
    await MakeServiceReturning(npcs: ["Alpha"], [], []).RefreshAsync(path, SeedJson, CancellationToken.None);

    Assert.Equal(["Alpha"], NpcNameListCodec.Parse(File.ReadAllText(path)).Data!.NPCs);
}
```

using the fake scraper the existing `NpcNameRefreshServiceTests` already provides.

**Three existing behaviours change with snapshot semantics and each needs handling here, not
discovering later:**

- **`RefreshAsync_NeverRemovesExistingNames` (`NpcNameRefreshServiceTests.cs:101`) exists to pin
  `MergeAdditive` and MUST now fail.** Delete it and say so in the report — it pins behaviour this
  task deliberately removes. This is the one existing-test failure that is expected; anything else
  failing means something is wrong.
- **`AddedCount` (`NpcNameRefreshService.cs:44-46`)** is computed as merged-minus-existing per
  category and becomes meaningless or negative under snapshots. Change it to report the count in
  the new snapshot, and update the UI text that shows it from "+N" to a plain total.
- **`LoadForRefresh` (`NpcNameRefreshService.cs:57-67`)** falls back to the **seed document** on a
  missing or corrupt file. Under snapshot semantics that would inject bundled seed names into the
  scraped file. It must fall back to an **empty document** instead: a refresh's output is the wiki's
  contents, nothing else. `Excluded` is carried forward from the previous scraped file if one
  parsed, and is otherwise empty.

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
