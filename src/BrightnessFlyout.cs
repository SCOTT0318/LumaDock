using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace MonitorControl
{
    internal sealed class BrightnessFlyout : Window
    {
        private readonly MonitorService service;
        private readonly Action<string> status;
        private readonly Func<Task> rescan;
        private readonly StackPanel list;
        private readonly List<FeatureRow> rows = new List<FeatureRow>();
        internal BrightnessFlyout(MonitorService monitorService, ResourceDictionary styles, Action<string> report, Func<Task> refresh, Action open, Action settings)
        {
            service = monitorService; status = report; rescan = refresh;
            Title = "LumaDock 밝기"; Width = 390; SizeToContent = SizeToContent.Height; MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 30);
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
            Background = MainWindow.Brush("#111E29"); Foreground = MainWindow.Brush("#EDF6FA"); Icon = AppIdentity.WindowIcon();
            Resources.MergedDictionaries.Add(styles);
            var frame = new Border { Background = Background, Padding = new Thickness(18), BorderThickness = new Thickness(1), BorderBrush = MainWindow.Brush("#36525E") }; Content = frame;
            var body = new Grid(); frame.Child = body;
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); body.RowDefinitions.Add(new RowDefinition()); body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var header = new DockPanel { Margin = new Thickness(3, 0, 0, 16) };
            var close = new Button { Content = "×", Padding = new Thickness(8, 3, 8, 3), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            AutomationProperties.SetName(close, "밝기 팝업 닫기"); close.Click += delegate { HidePopup(); }; DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
            header.Children.Add(MainWindow.Text("밝기", 19, "#E9F6F9")); body.Children.Add(header);
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 530 };
            list = new StackPanel(); scroll.Content = list; Grid.SetRow(scroll, 1); body.Children.Add(scroll);
            var actions = new Grid { Margin = new Thickness(0, 6, 0, 0) }; actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition());
            var settingsButton = new Button { Content = "설정", Margin = new Thickness(0, 0, 5, 0) }; AutomationProperties.SetName(settingsButton, "밝기 설정");
            settingsButton.Click += delegate { HidePopup(); settings(); }; actions.Children.Add(settingsButton);
            var openButton = new Button { Content = "LumaDock 열기", Margin = new Thickness(5, 0, 0, 0) }; AutomationProperties.SetName(openButton, "밝기 팝업에서 앱 열기");
            openButton.Click += delegate { HidePopup(); open(); }; Grid.SetColumn(openButton, 1); actions.Children.Add(openButton); Grid.SetRow(actions, 2); body.Children.Add(actions);
            Deactivated += delegate { if (IsVisible) HidePopup(); };
        }

        internal void Bind(IList<MonitorDevice> monitors)
        {
            Suspend(); rows.Clear(); list.Children.Clear();
            if (monitors.Count == 0) list.Children.Add(MainWindow.Text("연결된 모니터 없음", 12, "#90AAB8"));
            foreach (var monitor in monitors)
            {
                var name = MainWindow.Text(monitor.Name, 13, "#E8F1F6"); name.FontWeight = FontWeights.SemiBold; name.TextTrimming = TextTrimming.CharacterEllipsis; name.Margin = new Thickness(3, 2, 3, 4); list.Children.Add(name);
                var location = MainWindow.Text(monitor.Display + " · " + monitor.PositionLabel, 10, "#88A4B5"); location.Margin = new Thickness(3, 0, 3, 10); list.Children.Add(location);
                var feature = monitor.Features.FirstOrDefault(x => x.Definition.Code == 0x10);
                if (feature == null) { var unsupported = MainWindow.Text("밝기 제어 미지원", 11, "#819BAA"); unsupported.Margin = new Thickness(4, 0, 0, 17); list.Children.Add(unsupported); continue; }
                var row = new FeatureRow(service, monitor, feature, status, rescan, true); rows.Add(row); list.Children.Add(row);
            }
        }

        internal void ShowPopup()
        {
            Show(); UpdateLayout();
            var cursor = Forms.Control.MousePosition;
            var area = Forms.Screen.FromPoint(cursor).WorkingArea;
            var handle = new WindowInteropHelper(this).Handle; NativeRect rect;
            if (GetWindowRect(handle, out rect))
            {
                int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
                int x = Math.Max(area.Left, Math.Min(cursor.X - width / 2, area.Right - width - 8));
                int y = Math.Max(area.Top + 8, area.Bottom - height - 8);
                SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004);
            }
            Activate();
        }
        internal void RefreshValues(MonitorFeature feature) { foreach (var row in rows) row.RefreshFromModel(feature); }
        internal void Suspend() { foreach (var row in rows) row.Stop(); }
        internal void HidePopup() { foreach (var row in rows) row.Stop(true); Hide(); }
        internal void Shutdown() { Suspend(); Close(); }
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    }

    internal sealed class MonitorIdentifier : Window
    {
        internal MonitorIdentifier(MonitorDevice monitor, int number)
        {
            Width = 280; Height = 160; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; ShowActivated = false; Topmost = true;
            Background = MainWindow.Brush("#142A31");
            var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20) };
            var id = MainWindow.Text(number.ToString(), 60, "#8CE8D3"); id.HorizontalAlignment = HorizontalAlignment.Center; panel.Children.Add(id);
            var name = MainWindow.Text(monitor.Name, 14, "#F0F8FA"); name.HorizontalAlignment = HorizontalAlignment.Center; name.TextTrimming = TextTrimming.CharacterEllipsis; panel.Children.Add(name);
            Content = new Border { BorderBrush = MainWindow.Brush("#6BC8B1"), BorderThickness = new Thickness(2), Child = panel };
            Loaded += delegate {
                var handle = new WindowInteropHelper(this).Handle;
                var screen = Forms.Screen.AllScreens.FirstOrDefault(x => x.DeviceName.Replace(@"\\.\", "") == monitor.Display);
                if (screen != null)
                {
                    var bounds = screen.Bounds;
                    SetWindowPos(handle, IntPtr.Zero, bounds.Left + (bounds.Width - 280) / 2, bounds.Top + (bounds.Height - 160) / 2, 0, 0, 0x0001 | 0x0004 | 0x0010);
                }
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += delegate { timer.Stop(); Close(); }; timer.Start();
            };
        }
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    }
}
