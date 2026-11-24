using Microsoft.UI.Dispatching;
using System.Text.Json;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public sealed partial class FilmCapability
{
    internal async Task SwitchToDataSectionAsync(FilmDataSection selected, CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(ActiveData, selected))
        {
            ActiveData = selected;
            Changed(nameof(ActiveData));
        }
        await NavigateAsync(DataRoute, cancellationToken);
    }

    public Task NavigateAsync(string route, CancellationToken cancellationToken) =>
        NavigateCoreAsync(route, cancellationToken, restoreOnly: false);

    private async Task NavigateCoreAsync(string route, CancellationToken cancellationToken, bool restoreOnly)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (stopping) return;
        ObjectDisposedException.ThrowIf(disposed, this);
        if (route is not (HomeRoute or DataRoute or PlaybackRoute))
            throw new ArgumentException("Unknown film route.", nameof(route));

        long generation = ++navigationGeneration;
        RetireRequests();
        this.route = route;
        Changed(nameof(IsHome));
        Changed(nameof(IsData));
        Changed(nameof(IsPlayback));
        Changed(nameof(Heading));
        if (route == PlaybackRoute)
        {
            if (Playback is not null && (!filmPlayback.HasSession || !filmPlayback.IsCurrentPage(Playback)))
                await OpenPageSessionAsync(Playback, cancellationToken, restoreOnly);
            else await filmPlayback.ResumePresentationAsync();
            return;
        }
        await filmPlayback.LeaveAsync(retainedOwner: filmPlayback.CanRetainMedia);
        if (route == DataRoute)
        {
            if (ActiveData.CanSeedFromHome)
            {
                var home = Sections.Single(section => section.Key == ActiveData.Key);
                ActiveData.Seed(home.Items);
            }
            await StartDataLoad(ActiveData, generation, DataPageSize, cancellationToken, replace: true);
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        var targets = Sections.Where(section => !requests.ContainsKey(section)).ToArray();
        await Task.WhenAll(targets.Select(section =>
            StartSectionLoad(section, generation, cancellationToken)));
        cancellationToken.ThrowIfCancellationRequested();
    }

    public string CaptureNavigationLocation() =>
        IsPlayback && Playback is { } page
            ? $"{PlaybackRoute}:{page.NavigationIdentity}"
            : route;

    public async Task RestoreNavigationAsync(string location, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (stopping) return;
        ObjectDisposedException.ThrowIf(disposed, this);
        if (location.StartsWith(PlaybackRoute + ":", StringComparison.Ordinal))
        {
            string identity = location[(PlaybackRoute.Length + 1)..];
            if (!playbackPages.TryGetValue(identity, out var page))
                throw new ArgumentException("Unknown film playback location.", nameof(location));
            Playback = page;
            ReleaseUnreachablePages();
            Changed(nameof(Playback)); Changed(nameof(HasPlayback));
            await NavigateCoreAsync(PlaybackRoute, token, restoreOnly: true);
            return;
        }
        await NavigateAsync(location, token);
    }

    internal void SuspendPresentation(bool hiddenToTray = false)
    {
        navigationGeneration++;
        RetireRequests();
        // Ordinary covers retire presentation reads, not the active page's execution.
        // The explicit tray policy still drains preparations without accepted output.
        if (hiddenToTray && filmPlayback is { HasOutput: false } owner)
            ObservePresentationLeave(owner.LeaveAsync(owner.CanRetainMedia));
    }

    public Task LeaveNavigationAsync()
    {
        navigationGeneration++;
        RetireRequests();
        return filmPlayback.LeaveAsync(filmPlayback.CanRetainMedia);
    }

    internal void ResumePresentation()
    {
        if (stopping || disposed || dispatcher.IsClosing || !DesktopVisual.IsVisible(view) || !IsPlayback || Playback is null) return;
        if (filmPlayback.IsOpeningPage(Playback)) return;
        if (restoringPresentation is { IsCompleted: false }) return;
        restoringPresentation = dispatcher.EnqueueAsync(async () =>
        {
            if (stopping || disposed || !DesktopVisual.IsVisible(view) || !IsPlayback || Playback is null) return;
            if (filmPlayback.IsOpeningPage(Playback)) return;
            if (filmPlayback.HasSession && filmPlayback.IsCurrentPage(Playback))
                await filmPlayback.ResumePresentationAsync();
            else await OpenPageSessionAsync(Playback, lifetime.Token, restoreOnly: true);
        }, DispatcherQueuePriority.Low);
        ObservePresentationLeave(restoringPresentation, "播放资料恢复失败");
    }

    public Task PrepareRuntimeRecoveryAsync(CancellationToken cancellationToken) =>
        dispatcher.EnqueueAsync(async () =>
        {
            if (stopping || disposed) return;
            navigationGeneration++;
            RetireRequests();
            await Task.WhenAll(search.SuspendRuntimeAsync(), filmPlayback.CloseSessionAsync());
        }, DispatcherQueuePriority.Normal, cancellationToken);

    public void ResumeAfterRuntimeRecovery()
    {
        if (dispatcher.IsClosing) return;
        _ = dispatcher.EnqueueAsync(new Action(() =>
        {
            if (stopping || disposed) return;
            ObservePresentationLeave(RestoreVisibleAsync(), "影片资料恢复失败");
        }));
    }

    private async Task RestoreVisibleAsync()
    {
        await sources.StartAsync(lifetime.Token);
        if (stopping || disposed) return;
        RuntimeRestored?.Invoke(this, EventArgs.Empty);
        if (!DesktopVisual.IsVisible(view)) return;
        if (IsPlayback) ResumePresentation();
        else if (IsHome)
            await Task.WhenAll(Sections.Where(section => section.State is not FilmSectionState.Ready)
                .Select(section => StartSectionLoad(section, navigationGeneration, lifetime.Token)));
        else if (IsData && ActiveData.State is not FilmSectionState.Ready)
            await StartDataLoad(ActiveData, navigationGeneration, NextDataLimit(ActiveData), lifetime.Token);
    }

    internal Task OpenDataAsync(FilmHomeSection? section = null)
    {
        if (stopping || disposed || !IsHome) return Task.CompletedTask;
        section ??= Sections[0];
        if (!Sections.Contains(section)) throw new ArgumentException("Unknown film section.", nameof(section));
        var selected = DataSections.Single(data => data.Key == section.Key);
        if (!ReferenceEquals(ActiveData, selected))
        {
            ActiveData = selected;
            Changed(nameof(ActiveData));
        }
        if (NavigationRequested is not { } requested)
            return NavigateAsync(DataRoute, lifetime.Token);
        requested(this, new(DataRoute));
        return Task.CompletedTask;
    }

    internal async Task OpenPlaybackAsync(FilmCardItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (stopping || disposed || (!IsHome && !IsData)) return;
        if (!await SelectPlaybackAsync(item, lifetime.Token, IsHome ? "home" : "search")) return;
        if (NavigationRequested is not { } requested)
        {
            await NavigateAsync(PlaybackRoute, lifetime.Token);
            return;
        }
        requested(this, new(PlaybackRoute));
    }

    public async Task ReceiveSearchPlaybackAsync(FilmCardItem item, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (stopping || disposed) throw new InvalidOperationException("CLOSED");
        var request = item.PlaybackContext ?? throw new ArgumentException("Missing film business identity.", nameof(item));
        var context = item.SearchContext;
        long ticket = ++navigationGeneration;
        var page = context is null ? null : playbackPages.Values.FirstOrDefault(candidate =>
            ReferenceEquals(candidate.SearchContext, context));
        if (context is not null) request = await context.HandoffAsync(token);
        token.ThrowIfCancellationRequested();
        if (ticket != navigationGeneration) throw new OperationCanceledException(token);
        if (stopping || disposed) throw new FilmPlaybackException("CLOSED");
        if (page is null)
        {
            page = new FilmPlaybackPage(item with { PlaybackContext = request }, request.Own(), filmPlayback, library, business);
            playbackPages.Add(page.NavigationIdentity, page);
        }
        else page.UpdateItem(item with { PlaybackContext = request });
        Playback = page;
        ReleaseUnreachablePages();
        Changed(nameof(Playback)); Changed(nameof(HasPlayback));
    }

    public void RetainNavigationLocations(IReadOnlySet<string> locations)
    {
        retainedNavigationLocations = locations;
        ReleaseUnreachablePages();
    }

    private void ReleaseUnreachablePages()
    {
        foreach (var entry in playbackPages.Where(entry => !ReferenceEquals(entry.Value, Playback) &&
            !retainedNavigationLocations.Contains($"{PlaybackRoute}:{entry.Key}")).ToArray())
        {
            entry.Value.Release();
            playbackPages.Remove(entry.Key);
        }
    }

    internal async Task OpenRecommendationAsync(FilmCardItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (stopping || disposed || !IsPlayback || Playback is null) return;
        if (!await SelectPlaybackAsync(item, lifetime.Token, Playback.EntrySurface)) return;
        if (NavigationRequested is not { } requested)
        {
            await NavigateAsync(PlaybackRoute, lifetime.Token);
            return;
        }
        requested(this, new(PlaybackRoute));
    }

    public async Task OpenPlaybackAsync(FilmCardItem item, FilmPlaybackRequest request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (stopping || disposed) return;
        request = request with { EntrySurface = IsHome ? "home" : "search" };
        if (!await SelectPlaybackAsync(item with { PlaybackContext = request }, token)) return;
        if (NavigationRequested is { } requested) requested(this, new(PlaybackRoute));
        else await NavigateAsync(PlaybackRoute, token);
    }

    private async Task<bool> SelectPlaybackAsync(FilmCardItem item, CancellationToken token, string? entrySurface = null)
    {
        if (item.PlaybackContext is not { } request)
            throw new FilmPlaybackException("CAPABILITY_UNAVAILABLE");
        long ticket = ++navigationGeneration;
        request = (request with { EntrySurface = entrySurface ?? request.EntrySurface ?? "search" }).Own();
        var selected = new FilmPlaybackPage(item with { PlaybackContext = request }, request, filmPlayback, library, business);
        playbackPages.Add(selected.NavigationIdentity, selected);
        Playback = selected;
        ReleaseUnreachablePages();
        Changed(nameof(Playback)); Changed(nameof(HasPlayback));
        await OpenPageSessionAsync(selected, token);
        return ticket == navigationGeneration && ReferenceEquals(Playback, selected) && !stopping && !disposed;
    }

    private async Task OpenPageSessionAsync(FilmPlaybackPage page, CancellationToken token, bool restoreOnly = false)
    {
        long ticket = navigationGeneration;
        if (!ReferenceEquals(Playback, page) || stopping || disposed) return;
        var request = page.CaptureRequest;
        await filmPlayback.OpenAsync(page, new
        {
            platform = "windows", entrySurface = request.EntrySurface, restoreOnly = restoreOnly || page.HasPlaybackSnapshot,
            target = request.Target.Wire(), resumeTarget = request.ResumeTarget?.Wire(), sources = request.Sources,
            query = request.Query, results = request.Results, search = request.Search, metadata = request.Metadata,
            cacheEntries = request.CacheEntries, resultHints = request.ResultHints, searchReference = request.SearchReference,
            browsing = request.Browsing
        }, token);
        if (ticket == navigationGeneration && ReferenceEquals(Playback, page) && !stopping && !disposed)
            await page.LoadFavoriteAsync();
    }

    internal static async Task<FilmPlaybackOpen> PreparePlaybackOpenAsync(FilmSourceService sources,
        Func<CancellationToken, Task<RuntimeConnection>> capture, FilmPlaybackPage page, JsonElement parameters,
        bool reconnect, CancellationToken token)
    {
        // Only an explicit retry may recover a failed App epoch. Ordinary capture,
        // navigation and an event-stream reconnect never restart it.
        if (reconnect) await sources.RefreshAsync(token);
        var borrowed = await capture(token);
        borrowed.Revoked.ThrowIfCancellationRequested();
        await sources.StartAsync(token);
        var verified = await capture(token);
        if (!FilmPlaybackOpen.SameOwner(borrowed, verified)) throw new FilmPlaybackException("STALE_SESSION");
        return new(borrowed, FilmPlaybackOpen.ParametersFor(page, parameters, borrowed, PlaybackSources(sources)));
    }

    private IReadOnlyList<JsonElement> PlaybackSources() => PlaybackSources(sources);

    private static IReadOnlyList<JsonElement> PlaybackSources(FilmSourceService sources)
    {
        var (cats, videos) = sources.ReadSnapshot();
        return videos.Where(video => video.Options.Enabled)
            .Select(video => new { Video = video, Cat = cats.FirstOrDefault(cat => cat.InstallationId == video.Key.InstallationId) })
            .Where(row => row.Cat is { Enabled: true, State: "ready", Generation: > 0 })
            .Select(row => FilmJson.Encode(new
            {
                key = JsonSerializer.Serialize(new[] { row.Video.Key.InstallationId, row.Video.Key.SiteKey }),
                engine = "cat", installationId = row.Video.Key.InstallationId, siteId = row.Video.Key.SiteKey,
                generation = row.Cat!.Generation, name = row.Video.Name, enabled = row.Video.Options.Enabled,
                searchable = row.Video.Options.SearchEnabled
            })).ToArray();
    }

    private void PlaybackSourcesChanged(object? sender, EventArgs args)
    {
        if (dispatcher.IsClosing) return;
        if (dispatcher.HasThreadAccess) Apply();
        else _ = dispatcher.EnqueueAsync(Apply);
        void Apply()
        {
            if (!stopping && !disposed) filmPlayback.SourcesChanged(PlaybackSources());
        }
    }

    private void PlaybackFailed(object? sender, string error) =>
        OperationFailed?.Invoke(this, FilmPlaybackError.Message(error, "播放操作失败"));

    private void PlaybackMediaChanged(object? sender, EventArgs args)
    {
        Changed(nameof(PlaybackMedia));
        string identity = Playback?.EntryTarget.Id ?? "";
        if (identity != favoriteTargetId)
        {
            favoriteTargetId = identity;
            FavoritesChanged(sender, args);
        }
    }

    private void FavoritesChanged(object? sender, EventArgs args)
    {
        if (dispatcher.IsClosing) return;
        _ = dispatcher.EnqueueAsync(new Action(() =>
        {
            if (stopping || disposed) return;
            favoriteDirty = true;
            if (favoriteRefresh is { IsCompleted: false }) return;
            favoriteRefresh = RefreshFavoritesAsync();
            ObservePresentationLeave(favoriteRefresh, "收藏读取失败");
        }));
    }

    private async Task RefreshFavoritesAsync()
    {
        while (favoriteDirty && !stopping && !disposed)
        {
            favoriteDirty = false;
            var items = await library.ReadAsync(lifetime.Token);
            if (!stopping && !disposed) Playback?.RefreshFavorite(items);
        }
    }

    private void ObservePresentationLeave(Task operation, string failure = "停止播放失败")
    {
        // The notification owns the business error; shutdown owns completion of
        // that observation, not the original operation's success.
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        operations.Add(settled.Task);
        _ = SettleAsync();
        async Task SettleAsync()
        {
            try { await operation; }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                if (!stopping && !disposed) OperationFailed?.Invoke(this, failure);
            }
            finally
            {
                operations.Remove(settled.Task);
                settled.TrySetResult();
            }
        }
    }

    public Task StepAsync(bool next) => filmPlayback.CommandAsync(next ? "next" : "previous");
    public Task ResumePlaybackAsync() => filmPlayback.HasOutput ? filmPlayback.SetPausedAsync(false) : Task.CompletedTask;
    public Task PausePlaybackAsync() => filmPlayback.HasOutput ? filmPlayback.SetPausedAsync(true) : Task.CompletedTask;
    public Task StopPlaybackAsync() => filmPlayback.CommandAsync("stop");
    public Task SeekPlaybackAsync(double seconds) => filmPlayback.SeekAsync(seconds);
    public Task PreemptPlaybackAsync() => filmPlayback.PreemptAsync();

    public async Task ExecuteMediaShortcutAsync(MediaShortcutAction action)
    {
        if (stopping || disposed) return;
        try
        {
            switch (action)
            {
                case MediaShortcutAction.TogglePlayback:
                    if (filmPlayback.HasOutput) await filmPlayback.PauseAsync();
                    else if (Playback is { CanPlay: true } page) await page.PlayAsync();
                    break;
                case MediaShortcutAction.Previous:
                case MediaShortcutAction.Next:
                    if (filmPlayback.HasSession) await StepAsync(action == MediaShortcutAction.Next);
                    break;
                case MediaShortcutAction.VolumeUp:
                case MediaShortcutAction.VolumeDown:
                    if (filmPlayback.HasOutput)
                        filmPlayback.Volume = Math.Clamp(filmPlayback.Volume + (action == MediaShortcutAction.VolumeUp ? 5 : -5), 0, 100);
                    break;
                default:
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!stopping && !disposed)
                OperationFailed?.Invoke(this, FilmPlaybackError.Message(error, "播放快捷键操作失败"));
        }
    }
}
