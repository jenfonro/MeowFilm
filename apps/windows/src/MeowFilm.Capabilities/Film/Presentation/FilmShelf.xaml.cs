using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MeowFilm.Capabilities;

public partial class FilmShelf : UserControl
{
    private static readonly DependencyProperty AnimatedOffsetProperty = DependencyProperty.Register(
        "AnimatedOffset",
        typeof(double),
        typeof(FilmShelf),
        new PropertyMetadata(0d, (value, args) =>
            ((FilmShelf)value).ApplyAnimatedOffset((double)args.NewValue)));

    private ScrollViewer? rail;
    private FilmHomeSection? section;
    private bool animating;
    private bool restoreQueued;
    private bool restoring;
    private long motionGeneration;

    internal ScrollViewer? ScrollHost => rail;
    internal bool IsSettled => !animating;

    public event EventHandler<FilmCardActivatedEventArgs>? ItemActivated;
    public event EventHandler? RetryRequested;

    public FilmShelf()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnVisibilityChanged;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachSection(DataContext as FilmHomeSection);
        AttachScrollHost();
        ScheduleRestore();
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            CommitOffset();
            StopMotion();
            if (rail is not null) rail.ScrollChanged -= OnScrollChanged;
        }
        else if (IsLoaded)
        {
            if (rail is not null)
            {
                rail.ScrollChanged -= OnScrollChanged;
                rail.ScrollChanged += OnScrollChanged;
            }
            ScheduleRestore();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CommitOffset();
        StopMotion();
        if (rail is not null) rail.ScrollChanged -= OnScrollChanged;
        rail = null;
        AttachSection(null);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        CommitOffset();
        StopMotion();
        AttachSection(e.NewValue as FilmHomeSection);
        ScheduleRestore();
    }

    private void AttachSection(FilmHomeSection? next)
    {
        if (ReferenceEquals(section, next)) return;
        if (section is not null)
        {
            section.PropertyChanged -= OnSectionChanged;
            section.Items.CollectionChanged -= OnItemsChanged;
        }
        section = next;
        if (section is not null)
        {
            section.PropertyChanged += OnSectionChanged;
            section.Items.CollectionChanged += OnItemsChanged;
        }
    }

    private void OnSectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) ||
            e.PropertyName is nameof(FilmHomeSection.HasItems) or nameof(FilmHomeSection.ShowStatus))
            ScheduleRestore();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        ScheduleRestore();

    private void AttachScrollHost()
    {
        ItemsHost.ApplyTemplate();
        var next = ItemsHost.Template.FindName("PART_ScrollViewer", ItemsHost) as ScrollViewer;
        if (!ReferenceEquals(next, rail))
        {
            if (rail is not null) rail.ScrollChanged -= OnScrollChanged;
            rail = next;
            if (rail is not null && IsVisible) rail.ScrollChanged += OnScrollChanged;
        }
    }

    private void ScheduleRestore()
    {
        if (!IsLoaded || !IsVisible || restoreQueued) return;
        restoreQueued = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            restoreQueued = false;
            if (!IsLoaded || !IsVisible) return;
            if (rail is null) AttachScrollHost();
            if (rail is null) return;
            restoring = true;
            try
            {
                StopMotion();
                double offset = Math.Clamp(section?.HorizontalOffset ?? 0, 0, rail.ScrollableWidth);
                rail.ScrollToHorizontalOffset(offset);
                SetValue(AnimatedOffsetProperty, offset);
            }
            finally
            {
                restoring = false;
                UpdateButtons();
            }
        }));
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!restoring && section is not null) section.HorizontalOffset = rail?.HorizontalOffset ?? 0;
        UpdateButtons();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (animating) StopMotion();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        if (rail is null) return;
        bool overflow = rail.ScrollableWidth > .5;
        Previous.Visibility = overflow && rail.HorizontalOffset > .5
            ? Visibility.Visible
            : Visibility.Collapsed;
        Next.Visibility = overflow && rail.ScrollableWidth - rail.HorizontalOffset > .5
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Page(-1);
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Page(1);
    }

    private void Page(int direction)
    {
        if (rail is null || direction is not (-1 or 1)) return;
        AnimateTo(rail.HorizontalOffset + direction * 1000);
    }

    private void AnimateTo(double target)
    {
        if (rail is null || !IsVisible) return;
        target = Math.Clamp(target, 0, rail.ScrollableWidth);
        double from = StopMotion();
        long generation = motionGeneration;
        if (!SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast ||
            Math.Abs(target - from) < .5)
        {
            rail.ScrollToHorizontalOffset(target);
            SetValue(AnimatedOffsetProperty, target);
            CommitOffset();
            UpdateButtons();
            return;
        }
        animating = true;
        var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            if (generation != motionGeneration || rail is null) return;
            BeginAnimation(AnimatedOffsetProperty, null);
            SetValue(AnimatedOffsetProperty, target);
            rail.ScrollToHorizontalOffset(target);
            animating = false;
            CommitOffset();
            UpdateButtons();
        };
        BeginAnimation(AnimatedOffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private double StopMotion()
    {
        motionGeneration++;
        double offset = rail?.HorizontalOffset ?? 0;
        BeginAnimation(AnimatedOffsetProperty, null);
        SetValue(AnimatedOffsetProperty, offset);
        animating = false;
        return offset;
    }

    private void ApplyAnimatedOffset(double offset)
    {
        if (rail is not null)
            rail.ScrollToHorizontalOffset(Math.Clamp(offset, 0, rail.ScrollableWidth));
    }

    private void CommitOffset()
    {
        if (section is not null && rail is not null)
            section.HorizontalOffset = rail.HorizontalOffset;
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            if (rail is null) return;
            StopMotion();
            rail.ScrollToHorizontalOffset(
                Math.Clamp(rail.HorizontalOffset - e.Delta, 0, rail.ScrollableWidth));
            CommitOffset();
            UpdateButtons();
            e.Handled = true;
            return;
        }
        for (DependencyObject? parent = VisualTreeHelper.GetParent(this);
             parent is not null;
             parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer outer)
            {
                outer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                {
                    RoutedEvent = Mouse.MouseWheelEvent,
                    Source = outer
                });
                e.Handled = true;
                return;
            }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyboardDevice.Modifiers != ModifierKeys.None) return;
        bool handled = true;
        switch (e.Key)
        {
            case Key.Left:
            case Key.PageUp:
                Page(-1);
                break;
            case Key.Right:
            case Key.PageDown:
                Page(1);
                break;
            case Key.Home:
                AnimateTo(0);
                break;
            case Key.End:
                if (rail is not null) AnimateTo(rail.ScrollableWidth);
                break;
            default:
                handled = false;
                break;
        }
        if (handled) e.Handled = true;
    }

    private void Card_Activated(object? sender, FilmCardActivatedEventArgs e)
    {
        if (sender is FilmCard)
            ItemActivated?.Invoke(this, e);
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        RetryRequested?.Invoke(this, EventArgs.Empty);
    }
}
