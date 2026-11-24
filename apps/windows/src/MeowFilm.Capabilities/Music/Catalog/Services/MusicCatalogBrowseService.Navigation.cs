namespace MeowFilm.Capabilities;

internal sealed partial class MusicCatalogBrowseService
{
    // Navigation & browsing operations
    internal Task ChangeCatalogPlatformAsync(MusicCatalogPlatform platform)
    {
        if (ctx.Publishing || !CanCatalogBrowse || !IsCatalogPage || platform == personal.SelectedCatalogPlatform ||
            !personal.PlatformChoicesFor(catalogLoadedPlatform).Contains(platform)) return Task.CompletedTask;
        RememberCatalogLocation();
        personal.SelectedCatalogPlatform = platform;
        if (platform.Key != "tx") personal.CatalogPersonalKind = "all";
        return LoadCatalogAsync(personal.IsCatalogPersonal ? "catalog-personal" : "catalog-home", "", 1, null, ctx.Lifetime.Token);
    }

    internal Task CatalogTagAsync(MusicCatalogTag tag)
    {
        if (ctx.Publishing || !CanCatalogBrowse || tag is null || !CatalogTags.Contains(tag) ||
            !ctx.CurrentRoute.StartsWith("catalog-channel/", StringComparison.Ordinal) ||
            tag.Id == catalogTag && ctx.LoadCancellation is null) return Task.CompletedTask;
        return LoadCatalogAsync(ctx.CurrentRoute, catalogQuery, 1, null, ctx.Lifetime.Token, tag: tag.Id);
    }

    internal Task RefreshCatalogAsync()
    {
        if (!IsCatalogPage || ctx.Stopping || personal.IsCatalogDaily && personal.DailyStyleBusy || personal.IsUnsupportedCatalogRoute(ctx.CurrentRoute))
            return Task.CompletedTask;
        ctx.RetireOperationStatus();
        if (personal.IsCatalogDaily) personal.RetireDailyStyleEditor();
        return search.IsSearchPage ? search.BeginSearchAsync(search.SearchKeyword, ctx.Lifetime.Token, refresh: true) :
            LoadCatalogAsync(ctx.CurrentRoute, catalogQuery, 1, null, ctx.Lifetime.Token, refresh: true, tag: catalogTag);
    }

    internal async Task CatalogPageAsync(int direction)
    {
        if (direction is not (-1 or 1) || (direction > 0 ? !CanCatalogNext : !CanCatalogPrevious)) return;
        RememberCatalogLocation();
        int next = catalogPage + direction;
        if (catalogPages.TryGetValue(next, out var cached) && personal.RestoreCatalogPage(cached, catalogLoadedPlatform))
        {
            ctx.AdvanceLoadGeneration();
            catalogActions.Invalidate();
            await PublishCatalogAsync(ctx.CurrentRoute, catalogQuery, catalogLoadedPlatform, catalogLoadedKind, next, cached,
                token: ctx.Lifetime.Token, restore: true);
            return;
        }
        await LoadCatalogAsync(ctx.CurrentRoute, catalogQuery, next, catalogContinuation, ctx.Lifetime.Token, tag: catalogTag);
    }

    internal void SelectCatalogSectionTab(MusicCatalogTab tab)
    {
        var section = CatalogSections.FirstOrDefault(section => section.Tabs?.Contains(tab) == true);
        if (section is null) return;
        section.SelectTab(tab);
        if (IsCatalogHome) RefreshHomeDisplay();
        else optionalReads.RefreshDemandMembers();
    }

