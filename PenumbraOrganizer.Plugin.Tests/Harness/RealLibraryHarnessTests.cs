using System.Diagnostics;
using System.Text;
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Core.Models;
using PenumbraOrganizer.Core.Services;
using PenumbraOrganizer.Plugin.LibraryWork.Pure;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Classification;
using PenumbraOrganizer.Plugin.Organizer.Layouts;
using PenumbraOrganizer.Plugin.Organizer.Rules;
using PenumbraOrganizer.Plugin.Organizer.Workbook;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Harness;

/// <summary>
/// An opt-in, READ-ONLY run of the plugin's own scan, sort, export and import code over a real mod library
/// on disk. It does nothing unless PENUMBRA_HARNESS_LIBS names one or more library folders (separated by
/// semicolons); the report goes to PENUMBRA_HARNESS_OUT (default: the temp folder). Nothing is written to a
/// library. What it cannot see: Penumbra's changed-item keys (they come from Penumbra over IPC), so Face,
/// Skin and Tails race evidence and virtual folders are out of reach; file-based evidence (hair, racial
/// scaling) is real.
/// </summary>
public class RealLibraryHarnessTests
{
    [Fact]
    public void RunAgainstRealLibraries()
    {
        var libs = Environment.GetEnvironmentVariable("PENUMBRA_HARNESS_LIBS");
        if (string.IsNullOrWhiteSpace(libs))
            return;

        var outPath = Environment.GetEnvironmentVariable("PENUMBRA_HARNESS_OUT")
                      ?? Path.Combine(Path.GetTempPath(), "penumbra-harness-report.txt");
        var report = new StringBuilder();
        var failures = 0;
        foreach (var lib in libs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            failures += RunOne(lib, report);

        File.WriteAllText(outPath, report.ToString());
        Assert.True(failures == 0, $"{failures} harness check(s) failed; see {outPath}");
    }

    private static int RunOne(string lib, StringBuilder r)
    {
        var failures = 0;
        void Check(string what, bool ok, string detail = "")
        {
            r.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail.Length > 0 ? " - " + detail : "")}");
            if (!ok)
                failures++;
        }

        r.AppendLine($"=== Library {lib}");
        var cfgDir = Path.Combine(Path.GetTempPath(), "penumbra-harness-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cfgDir);
        var processor = new ScanProcessor(cfgDir, useScrapedNpcNameList: false);
        processor.Prepare(CancellationToken.None);

        // ---- scan (no changed-item keys available headlessly)
        var rows = new List<OrganizerModRow>();
        var timings = new List<(string Name, double Ms)>();
        var scanFailures = new List<string>();
        var total = Stopwatch.StartNew();
        foreach (var dir in Directory.GetDirectories(lib).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dir);
            var watch = Stopwatch.StartNew();
            try
            {
                // Without Penumbra's changed-item keys the real classifier files everything as Others and never
                // reads the files, so the file-based evidence is read directly and the category is inferred
                // from it: hair files make a Hair mod, anything else is Gear (where a racial-scaling pack sits
                // in the real plugin).
                var content = ModContentReader.Read(new DirectoryInfo(dir));
                var hasHair = content is not null && content.Hair.ByRaceCode is { Count: > 0 };
                rows.Add(new OrganizerModRow
                {
                    Identifier = name, Name = name, Author = string.Empty,
                    CurrentPath = "All/" + name, ProposedPath = "All/" + name,
                    Category = hasHair ? ModCategory.Hair : ModCategory.Gear,
                    HairFlat = content?.Hair.Flat,
                    HairByRaceCode = content?.Hair.ByRaceCode,
                    Rsp = content?.Rsp,
                    IsRacialScaling = content?.Rsp is { Pure: true },
                });
            }
            catch (Exception ex)
            {
                scanFailures.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
            }

            timings.Add((name, watch.Elapsed.TotalMilliseconds));
        }

        total.Stop();
        r.AppendLine($"Scanned {rows.Count} mods in {total.Elapsed.TotalSeconds:F1}s ({scanFailures.Count} failures).");
        foreach (var f in scanFailures.Take(10))
            r.AppendLine("  scan failure: " + f);
        r.AppendLine("Slowest mods: " + string.Join("; ", timings.OrderByDescending(t => t.Ms).Take(5).Select(t => $"{t.Name} {t.Ms:F0} ms")));
        Check("no mod failed to scan", scanFailures.Count == 0);

