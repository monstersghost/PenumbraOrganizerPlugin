# Body Parts Folders and Collection Body Suggestion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** (1) File Body mods under `Body/<body>/<part>/<mod>` by part (Replacer, Skeleton, Physics, Hands and Feet). (2) Let the user press a button that reads the active Penumbra collection and offers to make its body the First-choice body.

**Architecture:** A pure `BodyPartClassifier` runs at scan time on the mod's name and game-file paths (the shared single config walk gains a `GamePaths` list). The row stores `BodyPart`; `SortFolderSelectors.Select` appends the part folder for Body rows when `splitBodyMods` is on. Feature 2 is a pure `ActiveCollectionBodyDetector`, a thin Penumbra IPC reader, and an on-demand button inside the existing Body priority section.

**Tech Stack:** C# / .NET 10, Dalamud, ImGui (`Dalamud.Bindings.ImGui`), Penumbra.Api 5.15.1, xUnit. There is no `InternalsVisibleTo`: anything a test touches must be public.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-11-06-body-parts-and-collection-detection-design.md` (amended by Task 0 below).
- Part folder names, exactly: `Replacer`, `Skeleton`, `Physics`, `Hands and Feet`. Plain names, no numbers.
- The part folder is added ONLY for `ModCategory.Body` rows, only when `splitBodyMods` is true, and only when the row resolved a body. No new checkbox, no template flag, `TemplateFallback` unchanged.
- A template `renameFolder` is applied to the `Body/<body>` folder BEFORE the part is appended, so template labels keep matching.
- The collection suggestion never changes the priority unless the user presses "Make first choice".
- Do not commit the diagnostics changes or touch the Unprotect-inside code. Do not release, tag, or enable the Discord workflow.
- Run tests with: `dotnet test PenumbraOrganizer.Plugin.Tests --nologo -v q` (add `--filter "FullyQualifiedName~Name"` for one test).
- Commit message trailer: `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.

---

### Task 0: Amend the spec with three decisions found while planning

**Files:**
- Modify: `docs/superpowers/specs/2026-11-06-body-parts-and-collection-detection-design.md`

- [ ] **Step 1: Edit the spec**

In the "Detection" section, replace the Hands and Feet row's File evidence cell with `none (name keywords only)`, and after the table add this paragraph:

```
**Replacer guard.** A body replacer commonly ships a skeleton file next to its body models, so
a mod with any `.mdl` game path containing `/obj/body/` is a Replacer whatever else it ships.
Skeleton and Physics file evidence is only trusted when no such body model is present.
```

In "Feature 2 / Behavior", replace the first sentence ("After a scan, the plugin reads...") with:

```
When the user presses "Check my collection" in the Body priority section, the plugin reads the enabled
mods of the collection affecting the local player (falling back to Penumbra's Current collection),
resolves each enabled Replacer-part Body mod's body, and counts distinct bodies.
```

Replace the "Open point to verify" section with:

```
### IPC used
`GetCollectionForObject(0)` (the local player), else `GetCollection(ApiCollectionType.Current)`, then
`GetAllModSettings(collectionId)`, keeping entries whose Enabled flag is true. Keys are mod directory
names, which equal `OrganizerModRow.Identifier`. Which of the two collections a user means is verified
in game, not assumed.
```

- [ ] **Step 2: Commit**

```bash
git add docs/superpowers/specs/2026-11-06-body-parts-and-collection-detection-design.md
git commit -m "docs: amend body parts spec (replacer guard, name-only hands/feet, on-demand collection check)

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 1: BodyPart enum and BodyPartClassifier

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Classification/BodyPart.cs`
- Create: `PenumbraOrganizer.Plugin/Organizer/Classification/BodyPartClassifier.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/BodyPartClassifierTests.cs`

**Interfaces:**
- Produces: `enum BodyPart { Replacer, Skeleton, Physics, HandsAndFeet }`; `static class BodyPartFolders { string Folder(BodyPart) }`; `static class BodyPartClassifier { BodyPart Classify(string name, IEnumerable<string>? gamePaths) }`. Namespace `PenumbraOrganizer.Plugin.Organizer.Classification`.

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Plugin.Organizer.Classification;
using Xunit;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public sealed class BodyPartClassifierTests
{
    [Fact]
    public void Sklb_file_alone_is_skeleton() =>
        Assert.Equal(BodyPart.Skeleton, BodyPartClassifier.Classify(
            "Rue+ extras", ["chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb"]));

    [Fact]
    public void Skp_file_alone_is_skeleton() =>
        Assert.Equal(BodyPart.Skeleton, BodyPartClassifier.Classify("x", ["chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.skp"]));

    [Fact]
    public void Phyb_file_alone_is_physics() =>
        Assert.Equal(BodyPart.Physics, BodyPartClassifier.Classify("x", ["chara/human/c0101/skeleton/base/b0001/phy_c0101b0001.phyb"]));

    [Fact]
    public void Skeleton_and_physics_files_together_are_a_replacer() =>
        Assert.Equal(BodyPart.Replacer, BodyPartClassifier.Classify("x",
            ["a/skl.sklb", "a/phy.phyb"]));

    [Fact]
    public void Body_model_present_makes_a_replacer_even_with_a_skeleton_file() =>
        Assert.Equal(BodyPart.Replacer, BodyPartClassifier.Classify("Rue+",
            ["chara/human/c0101/obj/body/b0001/model/c0101b0001_top.mdl", "a/skl.sklb"]));

    [Theory]
    [InlineData("Rue+ Skeleton", BodyPart.Skeleton)]
    [InlineData("Jiggle physics pack", BodyPart.Physics)]
    [InlineData("Rue+ Hands", BodyPart.HandsAndFeet)]
    [InlineData("Pretty feet", BodyPart.HandsAndFeet)]
    [InlineData("Toenails", BodyPart.HandsAndFeet)]
    [InlineData("Rue+", BodyPart.Replacer)]
    public void Name_keywords_decide_when_there_is_no_file_evidence(string name, BodyPart expected) =>
        Assert.Equal(expected, BodyPartClassifier.Classify(name, []));

    [Theory]
    [InlineData("Handsome face paint")]
    [InlineData("Skeletal armour")]
    [InlineData("Bouncer outfit")]
    public void Keywords_need_word_boundaries(string name) =>
        Assert.Equal(BodyPart.Replacer, BodyPartClassifier.Classify(name, []));

    [Fact]
    public void Two_name_parts_fall_back_to_replacer() =>
        Assert.Equal(BodyPart.Replacer, BodyPartClassifier.Classify("Skeleton and physics", []));

    [Fact]
    public void File_evidence_beats_the_name() =>
        Assert.Equal(BodyPart.Physics, BodyPartClassifier.Classify("Rue+ Hands", ["a/phy.phyb"]));

    [Fact]
    public void Null_paths_are_treated_as_none() =>
        Assert.Equal(BodyPart.Replacer, BodyPartClassifier.Classify("Rue+", null));

    [Theory]
    [InlineData(BodyPart.Replacer, "Replacer")]
    [InlineData(BodyPart.Skeleton, "Skeleton")]
    [InlineData(BodyPart.Physics, "Physics")]
    [InlineData(BodyPart.HandsAndFeet, "Hands and Feet")]
    public void Folder_names(BodyPart part, string folder) =>
        Assert.Equal(folder, BodyPartFolders.Folder(part));
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --nologo -v q --filter "FullyQualifiedName~BodyPartClassifierTests"`
Expected: build FAIL, `BodyPart` / `BodyPartClassifier` not defined.

- [ ] **Step 3: Implement**

`BodyPart.cs`:
```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>Which piece of a body setup a Body-category mod is. Replacer is the default and the fallback.</summary>
public enum BodyPart { Replacer, Skeleton, Physics, HandsAndFeet }

public static class BodyPartFolders
{
    public static string Folder(BodyPart part) => part switch
    {
        BodyPart.Replacer => "Replacer",
        BodyPart.Skeleton => "Skeleton",
        BodyPart.Physics => "Physics",
        BodyPart.HandsAndFeet => "Hands and Feet",
        _ => throw new ArgumentOutOfRangeException(nameof(part), part, "Unknown body part."),
    };
}
```

`BodyPartClassifier.cs`:
```csharp
using System.Text.RegularExpressions;

namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>
/// Decides which part of a body setup a Body mod is, from its game-file paths and its name.
/// Anything unclear is a Replacer, so a wrong guess costs a mod a place in the Replacer folder
/// rather than losing it among add-ons.
/// </summary>
public static class BodyPartClassifier
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly Regex SkeletonName = new(@"\b(skeleton|skel)\b", Opts);
    private static readonly Regex PhysicsName = new(@"\b(physics|jiggle|bounce)\b", Opts);
    private static readonly Regex HandsFeetName = new(@"\b(hands?|feet|foot|nails|toenails|claws)\b", Opts);

    public static BodyPart Classify(string name, IEnumerable<string>? gamePaths)
    {
        bool skeleton = false, physics = false, bodyModel = false;
        if (gamePaths is not null)
        {
            foreach (var raw in gamePaths)
            {
                var path = raw.Replace('\\', '/');
                if (path.EndsWith(".sklb", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".skp", StringComparison.OrdinalIgnoreCase))
                    skeleton = true;
                else if (path.EndsWith(".phyb", StringComparison.OrdinalIgnoreCase))
                    physics = true;
                else if (path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)
                         && path.Contains("/obj/body/", StringComparison.OrdinalIgnoreCase))
                    bodyModel = true;
            }
        }

        // A replacer often ships a skeleton beside its body models; the models win.
        if (bodyModel)
            return BodyPart.Replacer;
        if (skeleton && physics)
            return BodyPart.Replacer;
        if (skeleton)
            return BodyPart.Skeleton;
        if (physics)
            return BodyPart.Physics;

        var byName = new List<BodyPart>(3);
        if (SkeletonName.IsMatch(name)) byName.Add(BodyPart.Skeleton);
        if (PhysicsName.IsMatch(name)) byName.Add(BodyPart.Physics);
        if (HandsFeetName.IsMatch(name)) byName.Add(BodyPart.HandsAndFeet);
        return byName.Count == 1 ? byName[0] : BodyPart.Replacer;
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run the Step 2 command. Expected: all `BodyPartClassifierTests` PASS.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Classification/BodyPart.cs PenumbraOrganizer.Plugin/Organizer/Classification/BodyPartClassifier.cs PenumbraOrganizer.Plugin.Tests/Organizer/Classification/BodyPartClassifierTests.cs
git commit -m "feat: classify a body mod's part from its files and name

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Store the part on the row at scan time

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Classification/ModContentReader.cs`
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerModRow.cs`
- Modify: `PenumbraOrganizer.Plugin/LibraryWork/Pure/ScanProcessor.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModContentReaderTests.cs` (create if absent; if a file with tests for `ModContentReader` already exists, add to it instead — find with `grep -rl ModContentReader PenumbraOrganizer.Plugin.Tests`)

**Interfaces:**
- Consumes: `BodyPartClassifier.Classify` (Task 1).
- Produces: `ModContent.GamePaths` (`IReadOnlyList<string>?`, last positional parameter, default null); `OrganizerModRow.BodyPart` (`BodyPart`, `init`, default `Replacer`).

- [ ] **Step 1: Write the failing test** (a temp mod directory with one option group listing a `.sklb` key)

```csharp
using PenumbraOrganizer.Plugin.Organizer.Classification;
using Xunit;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public sealed class ModContentReaderGamePathTests : IDisposable
{
    private readonly DirectoryInfo _dir =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pm-" + Guid.NewGuid().ToString("N")));

    public void Dispose() => _dir.Delete(true);

    [Fact]
    public void Game_paths_are_the_file_keys()
    {
        File.WriteAllText(Path.Combine(_dir.FullName, "default_mod.json"),
            """{ "Files": { "chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb": "a.sklb" } }""");

        var content = ModContentReader.Read(_dir);

        Assert.NotNull(content);
        Assert.Equal(["chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb"], content!.GamePaths);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --nologo -v q --filter "FullyQualifiedName~ModContentReaderGamePathTests"`
Expected: build FAIL (`GamePaths` missing).

- [ ] **Step 3: Implement**

`ModContentReader.cs`: change the record and reader:
```csharp
public sealed record ModContent(
    IReadOnlySet<EquipmentSlot> Slots,
    ModHairFileReader.HairEvidence Hair,
    IReadOnlyList<string> GroupNames,
    IReadOnlyList<string> OptionNames,
    IReadOnlyList<string> FileValues,
    IReadOnlyList<string>? GamePaths = null);
```
In `Read`, add `var gamePaths = new List<string>();`, extend `OnFileKey`:
```csharp
OnFileKey = path =>
{
    gamePaths.Add(path);
    ModEquipmentFileReader.CollectFromFileKey(path, slots);
    ModHairFileReader.CollectFromFileKey(path, flat, byRace);
},
```
and the return: `new ModContent(slots, ModHairFileReader.Freeze(flat, byRace), groups, options, values, gamePaths)`.

`OrganizerModRow.cs` — add beside `BodyType`:
```csharp
/// <summary>
/// Which part of a body setup a Body-category mod is (replacer, skeleton, physics, hands and feet).
/// Set at scan time; used only when the body-mods split is on. Replacer for every other row.
/// </summary>
public Classification.BodyPart BodyPart { get; init; } = Classification.BodyPart.Replacer;
```

`ScanProcessor.cs`: before `return new OrganizerModRow`, add
```csharp
var bodyPart = classification.Category == ModCategory.Body
    ? BodyPartClassifier.Classify(item.Name, content?.GamePaths)
    : BodyPart.Replacer;
```
and `BodyPart = bodyPart,` in the initializer (after `BodyCandidates`). Add `using PenumbraOrganizer.Plugin.Organizer.Classification;` only if not already present.

- [ ] **Step 4: Run to verify pass**

Run the Step 2 command, then the whole suite: `dotnet test PenumbraOrganizer.Plugin.Tests --nologo -v q`. Expected: PASS; if an existing test constructs `ModContent` positionally with 5 args it still compiles (default param).

- [ ] **Step 5: Commit**

```bash
git add -A PenumbraOrganizer.Plugin PenumbraOrganizer.Plugin.Tests
git commit -m "feat: record a Body mod's part on its row at scan time

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 3: File Body mods under the part folder

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/SortFolderSelectors.cs` (the end of the local function `Type()`)
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/BodyPartSortTests.cs` (create)
- Modify (expectations only): any existing test failing because `splitBodyMods` + a resolved body now ends in `/Replacer`

**Interfaces:**
- Consumes: `OrganizerModRow.BodyPart`, `BodyPartFolders.Folder`.

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Classification;
using Xunit;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public sealed class BodyPartSortTests
{
    private static OrganizerModRow Row(ModCategory cat, string? body, BodyPart part) => new()
    {
        Identifier = "m", Name = "n", Author = "a", CurrentPath = "n", ProposedPath = "n",
        Category = cat, BodyType = body, BodyPart = part,
    };

    private static string? Type(OrganizerModRow row, bool splitBodyMods, Func<string, string>? rename = null) =>
        SortFolderSelectors.Select(SortStrategy.TypeOnly, false, false, row,
            renameFolder: rename, splitBodyMods: splitBodyMods).Primary;

    [Theory]
    [InlineData(BodyPart.Replacer, "Body/Rue+/Replacer")]
    [InlineData(BodyPart.Skeleton, "Body/Rue+/Skeleton")]
    [InlineData(BodyPart.Physics, "Body/Rue+/Physics")]
    [InlineData(BodyPart.HandsAndFeet, "Body/Rue+/Hands and Feet")]
    public void Body_mod_gets_its_part_folder_when_the_flag_is_on(BodyPart part, string expected) =>
        Assert.Equal(expected, Type(Row(ModCategory.Body, "Rue+", part), true));

    [Fact]
    public void Flag_off_leaves_the_flat_body_folder() =>
        Assert.Equal("Body", Type(Row(ModCategory.Body, "Rue+", BodyPart.Skeleton), false));

    [Fact]
    public void No_resolved_body_means_no_part_folder() =>
        Assert.Equal("Body", Type(Row(ModCategory.Body, null, BodyPart.Skeleton), true));

    [Fact]
    public void Gear_never_gets_a_part_folder() =>
        Assert.Equal("Gear", Type(Row(ModCategory.Gear, "Rue+", BodyPart.Skeleton), true));

    [Fact]
    public void Template_rename_applies_before_the_part_is_appended() =>
        Assert.Equal("Bodies/Rue+/Physics",
            Type(Row(ModCategory.Body, "Rue+", BodyPart.Physics), true,
                f => f.Replace("Body/", "Bodies/")));
}
```
(Adjust the `Row` initializer if `OrganizerModRow` has other required members: copy the set used in `ScanProcessor.Process`.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --nologo -v q --filter "FullyQualifiedName~BodyPartSortTests"`
Expected: the Body part theory and the rename test FAIL (no part folder appended); the other three PASS.

- [ ] **Step 3: Implement**

In `SortFolderSelectors.Type()`, replace the final `return TypeFolder(...)` with:
```csharp
var body = BodyFor(row, splitGearBody, splitBodyMods, bodyPriority);
var folder = TypeFolder(row.Category, sub, renameFolder, body);
// The part goes AFTER the template rename so a template's label for Body/<body> keeps matching.
return folder is not null && body is not null && row.Category == ModCategory.Body
    ? $"{folder}/{BodyPartFolders.Folder(row.BodyPart)}"
    : folder;
```

- [ ] **Step 4: Run to verify pass, then the full suite**

Run the Step 2 command (PASS), then `dotnet test PenumbraOrganizer.Plugin.Tests --nologo -v q`. For each pre-existing test that now fails only because a Body row with a resolved body and `splitBodyMods: true` ends in `/Replacer`, update its expected string deliberately (do not loosen assertions). Any other failure is a real bug: stop and report.

- [ ] **Step 5: Commit**

```bash
git add -A PenumbraOrganizer.Plugin PenumbraOrganizer.Plugin.Tests
git commit -m "feat: file Body mods under a part folder when splitting body mods

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Help text and docs for body parts

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Resources/help-content.json` (entry for the body-mods split, id found by `grep -n "split-body-mods" PenumbraOrganizer.Plugin/Resources/help-content.json`)
- Modify: `docs/HOW_SORTING_WORKS.md`, `docs/USER_GUIDE.md` (the "Split body mods" paragraphs; find with `grep -n -i "body mods" docs/HOW_SORTING_WORKS.md docs/USER_GUIDE.md`)

- [ ] **Step 1:** In the help entry's long text (keep the short text and the 200-character short limit unchanged), append: "With it on, each body also splits by part: Replacer, Skeleton, Physics and Hands and Feet. A mod that is not clearly a part stays in Replacer." In both docs add the same sentence plus the example `Body/Rue+/Skeleton/<mod>` next to the existing body-mods example.
- [ ] **Step 2:** Run the full suite (`dotnet test PenumbraOrganizer.Plugin.Tests --nologo -v q`): the help tests enforce length and topic usage. Expected PASS.
- [ ] **Step 3: Commit** `docs: describe body part folders` (with trailer).

---

### Task 5: ActiveCollectionBodyDetector (pure)

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Classification/ActiveCollectionBodyDetector.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ActiveCollectionBodyDetectorTests.cs`

**Interfaces:**
- Consumes: `BodyPriorityResolver.Resolve(BodyCandidates?, BodyPriority, bool nameFirst)` returning an object with `.Label`; `OrganizerModRow.BodyCandidates/BodyType/BodyPart/Category/Identifier`.
- Produces: `static string? ActiveCollectionBodyDetector.Detect(IEnumerable<OrganizerModRow> rows, IReadOnlySet<string> enabledIdentifiers, BodyPriority priority)`: the single body, or null for none or several.

- [ ] **Step 1: Write the failing tests**

```csharp
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

    private static HashSet<string> Enabled(params string[] ids) => [.. ids];

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
```
(Make `Enabled` return `HashSet<string>` built with `StringComparer.OrdinalIgnoreCase`: `new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase)`; the last test needs that comparer.)

- [ ] **Step 2: Run to verify failure** (`--filter "FullyQualifiedName~ActiveCollectionBodyDetectorTests"`): build FAIL, type missing.

- [ ] **Step 3: Implement**

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;

namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>
/// Which single body the enabled mods of a collection use. Only Body-category Replacer mods count:
/// gear and add-on parts say what they fit, not what the character wears.
/// </summary>
public static class ActiveCollectionBodyDetector
{
    public static string? Detect(
        IEnumerable<OrganizerModRow> rows, IReadOnlySet<string> enabledIdentifiers, BodyPriority priority)
    {
        var bodies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (row.Category != ModCategory.Body || row.BodyPart != BodyPart.Replacer)
                continue;
            if (!enabledIdentifiers.Contains(row.Identifier))
                continue;

            var body = row.BodyCandidates is not null
                ? BodyPriorityResolver.Resolve(row.BodyCandidates, priority, nameFirst: true)?.Label
                : row.BodyType;
            if (body is not null)
                bodies.Add(body);
        }
        return bodies.Count == 1 ? bodies.First() : null;
    }
}
```
If `Resolve`'s return type exposes the label differently, adapt to match `SortFolderSelectors.ResolveBody`.

- [ ] **Step 4: Run to verify pass.**
- [ ] **Step 5: Commit** `feat: detect the single body a collection uses` (with trailer).

---

### Task 6: Penumbra IPC reader for the enabled mods

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Operations/ActiveCollectionReader.cs`
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs` (next to `new Organizer.Operations.PenumbraOperationsAdapter(PluginInterface)`, line ~116)

**Interfaces:**
- Produces: `public sealed class ActiveCollectionReader(IDalamudPluginInterface pluginInterface)` with `IReadOnlySet<string>? ReadEnabledModDirectories()` (null when unavailable). `Plugin.ActiveCollectionReader` public property.

- [ ] **Step 1: Implement**

```csharp
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc.Exceptions;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary>
/// Reads which mods are enabled in the collection that affects the local player. Null when
/// Penumbra is missing or answers with anything but a usable collection. Nothing here writes.
/// </summary>
public sealed class ActiveCollectionReader
{
    private readonly GetCollectionForObject _forObject;
    private readonly GetCollection _current;
    private readonly GetAllModSettings _allSettings;

    public ActiveCollectionReader(IDalamudPluginInterface pluginInterface)
    {
        _forObject = new GetCollectionForObject(pluginInterface);
        _current = new GetCollection(pluginInterface);
        _allSettings = new GetAllModSettings(pluginInterface);
    }

    public IReadOnlySet<string>? ReadEnabledModDirectories()
    {
        try
        {
            var id = PlayerCollectionId() ?? CurrentCollectionId();
            if (id is null)
                return null;

            var (ec, settings) = _allSettings.Invoke(id.Value);
            if (ec != PenumbraApiEc.Success || settings is null)
                return null;

            return settings.Where(kv => kv.Value.Item1)
                .Select(kv => kv.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (IpcError)
        {
            return null;
        }
    }

    private Guid? PlayerCollectionId()
    {
        var (valid, _, collection) = _forObject.Invoke(0);
        return valid && collection.Id != Guid.Empty ? collection.Id : null;
    }

    private Guid? CurrentCollectionId()
    {
        var current = _current.Invoke(ApiCollectionType.Current);
        return current is { } c && c.Id != Guid.Empty ? c.Id : null;
    }
}
```
The exact tuple shapes of `Invoke` come from Penumbra.Api 5.15.1. Build, and if the compiler disagrees, fix the destructuring to the shapes it reports (the docs XML at `~/.nuget/packages/penumbra.api/5.15.1/lib/net10.0-windows7.0/Penumbra.Api.xml` describes them: `GetCollectionForObject` returns (valid, individual-assigned, (Guid Id, string Name)); `GetCollection` returns a nullable (Guid Id, string Name); `GetAllModSettings` returns (PenumbraApiEc, Dictionary<string, (bool Enabled, int Priority, Dictionary<string, List<string>> Settings, bool Inherited, bool Temporary)>?)). Do not change the public shape of `ReadEnabledModDirectories`.

- [ ] **Step 2:** In `Plugin.cs`, beside the operations adapter add a field/property and construct it with the same `PluginInterface`:
```csharp
public Organizer.Operations.ActiveCollectionReader ActiveCollectionReader { get; }
...
ActiveCollectionReader = new Organizer.Operations.ActiveCollectionReader(PluginInterface);
```
- [ ] **Step 3:** `dotnet build PenumbraOrganizer.Plugin --nologo -v q` Expected: 0 errors.
- [ ] **Step 4: Commit** `feat: read the enabled mods of the player's collection` (with trailer).

---

### Task 7: "Check my collection" in the Body priority section

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/SortPanel.cs` (`Draw` signature, `DrawBodyPriority`, two new fields)
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.SortTab.cs` (the `_sortPanel.Draw(` call)
- Modify: `PenumbraOrganizer.Plugin/Windows/HelpTopics.cs`, `HelpTopicUsage.cs`, `HelpTab.cs`, `PenumbraOrganizer.Plugin/Resources/help-content.json` (new topic `sort.check-collection`)

**Interfaces:**
- Consumes: `ActiveCollectionBodyDetector.Detect`, `ActiveCollectionReader.ReadEnabledModDirectories`, `BodyPriority.MoveToTop`, `OrganizerState.Mods`.

- [ ] **Step 1: Help topic.** Mirror `sort.first-choice-body` everywhere it appears (`grep -rn "first-choice-body\|SortFirstChoiceBody" PenumbraOrganizer.Plugin PenumbraOrganizer.Plugin.Tests`): add `HelpTopics.SortCheckCollection = new("sort.check-collection")`, list it in `HelpTopicUsage` and `HelpTab` next to the First-choice entry, and add a `help-content.json` entry with short text "Reads the mods enabled in your active collection and suggests that body as your first choice. Nothing changes unless you press Make first choice." (under 200 characters) and a longer paragraph saying it counts only body replacer mods, and shows nothing when your collection uses several bodies or none.
- [ ] **Step 2: Panel state.** In `SortPanel` add fields:
```csharp
private string? _collectionBody;      // the single body the last check found
private string? _collectionMessage;   // shown under the button after a check
```
Add a last parameter to `Draw`: `Func<IReadOnlySet<string>?>? readEnabledMods = null`, and pass it plus `state` to `DrawBodyPriority` (add parameters `OrganizerState state, Func<IReadOnlySet<string>?>? readEnabledMods`).
- [ ] **Step 3: UI.** In `DrawBodyPriority`, immediately after the First-choice combo block's `Help.Tooltip(...)`, add:
```csharp
if (readEnabledMods is not null)
{
    if (ImGui.SmallButton("Check my collection##check-collection"))
    {
        var enabled = readEnabledMods();
        if (enabled is null)
        {
            _collectionBody = null;
            _collectionMessage = "Could not read your active collection. Is Penumbra running?";
        }
        else
        {
            _collectionBody = ActiveCollectionBodyDetector.Detect(state.Mods, enabled, _bodyPriority);
            _collectionMessage = _collectionBody is null
                ? "Your active collection does not use exactly one body."
                : null;
        }
    }
    Help.Tooltip(HelpTopics.SortCheckCollection);

    if (_collectionBody is { } suggested)
    {
        if (string.Equals(FirstChoice(_bodyPriority), suggested, StringComparison.OrdinalIgnoreCase))
            ImGui.TextDisabled($"Your active collection uses {suggested}, already your first choice.");
        else
        {
            ImGui.TextUnformatted($"Your active collection uses {suggested}.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Make first choice##collection-first"))
            {
                SetBodyPriority(config, saveConfig, _bodyPriority.MoveToTop(suggested));
                _collectionBody = null;
            }
        }
    }
    else if (_collectionMessage is not null)
        ImGui.TextDisabled(_collectionMessage);
}
```
Keep it inside the existing `BeginDisabled(!states.FirstChoiceBody.Enabled)` scope so the activity gate applies.
- [ ] **Step 4: Wire.** In `MainWindow.SortTab.cs` append to the `_sortPanel.Draw(` call: `() => _plugin.ActiveCollectionReader.ReadEnabledModDirectories()`.
- [ ] **Step 5: Verify.** `dotnet build PenumbraOrganizer.Plugin --nologo -v q` (0 errors) then `dotnet test PenumbraOrganizer.Plugin.Tests --nologo -v q` (all PASS, including the help-topic tests).
- [ ] **Step 6: Commit** `feat: suggest the first-choice body from the active collection` (with trailer).

---

### Task 8: Docs, checklist, final run

**Files:**
- Modify: `docs/HOW_SORTING_WORKS.md`, `docs/USER_GUIDE.md` (one paragraph on Check my collection), `docs/ROADMAP.md` (move the two items from "not built" to "built, awaiting in-game check")
- Create: `docs/testing/2026-11-06-body-parts-checklist.md`

- [ ] **Step 1:** Write the checklist: (1) with Split body mods on, Rue+ add-ons land under Skeleton/Physics/Hands and Feet and the replacer under Replacer; counts per folder on the real library; (2) a replacer that ships a `.sklb` stays in Replacer; (3) flag off leaves `Body/<mod>`; (4) Check my collection with one body enabled shows it and Make first choice moves it to rank 1 and survives restart; (5) with two bodies enabled it says so; (6) which collection it reads (player vs Penumbra's Current) matches expectation when they differ; (7) Review shows all moves before Apply; (8) the First-choice dropdown after arrow moves and restart, and keyboard activation of the (?) popup (carried over, unverified).
- [ ] **Step 2:** Add the guide paragraph and ROADMAP edits.
- [ ] **Step 3:** `dotnet test PenumbraOrganizer.Plugin.Tests --nologo -v q` all PASS; `dotnet build PenumbraOrganizer.Plugin --nologo -v q -c Release` 0 errors.
- [ ] **Step 4: Commit** `docs: body parts and collection check (guide, roadmap, checklist)` (with trailer).
