# Race-Priority Hair Sorting Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the Sort tab file a hair mod under the hair number its chosen race uses, with a Midlander fallback, when Split hair is on.

**Architecture:** The reader gains per-race evidence in the same single walk that already produces the flat set. The scan stores both sets on each hair row, so the choice of priority is applied at Sort time, not scan time. A pure resolver applies the spec's steps 1-5. Sort takes an optional priority, and with no priority its output is byte-identical to 0.6.1.0.

**Tech Stack:** C# / .NET 10, Dalamud.NET.Sdk 15.0.0, Dalamud.Bindings.ImGui, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-01-race-priority-hair-design.md` (revision 3).

## Global Constraints

- The six-step order is exactly the spec's: step 1 priority race with exactly one number; step 2 priority absent, fall through; step 3 detected set has one number; step 4 different numbers across races with the Midlander fallback on and the Midlander rule yielding one number; step 5 flat `Hair`. Step 6 of the spec is covered by step 5 here.
- **Open question 1 of the spec (the tiebreak for two numbers in one race) is NOT implemented.** Two numbers within one race fall through to detection, which then stays flat. Do not add a tiebreak.
- Race codes are the 18 confirmed in the spec's table. Names are the Penumbra race names: `Midlander`, `Highlander`, `Elezen`, `Miqo'te`, `Roegadyn`, `Lalafell`, `Au Ra`, `Hrothgar`, `Viera`. Gender names are `Male` and `Female`.
- The EST path is unchanged: EST `SetId` on slot Hair still feeds the **flat** set only. It never feeds the per-race map, because an EST entry carries a race but the reader does not read it. Document this in the reader's remarks.
- Every non-ASCII symbol is an escape: `♂` (male), `♀` (female). Never paste a glyph into C#, JSON or this plan's code.
- Session state only: the priority and the fallback are plain panel fields, not persisted, like `_splitHair`.
- The feature is governed by the Split hair checkbox. With it off, neither the priority nor the fallback has any effect.
- Build and test commands, from the repository root:
  - `dotnet build PenumbraOrganizer.Plugin.sln`
  - `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj`
- **Base branch:** `SortPanel.cs` changes depend on `feat/sort-layout-and-help` (commit `19f1dc8`), which is not on main. Task 6 starts by rebasing this branch onto `feat/sort-layout-and-help`. Do not rebase before Task 6, so Tasks 1-5 stay independent of the UI branch.
- Commit trailers: `Co-Authored-By: Claude Haiku 5.5 <noreply@anthropic.com>` on every commit this plan produces.

## File Structure

| File | Responsibility |
|---|---|
| `PenumbraOrganizer.Plugin/Organizer/Classification/HairRaceCodes.cs` | **Create.** The 18 race/gender codes, one place. |
| `PenumbraOrganizer.Plugin/Organizer/Classification/HairPriorityResolver.cs` | **Create.** `HairPriority` record and the pure resolver for steps 1-5. |
| `PenumbraOrganizer.Plugin/Organizer/Classification/ModHairFileReader.cs` | **Modify.** Adds `ReadHairEvidence` (one walk, both sets). `ReadHairNumbers` stays and delegates to it. |
| `PenumbraOrganizer.Plugin/Organizer/OrganizerModRow.cs` | **Modify.** Two new init-only properties. |
| `PenumbraOrganizer.Plugin/LibraryWork/Pure/ScanProcessor.cs` | **Modify.** Stores the evidence on hair rows. |
| `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs` | **Modify.** `Sort` takes an optional `HairPriority`. |
| `PenumbraOrganizer.Plugin/Windows/SortPanel.cs` | **Modify.** `SortSelection` gains two fields; the Hair group gains two controls. |
| `PenumbraOrganizer.Plugin/Resources/help-content.json`, `Windows/HelpTopics.cs`, `Windows/HelpTab.cs`, `Windows/HelpTopicUsage.cs` | **Modify.** Two control topics, registered in the same four places as `sort.split-hair`. |

---

### Task 1: Race code table

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Classification/HairRaceCodes.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/HairRaceCodesTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `HairRaceCodes.All` (`IReadOnlyList<HairRace>`), `HairRaceCodes.TryGetByCode(string code, out HairRace race)`, `HairRaceCodes.Midlander.Male` / `Midlander.Female` as code constants, and `HairRace(string Code, string Race, string Gender)`.

- [ ] **Step 1: Write the failing test**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/HairRaceCodesTests.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer.Classification;
using Xunit;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class HairRaceCodesTests
{
    [Theory]
    [InlineData("0101", "Midlander", "Male")]
    [InlineData("0201", "Midlander", "Female")]
    [InlineData("0301", "Highlander", "Male")]
    [InlineData("0401", "Highlander", "Female")]
    [InlineData("0501", "Elezen", "Male")]
    [InlineData("0601", "Elezen", "Female")]
    [InlineData("0701", "Miqo'te", "Male")]
    [InlineData("0801", "Miqo'te", "Female")]
    [InlineData("0901", "Roegadyn", "Male")]
    [InlineData("1001", "Roegadyn", "Female")]
    [InlineData("1101", "Lalafell", "Male")]
    [InlineData("1201", "Lalafell", "Female")]
    [InlineData("1301", "Au Ra", "Male")]
    [InlineData("1401", "Au Ra", "Female")]
    [InlineData("1501", "Hrothgar", "Male")]
    [InlineData("1601", "Hrothgar", "Female")]
    [InlineData("1701", "Viera", "Male")]
    [InlineData("1801", "Viera", "Female")]
    public void EveryConfirmedCode_MapsToItsRaceAndGender(string code, string race, string gender)
    {
        // One row per confirmed code. A wrong row here is a wrong folder for a real user, so each
        // entry is pinned on its own rather than by counting the table.
        Assert.True(HairRaceCodes.TryGetByCode(code, out var found));
        Assert.Equal(new HairRace(code, race, gender), found);
    }

    [Fact]
    public void TheTableHasExactlyEighteenEntries()
    {
        Assert.Equal(18, HairRaceCodes.All.Count);
    }

    [Theory]
    [InlineData("0000")]
    [InlineData("1102")]
    [InlineData("9999")]
    public void AnUnknownCode_IsNotFound(string code)
    {
        Assert.False(HairRaceCodes.TryGetByCode(code, out _));
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~HairRaceCodesTests"`
Expected: build FAILS with `CS0246` for `HairRaceCodes` and `HairRace`.

