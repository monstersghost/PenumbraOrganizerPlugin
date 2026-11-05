using System.Text.Json;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class ModConfigFileWalkerNamesTests
{
    private static DirectoryInfo MakeTempModDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "PenumbraOrganizer.Plugin.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new DirectoryInfo(path);
    }

    private static void WriteJson(DirectoryInfo dir, string fileName, string json) =>
        File.WriteAllText(Path.Combine(dir.FullName, fileName), json);

    private static (bool Ok, List<string> Groups, List<string> Options, List<string> Keys, List<string> Values) Walk(DirectoryInfo dir)
    {
        var groups = new List<string>();
        var options = new List<string>();
        var keys = new List<string>();
        var values = new List<string>();
        var ok = ModConfigFileWalker.TryWalk(dir, new ConfigVisitor
        {
            OnFileKey = keys.Add,
            OnFileValue = values.Add,
            OnName = (kind, name) => (kind == ConfigNameKind.Group ? groups : options).Add(name),
        });
        return (ok, groups, options, keys, values);
    }

    [Fact]
    public void MetaJson_ReportsGroupAndOptionNames_ButNeverTheModTitle()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "meta.json", """
            {"Name":"Bibo+ Corset Gown","DefaultData":{"Files":{"a/b.mdl":"Base\\x.mdl"},"Manipulations":[]},
             "Groups":[{"Name":"Installation","Options":[{"Name":"Rue+","Files":{"c/d.mdl":"Rue+\\y.mdl"}},{"Name":"TBSE"}]}]}
            """);

        var (ok, groups, options, keys, values) = Walk(mod);

        Assert.True(ok);
        Assert.Equal(["Installation"], groups);
        Assert.Equal(["Rue+", "TBSE"], options);
        Assert.DoesNotContain("Bibo+ Corset Gown", groups.Concat(options));
        Assert.Equal(["a/b.mdl", "c/d.mdl"], keys.Order());
        Assert.Equal(["Base/x.mdl", "Rue+/y.mdl"], values.Order());
    }

    [Fact]
    public void LegacyLayout_GroupFileRootIsAGroup_DefaultModRootIsNot()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """{"Name":"Default Body","Files":{},"Manipulations":[]}""");
        WriteJson(mod, "group_001_install.json", """{"Name":"Install","Options":[{"Name":"YAB+","Files":{"k":"YAB+\\z.mdl"}}]}""");

        var (ok, groups, options, _, values) = Walk(mod);

        Assert.True(ok);
        Assert.Equal(["Install"], groups);
        Assert.Equal(["YAB+"], options);
        Assert.Equal(["YAB+/z.mdl"], values);
    }

    [Fact]
    public void ContainersCountAsOptionNames()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "meta.json", """
            {"Groups":[{"Name":"G","Containers":[{"Name":"Tight&Firm"}]}]}
            """);

        var (_, _, options, _, _) = Walk(mod);

        Assert.Equal(["Tight&Firm"], options);
    }

    [Fact]
    public void MissingOrNonStringNames_AreIgnored()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "meta.json", """{"Groups":[{"Options":[{"Name":5},{"Name":""},{}]}]}""");

        var (ok, groups, options, _, _) = Walk(mod);

        Assert.True(ok);
        Assert.Empty(groups);
        Assert.Empty(options);
    }

    [Fact]
    public void UnparseableConfig_ReturnsFalse()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "meta.json", "{ not json");

        Assert.False(Walk(mod).Ok);
    }

    [Fact]
    public void OldOverload_StillWalksKeysAndManipulations()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """{"Files":{"p/q.mdl":"r"},"Manipulations":[{"Type":"Eqp"}]}""");
        var keys = new List<string>();
        var manipulations = 0;

        var ok = ModConfigFileWalker.TryWalk(mod, keys.Add, _ => manipulations++);

        Assert.True(ok);
        Assert.Equal(["p/q.mdl"], keys);
        Assert.Equal(1, manipulations);
    }
}
