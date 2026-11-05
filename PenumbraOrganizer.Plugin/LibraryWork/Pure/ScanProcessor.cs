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
    private BodyPriority _defaultBodyPriority = BodyPriority.Default([]);

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

    /// <summary>
    /// The labels of the body list this scan's matcher was built from. Published with the rows so the
    /// Sort tab ranks exactly the bodies the evidence was matched against.
    /// </summary>
    public IReadOnlyList<string> BodyLabels { get; private set; } = [];

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
        _warnings.AddRange(bodyResult.Warnings);
        _bodyTypeMatcher = BodyTypeListStore.BuildMatcher(bodyResult.Document);
        BodyLabels = [.. bodyResult.Document.Bodies.Select(b => b.Label)];
        _defaultBodyPriority = BodyPriority.Default(BodyLabels);
    }

    public OrganizerModRow? Process(ScanSeed item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var modPath = new DirectoryInfo(item.ModDirectoryPath);
        var classification = ModTypeClassifier.Classify(item.Name, item.ChangedItemKeys, _npcNameMatcher);

        // ONE walk of the mod's config for every row that needs any of it: slots for Gear, hair
        // evidence for Hair, and body evidence for Gear and Body. A Gear mod is read once, not once
        // per consumer. Null means the walk could not be trusted.
        ModContent? content = null;
        var walked = classification.Category is ModCategory.Gear or ModCategory.Hair or ModCategory.Body;
        if (walked)
            content = ModContentReader.Read(modPath);

        var gearSlotDiagnostic = GearSlotDiagnostic.NotApplicable;
        IReadOnlySet<EquipmentSlot>? equipmentSlots = null;
        if (classification.Category == ModCategory.Gear)
        {
            equipmentSlots = content?.Slots;
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

        ModHairFileReader.HairEvidence? hairEvidence = null;
        if (classification.Category == ModCategory.Hair)
        {
            hairEvidence = content?.Hair;
            classification = ModTypeClassifier.EnrichHairSubCategory(classification, hairEvidence?.Flat);
        }

        // The body a mod is for. Tier 1 is what is INSIDE the mod (option groups, options, the
        // first folder of its files), then its name, then the library folders above it - see
        // BodyCandidateBuilder. The slot gate is unchanged and still decides WHETHER a body applies;
        // it is what keeps a weapon, hat or ring from having its options searched for a body name.
        // A match is a STATED intent, not a verified fact (see BodyTypeMatcher).
        BodyCandidates? bodyCandidates = null;
        string? bodyType = null;
        if (ModTypeClassifier.BodyTypeApplies(classification.Category, equipmentSlots))
        {
            bodyCandidates = BodyCandidateBuilder.Build(
                _bodyTypeMatcher, item.Name, item.CurrentPath, content, contentReadFailed: walked && content is null);
            bodyType = BodyPriorityResolver.Resolve(bodyCandidates, _defaultBodyPriority)?.Label;
        }

        return new OrganizerModRow
        {
            Identifier = item.Identifier,
            Name = item.Name,
            Author = item.Author,
            CurrentPath = item.CurrentPath,
            ProposedPath = item.CurrentPath,
            HeliosphereManaged = HeliosphereDetector.IsHeliosphereManaged(
                item.Identifier, modPath, item.Name, _knownHeliosphereIdentifiers),
            ProteusRole = ProteusDetector.Classify(item.Identifier, item.Author),
            Category = classification.Category,
            SubCategory = classification.SubCategory,
            BodyType = bodyType,
            BodyCandidates = bodyCandidates,
            HairFlat = hairEvidence?.Flat,
            HairByRaceCode = hairEvidence?.ByRaceCode,
            GearSlotDiagnostic = gearSlotDiagnostic,
        };
    }
}