- [ ] **Step 3: Write the implementation**

Create `PenumbraOrganizer.Plugin/Organizer/Classification/HairRaceCodes.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>One race and gender folder code, as Penumbra names them.</summary>
public sealed record HairRace(string Code, string Race, string Gender);

/// <summary>
/// The eighteen race and gender folder codes a hair mod can ship under, from the mods in the test set.
/// </summary>
/// <remarks>
/// The race names are Penumbra's own key names, so a priority chosen in the dropdown matches the
/// same names the changed-item keys use. Every code here is confirmed by at least one mod; see the
/// race-priority spec. Adding a code means adding a test row for it.
/// </remarks>
public static class HairRaceCodes
{
    public static readonly IReadOnlyList<HairRace> All =
    [
        new("0101", "Midlander", "Male"),
        new("0201", "Midlander", "Female"),
        new("0301", "Highlander", "Male"),
        new("0401", "Highlander", "Female"),
        new("0501", "Elezen", "Male"),
        new("0601", "Elezen", "Female"),
        new("0701", "Miqo'te", "Male"),
        new("0801", "Miqo'te", "Female"),
        new("0901", "Roegadyn", "Male"),
        new("1001", "Roegadyn", "Female"),
        new("1101", "Lalafell", "Male"),
        new("1201", "Lalafell", "Female"),
        new("1301", "Au Ra", "Male"),
        new("1401", "Au Ra", "Female"),
        new("1501", "Hrothgar", "Male"),
        new("1601", "Hrothgar", "Female"),
        new("1701", "Viera", "Male"),
        new("1801", "Viera", "Female"),
    ];

    /// <summary>The Midlander folder codes, used by the Midlander fallback.</summary>
    public const string MidlanderMale = "0101";
    public const string MidlanderFemale = "0201";

    public static bool TryGetByCode(string code, out HairRace race)
    {
        foreach (var entry in All)
        {
            if (entry.Code == code)
            {
                race = entry;
                return true;
            }
        }

        race = null!;
        return false;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~HairRaceCodesTests"`
Expected: PASS, 21 tests (18 theory rows, 1 count, 3 unknown rows counted as cases).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Classification/HairRaceCodes.cs PenumbraOrganizer.Plugin.Tests/Organizer/Classification/HairRaceCodesTests.cs
git commit -m "feat: add the eighteen confirmed hair race codes" -m "Co-Authored-By: Claude Haiku 5.5 <noreply@anthropic.com>"
```

---

### Task 2: One-walk hair evidence with per-race sets

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Classification/ModHairFileReader.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModHairFileReaderTests.cs` (extend the existing 26-case file; read it first and follow its fixture style)

**Interfaces:**
- Consumes: `ModConfigFileWalker.TryWalk` (already used).
- Produces: `ModHairFileReader.ReadHairEvidence(DirectoryInfo) : HairEvidence?` where `public sealed record HairEvidence(IReadOnlySet<int> Flat, IReadOnlyDictionary<string, IReadOnlySet<int>> ByRaceCode)`. `ReadHairNumbers` returns `ReadHairEvidence(dir)?.Flat` and keeps its 0.6.1.0 contract, including `null` on a failed read.

- [ ] **Step 1: Write the failing tests**

Append to `ModHairFileReaderTests.cs`, inside the class. These use the reader's file-key path so they need no disk fixtures; if the existing file builds fixtures a different way, match that way:

```csharp
    [Fact]
    public void ReadHairEvidence_KeysEachFileSetByItsRaceCode()
    {
        // Two race folders, one with two numbers: c0201 has 147 and 169, c1801 has 3.
        var evidence = ReadEvidenceFromKeys(
            "chara/human/c0201/obj/hair/h0147/texture/a.tex",
            "chara/human/c0201/obj/hair/h0169/texture/b.tex",
            "chara/human/c1801/obj/hair/h0003/model/c.mdl");

        Assert.Equal(new HashSet<int> { 147, 169 }, evidence!.ByRaceCode["0201"].ToHashSet());
        Assert.Equal(new HashSet<int> { 3 }, evidence.ByRaceCode["1801"].ToHashSet());
        Assert.Equal(new HashSet<int> { 3, 147, 169 }, evidence.Flat.ToHashSet());
    }

    [Fact]
    public void ReadHairEvidence_FlatSetIsTheUnionOfTheRaceSets()
    {
        var evidence = ReadEvidenceFromKeys(
            "chara/human/c0201/obj/hair/h0115/model/a.mdl",
            "chara/human/c0801/obj/hair/h0115/model/b.mdl");

        Assert.Equal(new HashSet<int> { 115 }, evidence!.Flat.ToHashSet());
        Assert.Equal(new HashSet<int> { 115 }, evidence.ByRaceCode["0801"].ToHashSet());
    }

    [Fact]
    public void ReadHairEvidence_EstEntriesFeedTheFlatSetOnly()
    {
        // An EST entry names a race in the metadata but the reader does not read that race, so its
        // SetId must never appear under any race code.
        var evidence = ReadEvidenceFromKeysAndManipulation(
            manipulationSetId: 115,
            fileKeys: "chara/human/c0201/obj/hair/h0147/model/a.mdl");

        Assert.Equal(new HashSet<int> { 115, 147 }, evidence!.Flat.ToHashSet());
        Assert.False(evidence.ByRaceCode.Values.Any(s => s.Contains(115)));
    }
```

Add these two helpers to the same test class. They call the same private methods the production reader uses, through the walker's callbacks, so they test the real collection code:

```csharp
    private static HairEvidence? ReadEvidenceFromKeys(params string[] fileKeys)
    {
        var numbers = new HashSet<int>();
        var byRace = new Dictionary<string, HashSet<int>>();
        foreach (var key in fileKeys)
            ModHairFileReader.CollectForTest(key, numbers, byRace);
        return ModHairFileReader.BuildEvidenceForTest(numbers, byRace);
    }

    private static HairEvidence? ReadEvidenceFromKeysAndManipulation(int manipulationSetId, params string[] fileKeys)
    {
        var json = System.Text.Json.JsonDocument.Parse(
            $"{{\"Type\":\"Est\",\"Manipulation\":{{\"Slot\":\"Hair\",\"SetId\":{manipulationSetId}}}}}").RootElement;
        var numbers = new HashSet<int>();
        var byRace = new Dictionary<string, HashSet<int>>();
        foreach (var key in fileKeys)
            ModHairFileReader.CollectForTest(key, numbers, byRace);
        ModHairFileReader.CollectManipulationForTest(json, numbers);
        return ModHairFileReader.BuildEvidenceForTest(numbers, byRace);
    }
```

