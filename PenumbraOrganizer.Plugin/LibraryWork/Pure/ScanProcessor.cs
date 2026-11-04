using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;
using PenumbraOrganizer.Plugin.Organizer.Classification;
using PenumbraOrganizer.Plugin.Organizer.NpcNames;

namespace PenumbraOrganizer.Plugin.LibraryWork.Pure;

/// <summary>
/// The whole of the scan's per-mod work: classification, NPC name matching, and the gear-slot and
/// Heliosphere disk probes. Lifted verbatim from the old synchronous Plugin.RunScan body, with the
/// Penumbra adapter reads left behind on the framework thread in ScanJob.
///
/// May not reference Dalamud or Penumbra types - LibraryWorkPurityTests enforces this. Warnings are
/// collected rather than logged so the framework thread can log them at publish time instead of this
/// class reaching for IPluginLog off-thread.
/// </summary>
public sealed class ScanProcessor : ILibraryWorkProcessor<ScanSeed, OrganizerModRow>
{
    private readonly string _configDirectory;
    private readonly bool _useScrapedNpcNameList;
    private readonly IReadOnlySet<string> _knownHeliosphereIdentifiers;
    private readonly List<string> _warnings = [];

    private NpcNameMatcher _npcNameMatcher = NpcNameMatcher.Empty;
    private BodyTypeMatcher _bodyTypeMatcher = BodyTypeMatcher.Empty;

    /// <param name="useScrapedNpcNameList">
    /// Already the conjunction of the config flag and the compile-time feature gate - ScanJob
    /// resolves it on the framework thread. This class never reads Configuration itself.
    /// </param>
    /// <param name="knownHeliosphereIdentifiers">
    /// Identifiers an earlier scan already resolved as Heliosphere-managed. Carried so a mod whose
    /// heliosphere.json is momentarily absent - which is precisely what an in-progress Heliosphere
    /// update looks like - is not silently unprotected and then moved by the next Apply. Defaults to
    /// empty so existing tests construct this unchanged.
    /// </param>
    public ScanProcessor(
        string configDirectory,
        bool useScrapedNpcNameList,
        IReadOnlySet<string>? knownHeliosphereIdentifiers = null)
    {
        _configDirectory = configDirectory;
        _useScrapedNpcNameList = useScrapedNpcNameList;
        _knownHeliosphereIdentifiers = knownHeliosphereIdentifiers ?? new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary> Framework thread reads this after the run completes. </summary>
    public IReadOnlyList<string> Warnings => _warnings;

    public void Prepare(CancellationToken ct)
    {
        var result = NpcNameListStore.LoadForMatching(_configDirectory, _useScrapedNpcNameList);
        if (result.Warning is not null)
            _warnings.Add(result.Warning);
        _npcNameMatcher = NpcNameListStore.BuildMatcher(result.Document);

        // Built unconditionally, not behind the split flag. Prepare runs once per scan and this
        // list is 26 bodies, so there is nothing to save by deferring it - and the scan stores the
        // body type on the row either way, so toggling the split later re-sorts without rescanning,
        // the same as the gear and hair splits already do.
        var bodyResult = BodyTypeListStore.LoadForMatching(_configDirectory);
        if (bodyResult.Warning is not null)
            _warnings.Add(bodyResult.Warning);
        _bodyTypeMatcher = BodyTypeListStore.BuildMatcher(bodyResult.Document);
    }

    public OrganizerModRow? Process(ScanSeed item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var modPath = new DirectoryInfo(item.ModDirectoryPath);
        var classification = ModTypeClassifier.Classify(item.Name, item.ChangedItemKeys, _npcNameMatcher);

        // Disk I/O only for mods the changed-items rule already confirmed are Gear - every other
        // category never touches disk for this.
        var gearSlotDiagnostic = GearSlotDiagnostic.NotApplicable;
        // Hoisted: the body-type gate below needs these slots too, to tell gear that is cut to a
        // body's shape from a weapon or a ring that merely classifies as Gear.
        IReadOnlySet<EquipmentSlot>? equipmentSlots = null;
        if (classification.Category == ModCategory.Gear)
        {
            equipmentSlots = ModEquipmentFileReader.ReadEquipmentSlots(modPath);
            classification = ModTypeClassifier.EnrichGearSubCategory(classification, equipmentSlots);

            gearSlotDiagnostic = equipmentSlots switch
            {
                null => GearSlotDiagnostic.ReadFailure,
                { Count: 0 } when !modPath.Exists => GearSlotDiagnostic.DirectoryMissing,
                { Count: 0 } => GearSlotDiagnostic.ZeroEvidence,
                { Count: 1 } => GearSlotDiagnostic.Single,
                _ => GearSlotDiagnostic.Ambiguous,
            };
        }

        // Disk I/O only for mods the changed-items rule already confirmed are Hair, matching the
        // gear gate above - every other category never touches disk for this. No diagnostic
        // counterpart to gearSlotDiagnostic on purpose: both readers share one walker and read the
        // same files, so a library-wide read failure is already visible through the gear one.
        ModHairFileReader.HairEvidence? hairEvidence = null;
        if (classification.Category == ModCategory.Hair)
        {
            hairEvidence = ModHairFileReader.ReadHairEvidence(modPath);
            classification = ModTypeClassifier.EnrichHairSubCategory(classification, hairEvidence?.Flat);
        }

        // The body a mod is for, from its NAME, and no disk read at all - unlike the gear and hair
        // passes above. There is no structural signal to read: a garment made for Bibo+ replaces
        // its own equipment set, not the body's models, and nothing in its files names the body.
        // Proteus establishes this by mesh and UV correspondence against the body's own meshes,
        // which is far more work than a sort should do.
        //
        // Gear and Body only, and for Gear only when the slots say the mod is cut to a body's
        // shape - see ModTypeClassifier.BodyTypeApplies. That gate needs no extra disk read: the
        // gear pass above already resolved these slots.
        //
        // It is what keeps a weapon out. A weapon mod classifies as Gear, carries no equipment
        // slot, and would otherwise have its name searched for a body, filing an "Artemis Bow"
        // under the Artemis body. Hats, earrings, necklaces, bracelets and rings go the same way.
        var bodyType = ModTypeClassifier.BodyTypeApplies(classification.Category, equipmentSlots)
            ? _bodyTypeMatcher.Match(item.Name)?.Label
            : null;

        return new OrganizerModRow
        {
            Identifier = item.Identifier,
            Name = item.Name,
            Author = item.Author,
            CurrentPath = item.CurrentPath,
            ProposedPath = item.CurrentPath,
            HeliosphereManaged = HeliosphereDetector.IsHeliosphereManaged(
                item.Identifier, modPath, item.Name, _knownHeliosphereIdentifiers),
            Category = classification.Category,
            SubCategory = classification.SubCategory,
            BodyType = bodyType,
            HairFlat = hairEvidence?.Flat,
            HairByRaceCode = hairEvidence?.ByRaceCode,
            GearSlotDiagnostic = gearSlotDiagnostic,
        };
    }
}
