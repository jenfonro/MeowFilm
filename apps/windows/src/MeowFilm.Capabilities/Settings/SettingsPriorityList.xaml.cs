using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace MeowFilm.Capabilities;

public sealed class SettingsPriorityMoveEventArgs(object source, object target) : EventArgs
{
    public object Source { get; } = source;
    public object Target { get; } = target;
}
public sealed class SettingsPriorityToggleEventArgs(object item, bool enabled) : EventArgs
{
    public object Item { get; } = item;
    public bool Enabled { get; } = enabled;
}

public partial class SettingsPriorityList : UserControl, INotifyPropertyChanged
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(nameof(ItemsSource),
        typeof(IEnumerable), typeof(SettingsPriorityList), new PropertyMetadata(null, ItemsChanged));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title),
        typeof(string), typeof(SettingsPriorityList), new PropertyMetadata(""));
    public static readonly DependencyProperty ShowLogosProperty = DependencyProperty.Register(nameof(ShowLogos),
        typeof(bool), typeof(SettingsPriorityList), new PropertyMetadata(false));
    public static readonly DependencyProperty CanEditProperty = DependencyProperty.Register(nameof(CanEdit),
        typeof(bool), typeof(SettingsPriorityList), new PropertyMetadata(false, AuthorityChanged));
    private readonly SettingsReorderDrag drag;
    private INotifyCollectionChanged? subscribed;
    private bool Ready => IsLoaded && IsVisible && IsEnabled && CanEdit;

    public SettingsPriorityList()
    {
        InitializeComponent();
        drag = new(PriorityItems);
        drag.StateChanged += PublishMoves;
        Loaded += (_, _) => { ObserveCollection(); PublishMoves(); };
        Unloaded += (_, _) => { drag.Cancel(); Unsubscribe(); };
        IsVisibleChanged += (_, e) => { if (e.NewValue is false) drag.Cancel(); PublishMoves(); };
        IsEnabledChanged += (_, e) => { if (e.NewValue is false) drag.Cancel(); PublishMoves(); };
        SizeChanged += (_, _) => { drag.Cancel(); PublishMoves(); };
        DataContextChanged += (_, _) => { drag.Cancel(); PublishMoves(); };
    }

    public IEnumerable? ItemsSource { get => (IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public bool ShowLogos { get => (bool)GetValue(ShowLogosProperty); set => SetValue(ShowLogosProperty, value); }
    public bool CanEdit { get => (bool)GetValue(CanEditProperty); set => SetValue(CanEditProperty, value); }
    public bool CanMoveUp => Ready && drag?.IsActive != true && PriorityItems?.SelectedIndex > 0;
    public bool CanMoveDown => Ready && drag?.IsActive != true && PriorityItems is { SelectedIndex: >= 0 } items &&
        items.SelectedIndex < items.Items.Count - 1;
    public event EventHandler<SettingsPriorityMoveEventArgs>? MoveRequested;
    public event EventHandler<SettingsPriorityToggleEventArgs>? ToggleRequested;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal bool IsDragging => drag.IsActive;
    internal SettingsReorderDrag Drag => drag;
    internal FrameworkElement DragViewport => PriorityItems;

    private static void ItemsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var view = (SettingsPriorityList)sender;
        view.drag?.Cancel();
        if (view.IsLoaded) view.ObserveCollection();
        view.PublishMoves();
    }
    private static void AuthorityChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var view = (SettingsPriorityList)sender;
        if (e.NewValue is false) view.drag?.Cancel();
        view.PublishMoves();
    }
    private void ObserveCollection()
    {
        Unsubscribe();
        subscribed = ItemsSource as INotifyCollectionChanged;
        if (subscribed is not null) subscribed.CollectionChanged += CollectionChanged;
    }
    private void Unsubscribe()
    {
        if (subscribed is not null) subscribed.CollectionChanged -= CollectionChanged;
        subscribed = null;
    }
    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        drag.Cancel();
        if (!PriorityItems.Items.Contains(PriorityItems.SelectedItem)) PriorityItems.SelectedItem = null;
        PublishMoves();
    }
    private void PublishMoves()
    {
        PropertyChanged?.Invoke(this, new(nameof(CanMoveUp)));
        PropertyChanged?.Invoke(this, new(nameof(CanMoveDown)));
    }
    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => PublishMoves();
    private void Move_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string direction } || PriorityItems.SelectedItem is not { } source) return;
        RequestMove(source, direction == "-1" ? -1 : 1);
    }
    private void RequestMove(object source, int direction)
    {
        if (!Ready || drag.IsActive || SettingsReorderDrag.KeyboardTarget(PriorityItems, source, direction) is not { } target) return;
        MoveRequested?.Invoke(this, new(source, target));
        PublishMoves();
    }
    private void Enabled_Click(object sender, RoutedEventArgs e)
    {
        if (!Ready || sender is not CheckBox { IsLoaded: true, IsVisible: true, IsEnabled: true, Tag: { } item } checkbox ||
            !PriorityItems.Items.Contains(item)) return;
        drag.Cancel();
        ToggleRequested?.Invoke(this, new(item, checkbox.IsChecked == true));
        checkbox.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
    }
    private void Grip_Started(object sender, DragStartedEventArgs e)
    {
        if (sender is not Thumb thumb || !Ready) return;
        PriorityItems.SelectedItem = thumb.DataContext;
        drag.Begin(thumb, PriorityItems, Mouse.GetPosition(PriorityItems));
        PublishMoves();
    }
    private void Grip_Delta(object sender, DragDeltaEventArgs e) => drag.Update(Mouse.GetPosition(PriorityItems));
    private void Grip_Completed(object sender, DragCompletedEventArgs e) => CompleteDrag(Mouse.GetPosition(PriorityItems), e.Canceled);
    internal void CompleteDrag(Point point, bool cancelled)
    {
        PriorityItems.UpdateLayout();
        var move = drag.Complete(point, cancelled);
        PublishMoves();
        if (Ready && move is not null) MoveRequested?.Invoke(this, new(move.Source, move.Target));
    }
    private void Grip_KeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { drag.Cancel(); PublishMoves(); e.Handled = true; return; }
        if (Keyboard.Modifiers != ModifierKeys.Alt || key is not (Key.Up or Key.Down) || sender is not Thumb { DataContext: { } row }) return;
        RequestMove(row, key == Key.Up ? -1 : 1); e.Handled = true;
    }
    private void Toolbar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool compact = e.NewSize.Width < 340;
        Grid.SetRow(MoveGroup, compact ? 1 : 0); Grid.SetColumn(MoveGroup, compact ? 0 : 1);
        Grid.SetRow(EnabledTitle, compact ? 1 : 0);
        MoveGroup.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        MoveGroup.Margin = compact ? new(0, 8, 0, 0) : new(0);
    }
}
