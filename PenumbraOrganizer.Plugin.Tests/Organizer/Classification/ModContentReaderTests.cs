using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class ModContentReaderTests
{
    private static DirectoryInfo MakeTempModDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "PenumbraOrganizer.Plugin.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new DirectoryInfo(path);
    }

    private const string Config = """
        {"DefaultData":{"Files":{
            "chara/equipment/e0001/model/c0101e0001_top.mdl":"Rue+\\top.mdl",
            "chara/human/c0101/obj/hair/h0115/model/c0101h0115_hir.mdl":"hair.mdl"},"Manipulations":[]},
         "Groups":[{"Name":"Body","Options":[{"Name":"Bibo+"}]}]}
        """;

    [Fact]
    public void Read_AgreesWithTheTwoStandaloneReaders()
    {
        var mod = MakeTempModDirectory();
        File.WriteAllText(Path.Combine(mod.FullName, "meta.json"), Config);

        var content = ModContentReader.Read(mod);

        Assert.NotNull(content);
        Assert.Equal(ModEquipmentFileReader.ReadEquipmentSlots(mod), content!.Slots);
        Assert.Equal([EquipmentSlot.Top], content.Slots);
        Assert.Equal(ModHairFileReader.ReadHairEvidence(mod)!.Flat, content.Hair.Flat);
        Assert.Equal(["Body"], content.GroupNames);
        Assert.Equal(["Bibo+"], content.OptionNames);
        Assert.Contains("Rue+/top.mdl", content.FileValues);
    }

    [Fact]
    public void Read_UnparseableConfig_IsNull()
    {
        var mod = MakeTempModDirectory();
        File.WriteAllText(Path.Combine(mod.FullName, "meta.json"), "{ nope");

        Assert.Null(ModContentReader.Read(mod));
    }

    [Fact]
    public void Read_DirectoryWithNoConfig_IsEmptyNotNull()
    {
        var content = ModContentReader.Read(MakeTempModDirectory());

        Assert.NotNull(content);
        Assert.Empty(content!.Slots);
        Assert.Empty(content.GroupNames);
    }
}
