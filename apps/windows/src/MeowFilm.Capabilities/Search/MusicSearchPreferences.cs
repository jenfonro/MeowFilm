using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace MeowFilm.Capabilities;

public sealed class MusicSearchPlatformRow(string platform, bool enabled) : INotifyPropertyChanged
{
    private bool isEnabled = enabled;
    public string Platform { get; } = platform;
    public string Label => new MusicMetadataPlatformRow(Platform, false).Label;
    public bool Enabled => isEnabled;
    internal void Apply(bool value)
    {
        if (isEnabled == value) return;
        isEnabled = value;
        PropertyChanged?.Invoke(this, new(nameof(Enabled)));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class MusicSearchKindSettings(string kind) : INotifyPropertyChanged
{
    private int pages = 1, concurrency = 1, cacheMinutes = 60;
    public string Kind { get; } = kind;
    public int Pages => pages;
    public int Concurrency => concurrency;
    public int CacheMinutes => cacheMinutes;
    public ObservableCollection<MusicSearchPlatformRow> Platforms { get; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Apply(JsonElement value)
    {
        int nextPages = MusicDto.Count(value, "pages"), nextConcurrency = MusicDto.Count(value, "concurrency"),
            nextCache = MusicDto.Count(value, "cacheMinutes");
        var enabled = value.GetProperty("enabled").EnumerateArray()
            .Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
        var order = value.GetProperty("order").EnumerateArray()
            .Select(item => item.GetString()!).ToArray();
        MusicDto.Require(nextPages is >= 1 and <= MusicSearchLimits.Pages &&
            nextConcurrency is >= 1 and <= MusicSearchLimits.Concurrency &&
            nextCache is >= 1 and <= MusicSearchLimits.CacheMinutes &&
            order.Length == 5 && order.Distinct(StringComparer.Ordinal).Count() == 5 &&
            order.All(key => key is "wy" or "tx" or "kw" or "kg" or "mg"));
        var existing = Platforms.ToDictionary(row => row.Platform, StringComparer.Ordinal);
        for (var index = 0; index < order.Length; index++)
        {
            var key = order[index];
            if (!existing.TryGetValue(key, out var row))
                row = new(key, enabled.Contains(key));
            else
                row.Apply(enabled.Contains(key));
            var currentIndex = Platforms.IndexOf(row);
            if (currentIndex < 0) Platforms.Insert(index, row);
            else if (currentIndex != index) Platforms.Move(currentIndex, index);
        }
        while (Platforms.Count > order.Length) Platforms.RemoveAt(Platforms.Count - 1);
        pages = nextPages; concurrency = nextConcurrency; cacheMinutes = nextCache;
        Changed(nameof(Pages));
        Changed(nameof(Concurrency));
        Changed(nameof(CacheMinutes));
    }

    internal SearchKindState Snapshot() => new(
        Platforms.Select(row => row.Platform).ToArray(),
        Platforms.Where(row => row.Enabled).Select(row => row.Platform).ToArray(),
        pages, concurrency, cacheMinutes);

    private void Changed([CallerMemberName] string name = "") =>
        PropertyChanged?.Invoke(this, new(name));
}

/// <summary>Accepted ranges of the per-kind advanced search settings.</summary>
public static class MusicSearchLimits
{
    public const int Pages = Library.MusicLibraryOwner.MaxSearchPages;
    public const int Concurrency = Library.MusicLibraryOwner.MaxSearchConcurrency;
    public const int CacheMinutes = Library.MusicLibraryOwner.MaxSearchCacheMinutes;
}

internal sealed record SearchKindState(string[] Order, string[] Enabled, int Pages, int Concurrency, int CacheMinutes)
{
    internal object Request => new { order = Order, enabled = Enabled, pages = Pages, concurrency = Concurrency, cacheMinutes = CacheMinutes };
}

public sealed partial class MusicCapability
{
    [Flags]
    private enum SearchHistorySurface
    {
        None = 0,
        Settings = 1,
        Discovery = 2
    }

    private bool searchPreferencesLoaded, searchPreferencesBusy, searchHistoryLoaded;
    private int searchHistoryCount;
    private long searchPreferenceRevision, searchHistoryRevision, searchSubmission;
    private long searchHistoryEpoch;
    private SearchHistorySurface searchHistorySurfaces;
    private CancellationTokenSource? searchHistoryRequest;
    private Task searchHistoryTail = Task.CompletedTask;
    private string searchPreferenceStatus = "";
    public MusicSearchKindSettings SongSearch { get; } = new("songs");
    public MusicSearchKindSettings PlaylistSearch { get; } = new("playlists");
    public bool CanEditSearchPreferences => CanEdit && searchPreferencesLoaded && !searchPreferencesBusy;
    public bool HasSearchHistory => searchHistoryCount > 0;
    public string SearchPreferenceStatus => searchPreferenceStatus;

    private async Task ReadSearchPreferences(CancellationToken token)
    {
        try
        {
            var reply = await Call("music.search.settings.get", cancellationToken: token);
            PublishSearchPreferences(reply.GetProperty("settings"));
            searchPreferenceStatus = "";
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            searchPreferencesLoaded = false;
            searchPreferenceStatus = "搜索设置不可用";
            throw;
        }
        finally
        {
            SearchPreferenceBindings();
        }
    }

    private void PublishSearchPreferences(JsonElement value)
    {
        searchPreferenceRevision = MusicDto.Integer(value, "revision");
        SongSearch.Apply(value.GetProperty("songs"));
        PlaylistSearch.Apply(value.GetProperty("playlists"));
        searchPreferencesLoaded = true;
        SearchPreferenceBindings();
    }

    private void PublishSearchHistory(JsonElement value)
    {
        long revision = MusicDto.Integer(value, "revision");
        var rows = value.GetProperty("items").EnumerateArray().ToArray();
        MusicDto.Require(rows.Length <= 60);
        foreach (var item in rows)
        {
            _ = MusicDto.Text(item, "text");
            _ = MusicDto.Text(item, "kind");
        }
        if (searchHistoryLoaded && revision < searchHistoryRevision) return;
        searchHistoryRevision = revision;
        searchHistoryCount = rows.Length;
        searchHistoryLoaded = true;
        Changed(nameof(HasSearchHistory));
    }

    private Task ActivateSearchHistoryAsync(SearchHistorySurface surface)
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
                var reply = await application.ExecuteAsync("music.search.history.get", new { }, request.Token);
                request.Token.ThrowIfCancellationRequested();
                reply = ValidateReply("music.search.history.get", reply);
                if (!stopping && !disposed && epoch == searchHistoryEpoch &&
                    searchHistorySurfaces != SearchHistorySurface.None)
                    PublishSearchHistory(reply.GetProperty("history"));
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

    private Task DeactivateSearchHistoryAsync(SearchHistorySurface surface)
    {
        searchHistorySurfaces &= ~surface;
        if (searchHistorySurfaces != SearchHistorySurface.None) return Task.CompletedTask;
        searchHistoryEpoch++;
        searchHistoryRequest?.Cancel();
        return searchHistoryTail;
    }

    private Task RetireSearchHistoryAsync()
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
            pages is < 1 or > MusicSearchLimits.Pages || concurrency is < 1 or > MusicSearchLimits.Concurrency ||
            cacheMinutes is < 1 or > MusicSearchLimits.CacheMinutes) return Task.CompletedTask;
        var songs = SongSearch.Snapshot();
        var playlists = PlaylistSearch.Snapshot();
        var current = kind == "songs" ? songs : playlists;
        if (current.Pages == pages && current.Concurrency == concurrency && current.CacheMinutes == cacheMinutes)
            return Task.CompletedTask;
        var desired = current with { Pages = pages, Concurrency = concurrency, CacheMinutes = cacheMinutes };
        if (kind == "songs") songs = desired; else playlists = desired;
        return SaveSearchPreferencesAsync(songs, playlists);
    }

    public Task SetSearchPlatformEnabledAsync(string kind, MusicSearchPlatformRow row, bool enabled)
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
            return state with { Enabled = state.Order.Where(values.Contains).ToArray() };
        }
        if (kind == "songs") songs = Change(songs); else playlists = Change(playlists);
        return SaveSearchPreferencesAsync(songs, playlists);
    }

    public Task MoveSearchPlatformAsync(string kind, MusicSearchPlatformRow source, MusicSearchPlatformRow target)
    {
        if (!CanEditSearchPreferences || ReferenceEquals(source, target)) return Task.CompletedTask;
        var settings = kind == "songs" ? SongSearch : kind == "playlists" ? PlaylistSearch : null;
        if (settings is null || !settings.Platforms.Contains(source) || !settings.Platforms.Contains(target))
            return Task.CompletedTask;
        var songs = SongSearch.Snapshot();
        var playlists = PlaylistSearch.Snapshot();
        SearchKindState Move(SearchKindState state)
        {
            var order = state.Order.ToList();
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

    private static bool Matches(MusicSearchKindSettings current, SearchKindState desired) =>
        current.Pages == desired.Pages && current.Concurrency == desired.Concurrency &&
        current.CacheMinutes == desired.CacheMinutes &&
        current.Platforms.Select(row => row.Platform).SequenceEqual(desired.Order) &&
        current.Platforms.Where(row => row.Enabled).Select(row => row.Platform).SequenceEqual(desired.Enabled);

    private Task SaveSearchPreferencesAsync(SearchKindState songs, SearchKindState playlists)
    {
        if (!CanEditSearchPreferences) return Task.CompletedTask;
        var request = new
        {
            ifRevision = searchPreferenceRevision,
            songs = songs.Request,
            playlists = playlists.Request
        };
        searchPreferencesBusy = true;
        SearchPreferenceBindings();
        return RunAsync(async () =>
        {
            await RetireSearchAsync();
            if (IsSearchPage)
                ClearSearchVisibleSnapshot();
            try
            {
                PublishSearchPreferences((await Call("music.search.settings.save", request, lifetime.Token))
                    .GetProperty("settings"));
                searchPreferenceStatus = "";
            }
            catch (MusicOperationException error) when (error.Code is
                "COMMIT_UNKNOWN" or "STORAGE_CONFLICT" or "PENDING_WRITE" or "REVISION_CONFLICT")
            {
                try
                {
                    PublishSearchPreferences((await Call("music.search.settings.get",
                        cancellationToken: lifetime.Token)).GetProperty("settings"));
                    searchPreferenceStatus = Matches(SongSearch, songs) && Matches(PlaylistSearch, playlists)
                        ? ""
                        : error.Code == "REVISION_CONFLICT"
                            ? "搜索设置已更新"
                            : "搜索设置保存失败";
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    searchPreferencesLoaded = false;
                    searchPreferenceStatus = "搜索设置保存失败";
                    throw;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                searchPreferenceStatus = "搜索设置保存失败";
                throw;
            }
            finally
            {
                searchPreferencesBusy = false;
                SearchPreferenceBindings();
            }
        });
    }

    public Task ClearSearchHistoryAsync()
    {
        if (!CanEdit || !HasSearchHistory || !dialogs.Confirm("清空搜索历史？", "搜索历史"))
            return Task.CompletedTask;
        long revision = searchHistoryRevision;
        return RunAsync(async () => PublishSearchHistory((await Call("music.search.history.clear",
            new { ifRevision = revision, confirm = true }, lifetime.Token)).GetProperty("history")));
    }

    private async Task RecordSearchHistory(string text, bool online, CancellationToken token)
    {
        try
        {
            PublishSearchHistory((await Call("music.search.history.record", new
            {
                text,
                kind = online ? CatalogSearchKind == "歌单" ? "playlists" : "songs" : "local"
            }, token)).GetProperty("history"));
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            searchPreferenceStatus = "搜索历史保存失败";
            SearchPreferenceBindings();
        }
    }

    private string[] SearchDispatchPlatforms(string kind) =>
        (kind == "songs" ? SongSearch : PlaylistSearch).Platforms
            .Where(row => row.Enabled).Select(row => row.Platform).ToArray();
}
