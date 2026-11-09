using System.Windows.Threading;

namespace MeowFilm.Desktop;

public sealed partial class ShellWindow
{
    private CancellationTokenSource? _pageNavigation;

    /// <summary>
    /// Trusted App/page assembly only, on the UI dispatcher. Admit an operation for the
    /// currently presented page and an enabled capability's declared route. The token retires on
    /// page exit/re-entry, cancel, or close; link it to the operation's own lifetime.
    /// It is not an event-bus permission or a security boundary against in-process code.
    /// </summary>
    public CancellationToken GetPageNavigationToken(IDesktopPage page, string moduleId, string route)
    {
        Dispatcher.VerifyAccess();
        ValidatePageNavigation(page, moduleId, route);
        return (_pageNavigation ?? throw new InvalidOperationException("SHELL_NAVIGATION_STALE")).Token;
    }

    /// <summary>
    /// Explicit App-bound navigation, not a NavigationRequested broadcast. Only the
    /// current registered public page can leave for an enabled capability's declared route.
    /// Completes after actual initialization/navigation; cancellation, supersession,
    /// timeout and failure are reported to the caller as well as the shell UI.
    /// </summary>
    public async Task NavigateFromPageAsync(IDesktopPage page, string moduleId, string route,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var generation = Interlocked.Read(ref _presentationGeneration);
        if (Dispatcher.CheckAccess()) await NavigateAsync();
        else await Dispatcher.InvokeAsync(NavigateAsync, DispatcherPriority.Normal, cancellationToken).Task.Unwrap();

        async Task NavigateAsync()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != Interlocked.Read(ref _presentationGeneration))
                throw new OperationCanceledException("SHELL_NAVIGATION_STALE");
            var session = ValidatePageNavigation(page, moduleId, route);
            session.IsSettingsOpen = false;
            _returnPage = _currentPage;
            PreparePresentation(session);
            await RunRequestAsync(new(session, new(route)), cancellationToken, reportOutcome: true);
        }
    }

    private ModuleSession ValidatePageNavigation(IDesktopPage page, string moduleId, string route)
    {
        if (_isClosing || _isClosed) throw new InvalidOperationException("SHELL_NAVIGATION_CLOSED");
        if (page is null || !ReferenceEquals(_currentPage?.Page, page) || _pageNavigation is null)
            throw new InvalidOperationException("SHELL_NAVIGATION_STALE");
        var session = _sessions.SingleOrDefault(item => string.Equals(item.Module.Id, moduleId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("SHELL_MODULE_UNAVAILABLE");
        if (string.IsNullOrWhiteSpace(route) ||
            !session.Module.Navigation.Any(item => string.Equals(item.Route, route, StringComparison.Ordinal)))
            throw new InvalidOperationException("SHELL_ROUTE_UNAVAILABLE");
        return session;
    }

    private void CancelPageNavigation()
    {
        var previous = _pageNavigation;
        _pageNavigation = null; // Retire admission before arbitrary cancellation callbacks.
        if (previous is not null)
        {
            Interlocked.Increment(ref _presentationGeneration);
            _ = RetireAsync(previous);
        }

        static async Task RetireAsync(CancellationTokenSource source)
        {
            try { await ShellRequest.ObserveAsync(source.CancelAsync()); }
            finally { source.Dispose(); }
        }
    }
}
