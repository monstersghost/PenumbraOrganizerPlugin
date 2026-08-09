using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Organizer;

public sealed class OrganizerState
{
    // Not readonly: ReplaceScanAtomically swaps these references only after every replacement
    // collection has been built successfully, so a throw during derivation cannot leave the state
    // half-replaced. Nothing else reassigns them.
    private Dictionary<string, OrganizerModRow> _mods = new();
    private HashSet<string> _protectedModIdentifiers = new(StringComparer.Ordinal);
    private HashSet<string> _protectedFolders = new(StringComparer.Ordinal);
    private List<string> _knownFolders = [];

    public IReadOnlyList<OrganizerModRow> Mods =>
        _mods.Values.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public bool HasScanned { get; private set; }

    public IReadOnlyCollection<string> ProtectedModIdentifiers => _protectedModIdentifiers;

    public IReadOnlyCollection<string> ProtectedFolders => _protectedFolders;

    // Every ancestor prefix of every scanned mod's virtual-parent folder — not just each mod's
    // immediate parent — so the Protect tab can offer a checkbox for any level of the tree a user
    // might want to protect (e.g. "Gear" when mods only live at "Gear/Feet/..."). The recursive
    // matching in IsUnderAnyProtectedFolder already protects an entire subtree correctly once a
    // folder is checked; this property only controls which folders are offered as checkboxes.
    // Recomputed once per LoadScan() (below), not on every access - this is read every ImGui
    // frame the Protect tab is open, and CurrentPath (which it depends on) is fixed for the
    // lifetime of a scan. Deliberately does NOT include persisted-but-currently-empty
    // ProtectedFolders entries; callers that need the union (the Protect tab's folder list) build
    // it themselves from both properties.
    public IReadOnlyList<string> KnownFolders => _knownFolders;

    // "Gear/Feet/Sub" -> ["Gear", "Gear/Feet", "Gear/Feet/Sub"].
    private static IEnumerable<string> AncestorChain(string folder)
    {
        var segments = folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
            yield return string.Join('/', segments.Take(i + 1));
    }

    // previouslyProtectedFolders defaults to null (treated as empty) so every existing call
    // site across this test project that predates folder protection keeps compiling unchanged.
    public void LoadScan(
        IEnumerable<OrganizerModRow> scanned,
        IReadOnlySet<string> previouslyProtectedIdentifiers,
        IReadOnlySet<string>? previouslyProtectedFolders = null) =>
        ReplaceScanAtomically(scanned, previouslyProtectedIdentifiers, previouslyProtectedFolders);

    /// <summary>
    /// Whole-state replacement that either fully happens or does not happen at all. Every
    /// replacement collection is built first; the field references are swapped only once all
    /// derivation has succeeded. A background scan publishes through this, so a throw here must
    /// leave the previously published scan exactly as it was rather than half-replaced.
    /// </summary>
    public void ReplaceScanAtomically(
        IEnumerable<OrganizerModRow> scanned,
        IReadOnlySet<string> previouslyProtectedIdentifiers,
        IReadOnlySet<string>? previouslyProtectedFolders = null)
    {
        var replacementProtectedIdentifiers = new HashSet<string>(previouslyProtectedIdentifiers, StringComparer.Ordinal);
        var replacementProtectedFolders = new HashSet<string>(
            previouslyProtectedFolders ?? new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);

        var replacementMods = new Dictionary<string, OrganizerModRow>();
        foreach (var row in scanned)
        {
            // Protection is derived against the REPLACEMENT sets, not the live fields, so this loop
            // reads nothing it is about to overwrite.
            row.Protected = IsEffectivelyProtected(row, replacementProtectedIdentifiers, replacementProtectedFolders);
            row.ProposedPath = row.CurrentPath;
            replacementMods[row.Identifier] = row;
        }

        var replacementKnownFolders = replacementMods.Values
            .Select(m => OrganizationCleanupPlanner.GetVirtualParent(m.CurrentPath))
            .Where(f => f is not null)
            .Select(f => f!)
            .SelectMany(AncestorChain)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        // COMMIT. Nothing above this point has touched published state.
        _protectedModIdentifiers = replacementProtectedIdentifiers;
        _protectedFolders = replacementProtectedFolders;
        _mods = replacementMods;
        _knownFolders = replacementKnownFolders;
        HasScanned = true;
    }

    public void SetProtected(string identifier, bool value)
    {
        if (value)
            _protectedModIdentifiers.Add(identifier);
        else
            _protectedModIdentifiers.Remove(identifier);

        if (_mods.TryGetValue(identifier, out var row))
            row.Protected = IsEffectivelyProtectedAfterIndividualToggle(row);
    }

