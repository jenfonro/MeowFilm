using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace MeowFilm.Desktop;

internal sealed record DesktopHeaderActionItem(long Generation, DesktopHeaderAction Action)
{
    public string Label => Action.Label;
    public string AutomationName => Action.AutomationName;
    public bool IsPrimary => Action.IsPrimary;
    public bool IsEnabled => Action.IsEnabled;
}

/// <summary>Owns contribution layout, source subscriptions and the lifetime of mounted header actions.</summary>
public partial class DesktopPageHeader : UserControl
{
    private sealed record Presentation(IDesktopHeaderSource? Source, DesktopHeaderContribution Fallback,
        string? HeadingFallback, IReadOnlyList<DesktopHeaderAction> AdditionalActions);

    public static readonly DependencyProperty NavigationProperty = DependencyProperty.Register(
        nameof(Navigation), typeof(FrameworkElement), typeof(DesktopPageHeader));
    public static readonly DependencyProperty CenterProperty = DependencyProperty.Register(
        nameof(Center), typeof(FrameworkElement), typeof(DesktopPageHeader));

    private readonly ObservableCollection<DesktopHeaderActionItem> actions = [];
    private Presentation? presentation;
    private EventHandler? headerChanged;
    private CancellationTokenSource? lifetime;
    private long generation;

    public DesktopPageHeader()
    {
        InitializeComponent();
        HeaderActions.ItemsSource = actions;
        Loaded += (_, _) =>
        {
            if (presentation is null || lifetime is not null) return;
            try { StartPresentation(); Refresh(); }
            catch (Exception) { OperationFailed?.Invoke(this, EventArgs.Empty); }
        };
        Unloaded += (_, _) => ReleasePresentation();
    }

    public FrameworkElement? Navigation
    {
        get => (FrameworkElement?)GetValue(NavigationProperty);
        set => SetValue(NavigationProperty, value);
    }
    public FrameworkElement? Center
    {
        get => (FrameworkElement?)GetValue(CenterProperty);
        set => SetValue(CenterProperty, value);
    }

    public DesktopHeaderContribution? Contribution { get; private set; }
    public event EventHandler? ContributionChanged;
    public event EventHandler? ActionCancelled;
    public event EventHandler? OperationFailed;

    /// <summary>
    /// A heading fallback opts into the leading title layout. Existing slots follow that title;
    /// ordinary contributions remain trailing unless their SlotPlacement explicitly selects a side.
    /// Navigation remains mounted across presentations and never belongs to an action lifetime.
    /// </summary>
    public void Present(IDesktopHeaderSource? source, DesktopHeaderContribution fallback,
        string? headingFallback = null, IReadOnlyList<DesktopHeaderAction>? additionalActions = null)
    {
        Dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(fallback);
        if (!ReferenceEquals(presentation?.Source, source)) ReleasePresentation();
        presentation = new(source, fallback, headingFallback, additionalActions ?? []);
        if (lifetime is null) StartPresentation();
        Refresh();
    }

    public void Retire()
    {
        Dispatcher.VerifyAccess();
        ReleasePresentation();
        presentation = null;
    }

    private void StartPresentation()
    {
        lifetime = new CancellationTokenSource();
        var currentGeneration = ++generation;
        if (presentation?.Source is not { } source) return;
        headerChanged = (_, _) =>
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try
            {
                _ = Dispatcher.InvokeAsync(() =>
                {
                    if (lifetime is null || generation != currentGeneration ||
                        !ReferenceEquals(presentation?.Source, source)) return;
                    try { Refresh(); }
                    catch (Exception) { OperationFailed?.Invoke(this, EventArgs.Empty); }
                }, DispatcherPriority.DataBind);
            }
            catch (InvalidOperationException) { /* Dispatcher teardown retires this presentation. */ }
        };
        source.HeaderChanged += headerChanged;
    }

    private void Refresh()
    {
        if (presentation is not { } current || lifetime is null) return;
        var contribution = current.Source?.Header ?? current.Fallback;
        var leading = contribution.SlotPlacement == DesktopHeaderSlotPlacement.Leading ||
            contribution.SlotPlacement == DesktopHeaderSlotPlacement.Automatic && current.HeadingFallback is not null;
        var slotHost = leading ? LeadingSlot : TrailingSlot;
        var otherHost = leading ? TrailingSlot : LeadingSlot;
        otherHost.Content = null;
        otherHost.Visibility = Visibility.Collapsed;
        var slot = contribution.Slot;
        if (!ReferenceEquals(slotHost.Content, slot))
        {
            slotHost.Content = null;
            if (slot is not null && (!slot.Dispatcher.CheckAccess() ||
                LogicalTreeHelper.GetParent(slot) is not null || VisualTreeHelper.GetParent(slot) is not null))
                throw new InvalidOperationException("DESKTOP_HEADER_SLOT_ATTACHED");
            slotHost.Content = slot;
        }
        slotHost.Visibility = slot is null ? Visibility.Collapsed : Visibility.Visible;
        Heading.Text = string.IsNullOrWhiteSpace(contribution.Title) ? current.HeadingFallback ?? "" : contribution.Title;
        Heading.Visibility = current.HeadingFallback is null ? Visibility.Collapsed : Visibility.Visible;
        actions.Clear();
        foreach (var action in contribution.HeaderActions)
            actions.Add(new(generation, action));
        foreach (var action in current.AdditionalActions)
            actions.Add(new(generation, action));
        TrailingHeader.Visibility = !leading && slot is not null || actions.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
        Contribution = contribution;
        ContributionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ReleasePresentation()
    {
        if (presentation?.Source is { } source && headerChanged is not null)
            source.HeaderChanged -= headerChanged;
        headerChanged = null;
        var previous = lifetime;
        lifetime = null;
        actions.Clear();
        LeadingSlot.Content = TrailingSlot.Content = null;
        LeadingSlot.Visibility = TrailingSlot.Visibility = TrailingHeader.Visibility = Heading.Visibility = Visibility.Collapsed;
        Contribution = null;
        if (previous is not null) _ = CancelAsync(previous);
    }

    private static async Task CancelAsync(CancellationTokenSource source)
    {
        try { await source.CancelAsync(); }
        catch (Exception) { /* Contributor cancellation failures cannot reopen a retired presentation. */ }
        finally { source.Dispose(); }
    }

    private async void OnActionClick(object sender, RoutedEventArgs e)
    {
        if (!IsEnabled || sender is not Button { Tag: DesktopHeaderActionItem item } ||
            lifetime is not { } current || item.Generation != generation ||
            !actions.Any(action => ReferenceEquals(action, item))) return;
        var token = current.Token;
        try
        {
            if (!item.Action.IsEnabled) return;
            await item.Action.ExecuteAsync(token);
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            if (IsCurrent()) ActionCancelled?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            if (IsCurrent()) OperationFailed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            if (IsCurrent())
            {
                try { Refresh(); }
                catch (Exception) { OperationFailed?.Invoke(this, EventArgs.Empty); }
            }
        }

        bool IsCurrent() => ReferenceEquals(lifetime, current) && generation == item.Generation;
    }
}
