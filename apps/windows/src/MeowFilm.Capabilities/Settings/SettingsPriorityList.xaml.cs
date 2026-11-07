using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

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

public partial class SettingsPriorityList : UserControl
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
    private sealed record KeyboardMove(object Row);
    private KeyboardMove? keyboardMove;
    private bool Ready => IsLoaded && IsVisible && IsEnabled && CanEdit;

    public SettingsPriorityList()
    {
        InitializeComponent();
        drag = new(PriorityItems);
        Loaded += (_, _) => ObserveCollection();
        Unloaded += (_, _) => { drag.Cancel(); keyboardMove = null; Unsubscribe(); };
        IsVisibleChanged += (_, e) => { if (e.NewValue is false) { drag.Cancel(); keyboardMove = null; } };
        IsEnabledChanged += (_, e) => { if (e.NewValue is false) { drag.Cancel(); keyboardMove = null; } };
        SizeChanged += (_, _) => { drag.Cancel(); };
        DataContextChanged += (_, _) => { drag.Cancel(); keyboardMove = null; };
    }

    public IEnumerable? ItemsSource { get => (IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public bool ShowLogos { get => (bool)GetValue(ShowLogosProperty); set => SetValue(ShowLogosProperty, value); }
    public bool CanEdit { get => (bool)GetValue(CanEditProperty); set => SetValue(CanEditProperty, value); }
    public event EventHandler<SettingsPriorityMoveEventArgs>? MoveRequested;
    public event EventHandler<SettingsPriorityToggleEventArgs>? ToggleRequested;
    internal bool IsDragging => drag.IsActive;
    internal SettingsReorderDrag Drag => drag;
    internal FrameworkElement DragViewport => PriorityItems;

    private static void ItemsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var view = (SettingsPriorityList)sender;
        view.drag?.Cancel();
        view.keyboardMove = null;
        if (view.IsLoaded) view.ObserveCollection();
    }
    private static void AuthorityChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var view = (SettingsPriorityList)sender;
        if (e.NewValue is false) view.drag?.Cancel();
        else if (view.keyboardMove is { } move) view.RestoreKeyboardFocus(move);
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
        if (keyboardMove is { } move && !PriorityItems.Items.Contains(move.Row)) keyboardMove = null;
        if (!PriorityItems.Items.Contains(PriorityItems.SelectedItem)) PriorityItems.SelectedItem = null;
    }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (ReferenceEquals(e.OldFocus, this)) keyboardMove = null;
    }
    private void RestoreKeyboardFocus(KeyboardMove move)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!ReferenceEquals(keyboardMove, move)) return;
            if (!IsLoaded || !IsVisible || !IsEnabled || !ReferenceEquals(Keyboard.FocusedElement, this))
            { keyboardMove = null; return; }
            if (!CanEdit || !PriorityItems.IsEnabled) return;
            keyboardMove = null;
            // A collection Move can recreate the row's visual container.
            if (PriorityItems.ItemContainerGenerator.ContainerFromItem(move.Row) is ListBoxItem container &&
                FindHandle(container) is { IsLoaded: true, IsVisible: true, IsEnabled: true, Focusable: true } handle &&
                ReferenceEquals(handle.DataContext, move.Row))
                handle.Focus();
        }));
    }
    private static Thumb? FindHandle(DependencyObject parent)
    {
        if (parent is Thumb handle) return handle;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindHandle(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
        return null;
    }
    private void RequestMove(Thumb handle, int direction)
    {
        if (!Ready || drag.IsActive || handle.DataContext is not { } source ||
            SettingsReorderDrag.KeyboardTarget(PriorityItems, source, direction) is not { } target) return;
        keyboardMove = null;
        // Keep keyboard ownership outside the list while its admitted save disables the rows.
        // A user focus change or page retirement abandons this restoration, never steals focus back.
        if (ReferenceEquals(Keyboard.FocusedElement, handle) && Focus()) keyboardMove = new(source);
        MoveRequested?.Invoke(this, new(source, target));
        if (keyboardMove is { } move) RestoreKeyboardFocus(move);
    }
    private void Move_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = Ready && !drag.IsActive && e.OriginalSource is Thumb { DataContext: { } row } &&
            SettingsReorderDrag.KeyboardTarget(PriorityItems, row, e.Command == ComponentCommands.MoveUp ? -1 : 1) is not null;
        e.Handled = true;
    }
    private void Move_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.OriginalSource is Thumb handle)
            RequestMove(handle, e.Command == ComponentCommands.MoveUp ? -1 : 1);
        e.Handled = true;
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
    }
    private void Grip_Delta(object sender, DragDeltaEventArgs e) => drag.Update(Mouse.GetPosition(PriorityItems));
    private void Grip_Completed(object sender, DragCompletedEventArgs e) => CompleteDrag(Mouse.GetPosition(PriorityItems), e.Canceled);
    internal void CompleteDrag(Point point, bool cancelled)
    {
        PriorityItems.UpdateLayout();
        var move = drag.Complete(point, cancelled);
        if (Ready && move is not null) MoveRequested?.Invoke(this, new(move.Source, move.Target));
    }
    private void Grip_KeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { drag.Cancel(); e.Handled = true; return; }
    }
}
