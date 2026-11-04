namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

using PenumbraOrganizer.Plugin.Organizer.Classification;

public class ModBodyFileReaderTests
{
    private static DirectoryInfo MakeTempModDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "PenumbraOrganizer.Plugin.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new DirectoryInfo(path);
    }

    private static void WriteJson(DirectoryInfo modDirectory, string fileName, string json) =>
        File.WriteAllText(Path.Combine(modDirectory.FullName, fileName), json);

    [Theory]
    [InlineData("_top")]
    [InlineData("_dwn")]
    [InlineData("_glv")]
    [InlineData("_sho")]
    public void ReadIsBodyReplacer_SmallclothesModelInAnyHalf_IsTrue(string slot)
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", $$"""
            {"Files":{"chara/equipment/e0000/model/c0201e0000{{slot}}.mdl":"files/body.mdl"},"Manipulations":[]}
            """);

        Assert.True(ModBodyFileReader.ReadIsBodyReplacer(mod));
    }

    [Fact]
    public void ReadIsBodyReplacer_GearModel_IsFalse()
    {
        // A real gear mod: a non-e0000 equipment set. This is the case that makes the whole feature
        // safe, because such a mod is very often NAMED after the body it was made for.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{"chara/equipment/e6042/model/c0201e6042_top.mdl":"files/dress.mdl"},"Manipulations":[]}
            """);

        Assert.False(ModBodyFileReader.ReadIsBodyReplacer(mod));
    }

    [Fact]
    public void ReadIsBodyReplacer_SkinTextureOnly_IsFalse()
    {
        // Skin mods sit next to bodies in a library and share their vocabulary, but replace no
        // body MODEL, so they must not be filed under a body type.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{"chara/human/c0201b0001/texture/c0201b0001_base.tex":"files/skin.tex"},"Manipulations":[]}
            """);

        Assert.False(ModBodyFileReader.ReadIsBodyReplacer(mod));
    }

    [Fact]
    public void ReadIsBodyReplacer_PathMerelyContainingTheShape_IsFalse()
    {
        // The regex is anchored at BOTH ends. An unanchored match would accept this.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{"mods/chara/equipment/e0000/model/c0201e0000_top.mdl.bak":"files/x.mdl"},"Manipulations":[]}
            """);

        Assert.False(ModBodyFileReader.ReadIsBodyReplacer(mod));
    }

    [Fact]
    public void ReadIsBodyReplacer_FullwidthDigitsInRaceCode_IsFalse()
    {
        // [0-9] not \d: .NET's \d matches every Unicode decimal digit, so this would MATCH under
        // \d while naming no real game path. ModHairFileReader carries the same guard.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{"chara/equipment/e0000/model/c０２０１e0000_top.mdl":"files/x.mdl"},"Manipulations":[]}
            """);

        Assert.False(ModBodyFileReader.ReadIsBodyReplacer(mod));
    }

    [Fact]
    public void ReadIsBodyReplacer_BackslashSeparators_IsTrue()
    {
        // Penumbra writes some keys with '\'. The walker normalises them, so the reader never sees
        // one - this pins that contract rather than re-testing the walker.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{"chara\\equipment\\e0000\\model\\c0101e0000_dwn.mdl":"files/body.mdl"},"Manipulations":[]}
            """);

        Assert.True(ModBodyFileReader.ReadIsBodyReplacer(mod));
    }

    [Fact]
    public void ReadIsBodyReplacer_BodyModelBehindAnOption_IsTrue()
    {
        // Every real body ships its halves as options; a body found only in default_mod.json would
        // be the exception. This walks the Options shape the walker recurses into.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """{"Files":{},"Manipulations":[]}""");
        WriteJson(mod, "group_001_chest.json", """
            {"Name":"Chest","Type":"Single","Options":[
              {"Name":"--- SFW ---","Files":{},"Manipulations":[]},
              {"Name":"Medium","Files":{"chara/equipment/e0000/model/c0201e0000_top.mdl":"files/m.mdl"},"Manipulations":[]}
            ]}
            """);

        Assert.True(ModBodyFileReader.ReadIsBodyReplacer(mod));
    }

    [Fact]
    public void ReadIsBodyReplacer_MalformedJson_IsNull()
    {
        // Null is "no evidence", never a confident false - the contract the gear and hair readers
        // already set. EnrichBodySubCategory requires true, so null leaves the mod flat in Body.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", "{ this is not json");

        Assert.Null(ModBodyFileReader.ReadIsBodyReplacer(mod));
    }

    [Fact]
    public void ReadIsBodyReplacer_MissingDirectory_IsFalseNotNull()
    {
        // Nothing to find is not a read failure - the walker's own documented distinction.
        var missing = new DirectoryInfo(Path.Combine(Path.GetTempPath(),
            "PenumbraOrganizer.Plugin.Tests", Guid.NewGuid().ToString("N")));

        Assert.False(ModBodyFileReader.ReadIsBodyReplacer(missing));
    }

    [Fact]
    public void ReadIsBodyReplacer_ManipulationsOnly_IsFalse()
    {
        // Metadata alone is not a body. Plenty of gear mods carry smallclothes EQDP entries, which
        // is why the reader looks at models only.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{},"Manipulations":[
              {"Type":"Eqdp","Manipulation":{"Slot":"Body","SetId":0,"Race":"Midlander","Gender":"Female"}}
            ]}
            """);

        Assert.False(ModBodyFileReader.ReadIsBodyReplacer(mod));
    }
}
