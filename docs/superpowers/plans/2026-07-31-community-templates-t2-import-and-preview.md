# Community Templates — Phase T2 (File Import and Preview) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user drop in or pick a `.json` template someone shared, browse what it would do to their library, and apply it — delivering the core user value of the feature without exposing anyone's mod list.

**Architecture:** Phase T1 shipped the whole format, validation, transport, planner and application core with no UI. T2 adds the persistence and presentation layers around it: a `templates/` folder store, a filename slug sanitizer, a pure tree-builder for the preview, and a Templates tab that lists templates, previews a plan, and applies it through the existing Review Changes → Apply pipeline. Everything a template can do to a library already exists and is tested; T2 adds no new organizing behavior.

**Tech Stack:** C# / .NET 10 (`net10.0-windows7.0`), xunit 2.5.3, Dalamud `ImRaii`/`FileDialogManager`, `System.Text.Json`. No new NuGet packages.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-07-30-community-templates-design.md`. Plan for the shipped phase: `docs/superpowers/plans/2026-07-30-community-templates-t1-core.md`.
- This is **T2 only**. Out of scope, deliberately: the export review-and-trim screen, `TemplateBuilder`, and clipboard share-code encode/decode wiring — all T3. Do not build any export affordance, not even a "quick export" button.
- All new template code lives in `PenumbraOrganizer.Plugin/Organizer/Templates/`, namespace `PenumbraOrganizer.Plugin.Organizer.Templates`; tests in `PenumbraOrganizer.Plugin.Tests/Organizer/Templates/`, namespace `PenumbraOrganizer.Plugin.Tests.Organizer.Templates`.
- Templates live in `templates/` under the plugin config directory (`PluginInterface.ConfigDirectory.FullName`), the same directory as `organizer-workbook.xlsx`, `organizer-history.json`, and `operations/`.
- **`TemplatePathValidator.IsValidFolder` must NOT be used to validate a filename.** It is a virtual-folder validator: it accepts `..`, `C:`, backslashes, and Windows reserved device names, which are harmless as Penumbra virtual paths and dangerous as filenames. Filenames go through `TemplateSlug` (Task 1) — this is the single highest-value carry-forward from the T1 review.
- Reuse T1 unchanged: `TemplateCodec.DecodeJson`, `TemplatePlanner.Plan`, `OrganizerState.ApplyTemplate`, `TemplateText.Preview`, `TemplateWarningCode`. Do not reimplement validation, planning, or application.
- File writes use the atomic write-to-`.tmp`-then-`File.Replace`/`File.Move` pattern already used for backup files, so a failed write cannot destroy a good file.
- UI follows `MainWindow.cs` conventions: `using var tab = ImRaii.TabItem("...")`, `CurrentGates()` for gating, `_lastError` for surfacing failures, and `ImGui.BeginDisabled` around actions that stage proposals.
- Existing behavior must not change. `dotnet build` and the full suite pass after every task. Baseline is 1018 passing tests and one pre-existing xUnit2017 warning in `ApplyPlannerTests.cs:306` — introduce no new warnings and do not fix that one here.
- Commit after every task. Never use `--no-verify`.

## File Structure

| File | Responsibility |
| --- | --- |
| `Organizer/Templates/TemplateSlug.cs` | Turn a template's display name into a safe, unique filename |
| `Organizer/Templates/TemplateStore.cs` | Enumerate, read, and save `templates/*.json`; tolerate bad files |
| `Organizer/Templates/TemplateTreeBuilder.cs` | Turn a plan's folder counts into a renderable tree (pure) |
| `Organizer/Templates/TemplatePlanner.cs` (modify) | Add `PlanFromDecoded`, so decode warnings cannot be dropped |
| `Organizer/OrganizerState.cs` (modify) | Add `ScanGeneration`, so a stale plan is detectable |
| `Windows/MainWindow.cs` (modify) | Templates tab: list, preview, import, apply |
| `Plugin.cs` (modify) | `TemplatesDirectory` path and the store's construction |

---

### Task 1: TemplateSlug

The one place a template's untrusted display name becomes a filename. `IsValidFolder` must never be used for this.

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Templates/TemplateSlug.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplateSlugTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `public static string TemplateSlug.From(string name)` and
  `public static string TemplateSlug.MakeUnique(string slug, IReadOnlySet<string> taken)`.

- [ ] **Step 1: Write the failing test**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplateSlugTests.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Tests.Organizer.Templates;

using PenumbraOrganizer.Plugin.Organizer.Templates;

public class TemplateSlugTests
{
    [Theory]
    [InlineData("Detailed type sort", "detailed-type-sort")]
    [InlineData("Akako's layout", "akakos-layout")]
    [InlineData("  spaced  out  ", "spaced-out")]
    [InlineData("UPPER Case", "upper-case")]
    [InlineData("with_underscores", "with_underscores")]
    public void From_OrdinaryNames_ProduceReadableSlugs(string name, string expected)
    {
        Assert.Equal(expected, TemplateSlug.From(name));
    }

    // A template name is untrusted: it arrives inside a document a stranger published. None of
    // these may produce a path that escapes the templates folder or names a device.
    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("C:\\Windows\\System32")]
    [InlineData("a/b\\c")]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    [InlineData("trailing dots...")]
    [InlineData("trailing space ")]
    public void From_HostileNames_ProduceSafeSingleSegmentSlugs(string name)
    {
        var slug = TemplateSlug.From(name);

        Assert.NotEmpty(slug);
        Assert.DoesNotContain('/', slug);
        Assert.DoesNotContain('\\', slug);
        Assert.DoesNotContain(':', slug);
        Assert.False(slug is "." or "..");
        Assert.DoesNotContain("..", slug);
        Assert.False(slug.EndsWith('.') || slug.EndsWith(' '));
        Assert.All(
            slug,
            character => Assert.DoesNotContain(character, Path.GetInvalidFileNameChars()));
        Assert.Equal(slug, Path.GetFileName(slug));
    }

    // Reserved device names are only dangerous as the whole stem, so they are suffixed rather
    // than stripped -- "console-tweaks" must not be mangled.
    [Fact]
    public void From_ReservedDeviceName_IsSuffixedNotStripped()
    {
        Assert.Equal("con-template", TemplateSlug.From("CON"));
        Assert.Equal("console-tweaks", TemplateSlug.From("Console Tweaks"));
    }

    [Fact]
    public void From_NameWithNothingUsable_FallsBackToAConstant()
    {
        Assert.Equal("template", TemplateSlug.From("///"));
        Assert.Equal("template", TemplateSlug.From(""));
    }

    [Fact]
    public void From_VeryLongName_IsTruncated()
    {
        var slug = TemplateSlug.From(new string('a', 500));

        Assert.True(slug.Length <= 64, $"Slug was {slug.Length} chars.");
    }

    [Fact]
    public void MakeUnique_UntakenSlug_IsUnchanged()
    {
        Assert.Equal("layout", TemplateSlug.MakeUnique("layout", new HashSet<string>()));
    }

    // Two templates may legitimately share a display name; importing one must never overwrite
    // a template already on disk.
    [Fact]
    public void MakeUnique_TakenSlug_GetsNumericSuffix()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "layout", "layout-2" };

        Assert.Equal("layout-3", TemplateSlug.MakeUnique("layout", taken));
    }

    [Fact]
    public void MakeUnique_IsCaseInsensitive()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Layout" };

        Assert.Equal("layout-2", TemplateSlug.MakeUnique("layout", taken));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~TemplateSlugTests"
