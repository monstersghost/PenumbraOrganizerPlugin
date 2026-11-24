# Help tab and in-plugin text audit (2026-11-25)

Branch `fix/help-audit`, based on main 75d3fc3. Audits everything the user can read in the plugin against the code
as of that commit: `Resources/help-content.json` (115 topics before the audit), `HelpTopics.cs`,
`HelpTopicUsage.cs`, `HelpTab.cs` (section membership and order), the first-run walkthrough, the new-mod popup,
confirm window and chat lines (`NewModPromptText.cs`), the Sort tab's static strings (`SortPanel*.cs`,
`CategoryLayoutEditor.cs`, `ToolModeStatusText.cs`), Review, Protect, Scan, History, Search and Templates tab
strings, `WorkbookExportGate.cs` and the `/porganizer` help text in `Plugin.cs`.

Verdicts: OK = true as written; STALE = was true before a recent change; WRONG = never true or now misleading;
MISSING = a visible control with no help topic.

## help-content.json

| Topic | What it said (gist) | Verdict | Fix |
|---|---|---|---|
| firstrun.welcome | 8 steps, outlines what to press | OK | - |
| firstrun.scan | Press Refresh mod list | OK | - |
| firstrun.protect | "tick any mod or folder" | STALE | Folders are a protect/exclude/inherit box now, not a tick: "tick any mod ..., or click a folder's box" |
| firstrun.sort | Recommended or Advanced, press Sort | OK | - |
| firstrun.review | collisions, Protect & Skip, workbook | OK | - |
| firstrun.apply | Apply, backup, Show failed mods | OK | - |
| firstrun.history | Restore | OK | - |
| firstrun.help | hover any control, Help tab | OK | - |
| firstrun.penumbra-unavailable | Penumbra not loaded | OK | - |
| help.what-this-does | "only changes anything when you press Apply" | WRONG | Auto sort's Sort automatically applies new mods without Apply; says so |
| help.safety-rules | list of everything that changes the library | WRONG | List lacked the Confirm sort window's Apply and Sort automatically; "Sorting never moves anything" narrowed to pressing Sort |
| help.tab-scan | Unused mods paragraph first, then Refresh | STALE | Reordered (Unused last), mentions Only my active collection and that nothing moves until Apply |
| help.tab-protect | "three places: checkbox, folder, Heliosphere"; no Proteus, no Sort inside folders | STALE | Four sources incl. Proteus, the notes, untick-frees-until-next-Scan, dropdowns, Sort tab status line, Sort inside folders |
| help.tab-sort | bar, Recommended, Advanced, rules, Auto sort | STALE | Adds the Heliosphere/Proteus status line, Show in Advanced, fixed split order (no reorder), Reset all, Select all splits, exactly which panels sit under which category (Hair panel, Race card, Body types card with Body priority and Check my collection), Show rules |
| help.tab-review | errors, folder cleanup | STALE | Adds the Workbook group (Export mode, Import Workbook) and the Diagnostics section |
| help.tab-history | Restore moves Heliosphere-managed mods | STALE | Heliosphere- or Proteus-managed |
| help.tab-search | Penumbra 1.7 native search | OK | - |
| help.tab-templates | template import/export | OK | - |
| help.when-things-go-wrong | "Create Diagnostic Dump on Review Changes" | STALE | It is inside the collapsed Diagnostics section now |
| help.where-your-files-are | "Show Config File on Review Changes" | STALE | Same: under Diagnostics |
| scan.refresh-mod-list | | OK | - |
| scan.event-log | | OK | - |
| protect.toggle-all | | OK | - |
| protect.heliosphere-mode | three modes, saved | OK | - |
| protect.sort-inside-folders | | OK | - |
| protect.proteus-mode | | OK | - |
| protect.proteus-managed-root | | OK | - |
| protect.proteus-note | | OK | - |
| protect.folders | cycle protect, exclude, inherit | OK | - |
| protect.folder-unprotect-contents | "... this folder or Heliosphere alike" | STALE | Proteus mods are freed the same way (IsToolManaged) |
| protect.folder-rule-cycle | | OK | - |
| protect.mods | tool/folder protected mods "have no checkbox here" | WRONG | Every mod has a checkbox; unticking frees a tool mod until the next Scan, a folder-protected mod follows its folder |
| protect.heliosphere-note | "the plugin never proposes moving it" | WRONG | Only while Heliosphere mods are Protected; untick or the dropdown frees it |
| protect.stranded-at-root | Heliosphere mods "protected on sight" | STALE | Only while Heliosphere mods are set to Protected |
| sort.grouping | | OK | - |
| sort.split-gear | | OK | - |
| sort.shared-hands-feet | | OK | - |
| sort.split-npc | | OK | - |
| sort.mode | Advanced changes folder, creator folder, splits | STALE | Adds that Rules are edited in Advanced and apply in both |
| sort.category-list | | OK | - |
| sort.category-folder | relative, no empty part, . or .., 260 chars | STALE | Also no { or } (LayoutFolderName.BracesError) |
| sort.creator-folder | | OK | - |
| sort.splits | order is fixed | OK | - |
| sort.example-path | | OK | - |
| sort.select-all | turns on every split + race | STALE | Asks first; includes Weapon by job (needs a rescan); Group by also stays |
| sort.reset-layout | | OK | - |
| sort.help-button | | OK | - |
| sort.hair-resolution | | OK | - |
| sort.split-hair | | OK | - |
| sort.split-hair-by-race | several races or none: Multi-race | STALE | With the Multi-race box off, several races go to the top-ranked race |
| sort.rules (short) | "Open this topic for the language" | WRONG | A tooltip cannot be opened; points to Sort rules on the Help tab |
| sort.rules (body) | see the rules section below | STALE/WRONG | see below |
| sort.race-split | | OK | - |
| sort.race-rows | | OK | - |
| sort.multi-race | | OK | - |
| sort.race-priority | | OK | - |
| sort.split-emotes | | OK | - |
| sort.split-weapon-job | | OK | - |
| sort.split-gear-body | | OK | - |
| sort.split-body-mods | "Leave it off if you keep one mod per body"; 00-02 only | STALE | It is on in Recommended; adds 03. Racial Scaling and the exact folder names |
| sort.first-choice-body | | OK | - |
| sort.check-collection | | OK | - |
| sort.body-priority | "Move a body with the arrows" | OK (minor) | Says the arrows are in Full priority order |
| sort.priority-race | | OK | - |
| sort.midlander-fallback | "the mod stays flat" | OK (minor) | "stays in Hair", the wording the other hair topics use |
| sort.button | "every unprotected mod" | STALE | Only the mods What to sort covers |
| sort.what-to-sort | not saved | OK | - |
| sort.scope-line | counts + protected root mods | STALE | The line also reports mods filed, skipped or left in place by rules |
| newmods.banner | Sort now / Review first / Not now | STALE | Review first stages the sort on Review Changes (it does not just open the tab). The Open Protect tab button is self-explanatory and the 200-character cap leaves no room for it |
| newmods.popup-dont-show | Sort > Auto sort | OK | - |
| newmods.confirm | | OK | - |
| sort.new-mod-action | more than 20 falls back to the popup | STALE | Also falls back while an unapplied sort or unprotect choice is waiting (NewModSortPipeline.Decide) |
| sort.show-new-mod-popup | | OK | - |
| unused.check | every collection | STALE | Only my active collection checkbox |
| unused.move | | OK | - |
| unused.protect-folder | | OK | - |
| sort.scraped-npc-list | "Turned off in this version" | STALE | The checkbox is not drawn at all in this build; says so plainly (topic kept, per the 2026-11-05 plan) |
| sort.import-workbook | | OK | - |
| sort.manual-assign | | OK | - |
| sort.refresh-npc-list | describes a button | STALE | No button is drawn in this build; says so plainly (topic kept, per the 2026-11-05 plan) |
| review.apply | | OK | - |
| review.export | | OK | - |
| review.show-config-file | | OK | - |
| review.diagnostic-dump | | OK | - |
| review.workbook-destinations | greyed "until you Sort" | STALE | It is the Export button that greys; also after a protection change or a root-only sort ("whole-library Sort") |
| review.diagnostics | "the mods the last Apply could not move" | WRONG | Apply or Restore (the Failed mods button reads both) |
| review.export-workbook | every mod and its destination | OK (minor) | Says the destination comes from Export mode |
| review.protect-and-skip | | OK | - |
| review.reread-organization-json | | OK | - |
| review.clean-up-folders | | OK | - |
| review.rollback-folder-cleanup | | OK | - |
| history.create-backup | | OK | - |
| history.restore | "protected and Heliosphere mods" | STALE | Proteus too |
| history.delete-snapshot | | OK | - |
| search.* (5 topics) | | OK | - |
| recovery.* (5 topics) | | OK | - |
| templates.export | | OK | - |
| templates.export-name | | OK | - |
| templates.export-fallback | "Same groupings and splits as the Sort tab" | WRONG | The export offers Group by plus five splits only (gear slot, gear body, NPC kind, hair number, body mods) |
| templates.export-folders / -search / -save / -share-code | | OK | - |
| help.tab-templates | | OK | - |
| templates.refresh-list / open-folder / import-file / preview / apply | | OK | - |
| (none) review.failed-mods | Failed mods button had an ad-hoc SetTooltip and no Help tab entry | MISSING | Added topic; the button now calls Help.Tooltip with the same text |

