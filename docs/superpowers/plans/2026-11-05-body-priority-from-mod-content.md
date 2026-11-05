# Body Detection From Mod Content + Saved Body Priority Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Detect a mod's body from its option groups, option names and file folders (then its name, then its library folder), and let the user keep one saved, reorderable body priority that decides which body a multi-body mod files under.

**Architecture:** One shared walk of each mod's config feeds the gear-slot, hair and new body logic. The scan stores per-tier body candidates on the row; the priority is applied at sort time (as the hair priority already is), so reordering never needs a rescan. The priority is an immutable, content-equal snapshot persisted in `Configuration` and edited in a collapsed section of the existing Sort tab.

**Tech Stack:** C# / .NET 10 (`net10.0-windows7.0`), Dalamud + ImGui bindings, xUnit.

Spec: `docs/superpowers/specs/2026-11-05-body-priority-from-mod-content-design.md` (read it first).

## Global Constraints

- Work in the worktree `C:\Repo\PenumbraOrganizer.Plugin\.claude\worktrees\body-type`, branch `claude/confident-brown-tvdcmm`. Verify the full path of every file you touch; do NOT edit the main checkout at `C:\Repo\PenumbraOrganizer.Plugin`.
- Run commands from the worktree root. Test command: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~<Name>"`. The plugin build needs the `..\..\PenumbraOrganizer` junction, which already exists.
- No `InternalsVisibleTo` in this repo: anything the test project must reach is `public`. `internal` is fine only for members called from non-test code in the same assembly.
- `LibraryWorkPurityTests` forbids Dalamud/Penumbra types in `LibraryWork/Pure`. New pure code (resolver, builder, content reader) must not reference them.
- Tier order, fixed: Tier 1 mod content (group names, option names, FIRST folder of mod-relative file values) -> Tier 2 mod name -> Tier 2.5 library folder path. A later tier is consulted only when every earlier tier found no body. User priority list breaks ties within a tier.
- The mod-level `Name` in `meta.json` is NEVER a group name. `Files` keys are game paths and never count; only values count, and only their first folder segment.
- Body-label matching: ordinal-ignore-case against the body list's own spelling. `MaxUserBodyCount` caps user-file bodies only, never the priority list.
- Non-ASCII hazard (see memory): build any non-ASCII test string from code points, never trust a pasted glyph. None is required by this plan.
- No version bump, no release notes.
- Commit trailer on every commit: `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.

## File Structure

| File | Change | Responsibility |
|---|---|---|
| `PenumbraOrganizer.Plugin/Organizer/Classification/BodyTypeMatcher.cs` | modify | add `MatchAll` |
| `PenumbraOrganizer.Plugin/Organizer/Classification/ModConfigFileWalker.cs` | modify | `ConfigVisitor`, names and file values |
| `PenumbraOrganizer.Plugin/Organizer/Classification/ModEquipmentFileReader.cs` | modify | collectors `internal` |
| `PenumbraOrganizer.Plugin/Organizer/Classification/ModHairFileReader.cs` | modify | collectors `internal`, `Freeze` |
| `PenumbraOrganizer.Plugin/Organizer/Classification/ModContentReader.cs` | create | the one shared walk |
| `PenumbraOrganizer.Plugin/Organizer/Classification/BodyCandidates.cs` | create | candidate model + `BodyCandidateBuilder` |
| `PenumbraOrganizer.Plugin/Organizer/BodyTypes/BodyPriority.cs` | create | immutable snapshot + normalization |
| `PenumbraOrganizer.Plugin/Organizer/Classification/BodyPriorityResolver.cs` | create | tier + priority pick |
| `PenumbraOrganizer.Plugin/LibraryWork/Pure/ScanProcessor.cs` | modify | one walk, candidates on the row |
| `PenumbraOrganizer.Plugin/Organizer/OrganizerModRow.cs` | modify | `BodyCandidates` |
| `PenumbraOrganizer.Plugin/Organizer/OrganizerExportFormatter.cs` | modify | `BodyEvidence:` dump line |
| `PenumbraOrganizer.Plugin/Organizer/SortFolderSelectors.cs`, `OrganizerState.cs` | modify | apply priority at sort time |
| `PenumbraOrganizer.Plugin/Configuration.cs` | modify | persisted priority, hair race, Midlander |
| `PenumbraOrganizer.Plugin/Windows/SortPanel.cs`, `MainWindow.SortTab.cs`, `Plugin.cs` | modify | the collapsed section, persistence, label source |
| Help: `HelpTopics.cs`, `HelpTopicUsage.cs`, `HelpTab.cs`, `Resources/help-content.json` | modify | new topic + updated split-body copy |
| `docs/USER_GUIDE.md`, `docs/HOW_SORTING_WORKS.md`, `docs/testing/...` | modify | user docs + manual checklist |

Tests go in `PenumbraOrganizer.Plugin.Tests/...` mirroring the source folders.

---

### Task 1: `BodyTypeMatcher.MatchAll`

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Classification/BodyTypeMatcher.cs` (after `Match`, ~line 90)
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/BodyTypeMatcherTests.cs`

**Interfaces:**
- Produces: `IReadOnlyList<string> BodyTypeMatcher.MatchAll(string text)` - every distinct body label whose alias appears in `text`, in longest-alias-first discovery order. An alias whose tokens overlap a token span already claimed by a longer alias is skipped, so "AB Eve" never also reports a body whose alias is just "Eve".

- [ ] **Step 1: Write the failing tests** (append inside `BodyTypeMatcherTests`; the file already has the `Entry` helper)

```csharp
    [Fact]
    public void MatchAll_ReturnsEveryDistinctBodyNamed()
    {
        var matcher = new BodyTypeMatcher([Entry("Bibo+", "bibo+", "bibo"), Entry("YAB", "yab", "yab+")]);

        var labels = matcher.MatchAll("Heartbeat for Bibo & YAB+");

        Assert.Equal(["Bibo+", "YAB"], labels.OrderBy(l => l, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void MatchAll_SameBodyTwice_ReportsItOnce()
    {
        var matcher = new BodyTypeMatcher([Entry("Bibo+", "bibo+", "bibo")]);

        Assert.Equal(["Bibo+"], matcher.MatchAll("Bibo+ and bibo outfit"));
    }

    [Fact]
    public void MatchAll_ShorterAliasInsideALongerOne_IsNotReportedAsASecondBody()
    {
        // "AB Eve" must not also read as the body whose alias is just "eve".
        var matcher = new BodyTypeMatcher([Entry("AB Eve", "ab eve"), Entry("Eve", "eve")]);

        Assert.Equal(["AB Eve"], matcher.MatchAll("AB Eve Top"));
        Assert.Equal(["Eve"], matcher.MatchAll("Eve Top"));
    }

    [Fact]
    public void MatchAll_NoTokensOrNoMatch_IsEmpty()
    {
        var matcher = new BodyTypeMatcher([Entry("Bibo+", "bibo+")]);

        Assert.Empty(matcher.MatchAll(""));
        Assert.Empty(matcher.MatchAll("Plain Robe"));
        Assert.Empty(BodyTypeMatcher.Empty.MatchAll("Bibo+"));
    }
```

- [ ] **Step 2: Run and see them fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~BodyTypeMatcherTests.MatchAll"`
Expected: build FAIL, `'BodyTypeMatcher' does not contain a definition for 'MatchAll'`.

- [ ] **Step 3: Implement** (insert after the `Match` method)

```csharp
    /// <summary>
    /// Every distinct body <paramref name="text"/> names. Same alias list, boundary rule and
    /// longest-alias-first order as <see cref="Match"/>; the difference is that it keeps going.
    /// </summary>
    /// <remarks>
    /// An alias is skipped when its tokens fall inside a span a longer alias already claimed, so a
    /// body whose alias is a word of another body's alias ("eve" in "ab eve") is not reported as a
    /// second body for the same words.
    /// </remarks>
    public IReadOnlyList<string> MatchAll(string text)
    {
        var tokens = Tokenize(Normalize(text));
        if (tokens.Length == 0)
            return [];

        var claimed = new bool[tokens.Length];
        var labels = new List<string>();

        foreach (var alias in _aliases)
        {
            if (labels.Contains(alias.Label))
                continue;

            for (var start = 0; start + alias.Tokens.Length <= tokens.Length; start++)
            {
                if (!MatchesAt(tokens, start, alias.Tokens))
                    continue;

                var overlaps = false;
                for (var i = start; i < start + alias.Tokens.Length; i++)
                    overlaps |= claimed[i];
                if (overlaps)
                    continue;

                for (var i = start; i < start + alias.Tokens.Length; i++)
                    claimed[i] = true;
                labels.Add(alias.Label);
                break;
            }
        }

        return labels;
    }
```

- [ ] **Step 4: Run and see them pass**

Run: same command as Step 2, then the whole class: `--filter "FullyQualifiedName~BodyTypeMatcherTests"`
Expected: all PASS (existing `Match_*` tests unchanged).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Classification/BodyTypeMatcher.cs PenumbraOrganizer.Plugin.Tests/Organizer/Classification/BodyTypeMatcherTests.cs
git commit -m "feat: BodyTypeMatcher.MatchAll reports every body a text names" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Walker reports group names, option names and file values

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Classification/ModConfigFileWalker.cs` (`TryWalk` lines 33-66, `WalkElement`/`WalkChildArray` lines 112-163)
- Create: `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModConfigFileWalkerNamesTests.cs`

**Interfaces:**
- Produces:
  - `public enum ConfigNameKind { Group, Option }`
  - `public sealed class ConfigVisitor { Action<string>? OnFileKey; Action<string>? OnFileValue; Action<JsonElement>? OnManipulation; Action<ConfigNameKind,string>? OnName }` (all `init`)
  - `ModConfigFileWalker.TryWalk(DirectoryInfo, ConfigVisitor)`; the old `TryWalk(DirectoryInfo, Action<string>, Action<JsonElement>)` stays and delegates.
- Names: group name from entries of `Groups` (meta.json) and from the root of `group_*.json`; option name from entries of `Options` and `Containers`. The root of `meta.json` and of `default_mod.json` is never a name. File values are `\`-to-`/` normalised, like keys.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text.Json;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class ModConfigFileWalkerNamesTests
{
    private static DirectoryInfo MakeTempModDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "PenumbraOrganizer.Plugin.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new DirectoryInfo(path);
    }

    private static void WriteJson(DirectoryInfo dir, string fileName, string json) =>
        File.WriteAllText(Path.Combine(dir.FullName, fileName), json);

    private static (bool Ok, List<string> Groups, List<string> Options, List<string> Keys, List<string> Values) Walk(DirectoryInfo dir)
    {
        var groups = new List<string>();
        var options = new List<string>();
        var keys = new List<string>();
        var values = new List<string>();
        var ok = ModConfigFileWalker.TryWalk(dir, new ConfigVisitor
        {
            OnFileKey = keys.Add,
            OnFileValue = values.Add,
            OnName = (kind, name) => (kind == ConfigNameKind.Group ? groups : options).Add(name),
        });
        return (ok, groups, options, keys, values);
    }

    [Fact]
    public void MetaJson_ReportsGroupAndOptionNames_ButNeverTheModTitle()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "meta.json", """
            {"Name":"Bibo+ Corset Gown","DefaultData":{"Files":{"a/b.mdl":"Base\\x.mdl"},"Manipulations":[]},
             "Groups":[{"Name":"Installation","Options":[{"Name":"Rue+","Files":{"c/d.mdl":"Rue+\\y.mdl"}},{"Name":"TBSE"}]}]}
            """);

        var (ok, groups, options, keys, values) = Walk(mod);

        Assert.True(ok);
        Assert.Equal(["Installation"], groups);
        Assert.Equal(["Rue+", "TBSE"], options);
        Assert.DoesNotContain("Bibo+ Corset Gown", groups.Concat(options));
        Assert.Equal(["a/b.mdl", "c/d.mdl"], keys.Order());
        Assert.Equal(["Base/x.mdl", "Rue+/y.mdl"], values.Order());
    }

    [Fact]
    public void LegacyLayout_GroupFileRootIsAGroup_DefaultModRootIsNot()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """{"Name":"Default Body","Files":{},"Manipulations":[]}""");
        WriteJson(mod, "group_001_install.json", """{"Name":"Install","Options":[{"Name":"YAB+","Files":{"k":"YAB+\\z.mdl"}}]}""");

        var (ok, groups, options, _, values) = Walk(mod);

        Assert.True(ok);
        Assert.Equal(["Install"], groups);
        Assert.Equal(["YAB+"], options);
        Assert.Equal(["YAB+/z.mdl"], values);
    }

    [Fact]
    public void ContainersCountAsOptionNames()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "meta.json", """
            {"Groups":[{"Name":"G","Containers":[{"Name":"Tight&Firm"}]}]}
            """);

        var (_, _, options, _, _) = Walk(mod);

        Assert.Equal(["Tight&Firm"], options);
    }

    [Fact]
    public void MissingOrNonStringNames_AreIgnored()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "meta.json", """{"Groups":[{"Options":[{"Name":5},{"Name":""},{}]}]}""");

        var (ok, groups, options, _, _) = Walk(mod);

        Assert.True(ok);
        Assert.Empty(groups);
        Assert.Empty(options);
    }

    [Fact]
    public void UnparseableConfig_ReturnsFalse()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "meta.json", "{ not json");

        Assert.False(Walk(mod).Ok);
    }

    [Fact]
    public void OldOverload_StillWalksKeysAndManipulations()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """{"Files":{"p/q.mdl":"r"},"Manipulations":[{"Type":"Eqp"}]}""");
        var keys = new List<string>();
        var manipulations = 0;

        var ok = ModConfigFileWalker.TryWalk(mod, keys.Add, _ => manipulations++);

        Assert.True(ok);
        Assert.Equal(["p/q.mdl"], keys);
        Assert.Equal(1, manipulations);
    }
}
```

- [ ] **Step 2: Run and see them fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~ModConfigFileWalkerNamesTests"`
Expected: build FAIL, `ConfigVisitor` / `ConfigNameKind` not found.

- [ ] **Step 3: Implement.** In `ModConfigFileWalker.cs`:

Add above the class:

```csharp
/// <summary>Whether a reported name belongs to an option group or to one option within it.</summary>
public enum ConfigNameKind { Group, Option }

/// <summary>What a walk reports. Every member is optional; an unset one is simply not called.</summary>
/// <remarks>
/// <see cref="OnFileKey"/> receives game paths (the keys of a Files object). <see cref="OnFileValue"/>
/// receives the mod-relative replacement paths (the values). Both are '/'-normalised. Names are the
/// option groups' and options' own, and never the mod's title: the root of meta.json and of
/// default_mod.json carries a name that is not one.
/// </remarks>
public sealed class ConfigVisitor
{
    public Action<string>? OnFileKey { get; init; }
    public Action<string>? OnFileValue { get; init; }
    public Action<JsonElement>? OnManipulation { get; init; }
    public Action<ConfigNameKind, string>? OnName { get; init; }
}
```

Replace the existing `TryWalk` signature body so the old overload delegates, and add the new one:

```csharp
    public static bool TryWalk(
        DirectoryInfo modDirectory,
        Action<string> onFileKey,
        Action<JsonElement> onManipulation) =>
        TryWalk(modDirectory, new ConfigVisitor { OnFileKey = onFileKey, OnManipulation = onManipulation });

    /// <inheritdoc cref="TryWalk(DirectoryInfo, Action{string}, Action{JsonElement})"/>
    public static bool TryWalk(DirectoryInfo modDirectory, ConfigVisitor visitor)
    {
        if (!modDirectory.Exists)
            return true;

        IReadOnlyList<FileInfo> configFiles;
        try
        {
            configFiles = DiscoverConfigFiles(modDirectory);
        }
        catch (Exception ex) when (IsExpectedFileSystemException(ex))
        {
            return false;
        }

        foreach (var file in configFiles)
        {
            try
            {
                using var stream = File.OpenRead(file.FullName);
                using var document = JsonDocument.Parse(stream);
                // Only a group_*.json ROOT is a group. meta.json's root is the mod itself and
                // default_mod.json's is the default option's container; neither name is a selection.
                var rootRole = file.Name.StartsWith("group_", StringComparison.OrdinalIgnoreCase)
                    ? ElementRole.Group
                    : ElementRole.None;
                WalkElement(document.RootElement, visitor, rootRole);
            }
            catch (Exception ex) when (IsExpectedFileSystemException(ex) || ex is JsonException)
            {
                return false;
            }
        }

        return true;
    }
```

(Keep the `catch` body exactly as the existing one - it returns `false`; copy the existing lines 59-63 rather than retyping if they differ.)

Replace `WalkElement` and `WalkChildArray` with:

```csharp
    private enum ElementRole { None, Group, Option }

    private static void WalkElement(JsonElement element, ConfigVisitor visitor, ElementRole role)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return; // guards every TryGetProperty call below against a non-object element

        if (role != ElementRole.None
            && visitor.OnName is { } onName
            && element.TryGetProperty("Name", out var nameProp)
            && nameProp.ValueKind == JsonValueKind.String
            && nameProp.GetString() is { Length: > 0 } name)
            onName(role == ElementRole.Group ? ConfigNameKind.Group : ConfigNameKind.Option, name);

        if (element.TryGetProperty("Files", out var files) && files.ValueKind == JsonValueKind.Object)
        {
            // Normalised here rather than in each reader: every consumer wants '/' separators, and
            // Penumbra writes some keys and values with '\'.
            foreach (var entry in files.EnumerateObject())
            {
                visitor.OnFileKey?.Invoke(entry.Name.Replace('\\', '/'));
                if (visitor.OnFileValue is { } onValue
                    && entry.Value.ValueKind == JsonValueKind.String
                    && entry.Value.GetString() is { } value)
                    onValue(value.Replace('\\', '/'));
            }
        }

        if (visitor.OnManipulation is { } onManipulation
            && element.TryGetProperty("Manipulations", out var manipulations)
            && manipulations.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in manipulations.EnumerateArray())
                onManipulation(item);
        }

        WalkChildArray(element, "Options", visitor, ElementRole.Option);
        WalkChildArray(element, "Containers", visitor, ElementRole.Option);

        // Penumbra 1.7.0's meta.json shape: the default option's Files/Manipulations live nested
        // one level under "DefaultData" (an object, not an array), and every group that used to be
        // its own group_*.json file is one entry in a top-level "Groups" array.
        if (element.TryGetProperty("DefaultData", out var defaultData)
            && defaultData.ValueKind == JsonValueKind.Object)
            WalkElement(defaultData, visitor, ElementRole.None);
        WalkChildArray(element, "Groups", visitor, ElementRole.Group);
    }

    private static void WalkChildArray(
        JsonElement element, string propertyName, ConfigVisitor visitor, ElementRole childRole)
    {
        if (!element.TryGetProperty(propertyName, out var children) || children.ValueKind != JsonValueKind.Array)
            return;

        // Genuinely recursive, so a future/unusual nesting is not silently dropped.
        foreach (var child in children.EnumerateArray())
            WalkElement(child, visitor, childRole);
    }
```

- [ ] **Step 4: Run new and existing reader tests**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~ModConfigFileWalkerNamesTests|FullyQualifiedName~ModEquipmentFileReaderTests|FullyQualifiedName~ModHairFileReaderTests"`
Expected: all PASS (the old overload and both readers are untouched in behaviour).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Classification/ModConfigFileWalker.cs PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModConfigFileWalkerNamesTests.cs
git commit -m "feat: config walker reports group names, option names and file values" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 3: `ModContentReader` - one shared walk

**Files:**
- Modify: `ModEquipmentFileReader.cs` (make `CollectFromFileKey` and `CollectFromManipulation` `internal static`)
- Modify: `ModHairFileReader.cs` (make `CollectFromFileKey` and `CollectFromManipulation` `internal static`; add `internal static HairEvidence Freeze(...)`; have `ReadHairEvidence` call it)
- Create: `PenumbraOrganizer.Plugin/Organizer/Classification/ModContentReader.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModContentReaderTests.cs`

**Interfaces:**
- Consumes: `ConfigVisitor`, `ModConfigFileWalker.TryWalk(DirectoryInfo, ConfigVisitor)`.
- Produces: `public sealed record ModContent(IReadOnlySet<EquipmentSlot> Slots, ModHairFileReader.HairEvidence Hair, IReadOnlyList<string> GroupNames, IReadOnlyList<string> OptionNames, IReadOnlyList<string> FileValues)` and `public static ModContent? ModContentReader.Read(DirectoryInfo modDirectory)` - null means the walk could not be trusted (same contract as the readers). A single `TryWalk` call, so each config file is opened once per mod.

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class ModContentReaderTests
{
    private static DirectoryInfo MakeTempModDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "PenumbraOrganizer.Plugin.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new DirectoryInfo(path);
    }

    private const string Config = """
        {"DefaultData":{"Files":{
            "chara/equipment/e0001/model/c0101e0001_top.mdl":"Rue+\\top.mdl",
            "chara/human/c0101/obj/hair/h0115/model/c0101h0115_hir.mdl":"hair.mdl"},"Manipulations":[]},
         "Groups":[{"Name":"Body","Options":[{"Name":"Bibo+"}]}]}
        """;

    [Fact]
    public void Read_AgreesWithTheTwoStandaloneReaders()
    {
        var mod = MakeTempModDirectory();
        File.WriteAllText(Path.Combine(mod.FullName, "meta.json"), Config);

        var content = ModContentReader.Read(mod);

        Assert.NotNull(content);
        Assert.Equal(ModEquipmentFileReader.ReadEquipmentSlots(mod), content!.Slots);
        Assert.Equal([EquipmentSlot.Top], content.Slots);
        Assert.Equal(ModHairFileReader.ReadHairEvidence(mod)!.Flat, content.Hair.Flat);
        Assert.Equal(["Body"], content.GroupNames);
        Assert.Equal(["Bibo+"], content.OptionNames);
        Assert.Contains("Rue+/top.mdl", content.FileValues);
    }

    [Fact]
    public void Read_UnparseableConfig_IsNull()
    {
        var mod = MakeTempModDirectory();
        File.WriteAllText(Path.Combine(mod.FullName, "meta.json"), "{ nope");

        Assert.Null(ModContentReader.Read(mod));
    }

    [Fact]
    public void Read_DirectoryWithNoConfig_IsEmptyNotNull()
    {
        var content = ModContentReader.Read(MakeTempModDirectory());

        Assert.NotNull(content);
        Assert.Empty(content!.Slots);
        Assert.Empty(content.GroupNames);
    }
}
```

(`EquipmentSlot.Top` is the enum member used throughout; if the enum spells it differently, mirror `ModEquipmentFileReaderTests`.)

- [ ] **Step 2: Run and see them fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~ModContentReaderTests"`
Expected: build FAIL, `ModContentReader` not found.

- [ ] **Step 3: Implement**

In `ModEquipmentFileReader.cs` change `private static void CollectFromFileKey(` and `private static void CollectFromManipulation(` to `internal static void ...`. In `ModHairFileReader.cs` do the same for its two `Collect*` methods, and replace the tail of `ReadHairEvidence` so the freeze is shared:

```csharp
        if (!ok)
            return null;

        return Freeze(flat, byRace);
    }

    internal static HairEvidence Freeze(HashSet<int> flat, Dictionary<string, HashSet<int>> byRace)
    {
        var frozen = byRace.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlySet<int>)pair.Value,
            StringComparer.Ordinal);
        return new HairEvidence(flat, frozen);
    }
```

Create `ModContentReader.cs`:

```csharp
using PenumbraOrganizer.Core.Classification;

namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>Everything the scan reads out of one mod's config, from one walk.</summary>
public sealed record ModContent(
    IReadOnlySet<EquipmentSlot> Slots,
    ModHairFileReader.HairEvidence Hair,
    IReadOnlyList<string> GroupNames,
    IReadOnlyList<string> OptionNames,
    IReadOnlyList<string> FileValues);

/// <summary>
/// One walk per mod feeding the equipment-slot, hair and body logic. Before this, each reader
/// walked the mod's config itself; adding a third would have read a Gear mod's files twice.
/// </summary>
/// <remarks>
/// Null means the walk could not be trusted and everything collected is discarded - the contract
/// <see cref="ModConfigFileWalker.TryWalk(DirectoryInfo, ConfigVisitor)"/> imposes on every caller.
/// </remarks>
public static class ModContentReader
{
    public static ModContent? Read(DirectoryInfo modDirectory)
    {
        var slots = new HashSet<EquipmentSlot>();
        var flat = new HashSet<int>();
        var byRace = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var groups = new List<string>();
        var options = new List<string>();
        var values = new List<string>();

        var ok = ModConfigFileWalker.TryWalk(modDirectory, new ConfigVisitor
        {
            OnFileKey = path =>
            {
                ModEquipmentFileReader.CollectFromFileKey(path, slots);
                ModHairFileReader.CollectFromFileKey(path, flat, byRace);
            },
            OnFileValue = values.Add,
            OnManipulation = item =>
            {
                ModEquipmentFileReader.CollectFromManipulation(item, slots);
                ModHairFileReader.CollectFromManipulation(item, flat);
            },
            OnName = (kind, name) => (kind == ConfigNameKind.Group ? groups : options).Add(name),
        });

        return ok ? new ModContent(slots, ModHairFileReader.Freeze(flat, byRace), groups, options, values) : null;
    }
}
```

- [ ] **Step 4: Run**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~ModContentReaderTests|FullyQualifiedName~ModEquipmentFileReaderTests|FullyQualifiedName~ModHairFileReaderTests"`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Classification PenumbraOrganizer.Plugin.Tests/Organizer/Classification/ModContentReaderTests.cs
git commit -m "refactor: one shared config walk for slots, hair and body evidence" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Body candidates and the tier builder

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Classification/BodyCandidates.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/BodyCandidateBuilderTests.cs`

**Interfaces:**
- Consumes: `BodyTypeMatcher.MatchAll`, `ModContent`.
- Produces:
  - `enum BodyEvidenceSource { Group, Option, FilePath, Name, LibraryPath }`
  - `readonly record struct BodyCandidate(string Label, BodyEvidenceSource Source)`
  - `sealed class BodyCandidates { IReadOnlyList<BodyCandidate> Content, Name, LibraryPath (init, default []); bool ContentReadFailed; string Describe() }`
  - `static BodyCandidates BodyCandidateBuilder.Build(BodyTypeMatcher matcher, string modName, string libraryPath, ModContent? content, bool contentReadFailed)`
  - `static string? BodyCandidateBuilder.FirstFolder(string modRelativePath)`
- Rules: Tier 1 = group names, option names, then `FirstFolder` of each file value; one entry per label (first source wins, order Group, Option, FilePath). Tier 2 = `MatchAll(modName)`. Tier 2.5 = `MatchAll` of every folder segment of `libraryPath` EXCEPT the last (the mod's own name, which is Tier 2). `FirstFolder` returns null for a value with no `/` or an empty first segment.

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class BodyCandidateBuilderTests
{
    private static readonly BodyTypeMatcher Matcher = new(
    [
        new BodyTypeEntry { Label = "Bibo+", Aliases = ["bibo+", "bibo"] },
        new BodyTypeEntry { Label = "Rue+", Aliases = ["rue+", "rue"] },
        new BodyTypeEntry { Label = "YAB", Aliases = ["yab", "yab+"] },
        new BodyTypeEntry { Label = "TBSE", Aliases = ["tbse"] },
    ]);

    private static ModContent Content(string[]? groups = null, string[]? options = null, string[]? values = null) =>
        new(new HashSet<EquipmentSlot>(),
            new ModHairFileReader.HairEvidence(new HashSet<int>(),
                new Dictionary<string, IReadOnlySet<int>>()),
            groups ?? [], options ?? [], values ?? []);

    private static string[] Labels(IReadOnlyList<BodyCandidate> list) => [.. list.Select(c => c.Label)];

    [Fact]
    public void Content_FindsBodiesInGroupAndOptionNames()
    {
        var result = BodyCandidateBuilder.Build(Matcher, "Corset", "Corset",
            Content(groups: ["Installation (Bibo+)"], options: ["Rue+", "TBSE"]), contentReadFailed: false);

        Assert.Equal(["Bibo+", "Rue+", "TBSE"], Labels(result.Content));
        Assert.Equal(BodyEvidenceSource.Group, result.Content[0].Source);
        Assert.Equal(BodyEvidenceSource.Option, result.Content[1].Source);
    }

    [Theory]
    [InlineData("Prestige YAB+/top.mdl", true)]
    [InlineData("Bibo+/chest.tex", true)]
    [InlineData("shared/Bibo/file.tex", false)]
    [InlineData("common/textures/rue.tex", false)]
    [InlineData("rue_top.mdl", false)]
    public void FilePathRule_OnlyTheFirstFolderCounts(string value, bool counts)
    {
        var result = BodyCandidateBuilder.Build(Matcher, "X", "X", Content(values: [value]), false);

        Assert.Equal(counts, result.Content.Count == 1);
        if (counts)
            Assert.Equal(BodyEvidenceSource.FilePath, result.Content[0].Source);
    }

    [Fact]
    public void FirstFolder_NoFolderOrEmptyFirstSegment_IsNull()
    {
        Assert.Equal("Rue+", BodyCandidateBuilder.FirstFolder("Rue+/a/b.mdl"));
        Assert.Null(BodyCandidateBuilder.FirstFolder("file.mdl"));
        Assert.Null(BodyCandidateBuilder.FirstFolder("/file.mdl"));
    }

    [Fact]
    public void SameBodyFromSeveralSources_IsOneCandidate_FirstSourceWins()
    {
        var result = BodyCandidateBuilder.Build(Matcher, "X", "X",
            Content(groups: ["Rue+"], options: ["Rue+"], values: ["Rue+/a.mdl"]), false);

        Assert.Equal(["Rue+"], Labels(result.Content));
        Assert.Equal(BodyEvidenceSource.Group, result.Content[0].Source);
    }

    [Fact]
    public void ModTitleInsideContent_IsNotATier1Signal()
    {
        // The walker never reports the title; this pins that the builder does not invent it either.
        var result = BodyCandidateBuilder.Build(Matcher, "Bibo+ Corset Gown", "Bibo+ Corset Gown", Content(), false);

        Assert.Empty(result.Content);
        Assert.Equal(["Bibo+"], Labels(result.Name));
    }

    [Fact]
    public void LibraryPath_UsesFoldersButNotTheModsOwnLeaf()
    {
        var result = BodyCandidateBuilder.Build(Matcher, "Cool Dress", "Heliosphere/Bibo+ Stuff/Cool Dress", Content(), false);

        Assert.Equal(["Bibo+"], Labels(result.LibraryPath));
        Assert.Equal(BodyEvidenceSource.LibraryPath, result.LibraryPath[0].Source);

        var leafOnly = BodyCandidateBuilder.Build(Matcher, "Rue+ Dress", "Rue+ Dress", Content(), false);
        Assert.Empty(leafOnly.LibraryPath);
    }

    [Fact]
    public void LibraryPath_AcceptsBackslashSeparators()
    {
        var result = BodyCandidateBuilder.Build(Matcher, "X", "Folder\\YAB\\X", Content(), false);

        Assert.Equal(["YAB"], Labels(result.LibraryPath));
    }

    [Fact]
    public void NullContent_FlagsFailureOnlyWhenTheReadActuallyFailed()
    {
        var failed = BodyCandidateBuilder.Build(Matcher, "X", "X", null, contentReadFailed: true);
        var notRead = BodyCandidateBuilder.Build(Matcher, "X", "X", null, contentReadFailed: false);

        Assert.True(failed.ContentReadFailed);
        Assert.False(notRead.ContentReadFailed);
        Assert.Empty(failed.Content);
    }

    [Fact]
    public void Describe_ListsEveryTierAndTheReadStatus()
    {
        var result = BodyCandidateBuilder.Build(Matcher, "TBSE Top", "A/B", Content(options: ["Rue+"]), false);

        Assert.Equal("content=[Rue+ (Option)] name=[TBSE (Name)] library=[] contentRead=ok", result.Describe());
        Assert.Contains("contentRead=FAILED",
            BodyCandidateBuilder.Build(Matcher, "X", "X", null, true).Describe());
    }
}
```

- [ ] **Step 2: Run and see them fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~BodyCandidateBuilderTests"`
Expected: build FAIL, types not found.

- [ ] **Step 3: Implement** `BodyCandidates.cs`

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>Where a body candidate came from. Recorded so a dump can explain a placement.</summary>
public enum BodyEvidenceSource { Group, Option, FilePath, Name, LibraryPath }

public readonly record struct BodyCandidate(string Label, BodyEvidenceSource Source);

/// <summary>
/// Every body a mod could be filed under, by tier. Stored on the row at scan time and resolved
/// against the user's priority at SORT time, exactly as hair evidence is - so reordering the
/// priority never needs a rescan.
/// </summary>
public sealed class BodyCandidates
{
    /// <summary>Tier 1: inside the mod - group names, option names, first folder of file values.</summary>
    public IReadOnlyList<BodyCandidate> Content { get; init; } = [];

    /// <summary>Tier 2: the mod's own name.</summary>
    public IReadOnlyList<BodyCandidate> Name { get; init; } = [];

    /// <summary>Tier 2.5: the folders above the mod in the library. Least reliable.</summary>
    public IReadOnlyList<BodyCandidate> LibraryPath { get; init; } = [];

    /// <summary>The mod's config could not be read. Distinct from "read fine, found no body".</summary>
    public bool ContentReadFailed { get; init; }

    public string Describe() =>
        $"content=[{Join(Content)}] name=[{Join(Name)}] library=[{Join(LibraryPath)}] "
        + $"contentRead={(ContentReadFailed ? "FAILED" : "ok")}";

    private static string Join(IReadOnlyList<BodyCandidate> list) =>
        string.Join(", ", list.Select(c => $"{c.Label} ({c.Source})"));
}

public static class BodyCandidateBuilder
{
    public static BodyCandidates Build(
        BodyTypeMatcher matcher, string modName, string libraryPath,
        ModContent? content, bool contentReadFailed)
    {
        var tier1 = new List<BodyCandidate>();
        if (content is not null)
        {
            foreach (var name in content.GroupNames)
                AddAll(tier1, matcher.MatchAll(name), BodyEvidenceSource.Group);
            foreach (var name in content.OptionNames)
                AddAll(tier1, matcher.MatchAll(name), BodyEvidenceSource.Option);
            foreach (var value in content.FileValues)
            {
                if (FirstFolder(value) is { } folder)
                    AddAll(tier1, matcher.MatchAll(folder), BodyEvidenceSource.FilePath);
            }
        }

        var tier2 = new List<BodyCandidate>();
        AddAll(tier2, matcher.MatchAll(modName), BodyEvidenceSource.Name);

        var tier25 = new List<BodyCandidate>();
        var segments = libraryPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        // Every folder ABOVE the mod; the last segment is the mod's own name, which Tier 2 covers.
        for (var i = 0; i < segments.Length - 1; i++)
            AddAll(tier25, matcher.MatchAll(segments[i]), BodyEvidenceSource.LibraryPath);

        return new BodyCandidates
        {
            Content = tier1,
            Name = tier2,
            LibraryPath = tier25,
            ContentReadFailed = contentReadFailed && content is null,
        };
    }

    /// <summary>
    /// The first folder of a mod-relative path, or null when there is none. Authors who ship
    /// per-body variants name the TOP folder ("Prestige YAB+/top.mdl"); a body name deeper down
    /// ("shared/Bibo/file.tex") or in a root file name is too noisy to count.
    /// </summary>
    public static string? FirstFolder(string modRelativePath)
    {
        var normalized = modRelativePath.Replace('\\', '/');
        var slash = normalized.IndexOf('/');
        return slash <= 0 ? null : normalized[..slash];
    }

    // One candidate per label within a tier; the first source to name a body keeps the credit.
    private static void AddAll(List<BodyCandidate> into, IReadOnlyList<string> labels, BodyEvidenceSource source)
    {
        foreach (var label in labels)
        {
            if (!into.Exists(c => c.Label == label))
                into.Add(new BodyCandidate(label, source));
        }
    }
}
```

- [ ] **Step 4: Run**

Run: same filter as Step 2. Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Classification/BodyCandidates.cs PenumbraOrganizer.Plugin.Tests/Organizer/Classification/BodyCandidateBuilderTests.cs
git commit -m "feat: per-tier body candidates built from mod content, name and library path" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 5: `BodyPriority` snapshot and normalization

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/BodyTypes/BodyPriority.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/BodyTypes/BodyPriorityTests.cs`

**Interfaces:**
- Produces: `public sealed class BodyPriority : IEquatable<BodyPriority>` with
  - `IReadOnlyList<string> Labels`
  - `static BodyPriority Normalize(IEnumerable<string>? saved, IEnumerable<string> allLabels)`
  - `static BodyPriority Default(IEnumerable<string> allLabels)`
  - `int Rank(string label)` (`int.MaxValue` when unknown; ordinal-ignore-case)
  - `BodyPriority MoveUp(int index)`, `BodyPriority MoveDown(int index)` (return `this` for an out-of-range or edge move)
  - content `Equals`/`GetHashCode`/`==`/`!=`.
- Normalization: canonical spelling is the body list's own; duplicates keep the first occurrence; labels not in `allLabels` dropped; missing bodies appended in `allLabels` order; null/empty `saved` gives the default; null/blank entries skipped.

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.BodyTypes;

public class BodyPriorityTests
{
    private static readonly string[] All = ["Bibo+", "Rue+", "TBSE", "YAB"];

    [Fact]
    public void Default_IsTheBodyListOrder() =>
        Assert.Equal(All, BodyPriority.Default(All).Labels);

    [Fact]
    public void Normalize_NullOrEmptySaved_FallsBackToDefault()
    {
        Assert.Equal(All, BodyPriority.Normalize(null, All).Labels);
        Assert.Equal(All, BodyPriority.Normalize([], All).Labels);
    }

    [Fact]
    public void Normalize_KeepsSavedOrder_AppendsMissingBodiesAtTheEnd()
    {
        var p = BodyPriority.Normalize(["YAB", "Bibo+"], All);

        Assert.Equal(["YAB", "Bibo+", "Rue+", "TBSE"], p.Labels);
    }

    [Fact]
    public void Normalize_DropsStaleLabels_IgnoresCase_UsesCanonicalSpelling()
    {
        var p = BodyPriority.Normalize(["yab", "GoneBody", "RUE+"], All);

        Assert.Equal(["YAB", "Rue+", "Bibo+", "TBSE"], p.Labels);
    }

    [Fact]
    public void Normalize_DuplicatesKeepTheFirstOccurrence_BlankEntriesSkipped()
    {
        var p = BodyPriority.Normalize(["TBSE", "", "  ", "tbse", "TBSE", null!], All);

        Assert.Equal(["TBSE", "Bibo+", "Rue+", "YAB"], p.Labels);
    }

    [Fact]
    public void Normalize_DuplicateLabelsInTheBodyList_AreCollapsed()
    {
        var p = BodyPriority.Default(["Bibo+", "bibo+", "Rue+"]);

        Assert.Equal(["Bibo+", "Rue+"], p.Labels);
    }

    [Fact]
    public void Normalize_IsNotCappedAtTheUserBodyLimit()
    {
        var many = Enumerable.Range(0, BodyTypeListStore.MaxUserBodyCount + 30).Select(i => $"Body{i}").ToArray();

        Assert.Equal(many.Length, BodyPriority.Default(many).Labels.Count);
    }

    [Fact]
    public void Rank_IsPositionInTheList_UnknownIsLast()
    {
        var p = BodyPriority.Normalize(["YAB"], All);

        Assert.Equal(0, p.Rank("yab"));
        Assert.Equal(1, p.Rank("Bibo+"));
        Assert.Equal(int.MaxValue, p.Rank("Nope"));
    }

    [Fact]
    public void Equality_IsByContent_NotByReference()
    {
        var a = BodyPriority.Normalize(["YAB"], All);
        var b = BodyPriority.Normalize(["yab"], All);

        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a != a.MoveDown(0));
    }

    [Fact]
    public void Move_ReordersWithoutMutating_AndEdgesAreNoOps()
    {
        var p = BodyPriority.Default(All);

        Assert.Equal(["Rue+", "Bibo+", "TBSE", "YAB"], p.MoveDown(0).Labels);
        Assert.Equal(["Rue+", "Bibo+", "TBSE", "YAB"], p.MoveUp(1).Labels);
        Assert.Same(p, p.MoveUp(0));
        Assert.Same(p, p.MoveDown(3));
        Assert.Equal(All, p.Labels);
    }

    [Fact]
    public void ResetToDefault_RestoresEqualityWithTheDefault()
    {
        var moved = BodyPriority.Default(All).MoveDown(0);

        Assert.True(moved != BodyPriority.Default(All));
        Assert.True(BodyPriority.Normalize(null, All) == BodyPriority.Default(All));
    }
}
```

- [ ] **Step 2: Run and see them fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~BodyPriorityTests"`
Expected: build FAIL, `BodyPriority` not found.

- [ ] **Step 3: Implement**

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.BodyTypes;

/// <summary>
/// The user's body ranking, first is highest. An immutable snapshot with CONTENT equality.
/// </summary>
/// <remarks>
/// It rides inside SortSelection, which SortPanel compares with <c>!=</c> to decide whether the last
/// sort is stale. A plain List would compare by reference: mutating it in place would never trip
/// the hint, and allocating a fresh one every frame would trip it permanently. Content equality
/// makes a reorder differ and an unchanged list not, and Reset equal the default again.
/// </remarks>
public sealed class BodyPriority : IEquatable<BodyPriority>
{
    private readonly string[] _labels;
    private readonly Dictionary<string, int> _rank;

    private BodyPriority(string[] labels)
    {
        _labels = labels;
        _rank = new Dictionary<string, int>(labels.Length, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < labels.Length; i++)
            _rank[labels[i]] = i;
    }

    public IReadOnlyList<string> Labels => _labels;

    public static BodyPriority Default(IEnumerable<string> allLabels) => Normalize(null, allLabels);

    /// <summary>
    /// Repairs a saved list against the current body list: canonical spelling from the body list,
    /// first occurrence wins, unknown labels dropped, missing bodies appended in list order.
    /// </summary>
    public static BodyPriority Normalize(IEnumerable<string>? saved, IEnumerable<string> allLabels)
    {
        var canonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var label in allLabels)
        {
            if (!string.IsNullOrWhiteSpace(label) && canonical.TryAdd(label, label))
                order.Add(label);
        }

        var result = new List<string>(order.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (saved is not null)
        {
            foreach (var entry in saved)
            {
                if (string.IsNullOrWhiteSpace(entry) || !canonical.TryGetValue(entry, out var spelled))
                    continue;
                if (seen.Add(spelled))
                    result.Add(spelled);
            }
        }

        foreach (var label in order)
        {
            if (seen.Add(label))
                result.Add(label);
        }

        return new BodyPriority([.. result]);
    }

    /// <summary>Position in the list, or <see cref="int.MaxValue"/> for a label not in it.</summary>
    public int Rank(string label) => _rank.TryGetValue(label, out var rank) ? rank : int.MaxValue;

    public BodyPriority MoveUp(int index) => Swap(index, index - 1);

    public BodyPriority MoveDown(int index) => Swap(index, index + 1);

    private BodyPriority Swap(int from, int to)
    {
        if (from < 0 || from >= _labels.Length || to < 0 || to >= _labels.Length)
            return this;

        var copy = (string[])_labels.Clone();
        (copy[from], copy[to]) = (copy[to], copy[from]);
        return new BodyPriority(copy);
    }

    public bool Equals(BodyPriority? other) =>
        other is not null && _labels.AsSpan().SequenceEqual(other._labels);

    public override bool Equals(object? obj) => Equals(obj as BodyPriority);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var label in _labels)
            hash.Add(label, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    public static bool operator ==(BodyPriority? left, BodyPriority? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(BodyPriority? left, BodyPriority? right) => !(left == right);
}
```

- [ ] **Step 4: Run.** Same filter. Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/BodyTypes/BodyPriority.cs PenumbraOrganizer.Plugin.Tests/Organizer/BodyTypes/BodyPriorityTests.cs
git commit -m "feat: immutable body priority snapshot with normalization" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 6: `BodyPriorityResolver`

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Classification/BodyPriorityResolver.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Classification/BodyPriorityResolverTests.cs`

**Interfaces:**
- Consumes: `BodyCandidates`, `BodyPriority`.
- Produces: `enum BodyEvidenceTier { Content, Name, LibraryPath }`, `sealed record BodyResolution(string Label, BodyEvidenceTier Tier, BodyEvidenceSource Source)`, `static BodyResolution? BodyPriorityResolver.Resolve(BodyCandidates? candidates, BodyPriority priority)`.
- Rule: first tier (Content, then Name, then LibraryPath) with any candidate; within it the lowest `Rank`; a rank tie (labels unknown to the list) breaks by ordinal label so the answer never depends on input order.

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class BodyPriorityResolverTests
{
    private static readonly string[] All = ["Bibo+", "Rue+", "TBSE", "YAB"];

    private static BodyCandidate C(string label, BodyEvidenceSource source = BodyEvidenceSource.Option) => new(label, source);

    private static BodyPriority Order(params string[] saved) => BodyPriority.Normalize(saved, All);

    [Fact]
    public void NullCandidates_NoAnswer() =>
        Assert.Null(BodyPriorityResolver.Resolve(null, Order()));

    [Fact]
    public void SingleCandidate_Wins()
    {
        var r = BodyPriorityResolver.Resolve(new BodyCandidates { Content = [C("TBSE")] }, Order());

        Assert.Equal(new BodyResolution("TBSE", BodyEvidenceTier.Content, BodyEvidenceSource.Option), r);
    }

    [Fact]
    public void SeveralCandidates_HighestRankedWins_AndReorderingChangesTheWinner()
    {
        var candidates = new BodyCandidates { Content = [C("Rue+"), C("TBSE")] };

        Assert.Equal("Rue+", BodyPriorityResolver.Resolve(candidates, Order("Rue+"))!.Label);
        Assert.Equal("TBSE", BodyPriorityResolver.Resolve(candidates, Order("TBSE"))!.Label);
    }

    [Fact]
    public void ContentOutranksName_EvenWhenTheNamedBodyRanksHigher()
    {
        var candidates = new BodyCandidates
        {
            Content = [C("Rue+")],
            Name = [C("Bibo+", BodyEvidenceSource.Name)],
        };

        var r = BodyPriorityResolver.Resolve(candidates, Order("Bibo+"))!;

        Assert.Equal("Rue+", r.Label);
        Assert.Equal(BodyEvidenceTier.Content, r.Tier);
    }

    [Fact]
    public void FallsThroughContentThenNameThenLibraryPath()
    {
        var nameOnly = new BodyCandidates { Name = [C("YAB", BodyEvidenceSource.Name)] };
        var pathOnly = new BodyCandidates { LibraryPath = [C("Bibo+", BodyEvidenceSource.LibraryPath)] };

        Assert.Equal(BodyEvidenceTier.Name, BodyPriorityResolver.Resolve(nameOnly, Order())!.Tier);
        Assert.Equal(BodyEvidenceTier.LibraryPath, BodyPriorityResolver.Resolve(pathOnly, Order())!.Tier);
    }

    [Fact]
    public void NameBeatsLibraryPath()
    {
        var both = new BodyCandidates
        {
            Name = [C("YAB", BodyEvidenceSource.Name)],
            LibraryPath = [C("Bibo+", BodyEvidenceSource.LibraryPath)],
        };

        Assert.Equal("YAB", BodyPriorityResolver.Resolve(both, Order("Bibo+"))!.Label);
    }

    [Fact]
    public void NothingFound_NoAnswer_EvenWhenTheReadFailed()
    {
        Assert.Null(BodyPriorityResolver.Resolve(new BodyCandidates(), Order()));
        Assert.Null(BodyPriorityResolver.Resolve(new BodyCandidates { ContentReadFailed = true }, Order()));
    }

    [Fact]
    public void FailedContentRead_StillUsesTheNameTier()
    {
        var candidates = new BodyCandidates
        {
            ContentReadFailed = true,
            Name = [C("Bibo+", BodyEvidenceSource.Name)],
        };

        Assert.Equal("Bibo+", BodyPriorityResolver.Resolve(candidates, Order())!.Label);
    }

    [Fact]
    public void LabelsUnknownToThePriority_TieBreakByOrdinalLabel_NotInputOrder()
    {
        var a = new BodyCandidates { Content = [C("Zeta"), C("Alpha")] };
        var b = new BodyCandidates { Content = [C("Alpha"), C("Zeta")] };

        Assert.Equal("Alpha", BodyPriorityResolver.Resolve(a, Order())!.Label);
        Assert.Equal("Alpha", BodyPriorityResolver.Resolve(b, Order())!.Label);
    }
}
```

- [ ] **Step 2: Run and see them fail.** `--filter "FullyQualifiedName~BodyPriorityResolverTests"` -> build FAIL.

- [ ] **Step 3: Implement**

```csharp
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;

namespace PenumbraOrganizer.Plugin.Organizer.Classification;

public enum BodyEvidenceTier { Content, Name, LibraryPath }

/// <summary>The body a mod files under, and which tier and source put it there.</summary>
public sealed record BodyResolution(string Label, BodyEvidenceTier Tier, BodyEvidenceSource Source);

/// <summary>
/// Picks one body from a mod's candidates. The mirror of <see cref="HairPriorityResolver"/>: pure,
/// no disk, applied at sort time against evidence the scan stored on the row.
/// </summary>
public static class BodyPriorityResolver
{
    public static BodyResolution? Resolve(BodyCandidates? candidates, BodyPriority priority)
    {
        if (candidates is null)
            return null;

        // A later tier is consulted only when every earlier one found nothing. User settings win
        // INSIDE a tier by rank; they do not let a noisy library path override clear content.
        return Pick(candidates.Content, BodyEvidenceTier.Content, priority)
            ?? Pick(candidates.Name, BodyEvidenceTier.Name, priority)
            ?? Pick(candidates.LibraryPath, BodyEvidenceTier.LibraryPath, priority);
    }

    private static BodyResolution? Pick(
        IReadOnlyList<BodyCandidate> tier, BodyEvidenceTier which, BodyPriority priority)
    {
        if (tier.Count == 0)
            return null;

        // Rank first; ordinal label second so two labels the list does not know (both
        // int.MaxValue) never resolve by the order the scan happened to find them in.
        var best = tier
            .OrderBy(c => priority.Rank(c.Label))
            .ThenBy(c => c.Label, StringComparer.Ordinal)
            .First();
        return new BodyResolution(best.Label, which, best.Source);
    }
}
```

- [ ] **Step 4: Run.** Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Classification/BodyPriorityResolver.cs PenumbraOrganizer.Plugin.Tests/Organizer/Classification/BodyPriorityResolverTests.cs
git commit -m "feat: resolve a mod's body from tiered candidates and the user priority" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Scan stores candidates; dump explains them

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerModRow.cs`
- Modify: `PenumbraOrganizer.Plugin/LibraryWork/Pure/ScanProcessor.cs` (fields, `Prepare` ~line 56-65, `Process` lines 67-139)
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerExportFormatter.cs` (after the `BodyType:` line, ~line 48)
- Test: `PenumbraOrganizer.Plugin.Tests/LibraryWork/Pure/` (find the existing `ScanProcessor` test file with `Glob **/ScanProcessor*Tests.cs` and add to it), `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerExportFormatterTests.cs`

**Interfaces:**
- Consumes: `ModContentReader.Read`, `BodyCandidateBuilder.Build`, `BodyPriorityResolver.Resolve`, `BodyPriority.Default`.
- Produces: `OrganizerModRow.BodyCandidates` (`BodyCandidates?`, `init`); `BodyType` keeps meaning "the resolution under the DEFAULT priority" (so the dump and any reader of it keep working).
- Behavior: for rows of category Gear, Hair or Body there is exactly one `ModContentReader.Read` call; slots and hair evidence come from it. Body evidence is built only when `BodyTypeApplies` is true (so a weapon/hat/ring never has its content searched). A null read stays null for slots (existing `ReadFailure` diagnostic) and sets `ContentReadFailed`.

- [ ] **Step 1: Write the failing tests.** Locate the existing ScanProcessor test fixture (`Glob`), reuse its helper for building a `ScanSeed` and a temp mod directory, then add:

```csharp
    [Fact]
    public void Process_GearWithNoBodyInName_ButBodyInOptions_GetsItFromContent()
    {
        // Gear row: a changed-item key that classifies as Gear, a top slot in the config, and an
        // option group naming the body. Build the seed/dir exactly as the neighbouring gear tests do.
        var row = ProcessGearWithConfig("Plain Corset", """
            {"DefaultData":{"Files":{"chara/equipment/e0001/model/c0101e0001_top.mdl":"x.mdl"},"Manipulations":[]},
             "Groups":[{"Name":"Body","Options":[{"Name":"Bibo+"},{"Name":"TBSE"}]}]}
            """);

        Assert.NotNull(row.BodyCandidates);
        Assert.Equal(["Bibo+", "TBSE"], row.BodyCandidates!.Content.Select(c => c.Label).Order());
        Assert.Equal("Bibo+", row.BodyType); // default priority = body-list order
        Assert.Empty(row.BodyCandidates.Name);
    }

    [Fact]
    public void Process_GearWithBodyOnlyInName_StillFilesByName()
    {
        var row = ProcessGearWithConfig("Bibo+ Medieval Dress", """
            {"DefaultData":{"Files":{"chara/equipment/e0001/model/c0101e0001_top.mdl":"x.mdl"},"Manipulations":[]}}
            """);

        Assert.Equal("Bibo+", row.BodyType);
        Assert.Empty(row.BodyCandidates!.Content);
        Assert.Equal(["Bibo+"], row.BodyCandidates.Name.Select(c => c.Label));
    }

    [Fact]
    public void Process_UnreadableConfig_FallsBackToNameAndFlagsTheFailure()
    {
        var row = ProcessGearWithConfig("Bibo+ Dress", "{ not json");

        // Slots are unreadable too, so the existing gate keeps body evidence off - the flag lives
        // on candidates only when evidence was built. Pin the gate: no candidates, no body.
        Assert.Equal(GearSlotDiagnostic.ReadFailure, row.GearSlotDiagnostic);
        Assert.Null(row.BodyType);
    }

    [Fact]
    public void Process_WeaponInOptionsNamedAfterABody_IsNotSearched()
    {
        var row = ProcessGearWithConfig("Artemis Bow", """
            {"DefaultData":{"Files":{"chara/weapon/w1001/obj/body/b0001/model/w1001b0001.mdl":"x.mdl"},"Manipulations":[]},
             "Groups":[{"Name":"Look","Options":[{"Name":"Artemis"}]}]}
            """);

        Assert.Null(row.BodyType);
        Assert.Null(row.BodyCandidates);
    }
```

`ProcessGearWithConfig(name, configJson)` is a small helper you add to that test class: create a temp mod directory, write `configJson` to `meta.json`, build a `ScanSeed` for it with a Gear-classifying `ChangedItemKeys` entry the same way the neighbouring tests do, call `Prepare` then `Process`. (The existing `BodyTypeApplies`/gear tests in that file show the exact seed shape; copy it rather than inventing one.)

Add to `OrganizerExportFormatterTests`:

```csharp
    [Fact]
    public void Format_BodyEvidence_IsEmittedWhenCandidatesExist()
    {
        var row = MakeRow("a", "Plain Corset", category: ModCategory.Gear, subCategory: "Top", bodyType: "Rue+");
        var withEvidence = new OrganizerModRow
        {
            Identifier = row.Identifier, Name = row.Name, Author = row.Author,
            CurrentPath = row.CurrentPath, ProposedPath = row.ProposedPath,
            Category = row.Category, SubCategory = row.SubCategory, BodyType = "Rue+",
            BodyCandidates = new BodyCandidates
            {
                Content = [new BodyCandidate("Rue+", BodyEvidenceSource.Option)],
            },
        };

        var result = OrganizerExportFormatter.Format(
            [withEvidence], new ReviewResult([], new Dictionary<string, List<string>>()));

        Assert.Contains("BodyEvidence: content=[Rue+ (Option)] name=[] library=[] contentRead=ok", result);
    }

    [Fact]
    public void Format_NoCandidates_EmitsNoBodyEvidenceLine()
    {
        var result = OrganizerExportFormatter.Format(
            [MakeRow("a", "Mystery Mod")], new ReviewResult([], new Dictionary<string, List<string>>()));

        Assert.DoesNotContain("BodyEvidence:", result);
    }
```

- [ ] **Step 2: Run and see them fail.** `--filter "FullyQualifiedName~ScanProcessor|FullyQualifiedName~OrganizerExportFormatterTests"` -> build FAIL (`BodyCandidates` not on the row).

- [ ] **Step 3: Implement.**

`OrganizerModRow.cs` - add after `BodyType`:

```csharp
    /// <summary>
    /// Every body this mod could file under, by tier. Null unless the row passed the body gate
    /// (Gear cut to a body's shape, or a Body mod). Resolved against the user's priority at SORT
    /// time, so reordering the priority re-files mods without a rescan; <see cref="BodyType"/>
    /// holds the resolution under the DEFAULT order for the dump and for callers with no priority.
    /// </summary>
    public BodyCandidates? BodyCandidates { get; init; }
```

`ScanProcessor.cs` - add field `private BodyPriority _defaultBodyPriority = BodyPriority.Default([]);` and in `Prepare`, after `_bodyTypeMatcher = ...`:

```csharp
        _defaultBodyPriority = BodyPriority.Default(bodyResult.Document.Bodies.Select(b => b.Label));
```

Replace the gear block, hair block and body block of `Process` (lines 74-121) with:

```csharp
        // ONE walk of the mod's config for every row that needs any of it: slots for Gear, hair
        // evidence for Hair, and body evidence for Gear and Body. A Gear mod is read once, not once
        // per consumer. Null means the walk could not be trusted.
        ModContent? content = null;
        var walked = classification.Category is ModCategory.Gear or ModCategory.Hair or ModCategory.Body;
        if (walked)
            content = ModContentReader.Read(modPath);

        var gearSlotDiagnostic = GearSlotDiagnostic.NotApplicable;
        IReadOnlySet<EquipmentSlot>? equipmentSlots = null;
        if (classification.Category == ModCategory.Gear)
        {
            equipmentSlots = content?.Slots;
            classification = ModTypeClassifier.EnrichGearSubCategory(classification, equipmentSlots);

            gearSlotDiagnostic = equipmentSlots switch
            {
                null => GearSlotDiagnostic.ReadFailure,
                { Count: 0 } when !modPath.Exists => GearSlotDiagnostic.DirectoryMissing,
                { Count: 0 } => GearSlotDiagnostic.ZeroEvidence,
                { Count: 1 } => GearSlotDiagnostic.Single,
                _ => GearSlotDiagnostic.Ambiguous,
            };
        }

        ModHairFileReader.HairEvidence? hairEvidence = null;
        if (classification.Category == ModCategory.Hair)
        {
            hairEvidence = content?.Hair;
            classification = ModTypeClassifier.EnrichHairSubCategory(classification, hairEvidence?.Flat);
        }

        // The body a mod is for. Tier 1 is what is INSIDE the mod (option groups, options, the
        // first folder of its files), then its name, then the library folders above it - see
        // BodyCandidateBuilder. The slot gate is unchanged and still decides WHETHER a body applies;
        // it is what keeps a weapon, hat or ring from having its options searched for a body name.
        // A match is a STATED intent, not a verified fact (see BodyTypeMatcher).
        BodyCandidates? bodyCandidates = null;
        string? bodyType = null;
        if (ModTypeClassifier.BodyTypeApplies(classification.Category, equipmentSlots))
        {
            bodyCandidates = BodyCandidateBuilder.Build(
                _bodyTypeMatcher, item.Name, item.CurrentPath, content, contentReadFailed: walked && content is null);
            bodyType = BodyPriorityResolver.Resolve(bodyCandidates, _defaultBodyPriority)?.Label;
        }
```

and in the returned row add `BodyCandidates = bodyCandidates,` beside `BodyType = bodyType,`. Add `using PenumbraOrganizer.Plugin.Organizer.BodyTypes;` if missing (it is already imported for `BodyTypeListStore`).

`OrganizerExportFormatter.cs` - after the `BodyType:` line:

```csharp
            // Why this mod landed where it did: every candidate with its tier and source. The
            // BodyType line above is the DEFAULT-order winner; a sort with another priority can pick
            // a different candidate from this same list.
            if (mod.BodyCandidates is { } candidates)
                sb.AppendLine($"BodyEvidence: {candidates.Describe()}");
```

Note on `IndexProcessor.cs:64` (`ModEquipmentFileReader.ReadEquipmentSlots`): leave it alone; it is a separate index pass with its own walk, out of scope.

- [ ] **Step 4: Run**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~ScanProcessor|FullyQualifiedName~OrganizerExportFormatterTests|FullyQualifiedName~LibraryWorkPurityTests|FullyQualifiedName~BodyTypeApplies"`
Expected: all PASS, including the purity test (the new code references no Dalamud/Penumbra types).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin PenumbraOrganizer.Plugin.Tests
git commit -m "feat: scan stores tiered body candidates from one shared config walk" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Apply the priority at sort time

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/SortFolderSelectors.cs` (`Select` signature line 33-42, `Type()` line 61-63)
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs` (the six-arg `Sort` overload, ~lines 517-522)
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateSortBodyTests.cs`

**Interfaces:**
- Consumes: `BodyPriorityResolver.Resolve`, `BodyPriority`.
- Produces: `SortFolderSelectors.Select(..., bool splitBody = false, BodyPriority? bodyPriority = null)` and `OrganizerState.Sort(..., bool splitBody = false, BodyPriority? bodyPriority = null)`. With a priority AND candidates on the row the body is resolved live; otherwise the row's stored `BodyType` is used, which keeps every existing caller and test (and the template planner, which passes neither) unchanged.

- [ ] **Step 1: Write the failing tests** (append to `OrganizerStateSortBodyTests`; add `using PenumbraOrganizer.Plugin.Organizer.BodyTypes; using PenumbraOrganizer.Plugin.Organizer.Classification;`)

```csharp
    private static OrganizerState WithMultiBodyMod()
    {
        var state = new OrganizerState();
        state.LoadScan(
        [
            new OrganizerModRow { Identifier = "multi", Name = "Succubus Lifestyle", Author = "Ann",
                CurrentPath = "Succubus Lifestyle", ProposedPath = "Succubus Lifestyle",
                Category = ModCategory.Gear, SubCategory = "Top", BodyType = "YAB",
                BodyCandidates = new BodyCandidates
                {
                    Content = [new BodyCandidate("YAB", BodyEvidenceSource.Option),
                               new BodyCandidate("Rue+", BodyEvidenceSource.Option)],
                } },
        ], new HashSet<string>());
        return state;
    }

    private static readonly string[] Bodies = ["YAB", "Rue+", "Bibo+"];

    [Fact]
    public void Sort_ReorderingThePriority_RefilesTheSameRowsWithoutARescan()
    {
        var state = WithMultiBodyMod();

        state.Sort(SortStrategy.TypeOnly, true, false, false, Canon, splitBody: true,
            bodyPriority: BodyPriority.Normalize(["Rue+"], Bodies));
        Assert.Equal("Gear/Rue+/Top/Succubus Lifestyle", PathOf(state, "multi"));

        state.Sort(SortStrategy.TypeOnly, true, false, false, Canon, splitBody: true,
            bodyPriority: BodyPriority.Normalize(["YAB"], Bodies));
        Assert.Equal("Gear/YAB/Top/Succubus Lifestyle", PathOf(state, "multi"));
    }

    [Fact]
    public void Sort_WithoutAPriority_UsesTheRowsStoredBodyType()
    {
        var state = WithMultiBodyMod();

        state.Sort(SortStrategy.TypeOnly, true, false, false, Canon, splitBody: true);

        Assert.Equal("Gear/YAB/Top/Succubus Lifestyle", PathOf(state, "multi"));
    }

    [Fact]
    public void Sort_BodySplitOff_IgnoresThePriorityEntirely()
    {
        var state = WithMultiBodyMod();

        state.Sort(SortStrategy.TypeOnly, true, false, false, Canon, splitBody: false,
            bodyPriority: BodyPriority.Normalize(["Rue+"], Bodies));

        Assert.Equal("Gear/Top/Succubus Lifestyle", PathOf(state, "multi"));
    }

    [Fact]
    public void Sort_RowsWithoutCandidates_KeepTheirStoredBodyType_EvenWithAPriority()
    {
        var state = WithMods(); // the existing fixture: rows carry BodyType only

        state.Sort(SortStrategy.TypeOnly, true, false, false, Canon, splitBody: true,
            bodyPriority: BodyPriority.Default(Bodies));

        Assert.Equal("Gear/Bibo+/Top/Bibo+ Medieval Dress", PathOf(state, "dress"));
    }
```

- [ ] **Step 2: Run and see them fail.** `--filter "FullyQualifiedName~OrganizerStateSortBodyTests"` -> build FAIL (`bodyPriority` parameter missing).

- [ ] **Step 3: Implement.** In `SortFolderSelectors.Select` add the parameter after `splitBody`:

```csharp
        bool splitBody = false,
        BodyPriority? bodyPriority = null)
```

(add `using PenumbraOrganizer.Plugin.Organizer.BodyTypes;`) and change the last line of `Type()`:

```csharp
            return TypeFolder(row.Category, sub, renameFolder, splitBody ? ResolveBody(row, bodyPriority) : null);
```

Add the helper beside `KnownSegment`:

```csharp
    // The live priority beats the stored default only when the row has candidates to rank. A row
    // built without them (an imported workbook, an older scan, most tests) keeps its BodyType, so
    // adding the priority changes no existing result.
    private static string? ResolveBody(OrganizerModRow row, BodyPriority? priority) =>
        priority is not null && row.BodyCandidates is not null
            ? BodyPriorityResolver.Resolve(row.BodyCandidates, priority)?.Label
            : row.BodyType;
```

In `OrganizerState.Sort` (the overload with `splitBody`):

```csharp
    public int Sort(SortStrategy strategy, bool splitGear, bool splitNpc, bool splitHair,
                    Func<string, string> canonicalizeCreator, HairPriority? hairPriority = null,
                    bool splitBody = false, BodyPriority? bodyPriority = null) =>
        Sort(row => SortFolderSelectors.Select(strategy, splitGear, splitNpc, row, canonicalizeCreator,
                                               splitHair: splitHair, hairPriority: hairPriority,
                                               splitBody: splitBody, bodyPriority: bodyPriority));
```

(add the `BodyTypes` using to `OrganizerState.cs` if absent).

- [ ] **Step 4: Run**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~OrganizerStateSort|FullyQualifiedName~SortFolderSelectorsTests|FullyQualifiedName~TemplateFallback"`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin PenumbraOrganizer.Plugin.Tests
git commit -m "feat: resolve the body against the live priority at sort time" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Persisted settings and the Body priority section

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Configuration.cs`
- Modify: `PenumbraOrganizer.Plugin/Windows/SortPanel.cs`
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.SortTab.cs` (`_sortPanel.Draw(...)` call, line 21-24)
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs` (body-label cache)
- Test: `PenumbraOrganizer.Plugin.Tests/ConfigurationTests.cs`, `PenumbraOrganizer.Plugin.Tests/Windows/SortSelectionTests.cs`, `PenumbraOrganizer.Plugin.Tests/Windows/TemplateFallbackConversionTests.cs` (line 50)

**Interfaces:**
- Consumes: `BodyPriority`, `BodyTypeListStore.LoadForMatching`.
- Produces:
  - `Configuration.BodyPriority` (`List<string>`), `Configuration.HairRaceCode` (`string?`), `Configuration.MidlanderFallback` (`bool`).
  - `SortSelection` gains an 8th primary-constructor parameter `BodyPriority? BodyPriority = null`.
  - `SortPanel.Draw(..., Func<IReadOnlyList<string>> getBodyLabels, Action? onShowSortHelp = null)`.
  - `Plugin.BodyLabels` (`IReadOnlyList<string>`, replaced by reference on refresh) and `Plugin.RefreshBodyLabels()`.
- Persists: priority list, hair race, Midlander fallback. Group by and the four split checkboxes stay session state.

- [ ] **Step 1: Write the failing tests**

`ConfigurationTests.cs`:

```csharp
    [Fact]
    public void SortPreferences_DefaultToEmptyAndNone()
    {
        var config = new Configuration();

        Assert.Empty(config.BodyPriority);
        Assert.Null(config.HairRaceCode);
        Assert.False(config.MidlanderFallback);
    }

    [Fact]
    public void SortPreferences_RoundTripThroughJson_AndAnOldConfigWithoutThemLoadsClean()
    {
        var config = new Configuration { HairRaceCode = "1801", MidlanderFallback = true };
        config.BodyPriority.AddRange(["Rue+", "TBSE"]);

        var loaded = System.Text.Json.JsonSerializer.Deserialize<Configuration>(
            System.Text.Json.JsonSerializer.Serialize(config))!;
        Assert.Equal(["Rue+", "TBSE"], loaded.BodyPriority);
        Assert.Equal("1801", loaded.HairRaceCode);
        Assert.True(loaded.MidlanderFallback);

        var old = System.Text.Json.JsonSerializer.Deserialize<Configuration>("""{"Version":2}""")!;
        Assert.Empty(old.BodyPriority);
    }
```

`SortSelectionTests.cs` (add `using PenumbraOrganizer.Plugin.Organizer.BodyTypes;`):

```csharp
    private static readonly string[] Bodies = ["Bibo+", "Rue+", "TBSE"];

    private static SortSelection WithPriority(BodyPriority? p) =>
        new(SortStrategy.TypeOnly, true, true, false, SplitBody: true, BodyPriority: p);

    [Fact]
    public void SortSelection_ReorderingTheBodyPriority_MakesTheSelectionDiffer()
    {
        var a = WithPriority(BodyPriority.Default(Bodies));
        var moved = WithPriority(BodyPriority.Default(Bodies).MoveDown(0));

        Assert.NotEqual(a, moved);
    }

    [Fact]
    public void SortSelection_UnchangedContentsInAFreshSnapshot_IsEqual()
    {
        // The panel rebuilds the snapshot; a new instance with the same contents must not look stale.
        Assert.Equal(
            WithPriority(BodyPriority.Normalize(["Rue+"], Bodies)),
            WithPriority(BodyPriority.Normalize(["rue+"], Bodies)));
    }

    [Fact]
    public void SortSelection_ResetRestoresEqualityWithTheDefault()
    {
        var def = WithPriority(BodyPriority.Default(Bodies));
        var moved = WithPriority(BodyPriority.Default(Bodies).MoveDown(1));

        Assert.NotEqual(def, moved);
        Assert.Equal(def, WithPriority(BodyPriority.Normalize(null, Bodies)));
    }
```

`TemplateFallbackConversionTests.cs` line 50: change `Assert.Equal(7, ...SortSelection...)` to `Assert.Equal(8, ...)` and extend the comment: the new field is the body priority, deliberately not stored by templates for the same reason the hair priority and body split are not (a shared template must not re-folder a recipient's gear against a body list they may hold a different version of). `TemplateFallback` stays at 3.

- [ ] **Step 2: Run and see them fail.** `--filter "FullyQualifiedName~ConfigurationTests|FullyQualifiedName~SortSelectionTests|FullyQualifiedName~TemplateFallbackConversionTests"` -> build FAIL.

- [ ] **Step 3: Implement.**

`Configuration.cs` - add after `UseScrapedNpcNameList`:

```csharp
    /// <summary>
    /// The user's body ranking, first is highest. Set once and left: most people use one body per
    /// character, so unlike the sort strategy this is a preference, not a per-sort choice. Repaired
    /// against the current body list on load by <c>BodyPriority.Normalize</c>, so a stale entry or a
    /// body added by a later release never breaks it.
    /// </summary>
    public List<string> BodyPriority { get; set; } = [];

    /// <summary>The hair priority race code ("1801"), or null for None. A set-once preference.</summary>
    public string? HairRaceCode { get; set; }

    /// <summary>The Midlander hair fallback. A set-once preference.</summary>
    public bool MidlanderFallback { get; set; }
```

`SortPanel.cs`:

1. `SortSelection` - add the parameter and `using PenumbraOrganizer.Plugin.Organizer.BodyTypes;`:

```csharp
public readonly record struct SortSelection(
    SortStrategy Strategy, bool SplitGear, bool SplitNpc, bool SplitHair,
    string? HairRaceCode = null, bool MidlanderFallback = false, bool SplitBody = false,
    BodyPriority? BodyPriority = null)
```

2. Replace the "SESSION state" comment (lines 63-67) with:

```csharp
    // Group by and the four split checkboxes are SESSION state: a sort strategy is a choice about
    // the action you are about to take, not a preference worth persisting.
    //
    // The hair race, the Midlander fallback and the body priority are PREFERENCES. They are set once
    // and left (most people use one body per character), so they are read from Configuration on the
    // first frame and written back the moment they change.
```

3. Fields (replace `_hairRaceIndex`/`_midlanderFallback` initialisers and add):

```csharp
    private bool _prefsLoaded;
    private BodyPriority _bodyPriority = BodyPriority.Default([]);
    private IReadOnlyList<string>? _bodyLabelsSeen;
```

4. Change the `Draw` signature to add `Func<IReadOnlyList<string>> getBodyLabels,` before `Action? onShowSortHelp = null`.

5. At the top of `Draw` (before `BeginDisabled`):

```csharp
        var bodyLabels = getBodyLabels();

        if (!_prefsLoaded)
        {
            _prefsLoaded = true;
            var raceIndex = config.HairRaceCode is null
                ? 0
                : Array.FindIndex(HairRaceCodes.All.ToArray(), r => r.Code == config.HairRaceCode) + 1;
            _hairRaceIndex = Math.Max(raceIndex, 0);
            _midlanderFallback = config.MidlanderFallback;
        }

        // Re-repaired only when the label list is a new instance, i.e. after a rescan changed the
        // body list, so this costs nothing per frame. Normalize drops stale labels and appends new
        // bodies, so a saved list from an older build or a different user list is always valid.
        if (!ReferenceEquals(_bodyLabelsSeen, bodyLabels))
        {
            _bodyLabelsSeen = bodyLabels;
            _bodyPriority = BodyPriority.Normalize(config.BodyPriority, bodyLabels);
        }
```

6. After the hair widgets, persist when changed (place right after the "Midlander fallback" `Help.Tooltip` line):

```csharp
        var raceCode = HairRaceCodeAt(_hairRaceIndex);
        if (config.HairRaceCode != raceCode || config.MidlanderFallback != _midlanderFallback)
        {
            config.HairRaceCode = raceCode;
            config.MidlanderFallback = _midlanderFallback;
            saveConfig();
        }
```

7. After the body checkbox tooltip (line 165), add the section:

```csharp
        DrawBodyPriority(config, saveConfig, bodyLabels, splitsApply);
```

and the method:

```csharp
    // Collapsed by default: set once and forget. Arrows rather than drag-and-drop, which is fiddly
    // in ImGui and unusable from a keyboard. Disabled under the same condition as the body checkbox.
    private void DrawBodyPriority(
        Configuration config, Action saveConfig, IReadOnlyList<string> bodyLabels, bool splitsApply)
    {
        if (!ImGui.CollapsingHeader("Body priority##body-priority"))
            return;

        ImGui.BeginDisabled(!splitsApply);
        ImGui.TextDisabled("Highest first. Used when a mod supports more than one body.");

        var order = _bodyPriority.Labels; // immutable: safe to iterate while a click replaces the field
        if (ImGui.BeginChild("##body-priority-list", new System.Numerics.Vector2(0, 220)))
        {
            for (var i = 0; i < order.Count; i++)
            {
                ImGui.PushID(i);

                ImGui.BeginDisabled(i == 0);
                if (ImGui.ArrowButton("##up", ImGuiDir.Up))
                    SetBodyPriority(config, saveConfig, _bodyPriority.MoveUp(i));
                ImGui.EndDisabled();

                ImGui.SameLine();
                ImGui.BeginDisabled(i == order.Count - 1);
                if (ImGui.ArrowButton("##down", ImGuiDir.Down))
                    SetBodyPriority(config, saveConfig, _bodyPriority.MoveDown(i));
                ImGui.EndDisabled();

                ImGui.SameLine();
                ImGui.TextUnformatted($"{i + 1}. {order[i]}");

                ImGui.PopID();
            }
        }
        ImGui.EndChild();

        if (ImGui.Button("Reset to default##body-priority-reset"))
            SetBodyPriority(config, saveConfig, BodyPriority.Default(bodyLabels));

        ImGui.EndDisabled();
        Help.Tooltip(HelpTopics.SortBodyPriority, splitsApply ? null : SplitsInapplicableReason);
    }

    private void SetBodyPriority(Configuration config, Action saveConfig, BodyPriority next)
    {
        if (next == _bodyPriority)
            return;
        _bodyPriority = next;
        config.BodyPriority = [.. next.Labels];
        saveConfig();
    }
```

If the repo's other `BeginChild` uses show a different overload shape (e.g. with `ImGuiChildFlags`), mirror them - grep `BeginChild` under `Windows/` first.

8. Build the selection and sort with the priority (replace lines 174-182):

```csharp
        var selection = new SortSelection(strategy, _splitGear, _splitNpc, _splitHair,
            HairRaceCodeAt(_hairRaceIndex), _midlanderFallback, _splitBody, _bodyPriority);

        if (ImGui.Button("Sort##sort-mods"))
        {
            state.Sort(selection.Strategy, selection.SplitGear, selection.SplitNpc, selection.SplitHair,
                canonicalizeCreator, selection.HairPriority, selection.SplitBody, selection.BodyPriority);
            _lastSorted = selection;
        }
```

`Plugin.cs`: find where the plugin's config directory is available (grep `new ScanProcessor(` / `GetPluginConfigDirectory`) and where a finished scan is published to `OrganizerState` (grep `LoadScan`). Add:

```csharp
    /// <summary>The current body labels, in list order. A new instance after each refresh, which is how SortPanel knows to re-repair the saved priority.</summary>
    public IReadOnlyList<string> BodyLabels { get; private set; } = [];

    public void RefreshBodyLabels()
    {
        var result = BodyTypeListStore.LoadForMatching(PluginInterface.GetPluginConfigDirectory());
        BodyLabels = [.. result.Document.Bodies.Select(b => b.Label)];
    }
```

Call `RefreshBodyLabels()` once at the end of the constructor and again where a completed scan is published (after `LoadScan`), so a user-list edit is picked up by the rescan the user already runs. (`GetPluginConfigDirectory` is a property/method on the injected plugin interface; use whatever the file already calls for the scan.) Warnings from the load are already surfaced by the scan, so ignore `result.Warnings` here.

`MainWindow.SortTab.cs` - pass it:

```csharp
        _sortPanel.Draw(
            _plugin.OrganizerState, gates, _plugin.Config, _plugin.SaveConfig,
            _creatorCanonicalizer.Canonicalize,
            () => _plugin.BodyLabels,
            () => _sortHelpRequested = true);
```

- [ ] **Step 4: Run**

Run: `dotnet build PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj -c Debug` then `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~ConfigurationTests|FullyQualifiedName~SortSelectionTests|FullyQualifiedName~TemplateFallbackConversionTests|FullyQualifiedName~SortPanel"`
Expected: build 0 errors; tests PASS. Any existing test that calls `SortPanel.Draw` must pass the new argument.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin PenumbraOrganizer.Plugin.Tests
git commit -m "feat: saved body priority, hair race and Midlander fallback, with a Body priority section" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 10: Help, docs and the full run

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/HelpTopics.cs` (add `SortBodyPriority = new("sort.body-priority")` after `SortSplitBody`)
- Modify: `PenumbraOrganizer.Plugin/Windows/HelpTopicUsage.cs` (add `HelpTopics.SortBodyPriority,` after `SortSplitBody`, ~line 35)
- Modify: `PenumbraOrganizer.Plugin/Windows/HelpTab.cs` (add it to the Sort section list after `SortSplitBody`, ~line 121)
- Modify: `PenumbraOrganizer.Plugin/Resources/help-content.json` (the `sort.split-body` entry at line 169; new entry)
- Modify: `docs/USER_GUIDE.md`, `docs/HOW_SORTING_WORKS.md`, `docs/testing/2026-11-04-body-type-sorting-checklist.md`
- Test: the existing help-content tests (find with `Grep "help-content" PenumbraOrganizer.Plugin.Tests`)

**Interfaces:** none produced; consumes `HelpTopics.SortBodyPriority` already used by `SortPanel` (Task 9, which does not compile without it - so do the three C# lines of this task FIRST if Task 9 was built before it).

- [ ] **Step 1: Add the topic.** Add the three C# lines above. In `help-content.json` replace the `sort.split-body` `short` and add the new entry after it:

```json
    {
      "id": "sort.split-body",
      "title": "Split body by body type",
      "short": "Puts gear in a folder named after the body it was made for, as Gear/Bibo+/Top. Body mods too. The body comes from the mod's options and files first, then its name, then the folder it sits in. A mod that shows none is left where it was."
    },
    {
      "id": "sort.body-priority",
      "title": "Body priority",
      "short": "When a mod supports more than one body, it is filed under whichever of them ranks highest here. Mods that name only one body are not affected. Move a body with the arrows; your order is saved."
    },
```

Optionally run the copy past the `design:ux-copy` skill before committing; keep the same plain voice as the neighbouring entries.

- [ ] **Step 2: Run the help tests**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~Help"`
Expected: PASS. A failure naming `sort.body-priority` means one of the three registration lists (usage, HelpTab section, content json) is missing it.

- [ ] **Step 3: Update the docs.**
  - `docs/HOW_SORTING_WORKS.md`, "Body-type sub-classification": replace "name only" with the three tiers (inside the mod, then its name, then its library folder), the file-path rule table, that the priority list resolves ties and applies at sort time, and that a mod whose config cannot be read falls back to its name.
  - `docs/USER_GUIDE.md`, "Body type": add the "Body priority" section (collapsed; arrows; Reset to default; saved between runs), and a Known caveat that a mod titled for one body but whose options offer others now files by the options.
  - `docs/testing/2026-11-04-body-type-sorting-checklist.md`: append a "Content detection and priority" section with the five in-game checks from the spec (rescan counts vs previous build; `[TPM] Succubus Lifestyle (YAB - Rue)` and `Heartbeat for Bibo & YAB+` under two orders re-sorted without a rescan; reorder, restart, order persisted; a mod with no body in its name filed from its options; Review tab shows the moves before apply). Add to it the `BodyEvidence:` dump line as the way to see why a mod landed where it did.
  - Regenerate the PDF so the guide and its PDF do not describe different versions: `python docs/build-pdf.py` (needs `wkhtmltopdf`; Windows only per the handoff note). If `wkhtmltopdf` is missing, say so in the commit message instead of committing a stale PDF.

- [ ] **Step 4: Full verification**

Run, from the worktree root:
`dotnet build PenumbraOrganizer.Plugin.sln -c Debug` -> 0 errors, 0 warnings introduced.
`dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj` -> all PASS. Baseline was 1671 passing before this work; the count must be higher by the number of tests added and nothing may be skipped or loosened (the `TemplateFallbackConversionTests` change is the one deliberate edit to an existing assertion).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin docs
git commit -m "docs: body priority and content detection in help, guide and checklist" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

## Self-Review (against the spec)

- Tiers 1 / 2 / 2.5 and fall-through: Tasks 4, 6. Title excluded from Tier 1: Tasks 2 (walker, tested) and 4.
- File-path rule (values only, first folder, accepted/rejected table): Task 4 tests.
- Applied at sort time, no rescan: Tasks 7 (store) + 8 (resolve), with the reorder-twice test.
- One walk per mod, three consumers: Task 3 + 7 (Gear read once, equivalence tested; `IndexProcessor` is a separate pass, left as is).
- Failure and provenance: `ContentReadFailed` (Task 4/7), `BodyEvidence:` dump line (Task 7), fall through to name when the read fails (Task 6 test). One consequence to know: a Gear mod whose config cannot be read has no slots, so the existing slot gate already keeps body evidence off for it (pinned in Task 7 so it is a decision, not an accident). `ContentReadFailed` therefore only ever appears on Body-category rows whose config could not be read. Note it in the checklist if it surprises anyone.
- Immutable, content-equal priority; reorder / unchanged / reset equality: Tasks 5 + 9.
- Normalization (duplicates, case, canonical, stale, appended, no cap, null/empty): Task 5. Malformed whole-config JSON is Dalamud's loader and out of scope, as the spec says.
- Persistence of priority, hair race, Midlander; Group by and splits stay session: Task 9. Templates unchanged and the tripwire updated deliberately: Task 9.
- Single Sort tab, no rename, collapsed section with arrows and reset, same disabling as the checkbox: Task 9.
- Help topic, docs, checklist, PDF: Task 10.
- Type consistency: `BodyCandidates`/`BodyCandidate`/`BodyEvidenceSource` (Task 4) are used unchanged in Tasks 6-8; `BodyEvidenceTier` and `BodyResolution` (Task 6) only in Task 6/7; `BodyPriority` members `Normalize`, `Default`, `Rank`, `MoveUp`, `MoveDown`, `Labels` (Task 5) match every later use; `ModContent` (Task 3) matches Task 4's test helper and Task 7.
