using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.UI.Dispatching;

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

    internal void PublishContentSelection((string Route, string SearchView, ObservableCollection<MusicCatalogSection> Sections) previous)
    {
        bool sectionsChanged = !ReferenceEquals(previous.Sections, Catalog.CatalogSections);
        bool routeChanged = previous.Route != ctx.CurrentRoute || (Search.IsSearchPage && previous.SearchView != Search.SearchView);
        if (!sectionsChanged && !routeChanged) return;
        Catalog.NotifyRouteChanged(sectionsChanged);
        if (!routeChanged) return;
        Search.NotifyRouteChanged();
        Library.NotifyRouteChanged();
        Discovery.NotifyRouteChanged();
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
            catch { }
            try { await Settings.ReadSearchPreferences(token); }
            catch (OperationCanceledException) { throw; }
            catch { }
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
        if (ctx.Stopping || submission != ctx.SearchSubmission || pageGeneration != ctx.LoadGeneration) return;
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
        if (Discovery.IsDiscovery || Discovery.HasActiveRequest) await Discovery.RetireDiscoveryPageAsync();
        token.ThrowIfCancellationRequested();
        if (ctx.Stopping || submission != ctx.SearchSubmission) return;
        await Settings.RecordSearchHistory(query, true, token);
        token.ThrowIfCancellationRequested();
        if (ctx.Stopping || submission != ctx.SearchSubmission) return;
        if (Search.HasSearchSession && Search.SearchKeyword == query && Search.SearchAccountEpoch == Accounts.AccountEpoch)
        {
            await Search.RestoreSearchAsync(token);
            await Search.OpenSearchTabAsync(MusicSearchController.SearchOverview);
            return;
        }
        await Search.BeginSearchAsync(query, token, overview: true);
    }

    internal async Task LoadUiAsync(MusicLocation target)
    {
        if (ctx.Stopping) return;
        try { await LoadAsync(target, ctx.Lifetime.Token); }
        catch (OperationCanceledException) { }
        catch { }
    }

    internal Task LoadAsync(MusicLocation target, CancellationToken cancellationToken, int retry = 0,
        bool publishStatus = true)
    {
        if (ctx.Stopping) return Task.CompletedTask;
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.LoadDrains.Add(drained.Task);
        return Tracked();
        async Task Tracked()
        {
            try { await LoadCoreAsync(target, cancellationToken, retry, publishStatus); }
            finally { ctx.LoadDrains.Remove(drained.Task); drained.TrySetResult(); }
        }
    }

    private async Task LoadCoreAsync(MusicLocation target, CancellationToken cancellationToken, int retry,
        bool publishStatus)
    {
        var generation = AdvanceLoadGeneration();
        var stamp = publishStatus ? ctx.NextStatusEpoch() : ctx.StatusEpoch;
        Library.CancelCollectionPage();
        ctx.LoadCancellation?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ctx.Lifetime.Token);
        ctx.LoadCancellation = request;
        var token = request.Token;
        try
        {
            if (publishStatus) ctx.ClearStatusQuiet();
            ctx.RefreshBindings();
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
                    var reply = await ctx.Call("music.library.search", new { query = target.Query, offset = target.Offset, limit = 100 }, token);
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
                        var reply = await ctx.Call("music.history.get", new { kind = "days", id = target.Date, offset = target.Offset, limit = 100 }, token);
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
                    var reply = await ctx.Call("music.library.detail", new { id, offset = 0, limit = 100 }, token);
                    detail = PlaylistRow.Read(reply.GetProperty("collection"));
                    MusicDto.Require(detail.Id == id);
                    title = detail.Name; count = MusicDto.Count(reply, "total");
                    rows = MusicDto.Page(reply, "songs", 0).Select(item => SongRow.Read(item)).ToArray();
                    break;
                }
            }
            token.ThrowIfCancellationRequested();
            if (ctx.Stopping || generation != ctx.LoadGeneration) return;
            if (detail is null && target.Offset > 0 && target.Offset >= count)
            {
                if (retry >= 2) throw new MusicOperationException("REVISION_CONFLICT");
                await LoadAsync(target with { Offset = count == 0 ? 0 : (count - 1) / 100 * 100 },
                    cancellationToken, retry + 1, publishStatus);
                return;
            }
            ctx.Publishing = true;
            try
            {
                var previousSelection = CaptureContentSelection();
                ctx.CurrentLocation = target;
                Library.ApplyLoadedLocation(detail, title, count, rows, dates, archives, target.Selected);
                ctx.Locations[target.Key] = target;
                PublishContentSelection(previousSelection);
                ctx.RefreshBindings();
            }
            finally { ctx.Publishing = false; }
            _ = Presentation.Dispatcher.EnqueueAsync(() =>
            {
                if (!ctx.Stopping && generation == ctx.LoadGeneration) Presentation.RestoreScroll(target.Scroll);
            }, DispatcherQueuePriority.Low);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            if (publishStatus && !ctx.Stopping && generation == ctx.LoadGeneration && stamp == ctx.StatusEpoch)
                ctx.SetStatus(MusicRuntimeContext.ErrorText(error));
            throw;
        }
        finally
        {
            if (ReferenceEquals(ctx.LoadCancellation, request))
            { ctx.LoadCancellation = null; if (!ctx.Disposed) ctx.RefreshBindings(); }
        }
    }

    private async Task<JsonElement[]> ReadHistoryAsync(string kind, CancellationToken token)
    {
        var rows = new List<JsonElement>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var reply = await ctx.Call("music.history.get", new { kind, offset = rows.Count, limit = 100 }, token);
            var page = MusicDto.Page(reply, "items", rows.Count);
            MusicDto.Require(page.All(row => ids.Add(MusicDto.Text(row, "id"))));
            rows.AddRange(page);
            if (rows.Count >= MusicDto.Count(reply, "total")) return rows.ToArray();
        }
    }
}
