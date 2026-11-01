namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

using PenumbraOrganizer.Plugin.Organizer.Classification;

public class ModHairFileReaderTests
{
    private static DirectoryInfo MakeTempModDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "PenumbraOrganizer.Plugin.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new DirectoryInfo(path);
    }

    private static void WriteJson(DirectoryInfo modDirectory, string fileName, string json) =>
        File.WriteAllText(Path.Combine(modDirectory.FullName, fileName), json);

    [Fact]
    public void ReadHairNumbers_SingleNumberFromFiles_Resolves()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{"chara/human/c0101/obj/hair/h0115/model/c0101h0115_hir.mdl":"files/hair.mdl"},"Manipulations":[]}
            """);

        var result = ModHairFileReader.ReadHairNumbers(mod);

        Assert.NotNull(result);
        Assert.Equal([115], result);
    }

    [Fact]
    public void ReadHairNumbers_SameHairAcrossManyRaceCodes_CollapsesToOneNumber()
    {
        // The common case by far: one hair shipped for every clan. Without discarding the c####
        // race code this would read as ambiguous and the mod would get no subfolder at all.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{
              "chara/human/c0101/obj/hair/h0115/model/c0101h0115_hir.mdl":"files/a.mdl",
              "chara/human/c0801/obj/hair/h0115/model/c0801h0115_hir.mdl":"files/b.mdl",
              "chara/human/c1401/obj/hair/h0115/texture/c1401h0115_hir_n.tex":"files/c.tex"
            },"Manipulations":[]}
            """);

        var result = ModHairFileReader.ReadHairNumbers(mod);

        Assert.NotNull(result);
        Assert.Equal([115], result);
    }

    [Fact]
    public void ReadHairNumbers_TwoDistinctNumbers_ReturnsBoth()
    {
        // The reader reports what it found; deciding that two numbers means "no subfolder" is
        // EnrichHairSubCategory's job, not this one's.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{
              "chara/human/c0101/obj/hair/h0115/model/c0101h0115_hir.mdl":"files/a.mdl",
              "chara/human/c0101/obj/hair/h0009/model/c0101h0009_hir.mdl":"files/b.mdl"
            },"Manipulations":[]}
            """);

        var result = ModHairFileReader.ReadHairNumbers(mod);

        Assert.NotNull(result);
        Assert.Equal([9, 115], result!.Order());
    }

    [Fact]
    public void ReadHairNumbers_BackslashSeparators_StillResolve()
    {
        // Penumbra writes some Files keys with '\'. The walker normalises them.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{"chara\\human\\c0101\\obj\\hair\\h0115\\model\\c0101h0115_hir.mdl":"files/hair.mdl"},"Manipulations":[]}
            """);

        var result = ModHairFileReader.ReadHairNumbers(mod);

        Assert.NotNull(result);
        Assert.Equal([115], result);
    }

    [Fact]
    public void ReadHairNumbers_EstManipulationWithHairSlot_ResolvesFromSetId()
    {
        // SetId is the hair number; Entry is the EST skeleton row it maps to. Reading Entry would
        // silently produce a plausible-looking wrong number, which is why they differ here.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{},"Manipulations":[{"Type":"Est","Manipulation":{"Entry":161,"Gender":"Female","Race":"Miqote","SetId":157,"Slot":"Hair"}}]}
            """);

        var result = ModHairFileReader.ReadHairNumbers(mod);

        Assert.NotNull(result);
        Assert.Equal([157], result);
    }

    [Fact]
    public void ReadHairNumbers_EstManipulationWithFaceSlot_ContributesNothing()
    {
        // Est covers Face too. Only Hair is ours.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{},"Manipulations":[{"Type":"Est","Manipulation":{"Entry":4,"Gender":"Male","Race":"Hyur","SetId":201,"Slot":"Face"}}]}
            """);

        var result = ModHairFileReader.ReadHairNumbers(mod);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void ReadHairNumbers_EquipmentPaths_ContributeNothing()
    {
        // A gear mod must not acquire a hair number.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{"chara/equipment/e0387/model/c0101e0387_sho.mdl":"files/sho.mdl"},"Manipulations":[]}
            """);

        var result = ModHairFileReader.ReadHairNumbers(mod);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void ReadHairNumbers_MetaJsonLayout_ReachesNestedGroupsAndDefaultData()
    {
        // Penumbra 1.7.0's single-file layout. A number reachable only through Groups/Options or
        // only through DefaultData must still be found.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "meta.json", """
            {
              "DefaultData":{"Files":{"chara/human/c0101/obj/hair/h0115/model/c0101h0115_hir.mdl":"files/a.mdl"},"Manipulations":[]},
              "Groups":[{"Options":[{"Files":{"chara/human/c0101/obj/hair/h0009/model/c0101h0009_hir.mdl":"files/b.mdl"},"Manipulations":[]}]}]
            }
            """);

        var result = ModHairFileReader.ReadHairNumbers(mod);

        Assert.NotNull(result);
        Assert.Equal([9, 115], result!.Order());
    }

    [Fact]
    public void ReadHairNumbers_LegacyGroupFiles_AreRead()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{},"Manipulations":[]}
            """);
        WriteJson(mod, "group_001_style.json", """
            {"Options":[{"Files":{"chara/human/c0101/obj/hair/h0115/model/c0101h0115_hir.mdl":"files/a.mdl"},"Manipulations":[]}]}
            """);

        var result = ModHairFileReader.ReadHairNumbers(mod);

        Assert.NotNull(result);
        Assert.Equal([115], result);
    }

    [Fact]
    public void ReadHairNumbers_MalformedJson_ReturnsNull()
    {
        // An untrustworthy partial read must never look like a confident answer.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", "{\"Files\":{");

        Assert.Null(ModHairFileReader.ReadHairNumbers(mod));
    }

    [Fact]
    public void ReadHairNumbers_MissingDirectory_ReturnsEmptyNotNull()
    {
        // No directory is "nothing to find", not a read failure - the same distinction the
        // equipment reader draws, and the one the scan's diagnostics rely on.
        var mod = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "PenumbraOrganizer.Plugin.Tests", Guid.NewGuid().ToString("N")));

        var result = ModHairFileReader.ReadHairNumbers(mod);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void ReadHairNumbers_NonHairHumanPaths_ContributeNothing()
    {
        // chara/human also carries face, body, tail and ear paths. Only obj/hair is ours.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{"chara/human/c0101/obj/face/f0002/model/c0101f0002_fac.mdl":"files/f.mdl"},"Manipulations":[]}
            """);

        var result = ModHairFileReader.ReadHairNumbers(mod);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void ReadHairNumbers_UppercasePath_StillResolves()
    {
        // The pattern is IgnoreCase; this pins that rather than leaving it to the flag's presence.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{"CHARA/HUMAN/C0101/OBJ/HAIR/H0115/MODEL/C0101H0115_HIR.MDL":"files/hair.mdl"},"Manipulations":[]}
            """);

        var result = ModHairFileReader.ReadHairNumbers(mod);

        Assert.NotNull(result);
        Assert.Equal([115], result);
    }

    [Fact]
    public void ReadHairNumbers_NestedUnderContainers_IsReached()
    {
        // Options and Groups are covered above; Containers is the third nesting shape the walker
        // recurses into and was otherwise untested.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Containers":[{"Files":{"chara/human/c0101/obj/hair/h0115/model/c0101h0115_hir.mdl":"files/a.mdl"},"Manipulations":[]}]}
            """);

        var result = ModHairFileReader.ReadHairNumbers(mod);

        Assert.NotNull(result);
        Assert.Equal([115], result);
    }

    [Fact]
    public void ReadHairNumbers_GoodFileThenMalformedFile_ReturnsNullAndDiscardsWhatItFound()
    {
        // The important failure case. The walker collects into the caller's set as it goes, so a
        // failure AFTER a successful file must discard the partial result rather than return a
        // confident-looking subset. Without this test, returning the 115 it already had would pass
        // every other test in this file.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{"chara/human/c0101/obj/hair/h0115/model/c0101h0115_hir.mdl":"files/a.mdl"},"Manipulations":[]}
            """);
        WriteJson(mod, "group_001_broken.json", "{\"Options\":[");

        Assert.Null(ModHairFileReader.ReadHairNumbers(mod));
    }

    [Theory]
    [InlineData("h0000", null)]      // parses to 0, which IsHairNumber rejects - would throw in GetFolder
    [InlineData("h0001", 1)]         // the bottom of the supported range
    [InlineData("h9999", 9999)]      // the top of it
    public void ReadHairNumbers_BoundaryPathNumbers_AreFilteredToTheSupportedRange(
        string hairToken, int? expected)
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", $$"""
            {"Files":{"chara/human/c0101/obj/hair/{{hairToken}}/model/x.mdl":"files/a.mdl"},"Manipulations":[]}
            """);

        var result = ModHairFileReader.ReadHairNumbers(mod);

        Assert.NotNull(result);
        if (expected is null)
            Assert.Empty(result);
        else
            Assert.Equal([expected.Value], result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10000)]
    [InlineData(2147483647)]
    public void ReadHairNumbers_EstSetIdOutsideTheSupportedRange_IsDiscarded(int setId)
    {
        // Every one of these would format into a subcategory GetFolder throws on.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", $$$"""
            {"Files":{},"Manipulations":[{"Type":"Est","Manipulation":{"Entry":1,"Gender":"Female","Race":"Miqote","SetId":{{{setId}}},"Slot":"Hair"}}]}
            """);

        var result = ModHairFileReader.ReadHairNumbers(mod);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void ReadHairNumbers_ValidEvidenceBesideOutOfRangeEvidence_KeepsTheValidNumber()
    {
        // The deliberate policy: out-of-range evidence is discarded, NOT promoted to a read
        // failure. A stray h0000 beside a real h0115 still files the mod under 115.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{
              "chara/human/c0101/obj/hair/h0115/model/c0101h0115_hir.mdl":"files/a.mdl",
              "chara/human/c0101/obj/hair/h0000/model/c0101h0000_hir.mdl":"files/b.mdl"
            },"Manipulations":[]}
            """);

        var result = ModHairFileReader.ReadHairNumbers(mod);

        Assert.NotNull(result);
        Assert.Equal([115], result);
    }

    [Fact]
    public void ReadHairNumbers_PathsAndEstAgreeing_YieldOneNumber()
    {
        // The two evidence sources overlapping is the normal case for a hair mod, and it must not
        // read as ambiguous.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{"chara/human/c0101/obj/hair/h0115/model/c0101h0115_hir.mdl":"files/a.mdl"},
             "Manipulations":[{"Type":"Est","Manipulation":{"Entry":1,"Gender":"Female","Race":"Miqote","SetId":115,"Slot":"Hair"}}]}
            """);

        var result = ModHairFileReader.ReadHairNumbers(mod);

        Assert.NotNull(result);
        Assert.Equal([115], result);
    }

    [Fact]
    public void ReadHairNumbers_PathsAndEstDisagreeing_YieldBoth()
    {
        // Two numbers, so the mod files flat. This is the CORRECT reading of a mod that carries an
        // EST entry for one hair and replacement paths for another - it is not evidence that
        // SetId was misunderstood.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{"chara/human/c0101/obj/hair/h0115/model/c0101h0115_hir.mdl":"files/a.mdl"},
             "Manipulations":[{"Type":"Est","Manipulation":{"Entry":1,"Gender":"Female","Race":"Miqote","SetId":157,"Slot":"Hair"}}]}
            """);

        var result = ModHairFileReader.ReadHairNumbers(mod);

        Assert.NotNull(result);
        Assert.Equal([115, 157], result!.Order());
    }

    [Fact]
    public void ReadHairNumbers_NonAsciiDigitsInThePath_AreRejectedWithoutThrowing()
    {
        // A non-ASCII-digit path yields no number and does NOT fail the read.
        //
        // What this test does NOT pin, measured rather than assumed: swapping the pattern's
        // [0-9] back to \d leaves all 26 tests in this file green. .NET's \d does match these
        // fullwidth digits, so the path matches - but int.TryParse with NumberStyles.None and
        // InvariantCulture then rejects them, so nothing is added and the result is empty either
        // way. The two spellings are behaviourally identical HERE.
        //
        // They were not identical in the design: the original draft used int.Parse, where \d let
        // a fullwidth path through to a FormatException that the walker does not catch and that
        // would have escaped into the scan. TryParse closes that hole. [0-9] is kept as a second
        // line - it says what the pattern means and stops a future edit back to Parse from
        // reopening it - but it is defence in depth, not the thing holding the behaviour up, and
        // no test can distinguish the two while TryParse is there.
        //
        // Built from code points rather than written as "\u-FF10" escapes (hyphen inserted here so
        // this comment cannot be mangled the same way): the escape form has already been silently
        // converted to the pasted glyph once on its way into this repo. Code points cannot be.
        // 0xFF10/0xFF11/0xFF15 are FULLWIDTH DIGIT ZERO / ONE / FIVE, so this spells 0115.
        var fullwidthDigits = new string([(char)0xFF10, (char)0xFF11, (char)0xFF11, (char)0xFF15]);
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", $$"""
            {"Files":{"chara/human/c0101/obj/hair/h{{fullwidthDigits}}/model/x.mdl":"files/a.mdl"},"Manipulations":[]}
            """);

        var result = ModHairFileReader.ReadHairNumbers(mod);

        Assert.NotNull(result);   // NOT null: this is a rejected path, not a read failure
        Assert.Empty(result);
    }

    [Fact]
    public void ReadHairEvidence_KeysEachFileSetByItsRaceCode()
    {
        // c0201 has 147 and 169; c1801 has 3.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{
              "chara/human/c0201/obj/hair/h0147/texture/a.tex":"files/a.tex",
              "chara/human/c0201/obj/hair/h0169/texture/b.tex":"files/b.tex",
              "chara/human/c1801/obj/hair/h0003/model/c.mdl":"files/c.mdl"
            },"Manipulations":[]}
            """);

        var evidence = ModHairFileReader.ReadHairEvidence(mod);

        Assert.NotNull(evidence);
        Assert.Equal(new HashSet<int> { 147, 169 }, evidence.ByRaceCode["0201"].ToHashSet());
        Assert.Equal(new HashSet<int> { 3 }, evidence.ByRaceCode["1801"].ToHashSet());
        Assert.Equal(new HashSet<int> { 3, 147, 169 }, evidence.Flat.ToHashSet());
    }

    [Fact]
    public void ReadHairEvidence_FlatSetIsTheUnionOfTheRaceSets()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{
              "chara/human/c0201/obj/hair/h0115/model/a.mdl":"files/a.mdl",
              "chara/human/c0801/obj/hair/h0115/model/b.mdl":"files/b.mdl"
            },"Manipulations":[]}
            """);

        var evidence = ModHairFileReader.ReadHairEvidence(mod);

        Assert.NotNull(evidence);
        Assert.Equal(new HashSet<int> { 115 }, evidence.Flat.ToHashSet());
        Assert.Equal(new HashSet<int> { 115 }, evidence.ByRaceCode["0801"].ToHashSet());
    }

    [Fact]
    public void ReadHairEvidence_EstEntriesFeedTheFlatSetOnly()
    {
        // The EST entry's SetId is 115. It must reach the flat set and never any race code, because
        // the reader does not use the race an EST entry names.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{"chara/human/c0201/obj/hair/h0147/model/a.mdl":"files/a.mdl"},
             "Manipulations":[{"Type":"Est","Manipulation":{"Entry":1,"Gender":"Female","Race":"Miqote","SetId":115,"Slot":"Hair"}}]}
            """);

        var evidence = ModHairFileReader.ReadHairEvidence(mod);

        Assert.NotNull(evidence);
        Assert.Equal(new HashSet<int> { 115, 147 }, evidence.Flat.ToHashSet());
        Assert.False(evidence.ByRaceCode.Values.Any(set => set.Contains(115)));
    }

    [Fact]
    public void ReadHairNumbers_StillReturnsTheFlatSet()
    {
        // The 0.6.1.0 callers keep working: ReadHairNumbers is the flat set, unchanged.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{"chara/human/c0201/obj/hair/h0115/model/a.mdl":"files/a.mdl"},"Manipulations":[]}
            """);

        Assert.Equal(new HashSet<int> { 115 }, ModHairFileReader.ReadHairNumbers(mod)!.ToHashSet());
    }
}