Note: the `*ForTest` members are `internal` in the production class, and this repo has no `InternalsVisibleTo`. Use the public seam instead, which is already the reader's contract: write the fixtures as a real directory under the test's temp folder, then call `ReadHairEvidence(new DirectoryInfo(dir))`. If the existing 26 tests already do that, use their helper and drop the `ForTest` helpers entirely. Read the file before writing these.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~ModHairFileReaderTests"`
Expected: build FAILS with `CS1061` for `ReadHairEvidence`.

- [ ] **Step 3: Write the implementation**

In `ModHairFileReader.cs`, add the record and the new method, and make `ReadHairNumbers` delegate. Replace the whole public `ReadHairNumbers` method and the `HairPath` regex with:

```csharp
    // Capture the race code as well as the hair number, so the per-race evidence can be keyed by
    // folder code. The race code is still verified by the same pattern; it is only kept, not dropped.
    private static readonly Regex HairPath = new(
        @"^chara/human/c([0-9]{4})/obj/hair/h([0-9]{4})/",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The flat set and the per-race sets, read in one walk of the mod.</summary>
    /// <remarks>
    /// <c>Flat</c> is every supported hair number from file paths and EST entries. <c>ByRaceCode</c>
    /// holds only the file paths, keyed by the four-digit race folder code. An EST entry names a race
    /// in the metadata, but this reader does not use that race, so an EST <c>SetId</c> never appears
    /// under any code. That is deliberate and matches the race-priority spec.
    /// </remarks>
    public sealed record HairEvidence(
        IReadOnlySet<int> Flat,
        IReadOnlyDictionary<string, IReadOnlySet<int>> ByRaceCode);

    /// <summary>
    /// Reads the mod's hair evidence in one walk. Null means the walk could not be trusted, the same
    /// meaning <see cref="ReadHairNumbers"/> has always had.
    /// </summary>
    public static HairEvidence? ReadHairEvidence(DirectoryInfo modDirectory)
    {
        var flat = new HashSet<int>();
        var byRace = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);

        var ok = ModConfigFileWalker.TryWalk(
            modDirectory,
            path => CollectFromFileKey(path, flat, byRace),
            item => CollectFromManipulation(item, flat));

        if (!ok)
            return null;

        var frozen = byRace.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlySet<int>)pair.Value,
            StringComparer.Ordinal);
        return new HairEvidence(flat, frozen);
    }

    /// <summary>The flat set only, for callers that do not need the race split.</summary>
    public static IReadOnlySet<int>? ReadHairNumbers(DirectoryInfo modDirectory) =>
        ReadHairEvidence(modDirectory)?.Flat;

    private static void CollectFromFileKey(
        string path, HashSet<int> flat, Dictionary<string, HashSet<int>> byRace)
    {
        // Already '/'-normalised by the walker.
        var match = HairPath.Match(path);
        if (!match.Success)
            return;

        // TryParse, not Parse: four ASCII digits cannot overflow an int, but a defensive parse
        // costs nothing and survives someone widening the pattern later.
        if (!int.TryParse(match.Groups[2].ValueSpan, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var number))
            return;
        if (number is < MinHairNumber or > MaxHairNumber)
            return;

        var raceCode = match.Groups[1].Value;
        flat.Add(number);
        if (!byRace.TryGetValue(raceCode, out var set))
            byRace[raceCode] = set = [];
        set.Add(number);
    }
```

Then update `CollectFromManipulation` so it takes only the flat set: change its signature to `private static void CollectFromManipulation(JsonElement item, HashSet<int> flat)` and rename its `numbers` uses to `flat`. Keep `AddIfSupported` for the EST path; rewrite `AddIfSupported` to take `HashSet<int> flat` and remove the per-race duplicate range check from `CollectFromFileKey` so the range guard stays in one place:

```csharp
    // The ONE place the supported range is enforced for EST evidence. File-path evidence is checked
    // by the same bounds in CollectFromFileKey, which is the one other producer.
    private static void AddIfSupported(int number, HashSet<int> flat)
    {
        if (number is >= MinHairNumber and <= MaxHairNumber)
            flat.Add(number);
    }
```

Then in `CollectFromFileKey` remove the explicit range check and the Flat add's extra guard, and call `AddIfSupported(number, flat)` for the flat set, adding to the race set only when in range:

```csharp
        if (!int.TryParse(match.Groups[2].ValueSpan, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var number))
            return;
        if (number is < MinHairNumber or > MaxHairNumber)
            return;

        var raceCode = match.Groups[1].Value;
        flat.Add(number);
        if (!byRace.TryGetValue(raceCode, out var set))
            byRace[raceCode] = set = [];
        set.Add(number);
```

(The explicit range check here is the file-path guard; `AddIfSupported` is the EST guard. Both bounds are `MinHairNumber`/`MaxHairNumber`, so there is still one definition of the range.)

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~ModHairFileReaderTests"`
Expected: PASS, the 26 existing cases plus the 3 new ones. Then run the whole suite once to confirm `ReadHairNumbers`' callers are unchanged:
`dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj`
Expected: all pass, 1111 + the new tests from Task 1 and this task.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Classification/ModHairFileReader.cs PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModHairFileReaderTests.cs
git commit -m "feat: read hair evidence per race folder in one walk" -m "Co-Authored-By: Claude Haiku 5.5 <noreply@anthropic.com>"
```

---

### Task 3: The priority resolver (steps 1-5)

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Classification/HairPriorityResolver.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/HairPriorityResolverTests.cs`

**Interfaces:**
- Consumes: `HairEvidence` (Task 2), `HairRaceCodes` (Task 1).
- Produces: `public readonly record struct HairPriority(string? RaceCode, bool MidlanderFallback)` (a `null` RaceCode means None) and `HairPriorityResolver.Resolve(IReadOnlySet<int>? flat, IReadOnlyDictionary<string, IReadOnlySet<int>>? byRace, HairPriority priority) : int?`. Returns the number to file under, or null for flat `Hair`.

