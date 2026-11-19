using System.Collections;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using MeowFilm.Desktop;

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
    public static readonly DependencyProperty ShowTitleProperty = DependencyProperty.Register(nameof(ShowTitle),
        typeof(bool), typeof(SettingsPriorityList), new PropertyMetadata(true));
    public static readonly DependencyProperty ShowLogosProperty = DependencyProperty.Register(nameof(ShowLogos),
        typeof(bool), typeof(SettingsPriorityList), new PropertyMetadata(false));
    public static readonly DependencyProperty ShowToggleProperty = DependencyProperty.Register(nameof(ShowToggle),
        typeof(bool), typeof(SettingsPriorityList), new PropertyMetadata(true));
    public static readonly DependencyProperty CanEditProperty = DependencyProperty.Register(nameof(CanEdit),
        typeof(bool), typeof(SettingsPriorityList), new PropertyMetadata(false, AuthorityChanged));
    private readonly SettingsReorderDrag drag;
    private INotifyCollectionChanged? subscribed;
    private IDisposable? visibility;
    private sealed record KeyboardMove(object Row);
    private KeyboardMove? keyboardMove;
    private bool Ready => IsLoaded && DesktopVisual.IsVisible(this) && IsEnabled && CanEdit;

    public SettingsPriorityList()
    {
        InitializeComponent();
        drag = new(PriorityItems, DragOverlay);
        Loaded += (_, _) =>
        {
            ObserveCollection();
            visibility = DesktopVisual.ObserveVisibility(this, visible =>
            {
                if (!visible) { drag.Cancel(); keyboardMove = null; }
            });
        };
        Unloaded += (_, _) =>
        {
            visibility?.Dispose(); visibility = null;
            drag.Cancel(); keyboardMove = null; Unsubscribe();
        };
        IsEnabledChanged += (_, _) => { if (!IsEnabled) { drag.Cancel(); keyboardMove = null; } };
        SizeChanged += (_, _) => { drag.Cancel(); };
        DataContextChanged += (_, _) => { drag.Cancel(); keyboardMove = null; };
    }

    public IEnumerable? ItemsSource { get => (IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public bool ShowTitle { get => (bool)GetValue(ShowTitleProperty); set => SetValue(ShowTitleProperty, value); }
    public bool ShowLogos { get => (bool)GetValue(ShowLogosProperty); set => SetValue(ShowLogosProperty, value); }
    public bool ShowToggle { get => (bool)GetValue(ShowToggleProperty); set => SetValue(ShowToggleProperty, value); }
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
    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        if (!ReferenceEquals(DesktopVisual.Focused(this), this)) keyboardMove = null;
    }
    private void RestoreKeyboardFocus(KeyboardMove move)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(keyboardMove, move)) return;
            if (!IsLoaded || !DesktopVisual.IsVisible(this) || !IsEnabled || !ReferenceEquals(DesktopVisual.Focused(this), this))
            { keyboardMove = null; return; }
            if (!CanEdit || !PriorityItems.IsEnabled) return;
            keyboardMove = null;
            if (PriorityItems.ContainerFromItem(move.Row) is ListViewItem container &&
                FindHandle(container) is { IsLoaded: true, Visibility: Visibility.Visible, IsEnabled: true } handle &&
                ReferenceEquals(handle.DataContext, move.Row)) handle.Focus(FocusState.Keyboard);
        });
    }
    private static Thumb? FindHandle(DependencyObject parent)
    {
        if (parent is Thumb handle) return handle;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindHandle(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
        return null;
    }
    internal bool CanMove(Thumb handle, int direction) => Ready && !drag.IsActive && handle.IsLoaded &&
        handle.IsEnabled && DesktopVisual.Contains(PriorityItems, handle) &&
        SettingsReorderDrag.KeyboardTarget(PriorityItems, handle.DataContext, direction) is not null;
    internal void RequestMove(Thumb handle, int direction)
    {
        if (!CanMove(handle, direction)) return;
        var source = handle.DataContext;
        var target = SettingsReorderDrag.KeyboardTarget(PriorityItems, source, direction)!;
        keyboardMove = null;
        // Keep keyboard ownership outside the list while its admitted save disables the rows.
        // A user focus change or page retirement abandons this restoration, never steals focus back.
        if (ReferenceEquals(DesktopVisual.Focused(this), handle) && Focus(FocusState.Programmatic)) keyboardMove = new(source);
        MoveRequested?.Invoke(this, new(source, target));
        if (keyboardMove is { } move) RestoreKeyboardFocus(move);
    }
    private void Enabled_Click(object sender, RoutedEventArgs e)
    {
        if (!Ready || sender is not CheckBox { IsLoaded: true, Visibility: Visibility.Visible, IsEnabled: true, Tag: { } item } checkbox ||
            !PriorityItems.Items.Contains(item)) return;
        drag.Cancel();
        ToggleRequested?.Invoke(this, new(item, checkbox.IsChecked == true));
        checkbox.IsChecked = item switch { MusicSearchPlatformRow row => row.Enabled, MusicQualityRow row => row.Enabled, MusicMetadataPlatformRow row => row.Enabled, _ => false };
    }
    private void Grip_Started(object sender, DragStartedEventArgs e)
    {
        if (sender is not Thumb thumb || !Ready) return;
        PriorityItems.SelectedItem = thumb.DataContext;
        drag.Begin(thumb, PriorityItems, thumb.TransformToVisual(PriorityItems).TransformPoint(new Point(e.HorizontalOffset, e.VerticalOffset)));
    }
    private void Grip_Delta(object sender, DragDeltaEventArgs e) => drag.Delta(e.HorizontalChange, e.VerticalChange);
    private void Grip_Completed(object sender, DragCompletedEventArgs e) => CompleteDrag(drag.Position, e.Canceled);
    internal void CompleteDrag(Point point, bool cancelled)
    {
        PriorityItems.UpdateLayout();
        var move = drag.Complete(point, cancelled);
        if (Ready && move is not null) MoveRequested?.Invoke(this, new(move.Source, move.Target));
    }
    internal bool ProcessKey(Thumb handle, VirtualKey key, VirtualKeyModifiers modifiers)
    {
        if (modifiers.HasFlag(VirtualKeyModifiers.Menu) && key is VirtualKey.Up or VirtualKey.Down)
        { RequestMove(handle, key == VirtualKey.Up ? -1 : 1); return true; }
        if (key == VirtualKey.Escape) { drag.Cancel(); return true; }
        return false;
    }
    private void Grip_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is Thumb handle) e.Handled = ProcessKey(handle, e.Key, DesktopVisual.Modifiers);
    }
}
