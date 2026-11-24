using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MeowFilm.Capabilities.Library;
using MeowFilm.Core.Debug;

namespace MeowFilm.Capabilities;

[Flags]
internal enum SearchHistorySurface
{
    None = 0,
    Settings = 1,
    Discovery = 2
}

public sealed class MusicSearchKindSettings(string kind) : INotifyPropertyChanged
{
    private int pages = 1, concurrency = 1, cacheMinutes = 60;
    private string[] savedOrder = [], savedEnabled = [];
    public string Kind { get; } = kind;
    public int Pages => pages;
    public int Concurrency => concurrency;
    public int CacheMinutes => cacheMinutes;
    public ObservableCollection<MusicPlatformPriorityRow> Platforms { get; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Apply(MusicSearchKindRecord value)
    {
        MusicDto.Require(value.Pages is >= 1 and <= int.MaxValue &&
            value.Concurrency is >= 1 and <= int.MaxValue &&
            value.CacheMinutes is >= 1 and <= MusicSearchSettings.MaxCacheMinutes);
        int nextPages = (int)value.Pages,
            nextConcurrency = (int)value.Concurrency,
            nextCache = (int)value.CacheMinutes;
        var enabled = value.Enabled.ToHashSet(StringComparer.Ordinal);
        var order = value.Order;
        savedOrder = order;
        savedEnabled = order.Where(enabled.Contains).ToArray();
        order = order.Concat(MusicLibraryOwner.SearchPlatforms.Except(order)).ToArray();
        MusicSettingsController.ReconcilePriorityRows(Platforms, order, row => row.Platform,
            key => new(key, enabled.Contains(key)),
            (row, key) => row.Apply(enabled.Contains(key)));
        pages = nextPages; concurrency = nextConcurrency; cacheMinutes = nextCache;
        Changed(nameof(Pages));
        Changed(nameof(Concurrency));
        Changed(nameof(CacheMinutes));
    }

    internal SearchKindState Snapshot() => new(
        savedOrder, savedEnabled,
        pages, concurrency, cacheMinutes);

    private void Changed([CallerMemberName] string name = "") =>
        PropertyChanged?.Invoke(this, new(name));
}

internal sealed record SearchKindState(string[] Order, string[] Enabled, int Pages, int Concurrency, int CacheMinutes)
{
    internal MusicSearchKindRecord ToRecord() => new(Order, Enabled, Pages, Concurrency, CacheMinutes);
}

/// <summary>
/// Standalone domain handler for search platform priorities, search history lifecycle,
/// and metadata platform priority policy.
/// </summary>
internal sealed class MusicSearchMetadataSettings(MusicRuntimeContext ctx, Action<string> notify)
{
    // Search policy & history state
    private bool songSearchLoaded, playlistSearchLoaded, searchPreferencesBusy, searchHistoryLoaded;
    private bool SearchPreferencesLoadedAll => songSearchLoaded && playlistSearchLoaded;
    private int searchHistoryCount;
    private long searchPreferenceRevision, searchHistoryRevision;
    private long searchHistoryEpoch, searchPreferencesGeneration;
    private Task searchPreferencesWork = Task.CompletedTask;
    private SearchHistorySurface searchHistorySurfaces;
    private CancellationTokenSource? searchHistoryRequest;
    private Task searchHistoryTail = Task.CompletedTask;
    private string searchPreferenceStatus = "";

    // Metadata policy state
    private long metadataRevision, metadataGeneration;
    private string[] metadataOrder = [], metadataEnabled = [];
    private bool metadataLoaded, metadataBusy;
    private Task metadataWork = Task.CompletedTask;

    internal event Func<Task>? SearchPolicyChanging;
    internal event Action? MetadataPolicyChanging;
    internal event Func<Task>? MetadataPolicyApplied;
    internal event Func<CancellationToken, Task>? MetadataArtworkRefreshRequested;

    internal MusicSearchKindSettings SongSearch { get; } = new("songs");
    internal MusicSearchKindSettings PlaylistSearch { get; } = new("playlists");
    internal ObservableCollection<MusicPlatformPriorityRow> MetadataPlatforms { get; } = [];

    internal bool CanEditSearchPreferences => ctx.CanEdit && SearchPreferencesLoadedAll && !searchPreferencesBusy;
    internal bool HasSearchHistory => searchHistoryCount > 0;
    internal string SearchPreferenceStatus => searchPreferenceStatus;
    internal bool CanEditMetadata => ctx.CanEdit && metadataLoaded && !metadataBusy;
    internal bool MetadataLoaded => metadataLoaded;
    internal Task SearchPreferencesWork => searchPreferencesWork;
    internal Task MetadataWork => metadataWork;