    internal async Task CatalogNavigateAsync(string route, CancellationToken token)
    {
        if (route == "catalog-home") { await NavigateHomeAsync(token); return; }
        if (MusicCatalogDispatcher.IsCatalogSourceRoute(route))
        {
            var submission = ctx.NavigationSubmission;
            try { await settings.EnsureCatalogSourceSettingsAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch
            {
                if (!ctx.Stopping && submission == ctx.NavigationSubmission) PublishCatalogSourceUnavailable(route);
                return;
            }
            if (ctx.Stopping || submission != ctx.NavigationSubmission) return;
            if (route.StartsWith("catalog-channel/", StringComparison.Ordinal) &&
                route.Split('/', 3)[1] != settings.CatalogSourcePlatform)
                route = "catalog-channels";
        }
        if (route == "catalog-channels" || route == lastChannelRoute && channelEntryError.Length > 0)
        {
            if (lastChannelRoute is not null) route = lastChannelRoute;
            if (channelEntryError.Length > 0)
            {
                RetireCatalogLoad();
                var previousSelection = ctx.CaptureContentSelection();
                ctx.SetLocation(new(route), heading: "音乐频道", clearSelection: true, clearTotal: true);
                ctx.SetStatusQuiet(channelEntryError);
                catalogPages.Clear(); catalogCursors.Clear(); catalogCursors.Add(null);
                personal.LoadedTarget = null; catalogMore = false; catalogContinuation = null;
                catalogPage = 1;
                ctx.PublishContentSelection(previousSelection); ctx.RefreshBindings(); return;
            }
        }
        if (route == "catalog-search") { await search.RestoreSearchAsync(token); return; }
        if (!MusicCatalogDispatcher.IsCatalogSourceRoute(route) && !route.Contains('/') && personal.SelectedCatalogPlatform.Key is "kw" or "mg")
            personal.SelectedCatalogPlatform = personal.CatalogPlatforms[0];
        catalogActions.Invalidate();
        var platform = route.Split('/') is { Length: 3 } parts ? parts[1] :
            MusicCatalogDispatcher.IsCatalogSourceRoute(route) ? settings.CatalogSourcePlatform : personal.SelectedCatalogPlatform.Key;
        bool playlist = route.StartsWith("catalog-playlist/", StringComparison.Ordinal);
        if (!playlist && catalogLocations.TryGetValue(route + "/" + platform, out var saved) &&
            saved.Pages.TryGetValue(saved.Page, out var savedPage) &&
            savedPage.Target?.Cacheable != false && personal.RestoreCatalogPage(savedPage, platform))
        {
            token.ThrowIfCancellationRequested();
            RetireCatalogLoad();
            if (!await PublishCatalogAsync(route, saved.Query, platform, saved.Kind, saved.Page, savedPage,
                token: token, restore: true, beforePublish: () =>
                {
                    catalogPages.Clear(); foreach (var p in saved.Pages) catalogPages[p.Key] = p.Value;
                    catalogCursors.Clear(); catalogCursors.AddRange(saved.Cursors);
                    catalogPageCode = saved.PageCode;
                })) return;
            return;
        }
        if (route.StartsWith("catalog-channel/", StringComparison.Ordinal))
            RetireCatalogLocation(route + "/" + platform);
        catalogPageCode = catalogChannelCodes.GetValueOrDefault(route) ?? "";
        await LoadCatalogAsync(route, "", 1, null, token, refresh: playlist);
    }

    internal void CancelCatalog()
    {
        if (ctx.Stopping) return;
        if (search.IsSearchPage) { _ = search.CancelSearchAsync(); return; }
        optionalReads.Retire(ctx.CurrentRoute);
        RetireCatalogLoad();
        catalogActions.Invalidate();
        ctx.SetStatusQuiet("已取消"); ctx.RefreshBindings();
    }

    internal void RetireCatalogSourcePages()
    {
        var previousSelection = ctx.CaptureContentSelection();
        optionalReads.Retire();
        catalogChannelKinds.Clear();
        catalogSourceReload |= IsCatalogLanding && catalogLoadedPlatform.Length > 0;
        if (catalogLoadFlight is { } flight && MusicCatalogDispatcher.IsCatalogSourceRoute(flight.Request.Key.Route))
            RetireCatalogLoad();
        foreach (var key in catalogLocations.Where(pair => MusicCatalogDispatcher.IsCatalogSourceRoute(pair.Value.Route))
            .Select(pair => pair.Key).ToArray())
            RetireCatalogLocation(key);
        foreach (var key in catalogChannelCodes.Keys.Where(key => key.StartsWith("catalog-channel/", StringComparison.Ordinal)).ToArray())
            catalogChannelCodes.Remove(key);
        homeSections.Clear(); homeDisplaySections.Clear(); homeProjection.Clear();
        channelTags.Clear(); CatalogTagGroups = [];
        ChannelTabs.Clear(); VisibleChannelTabs.Clear(); MoreChannelTabs.Clear(); lastChannelRoute = null; channelEntryError = "";
        publishedHomeState = null;
        homeContentPages.Clear(); homePlatform = ""; homeRefreshRequired = true;
        homePage = 1; homeCursors = [null];
        if (IsCatalogLanding)
        {
            ctx.AdvanceLoadGeneration();
            catalogActions.Invalidate();
            ctx.SetLocation(new(IsCatalogHome ? "catalog-home" : "catalog-channels"),
                heading: IsCatalogHome ? "发现音乐" : "音乐频道", clearCatalogSongs: true, clearSelection: true, clearTotal: true);
            catalogPages.Clear(); catalogCursors.Clear(); catalogCursors.Add(null);
            catalogLoadedPlatform = catalogTag = catalogPageCode = catalogQuery = catalogArtwork = "";
            personal.AccountStamp = ""; personal.LoadedTarget = null;
            catalogContinuation = null; catalogMore = false; catalogPage = 1;
        }
        ctx.PublishContentSelection(previousSelection); ctx.RefreshBindings();
    }

    private void PublishCatalogSourceUnavailable(string route)
    {
        RetireCatalogLoad();
        var previousSelection = ctx.CaptureContentSelection();
        ctx.SetLocation(new(route == "catalog-home" ? "catalog-home" : "catalog-channels"));
        RetireCatalogSourcePages();
        ctx.PublishContentSelection(previousSelection);
        catalogSourceReload = true;
        ctx.SetStatusQuiet(settings.CatalogSourceStatus);
        ctx.RefreshBindings();
    }

    internal Task ActivateCatalogLandingAsync()
    {
        if (ctx.Stopping || !settings.CatalogSourceLoaded || ctx.LoadCancellation is not null) return Task.CompletedTask;
        if (IsCatalogHome && homeRefreshRequired)
            return NavigateHomeAsync(ctx.Lifetime.Token);
        if (!catalogSourceReload) return Task.CompletedTask;
        catalogSourceReload = false;
        if (!IsCatalogLanding || catalogLoadedPlatform == settings.CatalogSourcePlatform)
            return Task.CompletedTask;
        return LoadCatalogAsync(IsCatalogHome ? "catalog-home" : "catalog-channels", "", 1, null, ctx.Lifetime.Token);
    }

    internal void RetireCatalogSession()
    {
        catalogActions.Invalidate();
        RetireCatalogContent();
    }

    private void RetireCatalogContent()
    {
        optionalReads.Retire();
        personal.ClearForSessionRetire();
        search.RetireSearchTracked();
        var wasPublishing = ctx.Publishing; ctx.Publishing = true;
        try
        {
            RetireCatalogLoad();
            RetireHomeAuthority();
            foreach (var key in catalogLocations.Keys.ToArray()) RetireCatalogLocation(key);
            catalogChannelCodes.Clear(); catalogChannelKinds.Clear(); catalogPages.Clear(); catalogCursors.Clear(); catalogCursors.Add(null);
            catalogArtwork = catalogSubtitle = ""; catalogSummaryCount = null; catalogMembersLoaded = false;
            catalogTag = catalogPageCode = catalogQuery = ""; catalogContinuation = null; catalogMore = false; catalogPage = 1;
            channelTags.Clear(); utilityTags.Clear(); CatalogTagGroups = [];
            utilitySections.Clear();
            search.ClearSearchSections();
            search.SearchSongsSource.Clear();
            if (IsCatalogPage && !IsCatalogHome) contentState.CatalogSongsSource.Clear();
            ChannelTabs.Clear(); VisibleChannelTabs.Clear(); MoreChannelTabs.Clear(); lastChannelRoute = null; channelEntryError = "";
            if (IsCatalogHome && publishedHomeState is { } home)
                catalogPages[1] = home;
            else if (IsCatalogPage)
            {
                ctx.ActiveSongs.Clear();
                contentState.ClearSelectionForCatalog("音乐内容");
            }
            ctx.RefreshBindings();
        }
        finally { ctx.Publishing = wasPublishing; }
    }

    internal void RetireCatalogLoad()
    {
        ctx.AdvanceLoadGeneration();
        var cancellation = ctx.LoadCancellation;
        ctx.LoadCancellation = null;
        catalogLoadFlight = null;
        CancelCatalogLoad(cancellation);
    }

    private bool RetireCatalogLoad(CatalogLoadFlight flight)
    {
        if (!ReferenceEquals(catalogLoadFlight, flight) || flight.Generation != ctx.LoadGeneration)
        {
            CancelCatalogLoad(flight.Cancellation);
            return false;
        }
        ctx.AdvanceLoadGeneration();
        catalogLoadFlight = null;
        if (ReferenceEquals(ctx.LoadCancellation, flight.Cancellation)) ctx.LoadCancellation = null;
        CancelCatalogLoad(flight.Cancellation);
        return true;
    }

    internal static void CancelCatalogLoad(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}
