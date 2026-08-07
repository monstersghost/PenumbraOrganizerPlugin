using System.Reflection;

namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>
/// Every help topic the code refers to. Pieces 3, 4 and 5 add to this; piece 2 only needs the
/// sort controls.
/// </summary>
public static class HelpTopics
{
    public static readonly HelpTopic SortGrouping = new("sort.grouping");
    public static readonly HelpTopic SortSplitGear = new("sort.split-gear");
    public static readonly HelpTopic SortSplitNpc = new("sort.split-npc");
    public static readonly HelpTopic SortButton = new("sort.button");
    public static readonly HelpTopic SortScrapedNpcList = new("sort.scraped-npc-list");
    public static readonly HelpTopic SortImportWorkbook = new("sort.import-workbook");

    /// <summary>
    /// Every constant declared above, found by reflection rather than repeated in a hand-written
    /// array. The tests assert properties of "all topics", and a hand-maintained list would let a
    /// new constant be added without ever being checked - which is the exact failure the tests
    /// exist to catch.
    /// </summary>
    public static IReadOnlyList<HelpTopic> All { get; } = typeof(HelpTopics)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(HelpTopic))
        .Select(f => (HelpTopic)f.GetValue(null)!)
        .ToList();
}
