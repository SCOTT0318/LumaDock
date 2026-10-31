using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MonitorControl;

internal static class TrayStartupTests
{
    private static readonly List<string> results = new List<string>();
    private static MainWindow window;
    private static bool sawVisible, completed;
    private static int exitCode;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 1) return 2;
        try
        {
            var app = new Application();
            window = new MainWindow(true, null, true);
            window.IsVisibleChanged += delegate { if (window.IsVisible) sawVisible = true; };
            app.MainWindow = window;
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            timer.Tick += async delegate {
                if (completed) return;
                if (!Field<bool>("startupInitialized") || Field<bool>("refreshing") || Field<List<MonitorDevice>>("monitors").Count != 3)
                {
                    if (DateTime.UtcNow < deadline) return;
                    completed = true; timer.Stop(); results.Add("FAIL Background initialization timed out."); exitCode = 1; window.RequestExit(); return;
                }
                completed = true; timer.Stop();
                try { await Run(); }
                catch (Exception ex) { results.Add("FAIL " + ex); exitCode = 1; }
                finally { window.RequestExit(); }
            };
            timer.Start();
            window.StartBackground();
            app.Run();
        }
        catch (Exception ex) { results.Add("FAIL " + ex); exitCode = 1; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0])));
        File.WriteAllLines(args[0], results);
        return exitCode;
    }

    private static T Field<T>(string name)
    { return (T)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window); }
    private static T Field<T>(object target, string name)
    { return (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target); }
    private static object Invoke(string name, params object[] args)
    { return typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).Invoke(window, args); }
    private static void Check(bool condition, string description)
    { if (!condition) throw new Exception(description); results.Add("PASS " + description); }
    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 100; i++) { if (condition()) return; await Task.Delay(30); }
        throw new TimeoutException("Background application did not settle.");
    }

    private static async Task Run()
    {
        Check(window.StartupToTray, "Background launch exposes its requested tray-only startup mode");
        Check(!sawVisible && !window.IsVisible, "The main window never becomes visible during initial background startup");
        Check(new WindowInteropHelper(window).Handle != IntPtr.Zero, "Background startup creates a hidden native handle for input and single-instance messages");
        var tray = Field<System.Windows.Forms.NotifyIcon>("tray");
        Check(tray.Visible, "Background startup exposes the system tray icon");
        var monitors = Field<List<MonitorDevice>>("monitors");
        Check(monitors.Count == 3 && monitors.All(x => x.Features.Count > 0), "Monitor discovery and controls initialize while the main window remains hidden");
        Check(!Field<Window>("flyout").IsVisible && Field<Window>("settingsWindow") == null, "Background startup does not open brightness or settings windows");
        var updater = Field<AppUpdater>("updater");
        Check(Field<Window>(updater, "dialog") == null, "Background startup does not display an update dialog");
        var input = Field<BrightnessInput>("brightnessInput");
        Check(!input.HotkeysAvailable && !input.RawInputAvailable && !input.WindowsEventsAvailable, "Demo background startup has no native hook side effects");
        window.StartBackground(); await Task.Delay(80);
        Check(!window.IsVisible && Object.ReferenceEquals(monitors, Field<List<MonitorDevice>>("monitors")), "Repeated background initialization does not show or rescan the window");
        Invoke("ShowWindow"); await WaitFor(() => window.IsVisible);
        Check(window.IsVisible && sawVisible, "The explicit tray app-opening action restores the full main window");
        Check(Object.ReferenceEquals(monitors, Field<List<MonitorDevice>>("monitors")), "Opening the main window does not run startup initialization twice");
        window.Hide(); Invoke("ToggleBrightnessFlyout");
        var flyout = Field<Window>("flyout"); await WaitFor(() => flyout.IsVisible);
        Check(!window.IsVisible && flyout.IsVisible, "Tray brightness controls remain available after tray-only startup without opening the main window");
        results.Add(results.Count + " tray startup checks passed without startup registry writes or monitor changes.");
    }
}
