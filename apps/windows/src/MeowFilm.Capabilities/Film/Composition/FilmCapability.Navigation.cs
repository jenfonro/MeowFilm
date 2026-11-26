using Microsoft.UI.Dispatching;
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
            Changed(nameof(BrowseActiveData));
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
        Changed(nameof(IsBrowse));
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
            await Task.WhenAll(Sections.Where(section => section.State is not CatalogSectionState.Ready)
                .Select(section => StartSectionLoad(section, navigationGeneration, lifetime.Token)));
        else if (IsData && ActiveData.State is not CatalogSectionState.Ready)
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
            Changed(nameof(BrowseActiveData));
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
        if (!await SelectPlaybackAsync(item, lifetime.Token)) return;
        if (NavigationRequested is not { } requested)
        {
            await NavigateAsync(PlaybackRoute, lifetime.Token);
            return;
        }
        requested(this, new(PlaybackRoute));
    }

    public Task ReceiveSearchPlaybackAsync(FilmCardItem item, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        token.ThrowIfCancellationRequested();
        if (stopping || disposed) throw new InvalidOperationException("CLOSED");
        var entry = (item.Entry ?? item.SearchContext?.Entry)?.Own()
            ?? throw new ArgumentException("Missing film business identity.", nameof(item));
        entry.Validate();
        navigationGeneration++;
        var context = item.SearchContext;
        var page = context is null ? null : playbackPages.Values.FirstOrDefault(candidate =>
            ReferenceEquals(candidate.SearchContext, context));
        if (page is null)
        {
            page = new FilmPlaybackPage(item with { Entry = entry }, entry, filmPlayback, library, business);
            playbackPages.Add(page.NavigationIdentity, page);
        }
        else
        {
            page.UpdateItem(item with { Entry = entry });
        }
        Playback = page;
        ReleaseUnreachablePages();
        Changed(nameof(Playback)); Changed(nameof(HasPlayback));
        return Task.CompletedTask;
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
        if (!await SelectPlaybackAsync(item, lifetime.Token)) return;
        if (NavigationRequested is not { } requested)
        {
            await NavigateAsync(PlaybackRoute, lifetime.Token);
            return;
        }
        requested(this, new(PlaybackRoute));
    }

    public async Task OpenPlaybackAsync(FilmCardItem item, FilmEntry entry, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (stopping || disposed) return;
        if (!await SelectPlaybackAsync(item with { Entry = entry }, token)) return;
        if (NavigationRequested is { } requested) requested(this, new(PlaybackRoute));
        else await NavigateAsync(PlaybackRoute, token);
    }

    private async Task<bool> SelectPlaybackAsync(FilmCardItem item, CancellationToken token)
    {
        if (item.Entry is not { } entry)
            throw new FilmPlaybackException("CAPABILITY_UNAVAILABLE");
        long ticket = ++navigationGeneration;
        entry = entry.Own();
        entry.Validate();
        var selected = new FilmPlaybackPage(item with { Entry = entry }, entry, filmPlayback, library, business);
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
        await filmPlayback.OpenAsync(page, new
        {
            platform = "windows",
            restoreOnly = restoreOnly || page.HasPlaybackSnapshot,
            entry = page.Entry.Wire(),
            searchReference = page.SearchContext?.Reference
        }, token);
        if (ticket == navigationGeneration && ReferenceEquals(Playback, page) && !stopping && !disposed)
            await page.LoadFavoriteAsync();
    }

    internal static async Task<RuntimeConnection> ConnectPlaybackRuntimeAsync(FilmSourceService sources,
        Func<CancellationToken, Task<RuntimeConnection>> capture, bool reconnect, CancellationToken token)
    {
        if (reconnect) await sources.RefreshAsync(token);
        var borrowed = await capture(token);
        borrowed.Revoked.ThrowIfCancellationRequested();
        await sources.StartAsync(token);
        var verified = await capture(token);
        if (verified.Revoked.IsCancellationRequested || !ReferenceEquals(borrowed.Client, verified.Client) ||
            borrowed.Revoked != verified.Revoked)
            throw new FilmPlaybackException("STALE_SESSION");
        return borrowed;
    }

    private void PlaybackFailed(object? sender, string error) =>
        OperationFailed?.Invoke(this, FilmPlaybackError.Message(error, "播放操作失败"));

    private void PlaybackMediaChanged(object? sender, EventArgs args)
    {
        Changed(nameof(PlaybackMedia));
        string identity = Playback?.TargetId ?? "";
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