```

Expected: build error — `TemplateSlug` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `PenumbraOrganizer.Plugin/Organizer/Templates/TemplateSlug.cs`:

```csharp
using System.Text;

namespace PenumbraOrganizer.Plugin.Organizer.Templates;

/// <summary>
/// Turns a template's display name into a filename. This is the ONLY sanitizer that may be used
/// for a path on disk: TemplatePathValidator.IsValidFolder deliberately accepts "..", drive
/// letters, backslashes and Windows device names, because those are harmless as Penumbra virtual
/// folders -- and every one of them is dangerous here. The document's own `name` stays
/// authoritative for display; this value is storage only.
/// </summary>
public static class TemplateSlug
{
    public const string Fallback = "template";
    private const int MaxLength = 64;

    // Dangerous only when they are the entire stem, so they are suffixed rather than stripped --
    // otherwise a legitimate "Console Tweaks" would be mangled.
    private static readonly HashSet<string> ReservedStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string From(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var character in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character) || character == '_')
                builder.Append(character);
            else if (character == ' ')
                builder.Append('-');
            // else: skip the character (punctuation, slashes, etc.)
        }

        var slug = CollapseDashes(builder.ToString());

        if (slug.Length > MaxLength)
            slug = CollapseDashes(slug[..MaxLength]);

        // Nothing usable survived (the name was punctuation, separators, or empty).
        if (slug.Length == 0)
            return Fallback;

        if (ReservedStems.Contains(slug))
            slug += "-" + Fallback;

        return slug;
    }

    public static string MakeUnique(string slug, IReadOnlySet<string> taken)
    {
        if (!taken.Contains(slug))
            return slug;

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{slug}-{suffix}";
            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    // Also removes leading/trailing dashes, which is what makes a trailing dot or space
    // impossible -- Windows silently strips those from a filename, so a slug must never end in
    // one.
    private static string CollapseDashes(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character == '-' && (builder.Length == 0 || builder[^1] == '-'))
                continue;

            builder.Append(character);
        }

        while (builder.Length > 0 && builder[^1] == '-')
            builder.Length--;

        return builder.ToString();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~TemplateSlugTests"
```

Expected: PASS, 25 tests.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Templates/TemplateSlug.cs PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplateSlugTests.cs
git commit -m "feat: add template filename slug sanitizer"
```

---

