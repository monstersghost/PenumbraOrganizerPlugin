# Race Split for Race-Specific Categories Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend the hair-by-race folder split to Face, Eyes, Skin, Tails/Ears/Horns and a newly detected Racial Scaling group, with a master switch, per-row opt-out, a race priority and a multi-race toggle.

**Architecture:** Pure value types (`RaceNames`, `RacePriority`, `RaceSplitOptions`, `RaceEvidence`) and one pure resolver (`RaceFolderResolver`) decide the race folder. Scan time stores per-row race evidence on `OrganizerModRow` (new fields). Sort time appends the race tail in `SortFolderSelectors.Select`, and routes scan-flagged racial-scaling mods under Body. The Sort tab gains a Race section; the old hair checkbox is replaced by the Hair row.

**Tech Stack:** C# / .NET (Dalamud plugin), xUnit, Newtonsoft config via Dalamud, ImGui (Dalamud.Bindings.ImGui).

Spec: `docs/superpowers/specs/2026-11-22-race-split-all-categories-design.md` (read it first; this plan implements it exactly).

## Global Constraints

- Race folder names are exactly these nine, ASCII only: `Midlander`, `Highlander`, `Elezen`, `Miqo'te`, `Roegadyn`, `Lalafell`, `Au Ra`, `Hrothgar`, `Viera`. The fallback folder is exactly `Multi-race`. (Memory: non-ASCII characters have been mangled in transit four times; every string this plan adds is plain ASCII, built from literal text, and tests assert the specific string.)
- Defaults reproduce today's output exactly: master off, multi-race toggle on, priority empty, `RaceSplitOptions` null anywhere means "no race split beyond today's hair behavior". Existing tests must keep passing untouched except the two named in Task 8.
- `HairRaceCode` (Priority race, per race AND gender) and `MidlanderFallback` are never changed by this feature.
- Config properties are non-nullable with defaults today, so the new master is `bool?` (null = not migrated). Do not use a `List` initializer with values (Newtonsoft appends to pre-filled lists); `RaceSplitRows` is `List<string>?` where null means "all rows ticked".
- Legacy field `SortSplitHairByRace` keeps being written, derived from the effective Hair row (`master && Hair row ticked`), never from the master alone.
- Race evidence from changed-item keys needs a re-scan; rows scanned before the upgrade have `KeyRaces == null` and count as "no evidence" (`Multi-race`).
- A priority pick is never made from incomplete evidence. Unreadable or incomplete evidence always files under `Multi-race`.
- Racial scaling is a scan-time flag (`IsRacialScaling`), never a category change. With its row off (the default) a flagged mod stays in Gear exactly as today.
- Apply, collisions, protected mods and rollback are not changed.
- Test command (repo root `C:\Repo\PenumbraOrganizer.Plugin`): `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~<Name>"`. Build check: `dotnet build`. Plugin source lives in `PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin\`, tests in `PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin.Tests\` (paths below are relative to the repo root).
- There is no `InternalsVisibleTo`: anything a test touches must be `public`.

## File Structure

New, plugin project (`PenumbraOrganizer.Plugin/`):
- `Organizer/Classification/RaceNames.cs`: the nine names, the sixteen-clan fold.
- `Organizer/Classification/RaceEvidence.cs`: `RaceEvidence`, `RaceFolderReason`, `RaceFolderResult`, `RaceFolderResolver`.
- `Organizer/Classification/ModRspReader.cs`: `RspEvidence`, `RspCollector`, `ModRspReader`.
- `Organizer/Classification/CustomizationRaceReader.cs`: `RowRaceData` and the key-based reader.
- `Organizer/RacePriority.cs`: immutable ranked race list with sequence equality.
- `Organizer/RaceSplitOptions.cs`: `RaceRows` flags, `RaceRowNames`, `RaceSplitOptions`.
- `Organizer/RaceSplitRouting.cs`: which row governs a mod, its evidence, its tail.

Modified: `Configuration.cs`, `Organizer/LayoutOptions.cs`, `Windows/SortSelectionFactory.cs`, `Organizer/Classification/HairRaceFolders.cs`, `Organizer/Classification/BodyPart.cs`, `Organizer/BodyFolderLayout.cs`, `Organizer/SortFolderSelectors.cs`, `Organizer/OrganizerModRow.cs`, `Organizer/Classification/ModContentReader.cs`, `LibraryWork/Pure/ScanProcessor.cs`, `Organizer/OrganizerState.cs`, `Organizer/OrganizerExportFormatter.cs`, `Windows/SortPanel.cs`, `Windows/SortLayout.cs`, `Windows/HelpTopics.cs`, `Windows/HelpTopicUsage.cs`, `Windows/HelpTab.cs`, `Windows/MainWindow.cs` (diagnostic dump), `Resources/help-content.json`.

New tests (`PenumbraOrganizer.Plugin.Tests/`): `Organizer/Classification/RaceNamesTests.cs`, `RaceFolderResolverTests.cs`, `ModRspReaderTests.cs`, `CustomizationRaceReaderTests.cs`; `Organizer/RacePriorityTests.cs`, `RaceSplitOptionsTests.cs`, `RaceSplitSelectorTests.cs`; `ConfigurationRaceSplitTests.cs`. Existing test files extended where named.

Docs: `docs/testing/2026-11-22-race-split-checklist.md` (new), `docs/HOW_SORTING_WORKS.md`, `docs/USER_GUIDE.md`, the spec (one clarification).

---

### Task 1: Step 0 evidence gate (manual, blocks Tasks 5-7)

Spec section 3 says no non-hair evidence has been checked against real mods. This task records that check. It needs the author's Penumbra and the supplied pack `variant_miqotes_v2.ttmp2`. If any result differs from what the spec assumes, stop and update the spec before Task 5.

**Files:**
- Create: `docs/testing/2026-11-22-race-split-checklist.md`
- Create (after the manual check): `PenumbraOrganizer.Plugin.Tests/Fixtures/RaceSplit/variant-miqotes-v2.default_mod.json`

**Interfaces:**
- Consumes: nothing.
- Produces: the confirmed JSON shape of an Rsp manipulation, the confirmed key text for the five unseen races, the confirmed ear/horn key part names, and a real fixture. Tasks 5-7 use these.

- [ ] **Step 1: Write the checklist file**

Create `docs/testing/2026-11-22-race-split-checklist.md` with this content:

```markdown
# Race split: step 0 evidence gate and manual checklist (2026-11-22)

Spec: docs/superpowers/specs/2026-11-22-race-split-all-categories-design.md

## Step 0 (before any non-hair code is written)

Tick each box and paste what you saw under it.

- [ ] A. Import `variant_miqotes_v2.ttmp2` into Penumbra. Open the new mod folder and find its config
      (`default_mod.json`, or `meta.json` under `DefaultData` on Penumbra 1.7+). Confirm it has a
      `Manipulations` array whose entries look like
      `{"Type":"Rsp","Manipulation":{"SubRace":"SeekerOfTheSun","Attribute":"MaleMinSize","Entry":1.0}}`,
      that `SubRace` is a name (not a number), that only `SeekerOfTheSun` and `KeeperOfTheMoon` appear, and
      that there are no `Files` entries. Copy the file to
      `PenumbraOrganizer.Plugin.Tests/Fixtures/RaceSplit/variant-miqotes-v2.default_mod.json`.
- [ ] B. In Penumbra's Changed Items tab for the same mod, copy the exact key text of one row. Expect
      `Seeker of the Sun Male Minimum Size` (no `Customization:` prefix).
- [ ] C. For one real mod of each of Highlander, Roegadyn, Lalafell, Hrothgar and Viera, copy one exact
      `Customization:` changed-item key (for example `Customization: Viera Female Face 3`). Confirm the race
      word is spelled exactly as in the spec's table. Add each as an `InlineData` row in
      `ChangedItemKeyParserTests.cs` (the existing rows show the format).
- [ ] D. Find one real tail mod and one real ear or horn mod (Miqo'te or Au Ra tail, Viera ears, Au Ra or
      Hrothgar horns). Copy their exact `Customization:` keys. Confirm the body part word is `Tail` or `Ears`.
      If horns use another word, write it here and add it to the part list in Task 5.
- [ ] E. Search your library for any mod folder whose group files contain a `Files` key ending `.rgsp`.
      Write "none found" or list them. If any exist, the racial-scaling reader needs a path rule too: stop
      and update the spec.
- [ ] F. Check a real face mod with an iris option: its keys should include `Face (Iris)` and `Face`.
      Confirm an iris-only mod shows only `Face (Iris)` rows.

## After the feature is built (in game)

- [ ] Master off: Sort proposes exactly the same folders as before the update (compare a saved
      Export before and after).
- [ ] Master on, Hair only ticked (the migrated state for an old hair-race user): same result as the old
      "Split hair by race" checkbox.
- [ ] Tick Face, Skin, Tails/Ears/Horns: run Scan, then Sort. One-race mods file under `Face/<Race>`,
      `Skin/<Race>`, `Body/.../<Race>`. Several-race mods file under `Multi-race`.
- [ ] Untick "Send multi-race mods to Multi-race", rank Viera first, Sort: a Viera plus Miqo'te mod files
      under Viera.
- [ ] Racial scaling: with the row off the pack stays in Gear; with it on (and Split body mods on) it
      files under `Body/03. Racial Scaling/Miqo'te`.
- [ ] Review tab Export shows a `StagedRace:` line for race-governed mods with its reason.
- [ ] Downgrade then upgrade: the hair setting survives; the other rows, priority and toggle reset.
```

- [ ] **Step 2: Run the manual check**

The author performs items A-F and pastes the results into the file. If any item contradicts the spec (Rsp shape, a race spelling, the part word, a raw `.rgsp` file), stop and update the spec first.

- [ ] **Step 3: Commit**

```bash
git add docs/testing/2026-11-22-race-split-checklist.md PenumbraOrganizer.Plugin.Tests/Fixtures/RaceSplit
git commit -m "docs: race split step 0 evidence checklist and real fixture"
```

---

### Task 2: Race names and the Rsp clan fold

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Classification/RaceNames.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/RaceNamesTests.cs`

**Interfaces:**
- Consumes: `HairRaceCodes.All` (existing, `Organizer/Classification/HairRaceCodes.cs`), `HairRaceFolders.MultiRace` (existing const `"Multi-race"`).
- Produces: `RaceNames.All` (`IReadOnlyList<string>`), `RaceNames.MultiRace` (`string`), `RaceNames.IsKnown(string?)` (`bool`), `RaceNames.FromSubRaceEnumName(string?)` (`string?`).

