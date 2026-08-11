using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using PenumbraOrganizer.Core.Services;
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.LibrarySearch;

namespace PenumbraOrganizer.Plugin.Windows;

public sealed partial class MainWindow
{
    private void DrawSearchTab()
    {
        using var tab = ImRaii.TabItem("Search");
        if (!tab)
            return;

        ImGui.TextWrapped(
            "Since Penumbra 1.7, its own Mods tab supports a native search syntax (c:[item], t:[tag], "
            + "a:[author], etc.) that covers much of what this tab does. This tab stays available for "
            + "now - it may be retired later if Penumbra's own filtering fully supersedes it.");
        ImGui.Spacing();

        var gates = CurrentGates();
        var indexState = _plugin.IndexWork.State;
        using (PluginTheme.PrimaryButton())
        {
            ImGui.BeginDisabled(!gates.CanIndex);
            if (ImGui.Button("Build/Refresh Index"))
                BuildChangedItemIndex();
            ImGui.EndDisabled();
        }
        Help.Tooltip(HelpTopics.SearchBuildIndex, gates.CanIndex ? null : ActivityGateReason);

        DrawLibraryWorkProgress(indexState, _plugin.IndexWork.RequestCancellation);
        DrawLibraryWorkOutcome(indexState);

        if (_plugin.LibraryIndexError is { } error)
            ImGui.TextColored(PluginTheme.CollisionBad, error);

        if (_plugin.LibraryIndex is not { } index)
        {
            ImGui.TextUnformatted("Click Build/Refresh Index to search your mod library.");
            return;
        }

        ImGui.TextWrapped(ChangedItemIndexSummary.Describe(index));
        ImGui.Text($"Index built at {index.BuiltAt:HH:mm:ss}");
        ImGui.Spacing();

        ImGui.InputText("Mod name contains", ref _librarySearchNameQuery, 256);
        Help.Tooltip(HelpTopics.SearchNameFilter);
        ImGui.InputText("Item contains", ref _librarySearchItemQuery, 256);
        Help.Tooltip(HelpTopics.SearchItemFilter);
        ImGui.Spacing();

        ImGui.TextUnformatted("Categories:");
        Help.Tooltip(HelpTopics.SearchCategories);
        var categoryToggles = SearchableCategories
            .Select(category => ($"{category}##search-category-{category}", _librarySearchCategories.Contains(category), (Action<bool>)(isChecked =>
            {
                if (isChecked)
                    _librarySearchCategories.Add(category);
                else
                    _librarySearchCategories.Remove(category);
            })))
            .Append(("Unknown##search-category-unknown", _librarySearchIncludeUnknown, isChecked => _librarySearchIncludeUnknown = isChecked))
            .ToList();
        DrawWrappingCheckboxRow(categoryToggles);

        if (_librarySearchCategories.Contains(ModCategory.Gear))
        {
            ImGui.Spacing();
            ImGui.TextUnformatted("Slots:");
            Help.Tooltip(HelpTopics.SearchSlots);
            var slotToggles = Enum.GetValues<EquipmentSlot>()
                .Select(slot => ($"{SlotLabel(slot)}##search-slot-{slot}", _librarySearchSlots.Contains(slot), (Action<bool>)(isChecked =>
                {
                    if (isChecked)
                        _librarySearchSlots.Add(slot);
                    else
                        _librarySearchSlots.Remove(slot);
                })))
                .Append(("Unresolved##slot-unresolved", _librarySearchIncludeUnresolved, isChecked => _librarySearchIncludeUnresolved = isChecked))
                .ToList();
            DrawWrappingCheckboxRow(slotToggles);
        }

        ImGui.Spacing();

        var filter = new LibrarySearchFilter(
            _librarySearchCategories, _librarySearchIncludeUnknown,
            _librarySearchSlots, _librarySearchIncludeUnresolved,
            _librarySearchNameQuery, _librarySearchItemQuery);

        var matches = index.Mods.Where(mod => LibrarySearchEngine.Matches(mod, filter)).ToList();

        // Same flag combination as PathTreeView.cs (the only other table in this codebase) --
        // Resizable | SizingStretchProp, no per-column width flags, for proportional stretch.
        using var columns = ImRaii.Table("SearchColumns", 2,
            ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp, new Vector2(0, 420));
        if (!columns)
            return;

        ImGui.TableSetupColumn("Mods");
        ImGui.TableSetupColumn("Changed items");
        ImGui.TableNextRow();
        ImGui.TableNextColumn();

        using (var left = ImRaii.Child("SearchModList", new Vector2(0, 400), border: true))
        {
            if (left)
            {
                if (matches.Count == 0)
                {
                    ImGui.TextUnformatted("No mods found.");
                }
                else
                {
                    foreach (var mod in matches)
                    {
                        var isSelected = mod.Identifier == _librarySearchSelectedModIdentifier;
                        if (ImGui.Selectable($"{mod.Name} ({mod.Author})##search-{mod.Identifier}", isSelected))
                            _librarySearchSelectedModIdentifier = mod.Identifier;
                    }
                }
            }
        }

        ImGui.TableNextColumn();
        using (var right = ImRaii.Child("SearchItemList", new Vector2(0, 400), border: true))
        {
            if (right)
            {
                var selectedMod = matches.FirstOrDefault(m => m.Identifier == _librarySearchSelectedModIdentifier);
                if (selectedMod is null)
                {
                    ImGui.TextUnformatted("Select a mod to see its changed items.");
                }
                else
                {
                    var (items, matchedByNameOnly) = LibrarySearchEngine.DisplayedItems(selectedMod, filter);
                    if (matchedByNameOnly)
                        ImGui.TextColored(PluginTheme.CollisionBad, "Matched by mod name, not by item.");
                    foreach (var item in items)
                        ImGui.TextUnformatted(item.Key);
                }
            }
        }
    }
}
