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
    public GearSlotDiagnostic GearSlotDiagnostic { get; init; } = GearSlotDiagnostic.NotApplicable;
}
