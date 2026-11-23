using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Desktop;

public sealed record ManagementRow(string Id, string Title, string Detail, bool Enabled,
    Action Edit, Action<bool> Toggle, MenuFlyout Menu, bool Failed = false,
    Func<IReadOnlyList<FrameworkElement>>? Cells = null, object? CellsIdentity = null);
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
    private bool measuringColumns, columnsQueued;
    private void QueueColumns()
    {
        if (columnsQueued) return;
        columnsQueued = DispatcherQueue.TryEnqueue(() => { columnsQueued = false; MeasureColumns(null, EventArgs.Empty); });
    }
    private void ColumnSizeChanged(object sender, SizeChangedEventArgs args) => QueueColumns();
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
        if (columnRows.Add(row)) { row.SizeChanged += ColumnSizeChanged; QueueColumns(); }
    }
    internal void DetachColumns(Grid row)
    {
        if (!columnRows.Remove(row)) return;
        row.SizeChanged -= ColumnSizeChanged; QueueColumns();
    }

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
        Loaded += (_, _) => { visibility?.Dispose(); visibility = DesktopVisual.ObserveVisibility(this, _ => UpdateDragAdmission()); };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
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
        if (header.Visibility == Visibility.Visible && columns.SequenceEqual(value)) return;
        columns = value.ToArray(); sharedWidths = []; ApplyColumns(header);
        if (header.Children.Count == 0) header.Children.Add(headerAll);
        while (header.Children.Count > columns.Count + 1) header.Children.RemoveAt(header.Children.Count - 1);
        for (int index = 0; index < columns.Count; index++)
        {
            TextBlock title;
            if (index + 1 < header.Children.Count) title = (TextBlock)header.Children[index + 1];
            else
            {
                title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(6, 0, 6, 0) };
                title.UseResource(StyleProperty, "MeowFilm.MutedText"); Grid.SetColumn(title, index + 1); header.Children.Add(title);
            }
            title.Text = columns[index].Header;
            title.HorizontalAlignment = columns[index].Width.IsStar ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        }
        header.Visibility = Visibility.Visible;
        foreach (var grid in columnRows) ApplyColumns(grid);
        QueueColumns();
    }
    internal void ApplyColumns(Grid grid)
    {
        if (grid.ColumnDefinitions.Count == 0) grid.ColumnDefinitions.Add(new() { Width = new(48) });
        while (grid.ColumnDefinitions.Count > columns.Count + 1) grid.ColumnDefinitions.RemoveAt(grid.ColumnDefinitions.Count - 1);
        for (int index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            if (grid.ColumnDefinitions.Count <= index + 1) grid.ColumnDefinitions.Add(new());
            var definition = grid.ColumnDefinitions[index + 1];
            definition.Width = column.Width.IsAuto && index < sharedWidths.Length && sharedWidths[index] > 0 ? new GridLength(sharedWidths[index]) : column.Width;
            definition.MaxWidth = column.MaxWidth;
        }
    }
    public void SetRows(IReadOnlyList<ManagementRow> value, int total, IEnumerable<string> validIds, string emptyMessage)
    {
        rows = value; this.total = total; selected.IntersectWith(validIds); empty.Text = emptyMessage;
        empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed; Fill(); Count();
    }
    private void Fill()
    {
        draggingOrder = null;
        var ids = rows.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        for (int index = items.Count - 1; index >= 0; index--)
            if (!ids.Contains(items[index].Row.Id)) items.RemoveAt(index);
        var retained = items.ToDictionary(item => item.Row.Id, StringComparer.Ordinal);
        for (int index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (retained.TryGetValue(row.Id, out var state))
            {
                if (!ReferenceEquals(items[index], state)) items.Move(items.IndexOf(state), index);
                state.Update(row, selected.Contains(row.Id));
            }
            else items.Insert(index, new(this, row, selected.Contains(row.Id)));
        }
        UpdateDragAdmission();
    }
    private void Count()
    {
        headerAll.IsChecked = all.IsChecked = rows.Count > 0 && rows.All(row => selected.Contains(row.Id)); all.Content = $"全选（{selected.Count}/{total}）";
        inverse.IsEnabled = rows.Count > 0; remove.IsEnabled = More.IsEnabled = selected.Count > 0;
    }
    internal void Select(ManagementRowState state, bool value)
    {
        if (value) selected.Add(state.Row.Id); else selected.Remove(state.Row.Id);
        state.SetSelected(value); Count(); SelectionChanged?.Invoke();
    }
    private void Changed()
    {
        foreach (var state in items) state.SetSelected(selected.Contains(state.Row.Id));
        Count(); SelectionChanged?.Invoke();
    }
    public static Button Button(string title, Action action)
    {
        var button = new Button { Content = title, Padding = new(12, 8, 12, 8), MinHeight = 36, Margin = new(4, 0, 0, 0) };
        button.UseResource(StyleProperty, "MeowFilm.QuietButton"); AutomationProperties.SetName(button, title); button.Click += (_, _) => action(); return button;
    }
    public static void OpenMenu(Button button) => (button.ContextFlyout as MenuFlyout)?.ShowAt(button, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Bottom });
}
internal sealed class ManagementRowState(ManagementList owner, ManagementRow row, bool selected)
{
    internal ManagementList Owner => owner;
    internal ManagementRow Row { get; private set; } = row;
    internal bool Selected { get; private set; } = selected;
    internal event Action? RowChanged;
    internal event Action? SelectionChanged;
    internal void Update(ManagementRow value, bool selected)
    {
        var before = Row; Row = value;
        if (before.Title != value.Title || before.Detail != value.Detail || before.Enabled != value.Enabled ||
            before.Failed != value.Failed || !ReferenceEquals(before.Menu, value.Menu) ||
            (before.Cells is null) != (value.Cells is null) ||
            value.Cells is not null && !ReferenceEquals(before.CellsIdentity ?? before, value.CellsIdentity ?? value))
            RowChanged?.Invoke();
        SetSelected(selected);
    }
    internal void SetSelected(bool value)
    {
        if (Selected == value) return;
        Selected = value; SelectionChanged?.Invoke();
    }
    internal void Select(bool value) => owner.Select(this, value);
}

