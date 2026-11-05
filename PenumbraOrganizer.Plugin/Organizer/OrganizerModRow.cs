using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Organizer;

public sealed class OrganizerModRow
{
    public required string Identifier { get; init; }
    public required string Name { get; init; }
    public required string Author { get; init; }
    public required string CurrentPath { get; init; }
    public required string ProposedPath { get; set; }
    public bool Protected { get; set; }
    public bool HeliosphereManaged { get; init; }
    public ModCategory? Category { get; init; }
    public string? SubCategory { get; init; }

    /// <summary>Every supported hair number the mod's files and EST entries carry. Null unless a readable hair mod.</summary>
    public IReadOnlySet<int>? HairFlat { get; init; }

    /// <summary>Hair numbers keyed by four-digit race folder code, from file paths only. Null unless a readable hair mod.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<int>>? HairByRaceCode { get; init; }
    /// <summary>
    /// The body replacer this mod is for, as the body-type list spells it ("Bibo+"), or null when
    /// its name matched none.
    /// </summary>
    /// <remarks>
    /// Its OWN dimension, deliberately not folded into <see cref="SubCategory"/>. A gear mod
    /// already uses that for its equipment slot, and the point of this field is a gear mod that is
    /// both - "Bibo+ Medieval Dress" is a Top AND a Bibo+ garment, filed at Gear/Bibo+/Top.
    /// <para>
    /// Set for Gear and Body rows only, from the mod's NAME alone. There is no structural signal
    /// for which body a garment was cut for: it replaces its own equipment set, not the body's
    /// models, and nothing in its files names the body. Proteus establishes this by mesh and UV
    /// correspondence against the body's meshes, which is far beyond what a sort should do.
    /// </para>
    /// </remarks>
    public string? BodyType { get; init; }

    /// <summary>
    /// Every body this mod could file under, by tier. Null unless the row passed the body gate
    /// (Gear cut to a body's shape, or a Body mod). Resolved against the user's priority at SORT
    /// time, so reordering the priority re-files mods without a rescan; <see cref="BodyType"/>
    /// holds the resolution under the DEFAULT order for the dump and for callers with no priority.
    /// </summary>
    public Classification.BodyCandidates? BodyCandidates { get; init; }

    public GearSlotDiagnostic GearSlotDiagnostic { get; init; } = GearSlotDiagnostic.NotApplicable;
}
