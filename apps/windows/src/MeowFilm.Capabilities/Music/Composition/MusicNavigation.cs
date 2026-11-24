using System.Collections.ObjectModel;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    internal long AdvanceLoadGeneration()
    {
        Catalog.OnLoadGenerationAdvanced();
        return ++ctx.LoadGeneration;
    }

    internal (string Route, string SearchView, ObservableCollection<MusicCatalogSection> Sections) CaptureContentSelection() =>
        (ctx.CurrentRoute, Search.SearchView, Catalog.CatalogSections);

    internal void SetLocation(MusicLocation next, string? heading = null, bool clearLibrary = false,
        bool clearCatalogSongs = false, bool clearSelection = false, bool clearTotal = false, bool clearCollection = false)
    {
        ctx.CurrentLocation = next;
        Library.SetLocationState(heading, clearLibrary, clearCatalogSongs, clearSelection, clearTotal, clearCollection);
    }

    internal bool PublishContentSelection((string Route, string SearchView, ObservableCollection<MusicCatalogSection> Sections) previous,
        bool refreshSearch = true)
    {
        bool sectionsChanged = !ReferenceEquals(previous.Sections, Catalog.CatalogSections);
        bool routeChanged = previous.Route != ctx.CurrentRoute || (Search.IsSearchPage && previous.SearchView != Search.SearchView);
        if (!sectionsChanged && !routeChanged) return false;
        Catalog.NotifyRouteChanged(sectionsChanged);
        if (!routeChanged) return false;
        Search.NotifyRouteChanged(refreshSearch);
        Library.NotifyRouteChanged();
        Discovery.NotifyRouteChanged();
        return true;
    }

    internal MusicLocation CaptureLocation() =>
        ctx.CurrentLocation with { Selected = Library.SelectedSong?.SelectionKey, Scroll = Presentation.CaptureScroll() };

    internal void RememberLocation()
    {
        ctx.Locations[ctx.CurrentLocation.Key] = CaptureLocation();
        Catalog.RememberCatalogLocation();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (ctx.Stopping) return;
        ctx.RetireOperationStatus();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ctx.Lifetime.Token);
        ctx.LoadCancellation?.Cancel();
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.LoadDrains.Add(drained.Task);
        ctx.LoadCancellation = request;
        var token = request.Token;
        try
        {
            ctx.RefreshBindings();
            await Catalog.PrepareStartupAsync(token);
            await Library.RefreshPlaylists(token);
            await Settings.ReadMetadataSettings(token);
            await Playback.ReadQueue(token);
            if (ctx.Stopping) return;
            try { await Settings.ReadAppearance(token); }
            catch (OperationCanceledException) { throw; }
            catch { Settings.SetAppearanceReadFailed(); }
            try { await Settings.ReadDeviceSettings(token); }
            catch (OperationCanceledException) { throw; }
            catch { ctx.SetStatus("播放设置读取失败"); }
            try { await Settings.ReadFeedbackSettings(token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                MeowFilm.Core.Debug.DebugLog.Warn(MeowFilm.Core.Debug.DebugCategories.MusicPlayback,
                    "startup", "startup_feedback_settings_failed", new { error = error.Message });
            }
            try { await Settings.ReadSearchPreferences(token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                MeowFilm.Core.Debug.DebugLog.Warn(MeowFilm.Core.Debug.DebugCategories.Search,
                    "startup", "startup_search_preferences_failed", new { error = error.Message });
            }
            await ctx.Application.InitializeAsync(token);
        }
        finally
        {
            ctx.LoadDrains.Remove(drained.Task); drained.TrySetResult();
            if (ReferenceEquals(ctx.LoadCancellation, request))
            { ctx.LoadCancellation = null; if (!ctx.Disposed) ctx.RefreshBindings(); }
        }
    }

    public async Task NavigateAsync(string route, CancellationToken cancellationToken)
    {
        if (ctx.Stopping || Catalog.IsUnsupportedCatalogRoute(route)) return;
        ctx.RetireOperationStatus();
        long submission = ++ctx.NavigationSubmission;
        bool retiringDiscovery = route != "catalog-discovery" && (Discovery.IsDiscovery || Discovery.HasActiveRequest);
        if (retiringDiscovery)
        {
            RememberLocation();
            await Discovery.RetireDiscoveryPageAsync();
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (ctx.Stopping || submission != ctx.NavigationSubmission) return;
        await NavigateCoreAsync(route, cancellationToken, remember: !retiringDiscovery);
    }

    private Task NavigateCoreAsync(string route, CancellationToken cancellationToken, bool remember)
    {
        if (ctx.Stopping) return Task.CompletedTask;
        if (route != ctx.CurrentRoute) Catalog.RetireDailyStyleEditor();
        Catalog.InvalidateCatalogAction();
        if (remember) RememberLocation();
        if (route == "catalog-discovery") return Discovery.LoadDiscoveryAsync(false, cancellationToken);
        if (route.StartsWith("search-local/", StringComparison.Ordinal))
        {
            string query = Uri.UnescapeDataString(route[13..]);
            if (query.Length < 1 || query.Any(char.IsControl))
                throw new ArgumentException("Invalid search route", nameof(route));
            var target = new MusicLocation("search", query);
            return LoadAsync(ctx.Locations.GetValueOrDefault(target.Key) ?? target, cancellationToken, publishStatus: false);
        }
        if (MusicCatalogController.IsCatalogRoute(route))
            return Catalog.CatalogNavigateAsync(route, cancellationToken);
        if (route is not ("favorites" or "history" or "archives") &&
            !(route.StartsWith("playlist/", StringComparison.Ordinal) && route.Length > 9))
            throw new ArgumentException("Unknown music route", nameof(route));
        return LoadAsync(ctx.Locations.GetValueOrDefault(route) ?? new(route), cancellationToken, publishStatus: false);
    }

    public string CaptureNavigationLocation() => ctx.CurrentRoute switch
    {
        "search" => "search-local/" + Uri.EscapeDataString(ctx.CurrentLocation.Query),
        "catalog-search" => "catalog-search/" + Uri.EscapeDataString(Search.SearchKeyword),
        _ => ctx.CurrentRoute
    };

    public async Task RestoreNavigationAsync(string navigationLocation, CancellationToken token)
    {
        if (ctx.Stopping) return;
        token.ThrowIfCancellationRequested();
        bool local = navigationLocation.StartsWith("search-local/", StringComparison.Ordinal);
        bool online = navigationLocation.StartsWith("catalog-search/", StringComparison.Ordinal);
        if (!local && !online) { await NavigateAsync(navigationLocation, token); return; }
        string encoded = navigationLocation[(local ? 13 : 15)..];
        string query = Uri.UnescapeDataString(encoded);
        if (query.Length < 1 || query.Any(char.IsControl))
            throw new ArgumentException("Invalid search location", nameof(navigationLocation));
        if (local) { await NavigateAsync("search-local/" + Uri.EscapeDataString(query), token); return; }
        ctx.RetireOperationStatus();
        Catalog.InvalidateCatalogAction();
        RememberLocation();
        long submission = ++ctx.NavigationSubmission;
        if (Discovery.IsDiscovery || Discovery.HasActiveRequest) await Discovery.RetireDiscoveryPageAsync();
        token.ThrowIfCancellationRequested();
        if (ctx.Stopping || submission != ctx.NavigationSubmission) return;
        if (Search.HasSearchSession && Search.SearchKeyword == query && Search.SearchAccountEpoch == Accounts.AccountEpoch)
        {
            await Search.RestoreSearchAsync(token);
            return;
        }
        await Search.BeginSearchAsync(query, token);
    }

    public async Task SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (ctx.Stopping) return;
        ctx.RetireOperationStatus();
        long submission = ++ctx.SearchSubmission;
        Catalog.InvalidateCatalogAction();
        RememberLocation();
        query = query.Trim();
        if (query.Length == 0)
        {
            if (Catalog.IsCatalogPage) await NavigateAsync("catalog-discovery", cancellationToken);
            return;
        }
        if (query.Any(char.IsControl)) throw new ArgumentException("Invalid query", nameof(query));
        bool online = Catalog.IsCatalogPage;
        long pageGeneration = ctx.LoadGeneration;
        long navigation = ctx.NavigationSubmission;
        if (Discovery.IsDiscovery || Discovery.HasActiveRequest) await Discovery.RetireDiscoveryPageAsync();
        if (ctx.Stopping || submission != ctx.SearchSubmission || pageGeneration != ctx.LoadGeneration || navigation != ctx.NavigationSubmission) return;
        await Settings.RecordSearchHistory(query, online, cancellationToken);
        if (ctx.Stopping || submission != ctx.SearchSubmission || pageGeneration != ctx.LoadGeneration || navigation != ctx.NavigationSubmission) return;
        if (online) { await Search.BeginSearchAsync(query, cancellationToken); return; }
        var target = new MusicLocation("search", query);
        await LoadAsync(ctx.Locations.GetValueOrDefault(target.Key) ?? target, cancellationToken, publishStatus: false);
    }

    public async Task SearchOnlineAsync(string query, CancellationToken token)
    {
        if (ctx.Stopping) return;
        query = query.Trim();
        if (query.Length < 1 || query.Any(char.IsControl))
            throw new ArgumentException("Invalid query", nameof(query));
        ctx.RetireOperationStatus();
        Catalog.InvalidateCatalogAction();
        RememberLocation();
        long submission = ++ctx.SearchSubmission;
        long pageGeneration = ctx.LoadGeneration;
        long navigation = ctx.NavigationSubmission;
        if (Discovery.IsDiscovery || Discovery.HasActiveRequest) await Discovery.RetireDiscoveryPageAsync();
        token.ThrowIfCancellationRequested();
        if (ctx.Stopping || submission != ctx.SearchSubmission || pageGeneration != ctx.LoadGeneration || navigation != ctx.NavigationSubmission) return;
        await Settings.RecordSearchHistory(query, true, token);
        token.ThrowIfCancellationRequested();
        if (ctx.Stopping || submission != ctx.SearchSubmission || pageGeneration != ctx.LoadGeneration || navigation != ctx.NavigationSubmission) return;
        if (Search.HasSearchSession && Search.SearchKeyword == query && Search.SearchAccountEpoch == Accounts.AccountEpoch)
        {
            await Search.RestoreSearchAsync(token);
            await Search.OpenSearchTabAsync(MusicSearchController.SearchOverview);
            return;
        }
        await Search.BeginSearchAsync(query, token, overview: true);
    }

    internal Task LoadUiAsync(MusicLocation target) => Library.LoadUiAsync(target);

    internal Task LoadAsync(MusicLocation target, CancellationToken cancellationToken, int retry = 0,
        bool publishStatus = true) =>
        Library.LoadAsync(target, cancellationToken, retry, publishStatus);
}
