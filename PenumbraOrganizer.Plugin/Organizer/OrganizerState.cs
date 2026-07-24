using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Organizer;

public sealed class OrganizerState
{
    private readonly Dictionary<string, OrganizerModRow> _mods = new();
    private readonly HashSet<string> _protectedModIdentifiers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _protectedFolders = new(StringComparer.Ordinal);
    private readonly List<string> _knownFolders = [];

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
        IReadOnlySet<string>? previouslyProtectedFolders = null)
    {
        HasScanned = true;
        _mods.Clear();
        _protectedModIdentifiers.Clear();
        _protectedModIdentifiers.UnionWith(previouslyProtectedIdentifiers);
        _protectedFolders.Clear();
        _protectedFolders.UnionWith(previouslyProtectedFolders ?? new HashSet<string>(StringComparer.Ordinal));

        foreach (var row in scanned)
        {
            row.Protected = IsEffectivelyProtectedFull(row);
            row.ProposedPath = row.CurrentPath;
            _mods[row.Identifier] = row;
        }

        _knownFolders.Clear();
        _knownFolders.AddRange(
            _mods.Values
                .Select(m => OrganizationCleanupPlanner.GetVirtualParent(m.CurrentPath))
                .Where(f => f is not null)
                .Select(f => f!)
                .SelectMany(AncestorChain)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(f => f, StringComparer.Ordinal));
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
        row.HeliosphereManaged
        || _protectedModIdentifiers.Contains(row.Identifier)
        || OrganizationCleanupPlanner.IsUnderAnyProtectedFolder(row.CurrentPath, _protectedFolders);

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

    public int SortByCreator(Func<string, string> canonicalizeCreator) =>
        Sort(row => (KnownSegment(canonicalizeCreator(row.Author)), null));

    public int SortByModType() =>
        Sort(row => (TypeFolder(row.Category, FlattenGearSubCategory(row.Category, row.SubCategory)), null));

    public int SortByModTypeDetailed() =>
        Sort(row => (TypeFolder(row.Category, row.SubCategory), null));

    public int SortByTypeThenCreator(Func<string, string> canonicalizeCreator) =>
        Sort(row => (TypeFolder(row.Category, row.SubCategory), KnownSegment(canonicalizeCreator(row.Author))));

    public int SortByTypeThenCreatorFlat(Func<string, string> canonicalizeCreator) =>
        Sort(row => (TypeFolder(row.Category, FlattenGearSubCategory(row.Category, row.SubCategory)), KnownSegment(canonicalizeCreator(row.Author))));

    public int SortByCreatorThenType(Func<string, string> canonicalizeCreator) =>
        Sort(row => (KnownSegment(canonicalizeCreator(row.Author)), TypeFolder(row.Category, row.SubCategory)));

    public int SortByCreatorThenTypeFlat(Func<string, string> canonicalizeCreator) =>
        Sort(row => (KnownSegment(canonicalizeCreator(row.Author)), TypeFolder(row.Category, FlattenGearSubCategory(row.Category, row.SubCategory))));

    // Gear only: always the flat folder, ignoring any resolved slot subcategory. Every other
    // category keeps its normal subfolder behavior via GetFolder unchanged. Shared by every
    // "flat" sort variant so Gear/Feet vs Gear stays a single decision point.
    private static string? FlattenGearSubCategory(ModCategory? category, string? subCategory) =>
        category == ModCategory.Gear ? null : subCategory;

    // Shared shape of every sort strategy: compute this row's (primary, secondary) folder
    // segments, build its proposed path, then run the shared pin-and-disambiguate tail once
    // over every touched row. Each public SortBy* method supplies only what varies: which
    // folder segments go where.
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
