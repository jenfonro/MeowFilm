using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace MeowFilm.Desktop;

internal sealed record ShellHeaderActionItem(long Generation, DesktopHeaderAction Action)
{
    public string Label => Action.Label;
    public string AutomationName => Action.AutomationName;
    public bool IsPrimary => Action.IsPrimary;
    public bool IsEnabled => Action.IsEnabled;
}

public sealed partial class ShellWindow
{
    private readonly ObservableCollection<ShellHeaderActionItem> _headerActions = [];
    private IDesktopHeaderSource? _headerSource;
    private EventHandler? _headerChanged;
    private CancellationTokenSource? _headerLifetime;
    private long _headerGeneration;

    private void InitializeHeaderPresentation() => HeaderActions.ItemsSource = _headerActions;

    private void ApplyHeaderPresentation(ModuleSession current)
    {
        var source = ResolveHeaderSource(current);
        SetHeaderSource(source);
        EnsureHeaderLifetime();

        var fallback = _currentPage is { } page
            ? new DesktopHeaderContribution(page.Page.Title, SearchVisible: false, SearchEnabled: false)
            : current.IsSettingsOpen
                ? new DesktopHeaderContribution("", SearchVisible: false, SearchEnabled: false)
                : new DesktopHeaderContribution(current.Module.Title);
        var contribution = source?.Header ?? fallback;

        bool settings = _currentPage is null && current.IsSettingsOpen;
        var slotHost = settings ? SettingsHeaderSlot : HeaderSlot;
        var otherHost = settings ? HeaderSlot : SettingsHeaderSlot;
        otherHost.Content = null;
        otherHost.Visibility = Visibility.Collapsed;
        var slot = contribution.Slot;
        if (!ReferenceEquals(slotHost.Content, slot))
        {
            slotHost.Content = null;
            if (slot is not null &&
                (LogicalTreeHelper.GetParent(slot) is not null ||
                 System.Windows.Media.VisualTreeHelper.GetParent(slot) is not null))
                throw new InvalidOperationException("SHELL_HEADER_SLOT_ATTACHED");
            slotHost.Content = slot;
        }
        slotHost.Visibility = slot is null ? Visibility.Collapsed : Visibility.Visible;
        SettingsHeading.Text = string.IsNullOrWhiteSpace(contribution.Title) ? "设置" : contribution.Title;
        SettingsHeading.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        ContentRegion.Margin = settings ? new Thickness(0) : new Thickness(0, 4, 0, 0);

        _headerActions.Clear();
        foreach (var action in contribution.HeaderActions)
            _headerActions.Add(new(_headerGeneration, action));
        if (_currentPage is null && current.IsSettingsOpen)
            foreach (var hiddenPage in _pages.Where(item => !item.Page.ShowInSidebar))
            {
                var target = hiddenPage;
                _headerActions.Add(new(_headerGeneration, new DesktopHeaderAction(
                    "page/" + target.Page.Id, target.Page.Title,
                    token =>
                    {
                        token.ThrowIfCancellationRequested();
                        // Activation transfers to the page request's own lifetime. The
                        // header retires itself as part of that transition.
                        return ActivatePageAsync(target);
                    },
                    () => !_isClosing, automationName: target.Page.Title)));
            }

        var searchAvailable = _currentPage is null && !current.IsSettingsOpen &&
            contribution.SearchVisible && contribution.SearchEnabled;
        SearchInput.Visibility = contribution.SearchVisible && _currentPage is null && !current.IsSettingsOpen
            ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.IsEnabled = SearchButton.IsEnabled = searchAvailable;
        PageHeader.Visibility = (!settings && slot is not null) || _headerActions.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
        var title = string.IsNullOrWhiteSpace(contribution.Title) ? current.Module.Title : contribution.Title;
        System.Windows.Automation.AutomationProperties.SetName(ContentDeck, title);
        Title = string.IsNullOrWhiteSpace(title) ? "meowfilm" : $"meowfilm · {title}";
    }

    private IDesktopHeaderSource? ResolveHeaderSource(ModuleSession current)
    {
        if (_currentPage is { } page)
            return page.Page as IDesktopHeaderSource ?? page.Host.Content as IDesktopHeaderSource;
        return (current.IsSettingsOpen ? current.SettingsHost.Content : current.MainHost.Content) as IDesktopHeaderSource;
    }

    private void SetHeaderSource(IDesktopHeaderSource? source)
    {
        if (ReferenceEquals(source, _headerSource)) return;
        if (_headerSource is not null && _headerChanged is not null)
            _headerSource.HeaderChanged -= _headerChanged;
        RetireHeaderPresentation();
        _headerSource = source;
        if (source is null) { _headerChanged = null; return; }
        _headerChanged = (_, _) => QueueUi(() =>
        {
            if (_current is { } current && ReferenceEquals(_headerSource, source) &&
                ReferenceEquals(ResolveHeaderSource(current), source))
                ApplyHeaderPresentation(current);
        });
        source.HeaderChanged += _headerChanged;
    }

    private void EnsureHeaderLifetime()
    {
        if (_headerLifetime is not null) return;
        _headerLifetime = new CancellationTokenSource();
        _headerGeneration++;
    }

    private void RetireHeaderPresentation()
    {
        var previous = _headerLifetime;
        _headerLifetime = null;
        _headerActions.Clear();
        HeaderSlot.Content = null;
        HeaderSlot.Visibility = Visibility.Collapsed;
        SettingsHeaderSlot.Content = null;
        SettingsHeaderSlot.Visibility = Visibility.Collapsed;
        if (previous is null) return;
        _ = RetireAsync(previous);

        static async Task RetireAsync(CancellationTokenSource source)
        {
            try { await ShellRequest.ObserveAsync(source.CancelAsync()); }
            finally { source.Dispose(); }
        }
    }

    private void OnHeaderActionClick(object sender, RoutedEventArgs e)
    {
        if (_isClosing || sender is not Button { Tag: ShellHeaderActionItem item }) return;
        RunUiAction(() => RunHeaderActionAsync(item));
    }

    private async Task RunHeaderActionAsync(ShellHeaderActionItem item)
    {
        var lifetime = _headerLifetime;
        if (lifetime is null || item.Generation != _headerGeneration || !IsPresented() || !item.Action.IsEnabled) return;
        try
        {
            await item.Action.ExecuteAsync(lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            if (IsCurrent()) SetStatus("已取消");
        }
        catch (Exception)
        {
            if (IsCurrent()) SetStatus("暂时无法完成操作", error: true);
        }
        finally
        {
            if (IsCurrent() && _current is { } current) ApplyHeaderPresentation(current);
        }

        bool IsPresented() => _headerActions.Any(current => ReferenceEquals(current, item));
        bool IsCurrent() => !_isClosing && ReferenceEquals(lifetime, _headerLifetime) &&
            item.Generation == _headerGeneration;
    }
}