- [ ] **Step 1: Write the failing tests**

Create `HairPriorityResolverTests.cs`. Each row is one line of the spec's worked-results table, using the real Zutail and Devilry shapes:

```csharp
using PenumbraOrganizer.Plugin.Organizer.Classification;
using Xunit;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class HairPriorityResolverTests
{
    private static IReadOnlySet<int> Set(params int[] numbers) => numbers.ToHashSet();

    private static IReadOnlyDictionary<string, IReadOnlySet<int>> Races(params (string Code, int[] Numbers)[] races) =>
        races.ToDictionary(r => r.Code, r => Set(r.Numbers), StringComparer.Ordinal);

    private static readonly HairPriority None = new(null, false);
    private static readonly HairPriority Fallback = new(null, true);

    // Devilry Hair With Bow: 108 for most races, Viera female uses 3.
    private static readonly IReadOnlySet<int> DevilryFlat = Set(3, 108);
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<int>> DevilryByRace =
        Races(("0201", [108]), ("0401", [108]), ("0801", [108]), ("1401", [108]), ("1801", [3]));

    // Zutail: female Midlander has 147 and 169; most other races have 169 only.
    private static readonly IReadOnlySet<int> ZutailFlat = Set(147, 169);
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<int>> ZutailByRace =
        Races(("0201", [147, 169]), ("0401", [169]), ("0601", [169]), ("0801", [169]),
              ("1001", [169]), ("1201", [169]), ("1401", [147, 169]));

    [Fact]
    public void Step1_PriorityWithOneNumber_FilesUnderIt()
    {
        // Devilry, priority Viera female: c1801 has exactly one number, 3.
        Assert.Equal(3, HairPriorityResolver.Resolve(DevilryFlat, DevilryByRace, new("1801", false)));
    }

    [Fact]
    public void Step1_PriorityWithOneNumber_IgnoresTheOtherRaces()
    {
        // Devilry, priority Midlander female: c0201 has one number, 108, even though the mod has two.
        Assert.Equal(108, HairPriorityResolver.Resolve(DevilryFlat, DevilryByRace, new("0201", false)));
    }

    [Fact]
    public void Step2_PriorityAbsent_FallsThroughToDetection()
    {
        // Zutail, priority Viera female: no c1801 folder, so detection gives 147 and 169, flat.
        Assert.Null(HairPriorityResolver.Resolve(ZutailFlat, ZutailByRace, new("1801", false)));
    }

    [Fact]
    public void Step2_PriorityWithTwoNumbers_FallsThroughToDetection()
    {
        // Zutail, priority Au Ra female: c1401 has 147 and 169, not exactly one, so flat.
        Assert.Null(HairPriorityResolver.Resolve(ZutailFlat, ZutailByRace, new("1401", false)));
    }

    [Fact]
    public void Step3_NoPriority_OneDetectedNumber_FilesUnderIt()
    {
        // Devilry has two numbers, so use a single-number mod for this row.
        var flat = Set(115);
        var byRace = Races(("0201", [115]), ("0801", [115]));
        Assert.Equal(115, HairPriorityResolver.Resolve(flat, byRace, None));
    }

    [Fact]
    public void Step5_NoPriority_TwoDetectedNumbers_StaysFlat()
    {
        Assert.Null(HairPriorityResolver.Resolve(DevilryFlat, DevilryByRace, None));
    }

    [Fact]
    public void Step4_MidlanderFallback_FemaleOnlyWithOneNumber_Resolves()
    {
        // Two detected numbers, no priority, only Midlander female present with one number.
        var flat = Set(108, 3);
        var byRace = Races(("0201", [108]), ("1801", [3]));
        Assert.Equal(108, HairPriorityResolver.Resolve(flat, byRace, Fallback));
    }

    [Fact]
    public void Step5_MidlanderFallbackOff_TwoDetectedNumbers_StaysFlat()
    {
        var flat = Set(108, 3);
        var byRace = Races(("0201", [108]), ("1801", [3]));
        Assert.Null(HairPriorityResolver.Resolve(flat, byRace, None));
    }

    [Fact]
    public void Midlander_BothGendersSameSingleNumber_Resolves()
    {
        var flat = Set(111, 3);
        var byRace = Races(("0101", [111]), ("0201", [111]), ("1801", [3]));
        Assert.Equal(111, HairPriorityResolver.Resolve(flat, byRace, Fallback));
    }

    [Fact]
    public void Midlander_BothGendersDifferent_StaysFlat()
    {
        // Lumi's Hairstyle V2 shape: male 111, female 110.
        var flat = Set(110, 111);
        var byRace = Races(("0101", [111]), ("0201", [110]));
        Assert.Null(HairPriorityResolver.Resolve(flat, byRace, Fallback));
    }

    [Fact]
    public void Midlander_FemaleOnlyWithTwoNumbers_StaysFlat()
    {
        // Zutail's Midlander female shape: 147 and 169, no male Midlander.
        Assert.Null(HairPriorityResolver.Resolve(ZutailFlat, ZutailByRace, Fallback));
    }

    [Fact]
    public void Midlander_NoMidlanderEntries_FallbackDoesNotApply()
    {
        var flat = Set(108, 3);
        var byRace = Races(("0401", [108]), ("1801", [3]));
        Assert.Null(HairPriorityResolver.Resolve(flat, byRace, Fallback));
    }

    [Fact]
    public void Rule6_TwoNumbersWithinOneRace_NeverPicksOne()
    {
        // Sampo's Midlander male shape: four numbers in c0101. Priority on it is "not exactly one",
        // and with no other detection evidence the answer is flat, not an arbitrary member.
        var flat = Set(122, 162, 163, 164);
        var byRace = Races(("0101", [122, 162, 163, 164]));
        Assert.Null(HairPriorityResolver.Resolve(flat, byRace, new("0101", true)));
    }

    [Fact]
    public void UnreadableEvidence_IsNeverGuessedFrom()
    {
        Assert.Null(HairPriorityResolver.Resolve(null, null, new("1801", true)));
    }

    [Fact]
    public void Zero_DetectedNumbers_StaysFlat()
    {
        Assert.Null(HairPriorityResolver.Resolve(Set(), Races(), new("1801", true)));
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~HairPriorityResolverTests"`
Expected: build FAILS with `CS0246` for `HairPriorityResolver` and `HairPriority`.

