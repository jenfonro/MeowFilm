using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Desktop;

internal sealed class ShellPageSession(IDesktopPage page)
{
    public IDesktopPage Page { get; } = page;
    public ContentControl Host { get; } = new()
    {
        Visibility = Visibility.Collapsed,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch,
        IsTabStop = false
    };
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public string? SearchDraft { get; set; }
}

public sealed partial class ShellWindow
{
    private sealed class PageRequest
    {
        public CancellationTokenSource Source { get; } = new();
        public Task Cancellation { get; private set; } = Task.CompletedTask;
        public TaskCompletionSource PresentationFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Cancel()
        {
            if (Source.IsCancellationRequested) return;
            Cancellation = Source.CancelAsync();
            _ = ShellRequest.ObserveAsync(Cancellation);
        }
    }

    private readonly ShellPageSession[] _pages;
    private ShellPageSession? _currentPage, _retryPage;
    private PageRequest? _pageRequest;

    private void OnPublicPageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || _isClosing || !ReferenceEquals(sender, PublicNavigationList)) return;
        if (PublicNavigationList.SelectedItem is IDesktopPage page)
            RunUiAction(() => ActivatePageAsync(_pages.Single(item => ReferenceEquals(item.Page, page))));
    }

    internal async Task ActivatePageAsync(ShellPageSession session, Func<CancellationToken, Task>? operation = null,
        RequestPlan? navigation = null, bool recordNavigation = true)
    {
        if (_isClosing) return;
        CancelActive();
        RetireHeaderPresentation();
        var previous = _currentPage;
        CancelPageActivation();
        if (previous is not null && !ReferenceEquals(previous, session)) UnmountPage(previous);
        _pageNavigation = new CancellationTokenSource();
        _retryPlan = null; _retryPage = null;
        Volatile.Write(ref _currentPage, session);
        var generation = Interlocked.Increment(ref _presentationGeneration);
        var navigationOwner = navigation?.Session ?? _current ?? _sessions[0];
        var destination = navigation?.Destination ?? new Destination(session.Page.Id, Location: session.Page.Id, PageId: session.Page.Id);
        ApplyPresentation();
        if (session.Page is IDesktopSearchPage searchPage)
        {
            _search.SetBoxText(session.SearchDraft ?? searchPage.Query);
            var owner = _sessions.Single(item => ReferenceEquals(item.Module, searchPage.ContentOwner));
            owner.MainHost.Content = null;
        }
        if (!ContentDeck.Children.Contains(session.Host))
        {
            var view = session.Page.View ?? throw new InvalidOperationException();
            ValidateView(view, session.Host);
            session.Host.Content = view;
            ContentDeck.Children.Add(session.Host);
        }
        ApplyPresentation();
        var request = new PageRequest();
        _pageRequest = request;
        var token = request.Source.Token;
        SetStatus("正在加载…", busy: true);
        var work = ExecuteAsync();
        _ = ReapAsync();
        try
        {
            await work.WaitAsync(OperationTimeout, _timeProvider, token);
            if (Current())
            {
                if (recordNavigation) _history.Commit(navigation ?? new(navigationOwner, destination), destination);
                SetStatus();
                ApplyPresentation();
            }
        }
        catch (OperationCanceledException)
        {
            if (Current()) SetStatus("已取消");
        }
        catch (Exception)
        {
            if (Current()) { _retryPage = session; SetStatus("暂时无法完成操作", error: true, retry: true); }
            request.Cancel();
        }
        finally
        {
            if (ReferenceEquals(_pageRequest, request)) _pageRequest = null;
            request.PresentationFinished.TrySetResult();
        }

        bool Current() => !_isClosing && ReferenceEquals(_pageRequest, request) &&
            ReferenceEquals(_currentPage, session) && generation == Interlocked.Read(ref _presentationGeneration);
        async Task ExecuteAsync()
        {
            await session.Gate.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested();
                if (session.Page is IDesktopSearchPage search)
                {
                    var owner = _sessions.Single(item => ReferenceEquals(item.Module, search.ContentOwner));
                    await owner.Gate.WaitAsync(token);
                    try
                    {
                        if (!owner.Initialized)
                        {
                            await owner.Module.InitializeAsync(token);
                            owner.Initialized = true;
                        }
                    }
                    finally { owner.Gate.Release(); }
                }
                if (operation is not null) await operation(token);
                else await session.Page.ActivateAsync(token);
            }
            finally { session.Gate.Release(); }
        }
        async Task ReapAsync()
        {
            await ShellRequest.ObserveAsync(work);
            await request.PresentationFinished.Task;
            await ShellRequest.ObserveAsync(request.Cancellation);
            request.Source.Dispose();
        }
    }

    private void CancelPageActivation()
    {
        CancelPageNavigation();
        var request = _pageRequest;
        _pageRequest = null;
        request?.Cancel();
        _retryPage = null;
    }

    internal void LeavePublicPage()
    {
        if (_currentPage is not { } page) return;
        if (page.Page is IDesktopSearchPage) page.SearchDraft = SearchBox.Text;
        RetireHeaderPresentation();
        CancelPageActivation();
        Volatile.Write(ref _currentPage, null);
        UnmountPage(page);
        Interlocked.Increment(ref _presentationGeneration);
        _suppressSelection = true;
        try { PublicNavigationList.SelectedItem = null; }
        finally { _suppressSelection = false; }
    }

    private void UnmountPage(ShellPageSession session)
    {
        session.Page.Deactivate();
        session.Host.Visibility = Visibility.Collapsed;
        session.Host.Content = null;
        ContentDeck.Children.Remove(session.Host);
    }
}
