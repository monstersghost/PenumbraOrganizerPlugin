namespace PenumbraOrganizer.Plugin.Organizer.Classification;

// Why a Gear mod did or didn't get a SubCategory from ModEquipmentFileReader/EnrichGearSubCategory.
// Recorded per-row at Scan time so the Export button can surface a breakdown without digging
// through the Dalamud log - added after a tester report where 0 of ~1500 Gear mods got a
// subcategory and there was no way to tell "every mod's config files failed to read" apart from
// "every mod is a legitimately ambiguous multi-piece outfit" without this.
public enum GearSlotDiagnostic
{
    NotApplicable,   // not a Gear mod (equipment-slot detection never runs for other categories)
    Single,          // resolved to exactly one slot - SubCategory was assigned
    Ambiguous,       // resolved to more than one slot - a real multi-piece outfit, not a bug
    ZeroEvidence,    // the mod's directory exists and every config file read fine, but none
                      // carried recognized equipment data - a real "nothing to find" case
    DirectoryMissing, // mod.ModPath.Exists was false - ReadEquipmentSlots can't distinguish this
                      // from ZeroEvidence on its own (by design, see its own tests), but it's a
                      // very different root cause worth separating for diagnostics: this means the
                      // path the IPC gave us for this mod couldn't be found at all, so no file was
                      // ever read - not "these files have no equipment info."
    ReadFailure,     // a config file could not be read or parsed - untrustworthy, treated as no evidence
}