        // ---- racial-scaling evidence
        var withRsp = rows.Where(x => x.Rsp is not null).ToList();
        var flagged = rows.Where(x => x.IsRacialScaling).ToList();
        var impure = withRsp.Where(x => !x.Rsp!.Pure).ToList();
        r.AppendLine($"Rsp evidence: {withRsp.Count} mods; flagged racial scaling (pure): {flagged.Count}; impure (not flagged): {impure.Count}.");
        foreach (var x in flagged)
            r.AppendLine($"  FLAGGED {x.Name}: races {string.Join(", ", x.Rsp!.Races.OrderBy(n => n))} (complete={x.Rsp.Complete})");
        foreach (var x in impure.Take(12))
            r.AppendLine($"  impure  {x.Name}: races {string.Join(", ", x.Rsp!.Races.OrderBy(n => n))}");

        foreach (var needle in new[] { "Ultimate Height Mod", "Body Scale 2.0" })
        {
            foreach (var x in rows.Where(m => m.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            {
                Check($"{x.Name} is flagged racial scaling", x.IsRacialScaling,
                    $"{x.Rsp?.Races.Count ?? 0} races, scan {timings.First(t => t.Name == x.Name).Ms:F0} ms");
                Check($"{x.Name} scanned in under 10 s", timings.First(t => t.Name == x.Name).Ms < 10_000);
            }
        }

        foreach (var x in rows.Where(m => m.Name.Contains("tiramisu2", StringComparison.OrdinalIgnoreCase)))
            Check($"{x.Name} is NOT flagged racial scaling", !x.IsRacialScaling, x.Rsp is null ? "no Rsp read" : $"races {string.Join(",", x.Rsp.Races)}");

        // ---- sort comparisons
        var labels = processor.BodyLabels;
        List<(string Id, string Path)> SortWith(Configuration cfg)
        {
            var state = new OrganizerState();
            state.LoadScan(rows, new HashSet<string>());
            SortRunner.Run(state, SortSelectionFactory.FromConfig(cfg, labels), n => n, scope: null);
            return state.Mods.Select(m => (m.Identifier, m.ProposedPath)).OrderBy(p => p.Identifier, StringComparer.Ordinal).ToList();
        }

        int Differences(List<(string Id, string Path)> a, List<(string Id, string Path)> b) =>
            a.Zip(b).Count(p => p.First.Path != p.Second.Path);

        var baseline = SortWith(new Configuration { SortSplitBodyMods = true });
        var masterOff = SortWith(new Configuration { SortSplitBodyMods = true, RaceSplitEnabled = false });
        Check("master off sorts exactly like the default", Differences(baseline, masterOff) == 0,
            $"{Differences(baseline, masterOff)} differences over {baseline.Count} mods");

        var legacyHair = SortWith(new Configuration { SortSplitHair = true, SortSplitHairByRace = true, SortSplitBodyMods = true });
        var migratedHair = SortWith(new Configuration
        {
            SortSplitHair = true, SortSplitBodyMods = true, RaceSplitEnabled = true, RaceSplitRows = ["Hair"],
        });
        Check("master on with only Hair ticked equals the old hair-by-race checkbox", Differences(legacyHair, migratedHair) == 0,
            $"{Differences(legacyHair, migratedHair)} differences; {Differences(baseline, legacyHair)} mods differ from no hair split");

        var scalingOn = SortWith(new Configuration
        {
            SortSplitBodyMods = true, RaceSplitEnabled = true, RaceSplitRows = ["RacialScaling"],
        });
        var scalingOnPriority = SortWith(new Configuration
        {
            SortSplitBodyMods = true, RaceSplitEnabled = true, RaceSplitRows = ["RacialScaling"],
            SendMultiRaceToMultiRace = false, RacePriority = ["Viera", "Miqo'te"],
        });
        var scalingRowOff = SortWith(new Configuration
        {
            SortSplitBodyMods = true, RaceSplitEnabled = true, RaceSplitRows = ["Hair"],
        });
        Check("with the Racial scaling row off, flagged packs file exactly where they did before",
            flagged.All(x => scalingRowOff.First(p => p.Id == x.Identifier).Path == baseline.First(p => p.Id == x.Identifier).Path));
        foreach (var x in flagged)
        {
            r.AppendLine($"  {x.Name}");
            r.AppendLine($"    off:                {baseline.First(p => p.Id == x.Identifier).Path}");
            r.AppendLine($"    on, multi-race on:  {scalingOn.First(p => p.Id == x.Identifier).Path}");
            r.AppendLine($"    on, priority V,M:   {scalingOnPriority.First(p => p.Id == x.Identifier).Path}");
        }

        Check("flagged packs move under Racial Scaling when the row is on",
            flagged.All(x => scalingOn.First(p => p.Id == x.Identifier).Path.Contains("Racial Scaling", StringComparison.Ordinal)));
        Check("impure Rsp mods (tiramisu2 and similar) are never moved to Racial Scaling",
            impure.All(x => !scalingOn.First(p => p.Id == x.Identifier).Path.Contains("Racial Scaling", StringComparison.Ordinal)));

        // ---- category layouts pass: migration keeps every path; Recommended is measured, not asserted
        {
            var migratedConfig = new Configuration { SortSplitBodyMods = true };
            migratedConfig.MigrateToCurrentVersion();
            var migrated = SortWith(migratedConfig);
            Check("a migrated legacy config sorts exactly as before migration", Differences(baseline, migrated) == 0,
                $"{Differences(baseline, migrated)} differences over {baseline.Count} mods");

            // An existing user who never changed the Sort tab, against a new install on Recommended.
            var today = SortWith(new Configuration());
            var recommended = SortWith(Configuration.CreateFresh());
            var moved = Differences(today, recommended);
            r.AppendLine($"  Recommended vs today's default layout: {moved} of {today.Count} mods would move");
            foreach (var (before, after) in today.Zip(recommended).Where(p => p.First.Path != p.Second.Path).Take(10))
                r.AppendLine($"    {before.Path}  ->  {after.Path}");

            var renamed = Configuration.CreateFresh();
            renamed.SaveLayoutProfile(LayoutProfile.Recommended.With(
                ModCategory.Gear, LayoutProfile.Recommended[ModCategory.Gear] with { Folder = "Equipment/Gear" }));
            var renamedPaths = SortWith(renamed);
            Check("renaming Gear to Equipment/Gear changes only the root of Gear paths",
                recommended.Zip(renamedPaths).All(p => p.First.Path == p.Second.Path
                    || (p.First.Path.StartsWith("Gear/", StringComparison.Ordinal)
                        && p.Second.Path == "Equipment/" + p.First.Path)));
        }

        // ---- keyword rules pass
        {
            var author = rows.Select(x => x.Author).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)) ?? "nobody";
            var ruleTexts = new[]
            {
                "name contains 'hair' -> Hair/By name",
                $"author = '{author.Replace("'", "''")}' -> {{creator}}/{{type}}",
                "type = 'Gear' and subtype in ('Top','Legs') -> Gear/Outfits",
                "race = '' and type = 'Body' -> Body/Unknown race",
                "not type in ('Gear','Hair') and name starts with 'a' -> A/{type}",
            };
            var set = RuleSet.Compile(ruleTexts.Select(t => new RuleEntry(t, true)).ToList(), RaceSplitOptions.Off);
            Check("all five example rules parse", set.Rules.All(x => x.Rule is not null),
                string.Join("; ", set.Rules.Where(x => x.Error is not null).Select(x => $"#{x.Ordinal}: {x.Error!.Message}")));

            var baseSelection = SortSelectionFactory.FromConfig(new Configuration { SortSplitBodyMods = true }, labels);
            List<(string Id, string Path, StagedRule? Rule)> RunSort(RuleSet? rules)
            {
                var s = new OrganizerState();
                s.LoadScan(rows, new HashSet<string>());
                SortRunner.Run(s, baseSelection with { Layout = baseSelection.Layout with { Rules = rules } }, n => n, scope: null);
                return s.Mods.Select(m => (m.Identifier, m.ProposedPath, m.StagedRule))
                    .OrderBy(p => p.Identifier, StringComparer.Ordinal).ToList();
            }

            var none = RunSort(null);
            var empty = RunSort(RuleSet.Empty);
            Check("an empty RuleSet gives the same proposals as no rules",
                none.Zip(empty).All(p => p.First.Path == p.Second.Path), $"{none.Count} mods");

            var ruleWatch = Stopwatch.StartNew();
            List<(string Id, string Path, StagedRule? Rule)> ruled;
            try { ruled = RunSort(set); }
            catch (Exception ex)
            {
                Check("rules pass raised no exception", false, ex.GetType().Name + ": " + ex.Message);
                ruled = none;
            }

            ruleWatch.Stop();
            var wins = ruled.Where(p => p.Rule is not null).ToList();
            r.AppendLine($"Rules pass: {wins.Count} of {ruled.Count} mods filed by a rule in {ruleWatch.Elapsed.TotalMilliseconds:F0} ms.");
            foreach (var rule in set.Rules)
                r.AppendLine($"  rule #{rule.Ordinal} wins {wins.Count(p => p.Rule!.Ordinal == rule.Ordinal)}: {rule.Text}");
            Check("every ruled ProposedPath is non-empty and has no '..'",
                wins.All(p => p.Path.Length > 0 && !p.Path.Contains("..", StringComparison.Ordinal)));
            if (rows.Count >= 2557)
                Check("rules pass at 2,557 mods or more takes under 2 seconds", ruleWatch.Elapsed.TotalSeconds < 2,
                    $"{ruleWatch.Elapsed.TotalMilliseconds:F0} ms for {rows.Count} mods");
        }