### Task 2: TemplateStore

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Templates/TemplateStore.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplateStoreTests.cs`

**Interfaces:**
- Consumes: `TemplateSlug` (Task 1), `TemplateCodec.DecodeJson`, `ValidatedOrganizationTemplate`, `TemplateWarning` (T1).
- Produces:
  ```csharp
  public sealed record StoredTemplate(
      string FileName,
      ValidatedOrganizationTemplate Template,
      IReadOnlyList<TemplateWarning> Warnings);

  public sealed record TemplateStoreListing(
      IReadOnlyList<StoredTemplate> Templates,
      IReadOnlyList<string> UnreadableFiles);

  public sealed class TemplateStore(string directory)
  {
      public string Directory { get; }
      public TemplateStoreListing List();
      public string Save(string json, string displayName);
  }
  ```

- [ ] **Step 1: Write the failing test**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplateStoreTests.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Tests.Organizer.Templates;

using PenumbraOrganizer.Plugin.Organizer.Templates;

public sealed class TemplateStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("template-store-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Json(string name, string strategy = "ModType", string entryName = "some mod") =>
        $$"""
        {"formatVersion":1,"name":"{{name}}","fallbackStrategy":"{{strategy}}",
         "folders":["Gear"],"entries":[{"n":"{{entryName}}","f":"Gear"}]}
        """;

    [Fact]
    public void List_EmptyOrMissingDirectory_ReturnsNothingWithoutThrowing()
    {
        var store = new TemplateStore(Path.Combine(_dir, "does-not-exist"));

        var listing = store.List();

        Assert.Empty(listing.Templates);
        Assert.Empty(listing.UnreadableFiles);
    }

    [Fact]
    public void Save_WritesFileNamedFromDisplayName()
    {
        var store = new TemplateStore(_dir);

        var fileName = store.Save(Json("Detailed type sort"), "Detailed type sort");

        Assert.Equal("detailed-type-sort.json", fileName);
        Assert.True(File.Exists(Path.Combine(_dir, fileName)));
    }

    [Fact]
    public void SaveThenList_RoundTripsTheTemplate()
    {
        var store = new TemplateStore(_dir);
        store.Save(Json("Detailed type sort"), "Detailed type sort");

        var listing = store.List();

        var stored = Assert.Single(listing.Templates);
        Assert.Equal("Detailed type sort", stored.Template.Name);
        Assert.Equal("detailed-type-sort.json", stored.FileName);
    }

    // Two people may share a template name; importing one must never clobber the other.
    [Fact]
    public void Save_ExistingSlug_GetsSuffixInsteadOfOverwriting()
    {
        var store = new TemplateStore(_dir);
        store.Save(Json("Layout", entryName: "first mod"), "Layout");

        var second = store.Save(Json("Layout", entryName: "second mod"), "Layout");

        Assert.Equal("layout-2.json", second);
        Assert.Equal(2, store.List().Templates.Count);
    }

    // A hostile display name must not write outside the templates directory.
    [Fact]
    public void Save_HostileDisplayName_StaysInsideTheDirectory()
    {
        var store = new TemplateStore(_dir);

        var fileName = store.Save(Json("escape"), "../../escaped");

        Assert.Equal(fileName, Path.GetFileName(fileName));
        var written = Path.GetFullPath(Path.Combine(_dir, fileName));
        Assert.StartsWith(Path.GetFullPath(_dir), written, StringComparison.OrdinalIgnoreCase);
    }

    // One bad file must not make the whole list unavailable.
    [Fact]
    public void List_InvalidFile_IsReportedWithoutHidingTheGoodOnes()
    {
        var store = new TemplateStore(_dir);
        store.Save(Json("Good one"), "Good one");
        File.WriteAllText(Path.Combine(_dir, "broken.json"), "{ not json");

        var listing = store.List();

        Assert.Single(listing.Templates);
        Assert.Equal("Good one", listing.Templates[0].Template.Name);
        Assert.Equal(["broken.json"], listing.UnreadableFiles);
    }

    [Fact]
    public void List_NonJsonFiles_AreIgnoredEntirely()
    {
        var store = new TemplateStore(_dir);
        store.Save(Json("Good one"), "Good one");
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "hello");

        var listing = store.List();

        Assert.Single(listing.Templates);
        Assert.Empty(listing.UnreadableFiles);
    }

    [Fact]
    public void List_IsOrderedByFileNameSoTheUiIsStable()
    {
        var store = new TemplateStore(_dir);
        store.Save(Json("Zeta"), "Zeta");
        store.Save(Json("Alpha"), "Alpha");

        var listing = store.List();

        Assert.Equal(["alpha.json", "zeta.json"], listing.Templates.Select(t => t.FileName));
    }

    // Warnings are part of what the preview shows, so they must survive being stored and re-read.
    [Fact]
    public void List_CarriesDecodeWarningsThrough()
    {
        var store = new TemplateStore(_dir);
        File.WriteAllText(
            Path.Combine(_dir, "warned.json"),
            """{"formatVersion":1,"name":"Warned","fallbackStrategy":"ModType","folders":["Gear//Bad"]}""");

        var stored = Assert.Single(store.List().Templates);

        Assert.Contains(stored.Warnings, w => w.Code == TemplateWarningCode.InvalidFolderPath);
    }

    [Fact]
    public void Save_FailedWriteLeavesNoPartialFile()
    {
        var store = new TemplateStore(_dir);

        Assert.Throws<ArgumentException>(() => store.Save("{ not a valid template }", "Bad"));
        Assert.Empty(Directory.GetFiles(_dir, "*.json"));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~TemplateStoreTests"
```

Expected: build error — `TemplateStore` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `PenumbraOrganizer.Plugin/Organizer/Templates/TemplateStore.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Templates;

public sealed record StoredTemplate(
    string FileName,
    ValidatedOrganizationTemplate Template,
    IReadOnlyList<TemplateWarning> Warnings);

public sealed record TemplateStoreListing(
    IReadOnlyList<StoredTemplate> Templates,
    IReadOnlyList<string> UnreadableFiles);

/// <summary>
/// Reads and writes the templates/ folder. Every file in it arrived from outside -- a Discord
/// attachment, a blog download -- so one unreadable file must never make the rest unavailable:
/// it is reported by name and skipped.
///
/// Filenames come from TemplateSlug, never from the document's raw name, and the document's own
/// `name` stays authoritative for display. A save never overwrites an existing file.
/// </summary>
public sealed class TemplateStore(string directory)
{
    public string Directory { get; } = directory;

    public TemplateStoreListing List()
    {
        if (!System.IO.Directory.Exists(Directory))
            return new TemplateStoreListing([], []);

        var templates = new List<StoredTemplate>();
        var unreadable = new List<string>();

        foreach (var path in System.IO.Directory.GetFiles(Directory, "*.json")
                     .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(path);
            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (IOException)
            {
                unreadable.Add(fileName);
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                unreadable.Add(fileName);
                continue;
            }

            var decoded = TemplateCodec.DecodeJson(json);
            if (!decoded.Succeeded)
            {
                unreadable.Add(fileName);
                continue;
            }

            templates.Add(new StoredTemplate(fileName, decoded.Template!, decoded.Warnings));
        }

        return new TemplateStoreListing(templates, unreadable);
    }

    /// <summary>
    /// Validates before writing, so an invalid document never reaches disk, then writes atomically
    /// under a filename that cannot collide with an existing one. Returns the filename used.
    /// </summary>
    public string Save(string json, string displayName)
    {
        var decoded = TemplateCodec.DecodeJson(json);
        if (!decoded.Succeeded)
            throw new ArgumentException($"Template is not valid: {decoded.ErrorDetail}", nameof(json));

        System.IO.Directory.CreateDirectory(Directory);

        var taken = System.IO.Directory.GetFiles(Directory, "*.json")
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var fileName = TemplateSlug.MakeUnique(TemplateSlug.From(displayName), taken) + ".json";
        var target = Path.Combine(Directory, fileName);
        var temp = target + ".tmp";

        File.WriteAllText(temp, json);
        File.Move(temp, target, overwrite: false);

        return fileName;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~TemplateStoreTests"
```

