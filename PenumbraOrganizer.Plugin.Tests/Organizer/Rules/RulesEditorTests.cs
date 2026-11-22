using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;
using PenumbraOrganizer.Plugin.Organizer.Rules;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Rules;

public class RulesEditorTests
{
    private long _now;

    private RulesEditor Editor(params (string, bool)[] saved) =>
        new(saved.Select(s => new SortRuleEntry { Text = s.Item1, Enabled = s.Item2 }).ToList(), () => _now);

    private static OrganizerModRow Row(string id, string name, bool prot = false) => new()
    {
        Identifier = id, Name = name, Author = "Ann", CurrentPath = name, ProposedPath = name,
        Category = ModCategory.Gear, Protected = prot,
    };

    private static void Observe(RulesEditor e, int gen, params OrganizerModRow[] rows) =>
        e.Observe(rows, gen, s => s, RaceSplitOptions.Off, null, false, false);

    private static string Canon(string s) => s;

    private string CanonInstance(string s) => s;

    [Fact]
    public void Add_remove_move_and_toggle()
    {
        var e = Editor(("name = 'a' -> A", true), ("name = 'b' -> B", true));
        Assert.True(e.Add());
        Assert.Equal(3, e.Rows.Count);
        e.SetText(2, "name = 'c' -> C");
        Assert.True(e.MoveUp(2));
        Assert.Equal("name = 'c' -> C", e.Rows[1].Text);
        Assert.False(e.MoveUp(0));
        Assert.True(e.MoveDown(0));
        Assert.Equal("name = 'c' -> C", e.Rows[0].Text);
        e.SetEnabled(0, false);
        Assert.False(e.ToConfig()[0].Enabled);
        e.Remove(0);
        Assert.Equal(2, e.Rows.Count);
    }

    [Fact]
    public void Add_stops_at_one_hundred_rules()
    {
        var e = Editor();
        for (var i = 0; i < 100; i++)
            Assert.True(e.Add());
        Assert.False(e.CanAdd);
        Assert.False(e.Add());
    }

    [Fact]
    public void Status_is_a_red_error_with_position_for_a_bad_rule()
    {
        var e = Editor(("nmae = 'a' -> X", true));
        var s = e.Status(0);
        Assert.True(s.IsError);
        Assert.Equal("Unknown field 'nmae'. Did you mean 'name'? (at 0)", s.Text);
    }

    [Fact]
    public void Wins_count_excludes_protected_mods_and_mods_an_earlier_rule_won_and_waits_for_the_debounce()
    {
        var e = Editor(("name contains 'boots' -> A", true), ("name contains 'boots' -> B", true));
        Observe(e, 1, Row("1", "Red Boots"), Row("2", "Blue Boots"), Row("3", "Prot Boots", prot: true));

        Assert.Equal("Counting...", e.Status(0).Text);
        _now += 299;
        e.Tick();
        Assert.Equal("Counting...", e.Status(0).Text);
        _now += 2;
        e.Tick();
        Assert.False(e.Status(0).IsError);
        Assert.Equal("Wins for 2 mods", e.Status(0).Text);
        Assert.Equal("Wins for 0 mods", e.Status(1).Text);
    }

    [Fact]
    public void A_change_in_any_input_restarts_the_count_and_a_stale_result_is_not_published()
    {
        var e = Editor(("name contains 'boots' -> A", true));
        Observe(e, 1, Row("1", "Boots"));
        _now += 400; e.Tick();
        Assert.Equal("Wins for 1 mods", e.Status(0).Text);

        e.SetText(0, "name contains 'zzz' -> A");
        Assert.Equal("Counting...", e.Status(0).Text);
        _now += 400; e.Tick();
        Assert.Equal("Wins for 0 mods", e.Status(0).Text);

        // a rescan (new generation) invalidates the count again
        Observe(e, 2, Row("1", "Boots"), Row("2", "zzz"));
        Assert.Equal("Counting...", e.Status(0).Text);
        _now += 400; e.Tick();
        Assert.Equal("Wins for 1 mods", e.Status(0).Text);
    }

