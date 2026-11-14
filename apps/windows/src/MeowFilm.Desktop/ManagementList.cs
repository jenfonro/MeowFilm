using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace MeowFilm.Desktop;

public sealed record ManagementRow(string Id, string Title, string Detail, bool Enabled,
    Action Edit, Action<bool> Toggle, ContextMenu Menu, bool Failed = false);

/// <summary>Reusable settings list chrome; item policy and persistence belong to the caller.</summary>
public sealed class ManagementList : Grid
{
    public TextBox Search { get; } = new() { MinWidth = 120, Padding = new(12, 9, 12, 9), Margin = new(0, 0, 12, 0) };
    public StackPanel Tools { get; } = new() { Orientation = Orientation.Horizontal };
    public Button More { get; }
    public IReadOnlySet<string> Selected => selected;
    public event Action? SelectionChanged;
    public event Action? Delete;
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
        var toolbar = new DockPanel { Margin = new(0, 4, 0, 10) }; DockPanel.SetDock(Tools, Dock.Right); toolbar.Children.Add(Tools); toolbar.Children.Add(Search); Children.Add(toolbar);
        Search.Tag = "搜索"; Search.SetResourceReference(StyleProperty, "MeowFilm.ManagementSearch"); AutomationProperties.SetName(Search, "搜索");
        var card = new Border { CornerRadius = new(22), BorderThickness = new(1), Padding = new(8, 8, 8, 76), ClipToBounds = true };
        card.SetResourceReference(FrameworkElement.StyleProperty, "MeowFilm.FloatingSurface");
        var items = new Grid(); items.Children.Add(list); items.Children.Add(empty); card.Child = items; SetRow(card, 1); Children.Add(card);
        var template = new DataTemplate(typeof(ManagementRow)) { VisualTree = new FrameworkElementFactory(typeof(ManagementRowView)) }; list.ItemTemplate = template;
        list.SetValue(VirtualizingPanel.IsVirtualizingProperty, true); list.SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
        list.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        var container = new Style(typeof(ListBoxItem)); container.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)); container.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0))); list.ItemContainerStyle = container;
        list.SetValue(ScrollViewer.CanContentScrollProperty, true); empty.SetResourceReference(FrameworkElement.StyleProperty, "MeowFilm.MutedText");
        var footer = new DockPanel { LastChildFill = true }; var actions = new StackPanel { Orientation = Orientation.Horizontal };
        inverse = Button("反选", () => { selected.SymmetricExceptWith(rows.Select(r => r.Id)); Changed(); });
        remove = Button("删除", () => Delete?.Invoke()); More = Button("···", () => OpenMenu(More!));
        AutomationProperties.SetName(More, "批量更多操作"); actions.Children.Add(inverse); actions.Children.Add(remove); actions.Children.Add(More); DockPanel.SetDock(actions, Dock.Right); footer.Children.Add(actions); footer.Children.Add(all);
        var floating = new Border { CornerRadius = new(26), BorderThickness = new(1), Padding = new(14, 8, 12, 8), Margin = new(12, 0, 12, 12), Height = 52, VerticalAlignment = VerticalAlignment.Bottom, Child = footer,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 22, ShadowDepth = 6, Opacity = .12 } };
        floating.SetResourceReference(FrameworkElement.StyleProperty, "MeowFilm.FloatingSurface");
        SetRow(floating, 1); Children.Add(floating);
        foreach (var button in new[] { inverse, remove, More }) { button.Height = 32; button.MinHeight = 32; button.Padding = new(16, 0, 16, 0); button.SetResourceReference(Control.BackgroundProperty, "MeowFilm.Brush.InputSurface"); }
        all.Tag = "round"; all.SetResourceReference(StyleProperty, "MeowFilm.ManagementCheck");
        all.Click += (_, _) => { selected.Clear(); if (all.IsChecked == true) selected.UnionWith(rows.Select(r => r.Id)); Changed(); };
        AutomationProperties.SetAutomationId(all, "management.selectAll"); AutomationProperties.SetAutomationId(list, "management.list");
    }
    public void SetRows(IReadOnlyList<ManagementRow> value, int total, IEnumerable<string> validIds, string emptyMessage)
    {
        rows = value; this.total = total; selected.IntersectWith(validIds); empty.Text = emptyMessage; empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Fill(); Count();
    }
    private void Fill()
    {
        // Only realized rows allocate native controls; state is keyed by stable item IDs.
        list.ItemsSource = rows.Select(row => new ManagementRowState(row, selected.Contains(row.Id), value => { if (value) selected.Add(row.Id); else selected.Remove(row.Id); Count(); SelectionChanged?.Invoke(); })).ToArray();
    }
    private void Count()
    {
        all.IsChecked = rows.Count > 0 && rows.All(r => selected.Contains(r.Id)); all.Content = $"全选（{selected.Count}/{total}）";
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
internal sealed class ManagementRowState(ManagementRow row, bool selected, Action<bool> select)
{
    internal ManagementRow Row => row;
    internal bool Selected { get; private set; } = selected;
    internal void Select(bool value) { Selected = value; select(value); }
}

public sealed class ManagementRowView : Grid
{
    public ManagementRowView()
    {
        DataContextChanged += (_, _) => Render();
        ColumnDefinitions.Add(new() { Width = GridLength.Auto }); ColumnDefinitions.Add(new()); ColumnDefinitions.Add(new() { Width = GridLength.Auto }); ColumnDefinitions.Add(new() { Width = GridLength.Auto }); ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Margin = new(6, 0, 6, 0); MinHeight = 64;
    }
    private void Render()
    {
        Children.Clear(); if (DataContext is not ManagementRowState state) return;
        var row = state.Row;
        var check = new CheckBox { IsChecked = state.Selected, Tag = "round", Margin = new(12, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center }; check.SetResourceReference(StyleProperty, "MeowFilm.ManagementCheck");
        AutomationProperties.SetName(check, "选择 " + row.Title); check.Click += (_, _) => state.Select(check.IsChecked == true); Children.Add(check);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 8, 12, 8) };
        var title = new TextBlock { Text = row.Title, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetResourceReference(TextBlock.FontSizeProperty, "MeowFilm.Font.Size.Body");
        title.SetResourceReference(TextBlock.FontWeightProperty, "MeowFilm.Font.Weight.Normal");
        title.SetResourceReference(FrameworkElement.StyleProperty, "MeowFilm.Text"); text.Children.Add(title);
        if (row.Detail.Length > 0) { var detail = new TextBlock { Text = row.Detail, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 4, 0, 0) }; detail.SetResourceReference(TextBlock.FontSizeProperty, "MeowFilm.Font.Size.Caption"); detail.SetResourceReference(FrameworkElement.StyleProperty, row.Failed ? "MeowFilm.ErrorText" : "MeowFilm.MutedText"); text.Children.Add(detail); }
        SetColumn(text, 1); Children.Add(text);
        var enabled = new CheckBox { IsChecked = row.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 12, 0) }; enabled.SetResourceReference(StyleProperty, "MeowFilm.ManagementSwitch"); AutomationProperties.SetName(enabled, "启用 " + row.Title); enabled.Click += (_, _) => row.Toggle(enabled.IsChecked == true); SetColumn(enabled, 2); Children.Add(enabled);
        var edit = ManagementList.Button("编辑", row.Edit); AutomationProperties.SetName(edit, "编辑 " + row.Title); SetColumn(edit, 3); Children.Add(edit);
        Button? more = null; more = ManagementList.Button("···", () => ManagementList.OpenMenu(more!)); more.ContextMenu = row.Menu; AutomationProperties.SetName(more, row.Title + "更多操作"); SetColumn(more, 4); Children.Add(more);
    }
}