- [ ] **Step 3: Write the implementation**

Create `PenumbraOrganizer.Plugin/Organizer/Classification/HairPriorityResolver.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>
/// The Sort tab's hair priority. A null <see cref="RaceCode"/> is None.
/// </summary>
public readonly record struct HairPriority(string? RaceCode, bool MidlanderFallback);

/// <summary>
/// Resolves the hair number a hair mod files under, from the race-priority spec's steps 1-5.
/// </summary>
/// <remarks>
/// Pure: no disk, no state. Null in means "no trustworthy evidence", and the answer is then null, the
/// same flat <c>Hair</c> as 0.6.1.0. Two numbers within one race never produce an arbitrary pick: such a
/// race is "not exactly one", so the rule falls through.
/// </remarks>
public static class HairPriorityResolver
{
    /// <summary>The number to file under, or null for flat <c>Hair</c>.</summary>
    public static int? Resolve(
        IReadOnlySet<int>? flat,
        IReadOnlyDictionary<string, IReadOnlySet<int>>? byRace,
        HairPriority priority)
    {
        if (flat is null || byRace is null)
            return null;

        // Step 1: the priority race has exactly one number. Other races are ignored.
        if (priority.RaceCode is { } code
            && byRace.TryGetValue(code, out var chosen)
            && chosen.Count == 1)
            return chosen.Single();

        // Step 2 is the fall-through: a priority that is absent or ambiguous reaches here unchanged.

        // Step 3: the mod's detected set has one number.
        if (flat.Count == 1)
            return flat.Single();

        // Step 4: different numbers across races, and the Midlander rule yields one.
        if (flat.Count > 1 && priority.MidlanderFallback)
            return MidlanderNumber(byRace);

        // Step 5: no single number. Flat.
        return null;
    }

    // Both genders present and agreeing on one number: that number. Both present and disagreeing:
    // no answer, since picking one gender would be arbitrary. One gender present with exactly one
    // number: that number. Anything else, including no Midlander entries at all: no answer.
    private static int? MidlanderNumber(IReadOnlyDictionary<string, IReadOnlySet<int>> byRace)
    {
        var hasMale = byRace.TryGetValue(HairRaceCodes.MidlanderMale, out var male);
        var hasFemale = byRace.TryGetValue(HairRaceCodes.MidlanderFemale, out var female);

        if (hasMale && hasFemale)
        {
            if (male!.Count == 1 && female!.Count == 1 && male.Single() == female.Single())
                return male.Single();
            return null;
        }

        if (hasMale)
            return male!.Count == 1 ? male.Single() : null;
        if (hasFemale)
            return female!.Count == 1 ? female.Single() : null;
        return null;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~HairPriorityResolverTests"`
Expected: PASS, 15 tests.

- [ ] **Step 5: Mutation-check the guards**

Each guard must fail a test when broken. Break one at a time, run the filter, and restore before the next:

1. In `Resolve`, change `chosen.Count == 1` to `chosen.Count >= 1`. Expected: `Rule6_TwoNumbersWithinOneRace_NeverPicksOne` FAILS (it would return `chosen.Single()` on a set of four).
2. In `Resolve`, delete `&& priority.MidlanderFallback`. Expected: `Step5_MidlanderFallbackOff_TwoDetectedNumbers_StaysFlat` FAILS.
3. In `MidlanderNumber`, change `male.Single() == female.Single()` to `true`. Expected: `Midlander_BothGendersDifferent_StaysFlat` FAILS.

Record each result in the commit message body. A guard whose break passes every test is not done: write the test that pins it before committing.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Classification/HairPriorityResolver.cs PenumbraOrganizer.Plugin.Tests/Organizer/Classification/HairPriorityResolverTests.cs
git commit -m "feat: resolve hair number from race priority and Midlander fallback" -m "Mutation checks: breaking the exactly-one guard, the fallback flag, and the Midlander agreement each fail a test." -m "Co-Authored-By: Claude Haiku 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Store the evidence on hair rows at scan time

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerModRow.cs`
- Modify: `PenumbraOrganizer.Plugin/LibraryWork/Pure/ScanProcessor.cs` (lines 76-79 and the row construction near line 85)
- Test: `PenumbraOrganizer.Plugin.Tests/LibraryWork/ScanProcessorTests.cs` (extend; read it first)

**Interfaces:**
- Consumes: `ModHairFileReader.ReadHairEvidence` (Task 2).
- Produces: `OrganizerModRow.HairFlat` (`IReadOnlySet<int>?`) and `OrganizerModRow.HairByRaceCode` (`IReadOnlyDictionary<string, IReadOnlySet<int>>?`). Both are null for non-hair rows and for unreadable hair rows.

- [ ] **Step 1: Write the failing test**

Add to `ScanProcessorTests.cs`, following its existing style for building a row:

```csharp
    [Fact]
    public void HairRow_CarriesThePerRaceEvidenceForLaterSorting()
    {
        // A hair mod whose Viera female folder has a different number from the rest. The row must
        // keep both views so Sort can apply a priority without re-reading the disk.
        var row = ScanAHairModWithFiles(
            "chara/human/c0801/obj/hair/h0108/model/a.mdl",
            "chara/human/c1801/obj/hair/h0003/model/b.mdl");

        Assert.Equal("0801", row.HairByRaceCode!.Keys.Single(k => k == "0801"));
        Assert.Equal(new HashSet<int> { 3 }, row.HairByRaceCode!["1801"].ToHashSet());
        Assert.Equal(new HashSet<int> { 3, 108 }, row.HairFlat!.ToHashSet());
    }

    [Fact]
    public void NonHairRow_CarriesNoHairEvidence()
    {
        var row = ScanAGearModWithFiles("chara/equipment/e0001/model/c0201e0001_top.mdl");
        Assert.Null(row.HairFlat);
        Assert.Null(row.HairByRaceCode);
    }
```

`ScanAHairModWithFiles` and `ScanAGearModWithFiles` are the test helpers `ScanProcessorTests` already uses to build a row from a temp mod folder. If those names differ in the file, use the file's own helper. Do not invent a new fixture style.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~ScanProcessorTests"`
Expected: build FAILS with `CS1061` for `HairFlat` and `HairByRaceCode`.

