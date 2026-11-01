using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace MonitorControl
{
    public sealed class MainWindow : Window
    {
        private readonly MonitorService service;
        private readonly bool demo;
        private readonly Grid view;
        private readonly List<FeatureRow> rows = new List<FeatureRow>();
        private List<MonitorDevice> monitors = new List<MonitorDevice>();
        private MonitorDevice selected;
        private Forms.NotifyIcon tray;
        private System.Drawing.Icon trayIcon;
        private readonly WidgetManager widgets;
        private readonly BrightnessInput brightnessInput;
        private readonly AppUpdater updater;
        private readonly BrightnessFlyout flyout;
        private DisplaySettingsWindow settingsWindow;
        private readonly Dictionary<MonitorDevice, int> pendingBrightness = new Dictionary<MonitorDevice, int>();
        private bool applyingBrightness, refreshNativeBrightness;
        private readonly DispatcherTimer brightnessTimer;
        private readonly DispatcherTimer displayTimer;
        private bool refreshing, refreshAgain, closing, allowClose, exitRequested;
        private readonly string previewDirectory;
        private readonly bool startupToTray;
        private bool startupInitialized;
        public bool StartupToTray { get { return startupToTray; } }

        public MainWindow(bool demoMode, string preview, bool startInTray = false)
        {
            demo = demoMode;
            previewDirectory = preview;
            service = new MonitorService(demo);
            Title = "LumaDock" + (demo ? " · 데모" : "");
            Width = 1160; Height = 820; MinWidth = 980; MinHeight = 700;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Brush("#0E141B");
            FontFamily = new FontFamily("Segoe UI, Malgun Gothic");
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MonitorControl.MainView.xaml"))
                view = (Grid)XamlReader.Load(stream);
            Content = view;
            widgets = new WidgetManager(this, service, view.Resources, RefreshAsync, SetStatus, !demo && String.IsNullOrEmpty(previewDirectory));
            startupToTray = startInTray || (!demo && String.IsNullOrEmpty(previewDirectory) && widgets.Settings.StartInTray);
            brightnessInput = new BrightnessInput(this, OnBrightnessDelta, SetStatus, demo);
            brightnessTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            brightnessTimer.Tick += delegate { brightnessTimer.Stop(); if (!applyingBrightness) ApplyPendingBrightness(); };
            updater = new AppUpdater(this, demo, RequestExit);
            flyout = new BrightnessFlyout(service, view.Resources, SetStatus, RefreshAsync, ShowWindow, ShowDisplaySettings);
            service.FeatureChanged += OnFeatureChanged;
            service.FeatureChanging += OnFeatureChanging;
            AppIdentity.Hook(this, ShowWindow); AppIdentity.DarkTitlebar(this);
            Element<Button>("RefreshButton").Click += async delegate { await RefreshAsync(); };
            Element<Button>("TrayButton").Click += delegate { Hide(); };
            Element<Button>("WidgetButton").Click += delegate { widgets.ShowManager(selected); };
            Element<Button>("SettingsButton").Click += delegate { ShowDisplaySettings(); };
            if (demo)
            {
                Element<TextBlock>("ModeBadge").Text = "DEMO";
                Element<TextBlock>("FooterLabel").Text = "LumaDock · DEMO";
            }
            displayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            displayTimer.Tick += async delegate { displayTimer.Stop(); await RefreshAsync(); };
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            SystemEvents.PowerModeChanged += OnPowerChanged;
            SetupTray();
            Loaded += async delegate { await InitializeAsync(); };
            Closing += OnClosing;
        }

        public void StartBackground()
        {
            new WindowInteropHelper(this).EnsureHandle();
            Dispatcher.BeginInvoke(new Action(async delegate { await InitializeAsync(); }));
        }

        private async Task InitializeAsync()
        {
            if (startupInitialized || closing) return;
            startupInitialized = true;
            string startupError = null;
            if (!demo)
            {
                try { ApplyStartupRegistration(); }
                catch (Exception ex) { startupError = "Windows 자동 실행 설정 실패 · " + ex.Message; }
            }
            await RefreshAsync();
            if (closing) return;
            if (startupError != null) SetStatus(startupError);
            if (!String.IsNullOrEmpty(previewDirectory)) await RenderPreviews();
            else if (!demo && widgets.Settings.CheckUpdatesOnStartup) await updater.CheckAsync(true, !startupToTray);
        }

        private void ApplyStartupRegistration()
        {
            if (!demo) StartupRegistration.SetEnabled(widgets.Settings.StartWithWindows, Assembly.GetExecutingAssembly().Location);
        }

        private T Element<T>(string name) where T : FrameworkElement { return (T)view.FindName(name); }
        internal static SolidColorBrush Brush(string hex) { return (SolidColorBrush)new BrushConverter().ConvertFromString(hex); }
        internal static TextBlock Text(string text, double size, string color)
        { return new TextBlock { Text = text, FontSize = size, Foreground = Brush(color) }; }
        private void SetStatus(string value) { Element<TextBlock>("StatusText").Text = value; }

        private async Task RefreshAsync()
        {
            if (closing) return;
            if (refreshing) { refreshAgain = true; return; }
            refreshing = true;
            string oldId = selected == null ? null : selected.Id;
            foreach (var row in rows) row.Stop();
            brightnessTimer.Stop(); pendingBrightness.Clear(); refreshNativeBrightness = false; flyout.Suspend();
            widgets.BeforeScan();
            Element<Button>("RefreshButton").IsEnabled = false;
            Element<Button>("WidgetButton").IsEnabled = false;
            Element<StackPanel>("MonitorList").IsEnabled = false;
            Element<StackPanel>("FeaturePanel").IsEnabled = false;
            SetStatus("모니터 검색 중");
            try
            {
                monitors = await service.ScanAsync();
                if (closing) return;
                ApplyDisplayNames();
                selected = monitors.FirstOrDefault(x => x.Id == oldId) ?? monitors.FirstOrDefault(x => x.Primary) ?? monitors.FirstOrDefault();
                RenderMonitorList(); RenderFeatures();
                widgets.Rebind(monitors);
                ConfigureBrightnessInput();
                if (settingsWindow != null) settingsWindow.RefreshConnectedMonitors(monitors);
                if (flyout.IsVisible) flyout.Bind(monitors);
                SetStatus(demo ? "데모" : "모니터 " + monitors.Count + "개 연결됨");
            }
            catch (Exception ex)
            {
                monitors.Clear(); selected = null;
                if (!closing)
                {
                    RenderMonitorList(); RenderFeatures(); widgets.Rebind(monitors);
                    if (settingsWindow != null) settingsWindow.RefreshConnectedMonitors(monitors);
                    if (flyout.IsVisible) flyout.Bind(monitors);
                    SetStatus("검색 실패 · " + ex.Message);
                }
            }
            finally
            {
                refreshing = false;
                Element<Button>("RefreshButton").IsEnabled = true;
                Element<Button>("WidgetButton").IsEnabled = true;
                Element<StackPanel>("MonitorList").IsEnabled = true;
                Element<StackPanel>("FeaturePanel").IsEnabled = true;
                if (refreshAgain && !closing) { refreshAgain = false; displayTimer.Stop(); displayTimer.Start(); }
            }
        }

        private void RenderMonitorList()
        {
            var list = Element<StackPanel>("MonitorList"); list.Children.Clear();
            Element<TextBlock>("MonitorCount").Text = monitors.Count.ToString("00");
            for (int i = 0; i < monitors.Count; i++)
            {
                var device = monitors[i];
                bool active = device == selected;
                var contents = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
                var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
                var number = Text((i + 1).ToString("00"), 10, active ? "#85E0CD" : "#697D8C");
                DockPanel.SetDock(number, Dock.Right); heading.Children.Add(number);
                var icon = new Border { Width = 29, Height = 19, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1.5), BorderBrush = Brush(active ? "#85E0CD" : "#6D8190"), HorizontalAlignment = HorizontalAlignment.Left };
                icon.Child = new Border { Background = Brush(active ? "#375B54" : "#29343E"), Margin = new Thickness(3), CornerRadius = new CornerRadius(1) };
                heading.Children.Add(icon); contents.Children.Add(heading);
                var name = Text(device.Name, 13, active ? "#EDF9F6" : "#CBD8E2"); name.FontWeight = FontWeights.SemiBold; name.TextTrimming = TextTrimming.CharacterEllipsis;
                contents.Children.Add(name);
                contents.Children.Add(new TextBlock { Text = device.Display + "  ·  " + device.PositionLabel, Foreground = Brush("#8A9EAD"), FontSize = 10, Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
                var button = new Button { Content = contents, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(15), Margin = new Thickness(0, 0, 0, 10),
                    Background = Brush(active ? "#203630" : "#1B242E"), BorderBrush = Brush(active ? "#65B6A6" : "#2B3744"), ToolTip = device.Name + " · " + device.Resolution };
                AutomationProperties.SetName(button, device.Name + " 모니터 선택");
                button.Click += delegate {
                    SelectMonitor(device);
                };
                list.Children.Add(button);
            }
        }

        private void RenderFeatures()
        {
            foreach (var row in rows) row.Stop(); rows.Clear();
            var panel = Element<StackPanel>("FeaturePanel"); panel.Children.Clear();
            if (selected == null)
            {
                Element<TextBlock>("DeviceTitle").Text = "연결된 모니터가 없습니다";
                Element<TextBlock>("DeviceSubtitle").Text = "모니터를 연결한 다음 다시 검색해 주세요";
                Element<TextBlock>("ConnectionText").Text = "연결 대기";
                panel.Children.Add(Note("연결을 확인해 주세요", "원격 데스크톱이나 일부 가상 디스플레이에서는 물리 모니터를 제어할 수 없습니다. 직접 연결된 Windows 화면에서 실행해 주세요.", false));
                return;
            }
            Element<TextBlock>("DeviceTitle").Text = selected.Name;
            Element<TextBlock>("DeviceSubtitle").Text = (selected.Name != selected.HardwareName ? selected.HardwareName + "  ·  " : "") + selected.Display + "  ·  " + selected.Resolution + "  ·  " + selected.PositionLabel;
            Element<TextBlock>("ConnectionText").Text = demo ? "데모 모니터" : selected.Features.Any(x => x.Backend == "WMI") ? "Windows 밝기" : selected.Features.Count > 0 ? "DDC/CI 연결" : "제어 미지원";
            var map = new DisplayMapControl(monitors, SelectMonitor) { Height = 140, Margin = new Thickness(0, 0, 0, 14) };
            map.SetSelected(selected.Id); panel.Children.Add(map);
            var basic = selected.Features.Where(x => x.Definition.Basic).ToList();
            if (basic.Count > 0)
            {
                panel.Children.Add(Section("기본 설정", ""));
                foreach (var feature in basic) AddFeature(panel, feature);
            }
            else panel.Children.Add(Note("밝기·명암을 사용할 수 없습니다", "현재 연결에서 기본 제어 값을 읽지 못했습니다. 모니터 메뉴에서 DDC/CI를 켜고 다시 검색해 주세요. HDR이나 화면 모드에 따라 항목이 잠길 수도 있습니다.", true));
            var extra = selected.Features.Where(x => !x.Definition.Basic).ToList();
            if (extra.Count > 0)
            {
                panel.Children.Add(Section("추가 설정", ""));
                foreach (var feature in extra) AddFeature(panel, feature);
            }
            if (!String.IsNullOrEmpty(selected.Notice)) panel.Children.Add(Note("연결 안내", selected.Notice, true));
            if (selected.Reported.Count > 0)
            {
                var details = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
                int unknown = selected.Reported.Keys.Count(x => !FeatureCatalog.All.Any(f => f.Code == x));
                var text = Text("보고된 기능 " + selected.Reported.Count + "개 · 조작 메뉴 " + selected.Features.Count + "개\n" +
                    "VCP " + String.Join("  ", selected.Reported.Keys.OrderBy(x => x).Select(x => x.ToString("X2"))) +
                    (unknown > 0 ? "\n제조사 전용·미등록 항목 " + unknown + "개는 정보만 표시합니다." : ""), 11, "#8DA3B2");
                text.TextWrapping = TextWrapping.Wrap; text.LineHeight = 20; details.Children.Add(text);
                var expander = new Expander { Header = "지원 기능 정보", Foreground = Brush("#8DA3B2"), FontSize = 11, Content = details, Margin = new Thickness(3, 14, 0, 15) };
                panel.Children.Add(expander);
            }
        }

        private void AddFeature(Panel panel, MonitorFeature feature)
        {
            var row = new FeatureRow(service, selected, feature, SetStatus, RefreshAsync);
            rows.Add(row); panel.Children.Add(row);
        }

        private static FrameworkElement Section(string title, string detail)
        {
            var grid = new Grid { Margin = new Thickness(2, 8, 2, 13) };
            grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = Text(title, 12, "#C4D3DE"); label.FontWeight = FontWeights.SemiBold; grid.Children.Add(label);
            var hint = Text(detail, 10, "#718896"); hint.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(hint, 1); grid.Children.Add(hint);
            return grid;
        }

        private static FrameworkElement Note(string title, string description, bool compact)
        {
            var stack = new StackPanel();
            var heading = Text(title, 13, "#B7D1D6"); heading.FontWeight = FontWeights.SemiBold; stack.Children.Add(heading);
            var body = Text(description, 11, "#8EA6B4"); body.TextWrapping = TextWrapping.Wrap; body.LineHeight = 20; body.Margin = new Thickness(0, 8, 0, 0); stack.Children.Add(body);
            return new Border { Child = stack, Background = Brush("#18232B"), BorderBrush = Brush("#2A3B46"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(compact ? 17 : 26), Margin = new Thickness(0, 5, 0, 14) };
        }

        private void SetupTray()
        {
            trayIcon = AppIdentity.LoadIcon();
            tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "LumaDock" + (demo ? " (Demo)" : ""), Visible = String.IsNullOrEmpty(previewDirectory) };
            var menu = new Forms.ContextMenuStrip { BackColor = System.Drawing.Color.FromArgb(23, 34, 45), ForeColor = System.Drawing.Color.FromArgb(232, 242, 248), Renderer = new Forms.ToolStripProfessionalRenderer(new TrayColors()) };
            menu.Items.Add("LumaDock 열기", null, delegate { ShowWindow(); });
            menu.Items.Add("밝기", null, delegate { ToggleBrightnessFlyout(); });
            menu.Items.Add("설정", null, delegate { ShowDisplaySettings(); });
            menu.Items.Add("업데이트 확인", null, async delegate { await updater.CheckAsync(); });
            menu.Items.Add("위젯 설정", null, delegate { ShowWindow(); widgets.ShowManager(selected); });
            menu.Items.Add("위젯 표시 / 숨기기", null, delegate { widgets.ToggleVisibility(); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("모니터 다시 검색", null, async delegate { await RefreshAsync(); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("종료", null, delegate { RequestExit(); });
            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate(object sender, Forms.MouseEventArgs e) { if (e.Button == Forms.MouseButtons.Left) ToggleBrightnessFlyout(); };
            Icon = AppIdentity.WindowIcon();
        }

        private sealed class TrayColors : Forms.ProfessionalColorTable
        {
            public override System.Drawing.Color ToolStripDropDownBackground { get { return System.Drawing.Color.FromArgb(23, 34, 45); } }
            public override System.Drawing.Color ImageMarginGradientBegin { get { return ToolStripDropDownBackground; } }
            public override System.Drawing.Color ImageMarginGradientMiddle { get { return ToolStripDropDownBackground; } }
            public override System.Drawing.Color ImageMarginGradientEnd { get { return ToolStripDropDownBackground; } }
            public override System.Drawing.Color MenuBorder { get { return System.Drawing.Color.FromArgb(55, 78, 92); } }
            public override System.Drawing.Color MenuItemSelected { get { return System.Drawing.Color.FromArgb(40, 70, 70); } }
            public override System.Drawing.Color MenuItemBorder { get { return System.Drawing.Color.FromArgb(90, 154, 140); } }
        }

        private void OnFeatureChanged(MonitorDevice device, MonitorFeature feature)
        {
            if (closing || Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(new Action(delegate {
                if (closing) return;
                if (selected == device) foreach (var row in rows) row.RefreshFromModel(feature);
                widgets.RefreshValues(device, feature);
                flyout.RefreshValues(feature);
            }));
        }

        private void SelectMonitor(MonitorDevice device)
        {
            foreach (var row in rows) row.Stop(true);
            selected = device; RenderMonitorList(); RenderFeatures(); SetStatus(device.Name);
        }

        private void ApplyDisplayNames()
        {
            BrightnessRouting.ApplyAliases(monitors, widgets.Settings.Monitors);
            foreach (var placement in widgets.Settings.Widgets)
            {
                var monitor = monitors.FirstOrDefault(x => String.Equals(x.Id, placement.MonitorId, StringComparison.OrdinalIgnoreCase));
                if (monitor != null) placement.MonitorName = monitor.Name;
            }
        }

        private void ShowDisplaySettings()
        {
            ShowWindow();
            if (settingsWindow != null) { settingsWindow.Activate(); return; }
            settingsWindow = new DisplaySettingsWindow(view.Resources, monitors, widgets.Settings, delegate {
                ApplyStartupRegistration();
                ApplyDisplayNames(); widgets.Save(); RenderMonitorList(); RenderFeatures(); widgets.Rebind(monitors);
                brightnessTimer.Stop(); pendingBrightness.Clear(); refreshNativeBrightness = false; ConfigureBrightnessInput();
                if (flyout.IsVisible) flyout.Bind(monitors);
                var inputSettings = widgets.Settings.Brightness;
                SetStatus(!demo && inputSettings.Enabled && inputSettings.UseHotkeys && !brightnessInput.HotkeysAvailable
                    ? "설정 저장됨 · 다른 앱이 단축키를 사용 중이어서 등록하지 못했습니다."
                    : "설정 저장됨");
            }, IdentifyMonitor, async delegate { await updater.CheckAsync(); }) { Owner = this };
            settingsWindow.Closed += delegate { settingsWindow = null; };
            settingsWindow.Show();
        }

        private void IdentifyMonitor(string id)
        {
            var monitor = monitors.FirstOrDefault(x => x.Id == id);
            if (monitor == null || monitor.Width <= 0) { SetStatus("모니터 위치 정보를 확인할 수 없습니다."); return; }
            new MonitorIdentifier(monitor, monitors.IndexOf(monitor) + 1).Show();
        }

        private async void ToggleBrightnessFlyout()
        {
            if (closing) return;
            if (flyout.IsVisible) { flyout.HidePopup(); return; }
            flyout.Bind(monitors); flyout.ShowPopup();
            try { await service.RefreshBrightnessAsync(); }
            catch (Exception ex) { SetStatus("밝기 조회 실패 · " + ex.Message); }
        }

        private void ConfigureBrightnessInput()
        {
            var preferences = widgets.Settings.Brightness;
            brightnessInput.Configure(preferences.Enabled, preferences.FollowWindows, preferences.StepPercent, ModifierKeys.Control | ModifierKeys.Alt, Key.Up, Key.Down, preferences.UseHotkeys);
        }

        private void OnFeatureChanging(MonitorDevice monitor, MonitorFeature feature, uint value)
        {
            if (feature.Definition.Code == 0x10 && feature.Backend == "WMI") brightnessInput.ExpectWindowsBrightness(monitor.WmiInstance, (int)value);
        }

        private void OnBrightnessDelta(int delta)
        {
            if (closing || refreshing || delta == 0) return;
            string source = brightnessInput.SourceKind, sourceInstance = brightnessInput.WindowsSourceInstance;
            // The Windows event already contains its panel's actual value. Querying
            // WMI again before sending the external writes adds visible lag.
            if (source == "windows" && brightnessInput.WindowsSourceBrightness.HasValue)
            {
                foreach (var monitor in monitors.Where(x => String.Equals(x.WmiInstance, sourceInstance, StringComparison.OrdinalIgnoreCase)))
                {
                    var feature = monitor.Features.FirstOrDefault(x => x.Definition.Code == 0x10 && x.Backend == "WMI");
                    if (feature == null) continue;
                    feature.Current = feature.FromPercent(brightnessInput.WindowsSourceBrightness.Value);
                    OnFeatureChanged(monitor, feature);
                }
            }
            var point = Forms.Control.MousePosition;
            var targets = BrightnessRouting.Targets(monitors, widgets.Settings.Brightness, point.X, point.Y);
            foreach (var monitor in targets)
            {
                var feature = monitor.Features.First(x => x.Definition.Code == 0x10);
                // Native Windows/firmware already changes its own panel. Never add
                // the same delta to that panel a second time.
                if (feature.Backend == "WMI" && (source == "raw" || (source == "windows" && String.Equals(monitor.WmiInstance, sourceInstance, StringComparison.OrdinalIgnoreCase))))
                { pendingBrightness.Remove(monitor); continue; }
                int desired;
                if (!pendingBrightness.TryGetValue(monitor, out desired)) desired = feature.Percent(feature.Current);
                pendingBrightness[monitor] = Math.Max(0, Math.Min(100, desired + delta));
            }
            if (source == "raw") refreshNativeBrightness = true;
            // Accumulate a short burst into one latest value per monitor. Do not
            // restart the timer on every event: held keys must still make progress.
            if (!applyingBrightness && !brightnessTimer.IsEnabled) brightnessTimer.Start();
        }

        private async void ApplyPendingBrightness()
        {
            applyingBrightness = true;
            try
            {
                while (!closing && !refreshing && (pendingBrightness.Count > 0 || refreshNativeBrightness))
                {
                    if (pendingBrightness.Count > 0)
                    {
                        var batch = new Dictionary<MonitorDevice, int>(pendingBrightness);
                        var results = await service.SetBrightnessAsync(batch);
                        foreach (var result in results)
                        {
                            if (result.Error != null) SetStatus(result.Device.Name + " · 밝기 변경 실패 · " + result.Error.Message);
                            int current;
                            if (pendingBrightness.TryGetValue(result.Device, out current) && current == batch[result.Device]) pendingBrightness.Remove(result.Device);
                        }
                    }
                    // Native readback is for UI synchronization only; external
                    // monitor writes always get to start first.
                    if (refreshNativeBrightness && pendingBrightness.Count == 0)
                    { refreshNativeBrightness = false; await service.RefreshBrightnessAsync(true); }
                }
            }
            catch (Exception ex) { SetStatus("밝기 조회 실패 · " + ex.Message); pendingBrightness.Clear(); }
            finally { applyingBrightness = false; }
        }

        private void ShowWindow() { Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); }
        private void OnDisplayChanged(object sender, EventArgs e) { ScheduleScan(); }
        private void OnPowerChanged(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) ScheduleScan(); }
        private void ScheduleScan()
        {
            if (demo || Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(new Action(delegate { if (!closing) { displayTimer.Stop(); displayTimer.Start(); } }));
        }

        private async void OnClosing(object sender, CancelEventArgs e)
        {
            if (allowClose) return;
            e.Cancel = true;
            if (!exitRequested && !demo && String.IsNullOrEmpty(previewDirectory)) { Hide(); return; }
            if (closing) return;
            closing = true;
            brightnessTimer.Stop(); pendingBrightness.Clear(); brightnessInput.Dispose(); updater.Dispose(); flyout.Shutdown();
            if (settingsWindow != null) settingsWindow.Close();
            foreach (var row in rows) row.Stop();
            displayTimer.Stop(); IsEnabled = false; SetStatus("모니터 연결을 정리하고 있습니다…");
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged; SystemEvents.PowerModeChanged -= OnPowerChanged;
            widgets.Dispose(); service.FeatureChanged -= OnFeatureChanged; service.FeatureChanging -= OnFeatureChanging;
            if (tray != null) { tray.Visible = false; tray.ContextMenuStrip.Dispose(); tray.Dispose(); trayIcon.Dispose(); }
            await Task.Run(delegate { service.Dispose(); });
            allowClose = true; Close();
        }

        public void RequestExit() { exitRequested = true; Close(); }

        private async Task RenderPreviews()
        {
            Directory.CreateDirectory(previewDirectory);
            for (int i = 0; i < monitors.Count; i++)
            {
                selected = monitors[i]; RenderMonitorList(); RenderFeatures();
                UpdateLayout(); await Task.Delay(150);
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(view);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(Path.Combine(previewDirectory, "demo-" + (i + 1) + ".png"))) encoder.Save(file);
            }
            selected = monitors[0]; RenderMonitorList(); RenderFeatures();
            widgets.Add(new WidgetPlacement { MonitorId = selected.Id, MonitorName = selected.Name, ShowBrightness = true, ShowContrast = true, AlwaysOnTop = true });
            var widget = widgets.Windows.First(); widget.UpdateLayout(); await Task.Delay(150);
            RenderElement((FrameworkElement)widget.Content, Path.Combine(previewDirectory, "widget.png"));
            widgets.ShowManager(selected); await Task.Delay(150);
            var managerWindow = Application.Current.Windows.OfType<WidgetManagerWindow>().First(); managerWindow.UpdateLayout();
            RenderElement((FrameworkElement)managerWindow.Content, Path.Combine(previewDirectory, "widget-settings.png"));
            managerWindow.Close();
            ShowDisplaySettings(); await Task.Delay(150); settingsWindow.UpdateLayout();
            RenderElement((FrameworkElement)settingsWindow.Content, Path.Combine(previewDirectory, "display-settings.png"));
            settingsWindow.Close();
            flyout.Bind(monitors); flyout.ShowPopup(); await Task.Delay(150); flyout.UpdateLayout();
            RenderElement((FrameworkElement)flyout.Content, Path.Combine(previewDirectory, "brightness-popup.png"));
            Close();
        }

        private static void RenderElement(FrameworkElement element, string path)
        {
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(path)) encoder.Save(file);
        }
    }

    internal sealed class FeatureRow : Border
    {
        private readonly MonitorService service;
        private readonly MonitorDevice device;
        private readonly MonitorFeature feature;
        private readonly Action<string> report;
        private readonly Func<Task> refresh;
        private readonly Action onUserChange;
        private readonly DispatcherTimer timer;
        private readonly TextBlock valueText, stateText;
        private Slider slider;
        private ComboBox combo;
        private Button apply;
        private bool updating, stopped;
        private int writes;
        private readonly bool light;
        private int revision;
        private uint requested;

        public FeatureRow(MonitorService monitorService, MonitorDevice monitor, MonitorFeature control, Action<string> status, Func<Task> rescan, bool compact = false, bool lightTheme = false, Action userChange = null)
        {
            service = monitorService; device = monitor; feature = control; report = status; refresh = rescan;
            onUserChange = userChange;
            light = lightTheme;
            Background = MainWindow.Brush(light ? "#E7F0F2" : "#18232E"); BorderBrush = MainWindow.Brush(light ? "#CCDEE2" : "#2D3D49"); BorderThickness = new Thickness(1); CornerRadius = new CornerRadius(compact ? 11 : 14);
            Padding = compact ? new Thickness(14, 11, 14, 10) : new Thickness(23, 19, 23, 17); Margin = new Thickness(0, 0, 0, compact ? 8 : 12);
            var layout = new Grid(); Child = layout;
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var heading = new Grid(); heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); layout.Children.Add(heading);
            var labels = new StackPanel(); heading.Children.Add(labels);
            var name = MainWindow.Text(feature.Definition.Name, compact ? 12 : 15, light ? "#203E48" : "#E2EDF3"); name.FontWeight = FontWeights.SemiBold; labels.Children.Add(name);
            name.ToolTip = feature.Definition.Hint;
            valueText = MainWindow.Text("", compact ? 25 : feature.Definition.Basic ? 34 : 24, light ? "#197E6E" : "#8CE8D3"); valueText.VerticalAlignment = VerticalAlignment.Center; valueText.FontWeight = FontWeights.Light; Grid.SetColumn(valueText, 1); heading.Children.Add(valueText);
            stateText = MainWindow.Text("", 10, "#728B99"); stateText.Margin = new Thickness(0, 4, 0, 0); Grid.SetRow(stateText, 2); layout.Children.Add(stateText);
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
            timer.Tick += async delegate { timer.Stop(); await CommitAsync(requested, revision); };
            if (feature.Definition.Discrete) BuildDiscrete(layout);
            else
            {
                slider = new Slider { Minimum = 0, Maximum = 100, Value = feature.Percent(feature.Current), IsEnabled = feature.Writable, Margin = new Thickness(0, 12, 0, 0) };
                AutomationProperties.SetName(slider, feature.Definition.Name);
                Grid.SetRow(slider, 1); layout.Children.Add(slider);
                slider.ValueChanged += delegate {
                    if (updating || stopped) return;
                    if (onUserChange != null) onUserChange();
                    requested = feature.FromPercent(slider.Value); revision++;
                    valueText.Text = feature.Percent(requested) + "%";
                    stateText.Text = "변경 대기…";
                    stateText.Visibility = Visibility.Visible;
                    stateText.Foreground = MainWindow.Brush("#A4B9C5");
                    timer.Stop(); timer.Start();
                };
            }
            ShowConfirmed();
        }

        private void BuildDiscrete(Grid layout)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            Grid.SetRow(line, 1); layout.Children.Add(line);
            combo = new ComboBox { IsEnabled = feature.Writable, DisplayMemberPath = "Label", SelectedValuePath = "Value" };
            foreach (uint choice in feature.Choices)
                combo.Items.Add(new Choice { Value = choice, Label = FeatureCatalog.ValueName(feature.Definition.Code, choice) });
            if (!feature.Choices.Contains(feature.Current)) combo.Items.Add(new Choice { Value = feature.Current, Label = FeatureCatalog.ValueName(feature.Definition.Code, feature.Current) + " (현재)" });
            combo.SelectedValue = feature.Current;
            AutomationProperties.SetName(combo, feature.Definition.Name);
            line.Children.Add(combo);
            apply = new Button { Content = "적용", Margin = new Thickness(10, 0, 0, 0), IsEnabled = feature.Writable };
            AutomationProperties.SetName(apply, feature.Definition.Name + " 적용");
            apply.Click += async delegate {
                var choice = combo.SelectedItem as Choice;
                if (choice == null || choice.Value == feature.Current) return;
                if (feature.Definition.Code == 0x60 && MessageBox.Show(Window.GetWindow(this), "입력을 전환하면 현재 PC의 화면이 사라질 수 있습니다.\n" + choice.Label + " 입력으로 전환할까요?", "입력 소스 전환", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                revision++; await CommitAsync(choice.Value, revision);
            };
            line.Children.Add(apply);
        }

        private void ShowConfirmed()
        {
            updating = true;
            if (slider != null) slider.Value = feature.Percent(feature.Current);
            if (combo != null) combo.SelectedValue = feature.Current;
            valueText.Text = feature.Definition.Discrete ? "" : feature.Percent(feature.Current) + "%";
            stateText.Foreground = MainWindow.Brush(light ? "#5B7986" : "#728B99");
            stateText.Text = feature.Definition.Discrete && !feature.Writable ? "지원 값 목록 없음" : "";
            stateText.Visibility = stateText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            updating = false;
        }

        private async Task CommitAsync(uint value, int version)
        {
            if (stopped) return;
            writes++;
            stateText.Text = "적용 중…";
            stateText.Visibility = Visibility.Visible;
            if (apply != null) { apply.IsEnabled = false; combo.IsEnabled = false; }
            try
            {
                uint actual = await service.SetAsync(device, feature, value);
                if (stopped || version != revision) return;
                ShowConfirmed();
                report(device.Name + " · " + feature.Definition.Name + " 적용됨");
                // Color modes can also reset gains/contrast and change which controls work.
                if (feature.Definition.Code == 0x14) await refresh();
            }
            catch (Exception ex)
            {
                if (stopped || version != revision) return;
                ShowConfirmed(); stateText.Text = "적용 실패 · " + ex.Message; stateText.Foreground = MainWindow.Brush("#F4B598"); stateText.TextWrapping = TextWrapping.Wrap;
                stateText.Visibility = Visibility.Visible;
                report(feature.Definition.Name + " 변경 실패 · " + ex.Message);
            }
            finally { writes--; if (apply != null && !stopped) { apply.IsEnabled = feature.Writable; combo.IsEnabled = feature.Writable; } }
        }

        internal void RefreshFromModel(MonitorFeature changed = null)
        { if (changed != null && changed != feature) return; if (!stopped && !timer.IsEnabled && writes == 0) ShowConfirmed(); }

        internal void CancelPending()
        { if (stopped) return; revision++; timer.Stop(); ShowConfirmed(); }

        public void Stop(bool flushPending = false)
        {
            bool pending = timer.IsEnabled && !stopped;
            stopped = true; revision++; timer.Stop();
            if (flushPending && pending) ApplyDetachedAsync(requested);
        }

        private async void ApplyDetachedAsync(uint value)
        {
            try { await service.SetAsync(device, feature, value); }
            catch (Exception ex) { report(device.Name + " · " + feature.Definition.Name + " 변경 실패 · " + ex.Message); }
        }
        private sealed class Choice { public uint Value { get; set; } public string Label { get; set; } }
    }
}
