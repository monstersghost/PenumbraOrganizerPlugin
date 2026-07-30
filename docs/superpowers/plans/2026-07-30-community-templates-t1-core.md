# Community Templates — Phase T1 (Format and Application Core) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the portable organization-template document format, its validation and transport codec, and a pure planner that turns a template plus a mod library into proposed destination folders applied through `OrganizerState`'s existing sort pipeline.

**Architecture:** Everything in this phase is pure, unit-testable logic in a new `PenumbraOrganizer.Plugin/Organizer/Templates/` folder, plus one behavior-preserving refactor of `OrganizerState`'s folder-selection expressions so the planner and the existing seven sort strategies share one implementation. Application goes through `OrganizerState`'s private `Sort`/`FinishProposals` path so pinning, collision disambiguation, and protected-row filtering are inherited unchanged. No UI, no file I/O, no clipboard, no Penumbra IPC in this phase.

**Tech Stack:** C# / .NET 10 (`net10.0-windows7.0`), xunit 2.5.3, `System.Text.Json`, `System.IO.Compression.DeflateStream`. No new NuGet packages.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-07-30-community-templates-design.md`. Every requirement there applies; this plan covers phase **T1 only** (no UI, no `TemplateStore`, no export review screen, no clipboard wiring — those are T2/T3).
- All new production code lives in `PenumbraOrganizer.Plugin/Organizer/Templates/`, namespace `PenumbraOrganizer.Plugin.Organizer.Templates`.
- All new tests live in `PenumbraOrganizer.Plugin.Tests/Organizer/Templates/`, namespace `PenumbraOrganizer.Plugin.Tests.Organizer.Templates`.
- `formatVersion` supported by this phase: exactly `1`.
- Share-code prefix: exactly `POT1:`.
- Hard limits (verbatim from the spec, all enforced during decode): compressed input 1 MB; decompressed size 8 MB enforced *during* inflation; entries 20,000; folders 5,000; `folderLabels` keys 500; any single string 512 chars; path depth 16 segments; segment length 128 chars.
- The seven fallback strategy names are exactly: `Creator`, `ModType`, `ModTypeDetailed`, `TypeThenCreator`, `TypeThenCreatorFlat`, `CreatorThenType`, `CreatorThenTypeFlat`.
- Nothing unvalidated may reach `OrganizerState`: only `ValidatedOrganizationTemplate` is accepted by the planner.
- Existing behavior must not change. `dotnet build` and the full existing test suite must pass after
  every task. Baseline is 886 passing tests and one pre-existing xUnit2017 warning in
  `ApplyPlannerTests.cs:306` — introduce no new warnings; do not fix that one here.
- Commit after every task. Never use `--no-verify`.

## File Structure

| File | Responsibility |
| --- | --- |
| `Organizer/Templates/ModNameNormalizer.cs` | The one normalization algorithm, used by both export and import |
| `Organizer/Templates/TemplateModels.cs` | Document records, `TemplateFallbackStrategy`, `TemplateWarning`, `TemplateWarningCode` |
| `Organizer/Templates/TemplateLimits.cs` | The hard-limit constants, in one place |
| `Organizer/Templates/TemplatePathValidator.cs` | Folder/segment validation for every externally supplied path |
| `Organizer/Templates/TemplateDuplicateResolver.cs` | The one duplicate/collision rule, shared by import and export |
| `Organizer/Templates/TemplateFolderLabels.cs` | Longest-prefix, segment-boundary folder renaming |
| `Organizer/Templates/TemplateCodec.cs` | Staged decode/encode: transport, JSON, schema, semantic, `ValidatedOrganizationTemplate` |
| `Organizer/SortFolderSelectors.cs` | Folder-selection expressions shared by the seven sorts and the planner |
| `Organizer/Templates/TemplatePlanner.cs` | Pure `Plan(...)` → `TemplateApplicationPlan` consumed by preview (T2) and apply |
| `Organizer/OrganizerState.cs` (modify) | Delegate the seven sorts to `SortFolderSelectors`; add `ApplyTemplate(plan)` |

---

### Task 1: ModNameNormalizer

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Templates/ModNameNormalizer.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Templates/ModNameNormalizerTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `public static string ModNameNormalizer.Normalize(string name)`.

- [ ] **Step 1: Write the failing test**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/Templates/ModNameNormalizerTests.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Tests.Organizer.Templates;

using PenumbraOrganizer.Plugin.Organizer.Templates;

public class ModNameNormalizerTests
{
    [Theory]
    [InlineData("Bibo+ Medieval (Penumbra)_1_1_0", "bibo+ medieval")]
    [InlineData("Bibo+  Medieval", "bibo+ medieval")]
    [InlineData("Bibo+ Medieval Redux", "bibo+ medieval redux")]
    [InlineData("Emperor's New Fists", "emperors new fists")]
    [InlineData("[WIP] Foo-Bar", "foo bar")]
    [InlineData("My Mod v2.1", "my mod")]
    [InlineData("Gear 2000", "gear 2000")]
    [InlineData("Café Outfit", "café outfit")]
    public void Normalize_SpecTable(string input, string expected)
    {
        Assert.Equal(expected, ModNameNormalizer.Normalize(input));
    }

    // The whole feature's correctness rests on these two NOT collapsing together.
    [Fact]
    public void Normalize_DistinctNames_DoNotCollide()
    {
        Assert.NotEqual(
            ModNameNormalizer.Normalize("Bibo+ Medieval"),
            ModNameNormalizer.Normalize("Bibo+ Medieval Redux"));
    }

