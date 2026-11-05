using System.Windows;

namespace MeowFilm.Desktop;

public sealed partial class ShellWindow
{
    private ModuleSession? _searchOwnerSession;

    private void InitializeHeaderPresentation()
    {
        // Construct navigation in the window's namescope, then mount that same tree
        // in the component. UserControl XAML cannot register its children's names
        // in the containing window's compile-time namescope.
        ((System.Windows.Controls.Panel)SearchChrome.Parent).Children.Remove(SearchChrome);
        PageHeaderHost.Navigation = SearchChrome;
        PageHeaderHost.ContributionChanged += (_, _) =>
        {
            if (!_isClosing && _current is { } current && PageHeaderHost.Contribution is { } contribution)
                ApplyHeaderContext(current, contribution);
        };
        PageHeaderHost.ActionCancelled += (_, _) => { if (!_isClosing) SetStatus("已取消"); };
        PageHeaderHost.OperationFailed += (_, _) => { if (!_isClosing) SetStatus("暂时无法完成操作", error: true); };
    }

    private void ApplyHeaderPresentation(ModuleSession current)
    {
        var source = ResolveHeaderSource(current);
        var fallback = _currentPage is { } page
            ? new DesktopHeaderContribution(page.Page.Title, SearchVisible: false, SearchEnabled: false)
            : current.IsSettingsOpen
                ? new DesktopHeaderContribution("", SearchVisible: false, SearchEnabled: false)
                : new DesktopHeaderContribution(current.Module.Title);
        bool settings = _currentPage is null && current.IsSettingsOpen;
        var additionalActions = settings
            ? _pages.Where(item => !item.Page.ShowInSidebar).Select(target => new DesktopHeaderAction(
                "page/" + target.Page.Id, target.Page.Title,
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    // Activation transfers to the page request's own lifetime as the header retires.
                    return ActivatePageAsync(target);
                },
                () => !_isClosing, automationName: target.Page.Title)).ToArray()
            : [];
        PageHeaderHost.Present(source, fallback, settings ? "设置" : null, additionalActions);
    }

    private void ApplyHeaderContext(ModuleSession current, DesktopHeaderContribution contribution)
    {
        var previousSearchOwner = _searchOwnerSession;
        if (_currentPage is null && !current.IsSettingsOpen && current.Mounted &&
            contribution.SearchVisible && contribution.SearchEnabled)
            _searchOwnerSession = current;
        if (!ReferenceEquals(previousSearchOwner, _searchOwnerSession))
            ApplySearchPresentation(force: true);

        var searchAvailable = !current.IsSettingsOpen && _searchOwnerSession is not null;
        SearchInput.Visibility = searchAvailable ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.IsEnabled = SearchButton.IsEnabled = searchAvailable;
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

    private void RetireHeaderPresentation() => PageHeaderHost.Retire();
}