- [ ] **Step 1: Write the failing test**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/RaceNamesTests.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class RaceNamesTests
{
    [Fact]
    public void All_IsExactlyTheNineNamesInOrder() =>
        Assert.Equal(
            new[] { "Midlander", "Highlander", "Elezen", "Miqo'te", "Roegadyn", "Lalafell", "Au Ra", "Hrothgar", "Viera" },
            RaceNames.All);

    [Fact]
    public void All_MatchesTheRacesHairRaceCodesKnows() =>
        Assert.Equal(
            RaceNames.All.OrderBy(n => n, StringComparer.Ordinal),
            HairRaceCodes.All.Select(r => r.Race).Distinct().OrderBy(n => n, StringComparer.Ordinal));

    [Fact]
    public void MultiRace_IsTheExistingFolderName() => Assert.Equal("Multi-race", RaceNames.MultiRace);

    [Theory]
    [InlineData("Midlander", true)]
    [InlineData("Miqo'te", true)]
    [InlineData("Hyur", false)]
    [InlineData("midlander", false)]
    [InlineData(null, false)]
    public void IsKnown_IsExactAndCaseSensitive(string? name, bool expected) =>
        Assert.Equal(expected, RaceNames.IsKnown(name));

    [Theory]
    [InlineData("Midlander", "Midlander")]
    [InlineData("Highlander", "Highlander")]
    [InlineData("Wildwood", "Elezen")]
    [InlineData("Duskwight", "Elezen")]
    [InlineData("Plainsfolk", "Lalafell")]
    [InlineData("Dunesfolk", "Lalafell")]
    [InlineData("SeekerOfTheSun", "Miqo'te")]
    [InlineData("KeeperOfTheMoon", "Miqo'te")]
    [InlineData("Seawolf", "Roegadyn")]
    [InlineData("Hellsguard", "Roegadyn")]
    [InlineData("Raen", "Au Ra")]
    [InlineData("Xaela", "Au Ra")]
    [InlineData("Helion", "Hrothgar")]
    [InlineData("Lost", "Hrothgar")]
    [InlineData("Rava", "Viera")]
    [InlineData("Veena", "Viera")]
    public void FromSubRaceEnumName_FoldsAllSixteenClans(string clan, string expected) =>
        Assert.Equal(expected, RaceNames.FromSubRaceEnumName(clan));

    [Theory]
    [InlineData("Unknown")]
    [InlineData("7")]
    [InlineData("Seeker of the Sun")]
    [InlineData("")]
    [InlineData(null)]
    public void FromSubRaceEnumName_RejectsUnknownNumericDisplayNamesAndEmpty(string? clan) =>
        Assert.Null(RaceNames.FromSubRaceEnumName(clan));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~RaceNamesTests"`
Expected: build FAIL, `RaceNames` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `PenumbraOrganizer.Plugin/Organizer/Classification/RaceNames.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>
/// The nine race folder names, spelled exactly as Penumbra's changed-item keys and
/// <see cref="HairRaceCodes"/> spell them. Midlander and Highlander are two names on purpose: hair has
/// always filed them separately, and merging them would rename existing folders.
/// </summary>
public static class RaceNames
{
    public const string MultiRace = HairRaceFolders.MultiRace;

    public static readonly IReadOnlyList<string> All =
    [
        "Midlander", "Highlander", "Elezen", "Miqo'te", "Roegadyn", "Lalafell", "Au Ra", "Hrothgar", "Viera",
    ];

    public static bool IsKnown(string? name) => name is not null && All.Contains(name, StringComparer.Ordinal);

    // Keyed on Penumbra's SubRace ENUM names, which is what an Rsp manipulation stores in its JSON
    // ("SeekerOfTheSun", not the display name "Seeker of the Sun"). Unknown, numbers and any other
    // spelling are deliberately absent: the caller treats them as unreadable evidence.
    private static readonly Dictionary<string, string> BySubRaceEnumName = new(StringComparer.Ordinal)
    {
        ["Midlander"] = "Midlander",
        ["Highlander"] = "Highlander",
        ["Wildwood"] = "Elezen",
        ["Duskwight"] = "Elezen",
        ["Plainsfolk"] = "Lalafell",
        ["Dunesfolk"] = "Lalafell",
        ["SeekerOfTheSun"] = "Miqo'te",
        ["KeeperOfTheMoon"] = "Miqo'te",
        ["Seawolf"] = "Roegadyn",
        ["Hellsguard"] = "Roegadyn",
        ["Raen"] = "Au Ra",
        ["Xaela"] = "Au Ra",
        ["Helion"] = "Hrothgar",
        ["Lost"] = "Hrothgar",
        ["Rava"] = "Viera",
        ["Veena"] = "Viera",
    };

    /// <summary>The race name for a SubRace enum name, or null when it is not one of the sixteen clans.</summary>
    public static string? FromSubRaceEnumName(string? subRace) =>
        subRace is not null && BySubRaceEnumName.TryGetValue(subRace, out var race) ? race : null;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~RaceNamesTests"`
Expected: PASS (all rows).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Classification/RaceNames.cs PenumbraOrganizer.Plugin.Tests/Organizer/Classification/RaceNamesTests.cs
git commit -m "feat: race names and the sixteen-clan Rsp fold"
```

---

### Task 3: Race priority, rows, options and the folder resolver

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/RacePriority.cs`
- Create: `PenumbraOrganizer.Plugin/Organizer/RaceSplitOptions.cs`
- Create: `PenumbraOrganizer.Plugin/Organizer/Classification/RaceEvidence.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/RacePriorityTests.cs`, `RaceSplitOptionsTests.cs`, `Classification/RaceFolderResolverTests.cs`

**Interfaces:**
- Consumes: `RaceNames` (Task 2).
- Produces (all `public`):
  - `RacePriority`: `Empty`, `Ranked` (`IReadOnlyList<string>`), `Normalize(IEnumerable<string>?)`, `Add(string)`, `Remove(string)`, `MoveUp(int)`, `MoveDown(int)`, `RankOf(string)` (`int`, -1 when unranked), `IEquatable<RacePriority>` with sequence equality.
  - `RaceRows` flags enum: `None, Hair, Face, Eyes, Skin, TailsEarsHorns, RacialScaling, All`.
  - `RaceRowNames`: `All` (`IReadOnlyList<(RaceRows Row, string Name, string Label)>`), `Parse(IEnumerable<string>?)` (`RaceRows`; null means All), `ToNames(RaceRows)` (`List<string>`).
  - `RaceSplitOptions(bool Enabled, RaceRows Rows, bool MultiRaceToMultiRace, RacePriority Priority)` record with `Off`, `InEffect(RaceRows, bool bodySplit)`, `NeedsBodySplit(RaceRows)`.
  - `RaceEvidence(IReadOnlySet<string> Races, bool Complete)` with `NoEvidence`, `Unreadable`, `Of(IEnumerable<string>, bool complete = true)`.
  - `RaceFolderReason` enum: `OneRace, SeveralToMultiRace, PriorityPick, SeveralNoRankedRace, NoEvidence, Unreadable`.
  - `RaceFolderResult(string Folder, RaceFolderReason Reason)`.
  - `RaceFolderResolver.Resolve(RaceEvidence, bool multiRaceToMultiRace, RacePriority) -> RaceFolderResult`.

- [ ] **Step 1: Write the failing tests**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/RacePriorityTests.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class RacePriorityTests
{
    [Fact]
    public void Normalize_KeepsKnownDistinctNamesInOrder() =>
        Assert.Equal(
            new[] { "Viera", "Miqo'te" },
            RacePriority.Normalize(["Viera", "Hyur", "Miqo'te", "Viera", "midlander"]).Ranked);

    [Fact]
    public void Normalize_NullIsEmpty() => Assert.Empty(RacePriority.Normalize(null).Ranked);

    [Fact]
    public void AddRemoveAndMove_ReturnNewInstancesAndNeverTouchTheOriginal()
    {
        var p = RacePriority.Empty.Add("Viera").Add("Elezen").Add("Viera");
        Assert.Equal(new[] { "Viera", "Elezen" }, p.Ranked);

        Assert.Equal(new[] { "Elezen", "Viera" }, p.MoveDown(0).Ranked);
        Assert.Equal(new[] { "Elezen", "Viera" }, p.MoveUp(1).Ranked);
        Assert.Equal(new[] { "Elezen" }, p.Remove("Viera").Ranked);
        Assert.Equal(new[] { "Viera", "Elezen" }, p.Ranked);
    }

    [Fact]
    public void MoveUpAtTopAndMoveDownAtBottom_ChangeNothing()
    {
        var p = RacePriority.Empty.Add("Viera").Add("Elezen");
        Assert.Equal(p.Ranked, p.MoveUp(0).Ranked);
        Assert.Equal(p.Ranked, p.MoveDown(1).Ranked);
    }

    [Fact]
    public void Add_IgnoresUnknownNames() => Assert.Empty(RacePriority.Empty.Add("Hyur").Ranked);

    [Fact]
    public void RankOf_IsZeroBasedAndMinusOneWhenUnranked()
    {
        var p = RacePriority.Empty.Add("Viera").Add("Elezen");
        Assert.Equal(0, p.RankOf("Viera"));
        Assert.Equal(1, p.RankOf("Elezen"));
        Assert.Equal(-1, p.RankOf("Lalafell"));
    }

    [Fact]
    public void Equality_IsBySequenceNotByReference()
    {
        var a = RacePriority.Empty.Add("Viera").Add("Elezen");
        var b = RacePriority.Normalize(["Viera", "Elezen"]);
        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, b.MoveDown(0));
    }
}
```

Create `PenumbraOrganizer.Plugin.Tests/Organizer/RaceSplitOptionsTests.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class RaceSplitOptionsTests
{
    private static RaceSplitOptions On(RaceRows rows) => new(true, rows, true, RacePriority.Empty);

    [Fact]
    public void Off_IsMasterOffAllRowsTickedToggleOnNoPriority()
    {
        Assert.False(RaceSplitOptions.Off.Enabled);
        Assert.Equal(RaceRows.All, RaceSplitOptions.Off.Rows);
        Assert.True(RaceSplitOptions.Off.MultiRaceToMultiRace);
        Assert.Empty(RaceSplitOptions.Off.Priority.Ranked);
    }

    [Fact]
    public void InEffect_NeedsTheMasterTheTickAndForTwoRowsTheBodySplit()
    {
        Assert.False(RaceSplitOptions.Off.InEffect(RaceRows.Face, bodySplit: true));
        Assert.True(On(RaceRows.Face).InEffect(RaceRows.Face, bodySplit: false));
        Assert.False(On(RaceRows.Hair).InEffect(RaceRows.Face, bodySplit: true));

        Assert.False(On(RaceRows.All).InEffect(RaceRows.TailsEarsHorns, bodySplit: false));
        Assert.True(On(RaceRows.All).InEffect(RaceRows.TailsEarsHorns, bodySplit: true));
        Assert.False(On(RaceRows.All).InEffect(RaceRows.RacialScaling, bodySplit: false));
        Assert.True(On(RaceRows.All).InEffect(RaceRows.RacialScaling, bodySplit: true));
    }

    [Fact]
    public void RowNames_RoundTripAndNullMeansAll()
    {
        Assert.Equal(RaceRows.All, RaceRowNames.Parse(null));
        Assert.Equal(RaceRows.None, RaceRowNames.Parse([]));
        Assert.Equal(RaceRows.Hair | RaceRows.Skin, RaceRowNames.Parse(["Hair", "Skin", "Bogus"]));
        Assert.Equal(new[] { "Hair", "Skin" }, RaceRowNames.ToNames(RaceRows.Hair | RaceRows.Skin));
        Assert.Equal(6, RaceRowNames.All.Count);
    }

    [Fact]
    public void Equality_ComparesThePriorityBySequence()
    {
        var a = new RaceSplitOptions(true, RaceRows.All, true, RacePriority.Empty.Add("Viera"));
        var b = new RaceSplitOptions(true, RaceRows.All, true, RacePriority.Normalize(["Viera"]));
        Assert.Equal(a, b);
        Assert.NotEqual(a, a with { MultiRaceToMultiRace = false });
    }
}
```

Create `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/RaceFolderResolverTests.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class RaceFolderResolverTests
{
    private static RaceFolderResult R(RaceEvidence e, bool toMulti = true, RacePriority? p = null) =>
        RaceFolderResolver.Resolve(e, toMulti, p ?? RacePriority.Empty);

    [Fact]
    public void OneRace_FilesUnderThatRace() =>
        Assert.Equal(new RaceFolderResult("Viera", RaceFolderReason.OneRace), R(RaceEvidence.Of(["Viera"])));

    [Fact]
    public void SeveralRaces_ToggleOn_GoToMultiRace() =>
        Assert.Equal(
            new RaceFolderResult("Multi-race", RaceFolderReason.SeveralToMultiRace),
            R(RaceEvidence.Of(["Viera", "Miqo'te"])));

    [Fact]
    public void SeveralRaces_ToggleOff_PicksTheHighestRankedRace()
    {
        var p = RacePriority.Empty.Add("Viera");
        Assert.Equal(
            new RaceFolderResult("Viera", RaceFolderReason.PriorityPick),
            R(RaceEvidence.Of(["Miqo'te", "Viera"]), toMulti: false, p));
    }

    [Fact]
    public void SeveralRaces_ToggleOff_PartialPriorityStillPicksARankedRaceAndUnrankedNeverWin()
    {
        var p = RacePriority.Empty.Add("Elezen").Add("Viera");
        Assert.Equal("Viera", R(RaceEvidence.Of(["Miqo'te", "Viera"]), toMulti: false, p).Folder);
    }

    [Fact]
    public void SeveralRaces_ToggleOff_NoneRanked_FallsBackToMultiRace()
    {
        var empty = R(RaceEvidence.Of(["Miqo'te", "Viera"]), toMulti: false);
        Assert.Equal(new RaceFolderResult("Multi-race", RaceFolderReason.SeveralNoRankedRace), empty);

        var other = RacePriority.Empty.Add("Elezen");
        Assert.Equal(
            RaceFolderReason.SeveralNoRankedRace,
            R(RaceEvidence.Of(["Miqo'te", "Viera"]), toMulti: false, other).Reason);
    }

    [Fact]
    public void NoEvidence_IsMultiRaceWhateverTheToggle()
    {
        var p = RacePriority.Empty.Add("Viera");
        Assert.Equal(new RaceFolderResult("Multi-race", RaceFolderReason.NoEvidence), R(RaceEvidence.NoEvidence, true, p));
        Assert.Equal(new RaceFolderResult("Multi-race", RaceFolderReason.NoEvidence), R(RaceEvidence.NoEvidence, false, p));
    }

    [Fact]
    public void IncompleteEvidence_IsNeverPickedByPriorityAndNeverFilesAsASingleRace()
    {
        var p = RacePriority.Empty.Add("Viera");
        Assert.Equal(
            new RaceFolderResult("Multi-race", RaceFolderReason.Unreadable),
            R(RaceEvidence.Of(["Viera", "Miqo'te"], complete: false), toMulti: false, p));
        Assert.Equal(
            new RaceFolderResult("Multi-race", RaceFolderReason.Unreadable),
            R(RaceEvidence.Of(["Viera"], complete: false)));
        Assert.Equal(new RaceFolderResult("Multi-race", RaceFolderReason.Unreadable), R(RaceEvidence.Unreadable));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~RacePriorityTests|FullyQualifiedName~RaceSplitOptionsTests|FullyQualifiedName~RaceFolderResolverTests"`
Expected: build FAIL, types do not exist.

- [ ] **Step 3: Write the implementations**

Create `PenumbraOrganizer.Plugin/Organizer/RacePriority.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// The user's ranked races, first is highest, used only to choose a folder for a mod made for several
/// races. May be partial: a ranked race wins in order, an unranked race never wins. Immutable, with
/// sequence equality so the Sort tab's "selection changed since the last sort" check (record equality)
/// compares it by value, as <c>BodyPriority</c> does.
/// </summary>
public sealed class RacePriority : IEquatable<RacePriority>
{
    public static readonly RacePriority Empty = new([]);

    private readonly string[] _ranked;

    private RacePriority(string[] ranked) => _ranked = ranked;

    public IReadOnlyList<string> Ranked => _ranked;

    /// <summary>Keeps known race names, drops unknown and duplicate ones, keeps the order.</summary>
    public static RacePriority Normalize(IEnumerable<string>? saved)
    {
        if (saved is null)
            return Empty;

        var ranked = new List<string>();
        foreach (var name in saved)
        {
            if (RaceNames.IsKnown(name) && !ranked.Contains(name, StringComparer.Ordinal))
                ranked.Add(name);
        }

        return new RacePriority([.. ranked]);
    }

    public RacePriority Add(string race) =>
        RaceNames.IsKnown(race) && RankOf(race) < 0 ? new([.. _ranked, race]) : this;

    public RacePriority Remove(string race) =>
        RankOf(race) < 0 ? this : new([.. _ranked.Where(r => !string.Equals(r, race, StringComparison.Ordinal))]);

    public RacePriority MoveUp(int index) => Swap(index - 1, index);

    public RacePriority MoveDown(int index) => Swap(index, index + 1);

    private RacePriority Swap(int first, int second)
    {
        if (first < 0 || second >= _ranked.Length || first >= second)
            return this;

        var copy = (string[])_ranked.Clone();
        (copy[first], copy[second]) = (copy[second], copy[first]);
        return new RacePriority(copy);
    }

    public int RankOf(string race) => Array.FindIndex(_ranked, r => string.Equals(r, race, StringComparison.Ordinal));

    public bool Equals(RacePriority? other) => other is not null && _ranked.AsSpan().SequenceEqual(other._ranked);

    public override bool Equals(object? obj) => Equals(obj as RacePriority);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var race in _ranked)
            hash.Add(race, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    public static bool operator ==(RacePriority? left, RacePriority? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(RacePriority? left, RacePriority? right) => !(left == right);
}
```

Create `PenumbraOrganizer.Plugin/Organizer/RaceSplitOptions.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>The rows of the Race section. Flags so a set of ticked rows is one comparable value.</summary>
[Flags]
public enum RaceRows
{
    None = 0,
    Hair = 1,
    Face = 2,
    Eyes = 4,
    Skin = 8,
    TailsEarsHorns = 16,
    RacialScaling = 32,
    All = 63,
}

/// <summary>Row names as stored in the config, and the labels the Sort tab shows.</summary>
public static class RaceRowNames
{
    private static readonly (RaceRows Row, string Name, string Label)[] Table =
    [
        (RaceRows.Hair, "Hair", "Hair"),
        (RaceRows.Face, "Face", "Face"),
        (RaceRows.Eyes, "Eyes", "Eyes"),
        (RaceRows.Skin, "Skin", "Skin"),
        (RaceRows.TailsEarsHorns, "TailsEarsHorns", "Tails, ears and horns"),
        (RaceRows.RacialScaling, "RacialScaling", "Racial scaling"),
    ];

    public static IReadOnlyList<(RaceRows Row, string Name, string Label)> All => Table;

    /// <summary>Null means "never touched": every row ticked. An empty list means the user unticked all.</summary>
    public static RaceRows Parse(IEnumerable<string>? names)
    {
        if (names is null)
            return RaceRows.All;

        var rows = RaceRows.None;
        foreach (var name in names)
        {
            foreach (var entry in Table)
            {
                if (string.Equals(entry.Name, name, StringComparison.Ordinal))
                    rows |= entry.Row;
            }
        }

        return rows;
    }

    public static List<string> ToNames(RaceRows rows) =>
        Table.Where(entry => rows.HasFlag(entry.Row)).Select(entry => entry.Name).ToList();
}

/// <summary>
/// The Race section of the Sort tab as one value. Null anywhere a <c>RaceSplitOptions?</c> is accepted
/// means today's behavior: no race split beyond the legacy hair option.
/// </summary>
public sealed record RaceSplitOptions(
    bool Enabled, RaceRows Rows, bool MultiRaceToMultiRace, RacePriority Priority)
{
    public static readonly RaceSplitOptions Off = new(false, RaceRows.All, true, RacePriority.Empty);

    /// <summary>The Tails/Ears/Horns and Racial scaling folders only exist while body mods are split.</summary>
    public static bool NeedsBodySplit(RaceRows row) => row is RaceRows.TailsEarsHorns or RaceRows.RacialScaling;

    /// <summary>A row applies only when the master is on, the row is ticked and its dependencies hold.</summary>
    public bool InEffect(RaceRows row, bool bodySplit) =>
        Enabled && Rows.HasFlag(row) && (!NeedsBodySplit(row) || bodySplit);
}
```

Create `PenumbraOrganizer.Plugin/Organizer/Classification/RaceEvidence.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>The races a mod's evidence names, and whether that read was complete.</summary>
public sealed record RaceEvidence(IReadOnlySet<string> Races, bool Complete)
{
    public static readonly RaceEvidence NoEvidence = new(new HashSet<string>(StringComparer.Ordinal), true);

    public static readonly RaceEvidence Unreadable = new(new HashSet<string>(StringComparer.Ordinal), false);

    public static RaceEvidence Of(IEnumerable<string> races, bool complete = true) =>
        new(races.ToHashSet(StringComparer.Ordinal), complete);
}

public enum RaceFolderReason
{
    OneRace,
    SeveralToMultiRace,
    PriorityPick,
    SeveralNoRankedRace,
    NoEvidence,
    Unreadable,
}

public sealed record RaceFolderResult(string Folder, RaceFolderReason Reason);

/// <summary>
/// The one pure rule that turns race evidence into a folder name (race spec, section 2). The workbook's
/// {race} token calls this same function so Sort and the workbook cannot disagree.
/// </summary>
public static class RaceFolderResolver
{
    public static RaceFolderResult Resolve(
        RaceEvidence evidence, bool multiRaceToMultiRace, RacePriority priority)
    {
        // Incomplete evidence never becomes a single race and is never picked by priority.
        if (!evidence.Complete)
            return new(RaceNames.MultiRace, RaceFolderReason.Unreadable);
        if (evidence.Races.Count == 0)
            return new(RaceNames.MultiRace, RaceFolderReason.NoEvidence);
        if (evidence.Races.Count == 1)
            return new(evidence.Races.Single(), RaceFolderReason.OneRace);
        if (multiRaceToMultiRace)
            return new(RaceNames.MultiRace, RaceFolderReason.SeveralToMultiRace);

        var pick = evidence.Races
            .Where(race => priority.RankOf(race) >= 0)
            .OrderBy(priority.RankOf)
            .FirstOrDefault();
        return pick is null
            ? new(RaceNames.MultiRace, RaceFolderReason.SeveralNoRankedRace)
            : new(pick, RaceFolderReason.PriorityPick);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~RacePriorityTests|FullyQualifiedName~RaceSplitOptionsTests|FullyQualifiedName~RaceFolderResolverTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/RacePriority.cs PenumbraOrganizer.Plugin/Organizer/RaceSplitOptions.cs PenumbraOrganizer.Plugin/Organizer/Classification/RaceEvidence.cs PenumbraOrganizer.Plugin.Tests/Organizer
git commit -m "feat: race priority, split options and the race folder resolver"
```

---

### Task 4: Config, migration, layout options, factory, and hair through the resolver

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Configuration.cs`
- Modify: `PenumbraOrganizer.Plugin/Organizer/LayoutOptions.cs`
- Modify: `PenumbraOrganizer.Plugin/Organizer/RaceSplitOptions.cs` (add `FromConfig`)
- Modify: `PenumbraOrganizer.Plugin/Windows/SortSelectionFactory.cs`
- Modify: `PenumbraOrganizer.Plugin/Organizer/Classification/HairRaceFolders.cs`
- Modify: `PenumbraOrganizer.Plugin/Organizer/SortFolderSelectors.cs:82-88` (hair call only)
- Test: create `PenumbraOrganizer.Plugin.Tests/ConfigurationRaceSplitTests.cs`; extend `Windows/SortSelectionFactoryTests.cs`, `Organizer/Classification/HairRaceFoldersTests.cs`

**Interfaces:**
- Consumes: Task 3 types.
- Produces:
  - `Configuration.RaceSplitEnabled` (`bool?`), `RaceSplitRows` (`List<string>?`), `RacePriority` (`List<string>`), `SendMultiRaceToMultiRace` (`bool`, default true), `EffectiveHairRaceSplit` (`bool`, JsonIgnore), `MigrateRaceSplit()`, `SyncLegacyRaceFields()`.
  - `RaceSplitOptions.FromConfig(Configuration) -> RaceSplitOptions`.
  - `LayoutOptions(bool SharedHandsAndFeet = false, bool SplitHairByRace = false, bool SplitEmotes = false, RaceSplitOptions? Race = null)`.
  - `HairRaceFolders.Tail(flat, byRace, priority, storedNumber, splitByNumber, RaceSplitOptions? race = null)`.

- [ ] **Step 1: Write the failing tests**

Create `PenumbraOrganizer.Plugin.Tests/ConfigurationRaceSplitTests.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests;

public class ConfigurationRaceSplitTests
{
    [Fact]
    public void Fresh_MigratesToMasterOffAllRowsTicked()
    {
        var config = new Configuration();
        config.MigrateToCurrentVersion();

        Assert.False(config.RaceSplitEnabled);
        Assert.Null(config.RaceSplitRows);
        Assert.True(config.SendMultiRaceToMultiRace);
        Assert.Empty(config.RacePriority);

        var options = RaceSplitOptions.FromConfig(config);
        Assert.False(options.Enabled);
        Assert.Equal(RaceRows.All, options.Rows);
    }

    [Fact]
    public void OldHairRaceUser_MigratesToMasterOnWithOnlyHairTicked()
    {
        var config = new Configuration { SortSplitHairByRace = true };
        config.MigrateToCurrentVersion();

        Assert.True(config.RaceSplitEnabled);
        Assert.Equal(new[] { "Hair" }, config.RaceSplitRows);
        Assert.True(config.EffectiveHairRaceSplit);
    }

    [Fact]
    public void Migration_RunsOnlyWhileTheMasterIsNull()
    {
        var config = new Configuration { SortSplitHairByRace = true, RaceSplitEnabled = false };
        config.MigrateToCurrentVersion();

        Assert.False(config.RaceSplitEnabled);
        Assert.Null(config.RaceSplitRows);
    }

    [Fact]
    public void FromConfig_BeforeMigration_StillReadsTheLegacyCheckbox()
    {
        var options = RaceSplitOptions.FromConfig(new Configuration { SortSplitHairByRace = true });
        Assert.True(options.Enabled);
        Assert.Equal(RaceRows.Hair, options.Rows);
    }

    [Fact]
    public void LegacyField_IsDerivedFromTheEffectiveHairRowNotTheMasterAlone()
    {
        var config = new Configuration { RaceSplitEnabled = true, RaceSplitRows = ["Face"] };
        config.SyncLegacyRaceFields();
        Assert.False(config.SortSplitHairByRace);

        config.RaceSplitRows = ["Face", "Hair"];
        config.SyncLegacyRaceFields();
        Assert.True(config.SortSplitHairByRace);

        config.RaceSplitRows = null; // all ticked
        config.SyncLegacyRaceFields();
        Assert.True(config.SortSplitHairByRace);

        config.RaceSplitEnabled = false;
        config.SyncLegacyRaceFields();
        Assert.False(config.SortSplitHairByRace);
    }

    [Fact]
    public void HairPriorityRaceAndMidlanderFallback_AreNeverTouched()
    {
        var config = new Configuration
        {
            SortSplitHairByRace = true, HairRaceCode = "1801", MidlanderFallback = true,
        };
        config.MigrateToCurrentVersion();
        config.RaceSplitEnabled = true;
        config.RaceSplitRows = ["Face"];
        config.SyncLegacyRaceFields();

        Assert.Equal("1801", config.HairRaceCode);
        Assert.True(config.MidlanderFallback);
    }

    [Fact]
    public void FromConfig_NormalizesThePriority()
    {
        var config = new Configuration { RaceSplitEnabled = true, RacePriority = ["Viera", "Bogus", "Viera"] };
        Assert.Equal(new[] { "Viera" }, RaceSplitOptions.FromConfig(config).Priority.Ranked);
    }
}
```

Append to `PenumbraOrganizer.Plugin.Tests/Windows/SortSelectionFactoryTests.cs`, inside the existing test class (match its existing `using`s and helpers; the labels list is `[]` as other tests there pass it):

```csharp
    [Fact]
    public void FromConfig_CarriesTheRaceSplitAndTheEffectiveHairFlag()
    {
        var config = new Configuration
        {
            RaceSplitEnabled = true,
            RaceSplitRows = ["Hair", "Skin"],
            SendMultiRaceToMultiRace = false,
            RacePriority = ["Viera"],
        };

        var selection = SortSelectionFactory.FromConfig(config, []);

        Assert.True(selection.Layout.SplitHairByRace);
        Assert.NotNull(selection.Layout.Race);
        Assert.True(selection.Layout.Race!.Enabled);
        Assert.Equal(RaceRows.Hair | RaceRows.Skin, selection.Layout.Race.Rows);
        Assert.False(selection.Layout.Race.MultiRaceToMultiRace);
        Assert.Equal(new[] { "Viera" }, selection.Layout.Race.Priority.Ranked);
    }

    [Fact]
    public void FromConfig_FaceOnly_LeavesTheHairFlagOff()
    {
        var config = new Configuration { RaceSplitEnabled = true, RaceSplitRows = ["Face"] };
        Assert.False(SortSelectionFactory.FromConfig(config, []).Layout.SplitHairByRace);
    }

    [Fact]
    public void SelectionEquality_SeesAChangedRacePriority()
    {
        var a = SortSelectionFactory.FromConfig(
            new Configuration { RaceSplitEnabled = true, RacePriority = ["Viera"] }, []);
        var same = SortSelectionFactory.FromConfig(
            new Configuration { RaceSplitEnabled = true, RacePriority = ["Viera"] }, []);
        var other = SortSelectionFactory.FromConfig(
            new Configuration { RaceSplitEnabled = true, RacePriority = ["Elezen"] }, []);

        Assert.Equal(a, same);
        Assert.NotEqual(a, other);
    }
```

If the file lacks `using PenumbraOrganizer.Plugin.Organizer;`, add it at the top.

Append to `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/HairRaceFoldersTests.cs` inside the class (the file already has `By(...)` and `Tail(...)` helpers; add a new helper that passes the race options):

```csharp
    private static string TailWith(IReadOnlyDictionary<string, IReadOnlySet<int>>? by, RaceSplitOptions race,
        bool byNumber = false)
    {
        var flat = by?.Values.SelectMany(v => v).ToHashSet();
        return HairRaceFolders.Tail(flat, by, null, null, byNumber, race);
    }

    private static RaceSplitOptions Race(bool toMulti, params string[] ranked) =>
        new(true, RaceRows.All, toMulti, RacePriority.Normalize(ranked));

    [Fact]
    public void ToggleOff_SeveralRaces_FilesUnderTheTopRankedRace() =>
        Assert.Equal("Viera", TailWith(By(("1801", [115]), ("0801", [105])), Race(false, "Viera")));

    [Fact]
    public void ToggleOff_PickedRaceWithOneNumber_GetsThatNumber() =>
        Assert.Equal("Viera/115", TailWith(By(("1801", [115]), ("0801", [105])), Race(false, "Viera"), byNumber: true));

    [Fact]
    public void ToggleOff_PickedRaceWithSeveralNumbers_GetsNoNumber() =>
        Assert.Equal("Viera", TailWith(By(("1801", [115, 120]), ("0801", [105])), Race(false, "Viera"), byNumber: true));

    [Fact]
    public void ToggleOff_NothingRanked_StaysMultiRace() =>
        Assert.Equal("Multi-race", TailWith(By(("1801", [115]), ("0801", [105])), Race(false)));

    [Fact]
    public void ToggleOn_BehavesExactlyAsBefore() =>
        Assert.Equal("Multi-race", TailWith(By(("1801", [115]), ("0801", [105])), Race(true, "Viera")));

    [Fact]
    public void NullRaceOptions_IsTodaysBehavior() =>
        Assert.Equal("Multi-race/115", Tail(By(("1801", [115]), ("0801", [115])), stored: "115"));
```

If the file lacks `using PenumbraOrganizer.Plugin.Organizer;`, it already has it (the first lines import it). Confirm the three usings at the top before running.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~ConfigurationRaceSplitTests|FullyQualifiedName~SortSelectionFactoryTests|FullyQualifiedName~HairRaceFoldersTests"`
Expected: build FAIL (new properties, `FromConfig`, the extra `Tail` parameter and `LayoutOptions.Race` do not exist).

- [ ] **Step 3: Implement**

In `PenumbraOrganizer.Plugin/Configuration.cs`, directly after the `SortSplitHairByRace` property (line 107), insert:

```csharp

    /// <summary>
    /// Race split master switch. Null means "not migrated yet": <see cref="MigrateRaceSplit"/> fills it
    /// from the legacy <see cref="SortSplitHairByRace"/>. Nullable on purpose, because every other
    /// property here is non-nullable with a default and "absent" could not be told from "default" (an
    /// older build that re-saves the file drops fields it does not know).
    /// </summary>
    public bool? RaceSplitEnabled { get; set; }

    /// <summary>Ticked rows of the Race section as names (see <c>RaceRowNames</c>). Null means all ticked.</summary>
    /// <remarks>No initializer: Newtonsoft appends to a pre-filled list instead of replacing it.</remarks>
    public List<string>? RaceSplitRows { get; set; }

    /// <summary>The user's ranked races for choosing a folder for a multi-race mod, highest first. May be partial.</summary>
    public List<string> RacePriority { get; set; } = [];

    /// <summary>On: a multi-race mod goes to Multi-race. Off: it goes to the top-ranked race it covers.</summary>
    public bool SendMultiRaceToMultiRace { get; set; } = true;

    /// <summary>Whether the Hair row is in effect: the master is on and Hair is ticked (or no row was ever unticked).</summary>
    [Newtonsoft.Json.JsonIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public bool EffectiveHairRaceSplit =>
        RaceSplitEnabled == true && (RaceSplitRows is null || RaceSplitRows.Contains("Hair"));

    /// <summary>
    /// Fills the race settings once, while <see cref="RaceSplitEnabled"/> is null. An old hair-race user gets
    /// master on with only Hair ticked, so nothing else moves; everyone else gets master off with every row
    /// ticked (the fresh-install state). Never re-runs once the master has a value.
    /// </summary>
    public void MigrateRaceSplit()
    {
        if (RaceSplitEnabled is not null)
            return;

        if (SortSplitHairByRace)
        {
            RaceSplitEnabled = true;
            RaceSplitRows = ["Hair"];
        }
        else
        {
            RaceSplitEnabled = false;
            RaceSplitRows = null;
        }
    }

    /// <summary>
    /// Rewrites the legacy hair checkbox from the EFFECTIVE Hair row (not from the master alone), so a
    /// downgrade keeps the user's hair behavior. <see cref="HairRaceCode"/> and <see cref="MidlanderFallback"/>
    /// are never touched.
    /// </summary>
    public void SyncLegacyRaceFields() => SortSplitHairByRace = EffectiveHairRaceSplit;
```

In the same file, inside `MigrateToCurrentVersion()`, add one line before `Version = CurrentVersion;`:

```csharp
        MigrateRaceSplit();
```

In `PenumbraOrganizer.Plugin/Organizer/RaceSplitOptions.cs`, add inside the `RaceSplitOptions` record body (after `InEffect`):

```csharp

    /// <summary>
    /// The options the saved configuration describes. Reads the legacy hair checkbox when the config has
    /// not been migrated yet, so a headless caller that skipped migration still sees the user's hair setting.
    /// </summary>
    public static RaceSplitOptions FromConfig(Configuration config)
    {
        var migrated = config.RaceSplitEnabled is not null;
        var enabled = config.RaceSplitEnabled ?? config.SortSplitHairByRace;
        var rows = migrated
            ? RaceRowNames.Parse(config.RaceSplitRows)
            : config.SortSplitHairByRace ? RaceRows.Hair : RaceRows.All;
        return new RaceSplitOptions(
            enabled, rows, config.SendMultiRaceToMultiRace, RacePriority.Normalize(config.RacePriority));
    }
```

(`RaceSplitOptions` is in `PenumbraOrganizer.Plugin.Organizer`; `Configuration` is in the parent namespace `PenumbraOrganizer.Plugin`, which resolves without a `using`.)

Replace the whole of `PenumbraOrganizer.Plugin/Organizer/LayoutOptions.cs` with:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// The optional layout splits on the Sort tab. All default to off, which is the 0.6.4.0 layout.
/// A null <c>LayoutOptions?</c> anywhere means all off.
/// </summary>
/// <remarks>
/// <see cref="SplitHairByRace"/> means "the Hair row of the race split is in effect"; the factory derives it
/// from <see cref="Race"/>. <see cref="Race"/> carries the rest of the Race section; null means no race
/// split beyond the legacy hair flag, with the multi-race toggle on and no priority, which is today's
/// behavior. The record struct compares <see cref="Race"/> by value (it is a record whose priority has
/// sequence equality), which the "selection changed since the last sort" check relies on.
/// </remarks>
public readonly record struct LayoutOptions(
    bool SharedHandsAndFeet = false, bool SplitHairByRace = false, bool SplitEmotes = false,
    RaceSplitOptions? Race = null);
```

In `PenumbraOrganizer.Plugin/Windows/SortSelectionFactory.cs`, replace the `FromConfig` body's `return new SortSelection(...)` statement (lines 24-30) with:

```csharp
        var race = RaceSplitOptions.FromConfig(config);
        return new SortSelection(
            strategy, config.SortSplitGear, config.SortSplitNpc, config.SortSplitHair,
            SortPanel.HairRaceCodeAt(SortPanel.HairRaceIndexFor(config.HairRaceCode)),
            config.MidlanderFallback, config.SortSplitGearBody, config.SortSplitBodyMods,
            priority,
            new LayoutOptions(
                config.SortSharedHandsFeet, race.InEffect(RaceRows.Hair, config.SortSplitBodyMods),
                config.SortSplitEmotes, race),
            RootOnly: false);
```

Replace the whole of `PenumbraOrganizer.Plugin/Organizer/Classification/HairRaceFolders.cs` with:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>
/// The folder below <c>Hair/</c> when hair is split by race: the race for a mod made for one race,
/// <c>Multi-race</c> for a mod made for several or one with no race evidence. With the race split's
/// multi-race toggle off, a mod made for several races files under the top-ranked race it covers.
/// </summary>
public static class HairRaceFolders
{
    public const string MultiRace = "Multi-race";

    /// <param name="flat">Every hair number the mod uses; passed to <see cref="HairPriorityResolver"/> only.</param>
    /// <param name="byRace">Hair numbers per race and gender folder code, or null when none were read.</param>
    /// <param name="priority">Null means no priority; the stored scan-time number is used for Multi-race.</param>
    /// <param name="storedNumber">The number the scan stored on the row, used for Multi-race with no priority.</param>
    /// <param name="splitByNumber">Whether the hair number split is also on.</param>
    /// <param name="race">Null means today's behavior: several races always go to Multi-race.</param>
    public static string Tail(
        IReadOnlySet<int>? flat,
        IReadOnlyDictionary<string, IReadOnlySet<int>>? byRace,
        HairPriority? priority,
        string? storedNumber,
        bool splitByNumber,
        RaceSplitOptions? race = null)
    {
        var raceNumbers = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        if (byRace is not null)
        {
            foreach (var (code, numbers) in byRace)
            {
                if (!HairRaceCodes.TryGetByCode(code, out var hairRace))
                    continue;
                if (!raceNumbers.TryGetValue(hairRace.Race, out var set))
                    raceNumbers[hairRace.Race] = set = [];
                set.UnionWith(numbers);
            }
        }

        // One race, or several races with the toggle off and one of them ranked: that race's own folder.
        // The priority race and the Midlander rule never apply here, and a picked race is numbered as if
        // it were the mod's only race.
        var result = RaceFolderResolver.Resolve(
            RaceEvidence.Of(raceNumbers.Keys),
            race?.MultiRaceToMultiRace ?? true,
            race?.Priority ?? RacePriority.Empty);
        if (result.Reason is RaceFolderReason.OneRace or RaceFolderReason.PriorityPick)
        {
            var numbers = raceNumbers[result.Folder];
            return Join(result.Folder, splitByNumber && numbers.Count == 1 ? numbers.Single() : null);
        }

        // Several races, or none: Multi-race, numbered by the existing priority rule.
        int? number = null;
        if (splitByNumber)
            number = priority is { } p ? HairPriorityResolver.Resolve(flat, byRace, p) : ParseStored(storedNumber);
        return Join(MultiRace, number);
    }

    private static string Join(string folder, int? number) =>
        number is { } n ? $"{folder}/{n.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(3, '0')}" : folder;

    // Same range the hair reader accepts: 1..9999.
    private static int? ParseStored(string? stored) =>
        int.TryParse(stored, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
        && n is >= 1 and <= 9999 ? n : null;
}
```

In `PenumbraOrganizer.Plugin/Organizer/SortFolderSelectors.cs`, change the hair branch's `HairRaceFolders.Tail(` call (line 87) to pass the race options:

```csharp
                    : $"{hairRoot}/{HairRaceFolders.Tail(row.HairFlat, row.HairByRaceCode, hairPriority, row.SubCategory, splitHair, opts.Race)}";
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~ConfigurationRaceSplitTests|FullyQualifiedName~SortSelectionFactoryTests|FullyQualifiedName~HairRaceFoldersTests"`
Expected: PASS, and every pre-existing `HairRaceFoldersTests` row still passes (that proves the null-options path is today's behavior).

Then run the whole suite: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: PASS. The two panel references to `LayoutOptions` (`SortPanel.cs:231`, `:346`) still compile because the new parameter is optional.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin PenumbraOrganizer.Plugin.Tests
git commit -m "feat: race split config, migration, layout options and hair through the resolver"
```

---

### Task 5: Scan-time race evidence and the racial-scaling flag

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Classification/ModRspReader.cs`
- Create: `PenumbraOrganizer.Plugin/Organizer/Classification/CustomizationRaceReader.cs`
- Modify: `PenumbraOrganizer.Plugin/Organizer/Classification/ModContentReader.cs`
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerModRow.cs`
- Modify: `PenumbraOrganizer.Plugin/LibraryWork/Pure/ScanProcessor.cs`
- Test: create `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModRspReaderTests.cs`, `CustomizationRaceReaderTests.cs`

**Interfaces:**
- Consumes: `RaceNames`, `ChangedItemKey`, `ChangedItemKeyParser`, `ModConfigFileWalker`, `ConfigVisitor` (existing).
- Produces:
  - `RspEvidence(IReadOnlySet<string> Races, bool Complete, bool Pure)`; `RspCollector` (mutable); `ModRspReader.CollectFromManipulation(JsonElement, RspCollector)`; `ModRspReader.Finish(RspCollector, int fileKeyCount) -> RspEvidence?` (null when the mod has no Rsp manipulation).
  - `RowRaceData(IReadOnlySet<string> Face, IReadOnlySet<string> Iris, IReadOnlySet<string> Skin, IReadOnlySet<string> TailEars, bool IrisOnly)`; `CustomizationRaceReader.From(IEnumerable<ChangedItemKey>) -> RowRaceData`.
  - `ModContent` gains `RspEvidence? Rsp = null` (last positional parameter).
  - `OrganizerModRow` gains `RowRaceData? KeyRaces`, `RspEvidence? Rsp`, `bool IsRacialScaling` (all `init`).

If Task 1 item D found a horn key part other than `Tail` or `Ears`, add that word to `TailEarParts` in `CustomizationRaceReader` and a test row.

- [ ] **Step 1: Write the failing tests**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModRspReaderTests.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class ModRspReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("race-rsp-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ModContent? Read(string defaultModJson)
    {
        File.WriteAllText(Path.Combine(_dir, "default_mod.json"), defaultModJson);
        return ModContentReader.Read(new DirectoryInfo(_dir));
    }

    private static string Rsp(string subRace, string attribute = "MaleMinSize") =>
        $"{{\"Type\":\"Rsp\",\"Manipulation\":{{\"SubRace\":\"{subRace}\",\"Attribute\":\"{attribute}\",\"Entry\":1.0}}}}";

    [Fact]
    public void PureMiqoteScalingPack_IsOneRacePureAndComplete()
    {
        var json = "{\"Files\":{},\"Manipulations\":["
            + Rsp("SeekerOfTheSun") + "," + Rsp("KeeperOfTheMoon", "FemaleMaxSize") + "]}";

        var rsp = Read(json)!.Rsp;

        Assert.NotNull(rsp);
        Assert.Equal(new[] { "Miqo'te" }, rsp!.Races);
        Assert.True(rsp.Complete);
        Assert.True(rsp.Pure);
    }

    [Fact]
    public void TwoRaces_AreBothReported()
    {
        var json = "{\"Files\":{},\"Manipulations\":[" + Rsp("Midlander") + "," + Rsp("Rava") + "]}";
        var rsp = Read(json)!.Rsp!;
        Assert.Equal(new[] { "Midlander", "Viera" }, rsp.Races.OrderBy(r => r, StringComparer.Ordinal));
        Assert.True(rsp.Pure);
    }

    [Fact]
    public void ModWithoutRsp_HasNullEvidence() =>
        Assert.Null(Read("{\"Files\":{},\"Manipulations\":[]}")!.Rsp);

    [Fact]
    public void AnOtherManipulation_MakesItImpure()
    {
        var other = "{\"Type\":\"Atch\",\"Manipulation\":{}}";
        var rsp = Read("{\"Files\":{},\"Manipulations\":[" + Rsp("Midlander") + "," + other + "]}")!.Rsp!;
        Assert.False(rsp.Pure);
    }

    [Fact]
    public void AGameFileRedirect_MakesItImpure()
    {
        var json = "{\"Files\":{\"chara/equipment/e0001/model/c0101e0001_top.mdl\":\"a.mdl\"},"
            + "\"Manipulations\":[" + Rsp("Midlander") + "]}";
        Assert.False(Read(json)!.Rsp!.Pure);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("7")]
    [InlineData("Seeker of the Sun")]
    public void AnUnrecognizedSubRace_MakesTheEvidenceIncomplete(string subRace)
    {
        var rsp = Read("{\"Files\":{},\"Manipulations\":[" + Rsp("Midlander") + "," + Rsp(subRace) + "]}")!.Rsp!;
        Assert.False(rsp.Complete);
    }

    [Fact]
    public void ASubRaceThatIsANumberInJson_MakesTheEvidenceIncomplete()
    {
        var numeric = "{\"Type\":\"Rsp\",\"Manipulation\":{\"SubRace\":7,\"Attribute\":\"MaleMinSize\",\"Entry\":1.0}}";
        Assert.False(Read("{\"Files\":{},\"Manipulations\":[" + numeric + "]}")!.Rsp!.Complete);
    }

    [Fact]
    public void AnUnreadableConfig_MakesTheWholeReadNull() =>
        Assert.Null(Read("{ this is not json"));

    [Fact]
    public void TheRealSamplePack_WhenFixtureIsPresent_IsPureMiqote()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "RaceSplit", "variant-miqotes-v2.default_mod.json");
        if (!File.Exists(path))
            return; // captured by hand in Task 1 and copied to the test output; absent until then

        var rsp = Read(File.ReadAllText(path))!.Rsp!;
        Assert.Equal(new[] { "Miqo'te" }, rsp.Races);
        Assert.True(rsp.Complete);
        Assert.True(rsp.Pure);
    }
}
```

For the fixture to reach the test output, add to `PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj` inside an `<ItemGroup>` (create one if none fits):

```xml
  <ItemGroup>
    <None Update="Fixtures\RaceSplit\*.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

Create `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/CustomizationRaceReaderTests.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class CustomizationRaceReaderTests
{
    private static RowRaceData From(params string[] keys) =>
        CustomizationRaceReader.From(keys.Select(ChangedItemKeyParser.Parse));

    [Fact]
    public void FaceKeys_GoToFaceNotIris()
    {
        var data = From("Customization: Viera Female Face 3", "Customization: Viera Female Face (Iris) 3");
        Assert.Equal(new[] { "Viera" }, data.Face);
        Assert.Equal(new[] { "Viera" }, data.Iris);
        Assert.False(data.IrisOnly);
    }

    [Fact]
    public void IrisOnlyMod_IsFlaggedAndHasNoFaceRaces()
    {
        var data = From("Customization: Midlander Male Face (Iris) 219");
        Assert.True(data.IrisOnly);
        Assert.Empty(data.Face);
        Assert.Equal(new[] { "Midlander" }, data.Iris);
    }

    [Fact]
    public void IncidentalSkinKeys_NeverAddToFaceRaces()
    {
        var data = From("Customization: Viera Female Face 3", "Customization: Au Ra Female Skin Textures");
        Assert.Equal(new[] { "Viera" }, data.Face);
        Assert.Equal(new[] { "Au Ra" }, data.Skin);
    }

    [Fact]
    public void SkinTexturesKey_IsMatchedByContainingSkin() =>
        Assert.Equal(new[] { "Midlander" }, From("Customization: Midlander Female Skin Textures").Skin);

    [Fact]
    public void TailAndEarsKeys_GoToTailEars()
    {
        var data = From("Customization: Miqo'te Male Tail 4", "Customization: Viera Female Ears 2");
        Assert.Equal(new[] { "Miqo'te", "Viera" }, data.TailEars.OrderBy(r => r, StringComparer.Ordinal));
    }

    [Fact]
    public void KeyWithNoRace_IsNoEvidenceButStillCountsForIrisOnly()
    {
        var data = From("Customization: Player Face (Iris) 3");
        Assert.Empty(data.Iris);
        Assert.True(data.IrisOnly);
    }

    [Fact]
    public void UnknownRaceWord_IsIgnored() =>
        Assert.Empty(From("Customization: Hyur Male Face 3").Face);

    [Fact]
    public void NonCustomizationKeys_AreIgnored()
    {
        var data = From("Midlander Bust Minimum X-Axis", "Emote: Dance 74");
        Assert.Empty(data.Face);
        Assert.Empty(data.Skin);
        Assert.False(data.IrisOnly);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~ModRspReaderTests|FullyQualifiedName~CustomizationRaceReaderTests"`
Expected: build FAIL (`ModRspReader`, `RspEvidence`, `CustomizationRaceReader`, `ModContent.Rsp` do not exist).

- [ ] **Step 3: Implement the readers**

Create `PenumbraOrganizer.Plugin/Organizer/Classification/ModRspReader.cs`:

```csharp
using System.Text.Json;

namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>
/// What a mod's Rsp (racial scaling) manipulations say. <c>Races</c> are the nine race names the sub-races
/// fold to; <c>Complete</c> is false when any entry's SubRace was not one of the sixteen clan enum names;
/// <c>Pure</c> is true when the mod has no other manipulation and no game-file redirect.
/// </summary>
public sealed record RspEvidence(IReadOnlySet<string> Races, bool Complete, bool Pure);

/// <summary>Mutable tally filled by the walker's manipulation callback, then frozen by <see cref="ModRspReader.Finish"/>.</summary>
public sealed class RspCollector
{
    public HashSet<string> Races { get; } = new(StringComparer.Ordinal);
    public int RspCount { get; set; }
    public int OtherManipulations { get; set; }
    public bool Unrecognized { get; set; }
}

/// <summary>
/// Reads Rsp manipulations in the same shape as <see cref="ModHairFileReader"/>: the walker hands each
/// manipulation to <see cref="CollectFromManipulation"/> synchronously. Penumbra writes
/// <c>{"Type":"Rsp","Manipulation":{"SubRace":"SeekerOfTheSun","Attribute":"MaleMinSize","Entry":1.0}}</c>;
/// TexTools racial-scaling packs (.rgsp) are converted to exactly this on import.
/// </summary>
public static class ModRspReader
{
    public static void CollectFromManipulation(JsonElement item, RspCollector collector)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return;

        if (!item.TryGetProperty("Type", out var typeProp)
            || typeProp.ValueKind != JsonValueKind.String
            || typeProp.GetString() != "Rsp")
        {
            collector.OtherManipulations++;
            return;
        }

        collector.RspCount++;
        if (item.TryGetProperty("Manipulation", out var manipulation)
            && manipulation.ValueKind == JsonValueKind.Object
            && manipulation.TryGetProperty("SubRace", out var subRace)
            && subRace.ValueKind == JsonValueKind.String
            && RaceNames.FromSubRaceEnumName(subRace.GetString()) is { } race)
            collector.Races.Add(race);
        else
            collector.Unrecognized = true;
    }

    /// <summary>Null when the mod has no Rsp manipulation at all.</summary>
    public static RspEvidence? Finish(RspCollector collector, int fileKeyCount) =>
        collector.RspCount == 0
            ? null
            : new RspEvidence(
                collector.Races, Complete: !collector.Unrecognized,
                Pure: collector.OtherManipulations == 0 && fileKeyCount == 0);
}
```

Create `PenumbraOrganizer.Plugin/Organizer/Classification/CustomizationRaceReader.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>
/// The races a mod's customization changed-item keys name, grouped by the part they belong to. A key is
/// filed by (body part, subtype), so a face mod's incidental skin keys never add a race to Face.
/// </summary>
public sealed record RowRaceData(
    IReadOnlySet<string> Face,
    IReadOnlySet<string> Iris,
    IReadOnlySet<string> Skin,
    IReadOnlySet<string> TailEars,
    bool IrisOnly);

public static class CustomizationRaceReader
{
    // Real key parts: "Face", "Skin Textures", "Tail", "Ears". Horns: confirmed in the step 0 checklist.
    private static readonly string[] TailEarParts = ["Tail", "Ears"];

    public static RowRaceData From(IEnumerable<ChangedItemKey> keys)
    {
        var face = new HashSet<string>(StringComparer.Ordinal);
        var iris = new HashSet<string>(StringComparer.Ordinal);
        var skin = new HashSet<string>(StringComparer.Ordinal);
        var tailEars = new HashSet<string>(StringComparer.Ordinal);
        var hasIrisKey = false;
        var hasOtherFaceKey = false;

        foreach (var key in keys)
        {
            if (key.Shape != ChangedItemKeyShape.Customization)
                continue;

            var part = key.BodyPart ?? string.Empty;
            var race = RaceNames.IsKnown(key.Race) ? key.Race : null;

            if (part == "Face")
            {
                if (key.Subtype == "Iris")
                {
                    hasIrisKey = true;
                    if (race is not null)
                        iris.Add(race);
                }
                else
                {
                    hasOtherFaceKey = true;
                    if (race is not null)
                        face.Add(race);
                }
            }
            else if (part.Contains("Skin", StringComparison.Ordinal))
            {
                if (race is not null)
                    skin.Add(race);
            }
            else if (TailEarParts.Contains(part, StringComparer.Ordinal))
            {
                if (race is not null)
                    tailEars.Add(race);
            }
        }

        return new RowRaceData(face, iris, skin, tailEars, IrisOnly: hasIrisKey && !hasOtherFaceKey);
    }
}
```

In `PenumbraOrganizer.Plugin/Organizer/Classification/ModContentReader.cs`:

1. Change the `ModContent` record to add the Rsp evidence as the last parameter:

```csharp
public sealed record ModContent(
    IReadOnlySet<EquipmentSlot> Slots,
    ModHairFileReader.HairEvidence Hair,
    IReadOnlyList<string> GroupNames,
    IReadOnlyList<string> OptionNames,
    IReadOnlyList<string> FileValues,
    IReadOnlyList<string>? GamePaths = null,
    RspEvidence? Rsp = null);
```

2. In `Read`, add `var rsp = new RspCollector();` after `var gamePaths = new List<string>();`, add `ModRspReader.CollectFromManipulation(item, rsp);` as the last line inside the `OnManipulation` lambda, and replace the final `return` with:

```csharp
        return ok
            ? new ModContent(
                slots, ModHairFileReader.Freeze(flat, byRace), groups, options, values, gamePaths,
                ModRspReader.Finish(rsp, gamePaths.Count))
            : null;
```

In `PenumbraOrganizer.Plugin/Organizer/OrganizerModRow.cs`, add after the `EmoteNames` property:

```csharp

    /// <summary>
    /// The races this mod's customization keys name, by part. Filled at scan time for Face, Skin and Body
    /// rows only. Null for any other row and for a row scanned before race evidence existed, which counts as
    /// "no evidence" until the next scan.
    /// </summary>
    public RowRaceData? KeyRaces { get; init; }

    /// <summary>The mod's Rsp (racial scaling) evidence. Null unless the walked config had an Rsp manipulation.</summary>
    public RspEvidence? Rsp { get; init; }

    /// <summary>
    /// True for a pure racial-scaling mod (only Rsp manipulations, no file redirects). A FLAG, not a category:
    /// the classifier files these mods under Gear, and rewriting that at scan time would move them whatever
    /// the settings. Sort routes a flagged row under Body only while the Racial scaling row is in effect.
    /// </summary>
    public bool IsRacialScaling { get; init; }
```

In `PenumbraOrganizer.Plugin/LibraryWork/Pure/ScanProcessor.cs`:

1. In `Process`, after the `var bodyPart = ...;` statement and before `return new OrganizerModRow`, insert:

```csharp
        // Race evidence. Key-based for Face, Skin and Body rows (no disk read); Rsp-based for the
        // racial-scaling flag, which needs the walked content, so only rows the walk already covered.
        RowRaceData? keyRaces = classification.Category is ModCategory.Face or ModCategory.Skin or ModCategory.Body
            ? CustomizationRaceReader.From(item.ChangedItemKeys.Select(ChangedItemKeyParser.Parse))
            : null;
        var rsp = content?.Rsp;
```

2. In the object initializer, add after `EmoteNames = ... : null,`:

```csharp
            KeyRaces = keyRaces,
            Rsp = rsp,
            IsRacialScaling = rsp is { Pure: true },
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~ModRspReaderTests|FullyQualifiedName~CustomizationRaceReaderTests"`
Expected: PASS.

Then add scan-level tests to the existing `PenumbraOrganizer.Plugin.Tests/LibraryWork/ScanProcessorTests.cs`, inside the class. They reuse that file's `NewProcessor()` and `Seed(modDirectoryPath, name, params changedItemKeys)` helpers and its temp-directory pattern:

```csharp
    [Fact]
    public void FaceRow_CarriesTheRacesItsKeysName()
    {
        // A Face row is not walked, so the mod directory does not need to exist.
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        var row = NewProcessor().Process(
            Seed(missing, changedItemKeys: ["Customization: Viera Female Face 3", "Customization: Au Ra Female Skin Textures"]),
            CancellationToken.None);

        Assert.Equal(ModCategory.Face, row!.Category);
        Assert.Equal(new[] { "Viera" }, row.KeyRaces!.Face);
        Assert.Equal(new[] { "Au Ra" }, row.KeyRaces.Skin);
        Assert.False(row.IsRacialScaling);
    }

    [Fact]
    public void PureRspMod_StaysGearButIsFlaggedWithItsRace()
    {
        var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        dir.Create();
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "meta.json"),
                """{"DefaultData":{"Files":{},"Manipulations":[{"Type":"Rsp","Manipulation":{"SubRace":"SeekerOfTheSun","Attribute":"MaleMinSize","Entry":1.0}}]}}""");

            // An Rsp changed item has no "Customization:" prefix, so it parses as the Gear shape and the
            // classifier files the mod under Gear today. The flag is what lets Sort route it later.
            var row = NewProcessor().Process(
                Seed(dir.FullName, changedItemKeys: "Seeker of the Sun Male Minimum Size"), CancellationToken.None);

            Assert.Equal(ModCategory.Gear, row!.Category);
            Assert.True(row.IsRacialScaling);
            Assert.Equal(new[] { "Miqo'te" }, row.Rsp!.Races);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RspModThatAlsoRedirectsFiles_IsNotFlagged()
    {
        var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        dir.Create();
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "meta.json"),
                """{"DefaultData":{"Files":{"chara/equipment/e0001/model/c0101e0001_top.mdl":"x.mdl"},"Manipulations":[{"Type":"Rsp","Manipulation":{"SubRace":"Midlander","Attribute":"BustMinX","Entry":1.0}}]}}""");

            var row = NewProcessor().Process(
                Seed(dir.FullName, changedItemKeys: "Ala Mhigan Gown"), CancellationToken.None);

            Assert.Equal(ModCategory.Gear, row!.Category);
            Assert.False(row.IsRacialScaling);
            Assert.False(row.Rsp!.Pure);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
```

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin PenumbraOrganizer.Plugin.Tests
git commit -m "feat: scan-time race evidence and the racial-scaling flag"
```

---

### Task 6: Sort-time routing, the shared Racial Scaling body part, and the race tail

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/RaceSplitRouting.cs`
- Modify: `PenumbraOrganizer.Plugin/Organizer/Classification/BodyPart.cs`
- Modify: `PenumbraOrganizer.Plugin/Organizer/BodyFolderLayout.cs`
- Modify: `PenumbraOrganizer.Plugin/Organizer/SortFolderSelectors.cs` (`Type()` local function, lines 54-116)
- Test: create `PenumbraOrganizer.Plugin.Tests/Organizer/RaceSplitSelectorTests.cs`

**Interfaces:**
- Consumes: Tasks 2-5.
- Produces:
  - `BodyPart.RacialScaling` (appended to the enum), `BodyPartFolders.Folder(RacialScaling) == "Racial Scaling"`, `BodyFolderLayout.PartFolder(RacialScaling) == "03. Racial Scaling"`, and `BodyFolderLayout.IsShared(RacialScaling) == true`.
  - `RaceSplitRouting.Governing(OrganizerModRow, RaceSplitOptions, bool bodySplit) -> RaceRows?`
  - `RaceSplitRouting.EvidenceFor(OrganizerModRow, RaceRows) -> RaceEvidence`
  - `RaceSplitRouting.Resolve(OrganizerModRow, RaceSplitOptions?, bool bodySplit) -> RaceFolderResult?` (any governed row, hair included; used by diagnostics)
  - `RaceSplitRouting.IsRoutedToRacialScaling(OrganizerModRow, RaceSplitOptions?, bool bodySplit) -> bool`
  - `RaceSplitRouting.Tail(OrganizerModRow, RaceSplitOptions?, bool bodySplit) -> string?` (non-hair rows only; `Eyes/<race>` for Eyes)

- [ ] **Step 1: Write the failing tests**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/RaceSplitSelectorTests.cs`:

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class RaceSplitSelectorTests
{
    private static ISet<string> S(params string[] v) => new HashSet<string>(v, StringComparer.Ordinal);

    private static RaceSplitOptions Race(
        RaceRows rows = RaceRows.All, bool toMulti = true, string[]? ranked = null) =>
        new(true, rows, toMulti, RacePriority.Normalize(ranked ?? []));

    private static OrganizerModRow Row(
        ModCategory category, RowRaceData? keys = null, BodyPart part = BodyPart.Replacer,
        bool racialScaling = false, RspEvidence? rsp = null) =>
        new()
        {
            Identifier = "m", Name = "Mod", Author = "A", CurrentPath = "Mod", ProposedPath = "Mod",
            Category = category, KeyRaces = keys, BodyPart = part, IsRacialScaling = racialScaling, Rsp = rsp,
        };

    private static RowRaceData Keys(
        string[]? face = null, string[]? iris = null, string[]? skin = null, string[]? tail = null,
        bool irisOnly = false) =>
        new(S(face ?? []).ToHashSet(), S(iris ?? []).ToHashSet(), S(skin ?? []).ToHashSet(),
            S(tail ?? []).ToHashSet(), irisOnly);

    private static string Path(OrganizerModRow row, RaceSplitOptions? race, bool splitBodyMods = true)
    {
        var options = new LayoutOptions(false, race?.InEffect(RaceRows.Hair, splitBodyMods) ?? false, false, race);
        var layout = SortFolderSelectors.BuildBodyLayout([row], splitBodyMods, null);
        var (primary, _) = SortFolderSelectors.Select(
            SortStrategy.TypeOnly, splitGear: false, splitNpc: true, row,
            splitBodyMods: splitBodyMods, bodyLayout: layout, options: options);
        return primary!;
    }

    [Fact]
    public void NoRaceOptions_ChangesNothing()
    {
        Assert.Equal("Face", Path(Row(ModCategory.Face, Keys(face: ["Viera"])), null));
        Assert.Equal("Skin", Path(Row(ModCategory.Skin, Keys(skin: ["Viera"])), null));
    }

    [Fact]
    public void MasterOff_ChangesNothing() =>
        Assert.Equal("Face", Path(Row(ModCategory.Face, Keys(face: ["Viera"])), Race() with { Enabled = false }));

    [Fact]
    public void Face_OneRace_FilesUnderTheRace() =>
        Assert.Equal("Face/Viera", Path(Row(ModCategory.Face, Keys(face: ["Viera"])), Race()));

    [Fact]
    public void Face_SeveralRaces_ToggleOn_GoesToMultiRace() =>
        Assert.Equal("Face/Multi-race", Path(Row(ModCategory.Face, Keys(face: ["Viera", "Elezen"])), Race()));

    [Fact]
    public void Face_SeveralRaces_ToggleOff_PicksTheTopRankedRace() =>
        Assert.Equal(
            "Face/Elezen",
            Path(Row(ModCategory.Face, Keys(face: ["Viera", "Elezen"])), Race(toMulti: false, ranked: ["Elezen"])));

    [Fact]
    public void Face_RowUnticked_ChangesNothing() =>
        Assert.Equal("Face", Path(Row(ModCategory.Face, Keys(face: ["Viera"])), Race(RaceRows.Skin)));

    [Fact]
    public void RowScannedBeforeUpgrade_HasNoEvidenceAndFilesUnderMultiRace() =>
        Assert.Equal("Face/Multi-race", Path(Row(ModCategory.Face, keys: null), Race()));

    [Fact]
    public void Skin_OneRace() =>
        Assert.Equal("Skin/Miqo'te", Path(Row(ModCategory.Skin, Keys(skin: ["Miqo'te"])), Race()));

    [Fact]
    public void IrisOnly_EyesRowInEffect_FilesUnderFaceEyesRace() =>
        Assert.Equal(
            "Face/Eyes/Viera",
            Path(Row(ModCategory.Face, Keys(iris: ["Viera"], irisOnly: true)), Race()));

    [Fact]
    public void IrisOnly_EyesRowOff_FaceRowOn_StaysInFaceAndUsesTheIrisRaces() =>
        Assert.Equal(
            "Face/Viera",
            Path(Row(ModCategory.Face, Keys(iris: ["Viera"], irisOnly: true)), Race(RaceRows.Face)));

    [Fact]
    public void MixedFaceAndIris_StaysInFaceAndFollowsTheFaceRow() =>
        Assert.Equal(
            "Face/Elezen",
            Path(Row(ModCategory.Face, Keys(face: ["Elezen"], iris: ["Viera"], irisOnly: false)), Race()));

    [Fact]
    public void TailsEarsHorns_FilesUnderTheSharedPartThenTheRace() =>
        Assert.Equal(
            "Body/02. Tails, Ears and Horns/Viera",
            Path(Row(ModCategory.Body, Keys(tail: ["Viera"]), BodyPart.TailsEarsHorns), Race()));

    [Fact]
    public void TailsEarsHorns_NeedsTheBodySplit()
    {
        var row = Row(ModCategory.Body, Keys(tail: ["Viera"]), BodyPart.TailsEarsHorns);
        Assert.Equal("Body", Path(row, Race(), splitBodyMods: false));
    }

    [Fact]
    public void BodyReplacer_IsNeverRaceSplit()
    {
        var row = Row(ModCategory.Body, Keys(tail: ["Viera"]), BodyPart.Replacer);
        Assert.Null(RaceSplitRouting.Resolve(row, Race(), bodySplit: true));
        Assert.Null(RaceSplitRouting.Tail(row, Race(), bodySplit: true));
    }

    private static RspEvidence Rsp(bool pure = true, bool complete = true, string[]? races = null) =>
        new(S(races ?? []).ToHashSet(), complete, pure);

    [Fact]
    public void RacialScaling_RowOn_RoutesGearRowUnderBodyThenTheRace() =>
        Assert.Equal(
            "Body/03. Racial Scaling/Miqo'te",
            Path(Row(ModCategory.Gear, racialScaling: true, rsp: Rsp(races: ["Miqo'te"])), Race()));

    [Fact]
    public void RacialScaling_RowOff_StaysInGearExactlyAsToday() =>
        Assert.Equal(
            "Gear",
            Path(Row(ModCategory.Gear, racialScaling: true, rsp: Rsp(races: ["Miqo'te"])), Race(RaceRows.Hair)));

    [Fact]
    public void RacialScaling_MasterOff_StaysInGear() =>
        Assert.Equal(
            "Gear",
            Path(Row(ModCategory.Gear, racialScaling: true, rsp: Rsp(races: ["Miqo'te"])), RaceSplitOptions.Off));

    [Fact]
    public void RacialScaling_WithoutTheBodySplit_StaysInGear() =>
        Assert.Equal(
            "Gear",
            Path(Row(ModCategory.Gear, racialScaling: true, rsp: Rsp(races: ["Miqo'te"])), Race(), splitBodyMods: false));

    [Fact]
    public void RacialScaling_TwoRaces_ToggleOn_MultiRace() =>
        Assert.Equal(
            "Body/03. Racial Scaling/Multi-race",
            Path(Row(ModCategory.Gear, racialScaling: true, rsp: Rsp(races: ["Miqo'te", "Viera"])), Race()));

    [Fact]
    public void RacialScaling_IncompleteEvidence_IsMultiRaceEvenWithPriorityAndToggleOff() =>
        Assert.Equal(
            "Body/03. Racial Scaling/Multi-race",
            Path(
                Row(ModCategory.Gear, racialScaling: true, rsp: Rsp(complete: false, races: ["Viera"])),
                Race(toMulti: false, ranked: ["Viera"])));

    [Fact]
    public void RacialScaling_DoesNotShiftBodyNumbers()
    {
        var scaling = Row(ModCategory.Gear, racialScaling: true, rsp: Rsp(races: ["Miqo'te"]));
        var layoutWith = SortFolderSelectors.BuildBodyLayout([scaling], true, null)!;
        var layoutWithout = SortFolderSelectors.BuildBodyLayout([], true, null)!;
        Assert.Equal(layoutWithout.BodyNumbers, layoutWith.BodyNumbers);
    }

    [Fact]
    public void HairWithToggleOff_UsesTheResolverThroughTheSelector()
    {
        var row = new OrganizerModRow
        {
            Identifier = "h", Name = "Hair", Author = "A", CurrentPath = "Hair", ProposedPath = "Hair",
            Category = ModCategory.Hair,
            HairFlat = new HashSet<int> { 115, 105 },
            HairByRaceCode = new Dictionary<string, IReadOnlySet<int>>
            {
                ["1801"] = new HashSet<int> { 115 }, ["0801"] = new HashSet<int> { 105 },
            },
        };
        var race = Race(toMulti: false, ranked: ["Viera"]);
        var options = new LayoutOptions(false, true, false, race);

        var (primary, _) = SortFolderSelectors.Select(
            SortStrategy.TypeOnly, false, true, row, splitHair: false, options: options);

        Assert.Equal("Hair/Viera", primary);
    }

    [Fact]
    public void BodyPart_RacialScaling_IsSharedAndNamed()
    {
        Assert.True(BodyFolderLayout.IsShared(BodyPart.RacialScaling));
        Assert.Equal("03. Racial Scaling", BodyFolderLayout.PartFolder(BodyPart.RacialScaling));
        Assert.Equal("Racial Scaling", BodyPartFolders.Folder(BodyPart.RacialScaling));
    }

    [Fact]
    public void Resolve_ReportsTheReasonForADiagnostic()
    {
        var row = Row(ModCategory.Face, Keys(face: ["Viera", "Elezen"]));
        var result = RaceSplitRouting.Resolve(row, Race(), bodySplit: true);
        Assert.Equal(RaceFolderReason.SeveralToMultiRace, result!.Reason);
        Assert.Null(RaceSplitRouting.Resolve(row, null, bodySplit: true));
        Assert.Null(RaceSplitRouting.Resolve(Row(ModCategory.Weapon), Race(), bodySplit: true));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~RaceSplitSelectorTests"`
Expected: build FAIL (`BodyPart.RacialScaling`, `RaceSplitRouting` do not exist).

- [ ] **Step 3: Implement**

In `PenumbraOrganizer.Plugin/Organizer/Classification/BodyPart.cs`, change the enum and the switch:

```csharp
public enum BodyPart { Replacer, Skeleton, Physics, HandsAndFeet, Sfw, TailsEarsHorns, RacialScaling }
```

and add this arm before the `_ =>` arm of `BodyPartFolders.Folder`:

```csharp
        BodyPart.RacialScaling => "Racial Scaling",
```

In `PenumbraOrganizer.Plugin/Organizer/BodyFolderLayout.cs`, add this arm before the `_ =>` arm of `PartFolder` (after the `TailsEarsHorns` arm):

```csharp
        BodyPart.RacialScaling => "03. Racial Scaling",
```

and change `IsShared` to:

```csharp
    public static bool IsShared(BodyPart part) =>
        part is BodyPart.Skeleton or BodyPart.Physics or BodyPart.TailsEarsHorns or BodyPart.RacialScaling;
```

Create `PenumbraOrganizer.Plugin/Organizer/RaceSplitRouting.cs`:

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// Which Race-section row governs a mod, what race evidence that row reads, and the folder it adds.
/// One place, used by the folder selector and by diagnostics, so a staged path and its explanation
/// cannot disagree.
/// </summary>
public static class RaceSplitRouting
{
    /// <summary>The row that applies to this mod right now, or null when none is in effect for it.</summary>
    public static RaceRows? Governing(OrganizerModRow row, RaceSplitOptions race, bool bodySplit)
    {
        if (row.IsRacialScaling)
            return race.InEffect(RaceRows.RacialScaling, bodySplit) ? RaceRows.RacialScaling : null;

        switch (row.Category)
        {
            case ModCategory.Hair:
                return race.InEffect(RaceRows.Hair, bodySplit) ? RaceRows.Hair : null;
            case ModCategory.Face:
                if (row.KeyRaces is { IrisOnly: true } && race.InEffect(RaceRows.Eyes, bodySplit))
                    return RaceRows.Eyes;
                return race.InEffect(RaceRows.Face, bodySplit) ? RaceRows.Face : null;
            case ModCategory.Skin:
                return race.InEffect(RaceRows.Skin, bodySplit) ? RaceRows.Skin : null;
            case ModCategory.Body when row.BodyPart == BodyPart.TailsEarsHorns:
                return race.InEffect(RaceRows.TailsEarsHorns, bodySplit) ? RaceRows.TailsEarsHorns : null;
            default:
                return null;
        }
    }

    public static RaceEvidence EvidenceFor(OrganizerModRow row, RaceRows governing) => governing switch
    {
        RaceRows.Hair => row.HairByRaceCode is null
            ? RaceEvidence.Unreadable
            : RaceEvidence.Of(HairRaces(row.HairByRaceCode)),
        // An iris-only mod has no other Face keys, so under the Face row it uses its iris keys.
        RaceRows.Face => FromKeys(row.KeyRaces is { IrisOnly: true } ? row.KeyRaces.Iris : row.KeyRaces?.Face),
        RaceRows.Eyes => FromKeys(row.KeyRaces?.Iris),
        RaceRows.Skin => FromKeys(row.KeyRaces?.Skin),
        RaceRows.TailsEarsHorns => FromKeys(row.KeyRaces?.TailEars),
        RaceRows.RacialScaling => row.Rsp is null
            ? RaceEvidence.Unreadable
            : RaceEvidence.Of(row.Rsp.Races, row.Rsp.Complete),
        _ => RaceEvidence.NoEvidence,
    };

    private static RaceEvidence FromKeys(IReadOnlySet<string>? races) =>
        races is null ? RaceEvidence.NoEvidence : RaceEvidence.Of(races);

    private static IEnumerable<string> HairRaces(IReadOnlyDictionary<string, IReadOnlySet<int>> byRaceCode)
    {
        foreach (var code in byRaceCode.Keys)
        {
            if (HairRaceCodes.TryGetByCode(code, out var hairRace))
                yield return hairRace.Race;
        }
    }

    /// <summary>The folder result for any governed row, hair included. Null when no row governs the mod.</summary>
    public static RaceFolderResult? Resolve(OrganizerModRow row, RaceSplitOptions? race, bool bodySplit)
    {
        if (race is not { Enabled: true })
            return null;
        if (Governing(row, race, bodySplit) is not { } governing)
            return null;
        return RaceFolderResolver.Resolve(
            EvidenceFor(row, governing), race.MultiRaceToMultiRace, race.Priority);
    }

    /// <summary>True when a scan-flagged racial-scaling mod is routed under Body (instead of staying in Gear).</summary>
    public static bool IsRoutedToRacialScaling(OrganizerModRow row, RaceSplitOptions? race, bool bodySplit) =>
        race is { Enabled: true }
        && row.IsRacialScaling
        && race.InEffect(RaceRows.RacialScaling, bodySplit);

    /// <summary>
    /// The folders to append for a non-hair row: the race, with an <c>Eyes/</c> level first for the Eyes row.
    /// Hair appends its own tail inside <c>HairRaceFolders</c>, so it returns null here.
    /// </summary>
    public static string? Tail(OrganizerModRow row, RaceSplitOptions? race, bool bodySplit)
    {
        if (race is not { Enabled: true })
            return null;
        if (Governing(row, race, bodySplit) is not { } governing || governing == RaceRows.Hair)
            return null;

        var folder = RaceFolderResolver.Resolve(
            EvidenceFor(row, governing), race.MultiRaceToMultiRace, race.Priority).Folder;
        return governing == RaceRows.Eyes ? $"Eyes/{folder}" : folder;
    }
}
```

In `PenumbraOrganizer.Plugin/Organizer/SortFolderSelectors.cs`, inside `Select`, rename the existing local function `string? Type()` (line 54) to `string? BaseType()` and add the new `Type()` right after `BaseType` closes (after the `}` at line 116, before `return strategy switch`):

```csharp
        // The race split (race spec sections 2 and 4). A scan-flagged racial-scaling mod is filed as Gear
        // by the classifier; while its row is in effect it is routed under Body as a shared part instead.
        // Every governed non-hair row then gets its race tail appended after whatever path it already has,
        // so the template rename of the root and the numbered Body layout keep working untouched.
        string? Type()
        {
            string? path;
            if (RaceSplitRouting.IsRoutedToRacialScaling(row, opts.Race, splitBodyMods))
            {
                var root = TypeFolder(ModCategory.Body, null, renameFolder);
                path = root is null
                    ? null
                    : $"{root}/{(bodyLayout is not null ? BodyFolderLayout.PartFolder(BodyPart.RacialScaling) : BodyPartFolders.Folder(BodyPart.RacialScaling))}";
            }
            else
            {
                path = BaseType();
            }

            if (path is null)
                return null;

            var tail = RaceSplitRouting.Tail(row, opts.Race, splitBodyMods);
            return tail is null ? path : $"{path}/{tail}";
        }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~RaceSplitSelectorTests"`
Expected: PASS.

Then confirm nothing else lists `BodyPart` exhaustively: `Grep "BodyPart\.[A-Za-z]+ =>" PenumbraOrganizer.Plugin` must show only `BodyPart.cs` and `BodyFolderLayout.cs`. Then run the whole suite: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: PASS (existing numbered-body and gear tests prove the new part does not shift numbers).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin PenumbraOrganizer.Plugin.Tests
git commit -m "feat: sort-time race tail, Eyes row and the shared Racial Scaling body part"
```

---

### Task 7: Diagnostics

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerModRow.cs`
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs` (Sort loop and the three clear sites)
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerExportFormatter.cs`
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs` (diagnostic dump, near line 693)
- Test: extend `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerExportFormatterTests.cs`; create `PenumbraOrganizer.Plugin.Tests/Organizer/RaceReasonSummaryTests.cs`

**Interfaces:**
- Consumes: `RaceSplitRouting.Resolve` (Task 6), `RaceFolderResult`.
- Produces: `OrganizerModRow.StagedRace` (`RaceFolderResult?`, settable); `RaceReasonSummary.Describe(IEnumerable<OrganizerModRow>) -> string?` (null when no row has a staged race); export line `StagedRace: <folder> (<reason>)`; export header line `Race split: ...`.

- [ ] **Step 1: Write the failing tests**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/RaceReasonSummaryTests.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class RaceReasonSummaryTests
{
    private static OrganizerModRow Row(RaceFolderResult? staged) =>
        new()
        {
            Identifier = Guid.NewGuid().ToString("N"), Name = "m", Author = "a", CurrentPath = "m", ProposedPath = "m",
            StagedRace = staged,
        };

    [Fact]
    public void NoStagedRaces_IsNull() => Assert.Null(RaceReasonSummary.Describe([Row(null), Row(null)]));

    [Fact]
    public void CountsEachReasonSeparately_SoADetectionFailureNeverLooksLikeMultiRace()
    {
        var rows = new[]
        {
            Row(new("Viera", RaceFolderReason.OneRace)),
            Row(new("Viera", RaceFolderReason.OneRace)),
            Row(new("Multi-race", RaceFolderReason.SeveralToMultiRace)),
            Row(new("Elezen", RaceFolderReason.PriorityPick)),
            Row(new("Multi-race", RaceFolderReason.NoEvidence)),
            Row(new("Multi-race", RaceFolderReason.Unreadable)),
            Row(new("Multi-race", RaceFolderReason.SeveralNoRankedRace)),
        };

        Assert.Equal(
            "Race split: 2 one race, 1 several races (Multi-race), 1 several races (priority pick), "
            + "1 several races (none ranked, Multi-race), 1 no race evidence, 1 unreadable evidence",
            RaceReasonSummary.Describe(rows));
    }
}
```

Append to `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerExportFormatterTests.cs` inside the class (use the same `ReviewResult` construction as the existing tests):

```csharp
    [Fact]
    public void StagedRace_IsPrintedWithItsReason()
    {
        var row = new OrganizerModRow
        {
            Identifier = "r", Name = "Face mod", Author = "a", CurrentPath = "Face mod", ProposedPath = "Face/Viera/Face mod",
            StagedRace = new RaceFolderResult("Viera", RaceFolderReason.OneRace),
        };

        var text = OrganizerExportFormatter.Format([row], new ReviewResult([], new Dictionary<string, List<string>>()));

        Assert.Contains("StagedRace: Viera (OneRace)", text);
        Assert.Contains("Race split: 1 one race", text);
    }

    [Fact]
    public void NoStagedRace_PrintsNoRaceLines()
    {
        var row = new OrganizerModRow
        {
            Identifier = "r", Name = "m", Author = "a", CurrentPath = "m", ProposedPath = "m",
        };

        var text = OrganizerExportFormatter.Format([row], new ReviewResult([], new Dictionary<string, List<string>>()));

        Assert.DoesNotContain("StagedRace:", text);
        Assert.DoesNotContain("Race split:", text);
    }
```

If the file's usings lack `PenumbraOrganizer.Plugin.Organizer.Classification`, add it.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~RaceReasonSummaryTests|FullyQualifiedName~OrganizerExportFormatterTests"`
Expected: build FAIL (`StagedRace`, `RaceReasonSummary` do not exist).

- [ ] **Step 3: Implement**

In `OrganizerModRow.cs`, add after `StagedBodyResolution`:

```csharp

    /// <summary>
    /// Why the most recent sort gave this row the race folder it got (one race, several races and which rule
    /// chose, no evidence, unreadable evidence), or null when no race row governed it. Cleared whenever the
    /// row's proposal is set by anything other than that sort.
    /// </summary>
    public RaceFolderResult? StagedRace { get; set; }
```

Add the using at the top of the file if absent: `using PenumbraOrganizer.Plugin.Organizer.Classification;` (it is already imported there).

Create `PenumbraOrganizer.Plugin/Organizer/RaceReasonSummary.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// One line counting why rows got their race folder. Several races, no evidence and unreadable evidence all
/// file under Multi-race for compatibility, so the counts are kept apart: a detection failure must never
/// look like a genuine multi-race mod.
/// </summary>
public static class RaceReasonSummary
{
    public static string? Describe(IEnumerable<OrganizerModRow> rows)
    {
        var staged = rows.Where(r => r.StagedRace is not null).Select(r => r.StagedRace!.Reason).ToList();
        if (staged.Count == 0)
            return null;

        int Count(RaceFolderReason reason) => staged.Count(r => r == reason);

        var parts = new List<string>();
        void Add(RaceFolderReason reason, string text)
        {
            if (Count(reason) is > 0 and var n)
                parts.Add($"{n} {text}");
        }

        Add(RaceFolderReason.OneRace, "one race");
        Add(RaceFolderReason.SeveralToMultiRace, "several races (Multi-race)");
        Add(RaceFolderReason.PriorityPick, "several races (priority pick)");
        Add(RaceFolderReason.SeveralNoRankedRace, "several races (none ranked, Multi-race)");
        Add(RaceFolderReason.NoEvidence, "no race evidence");
        Add(RaceFolderReason.Unreadable, "unreadable evidence");
        return "Race split: " + string.Join(", ", parts);
    }
}
```

In `OrganizerExportFormatter.cs`:

1. After the `if (gearMods.Count > 0) { ... }` block (ends at line 42) and before `sb.AppendLine();` at line 44, insert:

```csharp

        if (RaceReasonSummary.Describe(mods) is { } raceSummary)
            sb.AppendLine(raceSummary);
```

2. After the `StagedBody:` block (after line 66), insert:

```csharp
            if (mod.StagedRace is { } stagedRace)
                sb.AppendLine($"StagedRace: {stagedRace.Folder} ({stagedRace.Reason})");
```

In `OrganizerState.cs`:

1. Where the Sort loop records `row.StagedBodyResolution` (the `foreach (var row in Mods)` at line 589), set the race result next to it. Replace the loop's final statement `row.StagedBodyResolution = applies && bodyPriority is not null ? ... : null;` by appending directly after it, inside the same loop body:

```csharp

            row.StagedRace = RaceSplitRouting.Resolve(row, options?.Race, splitBodyMods);
```

(The `IsResetByScope` branch already `continue`s after clearing, and Step 3.2 below makes it clear `StagedRace` too.)

2. Clear it wherever the body resolution is cleared. Run from the repo root:

```bash
sed -i 's/^\(\s*\)row\.StagedBodyResolution = null;/&\n\1row.StagedRace = null;/' PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs
```

then verify exactly four insertions (lines near 516, 593, 674, 705): `grep -n "StagedRace" PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs` must list four `= null;` lines and the one `Resolve` line.

In `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`, in the diagnostic-dump method, insert immediately before the line `sb.AppendLine("== Session event log (most recent first) ==");` (about line 693):

```csharp
        if (Organizer.RaceReasonSummary.Describe(_plugin.OrganizerState.Mods) is { } raceSummary)
        {
            sb.AppendLine(raceSummary);
            sb.AppendLine();
        }

```

If `_plugin.OrganizerState` does not resolve inside that method, use whichever member the surrounding code uses to reach the state (search the method for `OrganizerState`), then rebuild. The line is plain text and only prints when a race row governed at least one mod.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~RaceReasonSummaryTests|FullyQualifiedName~OrganizerExportFormatterTests"` then `dotnet build` then `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: PASS, build clean.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin PenumbraOrganizer.Plugin.Tests
git commit -m "feat: staged race reasons in the Review export and the diagnostic dump"
```

---

### Task 8: Sort tab Race section, persistence and help

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/SortLayout.cs`
- Modify: `PenumbraOrganizer.Plugin/Windows/SortPanel.cs`
- Modify: `PenumbraOrganizer.Plugin/Windows/HelpTopics.cs`, `HelpTopicUsage.cs`, `HelpTab.cs`
- Modify: `PenumbraOrganizer.Plugin/Resources/help-content.json`
- Test: extend `PenumbraOrganizer.Plugin.Tests/Windows/SortLayoutTests.cs`; fix `Windows/SortLayoutOptionsTests.cs` and `Windows/SortSelectionFactoryTests.cs` references to the removed checkbox state

**Interfaces:**
- Consumes: Tasks 3-6.
- Produces: `SortControlStates.RaceSplit` (`ControlState`, replaces `SplitHairByRace`); help topics `SortRaceSplit`, `SortRaceRows`, `SortMultiRace`, `SortRacePriority`.

- [ ] **Step 1: Write the failing test**

Append to `PenumbraOrganizer.Plugin.Tests/Windows/SortLayoutTests.cs` inside the class:

```csharp
    [Fact]
    public void RaceSection_IsAlwaysVisibleAndGreyedUnderCreatorGrouping()
    {
        var typeThenCreator = Compute();
        var creator = Compute(strategy: SortStrategy.CreatorOnly);
        var gateClosed = Compute(gateOpen: false);

        Assert.True(typeThenCreator.RaceSplit.Visible);
        Assert.True(typeThenCreator.RaceSplit.Enabled);
        Assert.True(creator.RaceSplit.Visible);
        Assert.False(creator.RaceSplit.Enabled);
        Assert.True(gateClosed.RaceSplit.Visible);
        Assert.False(gateClosed.RaceSplit.Enabled);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~SortLayoutTests"`
Expected: build FAIL (`RaceSplit` does not exist on `SortControlStates`).

- [ ] **Step 3: Implement the layout state**

In `Windows/SortLayout.cs`, in `SortControlStates` replace `ControlState SplitHairByRace,` with `ControlState RaceSplit,`, and in `Compute` replace `SplitHairByRace: new(true, splitEnabled),` with:

```csharp
            RaceSplit: new(true, splitEnabled),
```

Then build: `dotnet build`. Expected: compile errors only in `SortPanel.cs` (it still reads `states.SplitHairByRace`) and in tests that read `SplitHairByRace` from `SortControlStates`. Fix the tests by deleting the assertions or lines that read `states.SplitHairByRace` or `.SplitHairByRace` on a `SortControlStates` value in `SortLayoutOptionsTests.cs` (the checkbox no longer exists; the layout-options assertions about `LayoutOptions.SplitHairByRace` itself stay). Do not change any assertion about `LayoutOptions.SplitHairByRace`.

- [ ] **Step 4: Implement the panel**

In `Windows/SortPanel.cs`:

1. Remove the field `private bool _splitHairByRace;` (line 113) and add, in its place:

```csharp
    // The Race section. Master off and every row ticked is the fresh-install state; the master is a pure
    // gate that never changes the ticks. Saved the moment any of them changes, like the other Sort choices.
    private bool _raceEnabled;
    private RaceRows _raceRows = RaceRows.All;
    private bool _multiRaceToMultiRace = true;
    private RacePriority _racePriority = RacePriority.Empty;
```

2. In `PersistSortChoices`, replace the two `_splitHairByRace` mentions. In the condition, replace `&& config.SortSharedHandsFeet == _sharedHandsFeet && config.SortSplitHairByRace == _splitHairByRace` with `&& config.SortSharedHandsFeet == _sharedHandsFeet && RaceMatchesConfig(config)`. In the write block, replace `config.SortSplitHairByRace = _splitHairByRace;` with:

```csharp
        config.RaceSplitEnabled = _raceEnabled;
        config.RaceSplitRows = RaceRowNames.ToNames(_raceRows);
        config.SendMultiRaceToMultiRace = _multiRaceToMultiRace;
        config.RacePriority = [.. _racePriority.Ranked];
        config.SyncLegacyRaceFields();
```

and add this method just after `PersistSortChoices`:

```csharp

    private bool RaceMatchesConfig(Configuration config) =>
        config.RaceSplitEnabled == _raceEnabled
        && RaceRowNames.Parse(config.RaceSplitRows) == _raceRows
        && config.SendMultiRaceToMultiRace == _multiRaceToMultiRace
        && RacePriority.Normalize(config.RacePriority) == _racePriority;
```

3. In the `_prefsLoaded` block, replace `_splitHairByRace = loaded.Layout.SplitHairByRace;` with:

```csharp
            var race = loaded.Layout.Race ?? RaceSplitOptions.Off;
            _raceEnabled = race.Enabled;
            _raceRows = race.Rows;
            _multiRaceToMultiRace = race.MultiRaceToMultiRace;
            _racePriority = race.Priority;
```

4. In the Hair group, delete the line `DrawCheckbox("Split hair by race", ref _splitHairByRace, states.SplitHairByRace, HelpTopics.SortSplitHairByRace, splitReason);` (line 303). The "Priority race" and "Midlander fallback" controls stay as they are.

5. After the `GroupHeading("Animation"); ...` block (after the emotes checkbox, before the trailing `ImGui.Spacing(); ImGui.Separator();` at lines 335-337), insert:

```csharp

        GroupHeading("Race");
        DrawRaceSplit(state, states.RaceSplit, splitReason);
```

6. In the `selection` construction (lines 344-346), replace `new LayoutOptions(_sharedHandsFeet, _splitHairByRace, _splitEmotes)` with:

```csharp
            new LayoutOptions(
                _sharedHandsFeet,
                new RaceSplitOptions(_raceEnabled, _raceRows, _multiRaceToMultiRace, _racePriority)
                    .InEffect(RaceRows.Hair, _splitBodyMods),
                _splitEmotes,
                new RaceSplitOptions(_raceEnabled, _raceRows, _multiRaceToMultiRace, _racePriority)),
```

7. Add these methods to the class, next to `DrawBodyModsHelpMarker`:

```csharp
    private const string RaceNeedsBodySplitReason = "This row's folder only exists while body mods are split by body type.";
    private const string RaceRescanHint = "Scan again to use race split for faces, skin, tails and ears.";

    // The Race section: master switch, the six rows, the multi-race toggle and the ranked race list.
    // Every row is always visible. A row that needs the body split stays ticked while that split is off
    // and simply greys, so nothing the user chose is cleared. Under Creator grouping the whole section is
    // readable but not editable, with the reason in each tooltip.
    private void DrawRaceSplit(OrganizerState state, ControlState section, string? reason)
    {
        DrawCheckbox("Split supported categories by race", ref _raceEnabled, section, HelpTopics.SortRaceSplit, reason);

        foreach (var (row, _, label) in RaceRowNames.All)
        {
            var ticked = _raceRows.HasFlag(row);
            var needsBody = RaceSplitOptions.NeedsBodySplit(row);
            var enabled = section.Enabled && (!needsBody || _splitBodyMods);

            ImGui.Indent();
            ImGui.BeginDisabled(!enabled);
            if (ImGui.Checkbox($"{label}##race-row-{row}", ref ticked))
                _raceRows = ticked ? _raceRows | row : _raceRows & ~row;
            ImGui.EndDisabled();
            Help.Tooltip(HelpTopics.SortRaceRows, enabled ? null : needsBody && section.Enabled ? RaceNeedsBodySplitReason : reason);
            ImGui.Unindent();
        }

        ImGui.Indent();
        DrawCheckbox("Send multi-race mods to Multi-race", ref _multiRaceToMultiRace, section, HelpTopics.SortMultiRace, reason);
        ImGui.Unindent();

        DrawRacePriority(section);

        if (_raceEnabled && state.Mods.Any(m => m.KeyRaces is null
                && m.Category is Core.Classification.ModCategory.Face or Core.Classification.ModCategory.Skin
                    or Core.Classification.ModCategory.Body))
            ImGui.TextDisabled(RaceRescanHint);
    }

    // Collapsed by default, like Body priority. Ranked races win in order; an unranked race never wins.
    private void DrawRacePriority(ControlState section)
    {
        var open = ImGui.CollapsingHeader("Race priority##race-priority");
        Help.Tooltip(HelpTopics.SortRacePriority);
        if (!open)
            return;

        ImGui.BeginDisabled(!section.Enabled);
        ImGui.TextDisabled("Highest first. Used when a mod supports more than one race and the Multi-race box above is off.");

        var ranked = _racePriority.Ranked; // immutable: safe to iterate while a click replaces the field
        for (var i = 0; i < ranked.Count; i++)
        {
            ImGui.PushID(i);

            ImGui.BeginDisabled(i == 0);
            if (ImGui.ArrowButton("##up", ImGuiDir.Up))
                _racePriority = _racePriority.MoveUp(i);
            ImGui.EndDisabled();

            ImGui.SameLine();
            ImGui.BeginDisabled(i == ranked.Count - 1);
            if (ImGui.ArrowButton("##down", ImGuiDir.Down))
                _racePriority = _racePriority.MoveDown(i);
            ImGui.EndDisabled();

            ImGui.SameLine();
            if (ImGui.SmallButton("Remove##remove"))
                _racePriority = _racePriority.Remove(ranked[i]);

            ImGui.SameLine();
            ImGui.TextUnformatted($"{i + 1}. {ranked[i]}");

            ImGui.PopID();
        }

        var unranked = RaceNames.All.Where(r => _racePriority.RankOf(r) < 0).ToList();
        if (unranked.Count > 0)
        {
            ImGui.SetNextItemWidth(220);
            if (ImGui.BeginCombo("Add a race##race-priority-add", "Choose a race"))
            {
                foreach (var race in unranked)
                {
                    if (ImGui.Selectable($"{race}##race-add-{race}"))
                        _racePriority = _racePriority.Add(race);
                }
                ImGui.EndCombo();
            }
        }

        ImGui.EndDisabled();
    }
```

(`ranked` is read once per frame before any click replaces `_racePriority`; the loop iterates the old immutable list, matching how Body priority does it.)

- [ ] **Step 5: Help topics and text**

In `Windows/HelpTopics.cs`, next to `SortSplitHairByRace` (line 60) add:

```csharp
    public static readonly HelpTopic SortRaceSplit = new("sort.race-split");
    public static readonly HelpTopic SortRaceRows = new("sort.race-rows");
    public static readonly HelpTopic SortMultiRace = new("sort.multi-race");
    public static readonly HelpTopic SortRacePriority = new("sort.race-priority");
```

In `Windows/HelpTopicUsage.cs`, in the Sort block, add the four topics right after `HelpTopics.SortSplitHairByRace,` (line 40), and remove nothing (the old topic is still referenced by the Help tab; see below):

```csharp
        HelpTopics.SortRaceSplit,
        HelpTopics.SortRaceRows,
        HelpTopics.SortMultiRace,
        HelpTopics.SortRacePriority,
```

Because the old checkbox is gone, `SortSplitHairByRace` is no longer shown on a control. Remove its entry from `HelpTopicUsage.ReferencedByControls` (line 40) and keep its text in `help-content.json` for the Help tab (its tooltip text is reused as the Hair row's explanation by `SortRaceRows`). In `Windows/HelpTab.cs`, in the `HelpTabSort` group list, add the four new topics after `HelpTopics.SortSplitHairByRace,` (line 126).

In `Resources/help-content.json`, add these four entries immediately after the `"sort.split-hair-by-race"` entry (keep the existing entry, but replace its `short` with the first line below):

```json
    {
      "id": "sort.split-hair-by-race",
      "title": "Split hair by race (now the Hair row)",
      "short": "Hair is split by race from the Race section: tick the master switch and leave Hair ticked. One-race hair files under its race (Hair/Viera/115). Hair for several races, or with no race found, goes under Hair/Multi-race."
    },
    {
      "id": "sort.race-split",
      "title": "Split supported categories by race",
      "short": "Master switch for the race folders below. Off keeps your folders exactly as they are. It only changes the rows you leave ticked, and the split is by race, not gender, so a male-only and a female-only mod of one race share a folder."
    },
    {
      "id": "sort.race-rows",
      "title": "Race rows",
      "short": "Hair, Face, Eyes, Skin, Tails/ears/horns and Racial scaling get a race folder (Face/Viera). Tails/ears/horns and Racial scaling need Split body mods by body type. Face paint, Body meshes, gear and the rest are never split. Face, Skin, Tails and Ears use data read at scan time, so scan again after updating."
    },
    {
      "id": "sort.multi-race",
      "title": "Send multi-race mods to Multi-race",
      "short": "On: a mod made for several races goes to Multi-race. Off: it goes to the folder of the highest-ranked race it covers (Race priority). A mod with no race found, or one that could not be read, always goes to Multi-race."
    },
    {
      "id": "sort.race-priority",
      "title": "Race priority",
      "short": "Your ranked races, highest first. Only used when the Multi-race box is off. A race you do not rank never wins. This is separate from Priority race under Hair, which only picks the hair number."
    },
```

(The first object replaces the existing `sort.split-hair-by-race` entry in place; do not leave two entries with that id.)

- [ ] **Step 6: Run everything**

Run: `dotnet build` then `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: build clean; all tests PASS. The help-topic tests (`EveryTopicWithAShort_IsDeclaredAsUsedByAControl` and the Help-tab coverage test) must pass: if one names a topic missing from `HelpTopicUsage` or the Help tab, add it there.

- [ ] **Step 7: Commit**

```bash
git add PenumbraOrganizer.Plugin PenumbraOrganizer.Plugin.Tests
git commit -m "feat: Race section on the Sort tab, persistence, and help text"
```

---

### Task 9: Docs and the spec clarification

**Files:**
- Modify: `docs/HOW_SORTING_WORKS.md`
- Modify: `docs/USER_GUIDE.md`
- Modify: `docs/superpowers/specs/2026-11-22-race-split-all-categories-design.md`

**Interfaces:** none (documentation).

- [ ] **Step 1: Update HOW_SORTING_WORKS.md**

Replace the paragraph block under `### Split hair by race` (the heading at line 197; keep its bullets) so it begins:

```markdown
### Race split (hair and the other race rows)

The **Race** section of the Sort tab replaces the old **Split hair by race** checkbox. A master switch,
**Split supported categories by race** (off by default), gates six rows: Hair, Face, Eyes, Skin, Tails, ears
and horns, and Racial scaling. A fresh install has every row ticked, so turning the master on splits all of
them; unticking a row leaves that row sorting as before. Someone who used the old hair checkbox is migrated to
master on with only Hair ticked, so nothing else moves.

Folders are by race, never by gender, using the nine names Penumbra uses (Midlander and Highlander are two
folders): `Face/Viera`, `Skin/Miqo'te`, `Body/02. Tails, Ears and Horns/Viera`. A mod made for one race files
under it. A mod made for several races files under `Multi-race` while **Send multi-race mods to Multi-race**
is on; with it off it files under the highest-ranked race it covers in **Race priority** (a race you do not
rank never wins; if none of its races is ranked it still goes to `Multi-race`). A mod with no race found, or
whose evidence could not be read, always goes to `Multi-race`, and the Review export's `StagedRace:` line says
which of the three it was.

Race comes from different places per row. Hair reads the mod's file paths, as before. Face, Eyes, Skin and
Tails/ears/horns read the race words in the mod's changed-item keys, by body part (a face mod's incidental
skin keys never add a race to Face); an iris-only mod is the Eyes row and files under `Face/Eyes/<race>`.
These are stored at scan time, so scan again after updating. Racial scaling reads the sub-race of the mod's
racial-scaling manipulations. Tails/ears/horns and Racial scaling need **Split body mods by body type**.

Racial scaling: a mod made only of racial-scaling manipulations (no gear or other files) is filed under Gear
today. With the Racial scaling row in effect it files under `Body/03. Racial Scaling/<race>` instead; with the
row off it stays in Gear exactly as before.

**Priority race** under Hair is separate and unchanged: it is chosen per race and gender and only picks the
hair number. **Race priority** only chooses a folder.

The existing hair rules below still apply:
```

(Keep the existing bullets that follow, unchanged.)

- [ ] **Step 2: Update USER_GUIDE.md**

Run `Grep "Split hair by race" docs/USER_GUIDE.md` and replace each description of that checkbox with a short pointer to the Race section using the same wording as above (master switch, six rows, multi-race box, race priority, scan again). Add one sentence that tails, ears and horns and Racial scaling need Split body mods by body type, and one that face paint is never split.

- [ ] **Step 3: Amend the spec for the iris clarification**

In `docs/superpowers/specs/2026-11-22-race-split-all-categories-design.md`, section 3, in the bullet starting `- **Face, Eyes, Skin, Tails/Ears/Horns:**`, append this sentence at the end of the bullet's first paragraph (after "A key with no race (`Player Face`) is no evidence."):

```markdown
  An iris-only mod has no other Face keys, so when it is governed by the Face row (the Eyes row off) its race
  evidence is its iris keys.
```

- [ ] **Step 4: Verify and commit**

Run: `dotnet build` and `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: PASS (docs only; confirms nothing regressed).

```bash
git add docs
git commit -m "docs: race split in the sorting guide, user guide and spec"
```

---

## Self-Review

**Spec coverage (spec section to task):**
- Goal, decisions, correction (nine names): Task 2 (`RaceNames`, test that `All` equals `HairRaceCodes`), Task 3.
- 1 Settings: master as pure gate and rows default ticked (Task 3 `RaceSplitOptions`, Task 4 config); `RacePriority` normalized (Task 3); toggle default on (Task 4); Priority race and Midlander fallback untouched (Task 4 test); plumbing through `PersistSortChoices`, `SortSelectionFactory`, headless new-mod sort (factory is the shared path, Task 4; `PersistSortChoices` Task 8), sequence equality (Task 3, tested in Task 4); migration with nullable master, no re-run, legacy mirror from the effective Hair row, downgrade limit (Task 4 tests; the limit is a documented property of an older build, listed in the checklist); UI section, dependencies, help text (Task 8).
- 2 Folder rule: Task 3 resolver (rules 1-4 incl. partial priority and no priority pick from incomplete evidence); `<root>` and rename (Task 6); hair numbering after a pick (Task 4); one pure function shared with the workbook (`RaceFolderResolver`, `RaceSplitRouting.Resolve`); diagnostics reasons (Task 7); applicability independent of switches (`RaceSplitRouting.Governing` plus the workbook spec reuses `Resolve`; the "not applicable" case returns null).
- 3 Evidence: hair unchanged (Task 4 `HairRaceFolders`); key-based with (part, subtype) and `Skin Textures` containing match, new scan data and re-scan hint (Tasks 5, 8); Rsp via the walker hook, enum-name fold, Unknown/numeric incomplete (Tasks 2, 5); step 0 gate (Task 1).
- 4 Evaluation order, Eyes, racial scaling: flag not category, routed at sort time, row off stays Gear, shared body part in numbered and un-numbered paths, `IsShared` and the body-layout builder filter unaffected (Task 6 tests incl. "does not shift body numbers"); pure definition across all options and file keys (Task 5, `Pure`); Eyes and mixed mods (Task 6 tests).
- 5 Out of scope: nothing in the plan touches attach offsets, clans, templates, Apply.
- 6 Testing and docs: each listed test group maps to a task (folder rule T3/T6, evidence T5, migration T4, plumbing T4/T8, lifecycle T3 `InEffect`, transitions covered by the pure selector tests, racial scaling T6, defaults reproduce today T4 "NullRaceOptions" and the full-suite runs, docs T9). In-game verification is the checklist in Task 1.
- Gap noted, deliberately not a task: "Transitions on an already-sorted library: second sort moves nothing" is a property of the existing Sort pipeline (idempotent path computation). The selector is pure, and the existing sort tests cover re-sort stability; add an end-to-end check only if the in-game checklist shows drift.

**Placeholder scan:** no TBD/TODO. One step carries a conditional: Task 7's diagnostic-dump insertion names the exact anchor line and the exact code, and says to use the surrounding method's own way of reaching `OrganizerState` if `_plugin.OrganizerState` does not resolve there (the compile in that step catches it).

**Type consistency:** `RaceSplitOptions(bool Enabled, RaceRows Rows, bool MultiRaceToMultiRace, RacePriority Priority)` is used identically in Tasks 3, 4, 6, 8. `RaceFolderResult(string Folder, RaceFolderReason Reason)` is used in Tasks 3, 6, 7. `RowRaceData(Face, Iris, Skin, TailEars, IrisOnly)` and `RspEvidence(Races, Complete, Pure)` match between Task 5 definitions and Task 6 usage. `LayoutOptions.Race` (Task 4) is read in Task 6 as `opts.Race`. `SortControlStates.RaceSplit` (Task 8) replaces `SplitHairByRace`. `BodyPart.RacialScaling` is the same name in Tasks 6 and tests.

**Known risks to check while executing:** (1) Task 1 may show the real Rsp shape, a race spelling or the horn key part differs from the spec; stop and update the spec first. (2) Task 4 and Task 8 edit existing test files whose helper names were read but not compiled; if a helper differs, follow that file's own pattern and keep the assertion. (3) Task 8's help-topic coverage tests may name a topic that must also be added to the Help tab or `HelpTopicUsage`; the step says how.