public sealed class ManagementRowView : Grid
{
    private ManagementList? owner;
    private ManagementRowState? state;
    private ManagementRow? renderedRow;
    private CheckBox? check, enabled;
    private StackPanel? text;
    private TextBlock? title, detail;
    private Button? edit, more;
    private object? cellsIdentity;
    private bool customCells, observing;
    public ManagementRowView()
    {
        DataContextChanged += (_, _) => Bind();
        Loaded += (_, _) =>
        {
            Bind();
            if (state is null) return;
            owner = state.Owner; owner.ApplyColumns(this); owner.AttachColumns(this);
        };
        Unloaded += (_, _) => { StopObserving(); owner?.DetachColumns(this); owner = null; };
        Margin = new(6, 0, 6, 0); MinHeight = 64;
    }
    private void StopObserving()
    {
        if (!observing || state is null) return;
        state.RowChanged -= UpdateRow; state.SelectionChanged -= UpdateSelection; observing = false;
    }
    private void Bind()
    {
        var next = DataContext as ManagementRowState;
        if (!ReferenceEquals(state, next))
        {
            StopObserving(); owner?.DetachColumns(this); owner = null; state = next;
            Children.Clear(); check = enabled = null; text = null; title = detail = null; edit = more = null;
            cellsIdentity = null; customCells = false; renderedRow = null;
        }
        if (state is null) return;
        if (IsLoaded && !observing) { state.RowChanged += UpdateRow; state.SelectionChanged += UpdateSelection; observing = true; }
        if (IsLoaded && owner is null) { owner = state.Owner; owner.AttachColumns(this); }
        state.Owner.ApplyColumns(this);
        if (check is null)
        {
            check = new CheckBox { Tag = "round", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            check.UseResource(StyleProperty, "MeowFilm.ManagementCheck");
            check.Click += (_, _) => state?.Select(check.IsChecked == true); Children.Add(check);
        }
        UpdateRow(); UpdateSelection();
    }
    private void UpdateSelection() { if (check is not null && state is not null) check.IsChecked = state.Selected; }
    private void UpdateRow()
    {
        if (state is null || check is null) return;
        var row = state.Row;
        var before = renderedRow; renderedRow = row;
        if (before?.Title != row.Title) AutomationProperties.SetName(check, "选择 " + row.Title);
        bool custom = row.Cells is not null;
        object identity = row.CellsIdentity ?? row;
        if (custom != customCells || custom && !ReferenceEquals(cellsIdentity, identity))
        {
            while (Children.Count > 1) Children.RemoveAt(Children.Count - 1);
            enabled = null; text = null; title = detail = null; edit = more = null;
            customCells = custom; cellsIdentity = identity;
        }
        if (custom)
        {
            if (Children.Count > 1) return;
            var cells = row.Cells!();
            if (cells.Count != ColumnDefinitions.Count - 1) throw new InvalidOperationException("Management row cells must match its columns.");
            for (int index = 0; index < cells.Count; index++) { SetColumn(cells[index], index + 1); Children.Add(cells[index]); }
            return;
        }
        bool created = text is null;
        if (created)
        {
            text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 8, 12, 8) };
            title = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
            title.UseResource(TextBlock.FontSizeProperty, "MeowFilm.Font.Size.Body");
            title.UseResource(TextBlock.FontWeightProperty, "MeowFilm.Font.Weight.Normal");
            title.UseResource(FrameworkElement.StyleProperty, "MeowFilm.Text"); text.Children.Add(title);
            SetColumn(text, 1); Children.Add(text);
            enabled = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 12, 0) };
            enabled.UseResource(StyleProperty, "MeowFilm.Switch");
            enabled.Click += (_, _) => state?.Row.Toggle(enabled.IsChecked == true); SetColumn(enabled, 2); Children.Add(enabled);
            edit = ManagementList.Button("编辑", () => state?.Row.Edit());
            edit.ClearValue(MinHeightProperty); edit.ClearValue(Control.PaddingProperty); edit.UseResource(StyleProperty, "MeowFilm.ManagementRowButton"); SetColumn(edit, 3); Children.Add(edit);
            more = ManagementList.Button("···", () => ManagementList.OpenMenu(more!));
            more.ClearValue(MinHeightProperty); more.ClearValue(Control.PaddingProperty); more.UseResource(StyleProperty, "MeowFilm.ManagementRowButton"); SetColumn(more, 4); Children.Add(more);
        }
        if (created || before?.Title != row.Title) title!.Text = row.Title;
        bool detailCreated = row.Detail.Length > 0 && detail is null;
        if (detailCreated)
        {
            detail = new TextBlock { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 4, 0, 0) };
            detail.UseResource(TextBlock.FontSizeProperty, "MeowFilm.Font.Size.Caption"); text!.Children.Add(detail);
        }
        if (detail is not null)
        {
            if (detailCreated || before?.Detail != row.Detail)
            {
                detail.Text = row.Detail; detail.Visibility = row.Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            if (detailCreated || before?.Failed != row.Failed)
                detail.UseResource(FrameworkElement.StyleProperty, row.Failed ? "MeowFilm.ErrorText" : "MeowFilm.MutedText");
        }
        if (created || before?.Enabled != row.Enabled) enabled!.IsChecked = row.Enabled;
        if (created || before?.Title != row.Title)
        {
            AutomationProperties.SetName(enabled!, "启用 " + row.Title); AutomationProperties.SetName(edit!, "编辑 " + row.Title);
            AutomationProperties.SetName(more!, row.Title + "更多操作");
        }
        if (created || !ReferenceEquals(before?.Menu, row.Menu)) more!.ContextFlyout = row.Menu;
    }
}
