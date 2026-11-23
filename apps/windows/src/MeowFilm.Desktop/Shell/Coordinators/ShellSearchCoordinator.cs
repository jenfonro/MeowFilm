using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Desktop;

internal sealed class ShellSearchCoordinator(
    TextBox searchBox,
    ShellSearchScope picker,
    Grid searchInput,
    Border searchDivider,
    Func<ShellPageSession?> getCurrentPage,
    Func<ShellRequest?> getActiveRequest)
{
    private ModuleSession? _searchOwnerSession;
    private IDesktopSearchPage? _searchPage;
    private ModuleSession? _searchSession;
    private string? _searchPresentationKey;
    private string? _searchContext;
    private bool _suppressSearch;
    private bool _searchComposing;

    public ModuleSession? OwnerSession => _searchOwnerSession;
    public IDesktopSearchPage? SearchPage => _searchPage;
    public bool IsComposing => _searchComposing;

    public static string SearchKey(Destination? destination) => destination switch
    {
        { IsSearch: true } => "search",
        { Value.Length: > 0 } value => "route/" + value.Value,
        _ => "default"
    };

    public void Initialize(IEnumerable<ShellPageSession> pages)
    {
        picker.SearchField = searchInput;
        picker.SurfaceSource = searchBox;
        _searchPage = pages.Select(item => item.Page).OfType<IDesktopSearchPage>().SingleOrDefault();
        if (_searchPage is { } searchPage)
        {
            picker.ItemsSource = searchPage.Targets;
            picker.Visibility = Visibility.Visible;
            searchDivider.Visibility = Visibility.Visible;
            searchBox.Padding = new(100, 4, 52, 4);
        }
    }

    public void SetBoxText(string text)
    {
        _suppressSearch = true;
        try { searchBox.Text = text; }
        finally { _suppressSearch = false; }
    }

    public void OnCompositionStarted() => _searchComposing = true;
    public void OnCompositionEnded() => _searchComposing = false;

    public void OnTextChanged()
    {
        if (_suppressSearch) return;
        if (getCurrentPage() is { Page: IDesktopSearchPage } page) page.SearchDraft = searchBox.Text;
        else if (_searchSession is { } owner)
            owner.SearchDrafts[owner.ActiveSearchKey] = searchBox.Text;
    }

    public void ApplySearchDraft(ModuleSession session, Destination? destination = null, bool force = false)
    {
        var key = SearchKey(destination ?? session.Current);
        if (!force && ReferenceEquals(_searchSession, session) &&
            string.Equals(_searchPresentationKey, key, StringComparison.Ordinal)) return;
        _searchSession = session;
        _searchPresentationKey = key;
        session.ActiveSearchKey = key;
        SetBoxText(session.SearchDrafts.GetValueOrDefault(key) ?? "");
    }

    public void ApplyPresentation(bool force = false)
    {
        if (getCurrentPage()?.Page is IDesktopSearchPage || _searchOwnerSession is not { } owner) return;
        var activeRequest = getActiveRequest();
        var destination = activeRequest is not null && ReferenceEquals(activeRequest.Plan.Session, owner)
            ? activeRequest.Plan.Destination
            : owner.Current;
        ApplySearchDraft(owner, destination, force);
    }

    public void PrepareDestination(ModuleSession session, Destination target)
    {
        if (target.IsSearch) session.SearchDrafts[SearchKey(target)] = target.Value;
        if (ReferenceEquals(session, _searchOwnerSession))
            ApplySearchDraft(session, target, force: true);
    }

    public void CommitSearchDestination(ModuleSession session, Destination destination)
    {
        if (!destination.IsSearch) return;
        session.ActiveSearchKey = SearchKey(destination);
        session.SearchDrafts[session.ActiveSearchKey] = destination.Value;
        if (ReferenceEquals(session, _searchOwnerSession))
            SetBoxText(destination.Value);
    }

    public void ApplyHeaderContext(ModuleSession current, DesktopHeaderContribution contribution)
    {
        var previousSearchOwner = _searchOwnerSession;
        var currentPage = getCurrentPage();
        if (_searchPage is { } searchPage && !current.IsSettingsOpen)
        {
            string? target = contribution.SearchTargetId;
            string context = currentPage is not null
                ? "page/" + currentPage.Page.Id + "/" + contribution.SearchContextId + "/" + target
                : "module/" + current.Module.Id + "/" + current.Module.CaptureNavigationLocation() + "/" + target;
            if (context != _searchContext && target is not null && searchPage.Targets.Any(item => item.Id == target))
            {
                _searchContext = context;
                picker.SelectedValue = target;
            }
        }
        if (currentPage is null && !current.IsSettingsOpen && current.Mounted &&
            contribution.SearchVisible && contribution.SearchEnabled)
            _searchOwnerSession = current;
        if (!ReferenceEquals(previousSearchOwner, _searchOwnerSession))
            ApplyPresentation(force: true);

        var searchAvailable = !current.IsSettingsOpen && _searchOwnerSession is not null;
        searchInput.Visibility = searchAvailable ? Visibility.Visible : Visibility.Collapsed;
        searchBox.IsEnabled = picker.IsEnabled = searchAvailable;
    }
}
