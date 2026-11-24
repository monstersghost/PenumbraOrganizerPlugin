using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.LibraryWork.Pure;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.LibraryWork;

// The opt-in weapon classification (the Weapon "Job" split). The file shapes are the real ones from the author's
// library: "[SAM]-...-ONIKAGE SHURA" has 24 files under chara/weapon/w2201 plus VFX, sounds and chara/human
// animation files and no equipment; a "skin weapon" pack has chara/weapon/w2024 plus chara/accessory/a0132 _wrs
// files and must stay Gear.
public class ScanProcessorWeaponTests
{
    private const string Katana = "chara/weapon/w2201/obj/body/b0001/model/w2201b0001.mdl";
    private const string KatanaTexture = "chara/weapon/w2201/obj/body/b0001/texture/v01_w2201b0001_n.tex";

    private static OrganizerModRow Scan(bool classifyWeapons, string name, string changedItem, params string[] gamePaths) =>
        ScanWith(classifyWeapons, null, name, [changedItem], gamePaths).Row;

    private static (OrganizerModRow Row, ScanProcessor Processor) ScanWith(
        bool classifyWeapons, IItemJobLookup? lookup, string name, string[] changedItems, params string[] gamePaths)
    {
        var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        dir.Create();
        try
        {
            var entries = string.Join(",", gamePaths.Select((p, i) => "\"" + p + "\":\"f" + i + ".mdl\""));
            File.WriteAllText(Path.Combine(dir.FullName, "default_mod.json"), "{\"Files\":{" + entries + "}}");
            var processor = new ScanProcessor(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), useScrapedNpcNameList: false,
                classifyWeapons: classifyWeapons, itemJobLookup: lookup);
            processor.Prepare(CancellationToken.None);
            var row = processor.Process(
                new ScanSeed("mod-dir", name, "An Author", "Gear/" + name, dir.FullName, changedItems),
                CancellationToken.None)!;
            return (row, processor);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private sealed class ThrowingLookup : IItemJobLookup
    {
        public int Calls { get; private set; }

        public IReadOnlySet<string>? JobsFor(string itemName)
        {
            Calls++;
            throw new InvalidOperationException("sheet unavailable");
        }
    }

    [Fact]
    public void On_AWeaponGetsTheJobOfItsItems_FromTheInjectedLookup()
    {
        var lookup = new Organizer.Classification.WeaponJobsTests.FakeLookup(new() { ["Some Katana"] = ["SAM"] });
        var (row, _) = ScanWith(true, lookup, "Onikage Shura", ["Some Katana", "Vfx"], Katana);

        Assert.Equal(ModCategory.Weapon, row.Category);
        Assert.Equal("SAM", row.WeaponJob);
    }

    [Fact]
    public void On_AWeaponWithNoMatchingItem_HasNoJob()
    {
        var lookup = new Organizer.Classification.WeaponJobsTests.FakeLookup(new());
        Assert.Null(ScanWith(true, lookup, "Onikage Shura", ["Some Katana"], Katana).Row.WeaponJob);
    }

    [Fact]
    public void Off_OrNotAWeapon_TheLookupIsNeverAsked()
    {
        var lookup = new Organizer.Classification.WeaponJobsTests.FakeLookup(new() { ["Some Katana"] = ["SAM"] });
        Assert.Null(ScanWith(false, lookup, "Onikage Shura", ["Some Katana"], Katana).Row.WeaponJob);
        Assert.Null(ScanWith(true, lookup, "Dress", ["Some Katana"], "chara/equipment/e0001/model/c0101e0001_top.mdl").Row.WeaponJob);
        Assert.Empty(lookup.Asked);
    }

    [Fact]
    public void ALookupThatThrows_FailsSoft_NoJobAndOneWarning()
    {
        var lookup = new ThrowingLookup();
        var (row, processor) = ScanWith(true, lookup, "Onikage Shura", ["Some Katana"], Katana);

        Assert.Equal(ModCategory.Weapon, row.Category);
        Assert.Null(row.WeaponJob);
        Assert.Single(processor.Warnings, w => w.Contains("sheet unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public void Off_AWeaponOnlyMod_StaysGear_ExactlyAsToday()
    {
        var row = Scan(false, "Onikage Shura", "Some Katana", Katana, KatanaTexture);

        Assert.Equal(ModCategory.Gear, row.Category);
        Assert.Null(row.SubCategory);
        Assert.Equal(GearSlotDiagnostic.ZeroEvidence, row.GearSlotDiagnostic);
    }

    [Fact]
    public void On_AWeaponOnlyMod_IsWeapon_WithNoSlotDiagnosticOrBody()
    {
        var row = Scan(true, "Bibo+ Onikage Shura", "Some Katana", Katana, KatanaTexture);

        Assert.Equal(ModCategory.Weapon, row.Category);
        Assert.Null(row.SubCategory);
        Assert.Equal(GearSlotDiagnostic.NotApplicable, row.GearSlotDiagnostic);
        Assert.Null(row.BodyType);
        Assert.Null(row.BodyCandidates);
    }

    [Fact]
    public void On_AWeaponWithVfxSoundAndAnimationFiles_IsStillWeapon()
    {
        var row = Scan(true, "Onikage Shura", "Some Katana", Katana,
            "vfx/ws/w2201/eff/ws_w2201_01.avfx", "sound/vfx/ws/se_ws_w2201.scd",
            "chara/human/c0101/animation/a0001/bt_common/ws/samurai/ws_s01.pap");

        Assert.Equal(ModCategory.Weapon, row.Category);
    }

    [Theory]
    [InlineData("chara/accessory/a0132/model/c0101a0132_wrs.mdl")]
    [InlineData("chara/equipment/e6001/model/c0101e6001_top.mdl")]
    public void On_AWeaponWithEquipmentOrAccessoryFiles_StaysGear(string mixed)
    {
        var row = Scan(true, "Skin Weapon", "Some Katana", Katana, mixed);

        Assert.Equal(ModCategory.Gear, row.Category);
    }

    [Fact]
    public void On_AGearModWithNoWeaponFiles_StaysGear()
    {
        var row = Scan(true, "Physics Pack", "Some Robe", "chara/human/c0101/skeleton/base/b0001/phy_c0101b0001.phyb");

        Assert.Equal(ModCategory.Gear, row.Category);
        Assert.Equal(GearSlotDiagnostic.ZeroEvidence, row.GearSlotDiagnostic);
    }

    [Fact]
    public void On_ANonGearModWithWeaponFiles_KeepsItsCategory()
    {
        var row = Scan(true, "Katana Glow", "Vfx", Katana);

        Assert.Equal(ModCategory.VFX, row.Category);
    }

    [Fact]
    public void On_AnUnreadableMod_StaysGear()
    {
        var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        dir.Create();
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "default_mod.json"), "{ not json");
            var processor = new ScanProcessor(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), false, classifyWeapons: true);
            processor.Prepare(CancellationToken.None);
            var row = processor.Process(
                new ScanSeed("m", "Katana", "A", "Gear/Katana", dir.FullName, ["Some Katana"]), CancellationToken.None)!;

            Assert.Equal(ModCategory.Gear, row.Category);
            Assert.Equal(GearSlotDiagnostic.ReadFailure, row.GearSlotDiagnostic);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ClassifiesWeapons_ReportsTheFlagTheScanRanWith()
    {
        Assert.False(new ScanProcessor("x", false).ClassifiesWeapons);
        Assert.True(new ScanProcessor("x", false, classifyWeapons: true).ClassifiesWeapons);
    }

    [Fact]
    public void EnrichWeapon_LeavesEveryNonGearResultAlone_AndNeedsTrustedContent()
    {
        var gear = new ClassificationResult(ModCategory.Gear, null, ClassificationSource.Structural);
        Assert.Same(gear, ModTypeClassifier.EnrichWeapon(gear, null));

        var hair = new ClassificationResult(ModCategory.Hair, null, ClassificationSource.Structural);
        Assert.Same(hair, ModTypeClassifier.EnrichWeapon(hair, Content(Katana)));

        Assert.Equal(ModCategory.Weapon, ModTypeClassifier.EnrichWeapon(gear, Content(Katana)).Category);
        Assert.Equal(ModCategory.Weapon, ModTypeClassifier.EnrichWeapon(gear, Content("CHARA/WEAPON/w2201/x.mdl")).Category);
        Assert.Same(gear, ModTypeClassifier.EnrichWeapon(gear, Content()));
        Assert.Same(gear, ModTypeClassifier.EnrichWeapon(gear, Content(Katana) with
        {
            Slots = new HashSet<EquipmentSlot> { EquipmentSlot.Wrists },
        }));
    }

    private static ModContent Content(params string[] gamePaths) => new(
        new HashSet<EquipmentSlot>(), new ModHairFileReader.HairEvidence(new HashSet<int>(), new Dictionary<string, IReadOnlySet<int>>()),
        [], [], [], gamePaths);
}
