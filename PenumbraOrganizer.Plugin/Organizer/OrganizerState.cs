using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Classification;
using PenumbraOrganizer.Plugin.Organizer.Templates;

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

    // Heliosphere-managed mods the user has explicitly unprotected this session.
    //
    // This exists because the override needs somewhere to LIVE. It used to be written straight onto
    // row.Protected and recorded nowhere, so any later full recompute - notably ticking any folder
    // checkbox - re-derived Protected from HeliosphereManaged and silently reversed the user's
    // choice, for every Heliosphere mod at once, whether or not it had anything to do with that
    // folder.
    //
    // Deliberately NOT persisted and deliberately cleared by ReplaceScanAtomically: the documented
    // contract is that Heliosphere mods are re-protected on every scan no matter how the toggle was
    // last left, because Heliosphere owns their location. This makes the override survive unrelated
    // UI actions, not survive a scan.
    private HashSet<string> _heliosphereUnprotectOverrides = new(StringComparer.Ordinal);

    public IReadOnlyList<OrganizerModRow> Mods =>
        _mods.Values.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public bool HasScanned { get; private set; }

    /// <summary>
    /// Increments every time a scan is published. A template plan is computed for a preview and
    /// applied on a later frame; if a rescan lands in between, the plan describes rows that no
    /// longer exist. Callers hold the generation they planned against and refuse to apply a plan
    /// whose generation no longer matches, rather than applying it partially.
    /// </summary>
    public int ScanGeneration { get; private set; }

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
        // A scan re-asserts Heliosphere protection unconditionally, so this session's overrides end
        // here. Built as a replacement like everything else so a throw below leaves them untouched.
        var replacementHeliosphereOverrides = new HashSet<string>(StringComparer.Ordinal);

        var replacementMods = new Dictionary<string, OrganizerModRow>();
        foreach (var row in scanned)
        {
            // Protection is derived against the REPLACEMENT sets, not the live fields, so this loop
            // reads nothing it is about to overwrite.
            row.Protected = IsEffectivelyProtected(
                row, replacementProtectedIdentifiers, replacementProtectedFolders, replacementHeliosphereOverrides);
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
        _heliosphereUnprotectOverrides = replacementHeliosphereOverrides;
        _mods = replacementMods;
        _knownFolders = replacementKnownFolders;
        HasScanned = true;
        ScanGeneration++;
    }

    public void SetProtected(string identifier, bool value)
    {
        if (value)
            _protectedModIdentifiers.Add(identifier);
        else
            _protectedModIdentifiers.Remove(identifier);

        // Unticking a Heliosphere mod records the override; reticking it withdraws it. Without the
        // withdrawal, re-protecting and then unprotecting a folder would leave the row stuck.
        if (_mods.TryGetValue(identifier, out var row))
        {
            if (row.HeliosphereManaged)
                SetHeliosphereOverride(identifier, unprotected: !value);

            row.Protected = IsEffectivelyProtectedFull(row);
        }
    }

    // Still a transient override re-asserted on the next Scan - that part is unchanged. What
    // changed is where it lives: in _heliosphereUnprotectOverrides rather than only on row.Protected,
    // so a later recompute triggered by an unrelated control cannot silently reverse it.
    public void SetHeliosphereProtection(bool value)
    {
        foreach (var row in _mods.Values.Where(m => m.HeliosphereManaged))
        {
            SetHeliosphereOverride(row.Identifier, unprotected: !value);
            row.Protected = IsEffectivelyProtectedFull(row);
        }
    }

    public void SetAllProtection(bool value)
    {
        if (value)
            _protectedModIdentifiers.UnionWith(_mods.Keys);
        else
            _protectedModIdentifiers.Clear();

        // "Toggle protect all" is an explicit instruction about every mod, Heliosphere ones
        // included, so it sets their override the same way the Heliosphere toggle does. Otherwise
        // unprotecting everything would visibly leave the Heliosphere rows ticked.
        foreach (var row in _mods.Values)
        {
            if (row.HeliosphereManaged)
                SetHeliosphereOverride(row.Identifier, unprotected: !value);

            row.Protected = IsEffectivelyProtectedFull(row);
        }
    }

    private void SetHeliosphereOverride(string identifier, bool unprotected)
    {
        if (unprotected)
            _heliosphereUnprotectOverrides.Add(identifier);
        else
            _heliosphereUnprotectOverrides.Remove(identifier);
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
        IsEffectivelyProtected(row, _protectedModIdentifiers, _protectedFolders, _heliosphereUnprotectOverrides);

    /// <summary>
    /// The single protection rule. Every path recomputes through this one.
    /// </summary>
    /// <remarks>
    /// There used to be a second, divergent rule for interactive toggles that simply omitted the
    /// Heliosphere clause. Two rules meant the answer depended on which control the user last
    /// touched, which is exactly how ticking a folder came to reverse an explicit Heliosphere
    /// unprotect. The override set replaces that divergence: Heliosphere still protects by default,
    /// and an explicit unprotect is represented rather than implied by whichever branch ran.
    /// <para>
    /// Order matters. An explicit mod protection or a protected folder still wins over the override,
    /// so unprotecting Heliosphere mods and then protecting a folder containing one protects that
    /// one - the newer, more specific instruction - while leaving every Heliosphere mod outside that
    /// folder alone.
    /// </para>
    /// </remarks>
    private static bool IsEffectivelyProtected(
        OrganizerModRow row,
        IReadOnlySet<string> protectedModIdentifiers,
        IReadOnlySet<string> protectedFolders,
        IReadOnlySet<string> heliosphereUnprotectOverrides) =>
        (row.HeliosphereManaged && !heliosphereUnprotectOverrides.Contains(row.Identifier))
        || protectedModIdentifiers.Contains(row.Identifier)
        || OrganizationCleanupPlanner.IsUnderAnyProtectedFolder(row.CurrentPath, protectedFolders);

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
    /// <para>
    /// The folder-selection expressions live in <see cref="SortFolderSelectors"/> rather than here,
    /// because the template planner needs the identical computation to place mods a template has no
    /// entry for. Two copies would let a sorted folder and a planned folder drift apart, which the
    /// preview promises cannot happen.
    /// </para>
    /// </remarks>
    public int Sort(SortStrategy strategy, bool splitGear, bool splitNpc,
                    Func<string, string> canonicalizeCreator) =>
        Sort(row => SortFolderSelectors.Select(strategy, splitGear, splitNpc, row, canonicalizeCreator));

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
