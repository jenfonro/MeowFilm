using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MeowFilm.Desktop;

public sealed record ManagementRow(string Id, string Title, string Detail, bool Enabled,
    Action Edit, Action<bool> Toggle, ContextMenu Menu, bool Failed = false,
    Func<IReadOnlyList<FrameworkElement>>? Cells = null);

public sealed record ManagementColumn(string Header, GridLength Width, double MaxWidth = double.PositiveInfinity);

/// <summary>Reusable settings list chrome; item policy and persistence belong to the caller.</summary>
public sealed class ManagementList : Grid
{
    public TextBox Search { get; } = new() { MinWidth = 120, Padding = new(12, 9, 12, 9), Margin = new(0, 0, 12, 0) };
    public StackPanel Tools { get; } = new() { Orientation = Orientation.Horizontal };
    public Button More { get; }
    public IReadOnlySet<string> Selected => selected;
    public event Action? SelectionChanged;
    public event Action? Delete;
    public bool EnableRowDrag
    {
        get => enableRowDrag;
        set { enableRowDrag = value; if (!value) CancelRowDrag(); }
    }
    public event Action<IReadOnlyList<string>>? OrderChanged;
    public bool ShowToolbar { get => toolbar.Visibility == Visibility.Visible; set => toolbar.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
    public bool ShowBulkActions
    {
        get => floating.Visibility == Visibility.Visible;
        set { floating.Visibility = value ? Visibility.Visible : Visibility.Collapsed; card.Padding = new(8, 8, 8, value ? 76 : 8); Count(); }
    }
    private readonly DockPanel toolbar;
    private readonly Border card, floating;
    private readonly Grid header = new() { Margin = new(7, 0, 7, 6), MinHeight = 32, Visibility = Visibility.Collapsed };
    private readonly CheckBox headerAll = new() { Tag = "round", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private IReadOnlyList<ManagementColumn> columns = [new("名称", new(1, GridUnitType.Star)), new("启用", GridLength.Auto), new("", GridLength.Auto), new("", GridLength.Auto)];
    private Point dragStart;
    private string? dragId;
    private bool enableRowDrag;
    private sealed record RowDrag(string Id);
    private RowDrag? activeDrag;
    private Window? dragWindow;
    private DispatcherTimer? dragScrollTimer;
    private ScrollViewer? dragScroll;
    private int dragScrollDirection;
    private Point dragPosition;
    private readonly HashSet<string> selected = [];
    private readonly ListBox list = new() { BorderThickness = new(0), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock empty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 80, 0, 0) };
    private readonly CheckBox all = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button inverse, remove;
    private IReadOnlyList<ManagementRow> rows = [];
    private int total;
    public ManagementList()
    {
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new());
        toolbar = new DockPanel { Margin = new(0, 0, 0, 8) }; DockPanel.SetDock(Tools, Dock.Right); toolbar.Children.Add(Tools); toolbar.Children.Add(Search); Children.Add(toolbar);
        Search.Tag = "搜索"; Search.SetResourceReference(StyleProperty, "MeowFilm.ManagementSearch"); AutomationProperties.SetName(Search, "搜索");
        card = new Border { CornerRadius = new(22), BorderThickness = new(1), Padding = new(8, 8, 8, 76), ClipToBounds = true };
        card.SetResourceReference(FrameworkElement.StyleProperty, "MeowFilm.FloatingSurface");
        card.Effect = null;
        var items = new Grid(); items.RowDefinitions.Add(new() { Height = GridLength.Auto }); items.RowDefinitions.Add(new());
        items.Children.Add(header); SetRow(list, 1); SetRow(empty, 1); items.Children.Add(list); items.Children.Add(empty); card.Child = items; SetRow(card, 1); Children.Add(card);
        items.SizeChanged += (_, e) => items.Clip = new RectangleGeometry(new Rect(new Point(), e.NewSize), 14, 14);
        list.SetResourceReference(StyleProperty, "MeowFilm.ManagementList");
        var template = new DataTemplate(typeof(ManagementRow)) { VisualTree = new FrameworkElementFactory(typeof(ManagementRowView)) }; list.ItemTemplate = template;
        list.SetValue(VirtualizingPanel.IsVirtualizingProperty, true); list.SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
        list.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        list.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        list.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "MeowFilm.ManagementListItem");
        list.SetValue(ScrollViewer.CanContentScrollProperty, true); empty.SetResourceReference(FrameworkElement.StyleProperty, "MeowFilm.MutedText");
        var footer = new DockPanel { LastChildFill = true }; var actions = new StackPanel { Orientation = Orientation.Horizontal };
        inverse = Button("反选", () => { selected.SymmetricExceptWith(rows.Select(r => r.Id)); Changed(); });
        remove = Button("删除", () => Delete?.Invoke()); More = Button("···", () => OpenMenu(More!));
        AutomationProperties.SetName(More, "批量更多操作"); actions.Children.Add(inverse); actions.Children.Add(remove); actions.Children.Add(More); DockPanel.SetDock(actions, Dock.Right); footer.Children.Add(actions); footer.Children.Add(all);
        floating = new Border { CornerRadius = new(26), BorderThickness = new(1), Padding = new(14, 8, 12, 8), Margin = new(12, 0, 12, 12), Height = 52, VerticalAlignment = VerticalAlignment.Bottom, Child = footer,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 22, ShadowDepth = 6, Opacity = .12 } };
        floating.SetResourceReference(FrameworkElement.StyleProperty, "MeowFilm.FloatingSurface");
        SetRow(floating, 1); Children.Add(floating);
        foreach (var button in new[] { inverse, remove, More }) { button.Height = 32; button.MinHeight = 32; button.Padding = new(16, 0, 16, 0); button.SetResourceReference(Control.BackgroundProperty, "MeowFilm.Brush.InputSurface"); }
        all.Tag = "round"; all.SetResourceReference(StyleProperty, "MeowFilm.ManagementCheck");
        all.Click += (_, _) => { selected.Clear(); if (all.IsChecked == true) selected.UnionWith(rows.Select(r => r.Id)); Changed(); };
        AutomationProperties.SetAutomationId(all, "management.selectAll"); AutomationProperties.SetAutomationId(list, "management.list");
        SetIsSharedSizeScope(this, true);
        headerAll.SetResourceReference(StyleProperty, "MeowFilm.ManagementCheck");
        AutomationProperties.SetName(headerAll, "全选");
        headerAll.Click += (_, _) => { selected.Clear(); if (headerAll.IsChecked == true) selected.UnionWith(rows.Select(r => r.Id)); Changed(); };
        list.AllowDrop = true;
        list.PreviewMouseLeftButtonDown += (_, e) =>
        {
            dragId = null;
            if (!CanDragRows || IsInteractive(e.OriginalSource as DependencyObject)) return;
            if (RowAt(e.OriginalSource as DependencyObject) is { } row) { dragId = row.Id; dragStart = e.GetPosition(list); }
        };
        list.PreviewMouseMove += (_, e) =>
        {
            if (dragId is null) return;
            if (!CanDragRows || e.LeftButton != MouseButtonState.Pressed) { dragId = null; return; }
            var point = e.GetPosition(list);
            if (Math.Abs(point.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(point.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var data = BeginRowDrag(dragId);
            if (data is null) return;
            try { DragDrop.DoDragDrop(list, data, DragDropEffects.Move); }
            finally { CancelRowDrag(); }
        };
        list.PreviewMouseLeftButtonUp += (_, _) => dragId = null;
        list.DragOver += (_, e) =>
        {
            e.Effects = UpdateRowDrag(e.Data, e.GetPosition(list)) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        list.DragLeave += (sender, e) => { if (DragViewport(e.GetPosition(list), out _, out _) is null) StopDragScroll(); };
        list.Drop += (_, e) =>
        {
            e.Effects = DropRowDrag(e.Data, e.GetPosition(list)) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        list.QueryContinueDrag += (_, e) =>
        {
            if (!CanDragRows || activeDrag is null) e.Action = DragAction.Cancel;
            if (e.EscapePressed || e.Action == DragAction.Cancel) CancelRowDrag();
        };
        Unloaded += (_, _) => CancelRowDrag();
        IsVisibleChanged += (_, _) => { if (!IsVisible) CancelRowDrag(); };
        IsEnabledChanged += (_, _) => { if (!IsEnabled) CancelRowDrag(); };
    }
    private bool CanDragRows => EnableRowDrag && IsEnabled && IsVisible && rows.Count == total;
    internal bool IsDragAutoScrolling => dragScrollTimer?.IsEnabled == true;
    internal IDataObject? BeginRowDrag(string id)
    {
        CancelRowDrag();
        if (!CanDragRows || !rows.Any(row => row.Id == id)) return null;
        activeDrag = new(id);
        dragWindow = Window.GetWindow(this);
        if (dragWindow is not null) dragWindow.Deactivated += DragWindowDeactivated;
        return new DataObject("MeowFilm.ManagementRow", activeDrag);
    }
    private void DragWindowDeactivated(object? sender, EventArgs e) => CancelRowDrag();
    internal void CancelRowDrag()
    {
        dragId = null; activeDrag = null; StopDragScroll();
        if (dragWindow is not null) dragWindow.Deactivated -= DragWindowDeactivated;
        dragWindow = null;
    }
    private bool IsCurrentDrag(IDataObject data) => CanDragRows && activeDrag is not null &&
        ReferenceEquals(data.GetData("MeowFilm.ManagementRow", false), activeDrag);
    private static T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T found) return found;
            if (FindVisual<T>(child) is { } nested) return nested;
        }
        return null;
    }
    private ScrollViewer? DragViewport(Point position, out Point point, out double height)
    {
        point = default; height = 0;
        var scroll = FindVisual<ScrollViewer>(list);
        if (scroll?.Template.FindName("PART_ScrollContentPresenter", scroll) is not ScrollContentPresenter viewport) return null;
        point = list.TranslatePoint(position, viewport); height = viewport.ActualHeight;
        return height > 0 && point.X >= 0 && point.X < viewport.ActualWidth && point.Y >= 0 && point.Y < height ? scroll : null;
    }
    internal bool UpdateRowDrag(IDataObject data, Point position)
    {
        if (!IsCurrentDrag(data)) { CancelRowDrag(); return false; }
        if (DragViewport(position, out var point, out double height) is not { } scroll) { StopDragScroll(); return false; }
        dragPosition = position;
        double edge = Math.Min(32, height / 3);
        int direction = point.Y < edge ? -1 : point.Y >= height - edge ? 1 : 0;
        if (direction == 0 || (direction < 0 ? scroll.VerticalOffset <= 0 : scroll.VerticalOffset >= scroll.ScrollableHeight))
        { StopDragScroll(); return true; }
        dragScroll = scroll; dragScrollDirection = direction;
        if (dragScrollTimer is null)
        {
            dragScrollTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(80) };
            dragScrollTimer.Tick += DragScrollTick;
        }
        dragScrollTimer.Start();
        return true;
    }
    private void DragScrollTick(object? sender, EventArgs e)
    {
        if (!CanDragRows || activeDrag is null) { CancelRowDrag(); return; }
        if (dragScroll is not { } scroll || !ReferenceEquals(DragViewport(dragPosition, out _, out _), scroll) ||
            (dragScrollDirection < 0 ? scroll.VerticalOffset <= 0 : scroll.VerticalOffset >= scroll.ScrollableHeight))
        { StopDragScroll(); return; }
        // Line scrolling respects the virtualizing panel's logical or pixel scroll units.
        if (dragScrollDirection < 0) scroll.LineUp(); else scroll.LineDown();
    }
    private void StopDragScroll()
    {
        if (dragScrollTimer is not null)
        {
            dragScrollTimer.Stop(); dragScrollTimer.Tick -= DragScrollTick; dragScrollTimer = null;
        }
        dragScroll = null; dragScrollDirection = 0;
    }
    internal bool DropRowDrag(IDataObject data, Point position)
    {
        string? sourceId = IsCurrentDrag(data) && DragViewport(position, out _, out _) is not null ? activeDrag!.Id : null;
        CancelRowDrag();
        if (sourceId is null || list.InputHitTest(position) is not DependencyObject hit ||
            RowAt(hit) is not { } target || ItemsControl.ContainerFromElement(list, hit) is not ListBoxItem container) return false;
        ReportOrder(sourceId, target.Id, list.TranslatePoint(position, container).Y >= container.ActualHeight / 2);
        return true;
    }
    public void SetColumns(IReadOnlyList<ManagementColumn> value)
    {
        columns = value.ToArray(); header.Children.Clear(); ApplyColumns(header);
        header.Children.Add(headerAll);
        for (int index = 0; index < columns.Count; index++)
        {
            var title = new TextBlock { Text = columns[index].Header, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(6, 0, 6, 0),
                HorizontalAlignment = columns[index].Width.IsStar ? HorizontalAlignment.Left : HorizontalAlignment.Center };
            title.SetResourceReference(StyleProperty, "MeowFilm.MutedText");
            SetColumn(title, index + 1); header.Children.Add(title);
        }
        header.Visibility = Visibility.Visible; Fill(); Count();
    }
    internal void ApplyColumns(Grid grid)
    {
        grid.ColumnDefinitions.Clear(); grid.ColumnDefinitions.Add(new() { Width = new(48) });
        for (int index = 0; index < columns.Count; index++)
            grid.ColumnDefinitions.Add(new() { Width = columns[index].Width, MaxWidth = columns[index].MaxWidth,
                SharedSizeGroup = columns[index].Width.IsAuto ? "managementColumn" + index : null });
    }
    internal static bool IsInteractive(DependencyObject? source)
    {
        for (var current = source; current is not null; current = ParentOf(current))
        {
            if (current is ButtonBase or TextBoxBase or PasswordBox or Selector or System.Windows.Documents.Hyperlink) return true;
            if (current is ManagementRowView or ListBoxItem) break;
        }
        return false;
    }
    private static DependencyObject? ParentOf(DependencyObject value) => value is Visual or System.Windows.Media.Media3D.Visual3D
        ? VisualTreeHelper.GetParent(value) : LogicalTreeHelper.GetParent(value);
    private ManagementRow? RowAt(DependencyObject? source) =>
        (ItemsControl.ContainerFromElement(list, source) as ListBoxItem)?.DataContext is ManagementRowState state ? state.Row : null;
    internal void ReportOrder(string sourceId, string targetId, bool after)
    {
        if (!EnableRowDrag || rows.Count != total || sourceId == targetId || !rows.Any(row => row.Id == sourceId) || !rows.Any(row => row.Id == targetId)) return;
        var order = rows.Select(row => row.Id).ToList(); order.Remove(sourceId);
        order.Insert(order.IndexOf(targetId) + (after ? 1 : 0), sourceId);
        if (!order.SequenceEqual(rows.Select(row => row.Id))) OrderChanged?.Invoke(order.ToArray());
    }
    public void SetRows(IReadOnlyList<ManagementRow> value, int total, IEnumerable<string> validIds, string emptyMessage)
    {
        rows = value; this.total = total; selected.IntersectWith(validIds); empty.Text = emptyMessage; empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Fill(); Count();
    }
    private void Fill()
    {
        CancelRowDrag();
        // Only realized rows allocate native controls; state is keyed by stable item IDs.
        list.ItemsSource = rows.Select(row => new ManagementRowState(this, row, selected.Contains(row.Id), value => { if (value) selected.Add(row.Id); else selected.Remove(row.Id); Count(); SelectionChanged?.Invoke(); })).ToArray();
    }
    private void Count()
    {
        headerAll.IsChecked = all.IsChecked = rows.Count > 0 && rows.All(r => selected.Contains(r.Id)); all.Content = $"全选（{selected.Count}/{total}）";
        inverse.IsEnabled = rows.Count > 0; remove.IsEnabled = More.IsEnabled = selected.Count > 0;
    }
    private void Changed() { Fill(); Count(); SelectionChanged?.Invoke(); }
    public static Button Button(string title, Action action)
    {
        var button = new Button { Content = title, Padding = new(12, 8, 12, 8), MinHeight = 36, Margin = new(4, 0, 0, 0) };
        button.SetResourceReference(StyleProperty, "MeowFilm.QuietButton"); AutomationProperties.SetName(button, title); button.Click += (_, _) => action(); return button;
    }
    public static void OpenMenu(Button button) { if (button.ContextMenu is not { } menu) return; menu.PlacementTarget = button; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true; }
}
internal sealed class ManagementRowState(ManagementList owner, ManagementRow row, bool selected, Action<bool> select)
{
    internal ManagementList Owner => owner;
    internal ManagementRow Row => row;
    internal bool Selected { get; private set; } = selected;
    internal void Select(bool value) { Selected = value; select(value); }
}

