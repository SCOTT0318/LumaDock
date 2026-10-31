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

internal static class UiTests
{
    private static readonly List<string> results = new List<string>();
    private static string output;
    private static MainWindow window;
    private static int exitCode;

    [STAThread]
    public static int Main(string[] args)
    {
        output = args[0];
        var app = new Application();
        window = new MainWindow(true, null);
        window.Loaded += async delegate {
            try { await Run(); }
            catch (Exception ex) { results.Add("FAIL " + ex); exitCode = 1; }
            finally { File.WriteAllLines(output, results); window.Close(); }
        };
        app.Run(window);
        return exitCode;
    }

    private static T Field<T>(string name)
    { return (T)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window); }
    private static T Field<T>(object target, string name)
    { return (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target); }
    private static object Invoke(object target, string method, params object[] args)
    { return target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, args); }
    private static WidgetSettings Settings(object manager)
    { return (WidgetSettings)manager.GetType().GetProperty("Settings", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(manager, null); }
    private static WidgetPlacement Placement(object manager, string monitorId)
    { return Settings(manager).Widgets.Single(x => String.Equals(x.MonitorId, monitorId, StringComparison.OrdinalIgnoreCase)); }
    private static T Named<T>(DependencyObject root, string name) where T : DependencyObject
    { return Descendants<T>(root).Single(x => AutomationProperties.GetName(x) == name); }
    private static void Click(DependencyObject root, string name)
    { Named<Button>(root, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T) yield return (T)child;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Check(bool condition, string name)
    { if (!condition) throw new Exception(name); results.Add("PASS " + name); }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 100; i++) { if (condition()) return; await Task.Delay(50); }
        throw new TimeoutException("UI did not settle.");
    }

    private static async Task Run()
    {
        var view = (Grid)window.Content;
        var list = (StackPanel)view.FindName("MonitorList");
        await WaitFor(() => list.Children.Count == 3 && !Field<bool>("refreshing"));
        window.UpdateLayout();
        var first = Field<MonitorDevice>("selected");
        var brightness = first.Features.First(x => x.Definition.Code == 0x10);
        Check(brightness.Current == 72, "Opening the UI does not write a brightness value");
        Check(Descendants<Slider>(view).Count() == 7, "First monitor renders all supported continuous controls");
        var combo = Descendants<ComboBox>(view).Single(x => AutomationProperties.GetName(x) == "입력 소스");
        Check(combo.Items.Count == 3 && Convert.ToUInt32(combo.SelectedValue) == 15, "Input menu uses reported choices and selects current input without applying it");
        var slider = Descendants<Slider>(view).First(x => AutomationProperties.GetName(x) == "밝기");
        slider.Value = 43;
        await WaitFor(() => brightness.Current == 43);
        Check(brightness.Current == 43, "Slider change is applied after debounce");
        slider.Value = 20; slider.Value = 21; slider.Value = 22;
        await WaitFor(() => brightness.Current == 22);
        Check(brightness.Current == 22, "Rapid slider changes keep the final requested value");
        slider.Value = 17;
        ((Button)list.Children[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        await WaitFor(() => brightness.Current == 17);
        Check(brightness.Current == 17, "Navigating to another monitor flushes its pending change");
        Check(Descendants<Slider>(view).Count() == 3 && Descendants<ComboBox>(view).Single().Items.Count == 2, "Second monitor has a different menu and different input choices");
        ((Button)list.Children[2]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        Check(Descendants<Slider>(view).Count() == 1 && !Descendants<ComboBox>(view).Any(), "Brightness-only monitor has no unsupported controls");
        ((Button)list.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        slider = Descendants<Slider>(view).First(x => AutomationProperties.GetName(x) == "밝기");
        slider.Value = 45;
        ((Button)view.FindName("RefreshButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitFor(() => !Field<bool>("refreshing") && Field<MonitorDevice>("selected") != first);
        Check(first.Features.First(x => x.Definition.Code == 0x10).Current == 17, "Rescan cancels pending UI changes before invalidating a connection");
        Check(Field<MonitorDevice>("selected").Features.First(x => x.Definition.Code == 0x10).Current == 72, "Rescan starts from newly queried values");
        window.UpdateLayout();
        var beforePreset = Field<MonitorDevice>("selected");
        var preset = Descendants<ComboBox>(view).Single(x => AutomationProperties.GetName(x) == "색상 프리셋");
        preset.SelectedValue = (uint)8;
        Check(beforePreset.Features.First(x => x.Definition.Code == 0x14).Current == 5, "Selecting a color preset alone does not apply it");
        Descendants<Button>(view).Single(x => AutomationProperties.GetName(x) == "색상 프리셋 적용").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitFor(() => !Field<bool>("refreshing") && Field<MonitorDevice>("selected") != beforePreset);
        Check(beforePreset.Features.First(x => x.Definition.Code == 0x14).Current == 8, "Applying a color preset refreshes dependent monitor controls");
        Check(window.Title.StartsWith("LumaDock") && window.Icon != null, "LumaDock has its own monitor window icon");
        var tray = Field<System.Windows.Forms.NotifyIcon>("tray");
        Check(tray.Visible && tray.Text.StartsWith("LumaDock") && tray.ContextMenuStrip.Items.Cast<System.Windows.Forms.ToolStripItem>().Any(x => x.Text == "위젯 설정"), "System tray shows the monitor icon and widget actions");
        ((Button)view.FindName("WidgetButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var manager = Field<object>("widgets");
        var editor = Field<Window>(manager, "editor"); editor.UpdateLayout();
        Check(editor.IsVisible && Named<ComboBox>(editor, "위젯 대상 모니터").Items.Count == 3, "Widget settings lists connected monitors");
        Named<CheckBox>(editor, "명암 표시").IsChecked = true;
        Named<CheckBox>(editor, "항상 위에 표시").IsChecked = true;
        Click(editor, "위젯 추가");
        await WaitFor(() => Application.Current.Windows.Cast<Window>().Count(x => x.Title.StartsWith("LumaDock 위젯")) == 1);
        var widget = Application.Current.Windows.Cast<Window>().Single(x => x.Title.StartsWith("LumaDock 위젯")); widget.UpdateLayout();
        Check(Descendants<Slider>(widget).Count() == 2 && widget.Topmost && !widget.ShowInTaskbar, "Placed widget exposes selected controls and topmost option without a taskbar button");
        editor.UpdateLayout();
        Check(Settings(manager).Widgets.Count == 1 && Named<Button>(editor, "변경 저장").IsEnabled && !Descendants<Button>(editor).Any(x => AutomationProperties.GetName(x) == "위젯 추가"), "Adding a widget switches its monitor to editing instead of allowing another add");
        bool duplicateRejected = false;
        try { Invoke(manager, "Add", new WidgetPlacement { MonitorId = "DEMO-1", MonitorName = "duplicate", ShowBrightness = false, ShowContrast = true }); }
        catch (TargetInvocationException ex) { if (ex.InnerException is InvalidOperationException) duplicateRejected = true; else throw; }
        Check(duplicateRejected && Settings(manager).Widgets.Count == 1 && Application.Current.Windows.Cast<Window>().Count(x => x.Title.StartsWith("LumaDock 위젯")) == 1, "Backend rejects a duplicate monitor even with different selected controls and casing");
        var currentDevice = Field<MonitorDevice>("selected");
        var pendingInput = Named<ComboBox>(view, "입력 소스"); pendingInput.SelectedValue = (uint)17;
        var widgetBrightness = Named<Slider>(widget, "밝기"); widgetBrightness.Value = 38;
        await WaitFor(() => currentDevice.Features.First(x => x.Definition.Code == 0x10).Current == 38 && Named<Slider>(view, "밝기").Value == 38);
        Check(Named<Slider>(view, "밝기").Value == 38, "Widget changes synchronize the main monitor control");
        Check(Convert.ToUInt32(pendingInput.SelectedValue) == 17 && currentDevice.Features.First(x => x.Definition.Code == 0x60).Current == 15, "Unrelated widget changes preserve an unapplied input selection");
        pendingInput.SelectedValue = (uint)15;
        Named<Slider>(view, "밝기").Value = 64;
        await WaitFor(() => widgetBrightness.Value == 64);
        Check(widgetBrightness.Value == 64, "Main monitor changes synchronize the widget");
        widgetBrightness.Value = 29;
        Click(editor, currentDevice.Name + " 위젯 수정");
        Click(editor, "라이트 테마");
        Named<CheckBox>(editor, "위치 잠금").IsChecked = true;
        Click(editor, "변경 저장");
        await WaitFor(() => currentDevice.Features.First(x => x.Definition.Code == 0x10).Current == 29);
        Check(currentDevice.Features.First(x => x.Definition.Code == 0x10).Current == 29, "Editing widget appearance flushes a pending control change");
        widget.UpdateLayout();
        var settingsButton = Named<Button>(widget, "위젯 설정");
        var foreground = ((SolidColorBrush)settingsButton.Foreground).Color;
        Check(foreground.R < 120 && foreground.G < 140, "Light widgets retain readable settings buttons");
        Check(Descendants<TextBlock>(widget).Any(x => x.Text == "위치 잠김"), "Lock setting is applied to the placed widget");
        editor.UpdateLayout();
        Named<CheckBox>(editor, "DELL U2723QE 위치 잠금").IsChecked = false; widget.UpdateLayout();
        Check(!Placement(manager, "demo-1").Locked && !Descendants<TextBlock>(widget).Any(x => x.Text == "위치 잠김"), "List lock toggle immediately updates the saved placement and live widget");
        Named<CheckBox>(editor, "DELL U2723QE 항상 위에 표시").IsChecked = false;
        Check(!Placement(manager, "demo-1").AlwaysOnTop && !widget.Topmost, "List topmost toggle immediately updates the saved placement and live window");
        Named<CheckBox>(editor, "DELL U2723QE 위치 잠금").IsChecked = true;
        Named<CheckBox>(editor, "DELL U2723QE 항상 위에 표시").IsChecked = true;
        Click(editor, "DELL U2723QE 위젯 수정");
        Named<CheckBox>(editor, "위치 잠금").IsChecked = false;
        Named<CheckBox>(editor, "항상 위에 표시").IsChecked = false;
        Click(editor, "다크 테마");
        Check(Placement(manager, "demo-1").Locked && Placement(manager, "demo-1").AlwaysOnTop && Placement(manager, "demo-1").Theme == "light", "Unapplied appearance edits do not mutate the saved widget");
        Click(editor, "위젯 설정 되돌리기");
        Check(Named<CheckBox>(editor, "위치 잠금").IsChecked == true && Named<CheckBox>(editor, "항상 위에 표시").IsChecked == true && Field<string>(editor, "theme") == "light", "Revert reloads the saved lock, topmost and theme options");
        var selector = Named<ComboBox>(editor, "위젯 대상 모니터"); selector.SelectedValue = "demo-2";
        Check(Named<Button>(editor, "위젯 추가").IsEnabled, "Choosing an unconfigured monitor presents a new-widget form");
        Click(editor, "위젯 추가"); editor.UpdateLayout();
        Check(Settings(manager).Widgets.Count == 2 && Named<Button>(editor, "변경 저장").IsEnabled, "Different physical monitors can each have one independently editable widget");
        duplicateRejected = false;
        var firstPlacement = Placement(manager, "demo-1");
        try { Invoke(manager, "Update", new WidgetPlacement { Id = firstPlacement.Id, MonitorId = "demo-2", MonitorName = "invalid reassignment", ShowBrightness = false, ShowContrast = true }); }
        catch (TargetInvocationException ex) { if (ex.InnerException is InvalidOperationException) duplicateRejected = true; else throw; }
        Check(duplicateRejected && Settings(manager).Widgets.Count == 2 && Placement(manager, "demo-1").Id == firstPlacement.Id && Placement(manager, "demo-1").Theme == "light", "Conflicting widget updates are rejected before changing the saved placement");
        Click(widget, "위젯 설정"); editor.UpdateLayout();
        Check((string)selector.SelectedValue == "demo-1" && Named<CheckBox>(editor, "명암 표시").IsChecked == true, "Widget settings button selects that widget even when another monitor was open");
        selector.SelectedValue = "demo-1";
        Click(editor, "LG ULTRAGEAR 위젯 수정");
        Check((string)selector.SelectedValue == "demo-2", "Editing a widget selects its own monitor rather than the previous form monitor");
        var internalSelector = Named<ComboBox>(editor, "위젯 대상 모니터"); internalSelector.SelectedValue = "demo-3";
        Check(!Named<CheckBox>(editor, "명암 표시").IsEnabled && !Named<CheckBox>(editor, "볼륨 표시").IsEnabled, "Unsupported widget controls are disabled per monitor");
        Check(Settings(manager).Widgets.Count == 2 && !Settings(manager).Widgets.Any(x => x.MonitorId == "demo-3"), "Switching form monitors does not retarget an existing widget");
        editor.Close();
        widget.Left = -50000; widget.Top = -50000; Invoke(widget, "EnsureOnScreen");
        Check(widget.Left > -10000 && widget.Top > -10000, "Off-screen widget placements are restored to an available display");
        var boundBeforeRescan = currentDevice;
        ((Button)view.FindName("RefreshButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitFor(() => !Field<bool>("refreshing") && Field<MonitorDevice>("selected") != boundBeforeRescan);
        widget.UpdateLayout();
        Check(Descendants<Slider>(widget).Count() == 2 && Named<Slider>(widget, "밝기").IsEnabled, "Rescan rebinds and re-enables existing widget controls");
        Invoke(manager, "Rebind", new List<MonitorDevice>()); widget.UpdateLayout();
        Check(!Descendants<Slider>(widget).Any(), "Disconnected monitors leave an offline widget without active controls");
        Click(widget, "위젯 설정");
        editor = Field<Window>(manager, "editor"); editor.UpdateLayout();
        Check((string)Named<ComboBox>(editor, "위젯 대상 모니터").SelectedValue == "demo-1" && Named<Button>(editor, "변경 저장").IsEnabled, "Disconnected widget still opens an editable appearance form");
        Check(Named<CheckBox>(editor, "밝기 표시").IsChecked == true && Named<CheckBox>(editor, "명암 표시").IsChecked == true, "Offline editing retains the saved control selection");
        Named<CheckBox>(editor, "위치 잠금").IsChecked = false;
        Named<CheckBox>(editor, "항상 위에 표시").IsChecked = false;
        Click(editor, "다크 테마"); Click(editor, "변경 저장");
        var offlinePlacement = Placement(manager, "demo-1");
        Check(!offlinePlacement.Locked && !offlinePlacement.AlwaysOnTop && offlinePlacement.Theme == "dark" && offlinePlacement.ShowBrightness && offlinePlacement.ShowContrast && !widget.Topmost, "Offline ancillary edits apply without discarding saved controls");
        editor.Close();
        Invoke(manager, "Rebind", Field<List<MonitorDevice>>("monitors")); widget.UpdateLayout();
        Check(Descendants<Slider>(widget).Count() == 2, "A reconnected monitor restores its widget controls");
        Invoke(manager, "ToggleVisibility");
        Check(!widget.IsVisible, "Tray widget toggle hides placed widgets");
        Invoke(manager, "ToggleVisibility");
        Check(widget.IsVisible, "Tray widget toggle restores placed widgets");
        ((Button)view.FindName("WidgetButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        editor = Field<Window>(manager, "editor"); editor.UpdateLayout();
        Click(editor, "DELL U2723QE 위젯 삭제");
        Check(!Application.Current.Windows.Cast<Window>().Any(x => x.Title == "LumaDock 위젯 · DELL U2723QE"), "Removing a widget closes its floating window");
        editor.Close();
        ((Button)view.FindName("TrayButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!window.IsVisible, "Tray action hides the window");
        window.Show(); window.UpdateLayout();
        Check(window.IsVisible, "Hidden window can be restored");
        window.Width = window.MinWidth; window.Height = window.MinHeight; window.UpdateLayout();
        Check(((TextBlock)view.FindName("DeviceTitle")).ActualWidth > 200, "Minimum window size preserves room for monitor identity");
        results.Add(results.Count + " UI checks passed in demo mode. No physical monitor values were changed.");
    }
}