## sort.rules body, checked against Organizer/Rules/*.cs

| Statement | Verdict | Fix |
|---|---|---|
| Fields name, author, folder, type, subtype, race, body | OK | - |
| type list Gear ... Animation and VFX, or Review | OK but incomplete | Says it is the BUILT-IN name even after a rename in Advanced (WorkbookTokens.TypeFolderFor is called without a placement), that both Animation and VFX give "Animation and VFX", that unclassified gives Review, and that Weapon needs a scan with the Job split on |
| race: "what {race} gives" | OK but incomplete | Empty for mods race does not apply to; works with Split by race off (ResolveForToken) |
| author | OK | adds "empty when unknown" (KnownSegment) |
| Operators, `not` before an operator, and/or/not/brackets | OK | - |
| Precedence not > and > or | OK | Now also stated as "same as SQL" |
| "use Up and Down" | WRONG | The button reads Dn; also X deletes |
| 100 rules / 300 characters | OK | "folder included" (the limit is on the whole line) |
| '' escape, case-insensitive keywords and text | OK | - |
| Different from SQL list | Incomplete | Adds: double and curly quotes are rejected, no < or >, no IS, no comments, numbers compared as text |
| "An unknown token is an error" | WRONG | It is not flagged in the rules list (the parser does not read the folder); the mod stays where it is and the Sort summary names it, like an empty folder or a stray brace (DestinationResolver.ResolveRuleFolder) |
| {type} in the folder | MISSING | In the folder, {type} is the category's configured (renamed) folder, unlike the type field (OrganizerState.Sort passes the placement) |
| Rules edited in Advanced, Recommended shows the count | OK | Adds the Show rules button |
| Amber warnings | OK | Adds that folders using tokens are not checked |
| Applies to every Sort and auto sort, not template fallback | OK | Adds: not to a workbook import |
| "Rules are saved when you leave the text box" | OK but incomplete | Also saved at once on add, remove, move, tick |