    internal bool SearchPreferencesLoaded(string kind) => kind == "songs" ? songSearchLoaded : playlistSearchLoaded;

    private bool SearchPreferencesCurrent(long generation) =>
        generation == searchPreferencesGeneration && !ctx.Stopping && !ctx.Disposed;

    internal void RetireSearchPreferences() => searchPreferencesGeneration++;

    internal void RetireMetadata()
    {
        metadataGeneration++;
        metadataLoaded = false;
        notify(nameof(MusicSettingsController.CanEditMetadata));
    }

    internal void CancelSearchHistoryForShutdown() => searchHistoryRequest?.Cancel();

    internal async Task<bool> EnsureSearchPreferencesLoadedAsync(string kind, CancellationToken token)
    {
        if (SearchPreferencesLoaded(kind)) return true;
        try { await ReadSearchPreferencesAsync(token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            DebugLog.Warn(DebugCategories.Search, "search-settings", "ensure-loaded-failed",
                new { kind, error = error.Message });
        }
        return SearchPreferencesLoaded(kind);
    }

    internal async Task ReadSearchPreferencesAsync(CancellationToken token)
    {
        long generation = searchPreferencesGeneration;
        Exception? failure = null;
        try
        {
            foreach (string kind in new[] { "songs", "playlists" })
            {
                try
                {
                    var settings = await ctx.RunSettingReceiptAsync(MusicSettingReceiptKeys.SearchPolicy,
                        ct => ctx.Application.GetSearchPolicyAsync(kind, ct), token);
                    if (!SearchPreferencesCurrent(generation)) return;
                    PublishSearchPreferences(settings, kind);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    if (!SearchPreferencesCurrent(generation)) return;
                    if (kind == "songs") songSearchLoaded = false; else playlistSearchLoaded = false;
                    failure ??= error;
                }
            }
            searchPreferenceStatus = failure is null ? "" : "搜索设置不可用";
            if (failure is not null) throw failure;
        }
        finally
        {
            if (SearchPreferencesCurrent(generation)) SearchPreferenceBindings();
        }
    }

    private void PublishSearchPreferences(MusicSearchPolicyRecord value, string? kind = null)
    {
        searchPreferenceRevision = value.Revision;
        if (kind is null or "songs")
        {
            SongSearch.Apply(value.Songs);
            songSearchLoaded = true;
        }
        if (kind is null or "playlists")
        {
            PlaylistSearch.Apply(value.Playlists);
            playlistSearchLoaded = true;
        }
        SearchPreferenceBindings();
    }

    private void PublishSearchHistory(MusicSearchHistoryRecord value)
    {
        long revision = value.Revision;
        if (searchHistoryLoaded && revision < searchHistoryRevision) return;
        searchHistoryRevision = revision;
        searchHistoryCount = value.Items.Length;
        searchHistoryLoaded = true;
        notify(nameof(MusicSettingsController.HasSearchHistory));
    }

    internal Task ActivateSearchHistoryAsync(SearchHistorySurface surface)
    {
        searchHistorySurfaces |= surface;
        if (ctx.Stopping || ctx.Disposed || searchHistoryLoaded) return Task.CompletedTask;
        long epoch = searchHistoryEpoch;
        var previous = searchHistoryTail;
        return searchHistoryTail = Read();

        async Task Read()
        {
            await previous;
            if (ctx.Stopping || ctx.Disposed || searchHistoryLoaded || epoch != searchHistoryEpoch ||
                searchHistorySurfaces == SearchHistorySurface.None)
                return;
            using var request = CancellationTokenSource.CreateLinkedTokenSource(ctx.Lifetime.Token);
            searchHistoryRequest = request;
            try
            {
                var history = await ctx.Application.GetSearchHistoryAsync(request.Token);
                request.Token.ThrowIfCancellationRequested();
                if (!ctx.Stopping && !ctx.Disposed && epoch == searchHistoryEpoch &&
                    searchHistorySurfaces != SearchHistorySurface.None)
                    PublishSearchHistory(history);
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch
            {
                if (!ctx.Stopping && !ctx.Disposed && epoch == searchHistoryEpoch &&
                    searchHistorySurfaces != SearchHistorySurface.None)
                {
                    searchPreferenceStatus = "搜索历史不可用";
                    SearchPreferenceBindings();
                }
            }
            finally
            {
                if (ReferenceEquals(searchHistoryRequest, request)) searchHistoryRequest = null;
            }
        }
    }

    internal Task DeactivateSearchHistoryAsync(SearchHistorySurface surface)
    {
        searchHistorySurfaces &= ~surface;
        if (searchHistorySurfaces != SearchHistorySurface.None) return Task.CompletedTask;
        searchHistoryEpoch++;
        searchHistoryRequest?.Cancel();
        return searchHistoryTail;
    }

    internal Task RetireSearchHistoryAsync()
    {
        searchHistoryEpoch++;
        searchHistoryRequest?.Cancel();
        return searchHistoryTail;
    }

    private void SearchPreferenceBindings()
    {
        notify(nameof(MusicSettingsController.CanEditSearchPreferences));
        notify(nameof(MusicSettingsController.SearchPreferenceStatus));
    }

    internal Task SetSearchAdvancedAsync(string kind, int pages, int concurrency, int cacheMinutes)
    {
        if (!CanEditSearchPreferences || kind is not ("songs" or "playlists") ||
            !MusicSearchSettings.ValidPages(pages) ||
            !MusicSearchSettings.ValidConcurrency(concurrency) ||
            !MusicSearchSettings.ValidCacheMinutes(cacheMinutes)) return Task.CompletedTask;
        var songs = SongSearch.Snapshot();
        var playlists = PlaylistSearch.Snapshot();
        var current = kind == "songs" ? songs : playlists;
        if (current.Pages == pages && current.Concurrency == concurrency && current.CacheMinutes == cacheMinutes)
            return Task.CompletedTask;
        var desired = current with { Pages = pages, Concurrency = concurrency, CacheMinutes = cacheMinutes };
        if (kind == "songs") songs = desired; else playlists = desired;
        return SaveSearchPreferencesAsync(songs, playlists);
    }

    internal Task SetSearchPlatformEnabledAsync(string kind, MusicPlatformPriorityRow row, bool enabled)
    {
        if (!CanEditSearchPreferences) return Task.CompletedTask;
        var settings = kind == "songs" ? SongSearch : kind == "playlists" ? PlaylistSearch : null;
        if (settings is null || !settings.Platforms.Contains(row) || row.Enabled == enabled)
            return Task.CompletedTask;
        var songs = SongSearch.Snapshot();
        var playlists = PlaylistSearch.Snapshot();
        SearchKindState Change(SearchKindState state)
        {
            var values = state.Enabled.ToHashSet(StringComparer.Ordinal);
            if (enabled) values.Add(row.Platform); else values.Remove(row.Platform);
            var order = enabled && !state.Order.Contains(row.Platform) ? state.Order.Append(row.Platform).ToArray() : state.Order;
            return state with { Order = order, Enabled = order.Where(values.Contains).ToArray() };
        }
        if (kind == "songs") songs = Change(songs); else playlists = Change(playlists);
        return SaveSearchPreferencesAsync(songs, playlists);
    }

    internal Task MoveSearchPlatformAsync(string kind, MusicPlatformPriorityRow source, MusicPlatformPriorityRow target)
    {
        if (!CanEditSearchPreferences || ReferenceEquals(source, target)) return Task.CompletedTask;
        var settings = kind == "songs" ? SongSearch : kind == "playlists" ? PlaylistSearch : null;
        if (settings is null || !settings.Platforms.Contains(source) || !settings.Platforms.Contains(target))
            return Task.CompletedTask;
        var songs = SongSearch.Snapshot();
        var playlists = PlaylistSearch.Snapshot();
        SearchKindState Move(SearchKindState state)
        {
            var order = settings.Platforms.Select(row => row.Platform).ToList();
            int from = order.IndexOf(source.Platform), to = order.IndexOf(target.Platform);
            if (from < 0 || to < 0) return state;
            order.RemoveAt(from);
            order.Insert(to, source.Platform);
            var enabled = state.Enabled.ToHashSet(StringComparer.Ordinal);
            return state with { Order = order.ToArray(), Enabled = order.Where(enabled.Contains).ToArray() };
        }
        if (kind == "songs") songs = Move(songs); else playlists = Move(playlists);
        return SaveSearchPreferencesAsync(songs, playlists);
    }

    private static bool Matches(MusicSearchKindSettings current, SearchKindState desired)
    {
        var saved = current.Snapshot();
        return saved.Pages == desired.Pages && saved.Concurrency == desired.Concurrency &&
            saved.CacheMinutes == desired.CacheMinutes && saved.Order.SequenceEqual(desired.Order) &&
            saved.Enabled.SequenceEqual(desired.Enabled);
    }

    private Task SaveSearchPreferencesAsync(SearchKindState songs, SearchKindState playlists)
    {
        if (!CanEditSearchPreferences) return Task.CompletedTask;
        long revision = searchPreferenceRevision;
        var songsRecord = songs.ToRecord();
        var playlistsRecord = playlists.ToRecord();
        searchPreferencesBusy = true;
        long generation = searchPreferencesGeneration;
        SearchPreferenceBindings();
        return searchPreferencesWork = ctx.RunAsync(async () =>
        {
            if (SearchPolicyChanging is { } onChanging) await onChanging();
            try
            {
                var saved = await ctx.RunSettingReceiptAsync(MusicSettingReceiptKeys.SearchPolicy,
                    ct => ctx.Application.SaveSearchPolicyAsync(revision, songsRecord, playlistsRecord, ct), ctx.Lifetime.Token);
                if (!SearchPreferencesCurrent(generation)) return;
                PublishSearchPreferences(saved);
                searchPreferenceStatus = "";
            }
            catch (MusicOperationException error) when (error.Code is
                "COMMIT_UNKNOWN" or "STORAGE_CONFLICT" or "PENDING_WRITE" or "REVISION_CONFLICT")
            {
                if (!SearchPreferencesCurrent(generation)) return;
                try
                {
                    var current = await ctx.RunSettingReceiptAsync(MusicSettingReceiptKeys.SearchPolicy,
                        ct => ctx.Application.GetSearchPolicyAsync(null, ct), ctx.Lifetime.Token);
                    if (!SearchPreferencesCurrent(generation)) return;
                    PublishSearchPreferences(current);
                    searchPreferenceStatus = Matches(SongSearch, songs) && Matches(PlaylistSearch, playlists)
                        ? ""
                        : error.Code == "REVISION_CONFLICT"
                            ? "搜索设置已更新"
                            : "搜索设置保存失败";
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    if (!SearchPreferencesCurrent(generation)) return;
                    songSearchLoaded = playlistSearchLoaded = false;
                    searchPreferenceStatus = "搜索设置保存失败";
                    throw;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                if (!SearchPreferencesCurrent(generation)) return;
                searchPreferenceStatus = "搜索设置保存失败";
                throw;
            }
            finally
            {
                searchPreferencesBusy = false;
                if (SearchPreferencesCurrent(generation)) SearchPreferenceBindings();
            }
        }, publishStatus: false);
    }

    internal async Task ClearSearchHistoryAsync()
    {
        if (!ctx.CanEdit || !HasSearchHistory || !await ctx.ConfirmAsync("清空搜索历史？", "搜索历史", ctx.Lifetime.Token))
            return;
        if (ctx.Stopping || ctx.Disposed || !ctx.CanEdit || !HasSearchHistory) return;
        long revision = searchHistoryRevision;
        await ctx.RunAsync(async () => PublishSearchHistory(
            await ctx.Application.ClearSearchHistoryAsync(revision, confirm: true, ctx.Lifetime.Token)));
    }

    internal async Task RecordSearchHistoryAsync(string text, string kind, CancellationToken token)
    {
        try
        {
            PublishSearchHistory(await ctx.Application.RecordSearchHistoryAsync(text, kind, token));
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            searchPreferenceStatus = "搜索历史保存失败";
            SearchPreferenceBindings();
        }
    }

    internal string[] SearchDispatchPlatforms(string kind) =>
        (kind == "songs" ? SongSearch : PlaylistSearch).Snapshot().Enabled;

    // Metadata policy methods
    internal async Task ReadMetadataSettingsAsync(CancellationToken token)
    {
        long generation = metadataGeneration;
        var settings = await ctx.RunSettingReceiptAsync(MusicSettingReceiptKeys.MetadataPolicy,
            ct => ctx.Application.GetMetadataPolicyAsync(ct), token);
        if (generation != metadataGeneration || ctx.Stopping || ctx.Disposed) return;
        PublishMetadataSettings(settings);
        if (generation == metadataGeneration && !ctx.Stopping && !ctx.Disposed &&
            MetadataArtworkRefreshRequested is { } refresh)
            await refresh(token);
    }

    private void PublishMetadataSettings(MusicMetadataPolicyRecord settings)
    {
        metadataRevision = settings.Revision;
        var enabled = settings.Enabled.ToHashSet(StringComparer.Ordinal);
        var platforms = settings.Platforms;
        metadataOrder = platforms;
        metadataEnabled = settings.Enabled;
        platforms = platforms.Concat(MusicLibraryOwner.MetadataPlatforms.Except(platforms)).ToArray();
        MusicSettingsController.ReconcilePriorityRows(MetadataPlatforms, platforms, row => row.Platform,
            platform => new(platform, enabled.Contains(platform)),
            (row, platform) => row.Apply(enabled.Contains(platform)));
        metadataLoaded = true;
        notify(nameof(MusicSettingsController.CanEditMetadata));
    }

    private bool MetadataSettingsMatch(string[] platforms, string[] enabled) =>
        metadataOrder.SequenceEqual(platforms) && metadataEnabled.SequenceEqual(enabled);

    internal Task SetMetadataEnabledAsync(MusicPlatformPriorityRow row, bool enabled)
    {
        if (!CanEditMetadata || !MetadataPlatforms.Contains(row) || row.Enabled == enabled)
            return Task.CompletedTask;
        var order = enabled && !metadataOrder.Contains(row.Platform) ? metadataOrder.Append(row.Platform).ToArray() : metadataOrder;
        var active = MetadataPlatforms.Where(value => value.Enabled)
            .Select(value => value.Platform).ToHashSet(StringComparer.Ordinal);
        if (enabled) active.Add(row.Platform); else active.Remove(row.Platform);
        var selected = metadataEnabled.Where(active.Contains).ToList();
        if (enabled && !selected.Contains(row.Platform)) selected.Add(row.Platform);
        return SaveMetadataSettingsAsync(order, selected.ToArray());
    }

    internal Task MoveMetadataPlatformAsync(MusicPlatformPriorityRow source, MusicPlatformPriorityRow target)
    {
        if (!CanEditMetadata || ReferenceEquals(source, target) ||
            !MetadataPlatforms.Contains(source) || !MetadataPlatforms.Contains(target))
            return Task.CompletedTask;
        var order = MetadataPlatforms.Select(row => row.Platform).ToList();
        int from = order.IndexOf(source.Platform), to = order.IndexOf(target.Platform);
        if (from < 0 || to < 0) return Task.CompletedTask;
        order.RemoveAt(from);
        order.Insert(to, source.Platform);
        var active = MetadataPlatforms.Where(row => row.Enabled)
            .Select(row => row.Platform).ToHashSet(StringComparer.Ordinal);
        return SaveMetadataSettingsAsync(order.ToArray(), order.Where(active.Contains).ToArray());
    }

    private Task SaveMetadataSettingsAsync(string[] platforms, string[] enabled)
    {
        if (!CanEditMetadata) return Task.CompletedTask;
        long revision = metadataRevision;
        long generation = metadataGeneration;
        metadataBusy = true;
        notify(nameof(MusicSettingsController.CanEditMetadata));
        MetadataPolicyChanging?.Invoke();
        return metadataWork = ctx.RunAsync(async () =>
        {
            try
            {
                var saved = await ctx.RunSettingReceiptAsync(MusicSettingReceiptKeys.MetadataPolicy,
                    ct => ctx.Application.SaveMetadataPolicyAsync(revision, platforms, enabled, ct), ctx.Lifetime.Token);
                if (generation != metadataGeneration || ctx.Stopping || ctx.Disposed) return;
                PublishMetadataSettings(saved);
                if (generation == metadataGeneration && !ctx.Stopping && !ctx.Disposed &&
                    MetadataArtworkRefreshRequested is { } refresh)
                    await refresh(ctx.Lifetime.Token);
            }
            catch
            {
                if (generation != metadataGeneration || ctx.Stopping || ctx.Disposed) return;
                try
                {
                    await ReadMetadataSettingsAsync(ctx.Lifetime.Token);
                    if (generation != metadataGeneration || ctx.Stopping || ctx.Disposed) return;
                    if (MetadataSettingsMatch(platforms, enabled)) return;
                }
                catch
                {
                    if (generation != metadataGeneration || ctx.Stopping || ctx.Disposed) return;
                    metadataLoaded = false;
                    throw;
                }
                throw;
            }
            finally
            {
                metadataBusy = false;
                if (generation == metadataGeneration && !ctx.Stopping && !ctx.Disposed)
                    notify(nameof(MusicSettingsController.CanEditMetadata));
                if (!ctx.Stopping && !ctx.Disposed && MetadataPolicyApplied is { } applied)
                    await applied();
            }
        });
    }
}
