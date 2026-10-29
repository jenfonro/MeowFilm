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
    private string mode = "aggregate", platform = "wy";
    public string Kind { get; } = kind;
    public string Mode => mode;
    public string Platform => platform;
    public bool IsAggregate => mode == "aggregate";
    public ObservableCollection<MusicSearchPlatformRow> Platforms { get; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Apply(JsonElement value)
    {
        var nextMode = MusicDto.Text(value, "mode");
        var nextPlatform = MusicDto.Text(value, "platform");
        var enabled = value.GetProperty("enabled").EnumerateArray()
            .Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
        var order = value.GetProperty("order").EnumerateArray()
            .Select(item => item.GetString()!).ToArray();
        MusicDto.Require(nextMode is "aggregate" or "single" &&
            order.Length == 5 && order.Distinct(StringComparer.Ordinal).Count() == 5 &&
            order.All(key => key is "wy" or "tx" or "kw" or "kg" or "mg") &&
            order.Contains(nextPlatform, StringComparer.Ordinal));
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
        mode = nextMode;
        platform = nextPlatform;
        Changed(nameof(Mode));
        Changed(nameof(Platform));
        Changed(nameof(IsAggregate));
    }

    internal SearchKindState Snapshot() => new(mode, platform,
        Platforms.Select(row => row.Platform).ToArray(),
        Platforms.Where(row => row.Enabled).Select(row => row.Platform).ToArray());

    private void Changed([CallerMemberName] string name = "") =>
        PropertyChanged?.Invoke(this, new(name));
}

internal sealed record SearchKindState(string Mode, string Platform, string[] Order, string[] Enabled)
{
    internal object Request => new { mode = Mode, platform = Platform, order = Order, enabled = Enabled };
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
    public MusicCatalogChoice[] SearchModes { get; } =
        [new("aggregate", "聚合搜索"), new("single", "单平台搜索")];
    public MusicCatalogPlatform[] SearchPlatformChoices { get; } =
        [new("wy", "网易云音乐"), new("tx", "QQ 音乐"), new("kw", "酷我音乐"),
         new("kg", "酷狗音乐"), new("mg", "咪咕音乐")];
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
            _ = MusicDto.Text(item, "platform");
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

    public Task SetSearchModeAsync(string kind, string mode)
    {
        if (!CanEditSearchPreferences || mode is not ("aggregate" or "single")) return Task.CompletedTask;
        var songs = SongSearch.Snapshot();
        var playlists = PlaylistSearch.Snapshot();
        if (kind == "songs" && songs.Mode == mode || kind == "playlists" && playlists.Mode == mode)
            return Task.CompletedTask;
        if (kind == "songs") songs = songs with { Mode = mode };
        else if (kind == "playlists") playlists = playlists with { Mode = mode };
        else return Task.CompletedTask;
        return SaveSearchPreferencesAsync(songs, playlists);
    }

    public Task SetSearchPlatformAsync(string kind, string platform)
    {
        if (!CanEditSearchPreferences || !SearchPlatformChoices.Any(row => row.Key == platform))
            return Task.CompletedTask;
        var songs = SongSearch.Snapshot();
        var playlists = PlaylistSearch.Snapshot();
        if (kind == "songs" && songs.Platform == platform || kind == "playlists" && playlists.Platform == platform)
            return Task.CompletedTask;
        if (kind == "songs") songs = songs with { Platform = platform };
        else if (kind == "playlists") playlists = playlists with { Platform = platform };
        else return Task.CompletedTask;
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
        current.Mode == desired.Mode &&
        current.Platform == desired.Platform &&
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
            await RetireAggregateSearchAsync();
            if (IsAggregateSearch)
                ClearAggregateVisibleSnapshot();
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
                kind = online ? CatalogSearchKind == "歌单" ? "playlists" : "songs" : "local",
                platform = online ? "aggregate" : "local"
            }, token)).GetProperty("history"));
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            searchPreferenceStatus = "搜索历史保存失败";
            SearchPreferenceBindings();
        }
    }

    private string[] SearchDispatchPlatforms(string kind)
    {
        var settings = kind == "songs" ? SongSearch : PlaylistSearch;
        return settings.Mode == "single"
            ? [settings.Platform]
            : settings.Platforms.Where(row => row.Enabled).Select(row => row.Platform).ToArray();
    }
}
