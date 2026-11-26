namespace MeowFilm.Capabilities;

public sealed partial class MusicCatalogController
{
    internal async Task ClearHomeSnapshotAsync(CancellationToken token = default)
    {
        homeSnapshot = null;
        homeContentPages.Clear();
        homeRefreshRequired = true;
        await homeSnapshotStore.ClearAsync(token).ConfigureAwait(false);
    }

    internal Task<(bool HasSnapshot, int PageCount, int SectionCount, int CardCount)> GetHomeSnapshotStatsAsync(CancellationToken token = default) =>
        homeSnapshotStore.GetStatsAsync(token);

    public Task PrepareStartupAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (stopping) return Task.FromException(new MusicOperationException("CLOSED"));
        if (startupPreparation is null)
        {
            var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            loadDrains.Add(drained.Task);
            var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            startupCancellation = request;
            startupPreparation = Prepare(request, drained);
            _ = Observe(startupPreparation);
        }
        return startupPreparation.WaitAsync(cancellationToken);

        static async Task Observe(Task work) { try { await work; } catch { } }
        async Task Prepare(CancellationTokenSource request, TaskCompletionSource drained)
        {
            await Task.Yield();
            try
            {
                await accounts.EnsureAccountsForRequestAsync(request.Token);
                await settings.EnsureCatalogSourceSettingsAsync(request.Token);
                if (!homeSnapshotRead)
                {
                    homeSnapshot = await MusicHomeSnapshotCodec.ReadAsync(
                        homeSnapshotStore, application.Library.ReadSongRecord, request.Token);
                    homeSnapshotRead = true;
                }
            }
            catch { startupPreparation = null; throw; }
            finally
            {
                if (ReferenceEquals(startupCancellation, request)) startupCancellation = null;
                request.Dispose();
                loadDrains.Remove(drained.Task); drained.TrySetResult();
            }
        }
    }

    private async Task NavigateHomeAsync(CancellationToken token)
    {
        long submission = ctx.NavigationSubmission;
        try
        {
            await PrepareStartupAsync(token);
            await settings.EnsureCatalogSourceSettingsAsync(token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            if (!stopping && submission == ctx.NavigationSubmission)
            {
                if (!settings.CatalogSourceLoaded) PublishCatalogSourceUnavailable("catalog-home");
                else ctx.SetStatus(ErrorText(error));
            }
            return;
        }
        token.ThrowIfCancellationRequested();
        if (stopping || submission != ctx.NavigationSubmission) return;
        var sourcePlatform = settings.CatalogSourcePlatform;
        if (publishedHomeState is null && IsCatalogHome && catalogLoadFlight is { } flight && !flight.Work.IsCompleted &&
            flight.Request.Key.Route == "catalog-home" && flight.Request.Key.Platform == sourcePlatform)
        {
            await SubscribeCatalogLoad(flight, token);
            return;
        }
        RetireCatalogLoad();
        if (publishedHomeState is { } published && homePlatform == sourcePlatform)
        {
            if (!await PublishCatalogAsync("catalog-home", "", homePlatform, "歌曲", homePage, published,
                initializeEntries: !homeRefreshRequired, token: token, restore: true, beforePublish: () =>
                {
                    catalogPages.Clear();
                    catalogCursors.Clear(); catalogCursors.AddRange(homeCursors);
                })) return;
        }
        else if (homeSnapshot is { } saved && saved.Platform == sourcePlatform)
        {
            MusicCatalogSection[] sections = [];
            foreach (var page in saved.Pages)
                sections = MusicCatalogDispatcher.MergeSections(sections, page);
            var state = new CatalogPageState(sections, [], "", "发现音乐", "", false, null,
                sections.Sum(section => section.Items.Count));
            if (!await PublishCatalogAsync("catalog-home", "", saved.Platform, "歌曲", 1, state,
                initializeEntries: false, token: token, beforePublish: () =>
                {
                    catalogPages.Clear(); catalogCursors.Clear(); catalogCursors.Add(null);
                    homePlatform = saved.Platform;
                    homeContentPages.Clear(); homeContentPages.AddRange(saved.Pages);
                })) return;
        }
        if (publishedHomeState is { } current && homePlatform == sourcePlatform)
        {
            catalogPages[homePage] = current;
            if (homeRefreshRequired) StartHomeRefresh();
            else StartHomeContinuation();
            return;
        }
        catalogPages.Clear(); catalogCursors.Clear(); catalogCursors.Add(null);
        homeRefreshRequired = false;
        await LoadCatalogAsync("catalog-home", "", 1, null, token, refresh: true);
    }

    private void StartHomeRefresh()
    {
        if (stopping || !IsCatalogHome || !settings.CatalogSourceLoaded || settings.IsSettingsRequested) return;
        homeRefreshRequired = false;
        _ = Refresh();
        async Task Refresh()
        {
            try { await LoadCatalogAsync("catalog-home", "", 1, null, lifetime.Token, refresh: true); }
            catch (OperationCanceledException) { }
        }
    }

    private void SaveHomeContent(string platform, int number, MusicCatalogSection[] pageSections)
    {
        if (number == 1) homeContentPages.Clear();
        homeContentPages.Add(pageSections);
        homePlatform = platform;
        homeSnapshot = new(platform, homeContentPages.ToArray());
        homeCursors = catalogCursors.ToArray();
        var saved = homeSnapshot;
        var write = Save();
        loadDrains.Add(write);
        _ = Observe();
        async Task Save()
        {
            try { await MusicHomeSnapshotCodec.SaveAsync(homeSnapshotStore, saved, lifetime.Token); }
            catch (OperationCanceledException) { }
        }
        async Task Observe()
        {
            try { await write; }
            finally { loadDrains.Remove(write); }
        }
    }

    internal void OnCatalogAccountObserved(string platform, long observation)
    {
        if (ctx.Dispatcher.IsClosing) return;
        _ = ctx.Dispatcher.EnqueueAsync(() =>
        {
            if (stopping) return;
            optionalReads.RetireForAccount(platform, observation);
            RetireChannelAccountPages(platform, observation);
            changedCatalogAccounts.Add(platform);
            if (catalogAction is { } action && action.Platform == platform && action.Observation < observation)
                InvalidateCatalogAction();
            bool currentRead = catalogLoadFlight is { } flight && flight.Request.Key.Platform == platform &&
                flight.Request.Key.AccountObservation >= observation;
            if (platform == settings.CatalogSourcePlatform &&
                !(currentRead && catalogLoadFlight!.Request.Key.Route == "catalog-home") &&
                publishedHomeState?.AccountStamp != CatalogAccountKey(platform))
                RetireHomeAuthority();
            if (platform == catalogLoadedPlatform && !currentRead && catalogAccountStamp != CatalogAccountKey(platform))
                RetireCatalogContent();
            BeginHomeAccountRecovery(forceRead: false);
        });
    }

    internal void RefreshHomeDisplay()
    {
        if (homePlatform.Length == 0) return;
        bool includeAccountEntries = publishedHomeState?.AccountStamp == CatalogAccountKey(homePlatform) ||
            accounts.HasSettledCurrentAccount(homePlatform);
        var display = homeSections.Select(source =>
        {
            var projected = source.DisplayableCards(includeAccountEntries);
            if (homeProjection.TryGetValue(source, out var existing) &&
                existing.Items.SequenceEqual(projected.Items, ReferenceEqualityComparer.Instance) &&
                ReferenceEquals(existing.Action, projected.Action)) return existing;
            return homeProjection[source] = projected;
        }).ToArray();
        foreach (var obsolete in homeProjection.Keys.Where(source =>
            !homeSections.Contains(source, ReferenceEqualityComparer.Instance)).ToArray())
            homeProjection.Remove(obsolete);
        ReconcileSections(homeDisplaySections, display);
        if (!IsCatalogHome) return;
        optionalReads.RefreshDemandMembers();
        var songs = display.SelectMany(section => section.Items).Select(card => card.Song).OfType<SongRow>().ToArray();
        MusicRuntimeContext.ReconcileSongRows(CatalogSongsSource, songs);
        if (ctx.RawStatus is "" or "暂无内容")
            ctx.SetStatus(display.Length == 0 ? "暂无内容" : "");
    }

    private void RetireHomeAuthority()
    {
        optionalReads.Retire("catalog-home");
        homeRefreshRequired = true;
        homePage = 1; homeCursors = [null];
        if (publishedHomeState is { } home)
            publishedHomeState = home with { More = false, Next = null, AccountStamp = "", ExpiresAt = null };
        RefreshHomeDisplay();
    }

    internal void BeginHomeAccountRecovery(bool forceRead)
    {
        if (stopping || !settings.CatalogSourceLoaded) return;
        homeAccountRecoveryPending = true;
        homeAccountRecoveryForceRead |= forceRead;
        if (!homeAccountRecovery.IsCompleted) return;
        homeAccountRecovery = Recover();
        loadDrains.Add(homeAccountRecovery);
        _ = Drain(homeAccountRecovery);
        async Task Drain(Task work) { try { await work; } finally { loadDrains.Remove(work); } }
        async Task Recover()
        {
            await Task.Yield();
            try
            {
                while (!stopping && homeAccountRecoveryPending)
                {
                    bool forced = homeAccountRecoveryForceRead;
                    homeAccountRecoveryPending = false;
                    homeAccountRecoveryForceRead = false;
                    if (forced) accounts.MarkAccountsStale();
                    await ctx.CommandTail;
                    if (stopping) return;
                    var changed = changedCatalogAccounts.ToArray();
                    changedCatalogAccounts.Clear();
                    await accounts.EnsureAccountsForRequestAsync(lifetime.Token);
                    if (stopping) return;
                    var account = accounts.Accounts.Single(a => a.Platform == settings.CatalogSourcePlatform);
                    if (forced && account.State is "none" or "revoked") RetireHomeAuthority();
                    if (homeRefreshRequired && IsCatalogHome) StartHomeRefresh();
                    if (changed.Any(platform => accounts.Accounts.Single(a => a.Platform == platform).State == "revoked"))
                        PublishCatalogAccountNotice("AUTH_EXPIRED");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (ErrorCode(error) is not null) { }
            finally
            {
                homeAccountRecoveryPending = false;
                homeAccountRecoveryForceRead = false;
            }
        }
    }

    private void PublishCatalogAccountNotice(string code)
    {
        catalogAccountNotice = MusicCatalogDto.ErrorText(code);
        ctx.NotifyStatusChanged();
    }

    private void RecoverHomeAfterAuthenticationFailure()
    {
        if (stopping || !IsCatalogHome ||
            accounts.Accounts.Single(a => a.Platform == settings.CatalogSourcePlatform).State is not ("saved" or "verified")) return;
        BeginHomeAccountRecovery(forceRead: true);
    }

    private void StartHomeContinuation()
    {
        if (stopping || !IsCatalogHome || !catalogMore || ctx.LoadCancellation is not null ||
            homeContinuationCancellation is not null) return;
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        homeContinuationCancellation = request;
        Changed(nameof(CanCancelCatalog));
        var generation = ctx.LoadGeneration;
        var work = Read();
        loadDrains.Add(work);
        _ = Drain(work);
        async Task Drain(Task task) { try { await task; } finally { loadDrains.Remove(task); } }
        async Task Read()
        {
            await Task.Yield();
            try
            {
                while (!stopping && generation == ctx.LoadGeneration && !request.IsCancellationRequested && IsCatalogHome && catalogMore)
                {
                    int previous = catalogPage;
                    var next = CaptureCatalogLoadRequest(ctx.CurrentRoute, catalogQuery, previous + 1, catalogContinuation, true, catalogTag);
                    await LoadCatalogCoreAsync(new(next, generation, ctx.StatusEpoch, request));
                    if (catalogPage == previous) break;
                }
            }
            finally
            {
                if (ReferenceEquals(homeContinuationCancellation, request)) homeContinuationCancellation = null;
                request.Dispose();
                if (!disposed) CatalogBindings();
            }
        }
    }
}
