using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;
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
    // Folder path -> true (protect) / false (exclude). An exclude is what makes "protect haloween,
    // except haloween/animation" expressible: resolution takes the DEEPEST rule on a path, so a
    // rule here is a local override of whatever an ancestor said, not an addition to it.
    private Dictionary<string, bool> _folderProtectionRules = new(StringComparer.Ordinal);
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

    /// <summary>The body priority the last sort used, or null when it had the body split off.</summary>
    public BodyPriority? StagedBodyPriority { get; private set; }

    public bool HasScanned { get; private set; }

    /// <summary>
    /// Increments every time a scan is published. A template plan is computed for a preview and
    /// applied on a later frame; if a rescan lands in between, the plan describes rows that no
    /// longer exist. Callers hold the generation they planned against and refuse to apply a plan
    /// whose generation no longer matches, rather than applying it partially.
    /// </summary>
    public int ScanGeneration { get; private set; }

    public IReadOnlyCollection<string> ProtectedModIdentifiers => _protectedModIdentifiers;

    /// <summary>Every folder carrying a rule, mapped to true (protect) or false (exclude).</summary>
    public IReadOnlyDictionary<string, bool> FolderProtectionRules => _folderProtectionRules;

    /// <summary>
    /// Folders whose own rule protects. Kept for callers that only ever meant "which folders are
    /// ticked" - notably the config writer - and deliberately excludes exclude-rule folders.
    /// </summary>
    public IReadOnlyCollection<string> ProtectedFolders =>
        _folderProtectionRules.Where(pair => pair.Value).Select(pair => pair.Key).ToList();

    // Every ancestor prefix of every scanned mod's virtual-parent folder — not just each mod's
    // immediate parent — so the Protect tab can offer a checkbox for any level of the tree a user
    // might want to protect (e.g. "Gear" when mods only live at "Gear/Feet/..."). The upward walk
    // in ResolveFolderProtection already protects an entire subtree correctly once a
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

    // previouslyProtectedFolderRules defaults to null (treated as empty) so every existing call
    // site across this test project that predates folder protection keeps compiling unchanged.
    //
    // bodyLabels is deliberately LAST: the third parameter is positional in many existing callers,
    // so a body-labels call names it (`bodyLabels: ...`). There used to be a second overload that
    // took the labels third; it made `LoadScan(rows, ids, null)` ambiguous, so it is gone.
    public void LoadScan(
        IEnumerable<OrganizerModRow> scanned,
        IReadOnlySet<string> previouslyProtectedIdentifiers,
        IReadOnlyDictionary<string, bool>? previouslyProtectedFolderRules = null,
        IReadOnlyList<string>? bodyLabels = null) =>
        ReplaceScanAtomically(scanned, previouslyProtectedIdentifiers, previouslyProtectedFolderRules, bodyLabels);

    /// <summary>
    /// The body labels of the scan that produced the current rows; null until a scan has published.
    /// They travel with the rows so the evidence and the priority ranking it share one snapshot.
    /// </summary>
    public IReadOnlyList<string>? BodyLabels { get; private set; }

    /// <summary>
    /// Whole-state replacement that either fully happens or does not happen at all. Every
    /// replacement collection is built first; the field references are swapped only once all
    /// derivation has succeeded. A background scan publishes through this, so a throw here must
    /// leave the previously published scan exactly as it was rather than half-replaced.
    /// </summary>
    public void ReplaceScanAtomically(
        IEnumerable<OrganizerModRow> scanned,
        IReadOnlySet<string> previouslyProtectedIdentifiers,
        IReadOnlyDictionary<string, bool>? previouslyProtectedFolderRules = null,
        IReadOnlyList<string>? bodyLabels = null)
    {
        var replacementProtectedIdentifiers = new HashSet<string>(previouslyProtectedIdentifiers, StringComparer.Ordinal);
        var replacementFolderRules = previouslyProtectedFolderRules is null
            ? new Dictionary<string, bool>(StringComparer.Ordinal)
            : new Dictionary<string, bool>(previouslyProtectedFolderRules, StringComparer.Ordinal);
        // A scan re-asserts Heliosphere protection unconditionally, so this session's overrides end
        // here. Built as a replacement like everything else so a throw below leaves them untouched.
        var replacementHeliosphereOverrides = new HashSet<string>(StringComparer.Ordinal);

        var replacementMods = new Dictionary<string, OrganizerModRow>();
        foreach (var row in scanned)
        {
            // Protection is derived against the REPLACEMENT sets, not the live fields, so this loop
            // reads nothing it is about to overwrite.
            row.Protected = IsEffectivelyProtected(
                row, replacementProtectedIdentifiers, replacementFolderRules, replacementHeliosphereOverrides);
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
        _folderProtectionRules = replacementFolderRules;
        _heliosphereUnprotectOverrides = replacementHeliosphereOverrides;
        _mods = replacementMods;
        _knownFolders = replacementKnownFolders;
        BodyLabels = bodyLabels;
        StagedBodyPriority = null;
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
    public void SetFolderProtected(string folderPath, bool value) =>
        SetFolderProtectionRule(folderPath, value ? FolderProtectionRule.Protected : FolderProtectionRule.None);

    /// <summary>
    /// Writes (or withdraws) this folder's own rule. <see cref="FolderProtectionRule.None"/>
    /// removes the rule, so the folder inherits from its nearest ruled ancestor again.
    /// </summary>
    public void SetFolderProtectionRule(string folderPath, FolderProtectionRule rule)
    {
        var normalized = OrganizationCleanupPlanner.NormalizeFolderPath(folderPath);
        if (normalized is null)
            return;

        if (rule == FolderProtectionRule.None)
            _folderProtectionRules.Remove(normalized);
        else
            _folderProtectionRules[normalized] = rule == FolderProtectionRule.Protected;

        foreach (var row in _mods.Values)
            row.Protected = IsEffectivelyProtectedFull(row);
    }

    /// <summary>A folder's effective rule and the folder that supplied it.</summary>
    /// <param name="Source">
    /// The folder whose rule decided this - the folder itself when it carries its own rule, an
    /// ancestor when inherited, null when no rule applies. The Protect tab shows an inherited
    /// state dimmed and names this in the tooltip.
    /// </param>
    public readonly record struct FolderRuleResolution(FolderProtectionRule Rule, string? Source);

    /// <summary>
    /// Resolves the rule for a FOLDER node, where the folder's own rule counts - unlike the mod
    /// path resolver, which starts at the mod's virtual parent.
    /// </summary>
    /// <param name="folderPath">
    /// Nullable, so a caller holding a mod's virtual parent (null for a mod at the library root)
    /// can pass it straight through and get None back.
    /// </param>
    public FolderRuleResolution ResolveFolderRule(string? folderPath)
    {
        var normalized = OrganizationCleanupPlanner.NormalizeFolderPath(folderPath);
        for (var folder = normalized; folder is not null; folder = OrganizationCleanupPlanner.GetVirtualParent(folder))
        {
            if (_folderProtectionRules.TryGetValue(folder, out var isProtected))
                return new FolderRuleResolution(
                    isProtected ? FolderProtectionRule.Protected : FolderProtectionRule.Excluded, folder);
        }

        return new FolderRuleResolution(FolderProtectionRule.None, null);
    }

    /// <summary>
    /// How many protected mods sit inside a folder, and how many of those this folder's own
    /// unprotect action could actually free.
    /// </summary>
    /// <param name="Protected">Protected mods inside the folder, at any depth, from any source.</param>
    /// <param name="Freeable">
    /// How many of those <see cref="UnprotectFolderContents"/> would leave unprotected. Lower than
    /// <paramref name="Protected"/> when a protected ANCESTOR folder also covers a row, because this
    /// action withdraws only this folder's own rule.
    /// </param>
    /// <param name="Total">Every mod inside the folder at any depth, protected or not.</param>
    public readonly record struct FolderProtectionCounts(int Protected, int Freeable, int Total);

    /// <summary>
    /// Clears protection for the mods currently inside <paramref name="folderPath"/> (recursively)
    /// that this folder's rule can actually free, and drops the folder's own rule. Returns how many
    /// rows went from protected to unprotected.
    /// </summary>
    /// <remarks>
    /// The folder checkbox stays a pure folder-rule toggle: unticking it withdraws only the folder's
    /// own contribution, so an explicit per-mod tick or a protected ancestor still holds. That is
    /// correct and composable, but it left "protect everything except this one folder" impossible -
    /// "Toggle protect all" writes an explicit entry for every mod and ticks no folder at all, so
    /// there was no folder to untick and every row stayed protected, which is how it was reported
    /// ("hit sort and it goes apply to 0 mods"). This is the affirmative bulk action for that:
    /// explicit entries under the folder go, Heliosphere rows under it get the same transient
    /// override the Heliosphere toggle sets, and the folder rule itself is withdrawn.
    /// <para>
    /// A row that ANOTHER folder's rule still covers is skipped entirely rather than half-cleared.
    /// Those rules are separate instructions this action does not withdraw, so the row would stay
    /// protected anyway; clearing its tick or recording a Heliosphere override for it would do
    /// nothing visible now and then surface later, when that other folder is unticked, as
    /// protection the user never knowingly dropped.
    /// </para>
    /// <para>
    /// It withdraws a protect rule on this folder and keeps an exclude. It never writes a rule, so
    /// it still cannot escape a protected ancestor on its own - that remains a deliberate click on
    /// the folder's own tri-state box. On an already-excluded folder its job is just the stragglers
    /// an exclude cannot touch: hand-ticked rows and Heliosphere-managed ones.
    /// </para>
    /// </remarks>
    public int UnprotectFolderContents(string folderPath)
    {
        var normalized = OrganizationCleanupPlanner.NormalizeFolderPath(folderPath);
        if (normalized is null)
            return 0;

        var scope = new HashSet<string>([normalized], StringComparer.Ordinal);
        var contents = _mods.Values
            .Where(row => OrganizationCleanupPlanner.IsUnderAnyProtectedFolder(row.CurrentPath, scope))
            .ToList();
        var wereProtected = contents.Where(row => row.Protected).ToList();

        // Removed first, so the check below reads the rules this action leaves behind: what
        // remains there is exactly the protection it is not withdrawing. Only a protect rule is
        // withdrawn; an exclude on this folder is kept, since dropping it would let an ancestor's
        // protection flow back in.
        if (_folderProtectionRules.TryGetValue(normalized, out var ownRule) && ownRule)
            _folderProtectionRules.Remove(normalized);
        foreach (var row in contents)
        {
            if (OrganizationCleanupPlanner.ResolveFolderProtection(row.CurrentPath, _folderProtectionRules)
                == FolderProtectionRule.Protected)
                continue;

            _protectedModIdentifiers.Remove(row.Identifier);
            if (row.HeliosphereManaged)
                SetHeliosphereOverride(row.Identifier, unprotected: true);
        }

        // Full recompute through the one protection rule, as every other protection path does,
        // rather than assigning the outcome this method just reasoned about to each row by hand.
        foreach (var row in _mods.Values)
            row.Protected = IsEffectivelyProtectedFull(row);

        // Rows that were already unprotected are not something this action freed, so the caller
        // can report this number as "N mods unprotected" without overstating it.
        return wereProtected.Count(row => !row.Protected);
    }

    /// <summary>
    /// Mod counts keyed by folder: every folder holding at least one mod at any depth, mapped to
    /// how many it holds, how many of those are protected, and how many its own unprotect action
    /// could free. Folders holding no mods are absent.
    /// </summary>
    /// <remarks>
    /// One pass over the rows, walking each row's ancestors, rather than a per-folder query. The
    /// Protect tab needs this for every folder row it draws, every frame, so asking per folder is
    /// O(folders x mods) on the render thread.
    /// <para>
    /// Freeable is what stops the tab offering an action that cannot do anything. The action
    /// withdraws this folder's protect rule and keeps an exclude, so a row is freeable unless some
    /// rule still protects it afterwards.
    /// </para>
    /// </remarks>
    public Dictionary<string, FolderProtectionCounts> BuildProtectedModCountsByFolder()
    {
        var counts = new Dictionary<string, FolderProtectionCounts>(StringComparer.Ordinal);
        // One buffer for the whole pass, cleared per row, rather than a list per row: this runs
        // every frame over every mod.
        var ancestors = new List<string>();
        foreach (var row in _mods.Values)
        {
            if (!row.Protected)
            {
                // Unprotected rows only add to Total, so they need no rule resolution at all.
                for (var folder = OrganizationCleanupPlanner.GetVirtualParent(row.CurrentPath);
                     folder is not null;
                     folder = OrganizationCleanupPlanner.GetVirtualParent(folder))
                {
                    var existing = counts.GetValueOrDefault(folder);
                    counts[folder] = existing with { Total = existing.Total + 1 };
                }

                continue;
            }

            ancestors.Clear();
            for (var folder = OrganizationCleanupPlanner.GetVirtualParent(row.CurrentPath);
                 folder is not null;
                 folder = OrganizationCleanupPlanner.GetVirtualParent(folder))
                ancestors.Add(folder);

            // Nearest-first, so the first ruled ancestor is the one deciding this row today and
            // the next one is what the row would fall back to if that rule were withdrawn. This
            // is why no dictionary copy is needed per folder: the only rule whose removal can
            // change the outcome is the deepest one - and an exclude is never removed at all.
            var deepestRuledIndex = -1;
            for (var i = 0; i < ancestors.Count; i++)
            {
                if (_folderProtectionRules.ContainsKey(ancestors[i]))
                {
                    deepestRuledIndex = i;
                    break;
                }
            }

            string? fallbackRuled = null;
            if (deepestRuledIndex >= 0)
            {
                for (var i = deepestRuledIndex + 1; i < ancestors.Count; i++)
                {
                    if (_folderProtectionRules.ContainsKey(ancestors[i]))
                    {
                        fallbackRuled = ancestors[i];
                        break;
                    }
                }
            }

            var protectedByDeepest = deepestRuledIndex >= 0 && _folderProtectionRules[ancestors[deepestRuledIndex]];
            var protectedByFallback = fallbackRuled is not null && _folderProtectionRules[fallbackRuled];

            for (var i = 0; i < ancestors.Count; i++)
            {
                var folder = ancestors[i];
                var current = counts.GetValueOrDefault(folder);

                // What the action would leave: it clears per-mod sources inside, withdraws this
                // folder's PROTECT rule, and keeps an exclude. So the row is freeable unless some
                // rule still protects it afterwards - the fallback when this folder's own protect
                // rule is the one deciding, the deciding rule itself otherwise. An exclude on this
                // folder survives the action, so it still decides and the row is free.
                var ownRuleIsExclude = i == deepestRuledIndex && !protectedByDeepest;
                var freeable = ownRuleIsExclude
                    || (i == deepestRuledIndex ? !protectedByFallback : !protectedByDeepest);
                counts[folder] = new FolderProtectionCounts(
                    current.Protected + 1, current.Freeable + (freeable ? 1 : 0), current.Total + 1);
            }
        }

        return counts;
    }

    private bool IsEffectivelyProtectedFull(OrganizerModRow row) =>
        IsEffectivelyProtected(row, _protectedModIdentifiers, _folderProtectionRules, _heliosphereUnprotectOverrides);

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
    /// <para>
    /// The folder clause is the only one an exclude rule can cancel. A mod inside an excluded
    /// folder that is Heliosphere-managed or ticked by hand stays protected, because those are
    /// per-mod instructions the folder tree has no business reversing.
    /// </para>
    /// </remarks>
    private static bool IsEffectivelyProtected(
        OrganizerModRow row,
        IReadOnlySet<string> protectedModIdentifiers,
        IReadOnlyDictionary<string, bool> folderRules,
        IReadOnlySet<string> heliosphereUnprotectOverrides) =>
        (row.HeliosphereManaged && !heliosphereUnprotectOverrides.Contains(row.Identifier))
        || protectedModIdentifiers.Contains(row.Identifier)
        || OrganizationCleanupPlanner.ResolveFolderProtection(row.CurrentPath, folderRules)
            == FolderProtectionRule.Protected;

    public bool AssignManual(string identifier, string proposedPath)
    {
        if (!_mods.TryGetValue(identifier, out var row) || row.Protected)
            return false;

        row.ProposedPath = proposedPath;
        row.StagedBodyResolution = null;
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
        Sort(strategy, splitGear, splitNpc, splitHair: false, canonicalizeCreator);

    /// <summary>
    /// The sort with the hair and two body splits and the optional race priority. With no priority and
    /// both splits off, this is the same folder selection as the four-argument overload.
    /// </summary>
    public int Sort(SortStrategy strategy, bool splitGear, bool splitNpc, bool splitHair,
                    Func<string, string> canonicalizeCreator, HairPriority? hairPriority = null,
                    bool splitGearBody = false, bool splitBodyMods = false, BodyPriority? bodyPriority = null)
    {
        // Recorded BEFORE the paths are computed, from the same inputs, so the explanation and the
        // proposal cannot disagree. A resolution is kept only for rows whose own category's flag is
        // on, and cleared otherwise so a stale one never lingers.
        StagedBodyPriority = splitGearBody || splitBodyMods ? bodyPriority : null;
        foreach (var row in Mods)
        {
            var applies = row.Category == ModCategory.Gear ? splitGearBody
                : row.Category == ModCategory.Body && splitBodyMods;
            row.StagedBodyResolution = applies && bodyPriority is not null
                ? BodyPriorityResolver.Resolve(row.BodyCandidates, bodyPriority)
                : null;
        }

        return Sort(row => SortFolderSelectors.Select(strategy, splitGear, splitNpc, row, canonicalizeCreator,
                                                      splitHair: splitHair, hairPriority: hairPriority,
                                                      splitGearBody: splitGearBody, splitBodyMods: splitBodyMods,
                                                      bodyPriority: bodyPriority));
    }

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
            row.StagedBodyResolution = null;
            touched.Add(row);
        }

        // The staged record describes the last SORT; a template re-pathed rows without a body split.
        StagedBodyPriority = null;

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