Expected: PASS, 10 tests.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Templates/TemplateStore.cs PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplateStoreTests.cs
git commit -m "feat: add template store for the templates folder"
```

---

### Task 3: OrganizerState.ScanGeneration

A plan is computed for a preview and applied on a later frame. If a rescan lands in between, the plan describes rows that no longer exist. The T1 review flagged this: `ApplyTemplate` would partially apply while still returning the old report's counts, so the user sees numbers describing an apply that did not happen. This task makes staleness detectable; Task 7 acts on it.

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateScanGenerationTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `public int OrganizerState.ScanGeneration { get; }` — increments on every published scan.

- [ ] **Step 1: Write the failing test**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateScanGenerationTests.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Tests.Organizer;

using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;

public class OrganizerStateScanGenerationTests
{
    private static OrganizerModRow Row(string identifier) => new()
    {
        Identifier = identifier,
        Name = identifier,
        Author = "Tsar",
        CurrentPath = identifier,
        ProposedPath = identifier,
        Category = ModCategory.Gear,
    };

    [Fact]
    public void ScanGeneration_StartsAtZero()
    {
        Assert.Equal(0, new OrganizerState().ScanGeneration);
    }

    [Fact]
    public void ScanGeneration_IncrementsOnEveryScan()
    {
        var state = new OrganizerState();

        state.LoadScan([Row("a")], new HashSet<string>());
        Assert.Equal(1, state.ScanGeneration);

        state.LoadScan([Row("a")], new HashSet<string>());
        Assert.Equal(2, state.ScanGeneration);
    }

    // Sorting stages proposals against the same rows -- it is not a new library, so a plan
    // computed before a sort is still about the rows it described.
    [Fact]
    public void ScanGeneration_IsUnchangedBySorting()
    {
        var state = new OrganizerState();
        state.LoadScan([Row("a")], new HashSet<string>());
        var generation = state.ScanGeneration;

        state.SortByModType();

        Assert.Equal(generation, state.ScanGeneration);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~OrganizerStateScanGenerationTests"
```

Expected: build error — `ScanGeneration` does not exist.

- [ ] **Step 3: Write minimal implementation**

In `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs`, add this property beside `HasScanned`:

```csharp
    /// <summary>
    /// Increments every time a scan is published. A template plan is computed for a preview and
    /// applied on a later frame; if a rescan lands in between, the plan describes rows that no
    /// longer exist. Callers hold the generation they planned against and refuse to apply a plan
    /// whose generation no longer matches, rather than applying it partially.
    /// </summary>
    public int ScanGeneration { get; private set; }
```

Then, inside `ReplaceScanAtomically`, immediately after the existing field swaps succeed (the same place `HasScanned` is set), add:

```csharp
        ScanGeneration++;
```

It must increment only after every replacement collection has been built successfully, so a throw mid-derivation leaves both the published scan and its generation untouched.

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~OrganizerStateScanGenerationTests"
```

Expected: PASS, 3 tests.

Then the whole suite, since this touches shipped code:

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj
```

Expected: PASS, every pre-existing test unchanged. If one fails, fix the change, not the test.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateScanGenerationTests.cs
git commit -m "feat: track scan generation so stale plans are detectable"
```

---

### Task 4: TemplatePlanner.PlanFromDecoded

`Plan`'s `decodeWarnings` parameter is optional, so a caller that omits it silently loses every decode warning and reports `InvalidEntriesSkipped: 0`. The T1 review flagged this as a trap for exactly the UI T2 is building. This adds an entry point where the warnings cannot be dropped.

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Templates/TemplatePlanner.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplatePlannerFromDecodedTests.cs`

**Interfaces:**
- Consumes: `TemplateDecodeResult`, `TemplateApplicationPlan`, `TemplatePlanner.Plan` (T1).
- Produces:
  ```csharp
  public static TemplateApplicationPlan TemplatePlanner.PlanFromDecoded(
      TemplateDecodeResult decoded,
      IReadOnlyCollection<OrganizerModRow> rows,
      Func<string, string> canonicalizeCreator);
  ```

- [ ] **Step 1: Write the failing test**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplatePlannerFromDecodedTests.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Tests.Organizer.Templates;

using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Templates;

public class TemplatePlannerFromDecodedTests
{
    private static OrganizerModRow Row(string identifier, string name) => new()
    {
        Identifier = identifier,
        Name = name,
        Author = "Tsar",
        CurrentPath = name,
        ProposedPath = name,
        Category = ModCategory.Gear,
    };

    private static string Same(string value) => value;

    // A document with a bad folder produces a decode warning. Routing through PlanFromDecoded
    // makes it impossible for a caller to forget to pass those warnings along.
    [Fact]
    public void PlanFromDecoded_CarriesDecodeWarningsIntoThePlan()
    {
        var decoded = TemplateCodec.DecodeJson(
            """
            {"formatVersion":1,"name":"x","fallbackStrategy":"ModType",
             "folders":["Gear//Bad"],"entries":[{"n":"some mod","f":"Gear"}]}
            """);
        Assert.True(decoded.Succeeded);

        var plan = TemplatePlanner.PlanFromDecoded(decoded, [Row("id1", "Some Mod")], Same);

        Assert.Contains(plan.Warnings, w => w.Code == TemplateWarningCode.InvalidFolderPath);
    }

    [Fact]
    public void PlanFromDecoded_ProducesTheSamePlacementsAsPlan()
    {
        var decoded = TemplateCodec.DecodeJson(
            """
            {"formatVersion":1,"name":"x","fallbackStrategy":"ModType",
             "entries":[{"n":"some mod","f":"Characters/Nyx"}]}
            """);
        OrganizerModRow[] Rows() => [Row("id1", "Some Mod"), Row("id2", "Other Mod")];

        var viaHelper = TemplatePlanner.PlanFromDecoded(decoded, Rows(), Same);
        var viaPlan = TemplatePlanner.Plan(decoded.Template!, Rows(), Same, decoded.Warnings);

        Assert.Equal(viaPlan.DestinationFolders, viaHelper.DestinationFolders);
        Assert.Equal(viaPlan.Report, viaHelper.Report);
    }

