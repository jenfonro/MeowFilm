using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    internal sealed record Location(string Route, string Query = "", int Offset = 0,
        string? Date = null, string? Selected = null, double Scroll = 0)
    {
        public string Key => Route == "search" ? "search/" + Query : Route;
    }

    private Location location = new("catalog-home");
    private readonly Dictionary<string, Location> locations = [];
    private readonly HashSet<Task> loadDrains = [];
    private CancellationTokenSource? loadCancellation;
    private long loadGeneration, navigationSubmission, searchSubmission;
    private bool publishing;

    internal string CurrentRoute => location.Route;
    internal Location CurrentLocation => location;
    internal Dictionary<string, Location> Locations => locations;
    internal CancellationTokenSource? LoadCancellation { get => loadCancellation; set => loadCancellation = value; }
    internal long LoadGeneration => loadGeneration;
    internal long NavigationSubmission => navigationSubmission;
    internal bool Publishing { get => publishing; set => publishing = value; }

    internal long AdvanceLoadGeneration()
    {
        Catalog.OnLoadGenerationAdvanced();
        return ++loadGeneration;
    }

    internal (string Route, string SearchView, ObservableCollection<MusicCatalogSection> Sections) CaptureContentSelection() =>
        (location.Route, Search.SearchView, Catalog.CatalogSections);

    internal void SetLocation(Location next, string? heading = null, bool clearLibrary = false,
        bool clearCatalogSongs = false, bool clearSelection = false, bool clearTotal = false, bool clearCollection = false)
    {
        location = next;
        Library.SetLocationState(heading, clearLibrary, clearCatalogSongs, clearSelection, clearTotal, clearCollection);
    }

    internal void PublishContentSelection((string Route, string SearchView, ObservableCollection<MusicCatalogSection> Sections) previous)
    {
        bool sectionsChanged = !ReferenceEquals(previous.Sections, Catalog.CatalogSections);
        bool routeChanged = previous.Route != location.Route || (Search.IsSearchPage && previous.SearchView != Search.SearchView);
        if (!sectionsChanged && !routeChanged) return;
        Catalog.NotifyRouteChanged(sectionsChanged);
        if (!routeChanged) return;
        Search.NotifyRouteChanged();
        Library.NotifyRouteChanged();
        Discovery.NotifyRouteChanged();
    }

    internal Location CaptureLocation() =>
        location with { Selected = Library.SelectedSong?.SelectionKey, Scroll = Presentation.CaptureScroll() };

    internal void RememberLocation()
    {
        locations[location.Key] = CaptureLocation();
        Catalog.RememberCatalogLocation();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (stopping) return;
        RetireOperationStatus();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        loadCancellation?.Cancel();
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loadDrains.Add(drained.Task);
        loadCancellation = request;
        var token = request.Token;
        try
        {
            RefreshBindings();
            // Restore App state without navigating or publishing a content page.
            await Catalog.PrepareStartupAsync(token);
            await Library.RefreshPlaylists(token);
            await Settings.ReadMetadataSettings(token);
            await Playback.ReadQueue(token);
            if (stopping) return;
            try { await Settings.ReadAppearance(token); }
            catch (OperationCanceledException) { throw; }
            catch { Settings.SetAppearanceReadFailed(); }
            try { await Settings.ReadDeviceSettings(token); }
            catch (OperationCanceledException) { throw; }
            catch { SetStatus("播放设置读取失败"); }
            try { await Settings.ReadFeedbackSettings(token); }
            catch (OperationCanceledException) { throw; }
            catch { /* Feedback preferences fail closed independently. */ }
            try { await Settings.ReadSearchPreferences(token); }
            catch (OperationCanceledException) { throw; }
            catch { /* Search preferences fail closed independently. */ }
            await application.InitializeAsync(token);
        }
        finally
        {
            loadDrains.Remove(drained.Task); drained.TrySetResult();
            if (ReferenceEquals(loadCancellation, request))
            { loadCancellation = null; if (!disposed) RefreshBindings(); }
        }
    }

    public async Task NavigateAsync(string route, CancellationToken cancellationToken)
    {
        if (stopping || Catalog.IsUnsupportedCatalogRoute(route)) return;
        RetireOperationStatus();
        long submission = ++navigationSubmission;
        bool retiringDiscovery = route != "catalog-discovery" && (Discovery.IsDiscovery || Discovery.HasActiveRequest);
        if (retiringDiscovery)
        {
            RememberLocation();
            await Discovery.RetireDiscoveryPageAsync();
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (stopping || submission != navigationSubmission) return;
        await NavigateCoreAsync(route, cancellationToken, remember: !retiringDiscovery);
    }

    private Task NavigateCoreAsync(string route, CancellationToken cancellationToken, bool remember)
    {
        if (stopping) return Task.CompletedTask;
        if (route != location.Route) Catalog.RetireDailyStyleEditor();
        Catalog.InvalidateCatalogAction();
        if (remember) RememberLocation();
        if (route == "catalog-discovery") return Discovery.LoadDiscoveryAsync(false, cancellationToken);
        if (route.StartsWith("search-local/", StringComparison.Ordinal))
        {
            string query = Uri.UnescapeDataString(route[13..]);
            if (query.Length < 1 || query.Any(char.IsControl))
                throw new ArgumentException("Invalid search route", nameof(route));
            var target = new Location("search", query);
            return LoadAsync(locations.GetValueOrDefault(target.Key) ?? target, cancellationToken, publishStatus: false);
        }
        if (MusicCatalogController.IsCatalogRoute(route))
            return Catalog.CatalogNavigateAsync(route, cancellationToken);
        if (route is not ("favorites" or "history" or "archives") &&
            !(route.StartsWith("playlist/", StringComparison.Ordinal) && route.Length > 9))
            throw new ArgumentException("Unknown music route", nameof(route));
        return LoadAsync(locations.GetValueOrDefault(route) ?? new(route), cancellationToken, publishStatus: false);
    }

    public string CaptureNavigationLocation() => location.Route switch
    {
        "search" => "search-local/" + Uri.EscapeDataString(location.Query),
        "catalog-search" => "catalog-search/" + Uri.EscapeDataString(Search.SearchKeyword),
        _ => location.Route
    };

    public async Task RestoreNavigationAsync(string navigationLocation, CancellationToken token)
    {
        if (stopping) return;
        token.ThrowIfCancellationRequested();
        bool local = navigationLocation.StartsWith("search-local/", StringComparison.Ordinal);
        bool online = navigationLocation.StartsWith("catalog-search/", StringComparison.Ordinal);
        if (!local && !online) { await NavigateAsync(navigationLocation, token); return; }
        string encoded = navigationLocation[(local ? 13 : 15)..];
        string query = Uri.UnescapeDataString(encoded);
        if (query.Length < 1 || query.Any(char.IsControl))
            throw new ArgumentException("Invalid search location", nameof(navigationLocation));
        if (local) { await NavigateAsync("search-local/" + Uri.EscapeDataString(query), token); return; }
        RetireOperationStatus();
        Catalog.InvalidateCatalogAction();
        RememberLocation();
        long submission = ++navigationSubmission;
        if (Discovery.IsDiscovery || Discovery.HasActiveRequest) await Discovery.RetireDiscoveryPageAsync();
        token.ThrowIfCancellationRequested();
        if (stopping || submission != navigationSubmission) return;
        if (Search.HasSearchSession && Search.SearchKeyword == query && Search.SearchAccountEpoch == Accounts.AccountEpoch)
        {
            await Search.RestoreSearchAsync(token);
            return;
        }
        // Only the current valid online search is retained. Retired history uses the domain start path.
        await Search.BeginSearchAsync(query, token);
    }

    public async Task SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (stopping) return;
        RetireOperationStatus();
        long submission = ++searchSubmission;
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
        long pageGeneration = loadGeneration;
        long navigation = navigationSubmission;
        if (Discovery.IsDiscovery || Discovery.HasActiveRequest) await Discovery.RetireDiscoveryPageAsync();
        if (stopping || submission != searchSubmission || pageGeneration != loadGeneration || navigation != navigationSubmission) return;
        await Settings.RecordSearchHistory(query, online, cancellationToken);
        if (stopping || submission != searchSubmission || pageGeneration != loadGeneration) return;
        if (online) { await Search.BeginSearchAsync(query, cancellationToken); return; }
        var target = new Location("search", query);
        await LoadAsync(locations.GetValueOrDefault(target.Key) ?? target, cancellationToken, publishStatus: false);
    }

    /// <summary>Public search entry always targets online comprehensive music, including from a library page.</summary>
    public async Task SearchOnlineAsync(string query, CancellationToken token)
    {
        if (stopping) return;
        query = query.Trim();
        if (query.Length < 1 || query.Any(char.IsControl))
            throw new ArgumentException("Invalid query", nameof(query));
        RetireOperationStatus();
        Catalog.InvalidateCatalogAction();
        RememberLocation();
        long submission = ++searchSubmission;
        if (Discovery.IsDiscovery || Discovery.HasActiveRequest) await Discovery.RetireDiscoveryPageAsync();
        token.ThrowIfCancellationRequested();
        if (stopping || submission != searchSubmission) return;
        await Settings.RecordSearchHistory(query, true, token);
        token.ThrowIfCancellationRequested();
        if (stopping || submission != searchSubmission) return;
        if (Search.HasSearchSession && Search.SearchKeyword == query && Search.SearchAccountEpoch == Accounts.AccountEpoch)
        {
            await Search.RestoreSearchAsync(token);
            await Search.OpenSearchTabAsync(MusicSearchController.SearchOverview);
            return;
        }
        await Search.BeginSearchAsync(query, token, overview: true);
    }

    internal async Task LoadUiAsync(Location target)
    {
        if (stopping) return;
        try { await LoadAsync(target, lifetime.Token); }
        catch (OperationCanceledException) { }
        catch { /* LoadAsync publishes a bounded error only for its current generation. */ }
    }

    internal Task LoadAsync(Location target, CancellationToken cancellationToken, int retry = 0,
        bool publishStatus = true)
    {
        if (stopping) return Task.CompletedTask;
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loadDrains.Add(drained.Task);
        return Tracked();
        async Task Tracked()
        {
            try { await LoadCoreAsync(target, cancellationToken, retry, publishStatus); }
            finally { loadDrains.Remove(drained.Task); drained.TrySetResult(); }
        }
    }

    private async Task LoadCoreAsync(Location target, CancellationToken cancellationToken, int retry,
        bool publishStatus)
    {
        var generation = AdvanceLoadGeneration();
        var stamp = publishStatus ? ++statusEpoch : statusEpoch;
        Library.CancelCollectionPage();
        loadCancellation?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        loadCancellation = request;
        var token = request.Token;
        try
        {
            if (publishStatus) status = "";
            RefreshBindings();
            var rows = Array.Empty<SongRow>();
            var dates = Array.Empty<string>();
            var archives = Array.Empty<ArchivedPlaylist>();
            PlaylistRow? detail = null;
            string title;
            var count = 0;
            switch (target.Route)
            {
                case "search":
                {
                    title = "曲库搜索";
                    var reply = await Call("music.library.search", new { query = target.Query, offset = target.Offset, limit = 100 }, token);
                    rows = MusicDto.Page(reply, "items", target.Offset).Select(item => SongRow.Read(item)).ToArray();
                    count = MusicDto.Count(reply, "total");
                    break;
                }
                case "history":
                {
                    title = "最近播放";
                    var history = await ReadHistoryAsync("days", token);
                    dates = history.Select(item => MusicDto.Text(item, "id")).ToArray();
                    MusicDto.Require(dates.Distinct().Count() == dates.Length);
                    target = target with { Date = dates.Contains(target.Date) ? target.Date : dates.FirstOrDefault() };
                    if (target.Date is not null)
                    {
                        var reply = await Call("music.history.get", new { kind = "days", id = target.Date, offset = target.Offset, limit = 100 }, token);
                        rows = MusicDto.Page(reply, "items", target.Offset).Select(item => SongRow.Read(item.GetProperty("song"))).ToArray();
                        count = MusicDto.Count(reply, "total");
                    }
                    break;
                }
                case "archives":
                {
                    title = "已播队列";
                    var history = await ReadHistoryAsync("playlists", token);
                    archives = history.Select(item => new ArchivedPlaylist(
                        MusicDto.Text(item, "id"), MusicDto.Text(item, "title"), MusicDto.Count(item, "count"))).ToArray();
                    break;
                }
                default:
                {
                    target = target with { Offset = 0 };
                    var id = target.Route == "favorites" ? "favorites" : target.Route[9..];
                    var reply = await Call("music.library.detail", new { id, offset = 0, limit = 100 }, token);
                    detail = PlaylistRow.Read(reply.GetProperty("collection"));
                    MusicDto.Require(detail.Id == id);
                    title = detail.Name; count = MusicDto.Count(reply, "total");
                    rows = MusicDto.Page(reply, "songs", 0).Select(item => SongRow.Read(item)).ToArray();
                    break;
                }
            }
            token.ThrowIfCancellationRequested();
            if (stopping || generation != loadGeneration) return;
            if (detail is null && target.Offset > 0 && target.Offset >= count)
            {
                // A successful removal may shrink the last page. Never publish a phantom offset.
                if (retry >= 2) throw new MusicOperationException("REVISION_CONFLICT");
                await LoadAsync(target with { Offset = count == 0 ? 0 : (count - 1) / 100 * 100 },
                    cancellationToken, retry + 1, publishStatus);
                return;
            }
            publishing = true;
            try
            {
                var previousSelection = CaptureContentSelection();
                location = target;
                Library.ApplyLoadedLocation(detail, title, count, rows, dates, archives, target.Selected);
                locations[target.Key] = target;
                PublishContentSelection(previousSelection);
                RefreshBindings();
            }
            finally { publishing = false; }
            _ = Presentation.Dispatcher.EnqueueAsync(() =>
            {
                if (!stopping && generation == loadGeneration) Presentation.RestoreScroll(target.Scroll);
            }, DispatcherQueuePriority.Low);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            if (publishStatus && !stopping && generation == loadGeneration && stamp == statusEpoch)
                SetStatus(ErrorText(error));
            throw;
        }
        finally
        {
            if (ReferenceEquals(loadCancellation, request))
            { loadCancellation = null; if (!disposed) RefreshBindings(); }
        }
    }

    private async Task<JsonElement[]> ReadHistoryAsync(string kind, CancellationToken token)
    {
        var rows = new List<JsonElement>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var reply = await Call("music.history.get", new { kind, offset = rows.Count, limit = 100 }, token);
            var page = MusicDto.Page(reply, "items", rows.Count);
            MusicDto.Require(page.All(row => ids.Add(MusicDto.Text(row, "id"))));
            rows.AddRange(page);
            if (rows.Count >= MusicDto.Count(reply, "total")) return rows.ToArray();
        }
    }
}
