namespace PenumbraOrganizer.Plugin.Organizer;

public static class ProtectionRule
{
    /// <summary>
    /// The single protection rule. Every path recomputes through this one.
    /// </summary>
    /// <remarks>
    /// There used to be a second, divergent rule for interactive toggles that simply omitted the
    /// Heliosphere clause. Two rules meant the answer depended on which control the user last
    /// touched, which is exactly how ticking a folder came to reverse an explicit Heliosphere
    /// unprotect. The override set replaces that divergence: tool-managed mods are protected by default according to their tool's mode,
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
    public static bool IsProtected(
        OrganizerModRow row,
        IReadOnlySet<string> protectedModIdentifiers,
        IReadOnlyDictionary<string, bool> folderRules,
        ToolPolicy policy,
        IReadOnlySet<string> toolUnprotectOverrides) =>
        ToolProtects(row, policy, toolUnprotectOverrides)
        || protectedModIdentifiers.Contains(row.Identifier)
        || OrganizationCleanupPlanner.ResolveFolderProtection(row.CurrentPath, folderRules)
            == FolderProtectionRule.Protected;

    // The tool clause of the single protection rule. A per-mod override (the user unticked a protected
    // tool mod) cancels it; an explicit mod protection or a protected folder, checked by the caller,
    // still beats the override. The Proteus managed mod is outside the three modes: protected unless
    // the user asked for it at the root.
    private static bool ToolProtects(
        OrganizerModRow row, ToolPolicy policy, IReadOnlySet<string> toolUnprotectOverrides)
    {
        if (toolUnprotectOverrides.Contains(row.Identifier))
            return false;

        return row.ProteusRole switch
        {
            ProteusRole.Managed => !policy.ProteusManagedAtRoot,
            ProteusRole.Made => policy.Proteus == ToolMode.Protected,
            _ => row.HeliosphereManaged && policy.Heliosphere == ToolMode.Protected,
        };
    }
}
