using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace MonitorControl
{
    public sealed class WidgetSettings
    {
        public int Version = 1;
        public List<WidgetPlacement> Widgets = new List<WidgetPlacement>();
        public bool RestoreWidgets = true;
        public List<MonitorPreference> Monitors = new List<MonitorPreference>();
        public BrightnessPreferences Brightness = new BrightnessPreferences();
        public bool CheckUpdatesOnStartup = true;
        public bool StartWithWindows = true;
        public bool StartInTray = true;
        public bool HdmiSafeMode = true;
    }

    public sealed class MonitorPreference
    {
        public string MonitorId = "";
        public string Alias = "";
    }

    public sealed class BrightnessPreferences
    {
        public bool Enabled;
        public bool FollowWindows = true;
        public bool UseHotkeys = true;
        public string Mode = "all";
        public List<string> SelectedMonitorIds = new List<string>();
        public int StepPercent = 5;
    }

    public sealed class WidgetPlacement
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string MonitorId = "";
        public string MonitorName = "";
        public double Left = 80;
        public double Top = 80;
        public bool AlwaysOnTop;
        public string Theme = "dark";
        public bool ShowBrightness = true;
        public bool ShowContrast;
        public bool ShowVolume;
        public bool Locked;
    }

    // Stores widget layout only. Brightness/contrast values are always queried from
    // the monitor and are deliberately never persisted or restored by this class.
    public sealed class SettingsStore
    {
        public const int MaximumWidgets = 32;
        public const int MaximumFileBytes = 256 * 1024;
        private static readonly object ProcessLock = new object();
        private readonly string settingsPath;

        public SettingsStore(string path = null)
        {
            if (path == null)
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaDock", "settings.json");
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("A settings file path is required.", "path");
            settingsPath = Path.GetFullPath(path);
        }

        public string SettingsPath { get { return settingsPath; } }
        public string LastLoadError { get; private set; }

        public WidgetSettings Load()
        {
            lock (ProcessLock)
            {
                LastLoadError = null;
                try
                {
                    // Loading a missing settings file must not create any folders.
                    if (!File.Exists(settingsPath)) return new WidgetSettings();
                    string json;
                    using (var stream = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        if (stream.Length > MaximumFileBytes) throw new InvalidDataException("The widget settings file is too large.");
                        using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true))
                        {
                            // Bound the read even if another process grows the file.
                            char[] buffer = new char[MaximumFileBytes + 1];
                            int count = reader.ReadBlock(buffer, 0, buffer.Length);
                            if (count > MaximumFileBytes) throw new InvalidDataException("The widget settings file is too large.");
                            json = new string(buffer, 0, count);
                        }
                    }
                    var parsed = Serializer().Deserialize<WidgetSettings>(json);
                    if (parsed == null) throw new InvalidDataException("The widget settings file is empty.");
                    return Sanitize(parsed);
                }
                catch (InvalidDataException ex) { LastLoadError = ex.Message; }
                catch (IOException ex) { LastLoadError = ex.Message; }
                catch (UnauthorizedAccessException ex) { LastLoadError = ex.Message; }
                catch (ArgumentException ex) { LastLoadError = ex.Message; }
                catch (InvalidOperationException ex) { LastLoadError = ex.Message; }
                return new WidgetSettings();
            }
        }

        public void Save(WidgetSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            lock (ProcessLock)
            {
                var clean = Sanitize(settings);
                byte[] data = new UTF8Encoding(false).GetBytes(Serializer().Serialize(clean));
                if (data.Length > MaximumFileBytes) throw new InvalidDataException("The widget settings file is too large.");
                string directory = Path.GetDirectoryName(settingsPath);
                Directory.CreateDirectory(directory);
                string temporary = Path.Combine(directory, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    // Write and flush beside the target so Replace/Move stay on one volume.
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    {
                        stream.Write(data, 0, data.Length);
                        stream.Flush(true);
                    }
                    if (File.Exists(settingsPath))
                    {
                        try { File.Replace(temporary, settingsPath, null, true); }
                        catch (FileNotFoundException)
                        {
                            // Another process may have removed the target after Exists.
                            if (File.Exists(settingsPath)) throw;
                            File.Move(temporary, settingsPath);
                        }
                    }
                    else
                    {
                        try { File.Move(temporary, settingsPath); }
                        catch (IOException)
                        {
                            // A competing instance may have created the first file.
                            if (!File.Exists(settingsPath)) throw;
                            File.Replace(temporary, settingsPath, null, true);
                        }
                    }
                }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        private static JavaScriptSerializer Serializer()
        { return new JavaScriptSerializer { MaxJsonLength = MaximumFileBytes, RecursionLimit = 32 }; }

        private static WidgetSettings Sanitize(WidgetSettings source)
        {
            var result = new WidgetSettings { RestoreWidgets = source.RestoreWidgets, CheckUpdatesOnStartup = source.CheckUpdatesOnStartup,
                StartWithWindows = source.StartWithWindows, StartInTray = source.StartInTray, HdmiSafeMode = source.HdmiSafeMode };
            var knownMonitors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var monitor in (source.Monitors ?? new List<MonitorPreference>()).Where(x => x != null))
            {
                string id = Bounded(monitor.MonitorId, 512);
                if (id.Length == 0 || !knownMonitors.Add(id)) continue;
                if (result.Monitors.Count >= 64) break;
                result.Monitors.Add(new MonitorPreference { MonitorId = id, Alias = Bounded(monitor.Alias, 80) });
            }
            var input = source.Brightness ?? new BrightnessPreferences();
            result.Brightness = new BrightnessPreferences {
                Enabled = input.Enabled, FollowWindows = input.FollowWindows, UseHotkeys = input.UseHotkeys,
                Mode = input.Mode == "cursor" || input.Mode == "selected" ? input.Mode : "all",
                StepPercent = Math.Max(1, Math.Min(20, input.StepPercent)),
                SelectedMonitorIds = (input.SelectedMonitorIds ?? new List<string>()).Select(x => Bounded(x, 512)).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToList()
            };
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var monitors = new Dictionary<string, WidgetPlacement>(StringComparer.OrdinalIgnoreCase);
            if (source.Widgets == null) return result;
            foreach (var item in source.Widgets)
            {
                if (item == null) continue;
                string monitorId = Bounded(item.MonitorId, 512);
                WidgetPlacement existing;
                if (monitorId.Length > 0 && monitors.TryGetValue(monitorId, out existing))
                {
                    // Legacy layouts could contain several panels for one monitor.
                    // Keep the first panel's placement/preferences and preserve all controls.
                    existing.ShowBrightness |= item.ShowBrightness;
                    existing.ShowContrast |= item.ShowContrast;
                    existing.ShowVolume |= item.ShowVolume;
                    continue;
                }
                if (result.Widgets.Count >= MaximumWidgets) continue;
                string id = item.Id == null ? "" : item.Id.Trim();
                if (id.Length == 0 || id.Length > 64 || !used.Add(id))
                {
                    do { id = Guid.NewGuid().ToString("N"); } while (!used.Add(id));
                }
                var placement = new WidgetPlacement {
                    Id = id,
                    MonitorId = monitorId,
                    MonitorName = Bounded(item.MonitorName, 160),
                    Left = Coordinate(item.Left), Top = Coordinate(item.Top),
                    AlwaysOnTop = item.AlwaysOnTop,
                    Theme = String.Equals((item.Theme ?? "").Trim(), "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark",
                    ShowBrightness = item.ShowBrightness,
                    ShowContrast = item.ShowContrast,
                    ShowVolume = item.ShowVolume,
                    Locked = item.Locked
                };
                result.Widgets.Add(placement);
                // Empty IDs cannot identify a physical monitor reliably.
                if (monitorId.Length > 0) monitors.Add(monitorId, placement);
            }
            foreach (var placement in result.Widgets)
                if (!(placement.ShowBrightness || placement.ShowContrast || placement.ShowVolume)) placement.ShowBrightness = true;
            return result;
        }

        private static string Bounded(string value, int maximum)
        {
            value = (value ?? "").Trim();
            return value.Length <= maximum ? value : value.Substring(0, maximum);
        }

        private static double Coordinate(double value)
        { return Double.IsNaN(value) || Double.IsInfinity(value) || Math.Abs(value) > 100000 ? 80 : value; }
    }
}