    [Fact]
    public void Observing_every_frame_with_equal_but_separately_created_delegates_still_completes_the_debounce()
    {
        var e = Editor(("name contains 'boots' -> A", true));
        var rows = new[] { Row("1", "Boots") };

        // Two method-group conversions of the same instance method are two delegate objects that are Equal.
        Func<string, string> first = CanonInstance;
        Func<string, string> second = CanonInstance;
        Assert.NotSame(first, second);
        Assert.Equal(first, second);

        e.Observe(rows, 1, first, RaceSplitOptions.Off, null, false, false);
        _now += 200;
        e.Observe(rows, 1, second, RaceSplitOptions.Off, null, false, false);
        _now += 200;
        e.Observe(rows, 1, first, RaceSplitOptions.Off, null, false, false);
        e.Tick();
        Assert.Equal("Wins for 1 mods", e.Status(0).Text);
    }

    [Fact]
    public void Equal_body_priorities_do_not_restart_the_count_but_a_reordered_one_does()
    {
        var e = Editor(("name contains 'boots' -> A", true));
        var rows = new[] { Row("1", "Boots") };
        e.Observe(rows, 1, Canon, RaceSplitOptions.Off, BodyPriority.Default(["A", "B"]), true, false);
        _now += 400; e.Tick();
        Assert.Equal("Wins for 1 mods", e.Status(0).Text);

        e.Observe(rows, 1, Canon, RaceSplitOptions.Off, BodyPriority.Default(["A", "B"]), true, false);
        Assert.Equal("Wins for 1 mods", e.Status(0).Text);

        e.Observe(rows, 1, Canon, RaceSplitOptions.Off, BodyPriority.Default(["B", "A"]), true, false);
        Assert.Equal("Counting...", e.Status(0).Text);
    }

    [Fact]
    public void Disabled_rules_show_off_and_do_not_win()
    {
        var e = Editor(("name contains 'boots' -> A", false), ("name contains 'boots' -> B", true));
        Observe(e, 1, Row("1", "Boots"));
        _now += 400; e.Tick();
        Assert.Equal("Off", e.Status(0).Text);
        Assert.Equal("Wins for 1 mods", e.Status(1).Text);
    }

    [Fact]
    public void A_rule_whose_folder_would_not_satisfy_its_own_folder_condition_warns()
    {
        var warns = Editor(("folder starts with 'Old' -> New", true));
        Assert.Equal(
            "This rule's folder no longer matches its own folder condition, so the next Sort may file the mod somewhere else.",
            warns.Warning(0));
        var fine = Editor(("folder starts with 'Old' -> Old/Moved", true));
        Assert.Null(fine.Warning(0));
    }

    [Fact]
    public void No_folder_condition_or_a_token_in_the_folder_gives_no_folder_warning()
    {
        Assert.Null(Editor(("name = 'a' -> New", true)).Warning(0));
        Assert.Null(Editor(("folder starts with 'Old' -> {type}", true)).Warning(0));
        Assert.Null(Editor(("nmae = 'a' -> New", true)).Warning(0));
    }

    [Fact]
    public void Literal_folder_under_a_protected_folder_warns()
    {
        var e = Editor(("name = 'a' -> Keep/Sub", true));
        Assert.NotNull(e.ProtectionWarning(0, f => f.StartsWith("Keep", StringComparison.OrdinalIgnoreCase)));
        Assert.Null(e.ProtectionWarning(0, _ => false));
    }

    [Fact]
    public void Protection_warning_skips_token_folders_and_broken_rules()
    {
        Assert.Null(Editor(("name = 'a' -> Keep/{creator}", true)).ProtectionWarning(0, _ => true));
        Assert.Null(Editor(("nmae = 'a' -> Keep", true)).ProtectionWarning(0, _ => true));
    }
}
