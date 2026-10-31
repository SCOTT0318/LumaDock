using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using MonitorControl;

internal static class DisplayFeaturesTests
{
    private static readonly List<string> results = new List<string>();
    private static MainWindow window;
    private static int exitCode;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 1) return 2;
        try
        {
            var app = new Application();
            window = new MainWindow(true, null);
            window.Loaded += async delegate {
                try { await Run(); }
                catch (Exception ex) { results.Add("FAIL " + ex); exitCode = 1; }
                finally { window.RequestExit(); }
            };
            app.Run(window);
        }
        catch (Exception ex) { results.Add("FAIL " + ex); exitCode = 1; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0])));
        File.WriteAllLines(args[0], results);
        return exitCode;
    }

    private static T Field<T>(object target, string name)
    { return (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target); }
    private static T Field<T>(string name) { return Field<T>(window, name); }
    private static object Invoke(object target, string name, params object[] args)
    { return target.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, args); }
    private static WidgetSettings Settings()
    {
        var manager = Field<object>("widgets");
        return (WidgetSettings)manager.GetType().GetProperty("Settings", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(manager, null);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T) yield return (T)child;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static T Named<T>(DependencyObject root, string name) where T : DependencyObject
    { return Descendants<T>(root).Single(x => AutomationProperties.GetName(x) == name); }
    private static void Click(DependencyObject root, string name)
    { Named<Button>(root, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    private static void Check(bool condition, string description)
    { if (!condition) throw new Exception(description); results.Add("PASS " + description); }
    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 100; i++) { if (condition()) return; await Task.Delay(50); }
        throw new TimeoutException("The display feature UI did not settle.");
    }
    private static MonitorFeature Brightness(MonitorDevice monitor)
    { return monitor.Features.Single(x => x.Definition.Code == 0x10); }
    private static Window OpenSettings(Grid view)
    {
        ((Button)view.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var settings = Field<Window>("settingsWindow"); settings.UpdateLayout(); return settings;
    }

    private static async Task Run()
    {
        var view = (Grid)window.Content;
        await WaitFor(() => Field<List<MonitorDevice>>("monitors").Count == 3 && !Field<bool>("refreshing"));
        window.UpdateLayout();
        var monitors = Field<List<MonitorDevice>>("monitors");
        var first = monitors[0]; string originalName = first.HardwareName;
        Check(monitors.All(x => x.Width > 0 && x.Height > 0 && !String.IsNullOrWhiteSpace(x.PositionLabel)), "Discovered demo displays include bounds and physical position labels");
        Check(first.Primary && first.PositionLabel == "주 모니터" && monitors[1].PositionLabel.Contains("오른쪽") && monitors[2].PositionLabel.Contains("아래쪽"), "Primary, right and lower displays are identified from their physical layout");
        Check(Descendants<Button>(view).Count(x => AutomationProperties.GetName(x).StartsWith("배치 모니터 ")) == 3, "The main window renders a spatial map of every connected monitor");
        CheckNegativeCoordinateMap(view.Resources);

        var placement = new WidgetPlacement { MonitorId = first.Id, MonitorName = first.Name, ShowBrightness = true };
        Invoke(Field<object>("widgets"), "Add", placement);
        var widget = Application.Current.Windows.Cast<Window>().Single(x => x.Title.StartsWith("LumaDock 위젯"));
        var editor = OpenSettings(view);
        Check(editor.IsVisible && editor.Title == "설정 · LumaDock", "The settings action opens the monitor and brightness settings window");
        Check(Named<CheckBox>(editor, "Windows 시작 시 자동 실행").IsChecked == true && Named<CheckBox>(editor, "트레이에서 시작").IsChecked == true, "Startup settings expose the default automatic and tray-only launch options");
        Named<CheckBox>(editor, "Windows 시작 시 자동 실행").IsChecked = false;
        Named<CheckBox>(editor, "트레이에서 시작").IsChecked = false;
        Check(Settings().StartWithWindows && Settings().StartInTray, "Changing startup checkboxes does not apply their draft values before saving");
        Check(Descendants<Button>(editor).Count(x => AutomationProperties.GetName(x).StartsWith("모니터 식별 ")) == monitors.Count, "Every connected monitor has an explicit identify action");
        var alias = Named<TextBox>(editor, "모니터 이름 DISPLAY1"); alias.Text = "왼쪽 작업";
        Check(first.Name == originalName && placement.MonitorName == originalName && !Settings().Monitors.Any(x => x.Alias == "왼쪽 작업"), "Alias drafts do not change monitor names or widgets before saving");
        Click(editor, "디스플레이 설정 저장"); widget.UpdateLayout();
        Check(first.Name == "왼쪽 작업" && first.HardwareName == originalName, "Saving a custom name preserves the original hardware model");
        Check(!Settings().StartWithWindows && !Settings().StartInTray, "Saving settings applies both startup options together");
        Check(((TextBlock)view.FindName("DeviceTitle")).Text == "왼쪽 작업" && Descendants<Button>(view).Any(x => AutomationProperties.GetName(x) == "왼쪽 작업 모니터 선택"), "Saved monitor names update both the main heading and sidebar");
        Check(placement.MonitorName == "왼쪽 작업" && widget.Title == "LumaDock 위젯 · 왼쪽 작업" && Descendants<TextBlock>(widget).Any(x => x.Text == "왼쪽 작업"), "Saved monitor names update the live widget and its placement label");
        alias.Text = "";
        Named<CheckBox>(editor, "Windows 시작 시 자동 실행").IsChecked = true;
        Named<CheckBox>(editor, "트레이에서 시작").IsChecked = true;
        Click(editor, "디스플레이 설정 저장");
        Check(Settings().StartWithWindows && Settings().StartInTray, "Startup options can be re-enabled through settings");
        Check(first.Name == originalName && first.HardwareName == originalName && !Settings().Monitors.Any(x => x.MonitorId == first.Id), "Clearing a custom name restores the hardware name and removes its alias preference");

        Named<CheckBox>(editor, "키보드 밝기 조절 사용").IsChecked = true;
        Named<CheckBox>(editor, "Windows 밝기 키 연동").IsChecked = true;
        Named<CheckBox>(editor, "기본 밝기 단축키 사용").IsChecked = true;
        Named<ComboBox>(editor, "밝기 조절 대상").SelectedValue = "all";
        Named<Slider>(editor, "밝기 조절 단계").Value = 7;
        Named<CheckBox>(editor, "시작 시 업데이트 확인").IsChecked = false;
        Click(editor, "디스플레이 설정 저장");
        Check(Settings().Brightness.Enabled && Settings().Brightness.FollowWindows && Settings().Brightness.UseHotkeys && Settings().Brightness.Mode == "all" && Settings().Brightness.StepPercent == 7, "The all-monitor keyboard mode, input sources and step save together");
        Named<TextBox>(editor, "모니터 이름 DISPLAY1").Text = "연결 변경 전 초안";
        Named<ComboBox>(editor, "밝기 조절 대상").SelectedValue = "selected";
        Named<CheckBox>(editor, "밝기 대상 DISPLAY2").IsChecked = true;
        Named<Slider>(editor, "밝기 조절 단계").Value = 11;
        Named<CheckBox>(editor, "Windows 시작 시 자동 실행").IsChecked = false;
        Named<CheckBox>(editor, "트레이에서 시작").IsChecked = false;
        Invoke(editor, "RefreshConnectedMonitors", monitors.Where(x => x.Id != "demo-2").ToList()); editor.UpdateLayout();
        Check(Named<TextBox>(editor, "모니터 이름 DISPLAY1").Text == "연결 변경 전 초안" && Named<CheckBox>(editor, "밝기 대상 DISPLAY2").IsChecked == true && !Named<CheckBox>(editor, "밝기 대상 DISPLAY2").IsEnabled, "Disconnecting a monitor preserves unsaved names and selected targets in an offline row");
        await (Task)Invoke(window, "RefreshAsync");
        monitors = Field<List<MonitorDevice>>("monitors"); first = monitors[0]; editor.UpdateLayout();
        Check(Named<TextBox>(editor, "모니터 이름 DISPLAY1").Text == "연결 변경 전 초안" && Named<CheckBox>(editor, "밝기 대상 DISPLAY2").IsChecked == true && Named<CheckBox>(editor, "밝기 대상 DISPLAY2").IsEnabled && Named<Slider>(editor, "밝기 조절 단계").Value == 11, "A real app rescan refreshes the open settings window without discarding drafts");
        Check(!Settings().Monitors.Any(x => x.Alias == "연결 변경 전 초안") && Settings().Brightness.Mode == "all" && Settings().Brightness.StepPercent == 7, "Connection refresh never persists drafts as a side effect");
        Check(Named<CheckBox>(editor, "Windows 시작 시 자동 실행").IsChecked == false && Named<CheckBox>(editor, "트레이에서 시작").IsChecked == false && Settings().StartWithWindows && Settings().StartInTray, "Display reconnection preserves unsaved startup choices without applying them");
        alias = Named<TextBox>(editor, "모니터 이름 DISPLAY1"); alias.Text = "";
        Named<ComboBox>(editor, "밝기 조절 대상").SelectedValue = "all"; Named<Slider>(editor, "밝기 조절 단계").Value = 7;
        Named<CheckBox>(editor, "밝기 대상 DISPLAY2").IsChecked = false; Click(editor, "디스플레이 설정 저장");
        var input = Field<BrightnessInput>("brightnessInput");
        Check(!input.HotkeysAvailable && !input.RawInputAvailable && !input.WindowsEventsAvailable, "Demo settings never register global hotkeys, Raw Input or Windows brightness watchers");
        Invoke(window, "OnBrightnessDelta", 5);
        await WaitFor(() => !Field<bool>("applyingBrightness") && monitors.All(x => Brightness(x).Current == 77));
        Check(monitors.All(x => Brightness(x).Current == 77), "The integrated all-monitor input route changes every demo monitor together");
        Named<ComboBox>(editor, "밝기 조절 대상").SelectedValue = "selected";
        Named<CheckBox>(editor, "밝기 대상 DISPLAY1").IsChecked = true;
        Named<CheckBox>(editor, "밝기 대상 DISPLAY2").IsChecked = false;
        Named<CheckBox>(editor, "밝기 대상 DISPLAY3").IsChecked = false;
        Click(editor, "디스플레이 설정 저장");
        Check(Settings().Brightness.Mode == "selected" && Settings().Brightness.SelectedMonitorIds.SequenceEqual(new[] { first.Id }), "The fixed-monitor mode retains exactly the user's selected target");
        Invoke(window, "OnBrightnessDelta", -4);
        await WaitFor(() => !Field<bool>("applyingBrightness") && Brightness(first).Current == 73);
        Check(Brightness(first).Current == 73 && monitors.Skip(1).All(x => Brightness(x).Current == 77), "The integrated selected-monitor route leaves other displays unchanged");
        Named<ComboBox>(editor, "밝기 조절 대상").SelectedValue = "cursor"; Click(editor, "디스플레이 설정 저장");
        var routed = BrightnessRouting.Targets(monitors, Settings().Brightness, monitors[1].Left + 10, monitors[1].Top + 10);
        Check(Settings().Brightness.Mode == "cursor" && routed.Count == 1 && routed[0] == monitors[1], "The pointer mode uses the monitor's physical rectangle rather than form selection");
        Named<ComboBox>(editor, "밝기 조절 대상").SelectedValue = "selected";
        foreach (var monitor in monitors) Named<CheckBox>(editor, "밝기 대상 " + monitor.Display).IsChecked = false;
        alias.Text = "저장되면 안 되는 이름"; Click(editor, "디스플레이 설정 저장");
        Check(Settings().Brightness.Mode == "cursor" && first.Name == originalName, "A missing fixed target rejects the save without partially changing monitor names");
        Named<CheckBox>(editor, "Windows 시작 시 자동 실행").IsChecked = true;
        Named<CheckBox>(editor, "트레이에서 시작").IsChecked = true;
        editor.Close(); await WaitFor(() => Field<Window>("settingsWindow") == null);
        Check(first.Name == originalName && Settings().Brightness.Mode == "cursor", "Closing invalid or unsaved settings preserves the last saved configuration");
        Check(!Settings().StartWithWindows && !Settings().StartInTray, "Closing settings discards unsaved changes to automatic and tray startup");

        editor = OpenSettings(view); Named<TextBox>(editor, "모니터 이름 DISPLAY1").Text = "작업용"; Click(editor, "디스플레이 설정 저장"); editor.Close();
        var tray = Field<System.Windows.Forms.NotifyIcon>("tray");
        string[] entries = tray.ContextMenuStrip.Items.Cast<System.Windows.Forms.ToolStripItem>().Select(x => x.Text).ToArray();
        Check(entries.Contains("밝기") && entries.Contains("설정") && entries.Contains("업데이트 확인") && entries.Contains("LumaDock 열기"), "The tray exposes brightness, settings, manual updates and explicit app opening");
        window.Hide(); Invoke(window, "ToggleBrightnessFlyout");
        var flyout = Field<Window>("flyout"); await WaitFor(() => flyout.IsVisible); flyout.UpdateLayout();
        Check(!window.IsVisible && flyout.IsVisible, "Opening the brightness flyout leaves the main application hidden");
        var flyoutSliders = Descendants<Slider>(flyout).Where(x => AutomationProperties.GetName(x) == "밝기").ToArray();
        Check(flyoutSliders.Length == monitors.Count && monitors.All(x => Descendants<TextBlock>(flyout).Any(t => t.Text == x.Name)), "The flyout shows every connected monitor's saved name and brightness control");
        Check(Descendants<TextBlock>(flyout).Any(x => x.Text.Contains("DISPLAY2") && x.Text.Contains("오른쪽")), "The flyout also shows monitor identity and physical position");
        flyoutSliders[0].Value = 42;
        await WaitFor(() => Brightness(first).Current == 42 && Named<Slider>(view, "밝기").Value == 42);
        Check(Brightness(first).Current == 42 && !window.IsVisible, "Flyout brightness changes update the monitor and hidden main control without opening the app");
        flyoutSliders[0].Value = 39; Click(flyout, "밝기 팝업 닫기");
        await WaitFor(() => Brightness(first).Current == 39);
        Check(!flyout.IsVisible && !window.IsVisible && Brightness(first).Current == 39, "Closing the flyout flushes a pending brightness change and keeps the app hidden");
        Invoke(window, "ToggleBrightnessFlyout"); await WaitFor(() => flyout.IsVisible); flyout.UpdateLayout();
        Check(Descendants<Slider>(flyout).First(x => AutomationProperties.GetName(x) == "밝기").Value == 39, "Reopening the flyout reads the current shared brightness value");
        Click(flyout, "밝기 설정"); await WaitFor(() => Field<Window>("settingsWindow") != null && Field<Window>("settingsWindow").IsVisible);
        editor = Field<Window>("settingsWindow"); editor.UpdateLayout();
        Check(window.IsVisible && !flyout.IsVisible && editor.IsVisible, "The flyout opens the full settings window only through its explicit settings action");
        Check(Named<Button>(editor, "업데이트 확인").IsEnabled && Named<CheckBox>(editor, "시작 시 업데이트 확인").IsChecked == false, "The settings window provides manual updates and preserves the startup-check preference");
        var updater = Field<AppUpdater>("updater"); await updater.CheckAsync(true);
        Check(Field<Window>(updater, "dialog") == null && updater.CurrentVersion == UpdateProtocol.Normalize(typeof(MainWindow).Assembly.GetName().Version), "Demo startup update checks return without network access or modal dialogs");
        editor.Close();
        results.Add(results.Count + " display feature checks passed in demo mode without native brightness changes, global hooks or update downloads.");
    }

    private static void CheckNegativeCoordinateMap(ResourceDictionary resources)
    {
        var left = new MonitorDevice { Id = "negative-left", Name = "왼쪽", Display = "DISPLAY8", Left = -1920, Top = -200, Width = 1920, Height = 1080 };
        var primary = new MonitorDevice { Id = "origin", Name = "주 화면", Display = "DISPLAY9", Width = 2560, Height = 1440, Primary = true };
        var devices = new List<MonitorDevice> { left, primary };
        typeof(MonitorService).GetMethod("InitializeMetadata", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { devices });
        string identified = null;
        var mapType = typeof(MainWindow).Assembly.GetType("MonitorControl.DisplayMapControl", true);
        var map = (Border)Activator.CreateInstance(mapType, BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { devices, new Action<MonitorDevice>(x => identified = x.Id) }, null);
        var host = new Window { Width = 570, Height = 260, ShowActivated = false, ShowInTaskbar = false, Content = map };
        host.Resources.MergedDictionaries.Add(resources);
        try
        {
            host.Show(); host.UpdateLayout();
            var canvas = (Canvas)map.Child;
            var first = Named<Button>(map, "배치 모니터 DISPLAY8"); var second = Named<Button>(map, "배치 모니터 DISPLAY9");
            Check(Canvas.GetLeft(first) < Canvas.GetLeft(second) && Canvas.GetTop(first) < Canvas.GetTop(second), "Spatial maps preserve negative horizontal and vertical screen coordinates");
            Check(primary.PositionLabel == "주 모니터" && left.PositionLabel.Contains("왼쪽") && canvas.Children.Count == 2, "Primary and negative-coordinate monitor labels remain visible in the map");
            Click(map, "배치 모니터 DISPLAY8");
            Check(identified == left.Id, "Each map tile selects its matching physical monitor identity");
        }
        finally { host.Close(); }
    }
}