public sealed class ManagementRowView : Grid
{
    public ManagementRowView()
    {
        DataContextChanged += (_, _) => Render();
        Margin = new(6, 0, 6, 0); MinHeight = 64;
    }
    private void Render()
    {
        Children.Clear(); if (DataContext is not ManagementRowState state) return;
        state.Owner.ApplyColumns(this); var row = state.Row;
        var check = new CheckBox { IsChecked = state.Selected, Tag = "round", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; check.SetResourceReference(StyleProperty, "MeowFilm.ManagementCheck");
        AutomationProperties.SetName(check, "选择 " + row.Title); check.Click += (_, _) => state.Select(check.IsChecked == true); Children.Add(check);
        if (row.Cells is not null)
        {
            var cells = row.Cells();
            if (cells.Count != ColumnDefinitions.Count - 1) throw new InvalidOperationException("Management row cells must match its columns.");
            for (int index = 0; index < cells.Count; index++) { SetColumn(cells[index], index + 1); Children.Add(cells[index]); }
            return;
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 8, 12, 8) };
        var title = new TextBlock { Text = row.Title, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetResourceReference(TextBlock.FontSizeProperty, "MeowFilm.Font.Size.Body");
        title.SetResourceReference(TextBlock.FontWeightProperty, "MeowFilm.Font.Weight.Normal");
        title.SetResourceReference(FrameworkElement.StyleProperty, "MeowFilm.Text"); text.Children.Add(title);
        if (row.Detail.Length > 0) { var detail = new TextBlock { Text = row.Detail, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 4, 0, 0) }; detail.SetResourceReference(TextBlock.FontSizeProperty, "MeowFilm.Font.Size.Caption"); detail.SetResourceReference(FrameworkElement.StyleProperty, row.Failed ? "MeowFilm.ErrorText" : "MeowFilm.MutedText"); text.Children.Add(detail); }
        SetColumn(text, 1); Children.Add(text);
        var enabled = new CheckBox { IsChecked = row.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 12, 0) }; enabled.SetResourceReference(StyleProperty, "MeowFilm.ManagementSwitch"); AutomationProperties.SetName(enabled, "启用 " + row.Title); enabled.Click += (_, _) => row.Toggle(enabled.IsChecked == true); SetColumn(enabled, 2); Children.Add(enabled);
        var edit = ManagementList.Button("编辑", row.Edit); edit.ClearValue(MinHeightProperty); edit.ClearValue(Control.PaddingProperty); edit.SetResourceReference(StyleProperty, "MeowFilm.ManagementRowButton"); AutomationProperties.SetName(edit, "编辑 " + row.Title); SetColumn(edit, 3); Children.Add(edit);
        Button? more = null; more = ManagementList.Button("···", () => ManagementList.OpenMenu(more!)); more.ClearValue(MinHeightProperty); more.ClearValue(Control.PaddingProperty); more.SetResourceReference(StyleProperty, "MeowFilm.ManagementRowButton"); more.ContextMenu = row.Menu; AutomationProperties.SetName(more, row.Title + "更多操作"); SetColumn(more, 4); Children.Add(more);
    }
}