    // Deliberately unchanged from the pre-folder-protection behavior: directly toggles
    // HeliosphereManaged rows, re-asserted on the next Scan. This existing, previously-confirmed
    // transient-override behavior is not touched by folder protection.
    public void SetHeliosphereProtection(bool value)
    {
        foreach (var row in _mods.Values.Where(m => m.HeliosphereManaged))
            row.Protected = value;
    }

    public void SetAllProtection(bool value)
    {
        if (value)
            _protectedModIdentifiers.UnionWith(_mods.Keys);
        else
            _protectedModIdentifiers.Clear();

        foreach (var row in _mods.Values)
            row.Protected = IsEffectivelyProtectedAfterIndividualToggle(row);
    }

    // A folder-rule change is a system/bulk event, not a single-mod interactive toggle, so it
    // always runs the full recompute (including HeliosphereManaged) over every row - not just
    // rows under this folder, because unprotecting one folder must correctly leave a still-
    // protected descendant or ancestor folder's rows alone.
    public void SetFolderProtected(string folderPath, bool value)
    {
        var normalized = OrganizationCleanupPlanner.NormalizeFolderPath(folderPath);
        if (normalized is null)
            return;

        if (value)
            _protectedFolders.Add(normalized);
        else
            _protectedFolders.Remove(normalized);

        foreach (var row in _mods.Values)
            row.Protected = IsEffectivelyProtectedFull(row);
    }

    private bool IsEffectivelyProtectedFull(OrganizerModRow row) =>
        IsEffectivelyProtected(row, _protectedModIdentifiers, _protectedFolders);

    private static bool IsEffectivelyProtected(
        OrganizerModRow row, IReadOnlySet<string> protectedModIdentifiers, IReadOnlySet<string> protectedFolders) =>
        row.HeliosphereManaged
        || protectedModIdentifiers.Contains(row.Identifier)
        || OrganizationCleanupPlanner.IsUnderAnyProtectedFolder(row.CurrentPath, protectedFolders);

    // Deliberately excludes HeliosphereManaged - see SetProtected/SetAllProtection's doc comment
    // context above and the plan's Global Constraints for why.
    private bool IsEffectivelyProtectedAfterIndividualToggle(OrganizerModRow row) =>
        _protectedModIdentifiers.Contains(row.Identifier)
        || OrganizationCleanupPlanner.IsUnderAnyProtectedFolder(row.CurrentPath, _protectedFolders);

    public bool AssignManual(string identifier, string proposedPath)
    {
        if (!_mods.TryGetValue(identifier, out var row) || row.Protected)
            return false;

        row.ProposedPath = proposedPath;
        return true;
    }

    // Each item's only failure modes (unknown identifier, protected row) are independent of
    // every other item - assigning mod A never affects whether mod B can succeed - so this
    // reports per-item results rather than pre-validating the whole batch before mutating
    // anything. Same-name collisions among the batch are caught afterward by the existing
    // Validate() on the Review Changes tab, exactly as a single manual assign can already
    // produce one today - not a new mechanism.
    public IReadOnlyList<(string Identifier, bool Success)> AssignManualBatch(
        IReadOnlySet<string> identifiers, string folder)
    {
        var normalizedFolder = folder.Trim().Trim('/');
        if (normalizedFolder.Length == 0)
            return identifiers.Select(id => (id, false)).ToList();

        var results = new List<(string, bool)>();
        foreach (var identifier in identifiers)
        {
            if (!_mods.TryGetValue(identifier, out var mod))
            {
                results.Add((identifier, false));
                continue;
            }
            results.Add((identifier, AssignManual(identifier, $"{normalizedFolder}/{mod.Name}")));
        }
        return results;
    }

