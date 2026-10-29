using System.Text.Json;
using System.Windows.Threading;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    private sealed record Location(string Route, string Query = "", int Offset = 0,
        string? Date = null, string? Selected = null, double Scroll = 0)
    {
        public string Key => Route == "search" ? "search/" + Query : Route;
    }
    private Location location = new("catalog-home");
    private readonly Dictionary<string, Location> locations = [];
    private readonly HashSet<Task> loadDrains = [];
    private CancellationTokenSource? loadCancellation;
    private CancellationTokenSource? collectionPageCancellation;
    private long loadGeneration, libraryRead, libraryRevision = -1;
    private bool publishing;

    private Location CaptureLocation() => location with { Selected = selectedSong?.SelectionKey, Scroll = page.CaptureScroll() };
    private void RememberLocation()
    {
        locations[location.Key] = CaptureLocation();
        RememberCatalogLocation();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (stopping) return;
        RetireOperationStatus();
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loadDrains.Add(drained.Task);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        loadCancellation?.Cancel();
        loadCancellation = request;
        var token = request.Token;
        RefreshBindings();
        try
        {
            // Restore App state without navigating or publishing a content page.
            await RefreshPlaylists(token);
            await ReadMetadataSettings(token);
            await ReadQueue(token);
            if (stopping) return;
            try { await ReadAppearance(token); }
            catch (OperationCanceledException) { throw; }
            catch { appearanceStatus = "外观设置读取失败"; AppearanceBindings(); }
            try { await ReadDeviceSettings(token); }
            catch (OperationCanceledException) { throw; }
            catch { status = "播放设置读取失败"; Changed(nameof(Status)); }
            try { await ReadFeedbackSettings(token); }
            catch (OperationCanceledException) { throw; }
            catch { /* Feedback preferences fail closed independently. */ }
            try { await ReadSearchPreferences(token); }
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
        if (stopping) return;
        RetireOperationStatus();
        long submission = ++navigationSubmission;
        bool retiringDiscovery = route != "catalog-discovery" && (IsDiscovery || discoveryRequest is not null);
        if (retiringDiscovery)
        {
            RememberLocation();
            await RetireDiscoveryPageAsync();
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (stopping || submission != navigationSubmission) return;
        await NavigateCoreAsync(route, cancellationToken, remember: !retiringDiscovery);
    }
    private Task NavigateCoreAsync(string route, CancellationToken cancellationToken, bool remember)
    {
        if (stopping) return Task.CompletedTask;
        InvalidateCatalogAction();
        if (remember) RememberLocation();
        if (route == "catalog-discovery") return LoadDiscoveryAsync(false, cancellationToken);
        if (route.StartsWith("search-local/", StringComparison.Ordinal))
        {
            string query = Uri.UnescapeDataString(route[13..]);
            if (query.Length is < 1 or > 200 || query.Any(char.IsControl))
                throw new ArgumentException("Invalid search route", nameof(route));
            return LoadAsync(new("search", query), cancellationToken, publishStatus: false);
        }
        if (IsCatalogRoute(route))
            return CatalogNavigateAsync(route, cancellationToken);
        if (route is not ("favorites" or "history" or "archives") &&
            !(route.StartsWith("playlist/", StringComparison.Ordinal) && route.Length > 9))
            throw new ArgumentException("Unknown music route", nameof(route));
        return LoadAsync(locations.GetValueOrDefault(route) ?? new(route), cancellationToken, publishStatus: false);
    }
    public async Task SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (stopping) return;
        RetireOperationStatus();
        long submission = ++searchSubmission;
        InvalidateCatalogAction();
        RememberLocation();
        query = query.Trim();
        if (query.Length == 0)
        {
            if (IsCatalogPage) await NavigateAsync("catalog-discovery", cancellationToken);
            return;
        }
        if (query.Length > 200 || query.Any(char.IsControl)) throw new ArgumentException("Invalid query", nameof(query));
        bool online = IsCatalogPage;
        long pageGeneration = loadGeneration;
        long navigation = navigationSubmission;
        if (IsDiscovery || discoveryRequest is not null) await RetireDiscoveryPageAsync();
        if (stopping || submission != searchSubmission || pageGeneration != loadGeneration || navigation != navigationSubmission) return;
        await RecordSearchHistory(query, online, cancellationToken);
        if (stopping || submission != searchSubmission || pageGeneration != loadGeneration) return;
        if (online) { await BeginAggregateSearchAsync(query, cancellationToken); return; }
        var target = new Location("search", query);
        await LoadAsync(locations.GetValueOrDefault(target.Key) ?? target, cancellationToken, publishStatus: false);
    }
    private void RetireOperationStatus()
    {
        ++statusEpoch;
        status = "";
        Changed(nameof(Status));
    }
    private async Task LoadUiAsync(Location target)
    {
        if (stopping) return;
        try { await LoadAsync(target, lifetime.Token); }
        catch (OperationCanceledException) { }
        catch { /* LoadAsync publishes a bounded error only for its current generation. */ }
    }
    private Task LoadAsync(Location target, CancellationToken cancellationToken, int retry = 0,
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
        collectionPageCancellation?.Cancel();
        loadCancellation?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        loadCancellation = request;
        var token = request.Token;
        if (publishStatus) status = "";
        RefreshBindings();
        try
        {
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
                    var reply = await Call("music.history.get", new { kind = "days", offset = 0, limit = 100 }, token);
                    dates = MusicDto.Page(reply, "items", 0).Select(item => MusicDto.Text(item, "id")).ToArray();
                    MusicDto.Require(dates.Length <= 7 && dates.Distinct().Count() == dates.Length);
                    target = target with { Date = dates.Contains(target.Date) ? target.Date : dates.FirstOrDefault() };
                    if (target.Date is not null)
                    {
                        reply = await Call("music.history.get", new { kind = "days", id = target.Date, offset = target.Offset, limit = 100 }, token);
                        rows = MusicDto.Page(reply, "items", target.Offset).Select(item => SongRow.Read(item.GetProperty("song"))).ToArray();
                        count = MusicDto.Count(reply, "total");
                    }
                    break;
                }
                case "archives":
                {
                    title = "已播队列";
                    var reply = await Call("music.history.get", new { kind = "playlists", offset = 0, limit = 100 }, token);
                    archives = MusicDto.Page(reply, "items", 0).Select(item => new ArchivedPlaylist(
                        MusicDto.Text(item, "id"), MusicDto.Text(item, "title"), MusicDto.Count(item, "count"))).ToArray();
                    MusicDto.Require(archives.Length <= 5);
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
                location = target; collection = detail; heading = title; total = count;
                selectedSong = null; Changed(nameof(SelectedSong));
                Songs.Clear(); foreach (var row in rows) Songs.Add(row);
                HistoryDates.Clear(); foreach (var date in dates) HistoryDates.Add(date);
                Archives.Clear(); foreach (var archive in archives) Archives.Add(archive);
                SelectedSong = rows.FirstOrDefault(row => row.SelectionKey == target.Selected);
                locations[target.Key] = target;
                RefreshBindings();
            }
            finally { publishing = false; }
            _ = page.Dispatcher.InvokeAsync(() =>
            {
                if (!stopping && generation == loadGeneration) page.RestoreScroll(target.Scroll);
            }, DispatcherPriority.Loaded);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            if (publishStatus && !stopping && generation == loadGeneration && stamp == statusEpoch)
            { status = ErrorText(error); Changed(nameof(Status)); }
            throw;
        }
        finally
        {
            if (ReferenceEquals(loadCancellation, request))
            { loadCancellation = null; if (!disposed) RefreshBindings(); }
        }
    }
    private async Task<(PlaylistRow[] Rows, long Revision)> FetchPlaylists(CancellationToken token)
    {
        const int maximumPages = 100;
        const int maximumPlaylists = 10_000;
        var rows = new List<PlaylistRow>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        long revision = -1;
        int? expectedCount = null;
        for (var pageNumber = 0; ; pageNumber++)
        {
            MusicDto.Require(pageNumber < maximumPages);
            var reply = await Call("music.library.list", new { offset = rows.Count, limit = 100 }, token);
            var nextRevision = MusicDto.Integer(reply, "revision");
            if (revision >= 0 && revision != nextRevision) throw new MusicOperationException("REVISION_CONFLICT");
            revision = nextRevision;
            var count = MusicDto.Count(reply, "total");
            MusicDto.Require(count <= maximumPlaylists && (expectedCount is null || expectedCount == count));
            expectedCount = count;
            var page = MusicDto.Page(reply, "items", rows.Count).Select(PlaylistRow.Read).ToArray();
            MusicDto.Require(rows.Count + page.Length <= maximumPlaylists &&
                page.All(row => ids.Add(row.Id)));
            rows.AddRange(page);
            if (rows.Count >= count) break;
        }
        return (rows.ToArray(), revision);
    }
    private async Task RefreshPlaylists(CancellationToken token)
    {
        if (stopping) return;
        var serial = ++libraryRead;
        var (rows, revision) = await FetchPlaylists(token);
        if (!stopping && serial == libraryRead) PublishPlaylists(rows, revision);
    }
    private void PublishPlaylists(PlaylistRow[] rows, long revision)
    {
        if (revision < libraryRevision) return;
        libraryRevision = revision;
        Playlists.Clear(); foreach (var row in rows) Playlists.Add(row);
        RebuildNavigation(); Changed(nameof(CountText));
        _ = RefreshCurrentFavoriteAsync();
    }
    private void RebuildNavigation()
    {
        NavigationItem[] items = [
            new("catalog-home", "推荐", "\uE80F"),
            new("catalog-channels", "频道", "\uE8A9"),
            new("favorites", "我的收藏", "\uEB51", "我的"),
            ..Playlists.Where(item => item.Kind == "playlist").Select(item =>
                new NavigationItem("playlist/" + item.Id, item.Name, "\uE8D6", "我的")),
            // Declared routes remain reachable from content/settings and public-page handoff.
            new("catalog-discovery", "搜索发现", "\uE721", ShowInSidebar: false),
            new("catalog-personal", "平台歌单", "\uE8D6", ShowInSidebar: false),
            new("catalog-daily", "每日推荐", "\uE734", ShowInSidebar: false),
            new("catalog-fm", "私人 FM", "\uE93E", ShowInSidebar: false),
            new("history", "最近播放", "\uE81C", ShowInSidebar: false),
            new("archives", "已播队列", "\uE8D6", ShowInSidebar: false)
        ];
        if (Navigation.SequenceEqual(items)) return;
        Navigation.Clear(); foreach (var item in items) Navigation.Add(item);
    }
}
