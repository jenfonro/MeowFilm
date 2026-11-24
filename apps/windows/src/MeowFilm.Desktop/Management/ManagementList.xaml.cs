using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace MeowFilm.Desktop;

public sealed record ManagementRow(string Id, string Title, string Detail, bool Enabled,
    Action? Edit = null, Action<bool>? Toggle = null, MenuFlyout? Menu = null, bool Failed = false,
    Func<IReadOnlyList<FrameworkElement>>? Cells = null, object? CellsIdentity = null);
public sealed record ManagementColumn(string Header, GridLength Width, double MaxWidth = double.PositiveInfinity);

/// <summary>Virtualized settings rows; native ListView owns drag, auto-scroll and keyboard reordering.</summary>
public sealed partial class ManagementList : UserControl
{
    public TextBox Search => SearchBox;
    public StackPanel Tools => ToolsPanel;
    public Button More => MoreButton;
    public IReadOnlySet<string> Selected => selected;
    public event Action? SelectionChanged;
    public event Action? Delete;
    public event Action<IReadOnlyList<string>>? OrderChanged;

    private bool enableRowDrag;
    public bool EnableRowDrag { get => enableRowDrag; set { enableRowDrag = value; UpdateDragAdmission(); } }
    public bool ShowToolbar { get => Toolbar.Visibility == Visibility.Visible; set => Toolbar.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
    public bool ShowBulkActions
    {
        get => FloatingBar.Visibility == Visibility.Visible;
        set { FloatingBar.Visibility = value ? Visibility.Visible : Visibility.Collapsed; Card.Padding = new(8, 8, 8, value ? 76 : 8); Count(); }
    }

    private IReadOnlyList<ManagementColumn> columns = [new("名称", new(1, GridUnitType.Star)), new("启用", GridLength.Auto), new("", GridLength.Auto), new("", GridLength.Auto)];
    private readonly HashSet<string> selected = [];
    private readonly ObservableCollection<ManagementRowState> items = [];
    private IReadOnlyList<ManagementRow> rows = [];
    private int total;
    private string[]? draggingOrder;
    private IDisposable? visibility;
    private readonly HashSet<Grid> columnRows = [];
    private double[] sharedWidths = [];
    private bool measuringColumns, columnsQueued;

    public ManagementList()
    {
        InitializeComponent();
        List.ItemsSource = items;
        Loaded += (_, _) => { visibility?.Dispose(); visibility = DesktopVisual.ObserveVisibility(this, _ => UpdateDragAdmission()); };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            visibility?.Dispose(); visibility = null;
            draggingOrder = null; List.CanDragItems = List.CanReorderItems = false;
        };
        IsEnabledChanged += (_, _) => UpdateDragAdmission();
    }

    private void QueueColumns()
    {
        if (columnsQueued) return;
        columnsQueued = DispatcherQueue.TryEnqueue(() => { columnsQueued = false; MeasureColumns(); });
    }

    private void ColumnSizeChanged(object sender, SizeChangedEventArgs args) => QueueColumns();

