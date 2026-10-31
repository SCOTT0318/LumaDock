using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using MonitorControl;

internal static class WidgetSettingsTests
{
    private static int checks;
    private static void Check(bool passed, string name)
    { if (!passed) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }

    public static int Main(string[] args)
    {
        if (args.Length != 1) { Console.Error.WriteLine("Usage: WidgetSettingsTests.exe <workspace-test-directory>"); return 2; }
        try
        {
            string root = Path.Combine(Path.GetFullPath(args[0]), "settings-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(root, "layout.json");
            var store = new SettingsStore(path);
            Check(!Directory.Exists(root), "Constructing a settings store creates no directory");
            var settings = store.Load();
            Check(settings.Version == 1 && settings.RestoreWidgets && settings.Widgets.Count == 0, "Missing settings use usable defaults");
            Check(!Directory.Exists(root), "Loading absent settings performs no writes");
            settings.Widgets.Add(new WidgetPlacement { Id = "one", MonitorId = "DISPLAY\\ABC\\UID10", MonitorName = "작업 모니터", Left = -420, Top = 160, AlwaysOnTop = true, ShowBrightness = false, ShowContrast = true, Theme = "light", Locked = true });
            store.Save(settings);
            var saved = store.Load();
            Check(saved.Widgets.Count == 1 && saved.Widgets[0].MonitorName == "작업 모니터", "Monitor identities and Unicode labels survive persistence");
            Check(saved.Widgets[0].Left == -420 && saved.Widgets[0].Top == 160, "Negative multi-monitor coordinates are preserved");
            Check(saved.Widgets[0].AlwaysOnTop && saved.Widgets[0].Locked && !saved.Widgets[0].ShowBrightness && saved.Widgets[0].ShowContrast && saved.Widgets[0].Theme == "light", "Widget configuration survives persistence");
            settings.RestoreWidgets = false; settings.Widgets[0].Top = 240; store.Save(settings);
            saved = store.Load();
            Check(!saved.RestoreWidgets && saved.Widgets[0].Top == 240, "Saving again atomically replaces the previous layout");
            Check(Directory.GetFiles(root, "*.tmp").Length == 0, "Successful saves leave no temporary files");
            string corrupt = "{not valid JSON"; File.WriteAllText(path, corrupt);
            Check(store.Load().Widgets.Count == 0 && !String.IsNullOrEmpty(store.LastLoadError), "Corrupt settings fall back without crashing");
            Check(File.ReadAllText(path) == corrupt, "Loading corrupt settings does not overwrite the user's file");
            File.WriteAllText(path, new string('x', SettingsStore.MaximumFileBytes + 1));
            Check(store.Load().Widgets.Count == 0 && store.LastLoadError != null, "Oversized settings are rejected before parsing");
            File.WriteAllText(path, "{\"Version\":1,\"Future\":\"ignored\",\"Widgets\":[{\"Id\":\"stable\",\"MonitorId\":\"display-a\",\"Unknown\":123,\"Left\":12,\"Top\":24}]}");
            saved = store.Load();
            Check(saved.Widgets.Single().Id == "stable" && saved.Widgets[0].Left == 12, "Unknown fields do not break known layout fields");
            Check(saved.RestoreWidgets && saved.Widgets[0].ShowBrightness && saved.Widgets[0].Theme == "dark", "Missing optional fields retain constructor defaults");
            Check(store.LastLoadError == null, "A successful load clears an earlier load error");
            settings = new WidgetSettings { Widgets = new List<WidgetPlacement> {
                new WidgetPlacement { Id = "repeat", Left = Double.NaN, Top = Double.PositiveInfinity, Theme = "invalid", ShowBrightness = false, ShowContrast = false },
                null,
                new WidgetPlacement { Id = "REPEAT", Left = -200000, Top = 320, Theme = " LIGHT ", ShowBrightness = false, ShowContrast = true },
                new WidgetPlacement { Id = "", MonitorId = new string('a', 600), MonitorName = new string('b', 200) },
                new WidgetPlacement { Id = "volume-only", ShowBrightness = false, ShowContrast = false, ShowVolume = true }
            }};
            store.Save(settings); saved = store.Load();
            Check(saved.Widgets.Count == 4 && saved.Widgets.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 4, "Null placements are ignored and duplicate or empty IDs are repaired");
            Check(saved.Widgets[0].Left == 80 && saved.Widgets[0].Top == 80 && saved.Widgets[1].Left == 80 && saved.Widgets[1].Top == 320, "Invalid or extreme coordinates fall back to finite positions");
            Check(saved.Widgets[0].ShowBrightness && !saved.Widgets[1].ShowBrightness && saved.Widgets[1].ShowContrast, "A widget retains at least one control without changing contrast-only layouts");
            Check(saved.Widgets[0].Theme == "dark" && saved.Widgets[1].Theme == "light", "Themes are normalized to supported names");
            Check(saved.Widgets[2].MonitorId.Length == 512 && saved.Widgets[2].MonitorName.Length == 160, "Persisted identifiers and labels are bounded");
            Check(saved.Widgets[3].ShowVolume && !saved.Widgets[3].ShowBrightness && !saved.Widgets[3].ShowContrast, "Volume-only widgets preserve their selected control across persistence");
            settings = new WidgetSettings { Widgets = new List<WidgetPlacement> {
                new WidgetPlacement { Id = "first", MonitorId = "  display\\ABC\\UID10  ", MonitorName = "Same model", Left = -210, Top = 45, Theme = "light", Locked = true, AlwaysOnTop = true, ShowBrightness = false, ShowContrast = true },
                new WidgetPlacement { Id = "duplicate", MonitorId = "DISPLAY\\abc\\uid10", MonitorName = "Renamed duplicate", Left = 500, Top = 800, Theme = "dark", Locked = false, AlwaysOnTop = false, ShowBrightness = true, ShowContrast = false, ShowVolume = true },
                new WidgetPlacement { Id = "other", MonitorId = "display\\ABC\\UID11", MonitorName = "Same model" }
            }};
            string legacy = new JavaScriptSerializer().Serialize(settings);
            File.WriteAllText(path, legacy);
            saved = store.Load();
            Check(saved.Widgets.Count == 2 && saved.Widgets[1].Id == "other", "Legacy duplicate monitor IDs collapse while identical models on different connections remain separate");
            Check(saved.Widgets[0].Id == "first" && saved.Widgets[0].Left == -210 && saved.Widgets[0].Top == 45 && saved.Widgets[0].Theme == "light" && saved.Widgets[0].Locked && saved.Widgets[0].AlwaysOnTop, "Duplicate migration preserves the first widget identity, position and appearance preferences");
            Check(saved.Widgets[0].ShowBrightness && saved.Widgets[0].ShowContrast && saved.Widgets[0].ShowVolume, "Duplicate migration merges enabled controls without losing an existing control");
            Check(saved.Widgets[0].MonitorId == "display\\ABC\\UID10", "Monitor matching ignores case and surrounding whitespace");
            Check(File.ReadAllText(path) == legacy, "Loading legacy duplicates does not overwrite the existing settings file");
            store.Save(settings);
            Check(settings.Widgets.Count == 3 && !settings.Widgets[0].ShowBrightness && !settings.Widgets[0].ShowVolume && settings.Widgets[0].MonitorId.StartsWith("  "), "Saving duplicate layouts sanitizes a clone without mutating the source placements");
            settings.Widgets = new List<WidgetPlacement> {
                new WidgetPlacement { Id = "empty-control", MonitorId = "monitor-a", ShowBrightness = false, ShowContrast = false, ShowVolume = false },
                new WidgetPlacement { Id = "contrast-control", MonitorId = "MONITOR-A", ShowBrightness = false, ShowContrast = true }
            };
            store.Save(settings); saved = store.Load();
            Check(saved.Widgets.Count == 1 && !saved.Widgets[0].ShowBrightness && saved.Widgets[0].ShowContrast, "The minimum-control fallback runs after merging and preserves a contrast-only result");
            settings.Widgets = new List<WidgetPlacement> { new WidgetPlacement { Id = "unknown-one", MonitorId = "" }, new WidgetPlacement { Id = "unknown-two", MonitorId = "  " } };
            store.Save(settings);
            Check(store.Load().Widgets.Count == 2, "Unknown empty monitor IDs are not treated as the same physical monitor");
            settings.Widgets = Enumerable.Range(0, SettingsStore.MaximumWidgets + 8).Select(x => new WidgetPlacement { Id = "w" + x }).ToList();
            store.Save(settings);
            Check(store.Load().Widgets.Count == SettingsStore.MaximumWidgets, "The layout has a bounded number of widgets");
            settings.Widgets = Enumerable.Range(0, SettingsStore.MaximumWidgets).Select(x => new WidgetPlacement { Id = "limited-" + x, MonitorId = "monitor-" + x, ShowBrightness = false, ShowContrast = true }).ToList();
            settings.Widgets.Add(new WidgetPlacement { Id = "late-duplicate", MonitorId = "MONITOR-0", ShowBrightness = false, ShowVolume = true });
            store.Save(settings); saved = store.Load();
            Check(saved.Widgets.Count == SettingsStore.MaximumWidgets && saved.Widgets[0].ShowContrast && saved.Widgets[0].ShowVolume && !saved.Widgets[0].ShowBrightness, "Duplicate controls still merge after reaching the unique-widget limit");
            var jobs = Enumerable.Range(0, 16).Select(index => Task.Run(delegate {
                new SettingsStore(path).Save(new WidgetSettings { Widgets = new List<WidgetPlacement> { new WidgetPlacement { Id = "concurrent-" + index } } });
            })).ToArray();
            Task.WaitAll(jobs); saved = store.Load();
            Check(saved.Widgets.Count == 1 && saved.Widgets[0].Id.StartsWith("concurrent-"), "Concurrent stores write one complete valid layout");
            Check(Directory.GetFiles(root, "*.tmp").Length == 0, "Concurrent saves leave no temporary files");
            string beforeNull = File.ReadAllText(path); bool rejected = false;
            try { store.Save(null); } catch (ArgumentNullException) { rejected = true; }
            Check(rejected && File.ReadAllText(path) == beforeNull, "Null saves cannot replace valid settings");
            File.WriteAllBytes(path, new byte[] { 0xFF, 0xFE, 0xFF, 0xFF, 0xFF });
            Check(store.Load().Widgets.Count == 0 && store.LastLoadError != null, "Invalid text encoding falls back safely");
            settings = new WidgetSettings { Widgets = null }; store.Save(settings);
            Check(store.Load().Widgets.Count == 0, "A null widget list becomes an empty layout");
            Console.WriteLine(checks + " widget settings checks passed. All writes stayed inside " + root); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