- [ ] **Step 3: Write the implementation**

In `OrganizerModRow.cs`, add two properties after `SubCategory`:

```csharp
    /// <summary>Every supported hair number the mod's files and EST entries carry. Null unless a readable hair mod.</summary>
    public IReadOnlySet<int>? HairFlat { get; init; }

    /// <summary>Hair numbers keyed by four-digit race folder code, from file paths only. Null unless a readable hair mod.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<int>>? HairByRaceCode { get; init; }
```

In `ScanProcessor.cs`, replace the hair block (currently lines 76-79) with a single read that fills both the classification and the evidence:

```csharp
        HairEvidence? hairEvidence = null;
        if (classification.Category == ModCategory.Hair)
        {
            hairEvidence = ModHairFileReader.ReadHairEvidence(modPath);
            classification = ModTypeClassifier.EnrichHairSubCategory(classification, hairEvidence?.Flat);
        }
```

Add `HairFlat = hairEvidence?.Flat,` and `HairByRaceCode = hairEvidence?.ByRaceCode,` to the `new OrganizerModRow { ... }` initializer, after `SubCategory = classification.SubCategory,`. Add `using PenumbraOrganizer.Plugin.Organizer.Classification;` if it is not already present.

`EnrichHairSubCategory` takes `IReadOnlySet<int>?`, so `hairEvidence?.Flat` matches its signature. The one-walk guarantee holds: `ReadHairEvidence` is the only disk read on this path.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~ScanProcessorTests"`
Expected: PASS, including the two new cases. Then the full suite.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/OrganizerModRow.cs PenumbraOrganizer.Plugin/LibraryWork/Pure/ScanProcessor.cs PenumbraOrganizer.Plugin.Tests/LibraryWork/ScanProcessorTests.cs
git commit -m "feat: keep hair evidence on the scan row for sort-time priority" -m "Co-Authored-By: Claude Haiku 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Sort applies the priority when Split hair is on

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs` (the `Sort` method, near line 263)
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateSortHairTests.cs` (extend the existing 4-test fixture)

**Interfaces:**
- Consumes: `HairPriority`, `HairPriorityResolver.Resolve` (Task 3); `OrganizerModRow.HairFlat`, `OrganizerModRow.HairByRaceCode` (Task 4).
- Produces: `Sort(SortStrategy strategy, bool splitGear, bool splitNpc, bool splitHair, Func<string,string> canonicalizeCreator, HairPriority? hairPriority = null)`. The new parameter is optional, so every existing call compiles unchanged.

- [ ] **Step 1: Write the failing tests**

Add to `OrganizerStateSortHairTests.cs`:

```csharp
    [Fact]
    public void Sort_WithPriority_FilesHairUnderThePriorityRaceNumber()
    {
        // Devilry shape: Viera female is 3, Midlander female is 108.
        var state = StateWithHairRow(
            flat: new HashSet<int> { 3, 108 },
            byRace: new Dictionary<string, IReadOnlySet<int>>
            {
                ["0201"] = new HashSet<int> { 108 },
                ["1801"] = new HashSet<int> { 3 },
            });

        state.Sort(SortStrategy.TypeOnly, true, true, true, c => c, new HairPriority("1801", false));

        Assert.Equal("Hair/3", state.Rows.Single().ProposedPath);
    }

    [Fact]
    public void Sort_WithNoPriority_IsIdenticalToTheSubCategoryTheScanStored()
    {
        // The byte-identical guarantee: with no priority, Sort reads the same SubCategory as 0.6.1.0.
        var state = StateWithHairRow(
            flat: new HashSet<int> { 115 },
            byRace: new Dictionary<string, IReadOnlySet<int>> { ["0801"] = new HashSet<int> { 115 } });

        state.Sort(SortStrategy.TypeOnly, true, true, true, c => c, null);
        var withoutPriority = state.Rows.Single().ProposedPath;

        state.Sort(SortStrategy.TypeOnly, true, true, true, c => c);
        Assert.Equal(withoutPriority, state.Rows.Single().ProposedPath);
        Assert.Equal("Hair/115", withoutPriority);
    }

    [Fact]
    public void Sort_PriorityIsIgnoredWhenSplitHairIsOff()
    {
        var state = StateWithHairRow(
            flat: new HashSet<int> { 3, 108 },
            byRace: new Dictionary<string, IReadOnlySet<int>> { ["1801"] = new HashSet<int> { 3 } });

        state.Sort(SortStrategy.TypeOnly, true, true, false, c => c, new HairPriority("1801", false));

        Assert.Equal("Hair", state.Rows.Single().ProposedPath);
    }
```

`StateWithHairRow` builds an `OrganizerState` with one `OrganizerModRow` whose `Category` is `ModCategory.Hair`, `HairFlat` is the given set, and `HairByRaceCode` is the given dictionary, using the file's existing row-building style. Add it to the test class if the file lacks it.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~OrganizerStateSortHairTests"`
Expected: build FAILS with `CS1501` (no overload takes six arguments).

- [ ] **Step 3: Write the implementation**

In `OrganizerState.cs`, change the `Sort` signature and the `Sub` local function:

```csharp
    public int Sort(SortStrategy strategy, bool splitGear, bool splitNpc, bool splitHair,
                    Func<string, string> canonicalizeCreator, HairPriority? hairPriority = null)
    {
        // Composed, not switched over: the three flatteners are independent decisions about the same
        // subcategory, so applying them in sequence is what makes every combination reachable
        // without writing eight variants of each strategy.
        string? Sub(OrganizerModRow row)
        {
            var sub = row.SubCategory;
            // A priority only ever changes a hair row's number, and only while hair is split. With no
            // priority the resolver's step 3 returns the same single number the scan stored, so the
            // output is unchanged from 0.6.1.0.
            if (splitHair && hairPriority is { } priority && row.Category == ModCategory.Hair)
            {
                var resolved = HairPriorityResolver.Resolve(row.HairFlat, row.HairByRaceCode, priority);
                sub = resolved?.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (!splitGear) sub = FlattenGearSubCategory(row.Category, sub);
            if (!splitNpc) sub = FlattenNpcSubCategory(row.Category, sub);
            if (!splitHair) sub = FlattenHairSubCategory(row.Category, sub);
            return sub;
        }
```

Leave the `return strategy switch { ... }` block unchanged.

