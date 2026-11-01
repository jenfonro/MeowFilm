using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Threading;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    private static readonly string[] DirectChannelIds =
        ["feature", "playlist", "chart", "artist", "vip", "euro", "djR", "huayu", "qingge", "acg"];
    private static readonly HashSet<string> DirectChannelIdSet = new(DirectChannelIds, StringComparer.Ordinal);
    private static readonly string[] MoreChannelIds =
    [
        "china", "yingshi", "rock", "music_top_tab_jpop", "paobu", "music_top_tab_game",
        "yueyu", "music_top_tab_sunoai", "old_hat", "music_top_tab_dance", "hFlow", "music_top_tab_world",
        "music_top_tab_dj", "rnb", "chuxing", "music_tab_drive", "music_top_tab_tipsy", "rap",
        "xiangcun", "music_top_tab_french", "sleep", "music_top_tab_light_music", "classic", "music_top_tab_children",
        "HiFi", "sir", "ballad", "music_top_tab_latin", "music_tab_pioneer", "funk",
        "landiao", "yueqi", "study", "yinyueju", "music_top_tab_sport"
    ];
    private static readonly HashSet<string> MoreChannelIdSet = new(MoreChannelIds, StringComparer.Ordinal);
    private static readonly string[] ChannelOrderIds = [.. DirectChannelIds, .. MoreChannelIds];
    public MusicCatalogPlatform[] CatalogPlatforms { get; } =
        [new("wy", "网易云音乐"), new("tx", "QQ 音乐"), new("kg", "酷狗音乐"),
         new("kw", "酷我音乐"), new("mg", "咪咕音乐")];
    public MusicCatalogPlatform[] CatalogPlatformChoices => CatalogPlatforms
        .Where(p => p.Key is "wy" or "tx" or "kg" || p.Key == catalogLoadedPlatform).ToArray();
    public MusicCatalogPlatform SelectedCatalogPlatform { get; set; } = new("wy", "网易云音乐");
    public string[] CatalogSearchKinds { get; } = ["歌曲", "歌单"];
    public string CatalogSearchKind { get; set; } = "歌曲";
    public ObservableCollection<MusicCatalogSection> CatalogSections { get; } = [];
    private readonly ObservableCollection<MusicCatalogSection> homeDisplaySections = [];
    public ObservableCollection<MusicCatalogSection> DisplayCatalogSections => IsCatalogHome ? homeDisplaySections : CatalogSections;
    public bool IsCatalogHome => location.Route == "catalog-home";
    public ObservableCollection<MusicCatalogCard> ChannelTabs { get; } = [];
    public bool IsChannelDirectory => location.Route == "catalog-channels";
    public bool IsVipChannel => location.Route == "catalog-channel/wy/vip";
    public string CurrentChannelKey => location.Route.StartsWith("catalog-channel/", StringComparison.Ordinal) ? location.Route[16..] : "";
    public IEnumerable<MusicCatalogCard> VisibleChannelTabs =>
        ChannelTabs.Where(card => DirectChannelIdSet.Contains(card.ActionId));
    public IEnumerable<MusicCatalogCard> MoreChannelTabs =>
        ChannelTabs.Where(card => MoreChannelIdSet.Contains(card.ActionId));
    public bool HasMoreChannelTabs => ChannelTabs.Any(card => MoreChannelIdSet.Contains(card.ActionId));
    public bool HasChannelTabs => IsChannelLanding && ChannelTabs.Count > 0;
    public bool IsCatalogUtilityPage => IsCatalogBrowsePage && !IsCatalogLanding && !IsCatalogDetailPage;
    public bool IsCatalogLanding => location.Route is "catalog-home" or "catalog-channels" || IsChannelLanding;
    public bool IsChannelLanding => location.Route == "catalog-channels" || location.Route.StartsWith("catalog-channel/", StringComparison.Ordinal);
    public ObservableCollection<MusicCatalogTag> CatalogTags { get; } = [];
    public IReadOnlyList<MusicCatalogTagGroup> CatalogTagGroups => CatalogTags
        .GroupBy(tag => tag.Group, StringComparer.Ordinal)
        .Select(group => new MusicCatalogTagGroup(group.Key, group.ToArray())).ToArray();
    public MusicCatalogTag? SelectedCatalogTag => CatalogTags.FirstOrDefault(tag => tag.Selected);
    public bool IsCatalogPage => location.Route.StartsWith("catalog-", StringComparison.Ordinal);
    public bool IsCatalogPlaylist => location.Route.StartsWith("catalog-playlist/", StringComparison.Ordinal);
    public bool IsCatalogArtist => location.Route.StartsWith("catalog-artist/", StringComparison.Ordinal);
    public bool IsCatalogDetailPage => IsCatalogPlaylist || IsCatalogArtist;
    public bool IsLocalLibraryPage => IsSongList || IsArchives;
    public bool CanCatalogBrowse => !stopping && commands == 0;
    public bool CanCatalogNext => CanCatalogBrowse && loadCancellation is null && catalogMore &&
        catalogPage < (catalogLoadedTarget?.MaximumPages ?? int.MaxValue);
    public bool CanCatalogPrevious => CanCatalogBrowse && loadCancellation is null && !IsCatalogDetailPage && catalogPage > 1;
    public bool CanLoadMoreCatalogPlaylist => IsCatalogPlaylist && CanCatalogNext;
    public bool HasCatalogTags => CatalogTags.Count > 0 && location.Route.StartsWith("catalog-channel/", StringComparison.Ordinal);
    public bool CanCatalogAction => CanCatalogBrowse && loadCancellation is null && IsCatalogCollection;
    public bool CanPlayCatalogPlaylist => CanCatalogAction && total > 0;
    public bool CanCancelCatalog => loadCancellation is not null || catalogActionCancellation is not null;
    public string CatalogPageText => IsCatalogPage ? $"第 {catalogPage} 页" : "";
    private bool catalogMore;
    private int catalogPage = 1;
    private JsonElement? catalogContinuation;
    private readonly List<JsonElement?> catalogCursors = [null];
    private string catalogQuery = "", catalogLoadedPlatform = "", catalogSnapshot = "", catalogTag = "", catalogPageCode = "",
        catalogArtwork = "";
    private string catalogLoadedKind = "歌曲";
    private readonly Dictionary<string, CatalogLocation> catalogLocations = [];
    private readonly Dictionary<string, string> catalogChannelCodes = [];
    private readonly Dictionary<int, CatalogPageState> catalogPages = [];
    private sealed record CatalogPageState(MusicCatalogSection[] Sections, MusicCatalogTag[] Tags,
        string Tag, string Snapshot, string Title, string Artwork, bool More, JsonElement? Next, int Total, double Scroll = 0,
        CatalogTarget? Target = null, string AccountStamp = "", long? ExpiresAt = null);
    private sealed record CatalogLocation(string Route, string Platform, string Query, string Kind, string PageCode,
        int Page, Dictionary<int, CatalogPageState> Pages, JsonElement?[] Cursors);
    private sealed record CatalogLoadKey(string Route, string Query, int Number, string Continuation,
        bool Refresh, string Tag, string Platform, string Kind, string PageCode, string Snapshot, long AccountEpoch,
        string TargetOperation, string TargetPlatform, string TargetTitle, string TargetParameters);
    private sealed record CatalogLoadRequest(CatalogLoadKey Key, JsonElement? Continuation, CatalogTarget? Target);
    private sealed class CatalogLoadFlight(CatalogLoadRequest request, long generation, long statusStamp,
        CancellationTokenSource cancellation)
    {
        public CatalogLoadRequest Request { get; } = request;
        public long Generation { get; } = generation;
        public long StatusStamp { get; } = statusStamp;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Work { get; set; } = Task.CompletedTask;
        public int Subscribers;
    }
    private CatalogLoadFlight? catalogLoadFlight;

    public void CatalogBindings()
    {
        foreach (var name in new[] { nameof(DisplayCatalogSections), nameof(IsCatalogHome), nameof(IsCatalogPage), nameof(IsChannelDirectory), nameof(IsVipChannel), nameof(CurrentChannelKey), nameof(VisibleChannelTabs), nameof(MoreChannelTabs), nameof(HasMoreChannelTabs), nameof(IsCatalogLanding), nameof(IsCatalogUtilityPage), nameof(IsChannelLanding), nameof(HasChannelTabs), nameof(IsLocalLibraryPage), nameof(IsCatalogPlaylist), nameof(IsCatalogArtist), nameof(IsCatalogDetailPage),
            nameof(CanCatalogNext), nameof(CanCatalogPrevious), nameof(CanCatalogBrowse), nameof(HasCatalogTags), nameof(CatalogTagGroups),
            nameof(CanCatalogAction), nameof(CanPlayCatalogPlaylist), nameof(CanCancelCatalog), nameof(CatalogPageText), nameof(SelectedCatalogTag),
            nameof(SelectedCatalogPlatform), nameof(CatalogSearchKind), nameof(CatalogPlatformChoices),
            nameof(IsContentDetailPage), nameof(ShowsSongTable), nameof(DetailArtwork), nameof(DetailCountText),
            nameof(DetailSectionTitle), nameof(DetailArtworkName), nameof(CanPlayContentDetail),
            nameof(CanLoadMoreCatalogPlaylist) }) Changed(name);
        PersonalCatalogBindings();
        SearchBindings();
    }
    private void RememberCatalogLocation()
    {
        if (IsDiscovery) return;
        if (IsSearchPage) { RememberSearch(); return; }
        if (!IsCatalogPage || !catalogPages.TryGetValue(catalogPage, out var state)) return;
        catalogPages[catalogPage] = state with { Scroll = page.CaptureScroll() };
        var key = location.Route + "/" + catalogLoadedPlatform;
        catalogLocations.Remove(key);
        catalogLocations[key] = new(location.Route, catalogLoadedPlatform, catalogQuery, catalogLoadedKind,
            catalogPageCode, catalogPage, new(catalogPages), catalogCursors.ToArray());
        while (catalogLocations.Count > 8) catalogLocations.Remove(catalogLocations.Keys.First());
    }
    public Task ChangeCatalogPlatformAsync()
    {
        if (publishing || !CanCatalogBrowse || !IsCatalogPage ||
            SelectedCatalogPlatform.Key == catalogLoadedPlatform && loadCancellation is null) return Task.CompletedTask;
        RememberCatalogLocation();
        if (SelectedCatalogPlatform.Key != "tx") CatalogPersonalKind = "all";
        return LoadCatalogAsync(IsCatalogPersonal ? "catalog-personal" : "catalog-home", "", 1, null, lifetime.Token);
    }
    public Task CatalogSearchKindAsync() => IsSearchPage ? SwitchSearchKindAsync() :
        publishing || !CanCatalogBrowse || location.Route != "catalog-search" || catalogQuery.Length == 0 ||
        CatalogSearchKind == catalogLoadedKind && loadCancellation is null ? Task.CompletedTask :
        LoadCatalogAsync("catalog-search", catalogQuery, 1, null, lifetime.Token);
    public Task CatalogTagAsync(MusicCatalogTag tag)
    {
        if (publishing || !CanCatalogBrowse || tag is null || !CatalogTags.Contains(tag) ||
            !location.Route.StartsWith("catalog-channel/", StringComparison.Ordinal) ||
            tag.Id == catalogTag && loadCancellation is null) return Task.CompletedTask;
        return LoadCatalogAsync(location.Route, catalogQuery, 1, null, lifetime.Token, tag: tag.Id);
    }
    public Task RefreshCatalogAsync() => !IsCatalogPage || stopping ? Task.CompletedTask :
        IsSearchPage ? BeginSearchAsync(searchKeyword, lifetime.Token, refresh: true) :
        LoadCatalogAsync(location.Route, catalogQuery, 1, null, lifetime.Token, refresh: true, tag: catalogTag);
    public Task CatalogPageAsync(int direction)
    {
        if (direction is not (-1 or 1) || (direction > 0 ? !CanCatalogNext : !CanCatalogPrevious)) return Task.CompletedTask;
        RememberCatalogLocation();
        int next = catalogPage + direction;
        if (catalogPages.TryGetValue(next, out var cached) && RestoreCatalogPage(cached, catalogLoadedPlatform))
        {
            AdvanceLoadGeneration();
            InvalidateCatalogAction();
            PublishCatalog(location.Route, catalogQuery, catalogLoadedPlatform, catalogLoadedKind, next, cached);
            return Task.CompletedTask;
        }
        return LoadCatalogAsync(location.Route, catalogQuery, next, catalogContinuation, lifetime.Token, tag: catalogTag);
    }
    public Task LoadMoreCatalogPlaylistAsync() =>
        CanLoadMoreCatalogPlaylist ? CatalogPageAsync(1) : Task.CompletedTask;
    private bool ContainsCatalogCard(MusicCatalogCard card) =>
        HasChannelTabs && ChannelTabs.Any(item => ReferenceEquals(item, card)) || CatalogSections.Any(section =>
        ReferenceEquals(section.Action, card) || section.Items.Any(item => ReferenceEquals(item, card)));
    public Task OpenCatalogCardAsync(MusicCatalogCard card)
    {
        if (!CanCatalogBrowse || card is null || !card.CanOpen || !ContainsCatalogCard(card)) return Task.CompletedTask;
        var channelTab = HasChannelTabs && ChannelTabs.Any(item => ReferenceEquals(item, card));
        if (!channelTab)
        {
            try { RequireCatalogContent(); } catch (MusicOperationException e) { return CatalogBusinessError(e.Code); }
        }
        if (card.Song is not null)
        {
            if (IsCatalogPlaylist) return CatalogPlaylistActionAsync("play",
                Songs.IndexOf(card.Song), card.Song.Id,
                card.MemberId.Length > 0 ? card.MemberId : null);
            if (IsCatalogBatch) return CatalogBatchActionAsync("play", Songs.IndexOf(card.Song));
            try { RequireCatalogContent(); } catch (MusicOperationException e) { return CatalogBusinessError(e.Code); }
            SelectedSong = card.Song;
            return PlaySelectionAsync();
        }
        if (card.Kind == "song") return PlayCatalogSongAsync(card);
        if (card.Locator is not null || card.Kind is "daily" or "fm" or "similar" or "album")
            return OpenPersonalCatalogCardAsync(card);
        RememberCatalogLocation();
        var destination = "catalog-" + card.Kind + "/" + card.Platform + "/" + Uri.EscapeDataString(card.ActionId);
        catalogChannelCodes[destination] = card.PageCode;
        while (catalogChannelCodes.Count > 100) catalogChannelCodes.Remove(catalogChannelCodes.Keys.First());
        return RequestNavigationAsync(destination);
    }
    public Task FavoriteCatalogCardAsync(MusicCatalogCard card)
    {
        if (!CanEdit || card?.Song is null || !card.CanFavorite || !ContainsCatalogCard(card)) return Task.CompletedTask;
        try { RequireCatalogContent(); } catch (MusicOperationException e) { return CatalogBusinessError(e.Code); }
        SelectedSong = card.Song;
        return FavoriteAsync();
    }
    private Task CatalogNavigateAsync(string route, CancellationToken token)
    {
        if (route == "catalog-search") return RestoreSearchAsync(token);
        if (!route.Contains('/') && SelectedCatalogPlatform.Key is "kw" or "mg")
            SelectedCatalogPlatform = CatalogPlatforms[0];
        InvalidateCatalogAction();
        var platform = route.Split('/') is { Length: 3 } parts ? parts[1] : SelectedCatalogPlatform.Key;
        if (catalogLocations.TryGetValue(route + "/" + platform, out var saved) &&
            saved.Pages[saved.Page].Target?.Cacheable != false && RestoreCatalogPage(saved.Pages[saved.Page], platform))
        {
            token.ThrowIfCancellationRequested();
            RetireCatalogLoad();
            catalogPages.Clear(); foreach (var p in saved.Pages) catalogPages[p.Key] = p.Value;
            catalogCursors.Clear(); catalogCursors.AddRange(saved.Cursors);
            catalogPageCode = saved.PageCode;
            PublishCatalog(route, saved.Query, platform, saved.Kind, saved.Page, saved.Pages[saved.Page]);
            return Task.CompletedTask;
        }
        catalogPageCode = catalogChannelCodes.GetValueOrDefault(route) ?? "";
        return LoadCatalogAsync(route, "", 1, null, token);
    }
    public void CancelCatalog()
    {
        if (stopping) return;
        if (IsSearchPage) { _ = CancelSearchAsync(); return; }
        RetireCatalogLoad();
        InvalidateCatalogAction();
        status = "已取消"; RefreshBindings(); CatalogBindings();
    }
    public void RetireCatalogSession()
    {
        RetireSearchTracked();
        var wasPublishing = publishing; publishing = true;
        try
        {
            RetireCatalogLoad();
            InvalidateCatalogAction();
            catalogLocations.Clear(); catalogChannelCodes.Clear(); catalogPages.Clear(); catalogCursors.Clear(); catalogCursors.Add(null);
            catalogTargets.Clear(); catalogLoadedTarget = null; catalogAccountStamp = ""; catalogExpiresAt = null;
            catalogSnapshot = catalogTag = catalogPageCode = catalogQuery = ""; catalogContinuation = null; catalogMore = false; catalogPage = 1;
            CatalogTags.Clear(); CatalogSections.Clear(); homeDisplaySections.Clear();
            if (IsCatalogPage) { Songs.Clear(); selectedSong = null; total = 0; heading = "音乐内容"; Changed(nameof(SelectedSong)); }
            RefreshBindings(); CatalogBindings();
        }
        finally { publishing = wasPublishing; }
    }
    private void RetireCatalogLoad()
    {
        AdvanceLoadGeneration();
        var cancellation = loadCancellation;
        loadCancellation = null;
        catalogLoadFlight = null;
        CancelCatalogLoad(cancellation);
    }
    private bool RetireCatalogLoad(CatalogLoadFlight flight)
    {
        if (!ReferenceEquals(catalogLoadFlight, flight) || flight.Generation != loadGeneration)
        {
            CancelCatalogLoad(flight.Cancellation);
            return false;
        }
        AdvanceLoadGeneration();
        catalogLoadFlight = null;
        if (ReferenceEquals(loadCancellation, flight.Cancellation)) loadCancellation = null;
        CancelCatalogLoad(flight.Cancellation);
        return true;
    }
    private static void CancelCatalogLoad(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }
    private Task LoadCatalogAsync(string route, string query, int pageNumber, JsonElement? continuation,
        CancellationToken token, bool refresh = false, string tag = "")
    {
        if (stopping) return Task.CompletedTask;
        token.ThrowIfCancellationRequested();
        var request = CaptureCatalogLoadRequest(route, query, pageNumber, continuation, refresh, tag);
        if (catalogLoadFlight is { } current && !current.Work.IsCompleted &&
            current.Generation == loadGeneration && !current.Cancellation.IsCancellationRequested &&
            current.Request.Key == request.Key)
            return SubscribeCatalogLoad(current, token);

        InvalidateCatalogAction();
        var generation = AdvanceLoadGeneration();
        var stamp = ++statusEpoch;
        loadCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var flight = new CatalogLoadFlight(request, generation, stamp, cancellation);
        loadCancellation = cancellation;
        catalogLoadFlight = flight;
        status = "";
        RefreshBindings();
        CatalogBindings();
        flight.Work = RunCatalogFlightAsync(flight);
        loadDrains.Add(flight.Work);
        _ = ObserveCatalogLoadAsync(flight);
        return SubscribeCatalogLoad(flight, token);
    }

    private CatalogLoadRequest CaptureCatalogLoadRequest(string route, string query, int number,
        JsonElement? continuation, bool refresh, string tag)
    {
        var platform = SelectedCatalogPlatform.Key;
        var kind = CatalogSearchKind;
        var routeParts = route.Split('/', 3);
        if (routeParts.Length == 3) platform = routeParts[1];
        var target = CatalogRouteTarget(route, platform);
        if (target is not null) platform = target.Platform;
        var cursor = continuation?.Clone();
        var key = new CatalogLoadKey(route, query, number, cursor?.GetRawText() ?? "", refresh, tag,
            platform, kind, catalogPageCode, catalogSnapshot, accountEpoch,
            target?.Operation ?? "", target?.Platform ?? "", target?.Title ?? "",
            target?.Parameters.GetRawText() ?? "");
        return new(key, cursor, target);
    }

    private Task SubscribeCatalogLoad(CatalogLoadFlight flight, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref flight.Subscribers);
        return WaitAsync();

        async Task WaitAsync()
        {
            var callerCancelled = false;
            try
            {
                if (cancellationToken.CanBeCanceled)
                    await flight.Work.WaitAsync(cancellationToken);
                else
                    await flight.Work;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                callerCancelled = true;
                throw;
            }
            finally
            {
                if (Interlocked.Decrement(ref flight.Subscribers) == 0 &&
                    callerCancelled && !flight.Work.IsCompleted)
                {
                    if (RetireCatalogLoad(flight) && !disposed)
                    {
                        RefreshBindings();
                        CatalogBindings();
                    }
                }
            }
        }
    }

    private async Task ObserveCatalogLoadAsync(CatalogLoadFlight flight)
    {
        try { await flight.Work; }
        catch { /* The active subscriber observes unexpected owner failures. */ }
        finally { loadDrains.Remove(flight.Work); }
    }

    private async Task RunCatalogFlightAsync(CatalogLoadFlight flight)
    {
        await Task.Yield(); // Publish the unique request owner before any port can complete synchronously.
        try { await LoadCatalogCoreAsync(flight); }
        finally
        {
            if (ReferenceEquals(catalogLoadFlight, flight)) catalogLoadFlight = null;
            if (ReferenceEquals(loadCancellation, flight.Cancellation))
            {
                loadCancellation = null;
                if (!disposed) { RefreshBindings(); CatalogBindings(); }
            }
            flight.Cancellation.Dispose();
        }
    }

    private async Task LoadCatalogCoreAsync(CatalogLoadFlight flight)
    {
        var request = flight.Request;
        var key = request.Key;
        var generation = flight.Generation;
        var stamp = flight.StatusStamp;
        var token = flight.Cancellation.Token;
        var route = key.Route;
        var query = key.Query;
        var number = key.Number;
        var continuation = request.Continuation;
        var refresh = key.Refresh;
        var tag = key.Tag;
        var platform = key.Platform;
        var kind = key.Kind;
        var target = request.Target;
        try
        {
            MusicDto.Require(CatalogPlatforms.Any(item => item.Key == platform));
            MusicDto.Require(CatalogPlatforms.Any(item => item.Key == platform) && number >= 1 && number <= (target?.MaximumPages ?? int.MaxValue));
            if (target is not null && target.Operation is not ("personalPlaylists" or "playlist") && platform != "wy")
                throw new MusicOperationException("UNSUPPORTED");
            if (target?.Private == true) await ReadCatalogAccountForRequestAsync(platform, generation, token);
            token.ThrowIfCancellationRequested();
            if (stopping || generation != loadGeneration) return;
            var accountStamp = CatalogAccountKey(platform);
            string operation, title;
            var parameters = new Dictionary<string, object?> { ["platform"] = platform, ["refresh"] = refresh };
            if (target is not null && target.Operation != "playlist")
            {
                operation = target.Operation; title = target.Title;
                ApplyCatalogTarget(target, parameters, number, continuation, refresh);
            }
            else if (route.StartsWith("catalog-playlist/", StringComparison.Ordinal) || route.StartsWith("catalog-channel/", StringComparison.Ordinal))
            {
                var parts = route.Split('/', 3);
                MusicDto.Require(parts.Length == 3 && CatalogPlatforms.Any(item => item.Key == parts[1]));
                platform = parts[1]; parameters["platform"] = platform;
                parameters["id"] = Uri.UnescapeDataString(parts[2]);
                operation = parts[0] == "catalog-playlist" ? "playlist" : "channel";
                title = operation == "playlist" ? "歌单" : "频道";
                if (operation == "playlist")
                {
                    parameters["page"] = number; parameters["limit"] = target?.Limit ?? 100;
                    if (target is not null) ApplyCatalogTarget(target, parameters, number, continuation, refresh);
                    // Target parameters are cloned JsonElements; keep the route identity
                    // as the same string used by the response identity check below.
                    parameters["id"] = Uri.UnescapeDataString(parts[2]);
                    if (number > 1) { MusicDto.Require(key.Snapshot.Length > 0); parameters["snapshot"] = key.Snapshot; }
                }
                else
                {
                    parameters["tag"] = tag;
                    if (key.PageCode.Length > 0) parameters["pageCode"] = key.PageCode;
                    if (continuation is not null) parameters["continuation"] = continuation.Value;
                }
            }
            else
            {
                (operation, title) = route switch
                {
                    "catalog-home" => ("home", "发现音乐"),
                    "catalog-channels" => ("channels", "音乐频道"),
                    "catalog-search" => ("search", "平台搜索"),
                    _ => throw new MusicOperationException("INVALID_REQUEST")
                };
                if (operation == "search")
                {
                    parameters["keyword"] = query; parameters["kind"] = kind == "歌单" ? "playlists" : "songs";
                    parameters["page"] = number; parameters["limit"] = 30;
                }
                else if (operation == "home" && continuation is not null) parameters["continuation"] = continuation.Value;
            }
            var reply = await Call("music.catalog." + operation, parameters, token);
            token.ThrowIfCancellationRequested();
            if (stopping || generation != loadGeneration) return;
            if (accountStamp != CatalogAccountKey(platform)) throw new MusicOperationException("STALE_SESSION");
            var sections = MusicCatalogDto.Read(reply, platform, operation, title, kind == "歌单" ? "playlists" : "songs").ToArray();
            ValidatePersonalCatalog(reply, target, sections.Sum(section => section.Items.Count));
            bool more = reply.TryGetProperty("more", out var hasMore) && hasMore.ValueKind == JsonValueKind.True;
            JsonElement? next = reply.TryGetProperty("continuation", out var c) && c.ValueKind == JsonValueKind.Object ? c.Clone() : null;
            var snapshot = MusicCatalogDto.Text(reply, "snapshot");
            int pageLimit = operation == "playlist" ? target?.Limit ?? 100 : 30;
            if (operation is "playlist" or "search" or "personalPlaylists")
            {
                MusicDto.Require(MusicDto.Integer(reply, "page") == number && MusicDto.Integer(reply, "limit") == pageLimit &&
                    sections.Sum(s => s.Items.Count) <= pageLimit);
                if (more) MusicDto.Require(next is not null && MusicDto.Integer(next.Value, "page") == number + 1);
            }
            if (operation == "playlist")
            {
                MusicDto.Require(MusicCatalogDto.Text(reply, "id") == (string)parameters["id"]! &&
                    snapshot.Length == 64 && snapshot.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f'));
                if (number > 1 && snapshot != key.Snapshot) throw new MusicOperationException("SNAPSHOT_CHANGED");
                var members = sections.SelectMany(s => s.Items).ToArray();
                if (target?.Limit == 30) MusicDto.Require(members.All(c => c.MemberId.Length > 0));
                MusicDto.Require(members.Select(c => c.ContentKey).Distinct().Count() == members.Length);
            }
            if (operation == "channel" && reply.TryGetProperty("channel", out var descriptor))
                MusicDto.Require(MusicCatalogDto.Text(descriptor, "id") == (string)parameters["id"]!);
            if (more && (next is null || operation != "personalPlaylists" && sections.Sum(s => s.Items.Count) == 0 ||
                number > 1 && catalogCursors.Take(number).Any(old => MusicCatalogDto.CursorKey(old) == MusicCatalogDto.CursorKey(next))))
                throw new MusicOperationException("PAGINATION_STALLED");
            if (more && number == (target?.MaximumPages ?? int.MaxValue)) throw new MusicOperationException("PAGINATION_LIMIT");
            if (number > 1)
            {
                var oldKeys = catalogPages.Where(p => p.Key < number).SelectMany(p => p.Value.Sections)
                    .SelectMany(s => s.Items).Select(i => i.ContentKey).ToHashSet();
                if (operation == "playlist" && sections.SelectMany(s => s.Items).Any(i => oldKeys.Contains(i.ContentKey)))
                    throw new MusicOperationException("PAGINATION_STALLED");
                if (sections.SelectMany(s => s.Items).Any() && sections.SelectMany(s => s.Items).All(i => oldKeys.Contains(i.ContentKey)))
                    throw new MusicOperationException("PAGINATION_STALLED");
                if (operation is "home" or "channel" or "playlist")
                    sections = MergeCatalogSections(CatalogSections, sections);
            }
            var tags = MusicCatalogDto.Tags(reply).ToArray();
            var selected = reply.TryGetProperty("feed", out var feed) && feed.ValueKind == JsonValueKind.Object
                ? MusicCatalogDto.Text(feed, "selectedTag") : MusicCatalogDto.Text(reply, "selectedTag");
            if (tag.Length > 0 && selected != tag) throw new MusicOperationException("SNAPSHOT_CHANGED");
            var name = MusicCatalogDto.Text(reply, "name");
            if (name.Length == 0) name = MusicCatalogDto.Text(reply, "title");
            var state = new CatalogPageState(sections, tags, selected, snapshot, name.Length > 0 ? name : title,
                MusicCatalogDto.Text(reply, "cover"), more, next,
                reply.TryGetProperty("total", out var sum) && sum.ValueKind != JsonValueKind.Null
                    ? MusicDto.Count(reply, "total") : sections.Sum(s => s.Items.Count),
                Scroll: number > 1 && operation is ("home" or "channel" or "playlist") ? page.CaptureScroll() : 0,
                Target: target, AccountStamp: accountStamp, ExpiresAt: CatalogExpiry(reply));
            if (number == 1) { catalogPages.Clear(); catalogCursors.Clear(); catalogCursors.Add(null); }
            else if (operation == "playlist") catalogPages.Clear();
            while (catalogCursors.Count <= number) catalogCursors.Add(null);
            catalogCursors[number] = next; catalogPages[number] = state;
            PublishCatalog(route, query, platform, kind, number, state);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!stopping && generation == loadGeneration && stamp == statusEpoch)
            {
                if (error is MusicOperationException { Code: "AUTH_REQUIRED" or "AUTH_EXPIRED" or "FORBIDDEN" or "STALE_SESSION" or "SESSION_CHANGED" or "INVALID_CREDENTIALS" })
                    RetireCatalogSession();
                status = MusicCatalogDto.ErrorText(error is MusicOperationException failure ? failure.Code : "INVALID_RESPONSE");
                if (error is MusicOperationException { Code: "PAGINATION_STALLED" or "PAGINATION_LIMIT" })
                {
                    catalogMore = false;
                    if (catalogPages.TryGetValue(catalogPage, out var prior))
                        catalogPages[catalogPage] = prior with { More = false };
                }
                publishing = true;
                try
                {
                    SelectedCatalogPlatform = CatalogPlatforms.FirstOrDefault(p => p.Key == catalogLoadedPlatform) ?? SelectedCatalogPlatform;
                    CatalogSearchKind = catalogLoadedKind;
                    if (catalogPages.TryGetValue(catalogPage, out var previous)) PublishPersonalCatalog(previous);
                    Changed(nameof(Status)); CatalogBindings();
                }
                finally { publishing = false; }
            }
        }
    }
    private static MusicCatalogSection[] MergeCatalogSections(IEnumerable<MusicCatalogSection> existingSections, MusicCatalogSection[] incoming)
    {
        var result = existingSections.ToList();
        foreach (var section in incoming)
        {
            var index = result.FindIndex(s => s.Id == section.Id);
            if (index < 0) result.Add(section);
            else
            {
                var existing = result[index];
                var merged = section with { Items = existing.Items.Concat(section.Items).DistinctBy(i => i.Key).ToArray() };
                merged.HorizontalOffset = existing.HorizontalOffset;
                result[index] = merged;
            }
        }
        return result.ToArray();
    }
    private void PublishCatalog(string route, string query, string platform, string kind, int number, CatalogPageState state)
    {
        publishing = true;
        try
        {
            location = new(route); heading = state.Title;
            if (catalogLoadedPlatform != platform) ChannelTabs.Clear();
            if (route == "catalog-channels")
            {
                ChannelTabs.Clear();
                foreach (var card in state.Sections.SelectMany(s => s.Items).Where(c => c.Kind == "channel")
                    .Where(c => DirectChannelIdSet.Contains(c.ActionId) || MoreChannelIdSet.Contains(c.ActionId))
                    .OrderBy(c => Array.IndexOf(ChannelOrderIds, c.ActionId))) ChannelTabs.Add(card);
            }
            catalogQuery = query; catalogLoadedPlatform = platform; catalogLoadedKind = kind; CatalogSearchKind = kind;
            catalogPage = number; catalogMore = state.More; catalogContinuation = state.Next;
            catalogSnapshot = state.Snapshot; catalogTag = state.Tag; catalogArtwork = state.Artwork;
            PublishPersonalCatalog(state);
            SelectedCatalogPlatform = CatalogPlatforms.Single(p => p.Key == platform);
            CatalogSections.Clear(); foreach (var section in state.Sections) CatalogSections.Add(section);
            homeDisplaySections.Clear();
            foreach (var source in state.Sections)
            {
                var display = IsCatalogHome ? source.DisplayableCards() : source;
                if (display.Items.Count > 0) homeDisplaySections.Add(display);
            }
            CatalogTags.Clear(); foreach (var tag in state.Tags) CatalogTags.Add(tag);
            IEnumerable<MusicCatalogSection> songSections = IsCatalogHome ? homeDisplaySections : state.Sections;
            Songs.Clear(); foreach (var song in songSections.SelectMany(s => s.Items).Select(i => i.Song).OfType<SongRow>()) Songs.Add(song);
            selectedSong = null; collection = null; total = state.Total;
            var visibleCount = IsCatalogHome
                ? homeDisplaySections.Sum(section => section.Items.Count)
                : state.Sections.Sum(section => section.Items.Count);
            status = visibleCount == 0 ? "暂无内容" : "";
            Changed(nameof(SelectedSong)); RefreshBindings(); CatalogBindings();
        }
        finally { publishing = false; }
        var generation = loadGeneration;
        _ = page.Dispatcher.InvokeAsync(() => { if (!stopping && generation == loadGeneration) page.RestoreScroll(state.Scroll); }, DispatcherPriority.Loaded);
    }
}