    // A general "strip trailing digits" rule would wrongly turn this into "gear".
    [Fact]
    public void Normalize_TrailingDigitsWithoutSeparatorPrefix_ArePreserved()
    {
        Assert.Equal("gear 2000", ModNameNormalizer.Normalize("Gear 2000"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("___")]
    public void Normalize_NothingSignificant_ReturnsEmpty(string input)
    {
        Assert.Equal("", ModNameNormalizer.Normalize(input));
    }

    [Fact]
    public void Normalize_IsIdempotent()
    {
        var once = ModNameNormalizer.Normalize("Bibo+ Medieval (Penumbra)_1_1_0");

        Assert.Equal(once, ModNameNormalizer.Normalize(once));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~ModNameNormalizerTests"
```

Expected: build error — `ModNameNormalizer` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `PenumbraOrganizer.Plugin/Organizer/Templates/ModNameNormalizer.cs`:

```csharp
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PenumbraOrganizer.Plugin.Organizer.Templates;

/// <summary>
/// The single normalization used on both sides of a template: the author's mod names at export
/// and the importer's mod names at import. This is the feature's compatibility surface — changing
/// it changes which entries match in templates that are already published, so any change to it is
/// a formatVersion question, not a free bug fix. See the spec's "Name normalization" section.
/// </summary>
public static class ModNameNormalizer
{
    // Bracketed tag groups: "[WIP] Foo" -> " Foo".
    private static readonly Regex BracketGroups = new(@"\[[^\]]*\]|\{[^}]*\}", RegexOptions.Compiled);

    // Only the two suffix forms that actually occur: Penumbra's own dealt "_1_1_0", and an
    // author's "v2.1". Deliberately NOT a general trailing-digit rule, which would destroy
    // legitimate names like "Gear 2000".
    private static readonly Regex PenumbraVersionSuffix = new(@"(?:_\d+)+$", RegexOptions.Compiled);
    private static readonly Regex AuthorVersionSuffix = new(@"[ _\-.]v\d+(?:[._]\d+)*$", RegexOptions.Compiled);

    public static string Normalize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var text = name.Trim().ToLowerInvariant();

        // Apostrophes are deleted rather than spaced, so "Emperor's" -> "emperors".
        text = text.Replace("'", string.Empty).Replace("’", string.Empty);

        text = BracketGroups.Replace(text, " ");
        text = text.Replace("(penumbra)", " ");

        text = AuthorVersionSuffix.Replace(text, string.Empty);
        text = PenumbraVersionSuffix.Replace(text, string.Empty);

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            // '+' is preserved explicitly: it is load-bearing in this ecosystem ("Bibo+", "YAB+").
            // Unicode letters and digits are preserved, so accented and non-Latin names survive.
            if (character == '+' || char.IsLetter(character) || char.IsDigit(character))
                builder.Append(character);
            else
                builder.Append(' ');
        }

        return CollapseWhitespace(builder.ToString());
    }

    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var character in text)
        {
            if (character == ' ')
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~ModNameNormalizerTests"
```

Expected: PASS, 13 tests.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Templates/ModNameNormalizer.cs PenumbraOrganizer.Plugin.Tests/Organizer/Templates/ModNameNormalizerTests.cs
git commit -m "feat: add mod name normalizer for organization templates"
```

---

### Task 2: Document models, limits, and warning codes

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Templates/TemplateModels.cs`
- Create: `PenumbraOrganizer.Plugin/Organizer/Templates/TemplateLimits.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplateModelsTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `TemplateFallbackStrategy` (enum, 7 values), `TemplateEntry(string N, string F)`, `OrganizationTemplate`, `ValidatedOrganizationTemplate`, `TemplateWarningCode` (enum), `TemplateWarning(TemplateWarningCode Code, string Subject)`, `TemplateLimits` (constants).

- [ ] **Step 1: Write the failing test**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplateModelsTests.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Tests.Organizer.Templates;

using System.Text.Json;
using PenumbraOrganizer.Plugin.Organizer.Templates;

public class TemplateModelsTests
{
    // The seven names are a wire contract: a template naming one of these must keep working.
    [Theory]
    [InlineData("Creator")]
    [InlineData("ModType")]
    [InlineData("ModTypeDetailed")]
    [InlineData("TypeThenCreator")]
    [InlineData("TypeThenCreatorFlat")]
    [InlineData("CreatorThenType")]
    [InlineData("CreatorThenTypeFlat")]
    public void FallbackStrategy_SpecNames_Parse(string name)
    {
        Assert.True(Enum.TryParse<TemplateFallbackStrategy>(name, ignoreCase: false, out _));
    }

    [Fact]
    public void FallbackStrategy_HasExactlySevenValues()
    {
        Assert.Equal(7, Enum.GetValues<TemplateFallbackStrategy>().Length);
    }

    // Entries dominate payload size, so they serialize as "n"/"f", not as property names.
    [Fact]
    public void TemplateEntry_SerializesWithShortFieldNames()
    {
        var json = JsonSerializer.Serialize(
            new TemplateEntry("bibo+ medieval", "Gear/Top"), TemplateJson.SerializerOptions);

        Assert.Equal("{\"n\":\"bibo+ medieval\",\"f\":\"Gear/Top\"}", json);
    }

    // The default encoder escapes '+' as + and every non-ASCII char as \uXXXX -- six bytes
    // where one belongs. Mod names are full of both ("Bibo+", "Café"), and payload size decides
    // whether a share code fits in a chat message, so the relaxed encoder is load-bearing rather
    // than cosmetic.
    [Fact]
    public void SerializerOptions_DoNotEscapePlusOrNonAscii()
    {
        var json = JsonSerializer.Serialize(
            new TemplateEntry("café+", "Gear"), TemplateJson.SerializerOptions);

        Assert.DoesNotContain("\\u", json);
    }

    [Fact]
    public void OrganizationTemplate_RoundTripsThroughJson()
    {
        var template = new OrganizationTemplate
        {
            FormatVersion = 1,
            Name = "Detailed type sort",
            Author = "Akako",
            Description = "Character mods up front.",
            FallbackStrategy = "TypeThenCreator",
            FolderLabels = new Dictionary<string, string> { ["Others"] = "_Unsorted" },
            Folders = ["Characters", "Gear/Top"],
            Entries = [new TemplateEntry("bibo+ medieval", "Gear/Top")],
        };

        var round = JsonSerializer.Deserialize<OrganizationTemplate>(JsonSerializer.Serialize(template))!;

        Assert.Equal(1, round.FormatVersion);
        Assert.Equal("Detailed type sort", round.Name);
        Assert.Equal("TypeThenCreator", round.FallbackStrategy);
        Assert.Equal("_Unsorted", round.FolderLabels["Others"]);
        Assert.Equal(["Characters", "Gear/Top"], round.Folders);
        Assert.Equal("bibo+ medieval", round.Entries[0].N);
    }

    // Provenance is informational only and must never be required to import.
    [Fact]
    public void OrganizationTemplate_MissingProvenance_DeserializesWithNulls()
    {
        var json = """{"formatVersion":1,"name":"x","fallbackStrategy":"ModType"}""";

        var template = JsonSerializer.Deserialize<OrganizationTemplate>(json)!;

        Assert.Null(template.CreatedWithVersion);
        Assert.Null(template.CreatedAtUtc);
        Assert.Empty(template.Entries);
        Assert.Empty(template.Folders);
        Assert.Empty(template.FolderLabels);
    }

    [Fact]
    public void Limits_MatchSpec()
    {
        Assert.Equal(1_048_576, TemplateLimits.MaxCompressedBytes);
        Assert.Equal(8_388_608, TemplateLimits.MaxDecompressedBytes);
        Assert.Equal(20_000, TemplateLimits.MaxEntries);
        Assert.Equal(5_000, TemplateLimits.MaxFolders);
        Assert.Equal(500, TemplateLimits.MaxFolderLabels);
        Assert.Equal(512, TemplateLimits.MaxStringLength);
        Assert.Equal(16, TemplateLimits.MaxPathDepth);
        Assert.Equal(128, TemplateLimits.MaxSegmentLength);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~TemplateModelsTests"
```

Expected: build error — `TemplateFallbackStrategy` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `PenumbraOrganizer.Plugin/Organizer/Templates/TemplateLimits.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Templates;

/// <summary>
/// Hard limits enforced during decode. Successful inflation does not make a document
/// structurally sane, so these are checked separately from the transport size caps.
/// </summary>
public static class TemplateLimits
{
    public const int MaxCompressedBytes = 1_048_576;    // 1 MB
    public const int MaxDecompressedBytes = 8_388_608;  // 8 MB
    public const int MaxEntries = 20_000;
    public const int MaxFolders = 5_000;
    public const int MaxFolderLabels = 500;
    public const int MaxStringLength = 512;
    public const int MaxPathDepth = 16;
    public const int MaxSegmentLength = 128;
}
```

Create `PenumbraOrganizer.Plugin/Organizer/Templates/TemplateModels.cs`:

```csharp
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PenumbraOrganizer.Plugin.Organizer.Templates;

/// <summary>
/// The one set of serializer options for template documents, shared by the models' tests and by
/// TemplateCodec, so a document written by one path is byte-identical to one written by the other.
///
/// The relaxed encoder matters for size, not looks: the default encoder escapes '+' as + and
/// every non-ASCII character as \uXXXX -- six bytes where one belongs. Mod names are full of both
/// ("Bibo+", "Café"), and payload size is what decides whether a share code fits in a chat
/// message. "Unsafe" here refers to embedding output directly in HTML, which templates never do:
/// they go to a .json file and to the clipboard.
/// </summary>
public static class TemplateJson
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };
}

/// <summary>
/// Names one of OrganizerState's seven existing sort strategies, used for mods a template has no
/// entry for. These names are a wire contract — renaming a member breaks published templates.
/// </summary>
public enum TemplateFallbackStrategy
{
    Creator,
    ModType,
    ModTypeDetailed,
    TypeThenCreator,
    TypeThenCreatorFlat,
    CreatorThenType,
    CreatorThenTypeFlat,
}

public enum TemplateWarningCode
{
    UnknownFolderLabelKey,
    InvalidEntryPath,
    DuplicateEntry,
    ConflictingDuplicateEntry,
    ExportNameCollision,
    UnmatchedTemplateEntry,
    AmbiguousLocalMatch,
}

/// <summary>
/// Structured rather than pre-formatted prose so the UI formats consistently and tests assert on
/// codes instead of comparing strings.
/// </summary>
public sealed record TemplateWarning(TemplateWarningCode Code, string Subject);

/// <summary>
/// One template entry. Short JSON field names because entries dominate the payload size, which
/// decides whether a share code fits in a chat message.
/// </summary>
public sealed record TemplateEntry(
    [property: JsonPropertyName("n")] string N,
    [property: JsonPropertyName("f")] string F);

/// <summary>
/// The raw document as deserialized. Untrusted: FallbackStrategy is a string here because an
/// unknown value must produce a stated error rather than a deserialization exception, and entry
/// keys are re-normalized rather than believed. Use TemplateCodec to obtain a validated template.
/// </summary>
public sealed class OrganizationTemplate
{
    [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("author")] public string? Author { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }

    // Informational provenance only: never validated beyond being a string, never blocks import.
    [JsonPropertyName("createdWithVersion")] public string? CreatedWithVersion { get; set; }
    [JsonPropertyName("createdAtUtc")] public string? CreatedAtUtc { get; set; }

    [JsonPropertyName("fallbackStrategy")] public string FallbackStrategy { get; set; } = string.Empty;
    [JsonPropertyName("folderLabels")] public Dictionary<string, string> FolderLabels { get; set; } = new();
    [JsonPropertyName("folders")] public List<string> Folders { get; set; } = [];
    [JsonPropertyName("entries")] public List<TemplateEntry> Entries { get; set; } = [];
}

/// <summary>
/// A template that has passed every stage of TemplateCodec's validation. This is the only shape
/// the planner accepts, so unvalidated external input cannot reach OrganizerState.
/// </summary>
public sealed record ValidatedOrganizationTemplate(
    string Name,
    string? Author,
    string? Description,
    TemplateFallbackStrategy FallbackStrategy,
    IReadOnlyDictionary<string, string> FolderLabels,
    IReadOnlyList<string> Folders,
    IReadOnlyDictionary<string, string> EntriesByNormalizedName);
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~TemplateModelsTests"
```

Expected: PASS, 13 tests.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Templates/TemplateModels.cs PenumbraOrganizer.Plugin/Organizer/Templates/TemplateLimits.cs PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplateModelsTests.cs
git commit -m "feat: add organization template document models and limits"
```

---

### Task 3: TemplatePathValidator

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Templates/TemplatePathValidator.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplatePathValidatorTests.cs`

**Interfaces:**
- Consumes: `TemplateLimits` (Task 2).
- Produces: `public static bool TemplatePathValidator.IsValidFolder(string folder)` — accepts `""` (root) and any well-formed `/`-separated folder path within the depth/segment limits.

- [ ] **Step 1: Write the failing test**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplatePathValidatorTests.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Tests.Organizer.Templates;

using PenumbraOrganizer.Plugin.Organizer.Templates;

public class TemplatePathValidatorTests
{
    [Theory]
    [InlineData("")]              // root is valid, matching the workbook's folder-only convention
    [InlineData("Gear")]
    [InlineData("Gear/Head")]
    [InlineData("Animation and VFX/Emotes")]
    [InlineData("_Unsorted")]
    [InlineData("Café")]
    public void IsValidFolder_WellFormed_ReturnsTrue(string folder)
    {
        Assert.True(TemplatePathValidator.IsValidFolder(folder));
    }

    [Theory]
    [InlineData("/Gear")]         // leading separator
    [InlineData("Gear/")]         // trailing separator
    [InlineData("Gear//Head")]    // empty segment
    [InlineData("Gear/ /Head")]   // whitespace-only segment
    [InlineData("Gear\u0007Head")]  // control character
    [InlineData("Gear\nHead")]
    public void IsValidFolder_Malformed_ReturnsFalse(string folder)
    {
        Assert.False(TemplatePathValidator.IsValidFolder(folder));
    }

    [Fact]
    public void IsValidFolder_AtDepthLimit_ReturnsTrue()
    {
        var folder = string.Join('/', Enumerable.Repeat("a", TemplateLimits.MaxPathDepth));

        Assert.True(TemplatePathValidator.IsValidFolder(folder));
    }

    [Fact]
    public void IsValidFolder_OverDepthLimit_ReturnsFalse()
    {
        var folder = string.Join('/', Enumerable.Repeat("a", TemplateLimits.MaxPathDepth + 1));

        Assert.False(TemplatePathValidator.IsValidFolder(folder));
    }

    [Fact]
    public void IsValidFolder_OverSegmentLengthLimit_ReturnsFalse()
    {
        var folder = new string('a', TemplateLimits.MaxSegmentLength + 1);

        Assert.False(TemplatePathValidator.IsValidFolder(folder));
    }

    [Fact]
    public void IsValidFolder_OverTotalStringLimit_ReturnsFalse()
    {
        var folder = string.Join('/', Enumerable.Repeat(new string('a', 100), 8));

        Assert.True(folder.Length > TemplateLimits.MaxStringLength);
        Assert.False(TemplatePathValidator.IsValidFolder(folder));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~TemplatePathValidatorTests"
```

Expected: build error — `TemplatePathValidator` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `PenumbraOrganizer.Plugin/Organizer/Templates/TemplatePathValidator.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Templates;

/// <summary>
/// Validates every externally supplied path in a template — entry destinations, the folders list,
/// and both the keys and the replacement values of folderLabels. Validating only entry
/// destinations would leave three other routes for a malformed path to reach a proposal.
/// </summary>
public static class TemplatePathValidator
{
    /// <summary>
    /// True for "" (root, matching the workbook's folder-only convention) and for any
    /// '/'-separated path with no leading/trailing separator, no empty or whitespace-only
    /// segment, no control characters, and within the depth, segment-length, and total-length
    /// limits.
    /// </summary>
    public static bool IsValidFolder(string folder)
    {
        if (folder.Length == 0)
            return true;

        if (folder.Length > TemplateLimits.MaxStringLength)
            return false;

        if (folder.StartsWith('/') || folder.EndsWith('/'))
            return false;

        if (folder.Any(char.IsControl))
            return false;

        var segments = folder.Split('/');
        if (segments.Length > TemplateLimits.MaxPathDepth)
            return false;

        return segments.All(segment =>
            !string.IsNullOrWhiteSpace(segment) && segment.Length <= TemplateLimits.MaxSegmentLength);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~TemplatePathValidatorTests"
```

Expected: PASS, 16 tests.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Templates/TemplatePathValidator.cs PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplatePathValidatorTests.cs
git commit -m "feat: add template path validator"
```

---

### Task 4: TemplateDuplicateResolver

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Templates/TemplateDuplicateResolver.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplateDuplicateResolverTests.cs`

**Interfaces:**
- Consumes: `TemplateEntry`, `TemplateWarning`, `TemplateWarningCode` (Task 2).
- Produces:
  ```csharp
  public sealed record TemplateDuplicateResolution(
      IReadOnlyDictionary<string, string> EntriesByNormalizedName,
      IReadOnlyList<TemplateWarning> Warnings);

  public static TemplateDuplicateResolution TemplateDuplicateResolver.Resolve(
      IEnumerable<TemplateEntry> entries);
  ```

- [ ] **Step 1: Write the failing test**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplateDuplicateResolverTests.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Tests.Organizer.Templates;

using PenumbraOrganizer.Plugin.Organizer.Templates;

public class TemplateDuplicateResolverTests
{
    [Fact]
    public void Resolve_NoDuplicates_KeepsEveryEntryWithoutWarnings()
    {
        var result = TemplateDuplicateResolver.Resolve([
            new TemplateEntry("bibo+ medieval", "Gear/Top"),
            new TemplateEntry("some hair", "Hair"),
        ]);

        Assert.Equal(2, result.EntriesByNormalizedName.Count);
        Assert.Equal("Gear/Top", result.EntriesByNormalizedName["bibo+ medieval"]);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Resolve_DuplicatesAgreeingOnFolder_KeepsOneAndWarns()
    {
        var result = TemplateDuplicateResolver.Resolve([
            new TemplateEntry("bibo+ medieval", "Gear/Top"),
            new TemplateEntry("bibo+ medieval", "Gear/Top"),
        ]);

        Assert.Equal("Gear/Top", result.EntriesByNormalizedName["bibo+ medieval"]);
        Assert.Equal(
            [new TemplateWarning(TemplateWarningCode.DuplicateEntry, "bibo+ medieval")],
            result.Warnings);
    }

    // The whole group is dropped rather than picking one: keeping an arbitrary winner publishes a
    // silent choice between two genuinely different intents.
    [Fact]
    public void Resolve_DuplicatesDisagreeingOnFolder_KeepsNoneAndWarns()
    {
        var result = TemplateDuplicateResolver.Resolve([
            new TemplateEntry("bibo+ medieval", "Gear/Top"),
            new TemplateEntry("bibo+ medieval", "Characters/Nyx"),
        ]);

        Assert.False(result.EntriesByNormalizedName.ContainsKey("bibo+ medieval"));
        Assert.Equal(
            [new TemplateWarning(TemplateWarningCode.ConflictingDuplicateEntry, "bibo+ medieval")],
            result.Warnings);
    }

    // Array order must never decide meaning: reversing the input changes nothing.
    [Fact]
    public void Resolve_IsOrderIndependent()
    {
        TemplateEntry[] entries = [
            new("a", "Gear"),
            new("dup", "Gear/Top"),
            new("dup", "Characters"),
            new("b", "Hair"),
        ];

        var forward = TemplateDuplicateResolver.Resolve(entries);
        var reversed = TemplateDuplicateResolver.Resolve(entries.Reverse());

        Assert.Equal(
            forward.EntriesByNormalizedName.OrderBy(p => p.Key, StringComparer.Ordinal),
            reversed.EntriesByNormalizedName.OrderBy(p => p.Key, StringComparer.Ordinal));
        Assert.Equal(forward.Warnings, reversed.Warnings);
    }

    // The reversal test above only exercises valid entries. Invalid entries warn from a different
    // code path, so they need their own reversal coverage -- this is the case that caught a real
    // ordering defect during review.
    [Fact]
    public void Resolve_InvalidEntriesMixedWithDuplicates_IsOrderIndependent()
    {
        TemplateEntry[] entries = [
            new("bad one", "Gear//Top"),
            new("dup", "Gear/Top"),
            new("bad two", "Foo//Bar"),
            new("dup", "Characters"),
            new("fine", "Hair"),
        ];

        var forward = TemplateDuplicateResolver.Resolve(entries);
        var reversed = TemplateDuplicateResolver.Resolve(entries.Reverse());

        Assert.Equal(forward.Warnings, reversed.Warnings);
        Assert.Equal(
            forward.EntriesByNormalizedName.OrderBy(p => p.Key, StringComparer.Ordinal),
            reversed.EntriesByNormalizedName.OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void Resolve_InvalidDestinationPath_SkipsEntryAndWarns()
    {
        var result = TemplateDuplicateResolver.Resolve([new TemplateEntry("bad", "Gear//Top")]);

        Assert.Empty(result.EntriesByNormalizedName);
        Assert.Equal(
            [new TemplateWarning(TemplateWarningCode.InvalidEntryPath, "bad")],
            result.Warnings);
    }

    // Entry keys are external input, not something the author's tool can be trusted to have done.
    [Fact]
    public void Resolve_UnnormalizedKey_IsRenormalized()
    {
        var result = TemplateDuplicateResolver.Resolve([
            new TemplateEntry("Bibo+ Medieval (Penumbra)_1_1_0", "Gear/Top"),
        ]);

        Assert.Equal("Gear/Top", result.EntriesByNormalizedName["bibo+ medieval"]);
    }

    // Re-normalization can itself create a collision; the same rule then applies to the result.
    [Fact]
    public void Resolve_RenormalizationCreatingConflict_DropsGroup()
    {
        var result = TemplateDuplicateResolver.Resolve([
            new TemplateEntry("Bibo+ Medieval_1_0", "Gear/Top"),
            new TemplateEntry("bibo+  medieval", "Characters/Nyx"),
        ]);

        Assert.Empty(result.EntriesByNormalizedName);
        Assert.Equal(
            [new TemplateWarning(TemplateWarningCode.ConflictingDuplicateEntry, "bibo+ medieval")],
            result.Warnings);
    }

    [Fact]
    public void Resolve_KeyNormalizingToEmpty_IsSkipped()
    {
        var result = TemplateDuplicateResolver.Resolve([new TemplateEntry("___", "Gear")]);

        Assert.Empty(result.EntriesByNormalizedName);
        Assert.Equal(
            [new TemplateWarning(TemplateWarningCode.InvalidEntryPath, "___")],
            result.Warnings);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~TemplateDuplicateResolverTests"
```

Expected: build error — `TemplateDuplicateResolver` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `PenumbraOrganizer.Plugin/Organizer/Templates/TemplateDuplicateResolver.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Templates;

public sealed record TemplateDuplicateResolution(
    IReadOnlyDictionary<string, string> EntriesByNormalizedName,
    IReadOnlyList<TemplateWarning> Warnings);

/// <summary>
/// The one duplicate rule, used for in-document duplicates on import and for normalized-name
/// collisions among the author's own mods on export — they are the same problem.
///
/// Agreeing duplicates collapse to one entry with a warning. Disagreeing duplicates drop the
/// whole group: keeping an arbitrary winner would publish a silent choice between two different
/// intents. "Last entry wins" is deliberately not used, because JSON array ordering must never
/// change meaning.
/// </summary>
public static class TemplateDuplicateResolver
{
    public static TemplateDuplicateResolution Resolve(IEnumerable<TemplateEntry> entries)
    {
        var byKey = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var warnings = new List<TemplateWarning>();

        foreach (var entry in entries)
        {
            // Entry keys are untrusted input: re-normalize rather than believe them.
            var key = ModNameNormalizer.Normalize(entry.N);
            if (key.Length == 0 || !TemplatePathValidator.IsValidFolder(entry.F))
            {
                warnings.Add(new TemplateWarning(TemplateWarningCode.InvalidEntryPath, entry.N));
                continue;
            }

            if (!byKey.TryGetValue(key, out var folders))
                byKey[key] = folders = [];
            folders.Add(entry.F);
        }

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, folders) in byKey.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var distinct = folders.Distinct(StringComparer.Ordinal).ToList();
            if (distinct.Count > 1)
            {
                warnings.Add(new TemplateWarning(TemplateWarningCode.ConflictingDuplicateEntry, key));
                continue;
            }

            if (folders.Count > 1)
                warnings.Add(new TemplateWarning(TemplateWarningCode.DuplicateEntry, key));

            resolved[key] = distinct[0];
        }

        // Order the warnings deterministically before returning. The invalid-entry pass above
        // runs in input order while the duplicate pass is key-sorted, so without this a template
        // holding two invalid entries would produce different warning sequences for identical
        // content in a different array order -- exactly what the order-independence rule forbids.
        var orderedWarnings = warnings
            .OrderBy(warning => warning.Subject, StringComparer.Ordinal)
            .ThenBy(warning => warning.Code)
            .ToList();

        return new TemplateDuplicateResolution(resolved, orderedWarnings);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~TemplateDuplicateResolverTests"
```

Expected: PASS, 9 tests.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Templates/TemplateDuplicateResolver.cs PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplateDuplicateResolverTests.cs
git commit -m "feat: add template duplicate and collision resolver"
```

---

### Task 5: TemplateFolderLabels

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Templates/TemplateFolderLabels.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplateFolderLabelsTests.cs`

**Interfaces:**
- Consumes: nothing beyond BCL.
- Produces: `public static Func<string, string> TemplateFolderLabels.Create(IReadOnlyDictionary<string, string> labels)` — returns an identity function when `labels` is empty.

- [ ] **Step 1: Write the failing test**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplateFolderLabelsTests.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Tests.Organizer.Templates;

using PenumbraOrganizer.Plugin.Organizer.Templates;

public class TemplateFolderLabelsTests
{
    [Fact]
    public void Create_EmptyMap_ReturnsPathUnchanged()
    {
        var rename = TemplateFolderLabels.Create(new Dictionary<string, string>());

        Assert.Equal("Gear/Head", rename("Gear/Head"));
    }

    [Fact]
    public void Create_ExactMatch_Renames()
    {
        var rename = TemplateFolderLabels.Create(new Dictionary<string, string> { ["Others"] = "_Unsorted" });

        Assert.Equal("_Unsorted", rename("Others"));
    }

    // Prefix rewriting is the point: an author renaming "Gear" must not end up with "Equipment"
    // sitting next to an unrenamed "Gear/Head".
    [Fact]
    public void Create_PrefixMatch_RenamesDescendants()
    {
        var rename = TemplateFolderLabels.Create(new Dictionary<string, string> { ["Gear"] = "Equipment" });

        Assert.Equal("Equipment/Head", rename("Gear/Head"));
    }

    [Fact]
    public void Create_PrefixMatch_RespectsSegmentBoundaries()
    {
        var rename = TemplateFolderLabels.Create(new Dictionary<string, string> { ["Gear"] = "Equipment" });

        Assert.Equal("Gearbox", rename("Gearbox"));
        Assert.Equal("Gearbox/Head", rename("Gearbox/Head"));
    }

    [Fact]
    public void Create_SeveralMatchingKeys_LongestWins()
    {
        var rename = TemplateFolderLabels.Create(new Dictionary<string, string>
        {
            ["Gear"] = "Equipment",
            ["Gear/Head"] = "Equipment/Headgear",
        });

        Assert.Equal("Equipment/Headgear", rename("Gear/Head"));
        Assert.Equal("Equipment/Top", rename("Gear/Top"));
    }

    // Applied once, non-recursively: a rename's output is never re-matched, so a map cannot loop.
    [Fact]
    public void Create_RenameOutput_IsNotRematched()
    {
        var rename = TemplateFolderLabels.Create(new Dictionary<string, string>
        {
            ["Gear"] = "Weapon",
            ["Weapon"] = "Gear",
        });

        Assert.Equal("Weapon", rename("Gear"));
        Assert.Equal("Gear", rename("Weapon"));
    }

    [Fact]
    public void Create_NoMatchingKey_ReturnsPathUnchanged()
    {
        var rename = TemplateFolderLabels.Create(new Dictionary<string, string> { ["Gear"] = "Equipment" });

        Assert.Equal("Hair", rename("Hair"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~TemplateFolderLabelsTests"
```

Expected: build error — `TemplateFolderLabels` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `PenumbraOrganizer.Plugin/Organizer/Templates/TemplateFolderLabels.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Templates;

/// <summary>
/// Renames canonical folder paths (the output of ModTypeFolders.GetFolder) using a template's
/// folderLabels map. Longest-prefix, segment-boundary matching: {"Gear": "Equipment"} rewrites
/// both "Gear" and "Gear/Head", but never "Gearbox". Exact-key-only matching was rejected because
/// it produces exactly the split tree this feature exists to avoid.
///
/// Renaming is applied once and non-recursively — the output is never re-matched — so a map
/// cannot loop or cascade.
/// </summary>
public static class TemplateFolderLabels
{
    public static Func<string, string> Create(IReadOnlyDictionary<string, string> labels)
    {
        if (labels.Count == 0)
            return static folder => folder;

        // Longest key first, so the most specific match wins.
        var ordered = labels
            .OrderByDescending(pair => pair.Key.Count(c => c == '/'))
            .ThenByDescending(pair => pair.Key.Length)
            .ToList();

        return folder =>
        {
            foreach (var (key, replacement) in ordered)
            {
                if (string.Equals(folder, key, StringComparison.Ordinal))
                    return replacement;

                // Segment boundary: "Gear" matches "Gear/Head" but not "Gearbox".
                if (folder.Length > key.Length
                    && folder[key.Length] == '/'
                    && folder.AsSpan(0, key.Length).SequenceEqual(key))
                {
                    return replacement + folder[key.Length..];
                }
            }

            return folder;
        };
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~TemplateFolderLabelsTests"
```

Expected: PASS, 7 tests.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Templates/TemplateFolderLabels.cs PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplateFolderLabelsTests.cs
git commit -m "feat: add longest-prefix folder label renaming"
```

---

### Task 6: TemplateCodec — document stages (JSON, schema, semantic)

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Templates/TemplateCodec.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplateCodecJsonTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–5.
- Produces:
  ```csharp
  public enum TemplateDecodeError
  {
      MissingPrefix, InvalidBase64, InvalidDeflate, PayloadTooLarge,
      MalformedJson, UnsupportedFormatVersion, MissingName,
      UnknownFallbackStrategy, LimitExceeded, InvalidFolderLabelValue,
  }

  public sealed record TemplateDecodeResult(
      ValidatedOrganizationTemplate? Template,
      TemplateDecodeError? Error,
      string? ErrorDetail,
      IReadOnlyList<TemplateWarning> Warnings)
  {
      public bool Succeeded => Template is not null;
  }

  public static TemplateDecodeResult TemplateCodec.DecodeJson(string json);
  public static string TemplateCodec.EncodeJson(OrganizationTemplate template);
  ```
  Task 7 adds `DecodeShareCode`/`EncodeShareCode` to this same class.

- [ ] **Step 1: Write the failing test**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplateCodecJsonTests.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Tests.Organizer.Templates;

using PenumbraOrganizer.Plugin.Organizer.Templates;

public class TemplateCodecJsonTests
{
    private const string ValidJson = """
    {
      "formatVersion": 1,
      "name": "Detailed type sort",
      "author": "Akako",
      "description": "Character mods up front.",
      "fallbackStrategy": "TypeThenCreator",
      "folderLabels": { "Others": "_Unsorted" },
      "folders": ["Characters", "Gear/Top"],
      "entries": [ { "n": "bibo+ medieval", "f": "Gear/Top" } ]
    }
    """;

    [Fact]
    public void DecodeJson_ValidDocument_Succeeds()
    {
        var result = TemplateCodec.DecodeJson(ValidJson);

        Assert.True(result.Succeeded);
        Assert.Equal("Detailed type sort", result.Template!.Name);
        Assert.Equal(TemplateFallbackStrategy.TypeThenCreator, result.Template.FallbackStrategy);
        Assert.Equal("Gear/Top", result.Template.EntriesByNormalizedName["bibo+ medieval"]);
        Assert.Equal(["Characters", "Gear/Top"], result.Template.Folders);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void EncodeThenDecode_RoundTrips()
    {
        var template = new OrganizationTemplate
        {
            FormatVersion = 1,
            Name = "Round trip",
            FallbackStrategy = "ModTypeDetailed",
            Folders = ["Gear/Head"],
            Entries = [new TemplateEntry("some mod", "Gear/Head")],
        };

        var result = TemplateCodec.DecodeJson(TemplateCodec.EncodeJson(template));

        Assert.True(result.Succeeded);
        Assert.Equal("Round trip", result.Template!.Name);
        Assert.Equal(TemplateFallbackStrategy.ModTypeDetailed, result.Template.FallbackStrategy);
        Assert.Equal("Gear/Head", result.Template.EntriesByNormalizedName["some mod"]);
    }

    [Fact]
    public void DecodeJson_MalformedJson_FailsWithMalformedJson()
    {
        var result = TemplateCodec.DecodeJson("{ not json");

        Assert.False(result.Succeeded);
        Assert.Equal(TemplateDecodeError.MalformedJson, result.Error);
    }

    // A future template must never be half-read by an older plugin.
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(99)]
    public void DecodeJson_UnsupportedFormatVersion_Fails(int version)
    {
        var result = TemplateCodec.DecodeJson(
            $$"""{"formatVersion":{{version}},"name":"x","fallbackStrategy":"ModType"}""");

        Assert.False(result.Succeeded);
        Assert.Equal(TemplateDecodeError.UnsupportedFormatVersion, result.Error);
        Assert.Contains(version.ToString(), result.ErrorDetail);
    }

    [Fact]
    public void DecodeJson_UnknownFallbackStrategy_Fails()
    {
        var result = TemplateCodec.DecodeJson(
            """{"formatVersion":1,"name":"x","fallbackStrategy":"ByVibes"}""");

        Assert.False(result.Succeeded);
        Assert.Equal(TemplateDecodeError.UnknownFallbackStrategy, result.Error);
        Assert.Contains("ByVibes", result.ErrorDetail);
    }

    [Fact]
    public void DecodeJson_MissingName_Fails()
    {
        var result = TemplateCodec.DecodeJson(
            """{"formatVersion":1,"name":"","fallbackStrategy":"ModType"}""");

        Assert.False(result.Succeeded);
        Assert.Equal(TemplateDecodeError.MissingName, result.Error);
    }

    [Fact]
    public void DecodeJson_TooManyEntries_FailsWithLimitExceeded()
    {
        var entries = string.Join(',',
            Enumerable.Range(0, TemplateLimits.MaxEntries + 1).Select(i => $$"""{"n":"m{{i}}","f":"Gear"}"""));
        var result = TemplateCodec.DecodeJson(
            $$"""{"formatVersion":1,"name":"x","fallbackStrategy":"ModType","entries":[{{entries}}]}""");

        Assert.False(result.Succeeded);
        Assert.Equal(TemplateDecodeError.LimitExceeded, result.Error);
    }

    [Fact]
    public void DecodeJson_InvalidFolderInFoldersList_IsSkippedWithWarning()
    {
        var result = TemplateCodec.DecodeJson("""
        {"formatVersion":1,"name":"x","fallbackStrategy":"ModType","folders":["Gear","Gear//Bad"]}
        """);

        Assert.True(result.Succeeded);
        Assert.Equal(["Gear"], result.Template!.Folders);
        Assert.Contains(
            new TemplateWarning(TemplateWarningCode.InvalidEntryPath, "Gear//Bad"),
            result.Warnings);
    }

    [Fact]
    public void DecodeJson_InvalidFolderLabelKey_IsDroppedWithWarning()
    {
        var result = TemplateCodec.DecodeJson("""
        {"formatVersion":1,"name":"x","fallbackStrategy":"ModType","folderLabels":{"Gear//Bad":"Equipment"}}
        """);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Template!.FolderLabels);
        Assert.Contains(
            new TemplateWarning(TemplateWarningCode.UnknownFolderLabelKey, "Gear//Bad"),
            result.Warnings);
    }

    // A malformed replacement VALUE would inject a broken path into every fallback proposal,
    // so unlike a bad key it is fatal rather than skippable.
    [Fact]
    public void DecodeJson_InvalidFolderLabelValue_Fails()
    {
        var result = TemplateCodec.DecodeJson("""
        {"formatVersion":1,"name":"x","fallbackStrategy":"ModType","folderLabels":{"Gear":"/Equipment"}}
        """);

        Assert.False(result.Succeeded);
        Assert.Equal(TemplateDecodeError.InvalidFolderLabelValue, result.Error);
    }

    [Fact]
    public void DecodeJson_ConflictingDuplicateEntries_DropsGroupAndWarns()
    {
        var result = TemplateCodec.DecodeJson("""
        {"formatVersion":1,"name":"x","fallbackStrategy":"ModType",
         "entries":[{"n":"dup","f":"Gear"},{"n":"dup","f":"Hair"}]}
        """);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Template!.EntriesByNormalizedName);
        Assert.Contains(
            new TemplateWarning(TemplateWarningCode.ConflictingDuplicateEntry, "dup"),
            result.Warnings);
    }

    [Fact]
    public void DecodeJson_UnnormalizedEntryKeys_AreRenormalized()
    {
        var result = TemplateCodec.DecodeJson("""
        {"formatVersion":1,"name":"x","fallbackStrategy":"ModType",
         "entries":[{"n":"Bibo+ Medieval (Penumbra)_1_1_0","f":"Gear/Top"}]}
        """);

        Assert.True(result.Succeeded);
        Assert.Equal("Gear/Top", result.Template!.EntriesByNormalizedName["bibo+ medieval"]);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~TemplateCodecJsonTests"
```

Expected: build error — `TemplateCodec` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `PenumbraOrganizer.Plugin/Organizer/Templates/TemplateCodec.cs`:

```csharp
using System.Text.Json;

namespace PenumbraOrganizer.Plugin.Organizer.Templates;

public enum TemplateDecodeError
{
    MissingPrefix,
    InvalidBase64,
    InvalidDeflate,
    PayloadTooLarge,
    MalformedJson,
    UnsupportedFormatVersion,
    MissingName,
    UnknownFallbackStrategy,
    LimitExceeded,
    InvalidFolderLabelValue,
}

public sealed record TemplateDecodeResult(
    ValidatedOrganizationTemplate? Template,
    TemplateDecodeError? Error,
    string? ErrorDetail,
    IReadOnlyList<TemplateWarning> Warnings)
{
    public bool Succeeded => Template is not null;

    public static TemplateDecodeResult Fail(TemplateDecodeError error, string detail) =>
        new(null, error, detail, []);
}

/// <summary>
/// Decodes in distinct stages — transport, JSON, schema, semantic — so that nothing unvalidated
/// can reach OrganizerState: only a ValidatedOrganizationTemplate leaves this class.
///
/// Fatal errors refuse the whole document rather than applying part of it. Non-fatal problems
/// (a bad folder in the folders list, an unknown label key, a duplicate entry) warn and continue.
/// </summary>
public static class TemplateCodec
{
    public const int SupportedFormatVersion = 1;

    // Task 2's shared options, not a second private copy: a document written here must be
    // byte-identical to one written anywhere else, and the relaxed encoder keeps '+' and
    // non-ASCII mod names from inflating to six bytes per character.
    private static JsonSerializerOptions SerializerOptions => TemplateJson.SerializerOptions;

    public static string EncodeJson(OrganizationTemplate template) =>
        JsonSerializer.Serialize(template, SerializerOptions);

    public static TemplateDecodeResult DecodeJson(string json)
    {
        // Stage 2: well-formedness only.
        OrganizationTemplate? document;
        try
        {
            document = JsonSerializer.Deserialize<OrganizationTemplate>(json, SerializerOptions);
        }
        catch (JsonException exception)
        {
            return TemplateDecodeResult.Fail(TemplateDecodeError.MalformedJson, exception.Message);
        }

        if (document is null)
            return TemplateDecodeResult.Fail(TemplateDecodeError.MalformedJson, "Document was null.");

        return Validate(document);
    }

    // Stages 3-5: schema validation, semantic normalization, validated construction.
    private static TemplateDecodeResult Validate(OrganizationTemplate document)
    {
        if (document.FormatVersion != SupportedFormatVersion)
        {
            return TemplateDecodeResult.Fail(
                TemplateDecodeError.UnsupportedFormatVersion,
                $"Template format version {document.FormatVersion}; this plugin supports {SupportedFormatVersion}.");
        }

        if (string.IsNullOrWhiteSpace(document.Name) || document.Name.Length > TemplateLimits.MaxStringLength)
            return TemplateDecodeResult.Fail(TemplateDecodeError.MissingName, "Template name is missing or too long.");

        if (!Enum.TryParse<TemplateFallbackStrategy>(document.FallbackStrategy, ignoreCase: false, out var strategy))
        {
            return TemplateDecodeResult.Fail(
                TemplateDecodeError.UnknownFallbackStrategy,
                $"Unknown fallback strategy '{document.FallbackStrategy}'.");
        }

        if (document.Entries.Count > TemplateLimits.MaxEntries)
            return TemplateDecodeResult.Fail(TemplateDecodeError.LimitExceeded, $"Entries: {document.Entries.Count}.");
        if (document.Folders.Count > TemplateLimits.MaxFolders)
            return TemplateDecodeResult.Fail(TemplateDecodeError.LimitExceeded, $"Folders: {document.Folders.Count}.");
        if (document.FolderLabels.Count > TemplateLimits.MaxFolderLabels)
            return TemplateDecodeResult.Fail(TemplateDecodeError.LimitExceeded, $"Folder labels: {document.FolderLabels.Count}.");

        var warnings = new List<TemplateWarning>();

        var folders = new List<string>();
        foreach (var folder in document.Folders)
        {
            if (TemplatePathValidator.IsValidFolder(folder))
                folders.Add(folder);
            else
                warnings.Add(new TemplateWarning(TemplateWarningCode.InvalidEntryPath, folder));
        }

        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, replacement) in document.FolderLabels)
        {
            // A malformed replacement value would inject a broken path into every fallback
            // proposal, so it is fatal; a malformed key only fails to match anything.
            if (!TemplatePathValidator.IsValidFolder(replacement) || replacement.Length == 0)
            {
                return TemplateDecodeResult.Fail(
                    TemplateDecodeError.InvalidFolderLabelValue,
                    $"Folder label '{key}' has invalid replacement '{replacement}'.");
            }

            if (!TemplatePathValidator.IsValidFolder(key) || key.Length == 0)
            {
                warnings.Add(new TemplateWarning(TemplateWarningCode.UnknownFolderLabelKey, key));
                continue;
            }

            labels[key] = replacement;
        }

        var resolution = TemplateDuplicateResolver.Resolve(document.Entries);
        warnings.AddRange(resolution.Warnings);

        var validated = new ValidatedOrganizationTemplate(
            document.Name,
            document.Author,
            document.Description,
            strategy,
            labels,
            folders,
            resolution.EntriesByNormalizedName);

        return new TemplateDecodeResult(validated, null, null, warnings);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~TemplateCodecJsonTests"
```

Expected: PASS, 13 tests.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Templates/TemplateCodec.cs PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplateCodecJsonTests.cs
git commit -m "feat: add template document decoding and validation"
```

---

### Task 7: TemplateCodec — share-code transport

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/Templates/TemplateCodec.cs` (add transport members)
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplateCodecShareCodeTests.cs`

**Interfaces:**
- Consumes: `TemplateCodec.DecodeJson`/`EncodeJson` (Task 6), `TemplateLimits` (Task 2).
- Produces: `public const string TemplateCodec.ShareCodePrefix = "POT1:"`, `public static string TemplateCodec.EncodeShareCode(OrganizationTemplate template)`, `public static TemplateDecodeResult TemplateCodec.DecodeShareCode(string code)`.

- [ ] **Step 1: Write the failing test**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplateCodecShareCodeTests.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Tests.Organizer.Templates;

using System.IO.Compression;
using System.Text;
using PenumbraOrganizer.Plugin.Organizer.Templates;

public class TemplateCodecShareCodeTests
{
    private static OrganizationTemplate SampleTemplate() => new()
    {
        FormatVersion = 1,
        Name = "Detailed type sort",
        FallbackStrategy = "TypeThenCreator",
        Folders = ["Gear/Top"],
        Entries = [new TemplateEntry("bibo+ medieval", "Gear/Top")],
    };

    [Fact]
    public void EncodeShareCode_StartsWithPrefix()
    {
        Assert.StartsWith("POT1:", TemplateCodec.EncodeShareCode(SampleTemplate()));
    }

    [Fact]
    public void EncodeThenDecodeShareCode_RoundTrips()
    {
        var result = TemplateCodec.DecodeShareCode(TemplateCodec.EncodeShareCode(SampleTemplate()));

        Assert.True(result.Succeeded);
        Assert.Equal("Detailed type sort", result.Template!.Name);
        Assert.Equal("Gear/Top", result.Template.EntriesByNormalizedName["bibo+ medieval"]);
    }

    [Fact]
    public void DecodeShareCode_SurroundingWhitespace_IsTolerated()
    {
        var code = TemplateCodec.EncodeShareCode(SampleTemplate());

        Assert.True(TemplateCodec.DecodeShareCode($"  {code}\n").Succeeded);
    }

    [Fact]
    public void DecodeShareCode_MissingPrefix_Fails()
    {
        var result = TemplateCodec.DecodeShareCode("bm90aGluZw==");

        Assert.False(result.Succeeded);
        Assert.Equal(TemplateDecodeError.MissingPrefix, result.Error);
    }

    [Fact]
    public void DecodeShareCode_InvalidBase64_Fails()
    {
        var result = TemplateCodec.DecodeShareCode("POT1:!!!not base64!!!");

        Assert.False(result.Succeeded);
        Assert.Equal(TemplateDecodeError.InvalidBase64, result.Error);
    }

    [Fact]
    public void DecodeShareCode_ValidBase64ThatIsNotDeflate_Fails()
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("this is not a deflate stream"));

        var result = TemplateCodec.DecodeShareCode("POT1:" + payload);

        Assert.False(result.Succeeded);
        Assert.Equal(TemplateDecodeError.InvalidDeflate, result.Error);
    }

    [Fact]
    public void DecodeShareCode_CompressedInputOverLimit_Fails()
    {
        var oversize = Convert.ToBase64String(new byte[TemplateLimits.MaxCompressedBytes + 1]);

        var result = TemplateCodec.DecodeShareCode("POT1:" + oversize);

        Assert.False(result.Succeeded);
        Assert.Equal(TemplateDecodeError.PayloadTooLarge, result.Error);
    }

    // A small compressed payload can inflate to something enormous, so the cap must be enforced
    // DURING inflation rather than after it.
    [Fact]
    public void DecodeShareCode_ZipBomb_FailsWithoutAllocatingTheWholePayload()
    {
        using var buffer = new MemoryStream();
        using (var deflate = new DeflateStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var chunk = new byte[64 * 1024];
            for (var i = 0; i < 256; i++)
                deflate.Write(chunk, 0, chunk.Length);
        }

        var code = "POT1:" + Convert.ToBase64String(buffer.ToArray());
        var result = TemplateCodec.DecodeShareCode(code);

        Assert.False(result.Succeeded);
        Assert.Equal(TemplateDecodeError.PayloadTooLarge, result.Error);
    }

    [Fact]
    public void DecodeShareCode_ValidTransportButBadDocument_ReportsDocumentError()
    {
        var badDocument = new OrganizationTemplate
        {
            FormatVersion = 99,
            Name = "x",
            FallbackStrategy = "ModType",
        };

        var result = TemplateCodec.DecodeShareCode(TemplateCodec.EncodeShareCode(badDocument));

        Assert.False(result.Succeeded);
        Assert.Equal(TemplateDecodeError.UnsupportedFormatVersion, result.Error);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~TemplateCodecShareCodeTests"
```

Expected: build error — `EncodeShareCode` does not exist.

- [ ] **Step 3: Write minimal implementation**

In `PenumbraOrganizer.Plugin/Organizer/Templates/TemplateCodec.cs`, add these usings at the top of the file:

```csharp
using System.IO.Compression;
using System.Text;
```

and add these members inside `public static class TemplateCodec`, directly below `SupportedFormatVersion`:

```csharp
    public const string ShareCodePrefix = "POT1:";

    public static string EncodeShareCode(OrganizationTemplate template)
    {
        var json = Encoding.UTF8.GetBytes(EncodeJson(template));
        using var buffer = new MemoryStream();
        using (var deflate = new DeflateStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(json, 0, json.Length);

        return ShareCodePrefix + Convert.ToBase64String(buffer.ToArray());
    }

    /// <summary>
    /// Stage 1 of decoding: transport only. Each failure names the stage that failed, so a user
    /// pasting a truncated code sees "invalid base64" rather than a generic parse error.
    /// </summary>
    public static TemplateDecodeResult DecodeShareCode(string code)
    {
        var trimmed = code.Trim();
        if (!trimmed.StartsWith(ShareCodePrefix, StringComparison.Ordinal))
        {
            return TemplateDecodeResult.Fail(
                TemplateDecodeError.MissingPrefix,
                $"A share code must start with '{ShareCodePrefix}'.");
        }

        byte[] compressed;
        try
        {
            compressed = Convert.FromBase64String(trimmed[ShareCodePrefix.Length..]);
        }
        catch (FormatException exception)
        {
            return TemplateDecodeResult.Fail(TemplateDecodeError.InvalidBase64, exception.Message);
        }

        if (compressed.Length > TemplateLimits.MaxCompressedBytes)
        {
            return TemplateDecodeResult.Fail(
                TemplateDecodeError.PayloadTooLarge,
                $"Compressed payload is {compressed.Length} bytes; the limit is {TemplateLimits.MaxCompressedBytes}.");
        }

        string json;
        try
        {
            json = Inflate(compressed);
        }
        catch (PayloadTooLargeException)
        {
            return TemplateDecodeResult.Fail(
                TemplateDecodeError.PayloadTooLarge,
                $"Payload inflates past the {TemplateLimits.MaxDecompressedBytes}-byte limit.");
        }
        catch (InvalidDataException exception)
        {
            return TemplateDecodeResult.Fail(TemplateDecodeError.InvalidDeflate, exception.Message);
        }

        return DecodeJson(json);
    }

    private sealed class PayloadTooLargeException : Exception;

    // Reads in chunks and stops the moment the cap is passed, so a small code that inflates to
    // gigabytes cannot be materialized before validation gets a chance to reject it.
    private static string Inflate(byte[] compressed)
    {
        using var source = new MemoryStream(compressed);
        using var deflate = new DeflateStream(source, CompressionMode.Decompress);
        using var destination = new MemoryStream();

        var chunk = new byte[81_920];
        int read;
        while ((read = deflate.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (destination.Length + read > TemplateLimits.MaxDecompressedBytes)
                throw new PayloadTooLargeException();

            destination.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(destination.ToArray());
    }
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~TemplateCodecShareCodeTests"
```

Expected: PASS, 9 tests.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Templates/TemplateCodec.cs PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplateCodecShareCodeTests.cs
git commit -m "feat: add template share-code transport with inflation cap"
```

---

### Task 8: Extract SortFolderSelectors (behavior-preserving refactor)

The planner must compute fallback destinations, but the folder-selection expressions currently live inline in `OrganizerState`'s seven `SortBy*` methods. Duplicating them in the planner would create two sorting implementations that drift. Extract them once; both callers use the extraction.

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/SortFolderSelectors.cs`
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs:170-195` (the seven `SortBy*` methods, `FlattenGearSubCategory`, `TypeFolder`)
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/SortFolderSelectorsTests.cs`

**Interfaces:**
- Consumes: `OrganizerModRow`, `ModTypeFolders`, `PenumbraPathSemantics`, `TemplateFallbackStrategy` (Task 2).
- Produces:
  ```csharp
  public static (string? Primary, string? Secondary) SortFolderSelectors.Select(
      TemplateFallbackStrategy strategy,
      OrganizerModRow row,
      Func<string, string>? canonicalizeCreator = null,
      Func<string, string>? renameFolder = null);

  public static string SortFolderSelectors.FlattenToFolder(string? primary, string? secondary);
  ```

- [ ] **Step 1: Write the failing test**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/SortFolderSelectorsTests.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Tests.Organizer;

using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Templates;

public class SortFolderSelectorsTests
{
    private static OrganizerModRow Row(ModCategory? category, string? subCategory, string author) => new()
    {
        Identifier = "id",
        Name = "Some Mod",
        Author = author,
        CurrentPath = "Some Mod",
        ProposedPath = "Some Mod",
        Category = category,
        SubCategory = subCategory,
    };

    private static string Same(string value) => value;

    [Fact]
    public void Select_ModTypeDetailed_UsesSubCategory()
    {
        var (primary, secondary) = SortFolderSelectors.Select(
            TemplateFallbackStrategy.ModTypeDetailed, Row(ModCategory.Gear, "Head", "Tsar"), Same);

        Assert.Equal("Gear/Head", primary);
        Assert.Null(secondary);
    }

    // The flat variants exist to collapse Gear specifically; every other category keeps its
    // subfolder behavior.
    [Fact]
    public void Select_ModType_FlattensGearSubCategory()
    {
        var (primary, _) = SortFolderSelectors.Select(
            TemplateFallbackStrategy.ModType, Row(ModCategory.Gear, "Head", "Tsar"), Same);

        Assert.Equal("Gear", primary);
    }

    [Fact]
    public void Select_ModType_KeepsNonGearSubCategory()
    {
        var (primary, _) = SortFolderSelectors.Select(
            TemplateFallbackStrategy.ModType, Row(ModCategory.NPC, "Bosses", "Tsar"), Same);

        Assert.Equal("NPC/Bosses", primary);
    }

    [Fact]
    public void Select_Creator_UsesCanonicalizedAuthor()
    {
        var (primary, secondary) = SortFolderSelectors.Select(
            TemplateFallbackStrategy.Creator, Row(ModCategory.Gear, null, "tsar"), _ => "Tsar");

        Assert.Equal("Tsar", primary);
        Assert.Null(secondary);
    }

    [Fact]
    public void Select_TypeThenCreator_OrdersTypeFirst()
    {
        var (primary, secondary) = SortFolderSelectors.Select(
            TemplateFallbackStrategy.TypeThenCreator, Row(ModCategory.Gear, "Head", "Tsar"), Same);

        Assert.Equal("Gear/Head", primary);
        Assert.Equal("Tsar", secondary);
    }

    [Fact]
    public void Select_CreatorThenTypeFlat_OrdersCreatorFirstAndFlattensGear()
    {
        var (primary, secondary) = SortFolderSelectors.Select(
            TemplateFallbackStrategy.CreatorThenTypeFlat, Row(ModCategory.Gear, "Head", "Tsar"), Same);

        Assert.Equal("Tsar", primary);
        Assert.Equal("Gear", secondary);
    }

    [Fact]
    public void Select_RenameFolder_AppliesToTypeSegmentOnly()
    {
        var rename = TemplateFolderLabels.Create(new Dictionary<string, string> { ["Gear"] = "Equipment" });

        var (primary, secondary) = SortFolderSelectors.Select(
            TemplateFallbackStrategy.TypeThenCreator, Row(ModCategory.Gear, "Head", "Gear"), Same, rename);

        Assert.Equal("Equipment/Head", primary);
        Assert.Equal("Gear", secondary);   // a creator literally named "Gear" is not renamed
    }

    [Fact]
    public void Select_NullCategory_ReturnsNullPrimary()
    {
        var (primary, _) = SortFolderSelectors.Select(
            TemplateFallbackStrategy.ModType, Row(null, null, "Tsar"), Same);

        Assert.Null(primary);
    }

    // The two type-only strategies have no creator segment, so callers pass no canonicalizer at
    // all rather than a dummy one whose result is discarded.
    [Fact]
    public void Select_TypeOnlyStrategy_NeedsNoCanonicalizer()
    {
        var (primary, secondary) = SortFolderSelectors.Select(
            TemplateFallbackStrategy.ModTypeDetailed, Row(ModCategory.Gear, "Head", "Tsar"));

        Assert.Equal("Gear/Head", primary);
        Assert.Null(secondary);
    }

    [Theory]
    [InlineData("Gear", "Tsar", "Gear/Tsar")]
    [InlineData("Gear", null, "Gear")]
    [InlineData(null, "Tsar", "Tsar")]
    [InlineData(null, null, "Review")]   // matches BuildPath's own unclassified fallback
    public void FlattenToFolder_MatchesBuildPathSegmentOrder(string? primary, string? secondary, string expected)
    {
        Assert.Equal(expected, SortFolderSelectors.FlattenToFolder(primary, secondary));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~SortFolderSelectorsTests"
```

Expected: build error — `SortFolderSelectors` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `PenumbraOrganizer.Plugin/Organizer/SortFolderSelectors.cs`:

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Classification;
using PenumbraOrganizer.Plugin.Organizer.Templates;

namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// The folder-selection expressions behind OrganizerState's seven sort strategies, extracted so
/// the template planner computes fallback destinations with the same code the sorts use rather
/// than a second implementation that can drift.
///
/// Extraction only — the expressions are unchanged from the inline SortBy* bodies.
/// </summary>
public static class SortFolderSelectors
{
    /// <param name="canonicalizeCreator">
    /// Null for the strategies that do not use a creator segment (ModType, ModTypeDetailed), so
    /// those callers neither supply nor compute one. The local functions below keep every segment
    /// lazy, so an unused segment is never built.
    /// </param>
    public static (string? Primary, string? Secondary) Select(
        TemplateFallbackStrategy strategy,
        OrganizerModRow row,
        Func<string, string>? canonicalizeCreator = null,
        Func<string, string>? renameFolder = null)
    {
        string? Creator() =>
            canonicalizeCreator is null ? null : KnownSegment(canonicalizeCreator(row.Author));

        string? Detailed() => TypeFolder(row.Category, row.SubCategory, renameFolder);

        string? Flat() =>
            TypeFolder(row.Category, FlattenGearSubCategory(row.Category, row.SubCategory), renameFolder);

        return strategy switch
        {
            TemplateFallbackStrategy.Creator => (Creator(), null),
            TemplateFallbackStrategy.ModType => (Flat(), null),
            TemplateFallbackStrategy.ModTypeDetailed => (Detailed(), null),
            TemplateFallbackStrategy.TypeThenCreator => (Detailed(), Creator()),
            TemplateFallbackStrategy.TypeThenCreatorFlat => (Flat(), Creator()),
            TemplateFallbackStrategy.CreatorThenType => (Creator(), Detailed()),
            TemplateFallbackStrategy.CreatorThenTypeFlat => (Creator(), Flat()),
            _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "Unknown fallback strategy."),
        };
    }

    /// <summary>
    /// Collapses a (primary, secondary) pair into the single folder a template plan carries.
    /// Deliberately mirrors BuildPath's segment order and its "Review" fallback for an
    /// unclassified row, so a planned folder and a sorted folder agree.
    /// </summary>
    public static string FlattenToFolder(string? primary, string? secondary)
    {
        if (primary is not null && secondary is not null)
            return $"{primary}/{secondary}";
        if (primary is not null)
            return primary;
        if (secondary is not null)
            return secondary;
        return "Review";
    }

    // Gear only: always the flat folder, ignoring any resolved slot subcategory. Every other
    // category keeps its normal subfolder behavior.
    public static string? FlattenGearSubCategory(ModCategory? category, string? subCategory) =>
        category == ModCategory.Gear ? null : subCategory;

    public static string? TypeFolder(ModCategory? category, string? subCategory, Func<string, string>? renameFolder)
    {
        if (category is null)
            return null;

        var folder = ModTypeFolders.GetFolder(category.Value, subCategory);
        if (string.IsNullOrWhiteSpace(folder))
            return null;

        // Renaming happens strictly after GetFolder, so no template value can reach GetFolder's
        // deliberate throw for a nonsense (category, subcategory) pairing.
        return renameFolder is null ? folder : renameFolder(folder);
    }

    // Single dynamic segments (creator names) mirror Penumbra's FixName so what we propose is
    // what Penumbra will actually store. Multi-level type folders must NOT be FixName'd — that
    // would turn their '/' separator into '\'.
    public static string? KnownSegment(string? segment) =>
        string.IsNullOrWhiteSpace(segment) ? null : PenumbraPathSemantics.FixName(segment);
}
```

Now replace `OrganizerState.cs` lines 170-195 (the seven `SortBy*` methods plus `FlattenGearSubCategory`) with delegating versions:

```csharp
    public int SortByCreator(Func<string, string> canonicalizeCreator) =>
        SortBy(TemplateFallbackStrategy.Creator, canonicalizeCreator);

    // No canonicalizer: these two strategies have no creator segment, so none is computed.
    public int SortByModType() =>
        SortBy(TemplateFallbackStrategy.ModType, null);

    public int SortByModTypeDetailed() =>
        SortBy(TemplateFallbackStrategy.ModTypeDetailed, null);

    public int SortByTypeThenCreator(Func<string, string> canonicalizeCreator) =>
        SortBy(TemplateFallbackStrategy.TypeThenCreator, canonicalizeCreator);

    public int SortByTypeThenCreatorFlat(Func<string, string> canonicalizeCreator) =>
        SortBy(TemplateFallbackStrategy.TypeThenCreatorFlat, canonicalizeCreator);

    public int SortByCreatorThenType(Func<string, string> canonicalizeCreator) =>
        SortBy(TemplateFallbackStrategy.CreatorThenType, canonicalizeCreator);

    public int SortByCreatorThenTypeFlat(Func<string, string> canonicalizeCreator) =>
        SortBy(TemplateFallbackStrategy.CreatorThenTypeFlat, canonicalizeCreator);

    private int SortBy(TemplateFallbackStrategy strategy, Func<string, string>? canonicalizeCreator) =>
        Sort(row => SortFolderSelectors.Select(strategy, row, canonicalizeCreator));
```

Delete the now-unused private `TypeFolder`, `KnownFolder`, `KnownSegment`, and `FlattenGearSubCategory` members from `OrganizerState.cs` (lines 242-253 and 194-195) — `SortFolderSelectors` owns them now. Add `using PenumbraOrganizer.Plugin.Organizer.Templates;` to the top of `OrganizerState.cs`. Leave `BuildPath`, `Sort`, and `FinishProposals` untouched.

- [ ] **Step 4: Run the new tests and the whole existing suite**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~SortFolderSelectorsTests"
```

Expected: PASS, 13 tests.

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj
```

Expected: PASS, entire suite. **This is the gate for the refactor** — every existing `OrganizerState` sort test must still pass unchanged. If any fails, the extraction changed behavior; fix the extraction rather than the test.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/SortFolderSelectors.cs PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs PenumbraOrganizer.Plugin.Tests/Organizer/SortFolderSelectorsTests.cs
git commit -m "refactor: extract sort folder selectors from OrganizerState"
```

---

### Task 9: TemplatePlanner

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Templates/TemplatePlanner.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplatePlannerTests.cs`

**Interfaces:**
- Consumes: `ValidatedOrganizationTemplate` (Task 2), `ModNameNormalizer` (Task 1), `TemplateFolderLabels` (Task 5), `SortFolderSelectors` (Task 8), `OrganizerModRow`.
- Produces:
  ```csharp
  public sealed record TemplateApplyReport(
      int ConsideredRows, int ProtectedRows, int RowsMatchedByEntry, int RowsPlacedByFallback,
      int TemplateEntriesMatched, int TemplateEntriesUnmatched, int AmbiguousLocalMatchGroups,
      int InvalidEntriesSkipped);

  public sealed record TemplateApplicationPlan(
      IReadOnlyDictionary<string, string> DestinationFolders,
      IReadOnlyDictionary<string, int> FolderCounts,
      TemplateApplyReport Report,
      IReadOnlyList<TemplateWarning> Warnings);

  public static TemplateApplicationPlan TemplatePlanner.Plan(
      ValidatedOrganizationTemplate template,
      IReadOnlyCollection<OrganizerModRow> rows,
      Func<string, string> canonicalizeCreator,
      IReadOnlyList<TemplateWarning>? decodeWarnings = null);
  ```

- [ ] **Step 1: Write the failing test**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplatePlannerTests.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Tests.Organizer.Templates;

using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Templates;

public class TemplatePlannerTests
{
    private static OrganizerModRow Row(
        string identifier, string name, ModCategory? category = ModCategory.Gear,
        string? subCategory = null, string author = "Tsar", bool isProtected = false) => new()
    {
        Identifier = identifier,
        Name = name,
        Author = author,
        CurrentPath = name,
        ProposedPath = name,
        Category = category,
        SubCategory = subCategory,
        Protected = isProtected,
    };

    private static ValidatedOrganizationTemplate Template(
        TemplateFallbackStrategy strategy = TemplateFallbackStrategy.ModType,
        Dictionary<string, string>? entries = null,
        Dictionary<string, string>? labels = null) => new(
            "T", "A", null, strategy,
            labels ?? new Dictionary<string, string>(),
            [],
            entries ?? new Dictionary<string, string>());

    private static string Same(string value) => value;

    [Fact]
    public void Plan_MatchedRow_UsesTemplateFolder()
    {
        var plan = TemplatePlanner.Plan(
            Template(entries: new() { ["bibo+ medieval"] = "Characters/Nyx" }),
            [Row("id1", "Bibo+ Medieval (Penumbra)_1_1_0")],
            Same);

        Assert.Equal("Characters/Nyx", plan.DestinationFolders["id1"]);
        Assert.Equal(1, plan.Report.RowsMatchedByEntry);
        Assert.Equal(0, plan.Report.RowsPlacedByFallback);
        Assert.Equal(1, plan.Report.TemplateEntriesMatched);
    }

    [Fact]
    public void Plan_UnmatchedRow_UsesFallbackStrategy()
    {
        var plan = TemplatePlanner.Plan(
            Template(TemplateFallbackStrategy.ModTypeDetailed),
            [Row("id1", "Unknown Mod", ModCategory.Gear, "Head")],
            Same);

        Assert.Equal("Gear/Head", plan.DestinationFolders["id1"]);
        Assert.Equal(0, plan.Report.RowsMatchedByEntry);
        Assert.Equal(1, plan.Report.RowsPlacedByFallback);
    }

    [Fact]
    public void Plan_FolderLabels_ApplyToFallbackPlacement()
    {
        var plan = TemplatePlanner.Plan(
            Template(TemplateFallbackStrategy.ModTypeDetailed, labels: new() { ["Gear"] = "Equipment" }),
            [Row("id1", "Unknown Mod", ModCategory.Gear, "Head")],
            Same);

        Assert.Equal("Equipment/Head", plan.DestinationFolders["id1"]);
    }

    [Fact]
    public void Plan_ProtectedRow_IsExcludedAndCounted()
    {
        var plan = TemplatePlanner.Plan(
            Template(entries: new() { ["locked mod"] = "Characters" }),
            [Row("id1", "Locked Mod", isProtected: true)],
            Same);

        Assert.Empty(plan.DestinationFolders);
        Assert.Equal(1, plan.Report.ProtectedRows);
        Assert.Equal(1, plan.Report.ConsideredRows);
    }

    // One entry deliberately matches every local row with that name: two installs of the same
    // mod should both land where the author put it.
    [Fact]
    public void Plan_OneEntryMatchingSeveralRows_PlacesAllOfThem()
    {
        var plan = TemplatePlanner.Plan(
            Template(entries: new() { ["bibo+ medieval"] = "Gear/Top" }),
            [Row("id1", "Bibo+ Medieval"), Row("id2", "Bibo+ Medieval_1_1_0")],
            Same);

        Assert.Equal("Gear/Top", plan.DestinationFolders["id1"]);
        Assert.Equal("Gear/Top", plan.DestinationFolders["id2"]);
        Assert.Equal(2, plan.Report.RowsMatchedByEntry);
        Assert.Equal(1, plan.Report.TemplateEntriesMatched);       // rows and entries differ
        Assert.Equal(1, plan.Report.AmbiguousLocalMatchGroups);
        Assert.Contains(
            new TemplateWarning(TemplateWarningCode.AmbiguousLocalMatch, "bibo+ medieval"),
            plan.Warnings);
    }

    [Fact]
    public void Plan_EntryMatchingNothing_IsCountedAndWarned()
    {
        var plan = TemplatePlanner.Plan(
            Template(entries: new() { ["mod i do not own"] = "Gear/Top" }),
            [Row("id1", "Something Else")],
            Same);

        Assert.Equal(1, plan.Report.TemplateEntriesUnmatched);
        Assert.Equal(0, plan.Report.TemplateEntriesMatched);
        Assert.Contains(
            new TemplateWarning(TemplateWarningCode.UnmatchedTemplateEntry, "mod i do not own"),
            plan.Warnings);
    }

    [Fact]
    public void Plan_FolderCounts_CountRowsPerDestination()
    {
        var plan = TemplatePlanner.Plan(
            Template(TemplateFallbackStrategy.ModType),
            [
                Row("id1", "A", ModCategory.Gear),
                Row("id2", "B", ModCategory.Gear),
                Row("id3", "C", ModCategory.Hair),
            ],
            Same);

        Assert.Equal(2, plan.FolderCounts["Gear"]);
        Assert.Equal(1, plan.FolderCounts["Hair"]);
    }

    [Fact]
    public void Plan_UnclassifiedUnmatchedRow_FallsBackToReview()
    {
        var plan = TemplatePlanner.Plan(
            Template(TemplateFallbackStrategy.ModType),
            [Row("id1", "Mystery", category: null)],
            Same);

        Assert.Equal("Review", plan.DestinationFolders["id1"]);
    }

    [Fact]
    public void Plan_DecodeWarnings_AreCarriedThrough()
    {
        var decodeWarnings = new[] { new TemplateWarning(TemplateWarningCode.DuplicateEntry, "dup") };

        var plan = TemplatePlanner.Plan(Template(), [Row("id1", "A")], Same, decodeWarnings);

        Assert.Contains(decodeWarnings[0], plan.Warnings);
    }

    [Fact]
    public void Plan_IsPure_AndDoesNotMutateRows()
    {
        var row = Row("id1", "Bibo+ Medieval");
        var originalProposed = row.ProposedPath;

        TemplatePlanner.Plan(
            Template(entries: new() { ["bibo+ medieval"] = "Characters/Nyx" }), [row], Same);

        Assert.Equal(originalProposed, row.ProposedPath);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~TemplatePlannerTests"
```

Expected: build error — `TemplatePlanner` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `PenumbraOrganizer.Plugin/Organizer/Templates/TemplatePlanner.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Templates;

/// <summary>
/// Row counts and entry counts are separate fields because they are separate numbers: 214 matched
/// rows can come from 190 matched entries.
/// </summary>
public sealed record TemplateApplyReport(
    int ConsideredRows,
    int ProtectedRows,
    int RowsMatchedByEntry,
    int RowsPlacedByFallback,
    int TemplateEntriesMatched,
    int TemplateEntriesUnmatched,
    int AmbiguousLocalMatchGroups,
    int InvalidEntriesSkipped);

public sealed record TemplateApplicationPlan(
    IReadOnlyDictionary<string, string> DestinationFolders,
    IReadOnlyDictionary<string, int> FolderCounts,
    TemplateApplyReport Report,
    IReadOnlyList<TemplateWarning> Warnings);

/// <summary>
/// Pure, non-mutating. Produces everything the preview shows AND everything the apply writes, so
/// the two cannot be different computations of the same answer — an approximate preview is
/// structurally impossible.
/// </summary>
public static class TemplatePlanner
{
    public static TemplateApplicationPlan Plan(
        ValidatedOrganizationTemplate template,
        IReadOnlyCollection<OrganizerModRow> rows,
        Func<string, string> canonicalizeCreator,
        IReadOnlyList<TemplateWarning>? decodeWarnings = null)
    {
        var renameFolder = TemplateFolderLabels.Create(template.FolderLabels);
        var warnings = new List<TemplateWarning>(decodeWarnings ?? []);

        var destinations = new Dictionary<string, string>(StringComparer.Ordinal);
        var matchedEntryKeys = new HashSet<string>(StringComparer.Ordinal);
        var rowsPerNormalizedName = new Dictionary<string, int>(StringComparer.Ordinal);

        var protectedRows = 0;
        var matchedRows = 0;
        var fallbackRows = 0;

        foreach (var row in rows)
        {
            // Protected rows are excluded here for reporting, and again by OrganizerState.Sort's
            // own !m.Protected filter when the plan is applied.
            if (row.Protected)
            {
                protectedRows++;
                continue;
            }

            var key = ModNameNormalizer.Normalize(row.Name);
            if (key.Length > 0)
                rowsPerNormalizedName[key] = rowsPerNormalizedName.GetValueOrDefault(key) + 1;

            if (key.Length > 0 && template.EntriesByNormalizedName.TryGetValue(key, out var folder))
            {
                destinations[row.Identifier] = folder;
                matchedEntryKeys.Add(key);
                matchedRows++;
                continue;
            }

            var (primary, secondary) =
                SortFolderSelectors.Select(template.FallbackStrategy, row, canonicalizeCreator, renameFolder);
            destinations[row.Identifier] = SortFolderSelectors.FlattenToFolder(primary, secondary);
            fallbackRows++;
        }

        // An entry matching several local rows is the most likely source of a surprising result,
        // so it is surfaced rather than silently multiplied out.
        var ambiguousGroups = 0;
        foreach (var key in matchedEntryKeys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (rowsPerNormalizedName.GetValueOrDefault(key) > 1)
            {
                ambiguousGroups++;
                warnings.Add(new TemplateWarning(TemplateWarningCode.AmbiguousLocalMatch, key));
            }
        }

        var unmatchedEntries = 0;
        foreach (var key in template.EntriesByNormalizedName.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (matchedEntryKeys.Contains(key))
                continue;

            unmatchedEntries++;
            warnings.Add(new TemplateWarning(TemplateWarningCode.UnmatchedTemplateEntry, key));
        }

        var folderCounts = destinations.Values
            .GroupBy(folder => folder, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var report = new TemplateApplyReport(
            ConsideredRows: rows.Count,
            ProtectedRows: protectedRows,
            RowsMatchedByEntry: matchedRows,
            RowsPlacedByFallback: fallbackRows,
            TemplateEntriesMatched: matchedEntryKeys.Count,
            TemplateEntriesUnmatched: unmatchedEntries,
            AmbiguousLocalMatchGroups: ambiguousGroups,
            InvalidEntriesSkipped: decodeWarnings?.Count(w => w.Code == TemplateWarningCode.InvalidEntryPath) ?? 0);

        return new TemplateApplicationPlan(destinations, folderCounts, report, warnings);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~TemplatePlannerTests"
```

Expected: PASS, 10 tests.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Templates/TemplatePlanner.cs PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplatePlannerTests.cs
git commit -m "feat: add pure template planner shared by preview and apply"
```

---

### Task 10: OrganizerState.ApplyTemplate

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs` (add `ApplyTemplate` beside the seven `SortBy*` methods)
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateApplyTemplateTests.cs`

**Interfaces:**
- Consumes: `TemplateApplicationPlan`, `TemplateApplyReport` (Task 9).
- Produces: `public TemplateApplyReport OrganizerState.ApplyTemplate(TemplateApplicationPlan plan)`.

- [ ] **Step 1: Write the failing test**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateApplyTemplateTests.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Tests.Organizer;

using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Templates;

public class OrganizerStateApplyTemplateTests
{
    private static OrganizerModRow Row(
        string identifier, string name, ModCategory? category = ModCategory.Gear,
        string? subCategory = null, string author = "Tsar", string? currentPath = null) => new()
    {
        Identifier = identifier,
        Name = name,
        Author = author,
        CurrentPath = currentPath ?? name,
        ProposedPath = currentPath ?? name,
        Category = category,
        SubCategory = subCategory,
    };

    private static ValidatedOrganizationTemplate Template(
        TemplateFallbackStrategy strategy = TemplateFallbackStrategy.ModType,
        Dictionary<string, string>? entries = null,
        Dictionary<string, string>? labels = null) => new(
            "T", "A", null, strategy,
            labels ?? new Dictionary<string, string>(),
            [],
            entries ?? new Dictionary<string, string>());

    private static string Same(string value) => value;

    private static OrganizerState StateWith(params OrganizerModRow[] rows)
    {
        var state = new OrganizerState();
        state.LoadScan(rows, new HashSet<string>());
        return state;
    }

    private static TemplateApplyReport Apply(
        OrganizerState state, ValidatedOrganizationTemplate template)
    {
        var plan = TemplatePlanner.Plan(template, state.Mods, Same);
        return state.ApplyTemplate(plan);
    }

    [Fact]
    public void ApplyTemplate_MatchedRow_ProposesTemplateFolderWithLocalName()
    {
        var state = StateWith(Row("id1", "Bibo+ Medieval"));

        Apply(state, Template(entries: new() { ["bibo+ medieval"] = "Characters/Nyx" }));

        Assert.Equal("Characters/Nyx/Bibo+ Medieval", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void ApplyTemplate_UnmatchedRow_UsesFallbackStrategy()
    {
        var state = StateWith(Row("id1", "Unknown", ModCategory.Gear, "Head"));

        Apply(state, Template(TemplateFallbackStrategy.ModTypeDetailed));

        Assert.Equal("Gear/Head/Unknown", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void ApplyTemplate_FolderLabels_ApplyToFallbackRows()
    {
        var state = StateWith(Row("id1", "Unknown", ModCategory.Gear, "Head"));

        Apply(state, Template(TemplateFallbackStrategy.ModTypeDetailed, labels: new() { ["Gear"] = "Equipment" }));

        Assert.Equal("Equipment/Head/Unknown", state.Mods.Single().ProposedPath);
    }

    // The leaf is the importer's own Name -- never the Identifier, and never the template's key.
    [Fact]
    public void ApplyTemplate_Leaf_IsLocalNameNotIdentifier()
    {
        var state = StateWith(Row("some_directory_name_1_0", "Pretty Display Name"));

        Apply(state, Template(entries: new() { ["pretty display name"] = "Gear" }));

        Assert.Equal("Gear/Pretty Display Name", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void ApplyTemplate_ProtectedRow_IsNotMoved()
    {
        var state = StateWith(Row("id1", "Locked", currentPath: "Original/Locked"));
        state.SetProtected("id1", true);

        Apply(state, Template(entries: new() { ["locked"] = "Characters" }));

        Assert.Equal("Original/Locked", state.Mods.Single().ProposedPath);
    }

    // The whole reason apply goes through OrganizerState.Sort rather than writing proposals
    // directly: the shared tail disambiguates two rows landing on the same path.
    [Fact]
    public void ApplyTemplate_TwoRowsSameFolderAndLeaf_AreDisambiguated()
    {
        var state = StateWith(Row("id1", "Same Name"), Row("id2", "Same Name"));

        Apply(state, Template(entries: new() { ["same name"] = "Gear" }));

        var proposed = state.Mods.Select(m => m.ProposedPath).ToList();
        Assert.Equal(2, proposed.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ApplyTemplate_ReturnsThePlansReport()
    {
        var state = StateWith(Row("id1", "Bibo+ Medieval"), Row("id2", "Other", ModCategory.Hair));
        var plan = TemplatePlanner.Plan(
            Template(entries: new() { ["bibo+ medieval"] = "Gear/Top" }), state.Mods, Same);

        var report = state.ApplyTemplate(plan);

        Assert.Equal(plan.Report, report);
        Assert.Equal(1, report.RowsMatchedByEntry);
        Assert.Equal(1, report.RowsPlacedByFallback);
    }

    // The result must equal the plan the user was shown, not a recomputation of it.
    [Fact]
    public void ApplyTemplate_Result_MatchesThePlannedFolderForEveryRow()
    {
        var state = StateWith(
            Row("id1", "Bibo+ Medieval"),
            Row("id2", "Other", ModCategory.Hair),
            Row("id3", "Mystery", category: null));
        var plan = TemplatePlanner.Plan(
            Template(entries: new() { ["bibo+ medieval"] = "Characters/Nyx" }), state.Mods, Same);

        state.ApplyTemplate(plan);

        foreach (var row in state.Mods)
        {
            var plannedFolder = plan.DestinationFolders[row.Identifier];
            Assert.StartsWith(plannedFolder + "/", row.ProposedPath);
        }
    }

    [Fact]
    public void ApplyTemplate_RowMissingFromPlan_IsLeftAlone()
    {
        var state = StateWith(Row("id1", "A"), Row("id2", "B"));
        var plan = new TemplateApplicationPlan(
            new Dictionary<string, string> { ["id1"] = "Gear" },
            new Dictionary<string, int> { ["Gear"] = 1 },
            new TemplateApplyReport(2, 0, 0, 1, 0, 0, 0, 0),
            []);

        state.ApplyTemplate(plan);

        Assert.Equal("Gear/A", state.Mods.Single(m => m.Identifier == "id1").ProposedPath);
        Assert.Equal("B", state.Mods.Single(m => m.Identifier == "id2").ProposedPath);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~OrganizerStateApplyTemplateTests"
```

Expected: build error — `ApplyTemplate` does not exist.

- [ ] **Step 3: Write minimal implementation**

In `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs`, add directly below the private `SortBy` helper added in Task 8:

```csharp
    /// <summary>
    /// Applies a plan the caller already built — and, in the UI, already showed the user. Goes
    /// through the same private Sort tail as every other strategy, so pinning, collision
    /// disambiguation and protected-row filtering are inherited rather than reimplemented.
    /// Because the plan was computed from these same rows, preview and result cannot diverge.
    ///
    /// A row absent from the plan keeps its current proposal: the plan is authoritative about
    /// which rows it covers.
    /// </summary>
    public TemplateApplyReport ApplyTemplate(TemplateApplicationPlan plan)
    {
        var touched = new List<OrganizerModRow>();
        foreach (var row in _mods.Values.Where(m => !m.Protected))
        {
            if (!plan.DestinationFolders.TryGetValue(row.Identifier, out var folder))
                continue;

            row.ProposedPath = BuildPath(folder, null, row.Name);
            touched.Add(row);
        }

        FinishProposals(touched);
        return plan.Report;
    }
```

Unlike the seven `SortBy*` methods this returns the report rather than a touched-row count — the count is already one of the report's fields.

- [ ] **Step 4: Run the new tests and the whole suite**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~OrganizerStateApplyTemplateTests"
```

Expected: PASS, 9 tests.

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj
```

Expected: PASS, entire suite.

```bash
dotnet build
```

Expected: Build succeeded, 0 errors, and no warnings beyond the pre-existing xUnit2017 warning
in `ApplyPlannerTests.cs:306`, which predates this branch and is out of scope here.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateApplyTemplateTests.cs
git commit -m "feat: apply organization templates through the shared sort pipeline"
```

---

### Task 11: Two-library interop fixture test

The spec's central claim is that a template exported from one library places mods correctly in a *different* library. Nothing so far tests that end to end.

**Files:**
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplateInteropTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–10. No production code changes.

- [ ] **Step 1: Write the failing test**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplateInteropTests.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Tests.Organizer.Templates;

using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Templates;

/// <summary>
/// The end-to-end claim this feature rests on: a document authored against one library places
/// mods correctly in a different library, with no identity binding of any kind (unlike the
/// workbook, which hard-errors across installs).
/// </summary>
public class TemplateInteropTests
{
    private static OrganizerModRow Row(
        string identifier, string name, ModCategory? category, string? subCategory = null,
        string author = "Tsar") => new()
    {
        Identifier = identifier,
        Name = name,
        Author = author,
        CurrentPath = name,
        ProposedPath = name,
        Category = category,
        SubCategory = subCategory,
    };

    private static string Same(string value) => value;

    // The author's library: mods sit in hand-made folders that no strategy would generate.
    private static OrganizationTemplate AuthorTemplate() => new()
    {
        FormatVersion = 1,
        Name = "Akako's layout",
        Author = "Akako",
        FallbackStrategy = "ModTypeDetailed",
        FolderLabels = new Dictionary<string, string> { ["Others"] = "_Unsorted" },
        Folders = ["Characters/Nyx", "Gear/Head", "_Unsorted"],
        Entries =
        [
            new TemplateEntry("bibo+ medieval", "Characters/Nyx"),
            new TemplateEntry("fancy hat", "Gear/Head"),
            new TemplateEntry("mod the importer does not own", "Characters/Nyx"),
        ],
    };

    [Fact]
    public void ShareCode_FromOneLibrary_PlacesMods_InAnother()
    {
        var code = TemplateCodec.EncodeShareCode(AuthorTemplate());

        // A different library: one shared mod under a different install suffix, one shared mod
        // named identically, one mod the author has never heard of, one unclassified mod.
        var state = new OrganizerState();
        state.LoadScan(
            [
                Row("dir_a_1_1_0", "Bibo+ Medieval (Penumbra)_1_1_0", ModCategory.Gear, "Top"),
                Row("dir_b", "Fancy Hat", ModCategory.Gear, "Head"),
                Row("dir_c", "Importer Only Mod", ModCategory.Hair),
                Row("dir_d", "Mystery", category: null),
            ],
            new HashSet<string>());

        var decoded = TemplateCodec.DecodeShareCode(code);
        Assert.True(decoded.Succeeded);

        var plan = TemplatePlanner.Plan(decoded.Template!, state.Mods, Same, decoded.Warnings);
        var report = state.ApplyTemplate(plan);

        var byIdentifier = state.Mods.ToDictionary(m => m.Identifier, m => m.ProposedPath);

        // Matched despite the install suffix the importer's copy carries.
        Assert.Equal("Characters/Nyx/Bibo+ Medieval (Penumbra)_1_1_0", byIdentifier["dir_a_1_1_0"]);
        Assert.Equal("Gear/Head/Fancy Hat", byIdentifier["dir_b"]);

        // Unmatched: placed by the declared fallback strategy, not left behind.
        Assert.Equal("Hair/Importer Only Mod", byIdentifier["dir_c"]);
        Assert.Equal("Review/Mystery", byIdentifier["dir_d"]);

        Assert.Equal(2, report.RowsMatchedByEntry);
        Assert.Equal(2, report.RowsPlacedByFallback);
        Assert.Equal(1, report.TemplateEntriesUnmatched);
    }

    [Fact]
    public void JsonFile_FromOneLibrary_ProducesTheSameResultAsTheShareCode()
    {
        var template = AuthorTemplate();
        OrganizerModRow[] Rows() =>
        [
            Row("dir_a", "Bibo+ Medieval", ModCategory.Gear, "Top"),
            Row("dir_c", "Importer Only Mod", ModCategory.Hair),
        ];

        string ApplyVia(TemplateDecodeResult decoded)
        {
            var state = new OrganizerState();
            state.LoadScan(Rows(), new HashSet<string>());
            state.ApplyTemplate(TemplatePlanner.Plan(decoded.Template!, state.Mods, Same, decoded.Warnings));
            return string.Join('|', state.Mods.Select(m => $"{m.Identifier}={m.ProposedPath}"));
        }

        Assert.Equal(
            ApplyVia(TemplateCodec.DecodeJson(TemplateCodec.EncodeJson(template))),
            ApplyVia(TemplateCodec.DecodeShareCode(TemplateCodec.EncodeShareCode(template))));
    }

    // A template is not identity-bound in any way: nothing about the importing library can make
    // it refuse to load. This is the property the workbook cannot have.
    [Fact]
    public void Template_CarriesNoInstallationIdentity()
    {
        var json = TemplateCodec.EncodeJson(AuthorTemplate());

        Assert.DoesNotContain("installationIdentity", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scanIdentity", json, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Run test to verify it fails or passes**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~TemplateInteropTests"
```

Expected: PASS if Tasks 1–10 are correct. If any assertion fails, the defect is in the earlier task's production code — fix it there, not by relaxing this test. This task is a gate, not new behavior.

- [ ] **Step 3: Run the full suite and build**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj
```

Expected: PASS, entire suite.

```bash
dotnet build
```

Expected: Build succeeded, 0 errors, and no warnings beyond the pre-existing xUnit2017 warning
in `ApplyPlannerTests.cs:306`, which predates this branch and is out of scope here.

- [ ] **Step 4: Commit**

```bash
git add PenumbraOrganizer.Plugin.Tests/Organizer/Templates/TemplateInteropTests.cs
git commit -m "test: add two-library template interop coverage"
```

---

### Task 12: Document T1 in the roadmap

**Files:**
- Modify: `docs/ROADMAP.md`

**Interfaces:**
- Consumes: nothing. No production code changes.

- [ ] **Step 1: Add the roadmap entry**

In `docs/ROADMAP.md`, under the "Where we are" list (the section containing the existing
"Detailed gear-slot classification — implemented, pending in-game verification." bullet), add:

```markdown
- **Community organization templates, Phase T1 (format and application core) — implemented, no UI
  yet.** A portable, identity-free template document (normalized mod name → folder entries, an
  author-declared fallback strategy, and a longest-prefix folder-label rename map) with staged
  validation, a `POT1:` share-code transport, and a pure `TemplatePlanner` whose plan is consumed
  by both the (future) preview and `OrganizerState.ApplyTemplate`. Unlike the workbook, a template
  carries no `installationIdentity`, so it travels between users. T2 (file import, template list,
  preview UI) and T3 (export review-and-trim, clipboard sharing) are not started — and T3's
  review-and-trim screen is a privacy mechanism, not polish, since export publishes the author's
  mod names. Design:
  `docs/superpowers/specs/2026-07-30-community-templates-design.md`. Plan:
  `docs/superpowers/plans/2026-07-30-community-templates-t1-core.md`.
```

- [ ] **Step 2: Verify the file still reads correctly**

```bash
git diff docs/ROADMAP.md
```

Expected: one added bullet, no other changes.

- [ ] **Step 3: Commit**

```bash
git add docs/ROADMAP.md
git commit -m "docs: record community templates T1 in the roadmap"
```

---

## Phase T1 Completion Criteria

- [ ] `dotnet build` succeeds and introduces no NEW warnings (one xUnit2017 warning in
  `ApplyPlannerTests.cs:306` pre-dates this branch and stays).
- [ ] `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj` passes in full, including every pre-existing test unchanged.
- [ ] `TemplateInteropTests` passes — a template authored against one synthetic library places mods correctly in a different one, through both transports.
- [ ] No UI, no file I/O, no clipboard, and no Penumbra IPC was added (T2/T3 scope).

**Not verifiable in this phase:** anything requiring the game or a second real library. In-game verification of templates needs a Discord tester with their own install and belongs to T2/T3.
