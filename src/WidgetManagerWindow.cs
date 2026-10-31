using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace MonitorControl
{
    internal sealed class WidgetManagerWindow : Window
    {
        private readonly WidgetManager manager;
        private readonly ComboBox monitorSelector;
        private readonly CheckBox brightness, contrast, volume, onTop, locked, restore;
        private readonly Button darkTheme, lightTheme, saveButton, cancelEdit;
        private readonly TextBlock title, formCaption, featureHint, listCount, message;
        private readonly StackPanel widgetList;
        private readonly ScrollViewer settingsScroll;
        private readonly Style checkStyle;
        private WidgetPlacement editing;
        private string formMonitorId;
        private string preferredId;
        private string theme = "dark";
        private bool updating;

        internal WidgetManagerWindow(WidgetManager widgetManager, ResourceDictionary styles, MonitorDevice preferred)
        {
            manager = widgetManager;
            preferredId = preferred == null ? null : preferred.Id;
            Title = "위젯 · LumaDock";
            Width = 660; Height = 790; MinWidth = 590; MinHeight = 660;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Color("#0E171F"); Foreground = Color("#E7F1F6");
            FontFamily = new FontFamily("Segoe UI, Malgun Gothic");
            Icon = AppIdentity.WindowIcon();
            AppIdentity.DarkTitlebar(this);
            Resources.MergedDictionaries.Add(styles);
            checkStyle = CreateCheckStyle();

            var root = new Grid { Background = Color("#0E171F") };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = root;

            var header = new Border { Padding = new Thickness(28, 25, 28, 22), BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = Color("#293B47"), Background = Color("#121F29") };
            var headerContent = new Grid(); header.Child = headerContent;
            headerContent.ColumnDefinitions.Add(new ColumnDefinition());
            headerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var heading = new StackPanel(); headerContent.Children.Add(heading);
            title = Label("위젯", 24, "#EBF4F8"); title.FontWeight = FontWeights.SemiBold;
            heading.Children.Add(title);
            var glyph = new Grid { Width = 30, Height = 30, VerticalAlignment = VerticalAlignment.Center };
            AddTile(glyph, HorizontalAlignment.Left, VerticalAlignment.Top, "#88DFCA");
            AddTile(glyph, HorizontalAlignment.Right, VerticalAlignment.Top, "#3C6F70");
            AddTile(glyph, HorizontalAlignment.Left, VerticalAlignment.Bottom, "#3C6F70");
            AddTile(glyph, HorizontalAlignment.Right, VerticalAlignment.Bottom, "#88DFCA");
            Grid.SetColumn(glyph, 1); headerContent.Children.Add(glyph); root.Children.Add(header);

            settingsScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(28, 22, 24, 18) };
            Grid.SetRow(settingsScroll, 1); root.Children.Add(settingsScroll);
            var body = new StackPanel(); settingsScroll.Content = body;

            formCaption = Section("새 위젯", body);
            body.Children.Add(Label("모니터", 11, "#9EB3C2"));
            monitorSelector = new ComboBox { DisplayMemberPath = "Label", SelectedValuePath = "Id", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 9, 0, 18) };
            AutomationProperties.SetName(monitorSelector, "위젯 대상 모니터");
            monitorSelector.SelectionChanged += delegate { if (!updating) LoadSelectedMonitor(false); };
            body.Children.Add(monitorSelector);

            body.Children.Add(Label("표시할 조절 항목", 11, "#9EB3C2"));
            var controls = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            for (int i = 0; i < 3; i++) controls.ColumnDefinitions.Add(new ColumnDefinition());
            brightness = Check("밝기", "밝기 표시");
            contrast = Check("명암", "명암 표시");
            volume = Check("볼륨", "볼륨 표시");
            var controlChecks = new[] { brightness, contrast, volume };
            foreach (var control in controlChecks)
            {
                control.Checked += delegate { if (!updating) UpdateSaveButton(); };
                control.Unchecked += delegate { if (!updating) UpdateSaveButton(); };
            }
            for (int i = 0; i < controlChecks.Length; i++)
            {
                var choice = new Border { Background = Color("#182833"), CornerRadius = new CornerRadius(9), BorderBrush = Color("#314754"), BorderThickness = new Thickness(1), Padding = new Thickness(13, 13, 8, 13), Margin = new Thickness(0, 0, i < 2 ? 8 : 0, 0), Child = controlChecks[i] };
                Grid.SetColumn(choice, i); controls.Children.Add(choice);
            }
            body.Children.Add(controls);
            featureHint = Label("", 10, "#7795A7");
            featureHint.TextWrapping = TextWrapping.Wrap; featureHint.Margin = new Thickness(1, 6, 0, 18); body.Children.Add(featureHint);

            var appearance = new Grid { Margin = new Thickness(0, 0, 0, 20) };
            appearance.ColumnDefinitions.Add(new ColumnDefinition()); appearance.ColumnDefinitions.Add(new ColumnDefinition());
            var themePanel = new StackPanel { Margin = new Thickness(0, 0, 19, 0) };
            themePanel.Children.Add(Label("테마", 11, "#9EB3C2"));
            var themeButtons = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            themeButtons.ColumnDefinitions.Add(new ColumnDefinition()); themeButtons.ColumnDefinitions.Add(new ColumnDefinition());
            darkTheme = ThemeButton("다크", "#142733"); darkTheme.Margin = new Thickness(0, 0, 7, 0);
            lightTheme = ThemeButton("라이트", "#E7F2F4"); Grid.SetColumn(lightTheme, 1);
            darkTheme.Click += delegate { SetTheme("dark"); }; lightTheme.Click += delegate { SetTheme("light"); };
            themeButtons.Children.Add(darkTheme); themeButtons.Children.Add(lightTheme); themePanel.Children.Add(themeButtons); appearance.Children.Add(themePanel);
            var behavior = new StackPanel { Margin = new Thickness(0, 0, 0, 0) }; Grid.SetColumn(behavior, 1);
            behavior.Children.Add(Label("옵션", 11, "#9EB3C2"));
            onTop = Check("항상 위에 표시", "항상 위에 표시"); onTop.Margin = new Thickness(0, 10, 0, 11); behavior.Children.Add(onTop);
            locked = Check("위치 잠금", "위치 잠금"); behavior.Children.Add(locked); appearance.Children.Add(behavior); body.Children.Add(appearance);

            var divider = new Border { Height = 1, Background = Color("#2A3E4B"), Margin = new Thickness(0, 2, 0, 21) }; body.Children.Add(divider);
            var listHeader = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            listHeader.ColumnDefinitions.Add(new ColumnDefinition()); listHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var placedTitle = Label("배치한 위젯", 13, "#CADDE8"); placedTitle.FontWeight = FontWeights.SemiBold; listHeader.Children.Add(placedTitle);
            listCount = Label("0개", 11, "#80BFAF"); listCount.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(listCount, 1); listHeader.Children.Add(listCount); body.Children.Add(listHeader);
            widgetList = new StackPanel(); body.Children.Add(widgetList);
            restore = Check("앱 실행 시 복원", "앱을 열 때 위젯 배치 복원"); restore.Margin = new Thickness(1, 13, 0, 0); restore.IsChecked = manager.Settings.RestoreWidgets;
            restore.Checked += delegate { manager.Settings.RestoreWidgets = true; manager.Save(); };
            restore.Unchecked += delegate { manager.Settings.RestoreWidgets = false; manager.Save(); };
            body.Children.Add(restore);

            var footer = new Border { Padding = new Thickness(28, 16, 28, 19), BorderBrush = Color("#2A3D49"), BorderThickness = new Thickness(0, 1, 0, 0), Background = Color("#121F29") };
            Grid.SetRow(footer, 2); root.Children.Add(footer);
            var footerLayout = new Grid(); footerLayout.ColumnDefinitions.Add(new ColumnDefinition()); footerLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); footer.Child = footerLayout;
            message = Label("", 10, "#8BA5B7"); message.TextWrapping = TextWrapping.Wrap; message.VerticalAlignment = VerticalAlignment.Center; message.Margin = new Thickness(0, 0, 14, 0); footerLayout.Children.Add(message);
            var actions = new StackPanel { Orientation = Orientation.Horizontal }; Grid.SetColumn(actions, 1); footerLayout.Children.Add(actions);
            cancelEdit = new Button { Content = "되돌리기", Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 10, 12, 10), FontSize = 11 };
            AutomationProperties.SetName(cancelEdit, "위젯 설정 되돌리기");
            cancelEdit.Click += delegate { LoadSelectedMonitor(true); ShowMessage("", false); }; actions.Children.Add(cancelEdit);
            saveButton = new Button { Content = "위젯 추가", Padding = new Thickness(19, 10, 19, 10), FontSize = 11, Style = (Style)FindResource("PrimaryButton") };
            saveButton.Click += delegate { SaveWidget(); }; actions.Children.Add(saveButton);

            SetTheme("dark");
            RefreshMonitors();
            RenderList();
        }

        private static SolidColorBrush Color(string hex) { return MainWindow.Brush(hex); }
        private static TextBlock Label(string text, double size, string color) { return MainWindow.Text(text, size, color); }
        private static void AddTile(Grid parent, HorizontalAlignment horizontal, VerticalAlignment vertical, string color)
        { parent.Children.Add(new Border { Width = 12, Height = 12, HorizontalAlignment = horizontal, VerticalAlignment = vertical, Background = Color(color), CornerRadius = new CornerRadius(3) }); }

        private static TextBlock Section(string text, Panel parent)
        {
            var title = Label(text, 13, "#CADDE8"); title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(0, 0, 0, 18); parent.Children.Add(title); return title;
        }

        private CheckBox Check(string text, string automationName)
        {
            var check = new CheckBox { Content = text, Style = checkStyle, FontSize = 11, VerticalContentAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(check, automationName); return check;
        }

        private static Style CreateCheckStyle()
        {
            return (Style)XamlReader.Parse(@"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='CheckBox'>
                <Setter Property='Foreground' Value='#C4D8E4'/><Setter Property='Cursor' Value='Hand'/>
                <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='CheckBox'>
                    <Border Background='Transparent'><Grid><Grid.ColumnDefinitions><ColumnDefinition Width='17'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>
                    <Border x:Name='Box' Width='16' Height='16' Background='#10212B' BorderBrush='#526E7E' BorderThickness='1' CornerRadius='4' VerticalAlignment='Center'>
                        <Path x:Name='Tick' Data='M 3,7 L 6,10 L 12,3' Stroke='#163831' StrokeThickness='1.8' Visibility='Collapsed'/>
                    </Border><ContentPresenter Grid.Column='1' Margin='9,0,0,0' VerticalAlignment='Center'/></Grid></Border>
                    <ControlTemplate.Triggers>
                    <Trigger Property='IsChecked' Value='True'><Setter TargetName='Box' Property='Background' Value='#8CE8D3'/><Setter TargetName='Box' Property='BorderBrush' Value='#8CE8D3'/><Setter TargetName='Tick' Property='Visibility' Value='Visible'/></Trigger>
                    <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Box' Property='BorderBrush' Value='#A1EBD8'/></Trigger>
                    <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Box' Property='BorderThickness' Value='2'/></Trigger>
                    <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.35'/></Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter></Style>");
        }

        private static Button ThemeButton(string text, string swatch)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            line.Children.Add(new Border { Width = 16, Height = 13, Background = Color(swatch), BorderBrush = Color("#55717F"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 7, 0) });
            line.Children.Add(Label(text, 11, "#D0E2EC"));
            var button = new Button { Content = line, Padding = new Thickness(9, 10, 9, 10) }; AutomationProperties.SetName(button, text + " 테마"); return button;
        }

        private void SetTheme(string value)
        {
            theme = value == "light" ? "light" : "dark";
            darkTheme.Background = Color(theme == "dark" ? "#20443E" : "#182833"); darkTheme.BorderBrush = Color(theme == "dark" ? "#73CBB6" : "#354B58");
            lightTheme.Background = Color(theme == "light" ? "#20443E" : "#182833"); lightTheme.BorderBrush = Color(theme == "light" ? "#73CBB6" : "#354B58");
        }

        internal void RefreshMonitors()
        {
            string selectedId = monitorSelector.SelectedValue as string ?? (editing == null ? preferredId : editing.MonitorId);
            updating = true;
            monitorSelector.Items.Clear();
            var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var monitor in manager.Monitors)
            {
                included.Add(monitor.Id);
                monitorSelector.Items.Add(new MonitorOption { Id = monitor.Id, Device = monitor,
                    Label = monitor.Name + "  ·  " + monitor.Display + (manager.FindByMonitor(monitor.Id) == null ? "" : "  ·  추가됨") });
            }
            foreach (var placement in manager.Settings.Widgets)
                if (included.Add(placement.MonitorId))
                    monitorSelector.Items.Add(new MonitorOption { Id = placement.MonitorId, Label = placement.MonitorName + "  ·  연결 대기" });
            monitorSelector.SelectedItem = monitorSelector.Items.Cast<MonitorOption>().FirstOrDefault(x => SameId(x.Id, selectedId));
            if (monitorSelector.SelectedIndex < 0 && monitorSelector.Items.Count > 0) monitorSelector.SelectedIndex = 0;
            monitorSelector.IsEnabled = monitorSelector.Items.Count > 0;
            updating = false;
            LoadSelectedMonitor(false);
        }

        internal void SelectMonitor(string monitorId)
        {
            if (String.IsNullOrWhiteSpace(monitorId)) return;
            preferredId = monitorId;
            var option = monitorSelector.Items.Cast<MonitorOption>().FirstOrDefault(x => SameId(x.Id, monitorId));
            if (option == null)
            {
                RefreshMonitors();
                option = monitorSelector.Items.Cast<MonitorOption>().FirstOrDefault(x => SameId(x.Id, monitorId));
            }
            if (option == null) return;
            if (monitorSelector.SelectedItem != option) monitorSelector.SelectedItem = option;
            else LoadSelectedMonitor(false);
        }

        private static bool SameId(string first, string second)
        { return String.Equals(first, second, StringComparison.OrdinalIgnoreCase); }

        private void LoadSelectedMonitor(bool reload)
        {
            var option = monitorSelector.SelectedItem as MonitorOption;
            var saved = option == null ? null : manager.FindByMonitor(option.Id);
            bool changed = !SameId(formMonitorId, option == null ? null : option.Id) || editing != saved;
            editing = saved;
            formMonitorId = option == null ? null : option.Id;
            if (reload || changed)
            {
                updating = true;
                brightness.IsChecked = saved == null ? true : saved.ShowBrightness;
                contrast.IsChecked = saved != null && saved.ShowContrast;
                volume.IsChecked = saved != null && saved.ShowVolume;
                onTop.IsChecked = saved != null && saved.AlwaysOnTop;
                locked.IsChecked = saved != null && saved.Locked;
                SetTheme(saved == null ? "dark" : saved.Theme);
                updating = false;
                ShowMessage("", false);
            }
            formCaption.Text = editing == null ? "새 위젯" : "위젯 설정";
            cancelEdit.Visibility = editing == null ? Visibility.Collapsed : Visibility.Visible;
            UpdateSupportedFeatures(changed || reload);
            RenderList();
        }

        private void UpdateSupportedFeatures(bool initializeNew)
        {
            var option = monitorSelector.SelectedItem as MonitorOption;
            var monitor = option == null ? null : option.Device;
            ConfigureCheck(brightness, monitor, 0x10); ConfigureCheck(contrast, monitor, 0x12); ConfigureCheck(volume, monitor, 0x62);
            if (editing == null && initializeNew)
            {
                updating = true;
                foreach (var check in controlChecks()) if (!check.IsEnabled) check.IsChecked = false;
                if (!controlChecks().Any(x => x.IsEnabled && x.IsChecked == true))
                {
                    var first = controlChecks().FirstOrDefault(x => x.IsEnabled);
                    if (first != null) first.IsChecked = true;
                }
                updating = false;
            }
            featureHint.Text = option == null ? "모니터를 선택하세요."
                : monitor == null ? "연결 대기" : controlChecks().Any(x => x.IsEnabled) ? "" : "사용 가능한 조절 항목이 없습니다.";
            UpdateSaveButton();
        }

        private IEnumerable<CheckBox> controlChecks() { return new[] { brightness, contrast, volume }; }
        private static void ConfigureCheck(CheckBox check, MonitorDevice monitor, byte code)
        {
            check.IsEnabled = monitor != null && monitor.Features.Any(x => x.Definition.Code == code && x.Writable);
            check.ToolTip = check.IsEnabled ? null : monitor == null ? "연결 대기 중에는 기존 표시 항목을 유지합니다." : "이 모니터에서는 지원하지 않습니다.";
        }

        private void UpdateSaveButton()
        {
            if (saveButton == null) return;
            var option = monitorSelector.SelectedItem as MonitorOption;
            saveButton.IsEnabled = editing != null || (option != null && option.Device != null && controlChecks().Any(x => x.IsEnabled && x.IsChecked == true)
                && manager.Settings.Widgets.Count < SettingsStore.MaximumWidgets && manager.FindByMonitor(option.Id) == null);
            saveButton.Content = editing == null ? "위젯 추가" : "변경 저장";
            AutomationProperties.SetName(saveButton, editing == null ? "위젯 추가" : "변경 저장");
        }

        private void SaveWidget()
        {
            var option = monitorSelector.SelectedItem as MonitorOption;
            if (option == null || (option.Device == null && editing == null)) { ShowMessage("연결된 모니터를 선택하세요.", true); return; }
            var existing = manager.FindByMonitor(option.Id);
            if (existing != editing)
            {
                LoadSelectedMonitor(true); ShowMessage(existing == null ? "위젯이 변경되었습니다." : "이미 추가된 위젯입니다.", false); return;
            }
            bool showBrightness = brightness.IsEnabled ? brightness.IsChecked == true : editing != null && editing.ShowBrightness;
            bool showContrast = contrast.IsEnabled ? contrast.IsChecked == true : editing != null && editing.ShowContrast;
            bool showVolume = volume.IsEnabled ? volume.IsChecked == true : editing != null && editing.ShowVolume;
            if (!showBrightness && !showContrast && !showVolume) { ShowMessage("표시할 조절 항목을 하나 이상 선택해 주세요.", true); return; }
            bool isNew = editing == null;
            var placement = isNew ? new WidgetPlacement() : Copy(editing);
            placement.MonitorId = option.Id; placement.MonitorName = option.Device == null ? editing.MonitorName : option.Device.Name;
            placement.ShowBrightness = showBrightness; placement.ShowContrast = showContrast; placement.ShowVolume = showVolume;
            placement.AlwaysOnTop = onTop.IsChecked == true; placement.Locked = locked.IsChecked == true; placement.Theme = theme;
            try
            {
                if (isNew)
                {
                    var screen = Forms.Screen.AllScreens.FirstOrDefault(x => x.DeviceName.Replace(@"\\.\", "") == option.Device.Display)
                        ?? Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
                    int offset = manager.Settings.Widgets.Count % 6 * 26;
                    placement.Left = screen.WorkingArea.Right - 400 - offset;
                    placement.Top = screen.WorkingArea.Top + 30 + offset;
                    manager.Add(placement);
                }
                else manager.Update(placement);
                RefreshMonitors(); LoadSelectedMonitor(true);
                ShowMessage(isNew ? "위젯 추가됨" : "저장됨", false);
            }
            catch (Exception ex) { ShowMessage(ex.Message, true); }
        }

        private void Edit(WidgetPlacement placement)
        {
            SelectMonitor(placement.MonitorId);
            settingsScroll.ScrollToTop();
        }

        private static WidgetPlacement Copy(WidgetPlacement placement)
        {
            return new WidgetPlacement { Id = placement.Id, MonitorId = placement.MonitorId, MonitorName = placement.MonitorName,
                Left = placement.Left, Top = placement.Top, AlwaysOnTop = placement.AlwaysOnTop, Theme = placement.Theme,
                ShowBrightness = placement.ShowBrightness, ShowContrast = placement.ShowContrast, ShowVolume = placement.ShowVolume, Locked = placement.Locked };
        }

        internal void RenderList()
        {
            if (widgetList == null) return;
            if (editing != null && !manager.Settings.Widgets.Any(x => x.Id == editing.Id)) { RefreshMonitors(); return; }
            widgetList.Children.Clear();
            listCount.Text = manager.Settings.Widgets.Count + "개";
            if (manager.Settings.Widgets.Count == 0)
            {
                var empty = Label("배치한 위젯이 없습니다.", 11, "#8FAABB");
                empty.LineHeight = 20; empty.TextWrapping = TextWrapping.Wrap;
                widgetList.Children.Add(new Border { Background = Color("#14232D"), BorderBrush = Color("#2D424F"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(16), Child = empty });
            }
            foreach (var placement in manager.Settings.Widgets.ToList())
            {
                var item = new Grid(); item.ColumnDefinitions.Add(new ColumnDefinition()); item.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                item.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); item.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var details = new StackPanel { Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center }; item.Children.Add(details);
                var name = Label(placement.MonitorName, 12, "#D6E8F2"); name.FontWeight = FontWeights.SemiBold; name.TextTrimming = TextTrimming.CharacterEllipsis; details.Children.Add(name);
                var controls = new List<string>(); if (placement.ShowBrightness) controls.Add("밝기"); if (placement.ShowContrast) controls.Add("명암"); if (placement.ShowVolume) controls.Add("볼륨");
                bool online = manager.Monitors.Any(x => SameId(x.Id, placement.MonitorId));
                var description = Label(String.Join(" · ", controls) + (online ? "" : "  ·  연결 대기"), 10, online ? "#819DAF" : "#C5A878"); description.Margin = new Thickness(0, 5, 0, 0); description.TextWrapping = TextWrapping.Wrap; details.Children.Add(description);
                var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(actions, 1); item.Children.Add(actions);
                var edit = SmallButton("설정", placement.MonitorName + " 위젯 수정"); edit.Click += delegate { Edit(placement); }; actions.Children.Add(edit);
                var show = SmallButton("보기", placement.MonitorName + " 위젯 보기"); show.Click += delegate { manager.Reveal(placement.Id); }; actions.Children.Add(show);
                var remove = SmallButton("삭제", placement.MonitorName + " 위젯 삭제"); remove.Foreground = Color("#DCA99E");
                remove.Click += delegate { manager.Remove(placement.Id); RefreshMonitors(); ShowMessage("위젯 삭제됨", false); }; actions.Children.Add(remove);
                var quickOptions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 13, 0, 0) };
                Grid.SetRow(quickOptions, 1); Grid.SetColumnSpan(quickOptions, 2); item.Children.Add(quickOptions);
                var quickLock = QuickOption(placement, true); quickLock.Margin = new Thickness(0, 0, 22, 0); quickOptions.Children.Add(quickLock);
                quickOptions.Children.Add(QuickOption(placement, false));
                bool selected = editing != null && editing.Id == placement.Id;
                widgetList.Children.Add(new Border { Background = Color(selected ? "#19352F" : "#172731"), BorderBrush = Color(selected ? "#5B9E8D" : "#2C424E"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 8), Child = item });
            }
            UpdateSaveButton();
        }

        private CheckBox QuickOption(WidgetPlacement placement, bool positionLock)
        {
            var check = Check(positionLock ? "위치 잠금" : "항상 위", placement.MonitorName + (positionLock ? " 위치 잠금" : " 항상 위에 표시"));
            check.IsChecked = positionLock ? placement.Locked : placement.AlwaysOnTop;
            bool applying = false;
            RoutedEventHandler apply = delegate {
                if (applying) return;
                try
                {
                    var changed = Copy(placement);
                    if (positionLock) changed.Locked = check.IsChecked == true;
                    else changed.AlwaysOnTop = check.IsChecked == true;
                    manager.Update(changed);
                    if (editing != null && editing.Id == placement.Id)
                    {
                        updating = true;
                        if (positionLock) locked.IsChecked = placement.Locked; else onTop.IsChecked = placement.AlwaysOnTop;
                        updating = false;
                    }
                    ShowMessage("저장됨", false);
                    RenderList();
                }
                catch (Exception ex)
                {
                    applying = true; check.IsChecked = positionLock ? placement.Locked : placement.AlwaysOnTop; applying = false;
                    ShowMessage(ex.Message, true);
                }
            };
            check.Checked += apply; check.Unchecked += apply;
            return check;
        }

        private static Button SmallButton(string text, string automationName)
        {
            var button = new Button { Content = text, Padding = new Thickness(9, 7, 9, 7), Margin = new Thickness(5, 0, 0, 0), FontSize = 10 };
            AutomationProperties.SetName(button, automationName); return button;
        }

        private void ShowMessage(string text, bool error)
        { message.Text = text; message.Foreground = Color(error ? "#F0B394" : "#8FAABA"); }

        private sealed class MonitorOption
        {
            public string Id { get; set; }
            public string Label { get; set; }
            public MonitorDevice Device { get; set; }
            public override string ToString() { return Label; }
        }
    }
}
