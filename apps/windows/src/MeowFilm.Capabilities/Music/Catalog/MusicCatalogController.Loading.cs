using System.Collections.ObjectModel;
using System.Text.Json;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCatalogController
{
    private async Task LoadCatalogAsync(string route, string query, int pageNumber, JsonElement? continuation,
        CancellationToken token, bool refresh = false, string tag = "")
    {
        if (stopping || IsUnsupportedCatalogRoute(route)) return;
        token.ThrowIfCancellationRequested();
        if (MusicCatalogDispatcher.IsCatalogSourceRoute(route) && !settings.CatalogSourceLoaded)
        {
            var submission = ctx.NavigationSubmission;
            try { await settings.EnsureCatalogSourceSettingsAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch
            {
                if (!stopping && submission == ctx.NavigationSubmission) PublishCatalogSourceUnavailable(route);
                return;
            }
            if (stopping || submission != ctx.NavigationSubmission) return;
        }
        var request = CaptureCatalogLoadRequest(route, query, pageNumber, continuation, refresh, tag);
        if (catalogLoadFlight is { } current && !current.Work.IsCompleted &&
            current.Generation == ctx.LoadGeneration && !current.Cancellation.IsCancellationRequested &&
            current.Request.Key == request.Key)
        {
            await SubscribeCatalogLoad(current, token);
            return;
        }

        if (route != "catalog-home" || !IsCatalogHome || catalogLoadedPlatform != request.Key.Platform)
            catalogActions.Invalidate();
        var generation = ctx.AdvanceLoadGeneration();
        var stamp = ctx.NextStatusEpoch();
        ctx.LoadCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var flight = new CatalogLoadFlight(request, generation, stamp, cancellation);
        ctx.LoadCancellation = cancellation;
        catalogLoadFlight = flight;
        ctx.ClearStatusQuiet();
        var previousSelection = ctx.CaptureContentSelection();
        if ((ctx.CurrentRoute != route || catalogLoadedPlatform != request.Key.Platform) &&
            !(IsCatalogHome && request.Target?.Private == true && request.Target.Operation is not ("daily" or "playlist")))
        {
            RetireDailyStyleEditor();
            ctx.SetLocation(new(route),
                heading: request.Target?.Title ?? (route.StartsWith("catalog-playlist/", StringComparison.Ordinal) ? "歌单"
                    : route is "catalog-daily" || route.StartsWith("catalog-daily/", StringComparison.Ordinal) ? "每日推荐" : "音乐内容"),
                clearCatalogSongs: true, clearSelection: true, clearTotal: true);
            if (request.Target?.Operation == "daily") CatalogDailyBranch = request.Target.Params.Branch ?? "";
            if (IsChannelLanding) CatalogTagGroups = [];
            else if (!IsCatalogHome) utilitySections.Clear();
            CatalogTags.Clear();
            catalogPages.Clear(); catalogCursors.Clear(); catalogCursors.Add(null);
            catalogLoadedPlatform = request.Key.Platform; catalogLoadedTarget = request.Target;
            catalogPage = 1; catalogMore = false; catalogContinuation = null;
            catalogTag = catalogQuery = "";
            catalogArtwork = request.Target?.Artwork ?? "";
            catalogSubtitle = request.Target?.Subtitle ?? "";
            catalogSummaryCount = request.Target?.Count; catalogMembersLoaded = false;
        }
        if (IsChannelLanding && ctx.CurrentRoute == route) SelectChannelPage(route, request.Key.Platform);
        ctx.PublishContentSelection(previousSelection); ctx.RefreshBindings();
        flight.Work = RunCatalogFlightAsync(flight);
        loadDrains.Add(flight.Work);
        _ = ObserveCatalogLoadAsync(flight);
        await SubscribeCatalogLoad(flight, token);
    }

    private CatalogLoadRequest CaptureCatalogLoadRequest(string route, string query, int number,
        JsonElement? continuation, bool refresh, string tag)
    {
        var platform = MusicCatalogDispatcher.IsCatalogSourceRoute(route) ? settings.CatalogSourcePlatform : SelectedCatalogPlatform.Key;
        var kind = contentState.SearchKindLabel;
        var routeParts = route.Split('/', 3);
        if (routeParts.Length == 3) platform = routeParts[1];
        var target = CatalogRouteTarget(route, platform);
        if (target is not null) platform = target.Platform;
        var cursor = continuation?.Clone();
        var key = new CatalogLoadKey(route, query, number, cursor?.GetRawText() ?? "", refresh, tag,
            platform, kind, catalogPageCode, accounts.AccountEpoch, accounts.PlatformObservation(platform),
            target?.Operation ?? "", target?.Platform ?? "", target?.Title ?? "",
            target?.Params.CanonicalKey() ?? "");
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
                        ctx.RefreshBindings();
                }
            }
        }
    }

    private async Task ObserveCatalogLoadAsync(CatalogLoadFlight flight)
    {
        try { await flight.Work; }
        catch { }
        finally { loadDrains.Remove(flight.Work); }
    }

    private async Task RunCatalogFlightAsync(CatalogLoadFlight flight)
    {
        await Task.Yield();
        try { await LoadCatalogCoreAsync(flight); }
        finally
        {
            if (ReferenceEquals(catalogLoadFlight, flight)) catalogLoadFlight = null;
            if (ReferenceEquals(ctx.LoadCancellation, flight.Cancellation))
            {
                ctx.LoadCancellation = null;
                if (!disposed) ctx.RefreshBindings();
            }
            flight.Cancellation.Dispose();
            if (!stopping && flight.Published && flight.Generation == ctx.LoadGeneration) StartHomeContinuation();
        }
    }

    private async Task ReadCatalogAccountForRequestAsync(long generation, CancellationToken token)
    {
        long epoch = accounts.AccountEpoch;
        await accounts.EnsureAccountsForRequestAsync(token);
        token.ThrowIfCancellationRequested();
        if (stopping || generation != ctx.LoadGeneration || epoch != accounts.AccountEpoch) throw new OperationCanceledException(token);
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
            if (target?.Private == true) await ReadCatalogAccountForRequestAsync(generation, token);
            else if (platform is "wy" or "tx" or "kg") await accounts.EnsureAccountsForRequestAsync(token);
            token.ThrowIfCancellationRequested();
            if (stopping || generation != ctx.LoadGeneration || key.AccountObservation != accounts.PlatformObservation(platform)) return;
            var accountStamp = CatalogAccountKey(platform);
            string operation, title;
            string? routeId = null;
            if (target is not null && target.Operation != "playlist")
            {
                operation = target.Operation; title = target.Title;
            }
            else if (route.StartsWith("catalog-playlist/", StringComparison.Ordinal) || route.StartsWith("catalog-channel/", StringComparison.Ordinal))
            {
                var parts = route.Split('/', 3);
                platform = parts[1];
                routeId = Uri.UnescapeDataString(parts[2]);
                operation = parts[0] == "catalog-playlist" ? "playlist" : "channel";
                title = operation == "playlist" ? "歌单" : "频道";
            }
            else
            {
                (operation, title) = route switch
                {
                    "catalog-home" => ("home", "发现音乐"),
                    "catalog-channels" => ("channels", "音乐频道"),
                    _ => throw new MusicOperationException("INVALID_REQUEST")
                };
            }
            var reply = await MusicCatalogDispatcher.InvokeOperationAsync(
                application, operation, platform, target, routeId, tag, key.PageCode, number, continuation, refresh, token);
            token.ThrowIfCancellationRequested();
            if (stopping || generation != ctx.LoadGeneration || key.AccountObservation != accounts.PlatformObservation(platform)) return;
            if (accountStamp != CatalogAccountKey(platform)) throw new MusicOperationException("STALE_SESSION");
            var pageSections = MusicCatalogDto.Read(reply, platform, operation, title, kind == "歌单" ? "playlists" : "songs").ToArray();
            var sections = pageSections;
            bool more = MusicCatalogDto.Boolean(reply, "more", false);
            var c = MusicCatalogDto.Field(reply, "continuation");
            JsonElement? next = c.ValueKind == JsonValueKind.Object ? c.Clone() : null;
            bool continuationBlocked = more && (next is null ||
                catalogCursors.Take(number).Any(old => MusicCatalogDto.CursorKey(old) == MusicCatalogDto.CursorKey(next)));
            if (number > 1 && operation is "home" or "channel" or "playlist")
                sections = MusicCatalogDispatcher.MergeSections(CatalogSections, pageSections);
            var tags = target?.Operation == "daily" ? Array.Empty<MusicCatalogTag>() : MusicCatalogDto.Tags(reply).ToArray();
            var selected = MusicCatalogDto.Text(reply, "selectedTag");
            var name = MusicCatalogDto.Text(reply, operation is "home" or "channel" or "artistActivity" or "chart" or "charts" ? "title" : "name");
            var state = new CatalogPageState(sections, tags, selected, name.Length > 0 ? name : title,
                MusicCatalogDto.Text(reply, "cover"), more, next,
                MusicCatalogDto.Number(reply, "total") ?? sections.Sum(s => s.Items.Count),
                Scroll: number > 1 && operation is ("home" or "channel" or "playlist") ? ctx.CaptureScroll() : 0,
                Target: target, AccountStamp: accountStamp,
                ExpiresAt: MusicCatalogDispatcher.CatalogExpiry(reply),
                DailySelection: target?.Operation == "daily" ? MusicCatalogDispatcher.DailySelectionSnapshot(reply) : null)
                { ContinuationBlocked = continuationBlocked };
            if (number > 1 && catalogPages.TryGetValue(catalogPage, out var previousPage))
                state = state with { OptionalReads = previousPage.OptionalReads };
            if (operation == "playlist")
                optionalReads.RememberCollectionMetadata(platform, MusicCatalogDto.Text(reply, "id"), name, state.Artwork,
                    MusicCatalogDto.LongNumber(reply, "playCount"));
            if (operation == "channels")
            {
                ChannelTabs.Clear();
                foreach (var card in sections.SelectMany(s => s.Items).Where(c => c.Kind == "channel" && c.Display))
                    ChannelTabs.Add(card);
                UpdateChannelTabs();
                var first = VisibleChannelTabs.Concat(MoreChannelTabs).FirstOrDefault(c => c.CanOpen);
                if (first is not null)
                {
                    lastChannelRoute = "catalog-channel/" + first.Platform + "/" + Uri.EscapeDataString(first.ActionId);
                    catalogChannelCodes[lastChannelRoute] = first.PageCode;
                    catalogChannelKinds[lastChannelRoute] = first.ChannelKind;
                    catalogPageCode = first.PageCode;
                    var previousSelection = ctx.CaptureContentSelection();
                    ctx.SetLocation(new(lastChannelRoute));
                    catalogLoadedPlatform = first.Platform;
                    SelectChannelPage(lastChannelRoute, first.Platform);
                    ctx.PublishContentSelection(previousSelection); ctx.RefreshBindings();
                    var channelKey = key with { Route = lastChannelRoute, Platform = first.Platform, PageCode = first.PageCode };
                    await LoadCatalogCoreAsync(new CatalogLoadFlight(new(channelKey, null, null), generation, stamp, flight.Cancellation));
                    return;
                }
            }
            if (operation == "channels") state = state with { Sections = [], Total = 0 };
            if (!await PublishCatalogAsync(route, query, platform, kind, number, state,
                append: operation == "home" && number > 1, token: token,
                isCurrent: () => generation == ctx.LoadGeneration && key.AccountObservation == accounts.PlatformObservation(platform),
                beforePublish: () =>
                {
                    if (number == 1) { catalogPages.Clear(); catalogCursors.Clear(); catalogCursors.Add(null); }
                    else if (operation is "playlist" or "home") catalogPages.Clear();
                    while (catalogCursors.Count <= number) catalogCursors.Add(null);
                    catalogCursors[number] = next; catalogPages[number] = state;
                })) return;
            if (operation == "home") SaveHomeContent(platform, number, pageSections);
            flight.Published = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!stopping && generation == ctx.LoadGeneration && stamp == ctx.StatusEpoch &&
                key.AccountObservation == accounts.PlatformObservation(platform))
            {
                if (!IsCatalogHome && error is MusicOperationException { Code: "AUTH_REQUIRED" or "AUTH_EXPIRED" or "STALE_SESSION" or "SESSION_CHANGED" })
                    RetireCatalogSession();
                var message = MusicCatalogDto.ErrorText(error is MusicOperationException failure ? failure.Code : "INVALID_RESPONSE");
                ctx.SetStatusQuiet(message);
                if (route == "catalog-channels" || route == lastChannelRoute && channelSections.Count == 0)
                    channelEntryError = message;
                if (error is MusicOperationException { Code: "PAGINATION_STALLED" })
                {
                    catalogMore = false;
                    if (catalogPages.TryGetValue(catalogPage, out var prior))
                        catalogPages[catalogPage] = prior with { More = false };
                }
                ctx.Publishing = true;
                try
                {
                    SelectedCatalogPlatform = CatalogPlatforms.FirstOrDefault(p => p.Key == catalogLoadedPlatform) ?? SelectedCatalogPlatform;
                    contentState.SearchKindLabel = catalogLoadedKind;
                    if (catalogPages.TryGetValue(catalogPage, out var previous)) PublishPersonalCatalog(previous);
                    ctx.NotifyStatusChanged(); CatalogBindings();
                }
                finally { ctx.Publishing = false; }
                if (error is MusicOperationException { Code: "AUTH_REQUIRED" or "AUTH_EXPIRED" })
                {
                    PublishCatalogAccountNotice(((MusicOperationException)error).Code);
                    RecoverHomeAfterAuthenticationFailure();
                }
            }
        }
    }

    private static void ReconcileSections(ObservableCollection<MusicCatalogSection> target, MusicCatalogSection[] incoming)
    {
        for (int index = 0; index < incoming.Length; index++)
        {
            if (index == target.Count) target.Add(incoming[index]);
            else if (!ReferenceEquals(target[index], incoming[index])) target[index] = incoming[index];
        }
        while (target.Count > incoming.Length) target.RemoveAt(target.Count - 1);
    }

    private async Task<bool> PublishCatalogAsync(string route, string query, string platform, string kind, int number, CatalogPageState state,
        bool append = false, bool initializeEntries = true, CancellationToken token = default,
        Func<bool>? isCurrent = null, Action? beforePublish = null, bool restore = false)
    {
        long generation = ctx.LoadGeneration, submission = ctx.NavigationSubmission;
        long observation = accounts.PlatformObservation(platform);
        string account = CatalogAccountKey(platform);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loadDrains.Add(drained.Task);
        bool Current() => !stopping && generation == ctx.LoadGeneration && submission == ctx.NavigationSubmission &&
            observation == accounts.PlatformObservation(platform) && account == CatalogAccountKey(platform) &&
            isCurrent?.Invoke() != false;
        try
        {
            if (!restore) await RefreshCatalogSongsAsync(MusicCatalogSection.RepositoryCards(state.Sections), token);
            token.ThrowIfCancellationRequested();
            if (!Current()) return false;
            beforePublish?.Invoke();
            Publish();
            return true;
        }
        catch (MusicOperationException error) when (error.Code is "STORAGE_UNAVAILABLE" or "CORRUPT_STORAGE" or "STORAGE")
        {
            if (Current()) { ctx.SetStatus(MusicCatalogDto.ErrorText(error.Code)); CatalogBindings(); }
            return false;
        }
        finally { loadDrains.Remove(drained.Task); drained.TrySetResult(); }

        void Publish()
        {
            ctx.Publishing = true;
            try
            {
                var previousSelection = ctx.CaptureContentSelection();
                ctx.SetLocation(new(route), heading: state.Title, clearCollection: true);
                if (route.StartsWith("catalog-channel/", StringComparison.Ordinal))
                { lastChannelRoute = route; channelEntryError = ""; }
                catalogQuery = query; catalogLoadedPlatform = platform; catalogLoadedKind = kind; contentState.SearchKindLabel = kind;
                catalogPage = number; catalogMore = state.More && !state.ContinuationBlocked; catalogContinuation = state.Next;
                catalogTag = state.Tag;
                catalogArtwork = state.Artwork.Length > 0 ? state.Artwork : state.Target?.Artwork ?? "";
                catalogSubtitle = state.Target?.Subtitle ?? "";
                catalogSummaryCount = state.Total; catalogMembersLoaded = true;
                PublishPersonalCatalog(state);
                if (state.DailySelection is { } daily) PublishDailyStyleSelection(daily);
                SelectedCatalogPlatform = CatalogPlatforms.Single(p => p.Key == platform);
                if (IsChannelLanding) SelectChannelPage(route, platform);
                var sectionsToPublish = state.Sections;
                if (!IsCatalogHome)
                {
                    var songs = state.Sections.SelectMany(s => s.Items).Select(i => i.Song).OfType<SongRow>().ToArray();
                    MusicRuntimeContext.ReconcileSongRows(ctx.ActiveSongs, songs);
                    if (songs.Length > 0 && songs.Length == ctx.ActiveSongs.Count)
                    {
                        int songIndex = 0;
                        bool anyReplaced = false;
                        var aligned = new MusicCatalogSection[state.Sections.Length];
                        for (int s = 0; s < state.Sections.Length; s++)
                        {
                            var section = state.Sections[s];
                            MusicCatalogCard[]? items = null;
                            for (int i = 0; i < section.Items.Count; i++)
                            {
                                var item = section.Items[i];
                                if (item.Song is null) continue;
                                var reconciled = ctx.ActiveSongs[songIndex++];
                                if (!ReferenceEquals(item.Song, reconciled))
                                {
                                    items ??= section.Items.ToArray();
                                    items[i] = item with { Song = reconciled };
                                    anyReplaced = true;
                                }
                            }
                            aligned[s] = items is not null ? section with { Items = items } : section;
                        }
                        if (anyReplaced)
                        {
                            sectionsToPublish = aligned;
                            if (catalogPages.TryGetValue(number, out var stored) && ReferenceEquals(stored, state))
                                catalogPages[number] = state = state with { Sections = aligned };
                        }
                    }
                }
                if (!CatalogSections.SequenceEqual(sectionsToPublish, ReferenceEqualityComparer.Instance))
                    ReconcileSections(CatalogSections, sectionsToPublish);
                if (IsCatalogHome)
                {
                    publishedHomeState = state; homePage = number; homePlatform = platform;
                    RefreshHomeDisplay();
                }
                if (!CatalogTags.SequenceEqual(state.Tags))
                {
                    CatalogTags.Clear(); foreach (var tag in state.Tags) CatalogTags.Add(tag);
                    if (IsChannelLanding)
                        CatalogTagGroups = channelTags.GroupBy(tag => tag.Group, StringComparer.Ordinal)
                            .Select(group => new MusicCatalogTagGroup(group.Key, group.ToArray())).ToArray();
                }
                contentState.PublishCatalogSummary(state.Total, !append);
                var visibleCount = IsCatalogHome
                    ? homeDisplaySections.Sum(section => section.Items.Count)
                    : state.Sections.Sum(section => section.Items.Count);
                if (!append) ctx.SetStatusQuiet(visibleCount == 0 ? "暂无内容" : "");
                if (state.ContinuationBlocked) ctx.SetStatusQuiet(MusicCatalogDto.ErrorText("PAGINATION_STALLED"));
                ctx.PublishContentSelection(previousSelection);
                contentState.NotifySelectionChanged(); ctx.RefreshBindings();
            }
            finally { ctx.Publishing = false; }
            state.OptionalReads = optionalReads.Activate(route, platform, state.OptionalReads, initializeEntries);
            if (append) return;
            _ = ctx.Dispatcher.EnqueueAsync(() => { if (!stopping && generation == ctx.LoadGeneration) ctx.RestoreScroll(state.Scroll); }, DispatcherQueuePriority.Low);
        }
    }
}
