using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Rules;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class OrganizerStateRulesTests
{
    private static string Canon(string s) => s;

    private static OrganizerModRow Row(string id, string name, string author = "Ann", string path = "",
        ModCategory? category = ModCategory.Gear, string? sub = "Feet", bool prot = false, ProteusRole role = ProteusRole.None) =>
        new()
        {
            Identifier = id, Name = name, Author = author,
            CurrentPath = path.Length == 0 ? name : path, ProposedPath = path.Length == 0 ? name : path,
            Category = category, SubCategory = sub, Protected = prot, ProteusRole = role,
        };

    private static OrganizerState State(params OrganizerModRow[] rows) => State(ToolPolicy.Default, rows);

    private static OrganizerState State(ToolPolicy policy, params OrganizerModRow[] rows)
    {
        var state = new OrganizerState();
        state.SetToolPolicy(policy);
        state.LoadScan(rows, rows.Where(r => r.Protected).Select(r => r.Identifier).ToHashSet());
        foreach (var row in rows.Where(r => r.Protected))
            Assert.True(state.Mods.Single(m => m.Identifier == row.Identifier).Protected);
        return state;
    }

    private static RuleSet Set(params (string, bool)[] rules) =>
        RuleSet.Compile(rules.Select(r => new RuleEntry(r.Item1, r.Item2)).ToList(), RaceSplitOptions.Off);

    private static LayoutOptions Rules(params (string, bool)[] rules) => new(Rules: Set(rules));

    private static int Sort(OrganizerState s, LayoutOptions? options, SortScope? scope = null,
        SortStrategy strategy = SortStrategy.TypeThenCreator) =>
        s.Sort(strategy, true, true, false, Canon, options: options, scope: scope);

    private static string PathOf(OrganizerState s, string id) => s.Mods.Single(m => m.Identifier == id).ProposedPath;

    [Fact]
    public void Rule_replaces_the_whole_path_and_names_the_rule()
    {
        var state = State(Row("a", "Hrothgar Boots"));
        Sort(state, Rules(("name contains 'hrothgar' -> Special/{creator}", true)));
        var row = state.Mods.Single();
        Assert.Equal("Special/Ann/Hrothgar Boots", row.ProposedPath);
        Assert.Equal(new StagedRule(1, "name contains 'hrothgar' -> Special/{creator}"), row.StagedRule);
        Assert.Null(row.StagedRace);
        Assert.Null(row.StagedBodyResolution);
    }

    [Fact]
    public void No_match_sorts_normally_and_empty_rules_equal_no_rules()
    {
        var plain = State(Row("a", "Boots"));
        Sort(plain, null);
        var withEmpty = State(Row("a", "Boots"));
        Sort(withEmpty, new LayoutOptions(Rules: RuleSet.Empty));
        var noMatch = State(Row("a", "Boots"));
        Sort(noMatch, Rules(("name = 'zzz' -> X", true)));

        Assert.Equal(plain.Mods.Single().ProposedPath, withEmpty.Mods.Single().ProposedPath);
        Assert.Equal(plain.Mods.Single().ProposedPath, noMatch.Mods.Single().ProposedPath);
        Assert.Null(noMatch.Mods.Single().StagedRule);
        Assert.Null(withEmpty.LastSortSummary!.Rules);
    }

    [Fact]
    public void Protected_mods_are_never_touched()
    {
        var state = State(Row("a", "Hrothgar Boots", prot: true));
        Sort(state, Rules(("name contains 'hrothgar' -> X", true)));
        Assert.Equal("Hrothgar Boots", state.Mods.Single().ProposedPath);
        Assert.Null(state.Mods.Single().StagedRule);
    }

    [Fact]
    public void Invalid_expansion_consumes_the_mod_and_leaves_it_in_place_even_if_a_later_rule_matches()
    {
        var state = State(Row("a", "Boots", author: ""));
        Sort(state, Rules(("name = 'boots' -> {creator}", true), ("name = 'boots' -> Later", true)));
        var row = state.Mods.Single();
        Assert.Equal("Boots", row.ProposedPath);
        Assert.Null(row.StagedRule);
        var summary = state.LastSortSummary!.Rules!;
        Assert.Single(summary.Unplaced);
        Assert.Contains("Rule 1", summary.Unplaced[0]);
    }

    [Fact]
    public void Broken_rules_are_skipped_and_listed()
    {
        var state = State(Row("a", "Boots"));
        Sort(state, Rules(("nmae = 'x' -> A", true), ("name = 'boots' -> B", true)));
        Assert.Equal("B/Boots", state.Mods.Single().ProposedPath);
        var summary = state.LastSortSummary!.Rules!;
        Assert.Equal(1, summary.Applied);
        Assert.Single(summary.SkippedRules);
        Assert.Contains("Rule 1", summary.SkippedRules[0]);
        Assert.Contains("Did you mean 'name'", summary.SkippedRules[0]);
    }

    [Fact]
    public void Ruled_rows_leave_no_gaps_in_the_numbered_gear_layout()
    {
        var state = State(
            new OrganizerModRow { Identifier = "r", Name = "Ruled", Author = "A", CurrentPath = "Ruled", ProposedPath = "Ruled",
                Category = ModCategory.Gear, SubCategory = "Top", BodyType = "Bibo+" },
            new OrganizerModRow { Identifier = "k", Name = "Kept", Author = "A", CurrentPath = "Kept", ProposedPath = "Kept",
                Category = ModCategory.Gear, SubCategory = "Top", BodyType = "Gen3" });
        var options = Rules(("name = 'ruled' -> Out", true));
        state.Sort(SortStrategy.TypeOnly, true, true, false, Canon, splitGearBody: true, options: options);
        Assert.StartsWith("Gear/10. Gen3/", PathOf(state, "k"));
        Assert.Equal("Out/Ruled", PathOf(state, "r"));
    }

    [Fact]
    public void Ruled_rows_leave_no_gaps_in_the_numbered_body_layout()
    {
        OrganizerModRow Body(string id, string body) => new()
        {
            Identifier = id, Name = id, Author = "A", CurrentPath = id, ProposedPath = id,
            Category = ModCategory.Body, SubCategory = "Skin", BodyType = body,
        };
        var withRule = State(Body("Ruled", "Bibo+"), Body("Kept", "Gen3"));
        withRule.Sort(SortStrategy.TypeOnly, true, true, false, Canon, splitBodyMods: true,
            options: Rules(("name = 'ruled' -> Out", true)));
        var without = State(Body("Kept", "Gen3"));
        without.Sort(SortStrategy.TypeOnly, true, true, false, Canon, splitBodyMods: true);

        Assert.Equal("Out/Ruled", PathOf(withRule, "Ruled"));
        Assert.Equal(PathOf(without, "Kept"), PathOf(withRule, "Kept"));
    }

    [Fact]
    public void Scoped_sort_applies_rules_only_inside_the_scope()
    {
        var state = State(Row("a", "Boots", path: "Folder/Boots"), Row("b", "Boots Two"));
        Sort(state, Rules(("name contains 'boots' -> R", true)), new SortScope(RootOnly: true));
        Assert.Equal("Folder/Boots", PathOf(state, "a"));
        Assert.Equal("R/Boots Two", PathOf(state, "b"));
        Assert.Null(state.Mods.Single(m => m.Identifier == "a").StagedRule);
    }

    [Fact]
    public void Root_only_run_applies_rules_to_root_mods()
    {
        var state = State(Row("b", "Boots Two"));
        Sort(state, Rules(("name contains 'boots' -> R", true)), new SortScope(RootOnly: true));
        Assert.Equal("R/Boots Two", PathOf(state, "b"));
    }

    [Fact]
    public void Sorting_twice_is_stable_for_a_folder_rule()
    {
        var state = State(Row("a", "Boots", path: "Old/Boots"));
        var options = Rules(("folder starts with 'Old' -> New", true), ("folder starts with 'New' -> New", true));
        Sort(state, options);
        var first = PathOf(state, "a");
        Assert.Equal("New/Boots", first);

        // Simulate Apply plus a rescan: the mod now lives at New/Boots.
        var again = State(Row("a", "Boots", path: first));
        var noRule = State(Row("a", "Boots", path: first));
        Sort(noRule, null);
        // The second rule matches the moved mod and files it into the same folder: unchanged across two sorts.
        Sort(again, options);
        Assert.Equal(first, PathOf(again, "a"));
        Assert.Equal(2, again.Mods.Single().StagedRule!.Ordinal);

        // With only the first rule, the moved mod no longer matches and the normal sort decides.
        var firstOnly = State(Row("a", "Boots", path: first));
        Sort(firstOnly, Rules(("folder starts with 'Old' -> New", true)));
        Assert.Null(firstOnly.Mods.Single().StagedRule);
        Assert.Equal(PathOf(noRule, "a"), PathOf(firstOnly, "a"));
        Assert.NotEqual("Old/Boots", PathOf(firstOnly, "a"));
    }

    [Fact]
    public void Collision_with_a_protected_mod_is_renamed_for_a_ruled_row()
    {
        var state = State(
            Row("p", "Boots", path: "R/Boots", prot: true),
            Row("a", "Boots", path: "Loose/Boots"));
        Sort(state, Rules(("name = 'boots' -> R", true)));
        Assert.Equal("R/Boots (2)", PathOf(state, "a"));
    }

    [Fact]
    public void StagedRule_is_cleared_by_manual_assignment_and_by_a_later_sort_without_a_match()
    {
        var state = State(Row("a", "Boots"));
        Sort(state, Rules(("name = 'boots' -> R", true)));
        Assert.NotNull(state.Mods.Single().StagedRule);
        state.AssignManual("a", "Manual/Boots");
        Assert.Null(state.Mods.Single().StagedRule);

        Sort(state, Rules(("name = 'boots' -> R", true)));
        Assert.NotNull(state.Mods.Single().StagedRule);
        Sort(state, null);
        Assert.Null(state.Mods.Single().StagedRule);
    }

    [Fact]
    public void StagedRule_is_cleared_by_stage_move()
    {
        var state = State(Row("a", "Boots"));
        Sort(state, Rules(("name = 'boots' -> R", true)));
        state.StageMoveUnder(["a"], "Unused");
        Assert.Null(state.Mods.Single().StagedRule);
    }

    [Fact]
    public void StagedRule_is_cleared_by_a_rescan()
    {
        var row = Row("a", "Boots");
        var state = State(row);
        Sort(state, Rules(("name = 'boots' -> R", true)));
        Assert.NotNull(row.StagedRule);
        state.LoadScan([row], new HashSet<string>());
        Assert.Null(row.StagedRule);
    }

    [Fact]
    public void A_rule_wins_under_CreatorOnly()
    {
        var state = State(Row("a", "Boots"));
        Sort(state, Rules(("name = 'boots' -> R", true)), strategy: SortStrategy.CreatorOnly);
        Assert.Equal("R/Boots", PathOf(state, "a"));
    }

    [Fact]
    public void A_sort_inside_anchored_row_gets_the_anchor_then_the_rule_folder()
    {
        var state = State(ToolPolicy.From(0, 0, false, [".chars"]),
            Row("a", "Boots", path: ".Chars/Old/Boots"));
        Sort(state, Rules(("name = 'boots' -> R", true)));
        Assert.Equal(".Chars/R/Boots", PathOf(state, "a"));
    }

    [Fact]
    public void A_proteus_managed_row_at_root_is_left_alone_and_gets_no_rule()
    {
        var row = Row("pm", "Boots", path: "Proteus/Boots", role: ProteusRole.Managed);
        var state = State(new ToolPolicy(ToolMode.Normal, ToolMode.Normal, true), row);
        Sort(state, Rules(("name = 'boots' -> R", true)));
        Assert.Equal("Boots", PathOf(state, "pm"));
        Assert.Null(state.Mods.Single().StagedRule);
        Assert.Equal(0, state.LastSortSummary!.Rules!.Applied);
    }

    [Fact]
    public void Unplaced_entries_are_kept_in_full_on_the_summary_but_the_text_shows_three()
    {
        var rows = Enumerable.Range(0, 5).Select(i => Row($"m{i}", $"Boots {i}", author: "")).ToArray();
        var state = State(rows);
        Sort(state, Rules(("name contains 'boots' -> {creator}", true)));
        Assert.Equal(5, state.LastSortSummary!.Rules!.Unplaced.Count);
        var text = SortSummaryText.Describe(state.LastSortSummary!);
        Assert.Contains("and 2 more", text);
    }

    [Fact]
    public void Diagnostics_list_every_unplaced_entry_and_the_staged_rule()
    {
        var rows = Enumerable.Range(0, 5).Select(i => Row($"m{i}", $"Boots {i}", author: "")).ToList();
        rows.Add(Row("ok", "Shoes"));
        var state = State(rows.ToArray());
        Sort(state, Rules(("name contains 'boots' -> {creator}", true), ("name = 'shoes' -> S", true)));
        var text = OrganizerExportFormatter.Format(state.Mods.ToList(), state.Validate(), null, state.LastSortSummary!.Rules);
        Assert.Contains("RulesUnplaced:", text);
        Assert.Contains("Boots 4", text);
        Assert.Contains("StagedRule: #2 name = 'shoes' -> S", text);
    }
}