        // ---- workbook round trips on the real names
        var installation = new PenumbraInstallation(
            "C:/Penumbra/Penumbra.json", "C:/Penumbra", "C:/Penumbra/Mods", null, null, null,
            DiscoveryConfidence.High, [], []);
        var canonicalizer = new CreatorCanonicalizer();
        var race = RaceSplitOptions.FromConfig(new Configuration { RaceSplitEnabled = true, RaceSplitRows = ["Hair"] });
        var service = new WorkbookService(Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkbookService>.Instance);

        WorkbookImportContext ContextFor(OrganizerState s)
        {
            var byId = s.Mods.ToDictionary(m => m.Identifier, StringComparer.Ordinal);
            return new WorkbookImportContext(
                id => WorkbookTokens.For(byId[id], canonicalizer.Canonicalize, race),
                id => WorkbookAdapter.SplitPath(byId[id].CurrentPath).Leaf);
        }

        foreach (var mode in new[] { WorkbookExportMode.AsIs, WorkbookExportMode.AsSuggestedBySort })
        {
            var staged = mode == WorkbookExportMode.AsSuggestedBySort;
            var state = new OrganizerState();
            state.LoadScan(rows, new HashSet<string>());
            if (staged)
                SortRunner.Run(state, SortSelectionFactory.FromConfig(
                    new Configuration { SortSplitBodyMods = true, RaceSplitEnabled = true, RaceSplitRows = ["RacialScaling"] }, labels),
                    n => n, scope: null);

            var before = state.Mods.ToDictionary(m => m.Identifier, m => m.ProposedPath);
            var inventory = WorkbookAdapter.ToScanInventory(state, installation);
            var file = Path.Combine(cfgDir, $"{mode}.xlsx");
            var watch = Stopwatch.StartNew();
            var request = new WorkbookExportRequest(
                inventory, WorkbookAdapter.ToProposals(state, staged), mode, state.SortFingerprint, [],
                WorkbookAdapter.Leaves(state, staged));
            service.ExportAsync(request, file, CancellationToken.None).GetAwaiter().GetResult();
            var exportMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            var result = service.ImportAsync(file, inventory, ContextFor(state), CancellationToken.None).GetAwaiter().GetResult();
            WorkbookAdapter.ApplyImportResult(state, result);
            var importMs = watch.Elapsed.TotalMilliseconds;

            var moved = state.Mods.Count(m => m.ProposedPath != before[m.Identifier]);
            r.AppendLine($"Workbook {mode}: export {exportMs:F0} ms, import {importMs:F0} ms, {new FileInfo(file).Length / 1024} KB, " +
                         $"{result.Errors.Count} errors, {result.Warnings.Count} warnings, counts {result.Counts}.");
            foreach (var e in result.Errors.Take(5))
                r.AppendLine("    error: " + e);
            // "Ready" counts moves against the CURRENT folders, so the staged export legitimately reports
            // every mod the Sort moved; the point is that import changes none of the staged paths.
            var movedBySort = before.Count(p => p.Value != "All/" + p.Key);
            Check($"unedited {mode} export re-imports with zero errors and zero changed paths",
                result.Errors.Count == 0 && moved == 0 && result.Counts!.Ready == (staged ? movedBySort : 0),
                $"{moved} paths changed, {result.Counts!.Ready} reported ready ({movedBySort} moved by the sort)");
        }

        r.AppendLine();
        try { Directory.Delete(cfgDir, true); } catch (IOException) { }
        return failures;
    }
}