    /// <summary>
    /// The single sort entry point, replacing the seven <c>SortBy*</c> methods that preceded it.
    /// Their behaviour is pinned by <c>OrganizerStateSortTests</c>, whose expectations were captured
    /// from those methods while they still existed.
    /// </summary>
    /// <remarks>
    /// The two splits are independent of each other and both are ignored by
    /// <see cref="SortStrategy.CreatorOnly"/>, which never consults the category at all. That gives
    /// 1 + (3 x 2 x 2) = 13 selections, of which the seven buttons offered seven; the six new ones
    /// are the whole splitNpc: false column, which had no button because NPC subdivision used to be
    /// unconditional.
    /// </remarks>
    public int Sort(SortStrategy strategy, bool splitGear, bool splitNpc,
                    Func<string, string> canonicalizeCreator)
    {
        // Composed, not switched over: the two flatteners are independent decisions about the same
        // subcategory, so applying them in sequence is what makes all four combinations reachable
        // without writing four variants of each strategy.
        string? Sub(OrganizerModRow row)
        {
            var sub = row.SubCategory;
            if (!splitGear) sub = FlattenGearSubCategory(row.Category, sub);
            if (!splitNpc) sub = FlattenNpcSubCategory(row.Category, sub);
            return sub;
        }

        return strategy switch
        {
            SortStrategy.CreatorOnly =>
                Sort(row => (KnownSegment(canonicalizeCreator(row.Author)), null)),
            SortStrategy.TypeOnly =>
                Sort(row => (TypeFolder(row.Category, Sub(row)), null)),
            SortStrategy.TypeThenCreator =>
                Sort(row => (TypeFolder(row.Category, Sub(row)), KnownSegment(canonicalizeCreator(row.Author)))),
            SortStrategy.CreatorThenType =>
                Sort(row => (KnownSegment(canonicalizeCreator(row.Author)), TypeFolder(row.Category, Sub(row)))),
            _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, null),
        };
    }

    // Gear only: always the flat folder, ignoring any resolved slot subcategory. Every other
    // category keeps its normal subfolder behavior via GetFolder unchanged. Shared by every
    // "flat" sort variant so Gear/Feet vs Gear stays a single decision point.
    private static string? FlattenGearSubCategory(ModCategory? category, string? subCategory) =>
        category == ModCategory.Gear ? null : subCategory;

    // The NPC mirror of the above. Unlike gear, this had no button: NPC mods were always split into
    // NPC/NPCs, NPC/Bosses and NPC/Enemies. Turning it off yields plain "NPC", which is the shape of
    // all six newly reachable combinations.
    private static string? FlattenNpcSubCategory(ModCategory? category, string? subCategory) =>
        category == ModCategory.NPC ? null : subCategory;

    // Shared shape of every sort strategy: compute this row's (primary, secondary) folder
    // segments, build its proposed path, then run the shared pin-and-disambiguate tail once
    // over every touched row. Each arm of the public Sort overload above supplies only what
    // varies: which folder segments go where.
    private int Sort(Func<OrganizerModRow, (string? Primary, string? Secondary)> folderSelector)
    {
        var count = 0;
        var touched = new List<OrganizerModRow>();
        foreach (var row in _mods.Values.Where(m => !m.Protected))
        {
            var (primary, secondary) = folderSelector(row);
            row.ProposedPath = BuildPath(primary, secondary, row.Name);
            touched.Add(row);
            count++;
        }

        FinishProposals(touched);
        return count;
    }

    private static string? TypeFolder(ModCategory? category, string? subCategory) =>
        category is null ? null : KnownFolder(ModTypeFolders.GetFolder(category.Value, subCategory));

    // For multi-level folder constants from ModTypeFolders (may contain a real '/', e.g.
    // "Gear/Feet") — must NOT be FixName'd, which would turn the separator into '\'.
    private static string? KnownFolder(string? folder) =>
        string.IsNullOrWhiteSpace(folder) ? null : folder;

    // For single dynamic segments (creator names): mirror Penumbra's FixName so what we
    // propose is what Penumbra will actually store (trimmed, '/' escaped).
    private static string? KnownSegment(string? segment) =>
        string.IsNullOrWhiteSpace(segment) ? null : PenumbraPathSemantics.FixName(segment);

    private static string BuildPath(string? primaryFolder, string? secondaryFolder, string name)
    {
        var leaf = PenumbraPathSemantics.FixName(name);
        if (primaryFolder is not null && secondaryFolder is not null)
            return $"{primaryFolder}/{secondaryFolder}/{leaf}";
        if (primaryFolder is not null)
            return $"{primaryFolder}/{leaf}";
        if (secondaryFolder is not null)
            return $"{secondaryFolder}/{leaf}";
        return $"Review/{leaf}";
    }

    // Shared tail of every sort strategy. Pinning runs BEFORE disambiguation so that a
    // duplicate install already sitting in the target folder keeps whatever transient " (N)"
    // suffix Penumbra dealt it (Penumbra discards those suffixes on save and re-deals them on
    // every reload — they are not identity, so "same folder, same base leaf" is already in
    // place), and its retained path is then reserved against the remaining collisions.
    private static void FinishProposals(List<OrganizerModRow> touched)
    {
        foreach (var row in touched)
        {
            if (PenumbraPathSemantics.AreEquivalent(row.CurrentPath, row.ProposedPath, row.Name))
                row.ProposedPath = row.CurrentPath;
        }

        CollisionDisambiguator.Disambiguate(touched);
    }

    public ReviewResult Validate()
    {
        var protectedViolations = _mods.Values
            .Where(m => m.Protected && !string.Equals(m.ProposedPath, m.CurrentPath, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Identifier)
            .ToList();

        var collisions = _mods.Values
            .GroupBy(m => m.ProposedPath, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToDictionary(g => g.Key, g => g.Select(m => m.Identifier).ToList());

        return new ReviewResult(protectedViolations, collisions);
    }
}

public sealed record ReviewResult(
    IReadOnlyList<string> ProtectedViolations,
    IReadOnlyDictionary<string, List<string>> PathCollisions)
{
    public bool HasIssues => ProtectedViolations.Count > 0 || PathCollisions.Count > 0;
}
