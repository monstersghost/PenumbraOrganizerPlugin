namespace PenumbraOrganizer.Plugin.Organizer.NewMods;

/// <summary>Minimal facts about a newly added mod, read without a scan.</summary>
public sealed record NewModCandidate(
    string Identifier, string Name, string Author, string CurrentPath, bool HeliosphereManaged);

/// <summary>
/// Decides which newly added mods a root-only sort would actually move, so the banner never promises
/// more than will happen. Uses the one protection rule (ProtectionRule) with no session overrides.
/// </summary>
public static class NewModEligibility
{
    /// <summary>Heliosphere detection as the scan and ReadCurrentMods do it.</summary>
    public static bool IsHeliosphere(
        string identifier, DirectoryInfo modPath, string? displayName,
        IReadOnlySet<string>? knownHeliosphereIdentifiers) =>
        HeliosphereDetector.IsHeliosphereManaged(identifier, modPath, displayName, knownHeliosphereIdentifiers);

    /// <summary>Candidates that are loose in the root and not protected, as rows.</summary>
    public static IReadOnlyList<OrganizerModRow> Filter(
        IEnumerable<NewModCandidate> candidates,
        IReadOnlySet<string> protectedModIdentifiers,
        IReadOnlyDictionary<string, bool> folderRules,
        ToolPolicy policy)
    {
        var none = new HashSet<string>();
        var result = new List<OrganizerModRow>();
        foreach (var c in candidates)
        {
            if (!SortScope.IsAtRoot(c.CurrentPath))
                continue;

            var row = new OrganizerModRow
            {
                Identifier = c.Identifier,
                Name = c.Name,
                Author = c.Author,
                CurrentPath = c.CurrentPath,
                ProposedPath = c.CurrentPath,
                HeliosphereManaged = c.HeliosphereManaged,
                ProteusRole = ProteusDetector.Classify(c.Identifier, c.Author),
            };

            if (ProtectionRule.IsProtected(row, protectedModIdentifiers, folderRules, policy, none))
                continue;

            result.Add(row);
        }

        return result;
    }

    /// <summary>
    /// New mods loose in the root that a tool's Protect tab mode keeps in place (Heliosphere or Proteus set to
    /// Protected), so the user can be told instead of the batch passing silently. Follows ProtectionRule's tool
    /// clause; the Proteus managed output mod is left out (it has its own setting and is not a new install).
    /// </summary>
    public static ToolHeldCounts ToolHeld(IEnumerable<NewModCandidate> candidates, ToolPolicy policy)
    {
        var heliosphere = 0;
        var proteus = 0;
        foreach (var c in candidates)
        {
            if (!SortScope.IsAtRoot(c.CurrentPath))
                continue;

            switch (ProteusDetector.Classify(c.Identifier, c.Author))
            {
                case ProteusRole.Made when policy.Proteus == ToolMode.Protected:
                    proteus++;
                    break;
                case ProteusRole.None when c.HeliosphereManaged && policy.Heliosphere == ToolMode.Protected:
                    heliosphere++;
                    break;
            }
        }

        return new ToolHeldCounts(heliosphere, proteus);
    }
}

/// <summary>How many new mods each tool's Protected mode kept in place.</summary>
public readonly record struct ToolHeldCounts(int Heliosphere, int Proteus)
{
    public int Total => Heliosphere + Proteus;

    public static ToolHeldCounts operator +(ToolHeldCounts a, ToolHeldCounts b) =>
        new(a.Heliosphere + b.Heliosphere, a.Proteus + b.Proteus);
}
