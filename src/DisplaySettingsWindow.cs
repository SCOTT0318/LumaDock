using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace MonitorControl
{
    internal sealed class DisplaySettingsWindow : Window
    {
        private IList<MonitorDevice> monitors;
        private readonly WidgetSettings settings;
        private readonly Action save;
        private readonly Action<string> identify;
        private readonly Dictionary<string, TextBox> aliases = new Dictionary<string, TextBox>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CheckBox> targets = new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> aliasDrafts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> targetDrafts = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MonitorDevice> knownMonitors = new Dictionary<string, MonitorDevice>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> supportedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly CheckBox enabled, followWindows, hotkeys, checkOnStartup, startWithWindows, startInTray, hdmiSafeMode;
        private readonly ComboBox mode;
        private readonly Slider step;
        private readonly TextBlock stepValue, message;
        private readonly StackPanel fixedTargets;
        private readonly StackPanel monitorEditors;
        private readonly DisplayMapControl map;
        private readonly Style checkStyle;

        internal DisplaySettingsWindow(ResourceDictionary styles, IList<MonitorDevice> connected, WidgetSettings currentSettings, Action onSave, Action<string> identifyMonitor, Action checkUpdates)
        {
            monitors = connected ?? new List<MonitorDevice>(); settings = currentSettings; save = onSave; identify = identifyMonitor;
            foreach (var monitor in monitors) knownMonitors[monitor.Id] = monitor;
            Title = "설정 · LumaDock"; Width = 820; Height = 850; MinWidth = 720; MinHeight = 690;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Color("#0E171F"); Foreground = Color("#E7F1F6");
            FontFamily = new FontFamily("Segoe UI, Malgun Gothic");
            Resources.MergedDictionaries.Add(styles); Icon = AppIdentity.WindowIcon(); AppIdentity.DarkTitlebar(this);
            checkStyle = CreateCheckStyle();

            var root = new Grid { Background = Color("#0E171F") }; Content = root;
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var header = new Border { Background = Color("#121F29"), BorderBrush = Color("#293B47"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(28, 23, 28, 23) };
            var headerGrid = new Grid(); headerGrid.ColumnDefinitions.Add(new ColumnDefinition()); headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); header.Child = headerGrid;
            var heading = Label("모니터 및 밝기", 23, "#EDF5F8"); heading.FontWeight = FontWeights.SemiBold; headerGrid.Children.Add(heading);
            var version = Label("LumaDock " + Assembly.GetExecutingAssembly().GetName().Version.ToString(3), 11, "#8DA9BA"); version.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(version, 1); headerGrid.Children.Add(version); root.Children.Add(header);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(28, 23, 24, 22) };
            Grid.SetRow(scroll, 1); root.Children.Add(scroll); var body = new StackPanel(); scroll.Content = body;
            Section(body, "모니터 이름과 배치");
            map = new DisplayMapControl(monitors, delegate(MonitorDevice monitor) { if (identify != null) identify(monitor.Id); });
            map.Margin = new Thickness(0, 0, 0, 13); body.Children.Add(map);
            monitorEditors = new StackPanel(); body.Children.Add(monitorEditors); RebuildMonitorEditors();

            Divider(body); Section(body, "HDMI 연결 안정성");
            hdmiSafeMode = Check("HDMI 안정 모드", "HDMI 안정 모드"); hdmiSafeMode.IsChecked = settings.HdmiSafeMode; body.Children.Add(hdmiSafeMode);
            var hdmiHint = Label("HDMI는 밝기·명암만 조회하고 제어 간격을 둡니다. 화면이 깨지면 트레이 오른쪽 메뉴의 HDMI 신호 복구를 실행하세요.", 10, "#7F9BAE");
            hdmiHint.TextWrapping = TextWrapping.Wrap; hdmiHint.Margin = new Thickness(25, 7, 0, 0); body.Children.Add(hdmiHint);

            Divider(body); Section(body, "키보드 밝기 조절");
            var brightness = settings.Brightness ?? new BrightnessPreferences();
            enabled = Check("키보드로 모니터 밝기 조절", "키보드 밝기 조절 사용"); enabled.IsChecked = brightness.Enabled; enabled.Margin = new Thickness(0, 0, 0, 15); body.Children.Add(enabled);
            var inputs = new Border { Background = Color("#14242E"), BorderBrush = Color("#2D424F"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(17), Margin = new Thickness(0, 0, 0, 19) };
            var inputOptions = new StackPanel(); inputs.Child = inputOptions; body.Children.Add(inputs);
            followWindows = Check("Windows 밝기 키 연동", "Windows 밝기 키 연동"); followWindows.IsChecked = brightness.FollowWindows; inputOptions.Children.Add(followWindows);
            followWindows.ToolTip = "Fn 키를 Windows가 감지해야 합니다. 제조사 처리 방식에 따라 내장 화면도 함께 바뀔 수 있습니다. 감지되지 않으면 Ctrl + Alt + ↑ / ↓를 사용하세요.";
            var nativeHint = Label("Windows에서 감지되는 밝기 변경을 모니터에 연동합니다.", 10, "#7F9BAE"); nativeHint.TextWrapping = TextWrapping.Wrap; nativeHint.Margin = new Thickness(25, 5, 0, 13); inputOptions.Children.Add(nativeHint);
            hotkeys = Check("Ctrl + Alt + ↑ / ↓", "기본 밝기 단축키 사용"); hotkeys.IsChecked = brightness.UseHotkeys; inputOptions.Children.Add(hotkeys);

            body.Children.Add(Label("조절 대상", 11, "#9EB4C3"));
            mode = new ComboBox { DisplayMemberPath = "Label", SelectedValuePath = "Id", Margin = new Thickness(0, 9, 0, 12), HorizontalAlignment = HorizontalAlignment.Stretch };
            mode.Items.Add(new ModeOption { Id = "all", Label = "모두 함께" });
            mode.Items.Add(new ModeOption { Id = "cursor", Label = "마우스가 있는 모니터" });
            mode.Items.Add(new ModeOption { Id = "selected", Label = "선택한 모니터만" });
            mode.SelectedValue = brightness.Mode; if (mode.SelectedIndex < 0) mode.SelectedIndex = 0;
            AutomationProperties.SetName(mode, "밝기 조절 대상"); body.Children.Add(mode);
            fixedTargets = new StackPanel { Margin = new Thickness(0, 0, 0, 14) }; body.Children.Add(fixedTargets);
            RebuildTargets();

            var stepHeader = new Grid { Margin = new Thickness(0, 3, 0, 6) }; stepHeader.ColumnDefinitions.Add(new ColumnDefinition()); stepHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            stepHeader.Children.Add(Label("한 번에 조절할 밝기", 11, "#9EB4C3")); stepValue = Label("", 12, "#8CE8D3"); Grid.SetColumn(stepValue, 1); stepHeader.Children.Add(stepValue); body.Children.Add(stepHeader);
            step = new Slider { Minimum = 1, Maximum = 20, Value = Math.Max(1, Math.Min(20, brightness.StepPercent)), TickFrequency = 1, IsSnapToTickEnabled = true, SmallChange = 1, LargeChange = 5, Margin = new Thickness(0, 0, 0, 2) };
            AutomationProperties.SetName(step, "밝기 조절 단계"); step.ValueChanged += delegate { UpdateStepLabel(); }; body.Children.Add(step); UpdateStepLabel();
            var limits = new Grid(); limits.ColumnDefinitions.Add(new ColumnDefinition()); limits.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); limits.Children.Add(Label("1%", 9, "#657F91")); var maximum = Label("20%", 9, "#657F91"); Grid.SetColumn(maximum, 1); limits.Children.Add(maximum); body.Children.Add(limits);
            enabled.Checked += delegate { UpdateInputState(); }; enabled.Unchecked += delegate { UpdateInputState(); }; mode.SelectionChanged += delegate { UpdateInputState(); }; UpdateInputState();

            Divider(body); Section(body, "실행");
            startWithWindows = Check("Windows 시작 시 자동 실행", "Windows 시작 시 자동 실행");
            startWithWindows.IsChecked = settings.StartWithWindows; startWithWindows.Margin = new Thickness(0, 0, 0, 13);
            startWithWindows.ToolTip = "현재 Windows 사용자로 로그인하면 트레이에서 실행합니다."; body.Children.Add(startWithWindows);
            startInTray = Check("트레이에서 시작", "트레이에서 시작");
            startInTray.IsChecked = settings.StartInTray;
            startInTray.ToolTip = "직접 실행할 때도 적용합니다. Windows 자동 실행은 항상 트레이에서 시작합니다."; body.Children.Add(startInTray);

            Divider(body); Section(body, "업데이트");
            var updateLine = new Grid(); updateLine.ColumnDefinitions.Add(new ColumnDefinition()); updateLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); body.Children.Add(updateLine);
            checkOnStartup = Check("앱 실행 시 업데이트 확인", "시작 시 업데이트 확인"); checkOnStartup.IsChecked = settings.CheckUpdatesOnStartup; checkOnStartup.VerticalAlignment = VerticalAlignment.Center; updateLine.Children.Add(checkOnStartup);
            var updateButton = new Button { Content = "업데이트 확인", Padding = new Thickness(14, 9, 14, 9), FontSize = 11 }; AutomationProperties.SetName(updateButton, "업데이트 확인"); Grid.SetColumn(updateButton, 1); updateLine.Children.Add(updateButton);
            updateButton.Click += delegate { try { if (checkUpdates != null) checkUpdates(); } catch (Exception ex) { ShowMessage(ex.Message, true); } };

            var footer = new Border { Background = Color("#121F29"), BorderBrush = Color("#293B47"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(28, 16, 28, 18) };
            Grid.SetRow(footer, 2); root.Children.Add(footer); var footerGrid = new Grid(); footerGrid.ColumnDefinitions.Add(new ColumnDefinition()); footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); footer.Child = footerGrid;
            message = Label("", 10, "#8EA9BB"); message.TextWrapping = TextWrapping.Wrap; message.VerticalAlignment = VerticalAlignment.Center; message.Margin = new Thickness(0, 0, 15, 0); footerGrid.Children.Add(message);
            var actions = new StackPanel { Orientation = Orientation.Horizontal }; Grid.SetColumn(actions, 1); footerGrid.Children.Add(actions);
            var close = new Button { Content = "닫기", Padding = new Thickness(17, 10, 17, 10), Margin = new Thickness(0, 0, 8, 0) }; close.Click += delegate { Close(); }; actions.Children.Add(close);
            var apply = new Button { Content = "저장", Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(25, 10, 25, 10) }; AutomationProperties.SetName(apply, "디스플레이 설정 저장"); apply.Click += delegate { Save(); }; actions.Children.Add(apply);
        }

        internal void RefreshConnectedMonitors(IList<MonitorDevice> connected)
        {
            foreach (var item in aliases) aliasDrafts[item.Key] = item.Value.Text;
            foreach (var item in targets) targetDrafts[item.Key] = item.Value.IsChecked == true;
            monitors = connected ?? new List<MonitorDevice>();
            foreach (var monitor in monitors) knownMonitors[monitor.Id] = monitor;
            map.SetMonitors(monitors); RebuildMonitorEditors(); RebuildTargets(); UpdateInputState();
            ShowMessage("모니터 연결 변경됨", false);
        }

        private IList<MonitorDevice> EditorMonitors()
        {
            var ordered = monitors.ToList();
            foreach (var monitor in knownMonitors.Values)
                if (!ordered.Any(x => String.Equals(x.Id, monitor.Id, StringComparison.OrdinalIgnoreCase))) ordered.Add(monitor);
            return ordered;
        }

        private bool Connected(string id)
        { return monitors.Any(x => String.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)); }

        private string EffectiveName(MonitorDevice monitor)
        {
            var preference = (settings.Monitors ?? new List<MonitorPreference>()).FirstOrDefault(x => x != null && String.Equals(x.MonitorId, monitor.Id, StringComparison.OrdinalIgnoreCase));
            return preference != null && !String.IsNullOrWhiteSpace(preference.Alias) ? preference.Alias : String.IsNullOrWhiteSpace(monitor.HardwareName) ? monitor.Name : monitor.HardwareName;
        }

        private void RebuildMonitorEditors()
        {
            monitorEditors.Children.Clear(); aliases.Clear(); var displays = EditorMonitors();
            if (displays.Count == 0)
            {
                var empty = Label("연결된 모니터가 없습니다.", 12, "#92AAB9"); empty.Margin = new Thickness(3, 4, 0, 12); monitorEditors.Children.Add(empty);
            }
            for (int index = 0; index < displays.Count; index++) AddMonitorEditor(monitorEditors, displays[index], index);
        }

        private void RebuildTargets()
        {
            fixedTargets.Children.Clear(); targets.Clear(); supportedTargets.Clear();
            var selectedIds = new HashSet<string>((settings.Brightness ?? new BrightnessPreferences()).SelectedMonitorIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var monitor in EditorMonitors())
            {
                bool supported = Connected(monitor.Id) && monitor.Features.Any(x => x.Definition.Code == 0x10 && x.Writable);
                var target = Check("", "밝기 대상 " + monitor.Display); bool drafted;
                target.IsChecked = targetDrafts.TryGetValue(monitor.Id, out drafted) ? drafted : selectedIds.Contains(monitor.Id); target.Margin = new Thickness(3, 0, 0, 10);
                targets[monitor.Id] = target; if (supported) supportedTargets.Add(monitor.Id);
                fixedTargets.Children.Add(target);
            }
            if (targets.Count == 0) fixedTargets.Children.Add(Label("연결된 모니터가 없습니다.", 11, "#7F9BAE"));
            RefreshTargetLabels();
        }

        private void RefreshTargetLabels()
        {
            foreach (var monitor in knownMonitors.Values)
            {
                CheckBox target;
                if (targets.TryGetValue(monitor.Id, out target)) target.Content = EffectiveName(monitor) + (!Connected(monitor.Id) ? " · 연결 대기" : supportedTargets.Contains(monitor.Id) ? "" : " · 밝기 미지원");
            }
        }

        private void AddMonitorEditor(Panel parent, MonitorDevice monitor, int index)
        {
            var card = new Border { Background = Color("#162630"), BorderBrush = Color("#2D424F"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(15), Margin = new Thickness(0, 0, 0, 10) };
            var layout = new Grid(); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) }); layout.ColumnDefinitions.Add(new ColumnDefinition()); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); card.Child = layout;
            var number = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(7), Background = Color("#25443F"), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Child = new TextBlock { Text = DisplayMapControl.Number(monitor, index), Foreground = Color("#9CEAD5"), FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } }; layout.Children.Add(number);
            var details = new StackPanel { Margin = new Thickness(0, 0, 14, 0) }; Grid.SetColumn(details, 1); layout.Children.Add(details);
            var known = (settings.Monitors ?? new List<MonitorPreference>()).FirstOrDefault(x => x != null && String.Equals(x.MonitorId, monitor.Id, StringComparison.OrdinalIgnoreCase));
            string draftedAlias;
            var alias = new TextBox { Text = aliasDrafts.TryGetValue(monitor.Id, out draftedAlias) ? draftedAlias : known == null ? "" : known.Alias, MaxLength = 80, Background = Color("#0F1D27"), Foreground = Color("#E9F3F7"), BorderBrush = Color("#3B5361"), BorderThickness = new Thickness(1), CaretBrush = Color("#8CE8D3"), SelectionBrush = Color("#376C65"), Padding = new Thickness(10, 7, 10, 7), FontSize = 12, ToolTip = "이름을 비워두면 모델명을 사용합니다." };
            AutomationProperties.SetName(alias, "모니터 이름 " + monitor.Display); aliases[monitor.Id] = alias; details.Children.Add(alias);
            string hardware = String.IsNullOrWhiteSpace(monitor.HardwareName) ? monitor.Name : monitor.HardwareName;
            var model = Label(hardware, 10, "#90A8B8"); model.TextTrimming = TextTrimming.CharacterEllipsis; model.Margin = new Thickness(1, 7, 0, 0); details.Children.Add(model);
            var position = Label(monitor.Display + " · " + (Connected(monitor.Id) ? Position(monitor) : "연결 대기"), 10, "#6F8F9F"); position.TextTrimming = TextTrimming.CharacterEllipsis; position.Margin = new Thickness(1, 4, 0, 0); details.Children.Add(position);
            var identifyButton = new Button { Content = "식별", Padding = new Thickness(12, 8, 12, 8), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 0, 0), FontSize = 11 };
            AutomationProperties.SetName(identifyButton, "모니터 식별 " + monitor.Display); identifyButton.Click += delegate { if (identify != null) identify(monitor.Id); }; Grid.SetColumn(identifyButton, 2); layout.Children.Add(identifyButton); parent.Children.Add(card);
            identifyButton.IsEnabled = Connected(monitor.Id);
        }

        internal static string Position(MonitorDevice monitor)
        {
            return String.IsNullOrWhiteSpace(monitor.PositionLabel) ? (monitor.Width > 0 && monitor.Height > 0 ? (monitor.Primary ? "주 모니터 · " : "") + "(" + monitor.Left + ", " + monitor.Top + ")" : "배치 정보 없음") : monitor.PositionLabel;
        }

        private void UpdateInputState()
        {
            if (followWindows == null || hotkeys == null || mode == null || step == null) return;
            bool active = enabled.IsChecked == true;
            followWindows.IsEnabled = active; hotkeys.IsEnabled = active; mode.IsEnabled = active; step.IsEnabled = active;
            bool fixedMode = (mode.SelectedValue as string) == "selected";
            fixedTargets.Visibility = fixedMode ? Visibility.Visible : Visibility.Collapsed;
            foreach (var item in targets) item.Value.IsEnabled = active && fixedMode && supportedTargets.Contains(item.Key);
        }

        private void UpdateStepLabel() { if (step != null && stepValue != null) stepValue.Text = ((int)Math.Round(step.Value)) + "%"; }

        private void Save()
        {
            bool active = enabled.IsChecked == true;
            if (active && followWindows.IsChecked != true && hotkeys.IsChecked != true) { ShowMessage("밝기 입력 방식을 선택하세요.", true); return; }
            var brightness = settings.Brightness ?? new BrightnessPreferences();
            var selected = new HashSet<string>((brightness.SelectedMonitorIds ?? new List<string>()).Where(x => !targets.ContainsKey(x)), StringComparer.OrdinalIgnoreCase);
            foreach (var item in targets) if (item.Value.IsChecked == true) selected.Add(item.Key);
            string routing = mode.SelectedValue as string ?? "all";
            if (active && routing == "selected" && selected.Count == 0) { ShowMessage("조절할 모니터를 선택하세요.", true); return; }
            var preferences = (settings.Monitors ?? new List<MonitorPreference>()).Where(x => x != null && !String.IsNullOrWhiteSpace(x.MonitorId) && !aliases.ContainsKey(x.MonitorId)).Select(x => new MonitorPreference { MonitorId = x.MonitorId, Alias = x.Alias }).ToList();
            foreach (var item in aliases)
            {
                string alias = item.Value.Text.Trim();
                if (alias.Length > 0) preferences.Add(new MonitorPreference { MonitorId = item.Key, Alias = alias });
            }
            var oldMonitors = settings.Monitors; var oldBrightness = settings.Brightness; bool oldCheck = settings.CheckUpdatesOnStartup;
            bool oldStartWithWindows = settings.StartWithWindows, oldStartInTray = settings.StartInTray;
            bool oldHdmiSafeMode = settings.HdmiSafeMode;
            settings.Monitors = preferences;
            settings.Brightness = new BrightnessPreferences { Enabled = active, FollowWindows = followWindows.IsChecked == true, Mode = routing, SelectedMonitorIds = selected.ToList(), StepPercent = (int)Math.Round(step.Value), UseHotkeys = hotkeys.IsChecked == true };
            settings.CheckUpdatesOnStartup = checkOnStartup.IsChecked == true;
            settings.StartWithWindows = startWithWindows.IsChecked == true;
            settings.StartInTray = startInTray.IsChecked == true;
            settings.HdmiSafeMode = hdmiSafeMode.IsChecked == true;
            try { if (save != null) save(); map.Refresh(); RefreshTargetLabels(); ShowMessage("저장됨", false); }
            catch (Exception ex) { settings.Monitors = oldMonitors; settings.Brightness = oldBrightness; settings.CheckUpdatesOnStartup = oldCheck; settings.StartWithWindows = oldStartWithWindows; settings.StartInTray = oldStartInTray; settings.HdmiSafeMode = oldHdmiSafeMode; ShowMessage(ex.Message, true); }
        }

        private void ShowMessage(string text, bool error) { if (message != null) { message.Text = text; message.Foreground = Color(error ? "#F0B394" : "#8EA9BB"); } }
        private static SolidColorBrush Color(string hex) { return MainWindow.Brush(hex); }
        private static TextBlock Label(string text, double size, string color) { return MainWindow.Text(text, size, color); }
        private static void Section(Panel parent, string text)
        { var heading = Label(text, 13, "#CFDFE9"); heading.FontWeight = FontWeights.SemiBold; heading.Margin = new Thickness(1, 0, 0, 14); parent.Children.Add(heading); }
        private static void Divider(Panel parent) { parent.Children.Add(new Border { Height = 1, Background = Color("#293E4C"), Margin = new Thickness(0, 18, 0, 22) }); }
        private CheckBox Check(string text, string automationName)
        { var check = new CheckBox { Content = text, FontSize = 11, Style = checkStyle, VerticalContentAlignment = VerticalAlignment.Center }; AutomationProperties.SetName(check, automationName); return check; }
        private static Style CreateCheckStyle()
        {
            return (Style)XamlReader.Parse(@"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='CheckBox'>
                <Setter Property='Foreground' Value='#C9DCE6'/><Setter Property='Cursor' Value='Hand'/>
                <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='CheckBox'>
                    <Border Background='Transparent'><Grid><Grid.ColumnDefinitions><ColumnDefinition Width='17'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>
                    <Border x:Name='Box' Width='16' Height='16' Background='#10212B' BorderBrush='#526E7E' BorderThickness='1' CornerRadius='4' VerticalAlignment='Center'>
                        <Path x:Name='Tick' Data='M 3,7 L 6,10 L 12,3' Stroke='#163831' StrokeThickness='1.8' Visibility='Collapsed'/>
                    </Border><ContentPresenter Grid.Column='1' Margin='9,0,0,0' VerticalAlignment='Center'/></Grid></Border>
                    <ControlTemplate.Triggers><Trigger Property='IsChecked' Value='True'><Setter TargetName='Box' Property='Background' Value='#8CE8D3'/><Setter TargetName='Box' Property='BorderBrush' Value='#8CE8D3'/><Setter TargetName='Tick' Property='Visibility' Value='Visible'/></Trigger>
                    <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Box' Property='BorderBrush' Value='#B1F4E2'/></Trigger>
                    <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Box' Property='BorderThickness' Value='2'/></Trigger>
                    <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger></ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter></Style>");
        }
        private sealed class ModeOption { public string Id { get; set; } public string Label { get; set; } public override string ToString() { return Label; } }
    }

    internal sealed class DisplayMapControl : Border
    {
        private IList<MonitorDevice> monitors;
        private readonly Action<MonitorDevice> select;
        private readonly Canvas canvas;
        private string selectedId;

        internal DisplayMapControl(IList<MonitorDevice> connected, Action<MonitorDevice> onSelect)
        {
            monitors = connected ?? new List<MonitorDevice>(); select = onSelect;
            Background = MainWindow.Brush("#101F29"); BorderBrush = MainWindow.Brush("#2C4350"); BorderThickness = new Thickness(1); CornerRadius = new CornerRadius(12); Height = 196; Padding = new Thickness(14);
            canvas = new Canvas { Background = Brushes.Transparent, ClipToBounds = true }; Child = canvas; canvas.SizeChanged += delegate { Refresh(); };
            Loaded += delegate { Refresh(); };
        }

        internal void SetSelected(string monitorId) { selectedId = monitorId; Refresh(); }
        internal void SetMonitors(IList<MonitorDevice> connected)
        {
            monitors = connected ?? new List<MonitorDevice>();
            if (!monitors.Any(x => String.Equals(x.Id, selectedId, StringComparison.OrdinalIgnoreCase))) selectedId = null;
            Refresh();
        }
        internal static string Number(MonitorDevice monitor, int fallback)
        { var match = Regex.Match(monitor.Display ?? "", @"(\d+)$"); return match.Success ? match.Groups[1].Value : (fallback + 1).ToString(); }

        internal void Refresh()
        {
            canvas.Children.Clear();
            double availableWidth = canvas.ActualWidth, availableHeight = canvas.ActualHeight;
            if (availableWidth < 20 || availableHeight < 20) return;
            var known = monitors.Where(x => x.Width > 0 && x.Height > 0).ToList();
            if (known.Count == 0)
            {
                var text = MainWindow.Text(monitors.Count == 0 ? "연결된 모니터 없음" : "배치 정보 없음", 12, "#7895A7"); canvas.Children.Add(text); Canvas.SetLeft(text, 12); Canvas.SetTop(text, availableHeight / 2 - 8); return;
            }
            double left = known.Min(x => x.Left), top = known.Min(x => x.Top);
            double right = known.Max(x => (double)x.Left + x.Width), bottom = known.Max(x => (double)x.Top + x.Height);
            double scale = Math.Min((availableWidth - 24) / Math.Max(1, right - left), (availableHeight - 16) / Math.Max(1, bottom - top));
            double offsetX = (availableWidth - (right - left) * scale) / 2, offsetY = (availableHeight - (bottom - top) * scale) / 2;
            foreach (var monitor in known)
            {
                int index = monitors.IndexOf(monitor);
                bool active = String.Equals(selectedId, monitor.Id, StringComparison.OrdinalIgnoreCase) || (selectedId == null && monitor.Primary);
                double width = Math.Max(44, monitor.Width * scale - 7), height = Math.Max(35, monitor.Height * scale - 7);
                var contents = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7), MaxWidth = Math.Max(25, width - 18) };
                var number = MainWindow.Text(Number(monitor, index), height > 80 ? 25 : 17, active ? "#A5F2DC" : "#B8D0DC"); number.FontWeight = FontWeights.SemiBold; number.HorizontalAlignment = HorizontalAlignment.Center; contents.Children.Add(number);
                if (height > 65)
                {
                    var name = MainWindow.Text(monitor.Name, 10, "#94B6C5"); name.Margin = new Thickness(0, 4, 0, 0); name.TextTrimming = TextTrimming.CharacterEllipsis; name.HorizontalAlignment = HorizontalAlignment.Center; contents.Children.Add(name);
                    if (height > 105) { var position = MainWindow.Text(DisplaySettingsWindow.Position(monitor), 9, "#6E98A9"); position.Margin = new Thickness(0, 5, 0, 0); position.TextTrimming = TextTrimming.CharacterEllipsis; position.HorizontalAlignment = HorizontalAlignment.Center; contents.Children.Add(position); }
                }
                var button = new Button { Width = width, Height = height, Padding = new Thickness(0), Content = contents, Background = MainWindow.Brush(active ? "#203E39" : "#1A2C38"), BorderBrush = MainWindow.Brush(active ? "#7BD1B9" : "#466474"), BorderThickness = new Thickness(active ? 1.5 : 1), ToolTip = monitor.Name + " · " + DisplaySettingsWindow.Position(monitor) };
                AutomationProperties.SetName(button, "배치 모니터 " + monitor.Display);
                button.Click += delegate { selectedId = monitor.Id; if (select != null) select(monitor); Refresh(); };
                canvas.Children.Add(button); Canvas.SetLeft(button, offsetX + (monitor.Left - left) * scale + 3.5); Canvas.SetTop(button, offsetY + (monitor.Top - top) * scale + 3.5);
            }
            if (known.Count != monitors.Count)
            {
                var hint = MainWindow.Text("일부 화면의 배치 정보 없음", 9, "#7895A7"); canvas.Children.Add(hint); Canvas.SetLeft(hint, 6); Canvas.SetTop(hint, availableHeight - 12);
            }
        }
    }
}