Note the `resolved?.ToString(...)` returns `null` for flat; the flatteners and `GetFolder` already handle a null subcategory for hair as the flat `Hair` folder.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~OrganizerStateSort"`
Expected: PASS for the new three and every existing `OrganizerStateSort*` test, including the 16 byte-identical captured rows (md5 `e58f12ae...`), which must not change.

- [ ] **Step 5: Mutation-check**

1. Delete `splitHair &&` from the `if`. Expected: `Sort_PriorityIsIgnoredWhenSplitHairIsOff` FAILS.
2. Change `row.Category == ModCategory.Hair` to `true`. Expected: an existing gear or NPC sort test FAILS, or add one that pins a gear row under a priority to `null` before relying on this guard.
3. Replace `HairPriorityResolver.Resolve(...)` with `row.SubCategory`. Expected: `Sort_WithPriority_FilesHairUnderThePriorityRaceNumber` FAILS.

Record the results in the commit body.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateSortHairTests.cs
git commit -m "feat: apply the hair priority in Sort when Split hair is on" -m "Co-Authored-By: Claude Haiku 5.5 <noreply@anthropic.com>"
```

---

### Task 6: The Sort tab controls and Help text

**Prerequisite:** rebase this branch onto `feat/sort-layout-and-help` first, because `SortPanel.cs` and the Help files changed there:

```bash
git rebase feat/sort-layout-and-help
```

Resolve any conflict by keeping both sides' Help topics and the grouped layout from `feat/sort-layout-and-help`. The Hair group already exists in that branch.

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/SortPanel.cs`
- Modify: `PenumbraOrganizer.Plugin/Resources/help-content.json`, `Windows/HelpTopics.cs`, `Windows/HelpTab.cs`, `Windows/HelpTopicUsage.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Windows/SortSelectionTests.cs` (extend), `PenumbraOrganizer.Plugin.Tests/Windows/HelpContentTests.cs` (existing, must still pass)

**Interfaces:**
- Consumes: `HairPriority`, `HairRaceCodes.All` (Tasks 1, 3); `OrganizerState.Sort` with the new parameter (Task 5).
- Produces: `SortSelection(SortStrategy Strategy, bool SplitGear, bool SplitNpc, bool SplitHair, string? HairRaceCode = null, bool MidlanderFallback = false)` and `SortSelection.HairPriority` as a computed property.

- [ ] **Step 1: Write the failing tests**

Add to `SortSelectionTests.cs`:

```csharp
    [Fact]
    public void SortSelection_ValueEquality_CoversThePriorityFields()
    {
        var a = new SortSelection(SortStrategy.TypeOnly, false, true, true, "1801", false);
        var b = new SortSelection(SortStrategy.TypeOnly, false, true, true, "0201", false);
        var c = new SortSelection(SortStrategy.TypeOnly, false, true, true, "1801", true);

        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void SortSelection_HairPriority_IsNoneWhenTheRaceIsNull()
    {
        var selection = new SortSelection(SortStrategy.TypeOnly, false, true, true);
        Assert.Equal(new HairPriority(null, false), selection.HairPriority);
    }
```

Add to `HelpContentTests` nothing new: the existing tests already assert that every topic has a title, that every short is at most 200 characters and single-line, and that every control is in exactly one section. Those tests are the check for the new entries.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~SortSelectionTests"`
Expected: build FAILS with `CS7036` (no default for the new parameters) and `CS1061` (`HairPriority` is missing).

- [ ] **Step 3: Write the implementation**

In `SortPanel.cs`, extend the `SortSelection` record and add the computed property:

```csharp
public readonly record struct SortSelection(
    SortStrategy Strategy, bool SplitGear, bool SplitNpc, bool SplitHair,
    string? HairRaceCode = null, bool MidlanderFallback = false)
{
    // By Creator never consults category, so no split can change its output. All three checkboxes
    // are disabled when this is false.
    public bool SplitsApply => Strategy != SortStrategy.CreatorOnly;

    public HairPriority HairPriority => new(HairRaceCode, MidlanderFallback);
}
```

Add the session fields next to `_splitHair`, with the same comment style:

```csharp
    private int _hairRaceIndex;              // 0 is None; see HairRaceLabels
    private bool _midlanderFallback;         // off: an unchanged install keeps the 0.6.1.0 rule
```

Build the labels once, from `HairRaceCodes.All` in the same order, prefixed with None. The symbols are escapes:

```csharp
    private const string Male = "♂";
    private const string Female = "♀";

    // None first, then every confirmed race in table order. Labels are "Miqo'te ♂" and so on.
    private static readonly string[] HairRaceLabels =
        ["None", .. HairRaceCodes.All.Select(r => $"{r.Race} {(r.Gender == "Male" ? Male : Female)}")];

    private static string? HairRaceCodeAt(int index) =>
        index == 0 ? null : HairRaceCodes.All[index - 1].Code;
```

In `Draw`, inside the Hair group, after the Split hair checkbox and before the Hair `Help.Tooltip`, add the two controls, disabled with the same `splitsApply` gate:

```csharp
        ImGui.BeginDisabled(!splitsApply);
        ImGui.SetNextItemWidth(220);
        ImGui.Combo("Priority race", ref _hairRaceIndex, HairRaceLabels, HairRaceLabels.Length);
        ImGui.EndDisabled();
        Help.Tooltip(HelpTopics.SortPriorityRace, splitsApply ? null : SplitsInapplicableReason);

        ImGui.BeginDisabled(!splitsApply);
        ImGui.Checkbox("Midlander fallback", ref _midlanderFallback);
        ImGui.EndDisabled();
        Help.Tooltip(HelpTopics.SortMidlanderFallback, splitsApply ? null : SplitsInapplicableReason);
```

Pass the new fields wherever `SortSelection` is built in `Draw` and into `Sort`:

```csharp
        var selection = new SortSelection(strategy, _splitGear, _splitNpc, _splitHair,
            HairRaceCodeAt(_hairRaceIndex), _midlanderFallback);
        // ...
        state.Sort(selection.Strategy, selection.SplitGear, selection.SplitNpc, selection.SplitHair,
            canonicalizeCreator, selection.HairPriority);
```

Update the `splitsApply` line that builds a `SortSelection` for the gate to pass the same two new arguments, or leave them at their defaults since only `SplitsApply` reads it.

