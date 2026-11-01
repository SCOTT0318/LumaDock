using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MonitorControl;

internal static class ControlSettingsTests
{
    private static int count;
    private static void Check(bool condition, string description)
    { if (!condition) throw new Exception(description); count++; Console.WriteLine("PASS " + description); }

    private static MonitorDevice Monitor(string id, int left, int top, int width, int height, bool writable)
    {
        var monitor = new MonitorDevice { Id = id, Name = "Same model", HardwareName = "Same model", Left = left, Top = top, Width = width, Height = height, Demo = true };
        if (writable) monitor.Features.Add(new MonitorFeature { Definition = FeatureCatalog.All.First(x => x.Code == 0x10), Maximum = 100, Current = 50 });
        return monitor;
    }

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1) throw new ArgumentException("An isolated test output directory is required.");
            string directory = Path.Combine(Path.GetFullPath(args[0]), "controls-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var left = Monitor("panel-left", -1920, -100, 1920, 1080, true);
            var primary = Monitor("panel-primary", 0, 0, 2560, 1440, true);
            var unsupported = Monitor("panel-unsupported", 2560, 0, 1920, 1080, false);
            var monitors = new List<MonitorDevice> { left, primary, unsupported };
            var preferences = new List<MonitorPreference> { new MonitorPreference { MonitorId = "PANEL-LEFT", Alias = "  왼쪽 작업용  " }, new MonitorPreference { MonitorId = "panel-primary", Alias = "메인" } };
            BrightnessRouting.ApplyAliases(monitors, preferences);
            Check(left.Name == "왼쪽 작업용" && primary.Name == "메인", "Monitor aliases match stable identities regardless of casing");
            Check(left.HardwareName == "Same model" && primary.HardwareName == "Same model", "Custom naming retains the original hardware names");
            Check(unsupported.Name == "Same model", "Same-name monitors remain independently named by identity");
            BrightnessRouting.ApplyAliases(monitors, new[] { new MonitorPreference { MonitorId = "panel-left", Alias = "   " } });
            Check(left.Name == "Same model" && primary.Name == "Same model", "Clearing or removing aliases restores hardware names");
            var noHardwareName = Monitor("new", 0, 0, 10, 10, true); noHardwareName.HardwareName = null;
            BrightnessRouting.ApplyAliases(new[] { noHardwareName }, new[] { new MonitorPreference { MonitorId = "new", Alias = "별칭" } });
            BrightnessRouting.ApplyAliases(new[] { noHardwareName }, null);
            Check(noHardwareName.Name == "Same model", "Hardware names are initialized before the first alias replaces them");

            var controls = new BrightnessPreferences { Enabled = true, Mode = "all" };
            Check(BrightnessRouting.Targets(monitors, controls, 0, 0).SequenceEqual(new[] { left, primary }), "All mode selects every connected writable brightness control");
            controls.Enabled = false;
            Check(BrightnessRouting.Targets(monitors, controls, 0, 0).Count == 0 && BrightnessRouting.Targets(monitors, null, 0, 0).Count == 0, "Disabled or absent keyboard preferences have no targets");
            controls.Enabled = true; controls.Mode = "cursor";
            Check(BrightnessRouting.Targets(monitors, controls, -1, 0).Single() == left, "Cursor routing supports monitors with negative desktop coordinates");
            Check(BrightnessRouting.Targets(monitors, controls, -1920, -100).Single() == left, "The top-left monitor boundary is included");
            Check(BrightnessRouting.Targets(monitors, controls, 0, 0).Single() == primary, "A shared right boundary belongs to the next monitor");
            Check(BrightnessRouting.Targets(monitors, controls, -1, 980).Count == 0, "The bottom monitor boundary is excluded");
            Check(BrightnessRouting.Targets(monitors, controls, 2559, 1439).Single() == primary && BrightnessRouting.Targets(monitors, controls, 2560, 1439).Count == 0, "Bottom-right cursor coordinates use exclusive edges");
            Check(BrightnessRouting.Targets(monitors, controls, 3000, 10).Count == 0, "A cursor on an unsupported monitor does not adjust another display");
            Check(BrightnessRouting.Targets(monitors, controls, -5000, 0).Count == 0, "A cursor outside connected monitors does not choose a fallback monitor");
            var unknownGeometry = Monitor("geometry-missing", 0, 0, 0, 0, true);
            Check(BrightnessRouting.Targets(new[] { unknownGeometry }, controls, 0, 0).Count == 0, "Cursor routing ignores missing monitor geometry");
            controls.Mode = "selected"; controls.SelectedMonitorIds = new List<string> { "PANEL-PRIMARY", "offline", "panel-unsupported" };
            Check(BrightnessRouting.Targets(monitors, controls, -1, 0).SequenceEqual(new[] { primary }), "Fixed selection ignores cursor position and excludes offline or unsupported IDs");
            controls.SelectedMonitorIds = new List<string>();
            Check(BrightnessRouting.Targets(monitors, controls, 0, 0).Count == 0, "An empty fixed selection never adjusts arbitrary monitors");
            controls.SelectedMonitorIds = null;
            Check(BrightnessRouting.Targets(monitors, controls, 0, 0).Count == 0, "An absent fixed selection is handled safely");
            var lockedBrightness = Monitor("zero-range", 0, 0, 10, 10, true); lockedBrightness.Features[0].Maximum = 0;
            controls.Mode = "all";
            Check(BrightnessRouting.Targets(new[] { lockedBrightness }, controls, 0, 0).Count == 0, "A non-writable brightness range is excluded from keyboard routing");

            string path = Path.Combine(directory, "settings.json"); var store = new SettingsStore(path);
            var settings = new WidgetSettings {
                Monitors = new List<MonitorPreference> { new MonitorPreference { MonitorId = "panel-left", Alias = "작업용 모니터" }, new MonitorPreference { MonitorId = "panel-primary", Alias = "메인 화면" } },
                Brightness = new BrightnessPreferences { Enabled = true, FollowWindows = false, UseHotkeys = false, Mode = "selected", SelectedMonitorIds = new List<string> { "panel-primary" }, StepPercent = 10 },
                CheckUpdatesOnStartup = false
            };
            store.Save(settings); var loaded = store.Load();
            Check(loaded.Monitors.Count == 2 && loaded.Monitors[0].Alias == "작업용 모니터", "Unicode monitor names persist across settings saves");
            Check(loaded.Brightness.Enabled && !loaded.Brightness.FollowWindows && !loaded.Brightness.UseHotkeys && loaded.Brightness.Mode == "selected" && loaded.Brightness.StepPercent == 10, "Brightness input preferences persist independently");
            Check(loaded.Brightness.SelectedMonitorIds.SequenceEqual(new[] { "panel-primary" }) && !loaded.CheckUpdatesOnStartup, "Fixed monitor selection and update preference survive persistence");
            settings.StartWithWindows = false; settings.StartInTray = false; store.Save(settings); loaded = store.Load();
            Check(!loaded.StartWithWindows && !loaded.StartInTray, "Disabled automatic and tray startup remain disabled after saving and loading");
            settings.StartWithWindows = true; settings.StartInTray = true; store.Save(settings); loaded = store.Load();
            Check(loaded.StartWithWindows && loaded.StartInTray, "Both startup options can be re-enabled and persisted");
            settings.HdmiSafeMode = false; store.Save(settings); loaded = store.Load();
            Check(!loaded.HdmiSafeMode, "The HDMI safety option persists an explicit opt-out");
            settings.HdmiSafeMode = true; store.Save(settings); loaded = store.Load();
            Check(loaded.HdmiSafeMode, "HDMI safety mode can be re-enabled without changing monitor profiles");
            settings.Monitors = new List<MonitorPreference> { null, new MonitorPreference { MonitorId = " panel-left ", Alias = " " + new string('별', 100) + " " }, new MonitorPreference { MonitorId = "PANEL-LEFT", Alias = "duplicate" }, new MonitorPreference { MonitorId = " ", Alias = "empty" } };
            settings.Brightness.Mode = "unknown"; settings.Brightness.StepPercent = 999;
            settings.Brightness.SelectedMonitorIds = new List<string> { " panel-primary ", "PANEL-PRIMARY", "", null, "offline" };
            store.Save(settings); loaded = store.Load();
            Check(loaded.Monitors.Count == 1 && loaded.Monitors[0].MonitorId == "panel-left" && loaded.Monitors[0].Alias.Length == 80, "Monitor preferences trim IDs, cap aliases and remove invalid duplicates");
            Check(loaded.Brightness.Mode == "all" && loaded.Brightness.StepPercent == 20, "Unknown modes and excessive input steps are sanitized");
            Check(loaded.Brightness.SelectedMonitorIds.SequenceEqual(new[] { "panel-primary", "offline" }), "Fixed-selection IDs are trimmed, deduplicated and retain offline choices");
            Check(settings.Monitors.Count == 4 && settings.Monitors[1].Alias.Length == 102 && settings.Brightness.Mode == "unknown", "Sanitizing controls does not mutate the caller's settings");
            settings.Brightness.StepPercent = -5; store.Save(settings); loaded = store.Load();
            Check(loaded.Brightness.StepPercent == 1, "Input step is bounded to at least one percent");
            settings.Brightness = null; settings.Monitors = null; store.Save(settings); loaded = store.Load();
            Check(loaded.Monitors.Count == 0 && loaded.Brightness != null && loaded.Brightness.StepPercent == 5, "Missing new preference groups become usable defaults");
            File.WriteAllText(path, "{\"Version\":1,\"RestoreWidgets\":false,\"Widgets\":[]}"); loaded = store.Load();
            Check(!loaded.RestoreWidgets && !loaded.Brightness.Enabled && loaded.Brightness.FollowWindows && loaded.Brightness.UseHotkeys && loaded.CheckUpdatesOnStartup, "Legacy widget-only settings load with non-disruptive keyboard defaults");
            Check(loaded.StartWithWindows && loaded.StartInTray, "Legacy settings adopt automatic startup and tray-only launch defaults");
            Check(loaded.HdmiSafeMode, "Existing installations adopt HDMI safety mode without rewriting their settings on load");
            settings = new WidgetSettings { Monitors = Enumerable.Range(0, 80).Select(x => new MonitorPreference { MonitorId = "monitor-" + x, Alias = "alias" }).ToList(),
                Brightness = new BrightnessPreferences { SelectedMonitorIds = Enumerable.Range(0, 50).Select(x => "selected-" + x).ToList() } };
            store.Save(settings); loaded = store.Load();
            Check(loaded.Monitors.Count == 64 && loaded.Brightness.SelectedMonitorIds.Count == 32, "Control preferences keep bounded monitor collections");
            Console.WriteLine(count + " control settings checks passed. All writes stayed inside " + directory);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
