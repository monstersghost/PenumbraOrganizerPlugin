using PenumbraOrganizer.Plugin.Organizer.BodyTypes;
using PenumbraOrganizer.Plugin.Organizer.Workbook;

namespace PenumbraOrganizer.Plugin.Organizer.Rules;

public sealed class RuleRow
{
    public string Text { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

public sealed record RuleStatus(bool IsError, string Text);

/// <summary>
/// The Sort tab's rules list as plain state: edits, per-rule status and a debounced "Wins for N mods"
/// count. No ImGui, so it is tested directly. The count is recomputed only after text, order, enabled
/// flags, scan generation, race options, body priority or the creator canonicalizer change AND 300 ms
/// have passed, never per frame. It compiles the live (possibly unsaved) text itself, on purpose: the
/// shared <see cref="RuleSetCache"/> only knows the saved list.
/// </summary>
public sealed class RulesEditor
{
    public const int DebounceMs = 300;

    private readonly List<RuleRow> _rows;
    private readonly Func<long> _clock;
    private readonly Dictionary<string, RuleParseResult> _parseCache = new(StringComparer.Ordinal);

    private IReadOnlyList<OrganizerModRow> _mods = [];
    private int _generation;
    private Func<string, string> _canonicalize = s => s;
    private RaceSplitOptions _race = RaceSplitOptions.Off;
    private BodyPriority? _priority;
    private bool _splitGearBody, _splitBodyMods;

    private long _inputsVersion;
    private long _countedVersion = -1;
    private long _dueAt;
    private int[] _wins = [];

    public RulesEditor(IReadOnlyList<SortRuleEntry> saved, Func<long> clockMs)
    {
        _clock = clockMs;
        _rows = saved.Select(s => new RuleRow { Text = s.Text, Enabled = s.Enabled }).ToList();
        Touch();
    }

    public IReadOnlyList<RuleRow> Rows => _rows;
    public bool CanAdd => _rows.Count < RuleSet.MaxRules;

    public List<SortRuleEntry> ToConfig() =>
        _rows.Select(r => new SortRuleEntry { Text = r.Text, Enabled = r.Enabled }).ToList();

    public bool Add()
    {
        if (!CanAdd) return false;
        _rows.Add(new RuleRow());
        Touch();
        return true;
    }

    public void Remove(int index)
    {
        if (index < 0 || index >= _rows.Count) return;
        _rows.RemoveAt(index);
        Touch();
    }

    public bool MoveUp(int index) => Move(index, index - 1);
    public bool MoveDown(int index) => Move(index, index + 1);

    private bool Move(int from, int to)
    {
        if (from < 0 || from >= _rows.Count || to < 0 || to >= _rows.Count) return false;
        (_rows[from], _rows[to]) = (_rows[to], _rows[from]);
        Touch();
        return true;
    }

    public void SetText(int index, string text)
    {
        if (index < 0 || index >= _rows.Count || _rows[index].Text == text) return;
        _rows[index].Text = text;
        Touch();
    }

    public void SetEnabled(int index, bool enabled)
    {
        if (index < 0 || index >= _rows.Count || _rows[index].Enabled == enabled) return;
        _rows[index].Enabled = enabled;
        Touch();
    }

    /// <summary>Tells the editor what the count depends on; a change in any of it restarts the debounce.</summary>
    public void Observe(
        IReadOnlyList<OrganizerModRow> rows, int scanGeneration, Func<string, string> canonicalize,
        RaceSplitOptions race, BodyPriority? priority, bool splitGearBody, bool splitBodyMods)
    {
        // Delegate value equality: a method group converted each frame is a new object but Equal.
        var changed = scanGeneration != _generation
            || rows.Count != _mods.Count
            || !race.Equals(_race)
            || !SamePriority(priority, _priority)
            || splitGearBody != _splitGearBody || splitBodyMods != _splitBodyMods
            || !Equals(canonicalize, _canonicalize);

        _mods = rows;
        _generation = scanGeneration;
        _canonicalize = canonicalize;
        _race = race;
        _priority = priority;
        _splitGearBody = splitGearBody;
        _splitBodyMods = splitBodyMods;
        if (changed) Touch();
    }

    private static bool SamePriority(BodyPriority? a, BodyPriority? b) =>
        ReferenceEquals(a, b)
        || (a is not null && b is not null && a.Labels.SequenceEqual(b.Labels, StringComparer.Ordinal));

    private void Touch()
    {
        _inputsVersion++;
        _dueAt = _clock() + DebounceMs;
    }

    /// <summary>Publishes the count once the debounce has passed. Cheap when nothing changed.</summary>
    public void Tick()
    {
        if (_countedVersion == _inputsVersion || _clock() < _dueAt) return;

        var version = _inputsVersion;
        var set = RuleSet.Compile(_rows.Select(r => new RuleEntry(r.Text, r.Enabled)).ToList(), _race);
        var wins = new int[_rows.Count];
        foreach (var row in _mods)
        {
            if (row.Protected) continue;
            var body = RuleFields.BodyLabel(row, _priority, _splitGearBody, _splitBodyMods);
            var values = RuleFields.For(row, _canonicalize, _race, body);
            if (set.Match(values) is { } match)
                wins[match.Rule.Ordinal - 1]++;
        }

        if (version != _inputsVersion) return;
        _wins = wins;
        _countedVersion = version;
    }

    public RuleStatus Status(int index)
    {
        var row = _rows[index];
        var parsed = Parse(row.Text);
        if (parsed.Error is { } error)
            return new RuleStatus(true, $"{error.Message} (at {error.Position})");
        if (!row.Enabled)
            return new RuleStatus(false, "Off");
        if (_countedVersion != _inputsVersion || index >= _wins.Length)
            return new RuleStatus(false, "Counting...");
        return new RuleStatus(false, $"Wins for {_wins[index]} mods");
    }

    /// <summary>
    /// Non-null when the rule tests `folder`, its folder has no tokens, and that literal folder would NOT
    /// satisfy the same test: the mod leaves the condition once moved, so the next Sort may re-file it.
    /// </summary>
    public string? Warning(int index)
    {
        if (Parse(_rows[index].Text).Rule is not { } rule || !MentionsFolder(rule.Condition)) return null;
        var folder = DestinationResolver.NormalizePath(rule.FolderTemplate);
        if (folder.Contains('{')) return null;

        var values = new RuleFieldValues(string.Empty, string.Empty, folder, string.Empty, string.Empty, string.Empty, string.Empty);
        return RuleEvaluator.Matches(rule.Condition, values)
            ? null
            : "This rule's folder no longer matches its own folder condition, so the next Sort may file the mod somewhere else.";
    }

    /// <summary>
    /// Non-null when the rule's literal folder (tokens unexpanded, so token folders are not checked) sits
    /// under a protected folder, where the mods it files would be hard to move again.
    /// </summary>
    public string? ProtectionWarning(int index, Func<string, bool> isProtectedFolder)
    {
        if (Parse(_rows[index].Text).Rule is not { } rule) return null;
        var folder = DestinationResolver.NormalizePath(rule.FolderTemplate);
        if (folder.Length == 0 || folder.Contains('{')) return null;
        return isProtectedFolder(folder)
            ? "This rule's folder is inside a protected folder, so mods filed there will be treated as protected."
            : null;
    }

    private static bool MentionsFolder(RuleExpr e) => e switch
    {
        RuleCompare c => c.Field == RuleField.Folder,
        RuleNot n => MentionsFolder(n.Inner),
        RuleAnd a => MentionsFolder(a.Left) || MentionsFolder(a.Right),
        RuleOr o => MentionsFolder(o.Left) || MentionsFolder(o.Right),
        _ => false,
    };

    private RuleParseResult Parse(string text)
    {
        if (!_parseCache.TryGetValue(text, out var result))
        {
            if (_parseCache.Count > 512) _parseCache.Clear();
            result = RuleParser.Parse(text);
            _parseCache[text] = result;
        }

        return result;
    }
}
