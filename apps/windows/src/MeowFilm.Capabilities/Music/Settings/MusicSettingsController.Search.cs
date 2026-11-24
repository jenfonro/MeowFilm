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

public sealed partial class MusicSettingsController
{
    private bool songSearchLoaded, playlistSearchLoaded, searchPreferencesBusy, searchHistoryLoaded;
    private bool searchPreferencesLoaded => songSearchLoaded && playlistSearchLoaded;
    internal bool SearchPreferencesLoaded(string kind) => kind == "songs" ? songSearchLoaded : playlistSearchLoaded;
    private int searchHistoryCount;
    private long searchPreferenceRevision, searchHistoryRevision;
    private long searchHistoryEpoch;
    private long searchPreferencesGeneration;
    private Task searchPreferencesWork = Task.CompletedTask;
    private SearchHistorySurface searchHistorySurfaces;
    private CancellationTokenSource? searchHistoryRequest;
    private Task searchHistoryTail = Task.CompletedTask;
    private string searchPreferenceStatus = "";

    public MusicSearchKindSettings SongSearch { get; } = new("songs");
    public MusicSearchKindSettings PlaylistSearch { get; } = new("playlists");
    public bool CanEditSearchPreferences => CanEdit && searchPreferencesLoaded && !searchPreferencesBusy;
    public bool HasSearchHistory => searchHistoryCount > 0;
    public string SearchPreferenceStatus => searchPreferenceStatus;

    private bool SearchPreferencesCurrent(long generation) =>
        generation == searchPreferencesGeneration && !stopping && !disposed;

    private void RetireSearchPreferencesSettings()
    {
        searchPreferencesGeneration++;
    }

    internal async Task<bool> EnsureSearchPreferencesLoadedAsync(string kind, CancellationToken token)
    {
        if (SearchPreferencesLoaded(kind)) return true;
        try { await ReadSearchPreferences(token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            DebugLog.Warn(DebugCategories.Search, "search-settings", "ensure-loaded-failed",
                new { kind, error = error.Message });
        }
        return SearchPreferencesLoaded(kind);
    }

    internal void CancelSearchHistoryForShutdown() => searchHistoryRequest?.Cancel();

    internal async Task ReadSearchPreferences(CancellationToken token)
    {
        long generation = searchPreferencesGeneration;
        Exception? failure = null;
        try
        {
            foreach (string kind in new[] { "songs", "playlists" })
            {
                try
                {
                    var settings = await RunSettingReceiptAsync(MusicSettingReceiptKeys.SearchPolicy,
                        ct => application.GetSearchPolicyAsync(kind, ct), token);
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
        Changed(nameof(HasSearchHistory));
    }

    internal Task ActivateSearchHistoryAsync(SearchHistorySurface surface)
    {
        searchHistorySurfaces |= surface;
        if (stopping || disposed || searchHistoryLoaded) return Task.CompletedTask;
        long epoch = searchHistoryEpoch;
        var previous = searchHistoryTail;
        return searchHistoryTail = Read();

        async Task Read()
        {
            await previous;
            if (stopping || disposed || searchHistoryLoaded || epoch != searchHistoryEpoch ||
                searchHistorySurfaces == SearchHistorySurface.None)
                return;
            using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            searchHistoryRequest = request;
            try
            {
                var history = await application.GetSearchHistoryAsync(request.Token);
                request.Token.ThrowIfCancellationRequested();
                if (!stopping && !disposed && epoch == searchHistoryEpoch &&
                    searchHistorySurfaces != SearchHistorySurface.None)
                    PublishSearchHistory(history);
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch
            {
                if (!stopping && !disposed && epoch == searchHistoryEpoch &&
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
        Changed(nameof(CanEditSearchPreferences));
        Changed(nameof(SearchPreferenceStatus));
    }

    /// <summary>Saves one kind's advanced settings. Out-of-range input is rejected, not clamped.</summary>
    public Task SetSearchAdvancedAsync(string kind, int pages, int concurrency, int cacheMinutes)
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

    public Task SetSearchPlatformEnabledAsync(string kind, MusicPlatformPriorityRow row, bool enabled)
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

    public Task MoveSearchPlatformAsync(string kind, MusicPlatformPriorityRow source, MusicPlatformPriorityRow target)
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
        return searchPreferencesWork = RunAsync(async () =>
        {
            await searchPort.RetireSearchAsync();
            if (searchPort.IsSearchPage)
                searchPort.ClearSearchVisibleSnapshot();
            try
            {
                var saved = await RunSettingReceiptAsync(MusicSettingReceiptKeys.SearchPolicy,
                    ct => application.SaveSearchPolicyAsync(revision, songsRecord, playlistsRecord, ct), lifetime.Token);
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
                    var current = await RunSettingReceiptAsync(MusicSettingReceiptKeys.SearchPolicy,
                        ct => application.GetSearchPolicyAsync(null, ct), lifetime.Token);
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

    public async Task ClearSearchHistoryAsync()
    {
        if (!CanEdit || !HasSearchHistory || !await ConfirmAsync("清空搜索历史？", "搜索历史", lifetime.Token))
            return;
        if (stopping || disposed || !CanEdit || !HasSearchHistory) return;
        long revision = searchHistoryRevision;
        await RunAsync(async () => PublishSearchHistory(
            await application.ClearSearchHistoryAsync(revision, confirm: true, lifetime.Token)));
    }

    internal async Task RecordSearchHistory(string text, bool online, CancellationToken token)
    {
        try
        {
            string kind = online ? catalogPort.CatalogSearchKind == "歌单" ? "playlists" : "songs" : "local";
            PublishSearchHistory(await application.RecordSearchHistoryAsync(text, kind, token));
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
}