## Strings outside help-content.json

| Location | What it said | Verdict | Fix |
|---|---|---|---|
| HelpTab.Sections, Sort section | Unused mods topics listed under Sort | WRONG | Unused mods is on the Scan tab: moved the three topics to the Scan section (HelpTopicUsage comment order too) |
| HelpTab.Sections, Review section | no Failed mods entry | MISSING | review.failed-mods added after Diagnostics |
| WorkbookExportGate | "Sort settings changed, press Sort again." | STALE | "Sort settings" was the old name of the Auto sort card, which this gate does not read: "Sort choices changed since the last sort, press Sort again." |
| WorkbookExportGate | "Staged paths changed since the last sort, press Sort again." | WRONG after a root-only sort | "Staged paths changed since the last whole-library sort, press Sort again." |
| MainWindow.ReviewTab.cs Failed mods | ImGui.SetTooltip literal | MISSING topic | Help.Tooltip(HelpTopics.ReviewFailedMods), same text |
| SortPanel / CategoryLayoutEditor / ToolModeStatusText strings | | OK | - |
| NewModPromptText (popup, notice, chat lines) | | OK | "Review tab" is the Review Changes tab; left as is (unambiguous) |
| NewModPromptText command-only lines (NothingPending, StagedWithoutForce, ConfirmOpened) | mention /porganizer sortnew and sortroot | OK | Reachable only through the chat commands, which are switched off (Plugin.OnCommand); not shown |
| Plugin.cs /porganizer HelpMessage and help line | toggles the window; subcommands off | OK | - |
| Templates tab intro, export screen texts | | OK | - |
| Protect tab static texts and popups | | OK | - |
| MainWindow.ReviewTab.cs orphaned-folder texts | contain U+2014 em dashes | not fixed | Listed below |

## Not fixed (UI doubts, listed for the author)

1. `MainWindow.ReviewTab.cs` folder-cleanup messages use literal em dashes (U+2014). HelpContentTests forbid them in
   help content because the font may not render them; these strings are outside that test. Not touched here (the
   repo's non-ASCII transcription hazard).
2. After a root-only Sort, "As suggested by Sort" stays unavailable until a whole-library Sort; the reason text now
   says so, but the Sort tab itself does not.
3. "Only my active collection" (Unused mods) has no tooltip of its own; its meaning is now in unused.check.
