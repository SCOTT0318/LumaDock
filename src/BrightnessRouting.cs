using System;
using System.Collections.Generic;
using System.Linq;

namespace MonitorControl
{
    public static class BrightnessRouting
    {
        public static List<MonitorDevice> Targets(IEnumerable<MonitorDevice> monitors, BrightnessPreferences preferences, int cursorX, int cursorY)
        {
            var supported = monitors.Where(x => x.Features.Any(f => f.Definition.Code == 0x10 && f.Writable)).ToList();
            if (preferences == null || !preferences.Enabled) return new List<MonitorDevice>();
            if (preferences.Mode == "selected")
            {
                var ids = new HashSet<string>(preferences.SelectedMonitorIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                return supported.Where(x => ids.Contains(x.Id)).ToList();
            }
            if (preferences.Mode == "cursor")
                return supported.Where(x => x.Width > 0 && x.Height > 0 && cursorX >= x.Left && cursorX < (long)x.Left + x.Width && cursorY >= x.Top && cursorY < (long)x.Top + x.Height).Take(1).ToList();
            return supported;
        }

        public static void ApplyAliases(IEnumerable<MonitorDevice> monitors, IEnumerable<MonitorPreference> preferences)
        {
            var aliases = (preferences ?? new List<MonitorPreference>()).Where(x => x != null).ToList();
            foreach (var monitor in monitors)
            {
                if (String.IsNullOrWhiteSpace(monitor.HardwareName)) monitor.HardwareName = monitor.Name;
                var preference = aliases.FirstOrDefault(x => String.Equals(x.MonitorId, monitor.Id, StringComparison.OrdinalIgnoreCase));
                monitor.Name = preference == null || String.IsNullOrWhiteSpace(preference.Alias) ? monitor.HardwareName : preference.Alias.Trim();
            }
        }
    }
}