In `help-content.json`, add two control entries next to `sort.split-hair`. Both are short, one line, at most 200 characters:

```json
    {
      "id": "sort.priority-race",
      "title": "Priority race",
      "short": "When a hair mod has one hair number for this race, files it under that number. Pick None to turn this off."
    },
    {
      "id": "sort.midlander-fallback",
      "title": "Midlander fallback",
      "short": "When a mod uses different hair numbers across races, uses the Midlander number if male and female Midlanders agree. Otherwise the mod stays flat."
    },
```

In `HelpTopics.cs`, add next to `SortSplitHair`:

```csharp
    public static readonly HelpTopic SortPriorityRace = new("sort.priority-race");
    public static readonly HelpTopic SortMidlanderFallback = new("sort.midlander-fallback");
```

In `HelpTab.cs`, add both to the Sort section's control list directly after `HelpTopics.SortSplitHair,`. In `HelpTopicUsage.cs`, add both to `ReferencedByControls` directly after `HelpTopics.SortSplitHair,`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj`
Expected: the whole suite passes, including `SortSelectionTests`, `HelpContentTests` (short length and single line), and `HelpTabTests` (every control in exactly one section, every usage listed).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows PenumbraOrganizer.Plugin/Resources/help-content.json PenumbraOrganizer.Plugin.Tests/Windows
git commit -m "feat: priority race and Midlander fallback controls on the Sort tab" -m "Co-Authored-By: Claude Haiku 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Verification, docs and the in-game checklist

**Files:**
- Modify: `docs/HOW_SORTING_WORKS.md`, `docs/USER_GUIDE.md`
- Create: `docs/testing/2026-11-01-race-priority-hair-checklist.md`

- [ ] **Step 1: Full build and test run**

Run: `dotnet build PenumbraOrganizer.Plugin.sln` then `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj`
Expected: 0 errors; every test passes. Record the total count in the commit body.

- [ ] **Step 2: Add the real-mod worked examples to HOW_SORTING_WORKS.md**

Append a section that states the Devilry and Zutail results from the spec's worked tables, with the rule number for each row. Use the spec's table as the source; do not re-derive it.

- [ ] **Step 3: Add the Hair section to USER_GUIDE.md**

A short paragraph that points to the in-app Help tab for the controls. Do not copy the Help text.

- [ ] **Step 4: Write the in-game checklist**

Create `docs/testing/2026-11-01-race-priority-hair-checklist.md` with these checks, each with a checkbox:

```markdown
# Manual check: race-priority hair sorting

Build and load the plugin. Use the real mods named below, from the test set.

## Devilry Hair With Bow (Viera female = 3, others = 108)
- [ ] Split hair on, priority None: the mod sorts to flat Hair.
- [ ] Split hair on, priority Midlander female: Hair/108.
- [ ] Split hair on, priority Viera female: Hair/3.

## Imeris - Solar (hair 115, Viera female = 3)
- [ ] Priority None: flat Hair.
- [ ] Priority Miqo'te female: Hair/115.
- [ ] Priority Viera female: Hair/3.

## Winter Berry for Lalafell (Lalafell male and Midlander = 135, Highlander male = 5)
- [ ] Midlander fallback on, priority None: Hair/135.
- [ ] Midlander fallback off, priority None: flat Hair.

## Lumi's Hairstyle V2 (male Midlander 111, female 110)
- [ ] Midlander fallback on, priority None: flat Hair (the two genders disagree).

## Button and Help
- [ ] The Priority race and Midlander fallback controls are disabled when Split hair is off.
- [ ] Changed Items names: for each mod above, the number in Penumbra's changed-item name matches the number the plugin files under. Record any mismatch.
```

- [ ] **Step 5: Commit**

```bash
git add docs/HOW_SORTING_WORKS.md docs/USER_GUIDE.md docs/testing/2026-11-01-race-priority-hair-checklist.md
git commit -m "docs: race-priority worked examples, user guide and in-game checklist" -m "Co-Authored-By: Claude Haiku 5.5 <noreply@anthropic.com>"
```

---

## Self-Review

**1. Spec coverage**
- Priority race dropdown with 18 codes and None: Tasks 1, 6.
- Step 1 (priority exactly one): Task 3, tests and worked rows.
- Step 2 (absent falls through): Task 3.
- Step 3 (detected one number): Task 3.
- Step 4 (Midlander fallback): Task 3; both-gender, one-gender, none cases tested.
- Step 5 (flat): Task 3.
- Rule 6 (two numbers within one race never arbitrary): Task 3 `Rule6_...`.
- Governed by Split hair: Task 5 `splitHair &&` and Task 6 disabled controls.
- Session-only persistence: Task 6 plain fields.
- Byte-identical with defaults: Task 5 `Sort_WithNoPriority_...` and existing 16 captured rows.
- EST feeds flat only: Task 2 `ReadHairEvidence_EstEntriesFeedTheFlatSetOnly`.
- Help topics in four places: Task 6.
- Tiebreak (open question 1): excluded, stated in Global Constraints.
- Spec gap found while planning: the spec's data flow said the priority is applied in `EnrichHairSubCategory` at scan time. That cannot work, because the priority is chosen at Sort time and Sort reads only the stored row. The plan applies it at Sort time, with the evidence stored on the row (Tasks 4-5). The spec's data-flow section should be corrected to match.

**2. Placeholder scan.** No TBD or TODO. Two places refer to existing helpers in the test files (`ScanAHairModWithFiles`, `StateWithHairRow`, the `*ForTest` helpers in Task 2). Each step says to use the file's existing helper style, and Task 2 says to drop the `ForTest` helpers if the file builds fixtures a different way. That is a real instruction, not a placeholder.

**3. Type consistency.** `HairRace(Code, Race, Gender)` (Task 1) is used by the resolver (Task 3) through `HairRaceCodes.MidlanderMale/MidlanderFemale`. `HairEvidence(Flat, ByRaceCode)` (Task 2) feeds `OrganizerModRow.HairFlat` and `HairByRaceCode` (Task 4), which `Sort` reads (Task 5). `HairPriority(RaceCode, MidlanderFallback)` (Task 3) is built by `SortSelection.HairPriority` (Task 6) and passed to `Sort` (Task 5). `Resolve` returns `int?`, and Task 5 formats it with `ToString(InvariantCulture)`.
