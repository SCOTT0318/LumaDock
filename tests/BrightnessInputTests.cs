using System;
using System.Windows.Input;
using MonitorControl;

internal static class BrightnessInputTests
{
    private static int count;
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); count++; Console.WriteLine("PASS " + message); }
    [STAThread]
    public static int Main()
    {
        try
        {
            DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var tracker = new BrightnessDeltaTracker();
            Check(tracker.Observe("panel1", 70, now) == 0, "First unknown native event establishes a baseline without writing");
            Check(tracker.Observe("panel1", 75, now) == 5, "Native brightness increase produces its actual percentage delta");
            Check(tracker.Observe("panel1", 60, now) == -15, "Native decrease preserves direction and magnitude");
            Check(tracker.Observe("PANEL1", 60, now) == 0, "Duplicate native report does not create another adjustment");
            tracker.Seed("panel2", 30);
            Check(tracker.Observe("panel2", 35, now) == 5 && tracker.Observe("panel1", 65, now) == 5, "Different native monitor instances retain separate baselines");
            tracker.Expect("panel1", 80, now);
            Check(tracker.Observe("panel1", 80, now.AddMilliseconds(100)) == 0, "App-originated native write echo is suppressed");
            Check(tracker.Observe("panel1", 85, now.AddMilliseconds(120)) == 5, "A following real change uses the self-write value as its baseline");
            tracker.Expect("panel1", 50, now);
            Check(tracker.Observe("panel1", 50, now.AddSeconds(3)) == -35, "Expired self-write expectations do not hide a later real change");
            tracker.Suppress(now.AddSeconds(4));
            Check(tracker.Observe("panel1", 55, now.AddSeconds(3.5)) == 0 && tracker.Observe("panel1", 60, now.AddSeconds(4.1)) == 5, "Temporary suppression still advances the native baseline");
            Check(tracker.Observe("panel1", 101, now) == 0 && tracker.Observe("panel1", 65, now.AddSeconds(5)) == 5, "Invalid native percentages are ignored without corrupting the baseline");
            var fusion = new BrightnessNativeFusion();
            Check(fusion.FromRaw(1, now) == 1 && fusion.FromWindows(10, now.AddMilliseconds(50)) == 0, "Raw brightness report and its WMI echo produce one action");
            Check(fusion.FromRaw(1, now.AddMilliseconds(120)) == 1, "Repeated physical presses remain responsive during native deduplication");
            Check(fusion.FromWindows(-5, now.AddSeconds(1)) == -5 && fusion.FromRaw(-1, now.AddSeconds(1.05)) == 0, "WMI-first ordering does not double-apply the matching raw report");
            Check(fusion.FromWindows(5, now.AddSeconds(2)) == 5, "WMI-only keyboards remain supported after the raw deduplication window");
            var usage = new BrightnessUsageTracker();
            Check(usage.Observe("device/1", new ushort[] { 0x6F }) == 1, "USB consumer brightness increment is recognized");
            Check(usage.Observe("device/1", new ushort[] { 0x6F }) == 0, "Held HID button is not re-applied for duplicate reports");
            Check(usage.Observe("device/1", new ushort[0]) == 0 && usage.Observe("device/1", new ushort[] { 0x6F }) == 1, "A release permits the next brightness press");
            Check(usage.Observe("device/2", new ushort[] { 0x70 }) == -1, "USB consumer brightness decrement is recognized per report");
            Check(usage.Observe("device/3", new ushort[] { 0x79, 0x7A, 0xE9 }) == 0, "Keyboard backlight and volume usages are never interpreted as display brightness");
            Check(usage.Observe("device/4", new ushort[] { 0x6F, 0x70 }) == 0, "Contradictory simultaneous brightness usages are ignored");
            usage.RemoveDevice("device/");
            Check(usage.Observe("device/1", new ushort[] { 0x6F }) == 1, "Removing a HID device clears its held-key state");
            Check(BrightnessInput.ValidShortcut(ModifierKeys.Control | ModifierKeys.Alt, Key.Up, Key.Down), "Default Ctrl+Alt brightness shortcuts are valid");
            Check(!BrightnessInput.ValidShortcut(ModifierKeys.None, Key.Up, Key.Down) && !BrightnessInput.ValidShortcut(ModifierKeys.Windows, Key.Up, Key.Down), "Unmodified and Windows-reserved shortcut combinations are rejected");
            Check(!BrightnessInput.ValidShortcut(ModifierKeys.Control, Key.Up, Key.Up) && !BrightnessInput.ValidShortcut(ModifierKeys.Control, Key.F12, Key.Down), "Conflicting keys and debugger-reserved F12 are rejected");
            Console.WriteLine(count + " brightness input checks passed. No input registrations or monitor writes were performed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