    // Planning against a failed decode is a caller bug, not a user-facing condition: the UI must
    // surface the decode error instead of planning at all.
    [Fact]
    public void PlanFromDecoded_FailedDecode_Throws()
    {
        var decoded = TemplateCodec.DecodeJson("{ not json");
        Assert.False(decoded.Succeeded);

        Assert.Throws<ArgumentException>(
            () => TemplatePlanner.PlanFromDecoded(decoded, [], Same));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~TemplatePlannerFromDecodedTests"
```

Expected: build error — `PlanFromDecoded` does not exist.

- [ ] **Step 3: Write minimal implementation**

In `PenumbraOrganizer.Plugin/Organizer/Templates/TemplatePlanner.cs`, add above the existing `Plan` method:

```csharp
    /// <summary>
    /// Plans directly from a decode result, so the decode warnings cannot be dropped. Plan's own
    /// decodeWarnings parameter is optional, and a caller that omits it silently loses every
    /// warning and reports InvalidEntriesSkipped as 0 -- a plausible-looking but incomplete plan
    /// with no signal that anything is missing. UI callers use this entry point.
    /// </summary>
    public static TemplateApplicationPlan PlanFromDecoded(
        TemplateDecodeResult decoded,
        IReadOnlyCollection<OrganizerModRow> rows,
        Func<string, string> canonicalizeCreator)
    {
        if (!decoded.Succeeded)
        {
            throw new ArgumentException(
                "Cannot plan from a template that failed to decode; surface the error instead.",
                nameof(decoded));
        }

        return Plan(decoded.Template!, rows, canonicalizeCreator, decoded.Warnings);
    }
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~TemplatePlanner"
```

Expected: PASS, 13 tests (10 pre-existing planner tests plus 3 new).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Templates/TemplatePlanner.cs PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplatePlannerFromDecodedTests.cs
git commit -m "feat: add planner entry point that cannot drop decode warnings"
```

---

### Task 5: TemplateTreeBuilder

The preview's folder tree is real logic — nesting, per-folder counts, roll-ups — and it must not live inside a draw method where it cannot be tested.

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Templates/TemplateTreeBuilder.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplateTreeBuilderTests.cs`

**Interfaces:**
- Consumes: nothing beyond BCL.
- Produces:
  ```csharp
  public sealed record TemplateTreeNode(
      string Segment,
      string FullPath,
      int DirectCount,
      int TotalCount,
      IReadOnlyList<TemplateTreeNode> Children);

  public static IReadOnlyList<TemplateTreeNode> TemplateTreeBuilder.Build(
      IEnumerable<string> folders,
      IReadOnlyDictionary<string, int> folderCounts);
  ```

- [ ] **Step 1: Write the failing test**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplateTreeBuilderTests.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Tests.Organizer.Templates;

using PenumbraOrganizer.Plugin.Organizer.Templates;

public class TemplateTreeBuilderTests
{
    private static readonly Dictionary<string, int> NoCounts = new();

    [Fact]
    public void Build_FlatFolders_ProduceRootNodes()
    {
        var tree = TemplateTreeBuilder.Build(["Gear", "Hair"], NoCounts);

        Assert.Equal(["Gear", "Hair"], tree.Select(n => n.Segment));
        Assert.All(tree, node => Assert.Empty(node.Children));
    }

    [Fact]
    public void Build_NestedFolder_CreatesIntermediateParents()
    {
        var tree = TemplateTreeBuilder.Build(["Gear/Head"], NoCounts);

        var gear = Assert.Single(tree);
        Assert.Equal("Gear", gear.Segment);
        var head = Assert.Single(gear.Children);
        Assert.Equal("Head", head.Segment);
        Assert.Equal("Gear/Head", head.FullPath);
    }

    // The author's folder list and the planned destinations are different sets: a template can
    // declare an empty bucket, and a plan can place mods somewhere the list never mentioned.
    [Fact]
    public void Build_CountedFolderNotInFolderList_StillAppears()
    {
        var tree = TemplateTreeBuilder.Build([], new Dictionary<string, int> { ["Gear/Top"] = 3 });

        var gear = Assert.Single(tree);
        var top = Assert.Single(gear.Children);
        Assert.Equal("Gear/Top", top.FullPath);
        Assert.Equal(3, top.DirectCount);
    }

    [Fact]
    public void Build_DeclaredEmptyFolder_AppearsWithZeroCount()
    {
        var tree = TemplateTreeBuilder.Build(["Characters"], NoCounts);

        var node = Assert.Single(tree);
        Assert.Equal(0, node.DirectCount);
        Assert.Equal(0, node.TotalCount);
    }

    // TotalCount is what makes a collapsed parent meaningful.
    [Fact]
    public void Build_TotalCount_RollsUpThroughDescendants()
    {
        var counts = new Dictionary<string, int> { ["Gear"] = 1, ["Gear/Head"] = 2, ["Gear/Top"] = 3 };

        var gear = Assert.Single(TemplateTreeBuilder.Build([], counts));

        Assert.Equal(1, gear.DirectCount);
        Assert.Equal(6, gear.TotalCount);
        Assert.Equal(2, gear.Children.Count);
    }

    [Fact]
    public void Build_IsOrderedBySegmentAtEveryLevel()
    {
        var tree = TemplateTreeBuilder.Build(["Zeta", "Alpha", "Alpha/Zulu", "Alpha/Bravo"], NoCounts);

        Assert.Equal(["Alpha", "Zeta"], tree.Select(n => n.Segment));
        Assert.Equal(["Bravo", "Zulu"], tree[0].Children.Select(n => n.Segment));
    }

    [Fact]
    public void Build_DuplicateFolders_ProduceOneNode()
    {
        var tree = TemplateTreeBuilder.Build(["Gear", "Gear"], NoCounts);

        Assert.Single(tree);
    }

    [Fact]
    public void Build_NoInput_ReturnsEmpty()
    {
        Assert.Empty(TemplateTreeBuilder.Build([], NoCounts));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~TemplateTreeBuilderTests"
```

Expected: build error — `TemplateTreeBuilder` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `PenumbraOrganizer.Plugin/Organizer/Templates/TemplateTreeBuilder.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Templates;

public sealed record TemplateTreeNode(
    string Segment,
    string FullPath,
    int DirectCount,
    int TotalCount,
    IReadOnlyList<TemplateTreeNode> Children);

/// <summary>
/// Builds the preview tree from a template's declared folders plus a plan's per-folder counts.
/// Kept out of the draw method deliberately: nesting, intermediate parents and count roll-up are
/// real logic, and inside an ImGui frame they would be untestable.
///
/// The two inputs are different sets on purpose -- a template can declare an empty bucket the
/// author wants an importer to fill in themselves, and a plan can place mods in a folder the
/// declared list never mentioned. Both appear.
/// </summary>
public static class TemplateTreeBuilder
{
    private sealed class Builder
    {
        public int DirectCount;
        public readonly SortedDictionary<string, Builder> Children = new(StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<TemplateTreeNode> Build(
        IEnumerable<string> folders,
        IReadOnlyDictionary<string, int> folderCounts)
    {
        var roots = new SortedDictionary<string, Builder>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders)
            Ensure(roots, folder);

        foreach (var (folder, count) in folderCounts)
            Ensure(roots, folder).DirectCount = count;

        return Materialize(roots, parentPath: string.Empty);
    }

    private static Builder Ensure(SortedDictionary<string, Builder> roots, string folder)
    {
        var level = roots;
        Builder? current = null;

        foreach (var segment in folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!level.TryGetValue(segment, out var next))
                level[segment] = next = new Builder();

            current = next;
            level = next.Children;
        }

        // A folder that is empty or all separators has no node of its own; returning a throwaway
        // keeps the caller's assignment harmless rather than needing a null check.
        return current ?? new Builder();
    }

    private static IReadOnlyList<TemplateTreeNode> Materialize(
        SortedDictionary<string, Builder> level, string parentPath)
    {
        var nodes = new List<TemplateTreeNode>(level.Count);
        foreach (var (segment, builder) in level)
        {
            var fullPath = parentPath.Length == 0 ? segment : $"{parentPath}/{segment}";
            var children = Materialize(builder.Children, fullPath);
            var total = builder.DirectCount + children.Sum(child => child.TotalCount);

            nodes.Add(new TemplateTreeNode(segment, fullPath, builder.DirectCount, total, children));
        }

        return nodes;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~TemplateTreeBuilderTests"
```

Expected: PASS, 8 tests.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Templates/TemplateTreeBuilder.cs PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplateTreeBuilderTests.cs
git commit -m "feat: add pure preview tree builder"
```

---

### Task 6: Wire the store into Plugin

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs`

**Interfaces:**
- Consumes: `TemplateStore` (Task 2).
- Produces: `internal string Plugin.TemplatesDirectory` and `internal Organizer.Templates.TemplateStore Plugin.TemplateStore`.

This task has no unit test: it is path construction and object wiring in the same untested glue layer as `DefaultWorkbookFilePath` and `OperationsRoot`, matching this repo's established convention that only IPC and file-I/O glue in `Plugin.cs` goes unverified by unit tests.

- [ ] **Step 1: Add the path and the store**

In `PenumbraOrganizer.Plugin/Plugin.cs`, beside the existing `OperationsRoot` property, add:

```csharp
    internal string TemplatesDirectory => Path.Combine(PluginInterface.ConfigDirectory.FullName, "templates");
```

Then add a lazily-constructed store beside it, so the directory is not created until something actually uses templates:

```csharp
    private Organizer.Templates.TemplateStore? _templateStore;

    internal Organizer.Templates.TemplateStore TemplateStore =>
        _templateStore ??= new Organizer.Templates.TemplateStore(TemplatesDirectory);
```

- [ ] **Step 2: Confirm it builds**

```bash
dotnet build
```

Expected: Build succeeded, no new warnings.

- [ ] **Step 3: Confirm nothing regressed**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj
```

Expected: PASS, entire suite.

- [ ] **Step 4: Commit**

```bash
git add PenumbraOrganizer.Plugin/Plugin.cs
git commit -m "feat: expose the templates directory and store"
```

---

### Task 7: Templates tab

The tab itself. ImGui draw code is verified in-game rather than by unit tests, matching this repo's convention — every piece of real logic it needs was already extracted and tested in Tasks 1-5.

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`

**Interfaces:**
- Consumes: `Plugin.TemplateStore`, `TemplateStoreListing`, `StoredTemplate` (Tasks 2, 6), `TemplateTreeBuilder.Build` (Task 5), `TemplatePlanner.PlanFromDecoded` (Task 4), `OrganizerState.ScanGeneration` (Task 3), `OrganizerState.ApplyTemplate`, `TemplateCodec.DecodeJson`, `TemplateText.Preview` (T1).
- Produces: a `Templates` tab drawn from `DrawTemplatesTab()`.

- [ ] **Step 1: Add the tab's state fields**

In `MainWindow.cs`, beside the existing `_fileDialogManager` field, add:

```csharp
    private Organizer.Templates.TemplateStoreListing? _templateListing;
    private Organizer.Templates.StoredTemplate? _selectedTemplate;
    private Organizer.Templates.TemplateApplicationPlan? _templatePlan;
    private int _templatePlanScanGeneration = -1;
    private string? _templateStatus;
```

- [ ] **Step 2: Add the draw method**

Add this method to `MainWindow.cs`, next to the other `Draw*Tab` methods:

```csharp
    private void DrawTemplatesTab()
    {
        using var tab = ImRaii.TabItem("Templates");
        if (!tab)
            return;

        var gates = CurrentGates();

        ImGui.TextWrapped(
            "A template is a layout someone else shared. Importing one proposes where your mods "
            + "would go: mods you both have land where they put them, and everything else is placed "
            + "by the fallback strategy they chose. Nothing is applied until you review it.");
        ImGui.Separator();

        _templateListing ??= _plugin.TemplateStore.List();

        if (ImGui.Button("Refresh list"))
        {
            _templateListing = _plugin.TemplateStore.List();
            _selectedTemplate = null;
            _templatePlan = null;
        }

        ImGui.SameLine();
        if (ImGui.Button("Open templates folder"))
        {
            // Created on demand: the folder need not exist until someone actually wants it.
            // OpenFileWithDefaultApp is this window's existing helper and shell-executes a
            // directory path just as it does a file, so no new API is introduced here.
            Directory.CreateDirectory(_plugin.TemplatesDirectory);
            OpenFileWithDefaultApp(_plugin.TemplatesDirectory);
        }

        ImGui.SameLine();
        if (ImGui.Button("Import template file..."))
        {
            _fileDialogManager.OpenFileDialog(
                "Import Template",
                ".json",
                (success, paths) =>
                {
                    if (!success || paths.Count == 0)
                        return;

                    ImportTemplateFile(paths[0]);
                },
                selectionCountMax: 1);
        }

        if (_templateStatus is not null)
            ImGui.TextWrapped(_templateStatus);

        var listing = _templateListing!;

        foreach (var unreadable in listing.UnreadableFiles)
        {
            ImGui.TextColored(
                ImGuiColors.DalamudYellow, $"Skipped {unreadable}: not a readable template.");
        }

        if (listing.Templates.Count == 0)
        {
            ImGui.TextWrapped(
                "No templates yet. Use \"Import template file...\" to add a .json someone shared, "
                + "or drop one into the templates folder and hit Refresh list.");
            return;
        }

        ImGui.Separator();

        foreach (var stored in listing.Templates)
        {
            var label = string.IsNullOrWhiteSpace(stored.Template.Author)
                ? stored.Template.Name
                : $"{stored.Template.Name} — {stored.Template.Author}";

            if (ImGui.Selectable($"{label}##{stored.FileName}", _selectedTemplate == stored))
            {
                _selectedTemplate = stored;
                _templatePlan = null;
                _templateStatus = null;
            }
        }

        if (_selectedTemplate is null)
            return;

        ImGui.Separator();
        DrawTemplatePreview(_selectedTemplate, gates);
    }
```

- [ ] **Step 3: Add the preview and apply**

Add these methods to `MainWindow.cs` directly below `DrawTemplatesTab`:

```csharp
    private void DrawTemplatePreview(Organizer.Templates.StoredTemplate stored, ActivityGates gates)
    {
        if (!string.IsNullOrWhiteSpace(stored.Template.Description))
            ImGui.TextWrapped(stored.Template.Description);

        ImGui.BeginDisabled(!gates.CanStageProposals);
        if (ImGui.Button("Preview against my library"))
            BuildTemplatePlan(stored);
        ImGui.EndDisabled();

        if (!gates.CanStageProposals && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Library work is in progress.");

        if (_templatePlan is null)
            return;

        var plan = _templatePlan;
        var report = plan.Report;

        ImGui.TextWrapped(
            $"{report.RowsMatchedByEntry} of {report.ConsideredRows} mods matched this template; "
            + $"{report.RowsPlacedByFallback} placed by its fallback strategy; "
            + $"{report.ProtectedRows} skipped as protected. "
            + $"{report.TemplateEntriesUnmatched} of the template's entries matched nothing you own.");

        if (report.AmbiguousLocalMatchGroups > 0)
        {
            ImGui.TextColored(
                ImGuiColors.DalamudYellow,
                $"{report.AmbiguousLocalMatchGroups} template entries matched more than one of your "
                + "mods; every match is placed in the same folder.");
        }

        foreach (var warning in plan.Warnings.Take(20))
            ImGui.TextColored(ImGuiColors.DalamudYellow, DescribeWarning(warning));

        if (plan.Warnings.Count > 20)
            ImGui.TextDisabled($"...and {plan.Warnings.Count - 20} more.");

        ImGui.Separator();

        var tree = Organizer.Templates.TemplateTreeBuilder.Build(
            stored.Template.Folders, plan.FolderCounts);
        DrawTemplateTree(tree);

        ImGui.Separator();
        ImGui.BeginDisabled(!gates.CanStageProposals);
        if (ImGui.Button("Apply this template to my proposals"))
            ApplyTemplatePlan();
        ImGui.EndDisabled();
    }

    private static void DrawTemplateTree(IReadOnlyList<Organizer.Templates.TemplateTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            var label = node.TotalCount == 0
                ? $"{node.Segment} (empty)"
                : $"{node.Segment} ({node.TotalCount})";

            if (node.Children.Count == 0)
            {
                ImGui.TreeNodeEx(
                    $"{label}##{node.FullPath}",
                    ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen);
                continue;
            }

            using var treeNode = ImRaii.TreeNode($"{label}##{node.FullPath}");
            if (treeNode)
                DrawTemplateTree(node.Children);
        }
    }

    private static string DescribeWarning(Organizer.Templates.TemplateWarning warning) =>
        warning.Code switch
        {
            Organizer.Templates.TemplateWarningCode.UnmatchedTemplateEntry =>
                $"You do not have \"{warning.Subject}\".",
            Organizer.Templates.TemplateWarningCode.AmbiguousLocalMatch =>
                $"More than one of your mods is named \"{warning.Subject}\".",
            Organizer.Templates.TemplateWarningCode.InvalidEntryPath =>
                $"Skipped a bad entry: \"{warning.Subject}\".",
            Organizer.Templates.TemplateWarningCode.InvalidFolderPath =>
                $"Skipped a bad folder: \"{warning.Subject}\".",
            Organizer.Templates.TemplateWarningCode.UnknownFolderLabelKey =>
                $"Ignored an unknown folder label: \"{warning.Subject}\".",
            Organizer.Templates.TemplateWarningCode.DuplicateEntry =>
                $"Duplicate entry for \"{warning.Subject}\".",
            Organizer.Templates.TemplateWarningCode.ConflictingDuplicateEntry =>
                $"\"{warning.Subject}\" appears twice with different folders, so it was skipped.",
            _ => $"{warning.Code}: {warning.Subject}",
        };

    private void BuildTemplatePlan(Organizer.Templates.StoredTemplate stored)
    {
        try
        {
            // Re-read the file rather than planning from the listing's cached copy: the listing
            // may be minutes old and the file may have been replaced on disk since.
            var json = File.ReadAllText(Path.Combine(_plugin.TemplatesDirectory, stored.FileName));
            var decoded = Organizer.Templates.TemplateCodec.DecodeJson(json);
            if (!decoded.Succeeded)
            {
                _lastError = $"Template could not be read: {decoded.ErrorDetail}";
                _templatePlan = null;
                return;
            }

            _templatePlan = Organizer.Templates.TemplatePlanner.PlanFromDecoded(
                decoded, _plugin.OrganizerState.Mods, _creatorCanonicalizer.Canonicalize);
            _templatePlanScanGeneration = _plugin.OrganizerState.ScanGeneration;
            _templateStatus = null;
            _lastError = null;
        }
        catch (Exception ex)
        {
            _lastError = $"Template preview failed: {ex.Message}";
            _templatePlan = null;
        }
    }

    private void ApplyTemplatePlan()
    {
        if (_templatePlan is null)
            return;

        // A plan describes the rows it was built from. If a rescan landed between the preview and
        // this click, applying it would stage a partial result while reporting the old counts --
        // so refuse and make the user look at a fresh preview instead.
        if (_templatePlanScanGeneration != _plugin.OrganizerState.ScanGeneration)
        {
            _templatePlan = null;
            _lastError = "Your library was rescanned after this preview. Preview again before applying.";
            return;
        }

        if (!CurrentGates().CanStageProposals)
        {
            _lastError = "Applying the template was cancelled because library work started.";
            return;
        }

        var report = _plugin.OrganizerState.ApplyTemplate(_templatePlan);
        _templateStatus =
            $"Staged {report.RowsMatchedByEntry + report.RowsPlacedByFallback} proposals. "
            + "Open Review Changes to check them before applying.";
        _lastError = null;
    }

    private void ImportTemplateFile(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var fileName = _plugin.TemplateStore.Save(json, Path.GetFileNameWithoutExtension(path));

            _templateListing = _plugin.TemplateStore.List();
            _selectedTemplate = _templateListing.Templates
                .FirstOrDefault(t => string.Equals(t.FileName, fileName, StringComparison.OrdinalIgnoreCase));
            _templatePlan = null;
            _templateStatus = $"Imported as {fileName}.";
            _lastError = null;
        }
        catch (Exception ex)
        {
            _lastError = $"Template import failed: {ex.Message}";
        }
    }
```

Note `Save` is called with the file's own stem rather than the document's `name`, so an importer recognizes the file they picked. The store still slugifies it, so a hostile filename cannot escape the directory.

- [ ] **Step 4: Register the tab**

The tab bar calls each tab's draw method in order at `MainWindow.cs:128-133`:

```csharp
                DrawScanTab();
                DrawProtectTab();
                DrawSortTab();
                DrawReviewTab();
                DrawHistoryTab();
                DrawSearchTab();
```

Add one line after `DrawSearchTab();`, so Templates sits at the end of the strip:

```csharp
                DrawTemplatesTab();
```

- [ ] **Step 5: Confirm it builds and nothing regressed**

```bash
dotnet build
```

Expected: Build succeeded, no new warnings.

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj
```

Expected: PASS, entire suite.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/MainWindow.cs
git commit -m "feat: add Templates tab with import, preview and apply"
```

---

### Task 8: Documentation

**Files:**
- Modify: `docs/USER_GUIDE.md`
- Modify: `docs/ROADMAP.md`

- [ ] **Step 1: Document the tab in the user guide**

`docs/USER_GUIDE.md` documents the plugin tab by tab. Add a `## Templates` section, placed to match the tab's position in the strip (after Search), written in the same voice as the existing sections:

```markdown
## Templates

A template is an organization layout someone else shared as a `.json` file. Importing one
proposes where your mods would go: mods you and the template's author both have land in the
folder they chose, and everything else is placed by the fallback sort strategy they picked, using
their folder names.

1. Get a template file from whoever shared it and click **Import template file...**, or drop the
   file into the templates folder yourself (**Open templates folder** shows you where) and click
   **Refresh list**.
2. Select it in the list to see who made it and what it is for.
3. Click **Preview against my library**. You get a count of how many of your mods the template
   matched, how many its fallback strategy placed, and a browsable tree of the resulting folders
   with the number of mods in each.
4. If it looks right, click **Apply this template to my proposals**, then open **Review Changes**
   to check the result and apply it like any other sort.

Nothing is written to Penumbra until you apply from Review Changes, exactly as with the sort
buttons.

A few things worth knowing:

- **A template never sees your mod list.** It matches on mod names, so it only affects mods you
  already have. Mods its author never had are placed by the fallback strategy, not left behind.
- **Matching is on the mod's name**, ignoring case, install suffixes like `_1_1_0`, and bracketed
  tags. A mod you renamed will not match, and will be placed by the fallback strategy instead.
- **Protected mods are never moved**, the same as with any sort.
- If you rescan your library after previewing, the preview is discarded — preview again before
  applying.
```

- [ ] **Step 2: Update the roadmap**

In `docs/ROADMAP.md`, replace the T1 bullet added in the previous phase with one covering T1 and T2 together, keeping the same style as its neighbours:

```markdown
- **Community organization templates, Phases T1 and T2 — implemented, not yet verified in-game.**
  A portable, identity-free template document (normalized mod name → folder entries, an
  author-declared fallback strategy, and a longest-prefix folder-label rename map) with staged
  validation and a `POT1:` share-code transport, plus a Templates tab that imports a `.json`
  someone shared, previews the resulting folder tree and match counts against the current
  library, and stages proposals through the existing Review Changes pipeline. Unlike the
  workbook, a template carries no `installationIdentity`, so it travels between users. T3
  (export review-and-trim, clipboard sharing) is not started, and its review-and-trim screen is a
  privacy mechanism rather than polish, since export publishes the author's mod names. Verifying
  the feature end to end needs two libraries and therefore a second tester. Design:
  `docs/superpowers/specs/2026-07-30-community-templates-design.md`. Plans:
  `docs/superpowers/plans/2026-07-30-community-templates-t1-core.md`,
  `docs/superpowers/plans/2026-07-31-community-templates-t2-import-and-preview.md`.
```

- [ ] **Step 3: Confirm only docs changed**

```bash
git status --short
```

Expected: only `docs/USER_GUIDE.md` and `docs/ROADMAP.md`.

- [ ] **Step 4: Commit**

```bash
git add docs/USER_GUIDE.md docs/ROADMAP.md
git commit -m "docs: document the Templates tab"
```

---

## Phase T2 Completion Criteria

- [ ] `dotnet build` succeeds and introduces no new warnings (the one xUnit2017 warning in `ApplyPlannerTests.cs:306` pre-dates this work and stays).
- [ ] `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj` passes in full, with every pre-existing test unchanged. Expected total: 1018 baseline + 49 new = 1067.
- [ ] No export affordance of any kind was added (T3 scope).
- [ ] `TemplatePathValidator.IsValidFolder` is not used anywhere to validate a filename.

## Manual verification (in-game, after the phase)

Unit tests cannot cover the ImGui layer, so these are the checks that matter once the build is loaded:

1. With no `templates/` folder at all, the tab opens and explains what to do rather than erroring.
2. **Open templates folder** creates and opens the directory.
3. Importing a `.json` copies it in, selects it, and lists it by the document's own name.
4. Importing the same file twice produces a second entry rather than overwriting the first.
5. A deliberately corrupt `.json` dropped into the folder is listed as skipped, and the valid templates still appear.
6. Preview shows counts and a tree whose numbers add up to the matched-plus-fallback total.
7. Apply stages proposals visible in Review Changes; Apply there behaves exactly as it does after a sort button.
8. Rescanning between preview and apply is refused with the "preview again" message rather than partially applying.
9. Protected mods are untouched by an applied template.

**Still unverifiable alone:** the cross-library claim. Exercising it for real needs a template exported from a second person's library, which arrives with T3 — until then, in-game testing can only use hand-written template files.
