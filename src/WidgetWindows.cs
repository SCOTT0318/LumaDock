using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace MonitorControl
{
    internal sealed class WidgetManager : IDisposable
    {
        private readonly MainWindow owner;
        private readonly MonitorService service;
        private readonly ResourceDictionary resources;
        private readonly Func<Task> refresh;
        private readonly Action<string> status;
        private readonly SettingsStore store;
        private readonly bool persistent;
        private readonly Dictionary<string, DesktopWidget> windows = new Dictionary<string, DesktopWidget>();
        private readonly DispatcherTimer saveTimer;
        private List<MonitorDevice> monitors = new List<MonitorDevice>();
        private WidgetManagerWindow editor;
        private bool initialized, disposed, hidden;
        internal WidgetSettings Settings { get; private set; }
        internal IList<MonitorDevice> Monitors { get { return monitors; } }
        internal IEnumerable<DesktopWidget> Windows { get { return windows.Values; } }

        internal WidgetManager(MainWindow main, MonitorService monitorService, ResourceDictionary styles, Func<Task> rescan, Action<string> report, bool saveSettings)
        {
            owner = main; service = monitorService; resources = styles; refresh = rescan; status = report; persistent = saveSettings;
            store = new SettingsStore(); Settings = persistent ? store.Load() : new WidgetSettings();
            hidden = !Settings.RestoreWidgets;
            saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            saveTimer.Tick += delegate { saveTimer.Stop(); Save(); };
        }

        internal void BeforeScan()
        { foreach (var window in windows.Values) window.Suspend(); if (editor != null) editor.IsEnabled = false; }

        internal void Rebind(List<MonitorDevice> current)
        {
            monitors = current;
            if (!initialized)
            {
                initialized = true;
                if (Settings.RestoreWidgets) foreach (var placement in Settings.Widgets) Open(placement);
            }
            foreach (var pair in windows) pair.Value.Bind(FindMonitor(pair.Value.Placement.MonitorId));
            if (editor != null) { editor.IsEnabled = true; editor.RefreshMonitors(); }
        }

        internal void ShowManager(MonitorDevice preferred)
        {
            if (disposed) return;
            if (!owner.IsVisible) owner.Show();
            if (owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
            if (editor == null)
            {
                editor = new WidgetManagerWindow(this, resources, preferred) { Owner = owner };
                editor.Closed += delegate { editor = null; };
            }
            else if (preferred != null) editor.SelectMonitor(preferred.Id);
            editor.Show(); editor.Activate();
        }

        internal void ShowWidgetSettings(string placementId)
        {
            var placement = Settings.Widgets.FirstOrDefault(x => x.Id == placementId);
            if (placement == null) return;
            ShowManager(null);
            if (editor != null) editor.SelectMonitor(placement.MonitorId);
        }

        internal WidgetPlacement FindByMonitor(string monitorId)
        {
            if (String.IsNullOrWhiteSpace(monitorId)) return null;
            return Settings.Widgets.FirstOrDefault(x => String.Equals(x.MonitorId, monitorId.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        private MonitorDevice FindMonitor(string monitorId)
        {
            return monitors.FirstOrDefault(x => String.Equals(x.Id, monitorId, StringComparison.OrdinalIgnoreCase));
        }

        internal void Add(WidgetPlacement placement)
        {
            ValidatePlacement(placement);
            if (FindByMonitor(placement.MonitorId) != null) throw new InvalidOperationException("이미 추가된 모니터입니다. 기존 위젯의 설정을 수정하세요.");
            if (Settings.Widgets.Count >= SettingsStore.MaximumWidgets) throw new InvalidOperationException("위젯은 최대 32개까지 배치할 수 있습니다.");
            if (Settings.Widgets.Any(x => String.Equals(x.Id, placement.Id, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("이미 존재하는 위젯입니다.");
            hidden = false;
            Settings.Widgets.Add(placement);
            try { Open(placement); }
            catch { Settings.Widgets.Remove(placement); throw; }
            Save();
            status("위젯 추가됨");
        }

        private static void ValidatePlacement(WidgetPlacement placement)
        {
            if (placement == null) throw new ArgumentNullException("placement");
            if (String.IsNullOrWhiteSpace(placement.Id) || String.IsNullOrWhiteSpace(placement.MonitorId)) throw new InvalidOperationException("모니터를 선택하세요.");
            if (!placement.ShowBrightness && !placement.ShowContrast && !placement.ShowVolume) throw new InvalidOperationException("표시할 조절 항목을 하나 이상 선택하세요.");
        }

        private void Open(WidgetPlacement placement)
        {
            if (windows.ContainsKey(placement.Id)) return;
            var window = new DesktopWidget(service, placement, resources, refresh, status, ScheduleSave, Remove, ShowWidgetSettings);
            windows.Add(placement.Id, window);
            try
            {
                window.Bind(FindMonitor(placement.MonitorId));
                window.Show(); if (hidden) window.Hide();
            }
            catch { windows.Remove(placement.Id); window.Shutdown(); throw; }
        }

        internal void Update(WidgetPlacement placement)
        {
            ValidatePlacement(placement);
            var stored = Settings.Widgets.FirstOrDefault(x => x.Id == placement.Id);
            if (stored == null) throw new InvalidOperationException("위젯을 찾지 못했습니다.");
            var duplicate = FindByMonitor(placement.MonitorId);
            if (duplicate != null && duplicate.Id != stored.Id) throw new InvalidOperationException("이미 추가된 모니터입니다. 기존 위젯의 설정을 수정하세요.");
            // Keep the same model instance used by the floating window. Layout edits
            // must not overwrite a newer position captured while the form was open.
            stored.MonitorId = placement.MonitorId.Trim(); stored.MonitorName = placement.MonitorName;
            stored.ShowBrightness = placement.ShowBrightness; stored.ShowContrast = placement.ShowContrast; stored.ShowVolume = placement.ShowVolume;
            stored.AlwaysOnTop = placement.AlwaysOnTop; stored.Locked = placement.Locked; stored.Theme = placement.Theme == "light" ? "light" : "dark";
            DesktopWidget window;
            if (windows.TryGetValue(stored.Id, out window)) window.Bind(FindMonitor(stored.MonitorId), true);
            Save();
        }

        internal void Remove(string id)
        {
            DesktopWidget window;
            if (windows.TryGetValue(id, out window)) { windows.Remove(id); window.Shutdown(); }
            Settings.Widgets.RemoveAll(x => x.Id == id); Save();
            if (editor != null) editor.RefreshMonitors();
        }

        internal void Reveal(string id)
        {
            var placement = Settings.Widgets.FirstOrDefault(x => x.Id == id);
            if (placement == null) return;
            hidden = false; Open(placement); var window = windows[id]; window.Show(); window.Activate(); window.EnsureOnScreen();
        }

        internal void ToggleVisibility()
        {
            hidden = !hidden;
            if (!hidden) foreach (var placement in Settings.Widgets) Open(placement);
            foreach (var window in windows.Values) { if (hidden) window.Hide(); else window.Show(); }
        }

        internal void RefreshValues(MonitorDevice device, MonitorFeature feature)
        { foreach (var window in windows.Values) window.RefreshValues(device, feature); }

        internal void ScheduleSave() { if (!disposed) { saveTimer.Stop(); saveTimer.Start(); } }
        internal void Save()
        {
            if (!persistent || disposed) return;
            try { store.Save(Settings); }
            catch (IOException ex) { status("위젯 배치를 저장하지 못했습니다 · " + ex.Message); }
            catch (UnauthorizedAccessException ex) { status("위젯 배치를 저장하지 못했습니다 · " + ex.Message); }
        }

        public void Dispose()
        {
            saveTimer.Stop(); Save(); disposed = true;
            if (editor != null) editor.Close();
            foreach (var window in windows.Values.ToList()) window.Shutdown(); windows.Clear();
        }
    }

    internal sealed class DesktopWidget : Window
    {
        internal WidgetPlacement Placement { get; private set; }
        private readonly MonitorService service;
        private readonly Func<Task> refresh;
        private readonly Action<string> report;
        private readonly Action changed;
        private readonly Action<string> remove;
        private readonly Action<string> configure;
        private readonly List<FeatureRow> rows = new List<FeatureRow>();
        private readonly StackPanel body;
        private readonly Border card;
        private readonly TextBlock title, subtitle, footnote;
        private readonly Button pin, more, close;
        private readonly Grid header;
        private MonitorDevice device;
        private bool shuttingDown, positioned;

        internal DesktopWidget(MonitorService monitorService, WidgetPlacement placement, ResourceDictionary styles, Func<Task> rescan, Action<string> status, Action onChanged, Action<string> onRemove, Action<string> settings)
        {
            service = monitorService; Placement = placement; refresh = rescan; report = status; changed = onChanged; remove = onRemove; configure = settings;
            Title = "LumaDock 위젯 · " + placement.MonitorName; Width = 364; SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true; Background = Brushes.Transparent;
            ShowInTaskbar = false; ShowActivated = false; Topmost = placement.AlwaysOnTop;
            Resources.MergedDictionaries.Add(styles); Icon = AppIdentity.WindowIcon();
            card = new Border { CornerRadius = new CornerRadius(19), Padding = new Thickness(17), Margin = new Thickness(9), BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.28, Color = Colors.Black } };
            var canvas = new Grid { Background = Brushes.Transparent }; canvas.Children.Add(card); Content = canvas;
            var layout = new StackPanel(); card.Child = layout;
            header = new Grid { Background = Brushes.Transparent, Margin = new Thickness(2, 0, 0, 12), Cursor = Cursors.SizeAll };
            header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var heading = new StackPanel(); header.Children.Add(heading);
            title = MainWindow.Text(placement.MonitorName, 13, "#E7F1F6"); title.FontWeight = FontWeights.SemiBold; title.MaxWidth = 185; title.TextTrimming = TextTrimming.CharacterEllipsis;
            subtitle = MainWindow.Text("DISPLAY WIDGET", 9, "#89A6B7"); subtitle.Margin = new Thickness(0, 5, 0, 0);
            heading.Children.Add(title); heading.Children.Add(subtitle);
            var actions = new StackPanel { Orientation = Orientation.Horizontal }; Grid.SetColumn(actions, 1); header.Children.Add(actions);
            pin = TinyButton("↟", "항상 위에 표시 전환");
            pin.Click += delegate { Placement.AlwaysOnTop = !Placement.AlwaysOnTop; Topmost = Placement.AlwaysOnTop; ApplyTheme(); changed(); };
            actions.Children.Add(pin);
            more = TinyButton("⋯", "위젯 설정"); more.Click += delegate { configure(Placement.Id); }; actions.Children.Add(more);
            close = TinyButton("×", "이 위젯 제거"); close.Click += delegate { remove(Placement.Id); }; actions.Children.Add(close);
            header.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) {
                if (Placement.Locked || e.OriginalSource is Button || FindButton(e.OriginalSource as DependencyObject)) return;
                if (e.LeftButton != MouseButtonState.Pressed) return;
                try { DragMove(); EnsureOnScreen(); } catch (InvalidOperationException) { }
            };
            layout.Children.Add(header);
            body = new StackPanel(); layout.Children.Add(body);
            footnote = MainWindow.Text("", 9, "#7893A6"); footnote.Margin = new Thickness(2, 3, 0, 0); layout.Children.Add(footnote);
            SourceInitialized += delegate {
                HwndSource.FromHwnd(new WindowInteropHelper(this).Handle).AddHook(delegate(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) {
                    if (msg == 0x02E0) Dispatcher.BeginInvoke(new Action(EnsureOnScreen), DispatcherPriority.ContextIdle);
                    return IntPtr.Zero;
                });
            };
            Loaded += delegate { EnsureOnScreen(); positioned = true; };
            LocationChanged += delegate { if (positioned && !shuttingDown) { CapturePosition(); changed(); } };
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (!shuttingDown) { e.Cancel = true; Dispatcher.BeginInvoke(new Action(delegate { remove(Placement.Id); })); } };
            ApplyTheme();
        }

        private static bool FindButton(DependencyObject current)
        {
            while (current != null) { if (current is Button) return true; current = VisualTreeHelper.GetParent(current); }
            return false;
        }
        private static Button TinyButton(string text, string hint)
        {
            var button = new Button { Content = text, Width = 27, Height = 27, Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = hint, FontSize = 15 };
            AutomationProperties.SetName(button, hint); return button;
        }

        private void ApplyTheme()
        {
            bool light = Placement.Theme == "light";
            card.Background = MainWindow.Brush(light ? "#F2F7F8" : "#111E29"); card.BorderBrush = MainWindow.Brush(light ? "#D1DEE4" : "#34505B");
            title.Foreground = MainWindow.Brush(light ? "#142D38" : "#EDF6FA");
            subtitle.Foreground = MainWindow.Brush(light ? "#557483" : "#86A5B6"); footnote.Foreground = subtitle.Foreground;
            pin.Foreground = MainWindow.Brush(Placement.AlwaysOnTop ? (light ? "#177F6D" : "#8CE8D3") : "#8198A8");
            more.Foreground = close.Foreground = MainWindow.Brush(light ? "#315462" : "#B2C8D4");
            footnote.Text = Placement.Locked ? "위치 잠김" : "";
            footnote.Visibility = Placement.Locked ? Visibility.Visible : Visibility.Collapsed;
            header.Cursor = Placement.Locked ? Cursors.Arrow : Cursors.SizeAll;
            AutomationProperties.SetHelpText(header, Placement.Locked ? "위치 잠김" : "드래그로 위치 이동");
        }

        internal void Bind(MonitorDevice monitor, bool flushPending = false)
        {
            foreach (var row in rows) row.Stop(flushPending); rows.Clear(); body.Children.Clear(); device = monitor;
            body.IsEnabled = true;
            Topmost = Placement.AlwaysOnTop; ApplyTheme();
            title.Text = monitor == null ? Placement.MonitorName : monitor.Name;
            Title = "LumaDock 위젯 · " + title.Text;
            subtitle.Text = monitor == null ? "연결 대기" : monitor.Display + " · " + monitor.PositionLabel + (monitor.Demo ? "  ·  DEMO" : "");
            if (monitor == null)
            {
                var offline = MainWindow.Text("모니터 연결을 확인하세요.", 11, Placement.Theme == "light" ? "#4F707E" : "#93AABA");
                offline.LineHeight = 20; offline.Margin = new Thickness(4, 14, 4, 14); body.Children.Add(offline); return;
            }
            var codes = new List<byte>();
            if (Placement.ShowBrightness) codes.Add(0x10); if (Placement.ShowContrast) codes.Add(0x12); if (Placement.ShowVolume) codes.Add(0x62);
            foreach (byte code in codes)
            {
                var feature = monitor.Features.FirstOrDefault(x => x.Definition.Code == code);
                if (feature == null) continue;
                var row = new FeatureRow(service, monitor, feature, WidgetStatus, refresh, true, Placement.Theme == "light");
                rows.Add(row); body.Children.Add(row);
            }
            if (rows.Count == 0)
            {
                var text = MainWindow.Text("지원하는 조절 항목이 없습니다.", 11, Placement.Theme == "light" ? "#4F707E" : "#93AABA"); text.LineHeight = 20; body.Children.Add(text);
            }
            Dispatcher.BeginInvoke(new Action(delegate { if (IsLoaded && !shuttingDown) { UpdateLayout(); EnsureOnScreen(); } }), DispatcherPriority.Loaded);
        }

        private void WidgetStatus(string message) { report(message); }
        internal void Suspend() { foreach (var row in rows) row.Stop(); body.IsEnabled = false; }
        internal void RefreshValues(MonitorDevice monitor, MonitorFeature feature)
        { if (monitor == device) foreach (var row in rows) row.RefreshFromModel(feature); }

        internal void EnsureOnScreen()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle; if (hwnd == IntPtr.Zero) return;
            NativeRect rect; if (!GetWindowRect(hwnd, out rect)) return;
            int width = Math.Max(100, rect.Right - rect.Left), height = Math.Max(80, rect.Bottom - rect.Top);
            int x = positioned ? rect.Left : (int)Math.Round(Placement.Left), y = positioned ? rect.Top : (int)Math.Round(Placement.Top);
            var proposed = new System.Drawing.Rectangle(x, y, width, height);
            var screen = Forms.Screen.AllScreens.FirstOrDefault(s => s.WorkingArea.IntersectsWith(proposed)) ?? Forms.Screen.PrimaryScreen;
            var area = screen.WorkingArea;
            x = Math.Max(area.Left, Math.Min(x, area.Right - width)); y = Math.Max(area.Top, Math.Min(y, area.Bottom - height));
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);
            CapturePosition();
        }

        private void CapturePosition()
        {
            NativeRect rect; var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero && GetWindowRect(handle, out rect)) { Placement.Left = rect.Left; Placement.Top = rect.Top; }
        }
        internal void Shutdown()
        { if (shuttingDown) return; shuttingDown = true; foreach (var row in rows) row.Stop(); Close(); }
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    }
}