    private void MeasureColumns()
    {
        if (measuringColumns) return;
        measuringColumns = true;
        try
        {
            var widths = new double[columns.Count];
            foreach (var grid in columnRows.Prepend(Header))
                foreach (var cell in grid.Children.OfType<FrameworkElement>())
                {
                    if (cell.Visibility == Visibility.Collapsed) continue;
                    int index = Grid.GetColumn(cell) - 1;
                    if (index < 0 || index >= columns.Count || !columns[index].Width.IsAuto) continue;
                    cell.Measure(new Windows.Foundation.Size(columns[index].MaxWidth, double.PositiveInfinity));
                    widths[index] = Math.Min(columns[index].MaxWidth, Math.Max(widths[index], cell.DesiredSize.Width));
                }
            if (sharedWidths.SequenceEqual(widths)) return;
            sharedWidths = widths;
            foreach (var grid in columnRows.Prepend(Header))
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

    private bool CanDragRows => EnableRowDrag && IsEnabled && DesktopVisual.IsVisible(this) && rows.Count == total;

    private void UpdateDragAdmission()
    {
        List.CanDragItems = List.CanReorderItems = CanDragRows;
        if (!CanDragRows) draggingOrder = null;
    }

    private void SelectAll(bool value) { selected.Clear(); if (value) selected.UnionWith(rows.Select(row => row.Id)); Changed(); }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SelectAll(SelectAllCheck.IsChecked == true);
    private void HeaderAll_Click(object sender, RoutedEventArgs e) => SelectAll(HeaderAll.IsChecked == true);
    private void Inverse_Click(object sender, RoutedEventArgs e) { selected.SymmetricExceptWith(rows.Select(row => row.Id)); Changed(); }
    private void Remove_Click(object sender, RoutedEventArgs e) => Delete?.Invoke();
    private void More_Click(object sender, RoutedEventArgs e) => OpenMenu(MoreButton);

    private void List_DragItemsStarting(object sender, DragItemsStartingEventArgs args)
    {
        if (!CanDragRows) { args.Cancel = true; return; }
        draggingOrder = rows.Select(row => row.Id).ToArray();
    }

    private void List_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        var before = draggingOrder; draggingOrder = null;
        if (before is null || !CanDragRows || args.DropResult != Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move) { Fill(); return; }
        var after = items.Select(item => item.Row.Id).ToArray();
        if (!before.SequenceEqual(after)) OrderChanged?.Invoke(after);
    }

    public void SetColumns(IReadOnlyList<ManagementColumn> value)
    {
        if (Header.Visibility == Visibility.Visible && columns.SequenceEqual(value)) return;
        columns = value.ToArray(); sharedWidths = []; ApplyColumns(Header);
        while (Header.Children.Count > columns.Count + 1) Header.Children.RemoveAt(Header.Children.Count - 1);
        var mutedStyle = DesktopVisual.TryAppStyle("MeowFilm.MutedText");
        for (int index = 0; index < columns.Count; index++)
        {
            TextBlock title;
            if (index + 1 < Header.Children.Count) title = (TextBlock)Header.Children[index + 1];
            else
            {
                title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(6, 0, 6, 0), Style = mutedStyle };
                Grid.SetColumn(title, index + 1); Header.Children.Add(title);
            }
            title.Text = columns[index].Header;
            title.HorizontalAlignment = columns[index].Width.IsStar ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        }
        Header.Visibility = Visibility.Visible;
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
        rows = value; this.total = total; selected.IntersectWith(validIds); EmptyText.Text = emptyMessage;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed; Fill(); Count();
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
        HeaderAll.IsChecked = SelectAllCheck.IsChecked = rows.Count > 0 && rows.All(row => selected.Contains(row.Id));
        SelectAllCheck.Content = $"全选（{selected.Count}/{total}）";
        InverseButton.IsEnabled = rows.Count > 0;
        RemoveButton.IsEnabled = MoreButton.IsEnabled = selected.Count > 0;
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

    public static Button Button(string title, Action action, string styleKey = "MeowFilm.QuietButton")
    {
        var button = new Button { Content = title, Padding = new(12, 8, 12, 8), MinHeight = 36, Margin = new(4, 0, 0, 0), Style = DesktopVisual.TryAppStyle(styleKey) };
        AutomationProperties.SetName(button, title); button.Click += (_, _) => action(); return button;
    }

    public static Button RowButton(string title, Action action, string styleKey = "MeowFilm.ManagementRowButton")
    {
        var button = new Button { Content = title, Style = DesktopVisual.TryAppStyle(styleKey) };
        AutomationProperties.SetName(button, title); button.Click += (_, _) => action(); return button;
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
        if (value.Cells is not null)
        {
            if (before.Cells is null || before.Title != value.Title ||
                !ReferenceEquals(before.CellsIdentity ?? before, value.CellsIdentity ?? value))
                RowChanged?.Invoke();
        }
        else if (before.Cells is not null || before.Title != value.Title || before.Detail != value.Detail ||
            before.Enabled != value.Enabled || before.Failed != value.Failed || !ReferenceEquals(before.Menu, value.Menu))
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
