# Community templates T3 — export and share codes

Implements the **Export** and **Review-and-trim** sections of
`docs/superpowers/specs/2026-07-30-community-templates-design.md`. T1 (format, validation,
transport, planner, apply) and T2 (store, tree, tab) are shipped on this branch.

**This phase is the reason export was withheld from T1 and T2.** Exporting a name→folder map
publishes the author's entire mod list, which for this plugin's user base routinely includes content
they would not choose to broadcast. The review-and-trim screen is the safety mechanism that makes
export acceptable at all. It is not polish, and no "quick export" affordance may be added later that
bypasses it.

## Deltas from the spec

The spec predates two changes. Both are settled here; neither reopens the design.

1. **`TemplateMetadata.fallbackStrategy` is now a `TemplateFallback`** — `SortStrategy` plus
   `SplitGear`/`SplitNpc`, per the format reshape in `016090d`. The export screen must let the author
   choose all three, not pick one of seven names.

2. **`TemplateFallback` and `Windows.SortSelection` are structurally identical** — both are
   `(SortStrategy, bool, bool)`. They stay separate, with an explicit conversion in the UI layer.
   This is deliberate and is *not* the duplication that the sort-selector merge eliminated: that one
   duplicated **behaviour**, where two copies could compute different folders for the same row. These
   two carry no behaviour, and collapsing them would make the Templates domain depend on the Windows
   namespace. Task 5 pins the conversion with a test that fails if either type gains a field.

## Task 1 — `TemplateBuilder`

`Organizer/Templates/TemplateBuilder.cs`, pure and non-mutating.

```csharp
public static TemplateBuildResult Build(
    IReadOnlyCollection<OrganizerModRow> rows,
    IReadOnlySet<string> includedIdentifiers,
    IReadOnlyCollection<string> includedFolders,
    TemplateMetadata metadata);
```

- Entry folder comes from **`row.CurrentPath`**, never `ProposedPath`. The spec is explicit: export
  reflects the *applied* organization. Otherwise a user could sort, review, export, and unknowingly
  share the old layout. Take the virtual parent, matching `OrganizationCleanupPlanner.GetVirtualParent`.
- Entry key is `ModNameNormalizer.Normalize(row.Name)`. A row normalizing to an empty key is skipped.
- **`ExportNameCollision`**: two *included* rows normalizing to the same key would collapse into one
  entry, silently dropping the other. Emit one warning per colliding key with the key as subject, and
  **omit the whole group** from `entries` — same rule the import-side duplicate resolver uses for
  `ConflictingDuplicateEntry`. Deterministic ordering: `OrderBy(key, Ordinal)`.
- `TemplateMetadata` is a record: `Name`, `Author`, `Description`, `Fallback`, `FolderLabels`.
- `FormatVersion` is `TemplateCodec.SupportedFormatVersion`; `CreatedWithVersion` is the assembly
  version; `CreatedAtUtc` is ISO-8601 UTC.
- Result carries the document plus warnings, so the screen can show collisions before emitting.

**Tests:** included/excluded rows; `CurrentPath` not `ProposedPath` (a row whose proposed differs
must export the current one — this is the trap the spec names); empty-key skip; collision omits the
group and warns once; warning order deterministic; folders passed through verbatim; metadata mapped.

## Task 2 — Folder seeding

`TemplateExportFolders.Seed(IReadOnlyList<string> knownFolders, string? organizationJson)`.

`OrganizerState.KnownFolders` is built from mod `CurrentPath` parents only, so **a folder holding no
mods is invisible to it** — yet an intentionally empty bucket is exactly the kind of thing an author
wants to share. Union `KnownFolders` with the keys of `organization.json`'s `Folders` dictionary,
which lists every folder Penumbra knows.

Degrades, never fails: if the JSON is absent, malformed, or an unsupported version, return
`KnownFolders` alone plus a flag the UI uses to say so. `OrganizationJsonCodec.Parse` already never
throws and distinguishes those statuses.

**Tests:** union is deduplicated and ordinal-sorted; empty folders from JSON survive; each of
null/malformed/unsupported degrades with the flag set and never throws.

## Task 3 — `TemplateExportSelection`

Pure inclusion-set model, so the screen holds no logic worth testing through ImGui.

- Per-mod include/exclude, per-folder include/exclude (folder toggles every row under it,
  recursively, matching `IsUnderAnyProtectedFolder`'s prefix semantics).
- A search filter that narrows what is *shown* and never changes what is *included* — a filtered
  "exclude all" must not silently drop hidden rows.
- Live counts: included rows, excluded rows, included folders.
- Starts with everything included, which the screen then makes the author review.

**Tests:** folder toggle covers descendants; filter does not mutate inclusion; counts track toggles;
excluding every row yields a valid empty-entries template rather than an error.

## Task 4 — Encoded-length guidance

`TemplateShareCode.Describe(string json)` → encoded length plus whether it exceeds
`DiscordMessageLimit = 2000`.

A Discord message caps at 2000 characters, roughly 100 entries compressed. A real 900-mod library
will not fit. Past the threshold the UI tells the author plainly to share the `.json` file instead of
emitting a code that will be truncated on paste.

**Tests:** a small template is under and reported so; a large one is over; the boundary is exact.

## Task 5 — Export screen

`Windows/MainWindow.TemplatesExport.cs`, opened from the Templates tab.

Metadata fields (name required, author, description); fallback picker reusing
`SortPanel.Groupings` for labels — the static array only, not `SortPanel`'s instance state — with the
two split checkboxes disabled when the strategy is `CreatorOnly`, matching `SortSelection.SplitsApply`;
folder-label editing; the mod list with search and per-mod checkboxes; per-folder inclusion; live
counts; collision groups listed and unresolved-count shown.

Two emit buttons, both disabled until the name is non-empty: **Save to templates folder**
(`TemplateStore.Save`, which already validates-before-write and writes atomically) and **Copy share
code**, the latter additionally disabled past the length threshold.

Every filesystem and clipboard call is wrapped — this runs inside the ImGui draw call, where an
escaping exception kills the frame. This branch has already fixed that class of bug once (`838f184`).

Also here: the `TemplateFallback` ↔ `SortSelection` conversion, with the guard test from the deltas
section above.

**Tests:** the pure pieces are covered by tasks 1–4; this task adds the conversion guard and any
extracted helper. Do not attempt to drive ImGui from tests.

## Task 6 — Wiring and docs

Export button on the Templates tab; `USER_GUIDE.md` gains an Export subsection under Templates
stating plainly that export publishes a list of mod names and that the review screen is where to
trim it; roadmap entry; help-content entries for the new controls, matching the pattern in
`Resources/help-content.json` (0.6.0 added a Help tab that lists every control's tooltip — a new
screen with no entries would be a visible hole).

## Verification

- `dotnet build` clean on both projects, no new warnings.
- `dotnet test` green; current baseline is **1249**.
- The privacy gate, checked by hand: there is no path from the Templates tab to a written file or a
  clipboard write that does not pass through the review screen.
- Round trip: build a template via the screen, save it, re-import it through T2's path, and confirm
  the preview matches what the export screen showed.
