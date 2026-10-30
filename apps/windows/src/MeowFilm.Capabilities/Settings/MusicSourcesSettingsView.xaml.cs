using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public partial class MusicSourcesSettingsView : UserControl
{
    public static readonly DependencyProperty IsNarrowProperty = DependencyProperty.Register(
        nameof(IsNarrow), typeof(bool), typeof(MusicSourcesSettingsView), new PropertyMetadata(false));
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(MusicSourcesSettingsView), new PropertyMetadata(false));

    private readonly MusicSourceDemo demo = new();
    private readonly DispatcherTimer scrolling;
    private Window? window;
    private ScrollViewer? itemsScroll;
    private DemoMenu? menu;
    private DemoModal? modal;
    private DemoDrag? drag;
    private bool initialized;
    private int geometryBand = -1;
    private bool? shortLayout;

    public bool IsNarrow => (bool)GetValue(IsNarrowProperty);
    public bool IsCompact => (bool)GetValue(IsCompactProperty);
    private bool Ready => initialized && IsLoaded && IsVisible && IsEnabled && demo.IsActive;
    private double RowHeight => IsNarrow ? 84 : 76;

    public MusicSourcesSettingsView()
    {
        scrolling = new(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(32) };
        scrolling.Tick += ScrollTick;
        InitializeComponent();
        DataContext = demo;
        SourceLayout.DataContext = demo;
        demo.Invalidating += RetireInteractions;
        SourceList.ItemContainerGenerator.StatusChanged += (_, _) => ApplyRows();
        IsEnabledChanged += (_, e) => { if (initialized && e.NewValue is false) demo.Fence(); };
        initialized = true;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (double.IsPositiveInfinity(availableSize.Height))
        {
            // Do not inherit an unbounded extent from an accidental outer page scroller.
            double height = 0;
            for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null;
                 parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is ScrollViewer { ViewportHeight: > 0 } scroll)
                { height = scroll.ViewportHeight; break; }
                if (parent is FrameworkElement { ActualHeight: > 0 } element && parent is not Window)
                { height = element.ActualHeight; break; }
            }
            availableSize.Height = height;
        }
        if (initialized && double.IsFinite(availableSize.Width)) ApplyGeometry(Math.Min(availableSize.Width, 1248));
        if (initialized && double.IsFinite(availableSize.Height)) ApplyVerticalGeometry(availableSize.Height);
        return base.MeasureOverride(availableSize);
    }

    protected override Size ArrangeOverride(Size arrangeBounds)
    {
        if (initialized)
        {
            ApplyGeometry(Math.Min(arrangeBounds.Width, 1248));
            ApplyVerticalGeometry(arrangeBounds.Height);
        }
        var result = base.ArrangeOverride(arrangeBounds);
        if (initialized)
        {
            double remaining = Math.Max(0, ManagementContent.RowDefinitions[4].ActualHeight);
            if (double.IsInfinity(SourceTableBody.MaxHeight) || Math.Abs(SourceTableBody.MaxHeight - remaining) > .5)
                SourceTableBody.MaxHeight = remaining;
            AlignHeader();
        }
        return result;
    }

    private void View_Loaded(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        demo.Activate();
        DetachWindow(); window = Window.GetWindow(this);
        if (window is not null) { window.SizeChanged += Window_SizeChanged; window.Deactivated += Window_Deactivated; }
        AttachScroll(); ApplyGeometry(SourceLayout.ActualWidth);
    }
    private void View_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        demo.Deactivate(); DetachWindow(); DetachScroll();
    }
    private void View_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!initialized) return;
        if (e.NewValue is false) demo.Deactivate();
        else if (IsLoaded) demo.Activate();
    }
    private void View_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!initialized) return;
        demo.Fence();
        if (!ReferenceEquals(e.NewValue, demo)) SetCurrentValue(DataContextProperty, demo);
    }
    private void View_SizeChanged(object sender, SizeChangedEventArgs e)
    { if (initialized) { demo.Fence(); ApplyGeometry(SourceLayout.ActualWidth); } }
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => demo.Fence();
    private void Window_Deactivated(object? sender, EventArgs e) => demo.Fence();
    private void DetachWindow()
    {
        if (window is null) return;
        window.SizeChanged -= Window_SizeChanged; window.Deactivated -= Window_Deactivated; window = null;
    }
    private void AttachScroll()
    {
        SourceList.ApplyTemplate();
        var scroll = SourceList.Template.FindName("ItemsScroll", SourceList) as ScrollViewer;
        if (ReferenceEquals(scroll, itemsScroll)) return;
        DetachScroll(); itemsScroll = scroll;
        if (itemsScroll is not null) itemsScroll.ScrollChanged += Items_ScrollChanged;
    }
    private void DetachScroll()
    {
        if (itemsScroll is not null) itemsScroll.ScrollChanged -= Items_ScrollChanged;
        itemsScroll = null;
    }
    private void Items_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0 || e.ViewportHeightChange != 0) CloseMenu();
        if (e.VerticalChange != 0 && drag?.Preview is not null) UpdateDemoDrag(Mouse.GetPosition(SourceList));
        AlignHeader();
    }
    private void AlignHeader()
    {
        if (itemsScroll is null) return;
        double scrollbar = itemsScroll.ComputedVerticalScrollBarVisibility == Visibility.Visible ? SystemParameters.VerticalScrollBarWidth : 0;
        SourceHeader.Margin = new(12, 0, 12 + scrollbar, 0);
    }
    private void ApplyGeometry(double width)
    {
        if (!initialized || width <= 0) return;
        int band = width < 740 ? 2 : width < 1000 ? 1 : 0;
        if (band == geometryBand) return;
        geometryBand = band;
        SetValue(IsNarrowProperty, width < 1000);
        SetValue(IsCompactProperty, width < 740);
        ApplyColumns(SourceHeader);
        AddressHeading.Visibility = IsNarrow ? Visibility.Collapsed : Visibility.Visible;
        PlatformHeading.Visibility = IsCompact ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(SourceFilters, IsCompact ? 1 : 0);
        Grid.SetColumn(SourceFilters, IsCompact ? 0 : 1);
        Grid.SetColumnSpan(SourceFilters, IsCompact ? 2 : 1);
        SourceFilters.Margin = IsCompact ? new(0, shortLayout == true ? 4 : 8, 0, 0) : new(12, 0, 0, 0);
        ToolbarRule.Visibility = IsNarrow ? Visibility.Collapsed : Visibility.Visible;
        PlatformFilter.Width = StateFilter.Width = IsNarrow ? 104 : 114;
        StateFilter.Margin = new(IsNarrow ? 8 : 12, 0, 0, 0);
        ApplyRows();
    }
    private void ApplyVerticalGeometry(double height)
    {
        bool compact = height < 450;
        if (!initialized || shortLayout == compact) return;
        shortLayout = compact;
        ManagementContent.Margin = compact ? new(0, 8, 0, 8) : new(0, 28, 0, 20);
        ManagementTitleRow.Height = compact ? 32 : 36;
        ManagementTitleRow.Margin = new(0, 0, 0, compact ? 8 : 20);
        OfficialRow.Height = compact ? 56 : 64;
        OfficialRow.Margin = new(0, 0, 0, compact ? 8 : 24);
        SourceToolbar.Margin = new(0, 0, 0, compact ? 6 : 16);
        SourceHeader.Height = compact ? 32 : 36;
        ManagementContent.RowDefinitions[3].Height = new(compact ? 32 : 36);
        PlatformFilter.Height = PlatformFilter.MinHeight = StateFilter.Height = StateFilter.MinHeight = compact ? 30 : 34;
        SourceFilters.Margin = IsCompact ? new(0, compact ? 4 : 8, 0, 0) : new(12, 0, 0, 0);
    }
    private void ApplyColumns(Grid grid)
    {
        double[] widths = IsCompact ? [30, 42, -1, 0, 0, 74, 50, 90] :
            IsNarrow ? [32, 44, -1, 0, 126, 86, 58, 98] : [40, 48, -1, -1.4, -.9, 94, 68, 112];
        for (int i = 0; i < widths.Length; i++)
            grid.ColumnDefinitions[i].Width = widths[i] < 0 ? new(-widths[i], GridUnitType.Star) : new(widths[i]);
    }
    private void SourceRow_Loaded(object sender, RoutedEventArgs e)
    { if (sender is Border row && initialized) ApplyRow(row); }
    private void ApplyRows()
    {
        if (!initialized) return;
        for (int i = 0; i < SourceList.Items.Count; i++)
            if (SourceList.ItemContainerGenerator.ContainerFromIndex(i) is DependencyObject container &&
                Descendants(container).OfType<Border>().FirstOrDefault(border => border.Name == "SourceRow") is { } row)
                ApplyRow(row);
    }
    private void ApplyRow(Border row)
    {
        var elements = Descendants(row).OfType<FrameworkElement>().ToArray();
        if (elements.OfType<Grid>().FirstOrDefault(grid => grid.Name == "RowGrid") is { } columns) ApplyColumns(columns);
        foreach (var element in elements)
            element.Visibility = element.Name switch
            {
                "MobileAddress" => IsNarrow ? Visibility.Visible : Visibility.Collapsed,
                "MobilePlatforms" => IsCompact ? Visibility.Visible : Visibility.Collapsed,
                "RowAddress" => IsNarrow ? Visibility.Collapsed : Visibility.Visible,
                "RowPlatforms" => IsCompact ? Visibility.Collapsed : Visibility.Visible,
                _ => element.Visibility
            };
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private MusicSourceDemoHandle? RowHandle(object sender)
    {
        if (!Ready || sender is not FrameworkElement { DataContext: MusicSourceDemoRow row } element ||
            !element.IsEnabled || !element.IsLoaded || !element.IsVisible ||
            ItemsControl.ContainerFromElement(SourceList, element) is not FrameworkElement container ||
            !ReferenceEquals(SourceList.ItemContainerGenerator.ItemFromContainer(container), row)) return null;
        return demo.Capture(row);
    }

    private void SourceSelected_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox box)
        {
            if (RowHandle(box) is { } handle) demo.Select(handle.Row, box.IsChecked == true);
            box.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
        }
    }
    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (Ready) demo.ToggleAll();
        SelectAll.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
    }
    private void SourceEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox box)
        {
            if (RowHandle(box) is { } handle) demo.Enable(handle, box.IsChecked == true);
            box.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
        }
    }
    private void MoveUp_Click(object sender, RoutedEventArgs e) { if (Ready) demo.MoveSelected(-1); }
    private void MoveDown_Click(object sender, RoutedEventArgs e) { if (Ready) demo.MoveSelected(1); }
    private void EnableSelected_Click(object sender, RoutedEventArgs e) { if (Ready) demo.EnableSelected(true); }
    private void DisableSelected_Click(object sender, RoutedEventArgs e) { if (Ready) demo.EnableSelected(false); }
    private void Detect_Click(object sender, RoutedEventArgs e) { if (Ready) demo.DetectSelected(); }
    private void Official_Click(object sender, RoutedEventArgs e) { if (Ready) demo.UseOfficial(); }
    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!Ready) return;
        string platform = PlatformFilter.SelectedIndex == 0 ? "" : (string)((ComboBoxItem)PlatformFilter.SelectedItem).Content;
        string state = StateFilter.SelectedIndex == 0 ? "" : (string)((ComboBoxItem)StateFilter.SelectedItem).Content;
        demo.Filter(platform, state);
    }

    private sealed record DemoMenu(MusicSourceDemoHandle Handle, FrameworkElement Anchor);
    private sealed record DemoModal(long Epoch, MusicSourceDemoHandle? Handle, FrameworkElement? Anchor);
    private bool Current(DemoModal session) => Ready && ReferenceEquals(modal, session) && session.Epoch == demo.Epoch &&
        (session.Handle is not { } handle || demo.Current(handle));
    private bool Current(DemoMenu session) => Ready && ReferenceEquals(menu, session) && demo.Current(session.Handle) &&
        session.Anchor.IsLoaded && session.Anchor.IsVisible && RowHandle(session.Anchor) is { } handle && handle == session.Handle;

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor || RowHandle(sender) is not { } handle) return;
        RetireInteractions();
        var session = new DemoMenu(handle, anchor); menu = session;
        var actions = new StackPanel();
        void AddAction(string title, Action action, bool enabled = true, bool danger = false)
        {
            var button = CreateButton(title, danger ? "Settings.DangerButton" : "Source.Quiet", () =>
            { if (Current(session)) action(); }, title);
            button.Height = 34; button.MinHeight = 34; button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Padding = new(11, 5, 11, 5); button.IsEnabled = enabled; actions.Children.Add(button);
        }
        AddAction("切换音源", () => demo.Use(handle), handle.Row.Enabled && handle.Row.State != MusicSourceDemoState.Error);
        AddAction("加载音源", () => demo.Load(handle));
        AddAction("更新脚本", () => demo.Update(handle));
        AddAction("回退到 " + (handle.Row.PreviousVersion.Length == 0 ? "—" : handle.Row.PreviousVersion),
            () => demo.Rollback(handle), handle.Row.PreviousVersion.Length > 0);
        var rule = new Border { Height = 1, Margin = new(7, 4, 7, 4) };
        rule.SetResourceReference(Border.BackgroundProperty, "Settings.Brush.Rule"); actions.Children.Add(rule);
        AddAction("删除音源", () => OpenDelete(handle, anchor), danger: true);
        SourceMenuSurface.Child = actions; SourceMenuSurface.Margin = new(0); MenuLayer.Visibility = Visibility.Visible;
        SourceMenuSurface.Measure(new Size(168, double.PositiveInfinity));
        double height = SourceMenuSurface.DesiredSize.Height;
        var point = anchor.TranslatePoint(new Point(0, anchor.ActualHeight), SourceLayout);
        double x = Math.Clamp(point.X + anchor.ActualWidth - 168, 8, Math.Max(8, SourceLayout.ActualWidth - 176));
        double y = point.Y + height + 8 <= SourceLayout.ActualHeight ? point.Y + 4 : point.Y - anchor.ActualHeight - height - 4;
        SourceMenuSurface.Margin = new(x, Math.Clamp(y, 8, Math.Max(8, SourceLayout.ActualHeight - height - 8)), 0, 0);
        actions.Children.OfType<Button>().First().Focus();
    }
    private void CloseMenu() { menu = null; MenuLayer.Visibility = Visibility.Collapsed; SourceMenuSurface.Child = null; }
    private void MenuLayer_MouseDown(object sender, MouseButtonEventArgs e)
    { if (ReferenceEquals(e.OriginalSource, MenuLayer)) { CloseMenu(); e.Handled = true; } }
    private void ModalLayer_MouseDown(object sender, MouseButtonEventArgs e)
    { if (ReferenceEquals(e.OriginalSource, ModalLayer)) { CloseModal(); e.Handled = true; } }
    private void CloseModal()
    {
        modal = null; ModalLayer.Visibility = Visibility.Collapsed;
        ModalHeader.Content = ModalBody.Content = null; ModalFooter.Children.Clear();
    }
    private void RetireInteractions() { CloseMenu(); CloseModal(); CancelDemoDrag(); }
    private void View_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && (menu is not null || modal is not null || drag is not null))
        { demo.Fence(); e.Handled = true; }
    }

    private DemoModal OpenModal(string title, MusicSourceDemoHandle? handle, FrameworkElement? anchor)
    {
        RetireInteractions();
        var session = new DemoModal(demo.Epoch, handle, anchor); modal = session;
        var header = new Grid { Margin = new(26, 22, 26, 18) };
        header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var titles = new StackPanel { Orientation = Orientation.Horizontal };
        titles.Children.Add(Text(title, 18, "Settings.Brush.Text", FontWeights.SemiBold));
        var state = Text("演示数据", 12); state.Margin = new(12, 0, 0, 0); state.VerticalAlignment = VerticalAlignment.Center;
        if (title != "演示导出") titles.Children.Add(state);
        header.Children.Add(titles);
        var close = CreateButton("\uE711", "Settings.PopupClose", () => { if (Current(session)) CloseModal(); }, "关闭" + title);
        Grid.SetColumn(close, 1); header.Children.Add(close);
        ModalHeader.Content = header;
        ModalBody.Content = new StackPanel { Margin = new(26, 0, 26, 24) };
        ModalSurface.Width = Math.Max(0, Math.Min(600, SourceLayout.ActualWidth - 32));
        AutomationProperties.SetName(ModalSurface, title);
        ModalSurface.MaxHeight = Math.Max(0, SourceLayout.ActualHeight - 24);
        ModalScroll.MaxHeight = Math.Max(0, SourceLayout.ActualHeight - 178);
        ModalScroll.ScrollToTop(); ModalLayer.Visibility = Visibility.Visible;
        return session;
    }
    private void Footer(DemoModal session, string title, Action action, bool primary = false, bool danger = false)
    {
        var button = CreateButton(title, danger ? "Settings.DangerButton" : primary ? "Settings.PrimaryButton" : "Settings.Button32",
            () => { if (Current(session)) action(); }, title);
        button.Height = button.MinHeight = 36; button.MinWidth = 76;
        button.Margin = new(10, 0, 0, 0); ModalFooter.Children.Add(button);
    }
    private Button CreateButton(string title, string style, Action action, string name)
    {
        var button = new Button { Content = title, Style = (Style)FindResource(style) };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => { if (button.IsEnabled) action(); };
        return button;
    }
    private TextBlock Text(string value, double size = 13, string brush = "Settings.Brush.Muted", FontWeight? weight = null)
    {
        return new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap,
            FontWeight = weight ?? FontWeights.Normal,
            Style = (Style)FindResource(brush == "Settings.Brush.Text" ? "Settings.Text" : "Source.Muted") };
    }
    private TextBox Field(StackPanel body, string label, string name, object data, string path)
    {
        var field = new StackPanel { Margin = new(0, 0, 0, 20) };
        var caption = Text(label); caption.Margin = new(0, 0, 0, 9); field.Children.Add(caption);
        var input = new TextBox { Style = (Style)FindResource("Settings.TextBox"), FontSize = 13, DataContext = data };
        input.SetBinding(TextBox.TextProperty, new Binding(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        AutomationProperties.SetName(input, name); field.Children.Add(input); body.Children.Add(field); return input;
    }
    private WrapPanel Platforms(IEnumerable<string> names)
    {
        var wrap = new WrapPanel();
        foreach (string name in names)
        {
            var label = Text(name, 13, "Settings.Brush.Text"); label.Margin = new(0, 0, 18, 0); wrap.Children.Add(label);
        }
        return wrap;
    }
    private CheckBox Toggle(StackPanel body, string label, bool enabled, string name)
    {
        var row = new Grid { Height = 30 };
        row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var caption = Text(label, 13, "Settings.Brush.Text"); caption.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(caption);
        var toggle = new CheckBox { Style = (Style)FindResource("Settings.Switch"), IsChecked = enabled, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, name); Grid.SetColumn(toggle, 1); row.Children.Add(toggle); body.Children.Add(row); return toggle;
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (RowHandle(sender) is not { } handle) return;
        var session = OpenModal("编辑音源", handle, sender as FrameworkElement);
        var body = (StackPanel)ModalBody.Content;
        var edit = new MusicSourceDemoEdit(handle.Row);
        var name = Field(body, "音源名称", "演示音源名称", edit, nameof(edit.Name));
        Field(body, edit.AddressLabel, "演示音源地址", edit, nameof(edit.Address));
        var metadata = new Grid { Margin = new(0, 2, 0, 24) };
        foreach (var _ in Enumerable.Range(0, 3)) metadata.ColumnDefinitions.Add(new());
        (string Label, string Value)[] fields = [("当前版本", edit.Version), ("上一版本", edit.PreviousVersion), ("导入方式", edit.Origin)];
        for (int i = 0; i < fields.Length; i++)
        {
            var column = new StackPanel { Margin = new(0, 0, 22, 0) };
            var label = Text(fields[i].Label); label.Margin = new(0, 0, 0, 9); column.Children.Add(label);
            column.Children.Add(Text(fields[i].Value, 14, "Settings.Brush.Text")); Grid.SetColumn(column, i); metadata.Children.Add(column);
        }
        body.Children.Add(metadata);
        var platforms = Text("支持平台"); platforms.Margin = new(0, 0, 0, 9); body.Children.Add(platforms);
        var wrap = Platforms(edit.Platforms); wrap.Margin = new(0, 0, 0, 22); body.Children.Add(wrap);
        var enabled = Toggle(body, "启用音源", edit.Enabled, "编辑演示音源启用状态");
        enabled.DataContext = edit;
        enabled.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(edit.Enabled))
            { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        Footer(session, "取消", CloseModal);
        Footer(session, "保存", () => demo.Save(handle, edit), primary: true);
        name.Focus();
    }
    private void OpenDelete(MusicSourceDemoHandle handle, FrameworkElement anchor)
    {
        if (!demo.Current(handle)) return;
        var session = OpenModal("删除音源", handle, anchor);
        ((StackPanel)ModalBody.Content).Children.Add(Text($"删除演示音源“{handle.Row.Name}”？", 14, "Settings.Brush.Text"));
        Footer(session, "取消", CloseModal);
        Footer(session, "删除", () => demo.Remove(handle), danger: true);
    }
    private void Import_Click(object sender, RoutedEventArgs e)
    { if (Ready && demo.CanImport) OpenNetworkImport(sender as FrameworkElement); }
    private void ImportTabs(StackPanel body, DemoModal session, bool local, Func<bool> enabled)
    {
        var tabs = new Border { Width = 260, Height = 38, CornerRadius = new(6), BorderThickness = new(1), Padding = new(3),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 0, 0, 24) };
        tabs.SetResourceReference(Border.BackgroundProperty, "Settings.Brush.Ground");
        tabs.SetResourceReference(Border.BorderBrushProperty, "Settings.Brush.Border");
        var choices = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (bool file in new[] { false, true })
        {
            var button = CreateButton(file ? "本地文件" : "网络地址", "Source.Quiet", () =>
            {
                if (!Current(session)) return;
                if (file) OpenLocalImport(session.Anchor, enabled()); else OpenNetworkImport(session.Anchor, enabled());
            }, file ? "演示本地文件导入" : "演示网络地址导入");
            button.Width = 126; button.Height = button.MinHeight = 30;
            if (file == local)
            {
                button.SetResourceReference(Control.BackgroundProperty, "MeowFilm.Brush.Surface");
                button.SetResourceReference(Control.BorderBrushProperty, "Settings.Brush.Border");
                button.BorderThickness = new(1);
            }
            choices.Children.Add(button);
        }
        tabs.Child = choices; body.Children.Add(tabs);
    }
    private void OpenNetworkImport(FrameworkElement? anchor, bool initialEnabled = false)
    {
        var session = OpenModal("导入音源", null, anchor);
        var body = (StackPanel)ModalBody.Content;
        CheckBox? enabled = null;
        ImportTabs(body, session, false, () => enabled?.IsChecked == true);
        var caption = Text("网络地址"); caption.Margin = new(0, 0, 0, 9); body.Children.Add(caption);
        var joined = new Grid { Height = 40 };
        joined.ColumnDefinitions.Add(new()); joined.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var item = MusicSourceDemo.NetworkImport();
        var address = new TextBox { Text = item.Address, FontSize = 13, Style = (Style)FindResource("Settings.TextBox") };
        AutomationProperties.SetName(address, "演示网络导入地址"); joined.Children.Add(address);
        var result = new Border { CornerRadius = new(8), BorderThickness = new(1), Padding = new(18, 17, 18, 17),
            Margin = new(0, 22, 0, 22) };
        result.SetResourceReference(Border.BackgroundProperty, "Settings.Brush.Ground");
        result.SetResourceReference(Border.BorderBrushProperty, "Settings.Brush.Border");
        var read = CreateButton("读取", "Settings.Button32", () =>
        {
            if (!Current(session)) return;
            if (!MusicSourceDemo.ValidAddress(address.Text, "网络")) { demo.Notice("演示地址无效"); return; }
            address.Text = item.Address;
            result.Visibility = Visibility.Visible;
            ModalFooter.Children.OfType<Button>().Last().IsEnabled = demo.Sources.Count < MusicSourceDemo.MaximumSources;
        }, "读取演示网络音源");
        read.Height = read.MinHeight = 40; read.Margin = new(-1, 0, 0, 0);
        Grid.SetColumn(read, 1); joined.Children.Add(read); body.Children.Add(joined);
        var summary = new StackPanel();
        var heading = new Grid(); heading.ColumnDefinitions.Add(new()); heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        heading.Children.Add(Text(item.Name, 15, "Settings.Brush.Text"));
        var format = Text("LX 脚本", 12); Grid.SetColumn(format, 1); heading.Children.Add(format); summary.Children.Add(heading);
        var version = Text("版本 " + item.Version, 12); version.Margin = new(0, 3, 0, 12); summary.Children.Add(version);
        summary.Children.Add(Platforms(item.Platforms)); result.Child = summary; body.Children.Add(result);
        enabled = Toggle(body, "导入后启用", initialEnabled, "网络演示音源导入后启用");
        Footer(session, "取消", CloseModal);
        Footer(session, "导入", () => demo.Import(session.Epoch, [item], enabled.IsChecked == true), primary: true);
        ModalFooter.Children.OfType<Button>().Last().IsEnabled = demo.CanImport;
        address.TextChanged += (_, _) =>
        {
            if (!Current(session)) return;
            result.Visibility = Visibility.Collapsed; ModalFooter.Children.OfType<Button>().Last().IsEnabled = false;
        };
        address.Focus();
    }
    private void OpenLocalImport(FrameworkElement? anchor, bool initialEnabled = false, MusicSourceDemoImport[]? items = null)
    {
        var candidates = items ?? MusicSourceDemo.LocalImports();
        var session = OpenModal("导入音源", null, anchor);
        var body = (StackPanel)ModalBody.Content;
        CheckBox? enabled = null;
        ImportTabs(body, session, true, () => enabled?.IsChecked == true);
        var heading = new Grid { Margin = new(0, 0, 0, 14) };
        heading.ColumnDefinitions.Add(new()); heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var caption = Text("文件（.js）"); caption.VerticalAlignment = VerticalAlignment.Center; heading.Children.Add(caption);
        var choose = CreateButton("选择文件", "Settings.Button32", () =>
        { if (Current(session)) OpenLocalImport(anchor, enabled?.IsChecked == true); }, "选择演示文件");
        Grid.SetColumn(choose, 1); heading.Children.Add(choose); body.Children.Add(heading);
        var files = new Border { CornerRadius = new(8), BorderThickness = new(1), Padding = new(14, 0, 14, 0) };
        files.SetResourceReference(Border.BorderBrushProperty, "Settings.Brush.Border");
        var rows = new StackPanel();
        foreach (var item in candidates)
        {
            var row = new Grid { Height = 76 };
            row.ColumnDefinitions.Add(new() { Width = new(26) }); row.ColumnDefinitions.Add(new());
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var icon = new TextBlock { Text = "\uE8A5", FontSize = 20, VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(TextBlock.FontFamilyProperty, "MeowFilm.Font.Icons");
            icon.SetResourceReference(TextBlock.ForegroundProperty, "Settings.Brush.Muted"); row.Children.Add(icon);
            var identity = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new(14, 0, 0, 0) };
            identity.Children.Add(Text(item.Address, 14, "Settings.Brush.Text"));
            var details = Text(item.FileMetadata, 12); details.Margin = new(0, 5, 0, 0); identity.Children.Add(details);
            Grid.SetColumn(identity, 1); row.Children.Add(identity);
            var remove = CreateButton("\uE711", "Settings.PopupClose", () =>
            {
                if (Current(session)) OpenLocalImport(anchor, enabled?.IsChecked == true,
                    candidates.Where(candidate => !ReferenceEquals(candidate, item)).ToArray());
            }, "移除演示文件" + item.Address);
            remove.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(remove, 2); row.Children.Add(remove);
            if (!ReferenceEquals(item, candidates.Last()))
            {
                var rule = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom };
                rule.SetResourceReference(Border.BackgroundProperty, "Settings.Brush.Rule"); Grid.SetColumnSpan(rule, 3); row.Children.Add(rule);
            }
            rows.Children.Add(row);
        }
        files.Child = rows; body.Children.Add(files);
        var count = Text($"{candidates.Length} 个音源", 12); count.Margin = new(0, 12, 0, 20); body.Children.Add(count);
        enabled = Toggle(body, "导入后启用", initialEnabled, "本地演示音源导入后启用");
        Footer(session, "取消", CloseModal);
        Footer(session, "导入", () => demo.Import(session.Epoch, candidates, enabled.IsChecked == true), primary: true);
        ModalFooter.Children.OfType<Button>().Last().IsEnabled =
            candidates.Length > 0 && demo.Sources.Count + candidates.Length <= MusicSourceDemo.MaximumSources;
        choose.Focus();
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!Ready) return;
        var session = OpenModal("演示导出", null, sender as FrameworkElement);
        var text = new TextBox { Text = demo.Export(), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = Math.Max(0, Math.Min(280, SourceLayout.ActualHeight - 202)), FontSize = 12, Style = (Style)FindResource("Settings.TextBox") };
        AutomationProperties.SetName(text, "演示导出文本"); ((StackPanel)ModalBody.Content).Children.Add(text);
        Footer(session, "全选", () => { text.Focus(); text.SelectAll(); }); Footer(session, "关闭", CloseModal);
        text.Focus();
    }

    private sealed class DemoDrag(Thumb grip, MusicSourceDemoHandle handle, MusicSourceDemoRow[] rows, Point start,
        double startOffset, double width, BitmapSource snapshot, bool contrast)
    {
        internal Thumb Grip { get; } = grip;
        internal MusicSourceDemoHandle Handle { get; } = handle;
        internal MusicSourceDemoRow[] Rows { get; } = rows;
        internal Point Start { get; } = start;
        internal double StartOffset { get; } = startOffset;
        internal double Width { get; } = width;
        internal BitmapSource Snapshot { get; } = snapshot;
        internal bool Contrast { get; } = contrast;
        internal int SourceIndex { get; } = Array.IndexOf(rows, handle.Row);
        internal int Destination { get; set; } = -1;
        internal SettingsReorderShift Shift { get; } = new();
        internal DemoDragAdorner? Preview { get; set; }
        internal AdornerLayer? Layer { get; set; }
    }
    private void Grip_DragStarted(object sender, DragStartedEventArgs e)
    { if (sender is Thumb grip) BeginDemoDrag(grip, Mouse.GetPosition(SourceList)); }
    private void Grip_DragDelta(object sender, DragDeltaEventArgs e)
    { if (drag is { } session && ReferenceEquals(sender, session.Grip)) UpdateDemoDrag(Mouse.GetPosition(SourceList)); }
    private void Grip_DragCompleted(object sender, DragCompletedEventArgs e)
    { if (drag is { } session && ReferenceEquals(sender, session.Grip)) CompleteDemoDrag(Mouse.GetPosition(SourceList), e.Canceled); }
    private void Grip_LostMouseCapture(object sender, MouseEventArgs e)
    {
        // Thumb releases normal capture before raising its non-cancelled DragCompleted event.
        if (drag is { } session && ReferenceEquals(sender, session.Grip) && session.Grip.IsDragging) CancelDemoDrag();
    }
    private void Grip_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down) || (Keyboard.Modifiers & ModifierKeys.Alt) == 0 || RowHandle(sender) is not { } handle) return;
        demo.SelectOnly(handle.Row); demo.MoveSelected(e.Key == Key.Up ? -1 : 1); e.Handled = true;
    }
    internal void BeginDemoDrag(Thumb grip, Point point)
    {
        if (RowHandle(grip) is not { } original) return;
        RetireInteractions(); demo.SelectOnly(original.Row);
        if (demo.Capture(original.Row) is not { } handle ||
            ItemsControl.ContainerFromElement(SourceList, grip) is not FrameworkElement container) return;
        AttachScroll(); container.UpdateLayout();
        var contents = Descendants(container).OfType<Grid>().Single(grid => grid.Name == "RowGrid");
        var bitmap = SettingsReorderDrag.Snapshot(contents);
        drag = new(grip, handle, demo.VisibleRows(), point, itemsScroll?.VerticalOffset ?? 0, container.ActualWidth, bitmap,
            DesktopTheme.GetIsHighContrast(this));
    }
    private bool Current(DemoDrag session) => Ready && ReferenceEquals(drag, session) && demo.Current(session.Handle) &&
        session.Contrast == DesktopTheme.GetIsHighContrast(this);
    internal void UpdateDemoDrag(Point point)
    {
        if (drag is not { } session) return;
        if (!Current(session)) { CancelDemoDrag(); return; }
        if (session.Preview is null)
        {
            if (Math.Abs(point.X - session.Start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(point.Y - session.Start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            session.Layer = AdornerLayer.GetAdornerLayer(SourceList);
            if (session.Layer is null) { CancelDemoDrag(); return; }
            session.Preview = new(SourceList, session.Snapshot, session.Width, RowHeight, session.Contrast);
            session.Layer.Add(session.Preview); demo.SetDragging(session.Handle.Row);
            if (session.Grip.IsDragging) scrolling.Start();
        }
        session.Destination = -1;
        double offset = itemsScroll?.VerticalOffset ?? 0;
        // Keep the pointer at the same place on the lifted row; the row centre decides the slot.
        double top = point.Y - (session.Start.Y + session.StartOffset - session.SourceIndex * RowHeight);
        if (point.X >= 0 && point.X <= SourceList.ActualWidth && point.Y >= 0 && point.Y <= SourceList.ActualHeight)
        {
            double center = top + offset + RowHeight / 2;
            int slot = Math.Clamp((int)Math.Floor(center / RowHeight - .5) + 1, 0, session.Rows.Length);
            session.Destination = Math.Clamp(slot - (session.SourceIndex < slot ? 1 : 0), 0, session.Rows.Length - 1);
        }
        int target = session.Destination < 0 ? session.SourceIndex : session.Destination;
        bool animate = SystemParameters.ClientAreaAnimation && !session.Contrast;
        for (int index = 0; index < SourceList.Items.Count; index++)
            if (SourceList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement row)
                session.Shift.Apply(row, index, session.SourceIndex, target, RowHeight, animate);
        session.Preview.Move(top);
    }
    internal void CompleteDemoDrag(Point point, bool cancelled)
    {
        if (drag is not { } session) return;
        if (!cancelled) UpdateDemoDrag(point);
        var target = !cancelled && Current(session) && session.Preview is not null && session.Destination >= 0 &&
            session.Destination != session.SourceIndex ? session.Rows[session.Destination] : null;
        ResetDrag();
        if (target is not null) demo.Move(session.Handle, target);
    }
    internal void CancelDemoDrag()
    {
        var grip = drag?.Grip; ResetDrag();
        if (grip?.IsDragging == true) grip.CancelDrag();
    }
    private void ResetDrag()
    {
        scrolling.Stop();
        var previous = drag; drag = null;
        previous?.Shift.Clear();
        if (previous?.Preview is { } preview) previous.Layer?.Remove(preview);
        if (demo.DraggingSource is not null) demo.SetDragging(null);
    }
    private void ScrollTick(object? sender, EventArgs e)
    {
        if (drag is not { } session || !Current(session) || Mouse.LeftButton != MouseButtonState.Pressed)
        { CancelDemoDrag(); return; }
        var point = Mouse.GetPosition(SourceList);
        if (itemsScroll is not null && point.X >= 0 && point.X <= SourceList.ActualWidth)
        {
            double offset = point.Y < 28 ? Math.Max(0, itemsScroll.VerticalOffset - 12) : point.Y > SourceList.ActualHeight - 28
                ? Math.Min(itemsScroll.ScrollableHeight, itemsScroll.VerticalOffset + 12) : itemsScroll.VerticalOffset;
            if (offset != itemsScroll.VerticalOffset) { itemsScroll.ScrollToVerticalOffset(offset); itemsScroll.UpdateLayout(); }
        }
        UpdateDemoDrag(point);
    }
    private sealed class DemoDragAdorner : Adorner
    {
        private readonly VisualCollection visuals;
        private readonly Border card;
        private readonly double width, height;
        private double top;

        internal DemoDragAdorner(ItemsControl owner, BitmapSource snapshot, double width, double height, bool contrast) : base(owner)
        {
            // Inherited UI properties can enumerate visual children while they change.
            visuals = new(this);
            this.width = width; this.height = height; IsHitTestVisible = false; ClipToBounds = true;
            card = new Border
            {
                BorderBrush = contrast ? SystemColors.WindowTextBrush : (Brush)owner.FindResource("Settings.Brush.SelectedBorder"),
                BorderThickness = new(1), CornerRadius = new(6), Padding = new(10, 0, 10, 0),
                Background = contrast ? SystemColors.WindowBrush : (Brush)owner.FindResource("MeowFilm.Brush.Surface"),
                Child = new Image { Source = snapshot, Stretch = Stretch.Fill }
            };
            if (!contrast)
                card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 8, Opacity = .12 };
            visuals.Add(card);
        }
        internal void Move(double position) { top = position; InvalidateArrange(); }
        protected override int VisualChildrenCount => visuals.Count;
        protected override Visual GetVisualChild(int index) => visuals[index];
        protected override Size MeasureOverride(Size constraint)
        { card.Measure(new(width, height)); return AdornedElement.RenderSize; }
        protected override Size ArrangeOverride(Size finalSize)
        {
            double y = Math.Clamp(top, 0, Math.Max(0, finalSize.Height - height));
            card.Arrange(new Rect(0, y, Math.Min(width, finalSize.Width), height)); return finalSize;
        }
    }
}
