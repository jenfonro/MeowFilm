using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Desktop;

public sealed record ManagementRow(string Id, string Title, string Detail, bool Enabled,
    Action Edit, Action<bool> Toggle, MenuFlyout Menu, bool Failed = false,
    Func<IReadOnlyList<FrameworkElement>>? Cells = null);
public sealed record ManagementColumn(string Header, GridLength Width, double MaxWidth = double.PositiveInfinity);

/// <summary>Virtualized settings rows; native ListView owns drag, auto-scroll and keyboard reordering.</summary>
public sealed class ManagementList : UserControl
{
    public TextBox Search { get; } = new() { MinWidth = 120, Padding = new(12, 9, 12, 9), Margin = new(0, 0, 12, 0), PlaceholderText = "搜索" };
    public StackPanel Tools { get; } = new() { Orientation = Orientation.Horizontal };
    public Button More { get; }
    public IReadOnlySet<string> Selected => selected;
    public event Action? SelectionChanged;
    public event Action? Delete;
    public event Action<IReadOnlyList<string>>? OrderChanged;
    private readonly Grid layout = new();
    private bool enableRowDrag;
    public bool EnableRowDrag { get => enableRowDrag; set { enableRowDrag = value; UpdateDragAdmission(); } }
    public bool ShowToolbar { get => toolbar.Visibility == Visibility.Visible; set => toolbar.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
    public bool ShowBulkActions
    {
        get => floating.Visibility == Visibility.Visible;
        set { floating.Visibility = value ? Visibility.Visible : Visibility.Collapsed; card.Padding = new(8, 8, 8, value ? 76 : 8); Count(); }
    }
    private readonly DockPanel toolbar;
    private readonly Border card, floating;
    private readonly Grid header = new() { Margin = new(7, 0, 7, 6), MinHeight = 32, Visibility = Visibility.Collapsed };
    private readonly CheckBox headerAll = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private IReadOnlyList<ManagementColumn> columns = [new("名称", new(1, GridUnitType.Star)), new("启用", GridLength.Auto), new("", GridLength.Auto), new("", GridLength.Auto)];
    private readonly HashSet<string> selected = [];
    private readonly ListView list = new() { BorderThickness = new(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), HorizontalContentAlignment = HorizontalAlignment.Stretch, SelectionMode = ListViewSelectionMode.None, AllowDrop = true };
    private readonly TextBlock empty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 80, 0, 0) };
    private readonly CheckBox all = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button inverse, remove;
    private readonly ObservableCollection<ManagementRowState> items = [];
    private IReadOnlyList<ManagementRow> rows = [];
    private int total;
    private string[]? draggingOrder;
    private IDisposable? visibility;
    private readonly HashSet<Grid> columnRows = [];
    private double[] sharedWidths = [];
    private bool measuringColumns;
    private void MeasureColumns(object? sender, object args)
    {
        if (measuringColumns) return;
        measuringColumns = true;
        try
        {
            var widths = new double[columns.Count];
            foreach (var grid in columnRows.Prepend(header))
                foreach (var cell in grid.Children.OfType<FrameworkElement>())
                {
                    int index = Grid.GetColumn(cell) - 1;
                    if (index < 0 || index >= columns.Count || !columns[index].Width.IsAuto) continue;
                    cell.Measure(new Windows.Foundation.Size(columns[index].MaxWidth, double.PositiveInfinity));
                    widths[index] = Math.Min(columns[index].MaxWidth, Math.Max(widths[index], cell.DesiredSize.Width));
                }
            if (sharedWidths.SequenceEqual(widths)) return;
            sharedWidths = widths;
            foreach (var grid in columnRows.Prepend(header))
                for (int index = 0; index < columns.Count && index + 1 < grid.ColumnDefinitions.Count; index++)
                    if (columns[index].Width.IsAuto && widths[index] > 0) grid.ColumnDefinitions[index + 1].Width = new GridLength(widths[index]);
        }
        finally { measuringColumns = false; }
    }
    internal void AttachColumns(Grid row)
    {
        if (columnRows.Add(row)) row.LayoutUpdated += MeasureColumns;
    }
    internal void DetachColumns(Grid row) { columnRows.Remove(row); row.LayoutUpdated -= MeasureColumns; }

    public ManagementList()
    {
        Content = layout;
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new());
        toolbar = new DockPanel { Margin = new(0, 0, 0, 8) }; DockPanel.SetDock(Tools, Dock.Right); toolbar.Children.Add(Tools); toolbar.Children.Add(Search); layout.Children.Add(toolbar);
        Search.UseResource(StyleProperty, "MeowFilm.ManagementSearch"); AutomationProperties.SetName(Search, "搜索");
        card = new Border { CornerRadius = new(22), BorderThickness = new(1), Padding = new(8, 8, 8, 76) };
        card.UseResource(StyleProperty, "MeowFilm.FloatingSurface");
        var content = new Grid(); content.RowDefinitions.Add(new() { Height = GridLength.Auto }); content.RowDefinitions.Add(new());
        content.Children.Add(header); Grid.SetRow(list, 1); Grid.SetRow(empty, 1); content.Children.Add(list); content.Children.Add(empty); card.Child = content; Grid.SetRow(card, 1); layout.Children.Add(card);
        list.UseResource(StyleProperty, "MeowFilm.ManagementList");
        list.UseResource(ItemsControl.ItemContainerStyleProperty, "MeowFilm.ManagementListItem");
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        list.ItemsSource = items;
        empty.UseResource(StyleProperty, "MeowFilm.MutedText");
        var footer = new DockPanel(); var actions = new StackPanel { Orientation = Orientation.Horizontal };
        inverse = Button("反选", () => { selected.SymmetricExceptWith(rows.Select(row => row.Id)); Changed(); });
        remove = Button("删除", () => Delete?.Invoke()); More = Button("···", () => OpenMenu(More!));
        AutomationProperties.SetName(More, "批量更多操作"); actions.Children.Add(inverse); actions.Children.Add(remove); actions.Children.Add(More); DockPanel.SetDock(actions, Dock.Right); footer.Children.Add(actions); footer.Children.Add(all);
        floating = new Border { CornerRadius = new(26), BorderThickness = new(1), Padding = new(14, 8, 12, 8), Margin = new(12, 0, 12, 12), Height = 52, VerticalAlignment = VerticalAlignment.Bottom, Child = footer };
        floating.UseResource(StyleProperty, "MeowFilm.FloatingSurface"); Grid.SetRow(floating, 1); layout.Children.Add(floating);
        foreach (var button in new[] { inverse, remove, More }) { button.Height = button.MinHeight = 32; button.Padding = new(16, 0, 16, 0); button.UseResource(Control.BackgroundProperty, "MeowFilm.Brush.InputSurface"); }
        all.UseResource(StyleProperty, "MeowFilm.ManagementCheck");
        all.Click += (_, _) => SelectAll(all.IsChecked == true);
        headerAll.UseResource(StyleProperty, "MeowFilm.ManagementCheck"); headerAll.Click += (_, _) => SelectAll(headerAll.IsChecked == true);
        AutomationProperties.SetName(headerAll, "全选"); AutomationProperties.SetAutomationId(all, "management.selectAll"); AutomationProperties.SetAutomationId(list, "management.list");
        list.DragItemsStarting += (_, args) =>
        {
            if (!CanDragRows) { args.Cancel = true; return; }
            draggingOrder = rows.Select(row => row.Id).ToArray();
        };
        list.DragItemsCompleted += (_, args) =>
        {
            var before = draggingOrder; draggingOrder = null;
            if (before is null || !CanDragRows || args.DropResult != Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move) { Fill(); return; }
            var after = items.Select(item => item.Row.Id).ToArray();
            if (!before.SequenceEqual(after)) OrderChanged?.Invoke(after);
        };
        Loaded += (_, _) => visibility = DesktopVisual.ObserveVisibility(this, _ => UpdateDragAdmission());
        Unloaded += (_, _) =>
        {
            visibility?.Dispose(); visibility = null;
            draggingOrder = null; list.CanDragItems = list.CanReorderItems = false;
        };
        IsEnabledChanged += (_, _) => UpdateDragAdmission();
    }
    private bool CanDragRows => EnableRowDrag && IsEnabled && DesktopVisual.IsVisible(this) && rows.Count == total;
    private void UpdateDragAdmission()
    {
        list.CanDragItems = list.CanReorderItems = CanDragRows;
        if (!CanDragRows) draggingOrder = null;
    }
    private void SelectAll(bool value) { selected.Clear(); if (value) selected.UnionWith(rows.Select(row => row.Id)); Changed(); }
    public void SetColumns(IReadOnlyList<ManagementColumn> value)
    {
        columns = value.ToArray(); sharedWidths = []; header.Children.Clear(); ApplyColumns(header); header.Children.Add(headerAll);
        for (int index = 0; index < columns.Count; index++)
        {
            var title = new TextBlock { Text = columns[index].Header, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(6, 0, 6, 0), HorizontalAlignment = columns[index].Width.IsStar ? HorizontalAlignment.Left : HorizontalAlignment.Center };
            title.UseResource(StyleProperty, "MeowFilm.MutedText"); Grid.SetColumn(title, index + 1); header.Children.Add(title);
        }
        header.Visibility = Visibility.Visible; Fill(); Count();
    }
    internal void ApplyColumns(Grid grid)
    {
        grid.ColumnDefinitions.Clear(); grid.ColumnDefinitions.Add(new() { Width = new(48) });
        for (int index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            grid.ColumnDefinitions.Add(new() { Width = column.Width.IsAuto && index < sharedWidths.Length && sharedWidths[index] > 0 ? new GridLength(sharedWidths[index]) : column.Width, MaxWidth = column.MaxWidth });
        }
    }
    internal void ReportOrder(string sourceId, string targetId, bool after)
    {
        if (!EnableRowDrag || rows.Count != total || sourceId == targetId || !rows.Any(row => row.Id == sourceId) || !rows.Any(row => row.Id == targetId)) return;
        var order = rows.Select(row => row.Id).ToList(); order.Remove(sourceId); order.Insert(order.IndexOf(targetId) + (after ? 1 : 0), sourceId);
        if (!order.SequenceEqual(rows.Select(row => row.Id))) OrderChanged?.Invoke(order.ToArray());
    }
    public void SetRows(IReadOnlyList<ManagementRow> value, int total, IEnumerable<string> validIds, string emptyMessage)
    {
        rows = value; this.total = total; selected.IntersectWith(validIds); empty.Text = emptyMessage;
        empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed; Fill(); Count();
    }
    private void Fill()
    {
        draggingOrder = null; items.Clear();
        foreach (var row in rows) items.Add(new(this, row, selected.Contains(row.Id), value => { if (value) selected.Add(row.Id); else selected.Remove(row.Id); Count(); SelectionChanged?.Invoke(); }));
        UpdateDragAdmission();
    }
    private void Count()
    {
        headerAll.IsChecked = all.IsChecked = rows.Count > 0 && rows.All(row => selected.Contains(row.Id)); all.Content = $"全选（{selected.Count}/{total}）";
        inverse.IsEnabled = rows.Count > 0; remove.IsEnabled = More.IsEnabled = selected.Count > 0;
    }
    private void Changed() { Fill(); Count(); SelectionChanged?.Invoke(); }
    public static Button Button(string title, Action action)
    {
        var button = new Button { Content = title, Padding = new(12, 8, 12, 8), MinHeight = 36, Margin = new(4, 0, 0, 0) };
        button.UseResource(StyleProperty, "MeowFilm.QuietButton"); AutomationProperties.SetName(button, title); button.Click += (_, _) => action(); return button;
    }
    public static void OpenMenu(Button button) => (button.ContextFlyout as MenuFlyout)?.ShowAt(button, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Bottom });
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
    private ManagementList? owner;
    public ManagementRowView()
    {
        DataContextChanged += (_, _) => Render();
        Loaded += (_, _) => { if (DataContext is ManagementRowState state) { owner = state.Owner; owner.AttachColumns(this); } };
        Unloaded += (_, _) => { owner?.DetachColumns(this); owner = null; };
        Margin = new(6, 0, 6, 0); MinHeight = 64;
    }
    private void Render()
    {
        owner?.DetachColumns(this); owner = null;
        Children.Clear(); if (DataContext is not ManagementRowState state) return;
        if (IsLoaded) { owner = state.Owner; owner.AttachColumns(this); }
        state.Owner.ApplyColumns(this); var row = state.Row;
        var check = new CheckBox { IsChecked = state.Selected, Tag = "round", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; check.UseResource(StyleProperty, "MeowFilm.ManagementCheck");
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
        title.UseResource(TextBlock.FontSizeProperty, "MeowFilm.Font.Size.Body");
        title.UseResource(TextBlock.FontWeightProperty, "MeowFilm.Font.Weight.Normal");
        title.UseResource(FrameworkElement.StyleProperty, "MeowFilm.Text"); text.Children.Add(title);
        if (row.Detail.Length > 0) { var detail = new TextBlock { Text = row.Detail, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 4, 0, 0) }; detail.UseResource(TextBlock.FontSizeProperty, "MeowFilm.Font.Size.Caption"); detail.UseResource(FrameworkElement.StyleProperty, row.Failed ? "MeowFilm.ErrorText" : "MeowFilm.MutedText"); text.Children.Add(detail); }
        SetColumn(text, 1); Children.Add(text);
        var enabled = new CheckBox { IsChecked = row.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 12, 0) }; enabled.UseResource(StyleProperty, "MeowFilm.ManagementSwitch"); AutomationProperties.SetName(enabled, "启用 " + row.Title); enabled.Click += (_, _) => row.Toggle(enabled.IsChecked == true); SetColumn(enabled, 2); Children.Add(enabled);
        var edit = ManagementList.Button("编辑", row.Edit); edit.ClearValue(MinHeightProperty); edit.ClearValue(Control.PaddingProperty); edit.UseResource(StyleProperty, "MeowFilm.ManagementRowButton"); AutomationProperties.SetName(edit, "编辑 " + row.Title); SetColumn(edit, 3); Children.Add(edit);
        Button? more = null; more = ManagementList.Button("···", () => ManagementList.OpenMenu(more!)); more.ClearValue(MinHeightProperty); more.ClearValue(Control.PaddingProperty); more.UseResource(StyleProperty, "MeowFilm.ManagementRowButton"); more.ContextFlyout = row.Menu; AutomationProperties.SetName(more, row.Title + "更多操作"); SetColumn(more, 4); Children.Add(more);
    }
}
