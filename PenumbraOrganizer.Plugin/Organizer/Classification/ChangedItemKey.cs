namespace PenumbraOrganizer.Plugin.Organizer.Classification;

public enum ChangedItemKeyShape
{
    Gear,
    Customization,
    Npc,
    Mount,
    Minion,
    Emote,
    Action,
    Icon,
    CategoryLiteral,
}

/// <summary>
/// One parsed GetChangedItems key. Captures every field the raw string can reliably
/// yield, not just what today's classifier consumes (spec: Layer 1 preserves signal
/// like Action/Icon for future use even though Layer 2 doesn't act on it yet).
/// </summary>
public sealed record ChangedItemKey(
    ChangedItemKeyShape Shape,
    string Raw,
    string? ItemName = null,
    string? Race = null,
    string? Gender = null,
    string? BodyPart = null,
    string? Subtype = null,
    int? Number = null,
    string? CategoryLiteral = null);
