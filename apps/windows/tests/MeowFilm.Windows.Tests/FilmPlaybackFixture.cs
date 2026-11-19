using System.ComponentModel;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop.Artwork;
using MeowFilm.Media;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

// Explicit isolated wire fixtures. No product composition, source account, HTTP or libVLC.
internal static class FilmPlaybackFixture
{
    internal const string Assets = "ms-appx:///MeowFilm.Capabilities/Film/Presentation/Assets/";
    internal static readonly string[] Posters = ["poster-bad-kids.jpg", "poster-long-night.jpg", "poster-day-and-night.jpg",
        "poster-burning-ice.jpg", "poster-reset.jpg", "poster-knockout.jpg", "poster-long-season.jpg"];
    internal sealed class BundledArtworkLoader : IArtworkLoader
    {
        public async Task<IArtworkLease> AcquireAsync(string address, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = await global::Windows.Storage.StorageFile.GetFileFromApplicationUriAsync(new Uri(address, UriKind.Absolute));
            var bytes = await global::Windows.Storage.FileIO.ReadBufferAsync(file);
            cancellationToken.ThrowIfCancellationRequested();
            return new ArtworkLease(ArtworkRaster.Decode(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(bytes)));
        }

        private sealed class ArtworkLease(ArtworkPixels image) : IArtworkLease
        {
            public ArtworkPixels Image => image;
            public void Dispose() { }
        }
    }

    internal static FilmCardItem Card(string kind = "tv", int number = 100, string? title = null, string? poster = null)
    {
        var target = new FilmPlaybackTarget($"tmdb:{kind}:{number}", kind, title ?? $"契约样例 {number}", "tmdb", number.ToString());
        string image = poster ?? Assets + Posters[6];
        return new(target.Id, target.Title, image, "9.4") { PlaybackContext = new(target, Metadata: Metadata(target, image)) };
    }
    internal static FilmCardItem RefreshMetadata(FilmCardItem item)
    {
        var target = item.PlaybackContext!.Target with { Title = item.Title };
        return item with { PlaybackContext = item.PlaybackContext with { Target = target, Metadata = Metadata(target, item.Poster) } };
    }
    internal static JsonElement Metadata(FilmPlaybackTarget target, string poster)
    {
        string metadataKind = target.Kind == "movie" ? "movie" : "tv";
        var episodes = Enumerable.Range(1, 12).Select(index => new
        {
            businessId = $"{target.Id}:season:1:episode:{index}",
            playbackTarget = (target with { Season = 1, Episode = index, GlobalEpisode = index }).Wire(),
            season = 1, episode = index, globalEpisode = index, name = $"契约单集 {index}",
            overview = string.Concat(Enumerable.Repeat($"第{index}集的独立简介，保留完整显示内容。", 12)),
            still_path = Assets + $"episode-{index:00}.jpg", runtime = 45, air_date = "2023-01-01"
        }).ToArray();
        object[] seasons = target.Kind == "movie" ? [] : [new
        {
            businessId = target.Id + ":season:1", playbackTarget = (target with { Season = 1, Episode = null }).Wire(),
            season_number = 1, name = "第一季", episodes
        }];
        return FilmJson.Encode(new
        {
            title = target.Title, name = target.Title, poster_path = poster, year = "2023",
            genres = new[] { "剧情", "悬疑" }, countries = new[] { "中国大陆" }, directors = new[] { new { name = "契约导演" } },
            actors = new[] { new { name = "契约演员" } }, runtime = 107,
            overview = string.Concat(Enumerable.Repeat("这是一份明确注入的离线资料契约，不是产品默认影片。", 90)),
            seasons,
            similar = new { results = Enumerable.Range(201, 6).Select((id, index) =>
                new { id, businessId = $"tmdb:{metadataKind}:{id}",
                    playbackTarget = new FilmPlaybackTarget($"tmdb:{metadataKind}:{id}", metadataKind, $"关联样例 {id}", "tmdb", id.ToString()).Wire(),
                    name = $"关联样例 {id}", title = $"关联样例 {id}", poster_path = Assets + Posters[index], vote_average = 8.1 }).ToArray() }
        });
    }
    internal static JsonElement Snapshot(Guid id, long sequence, FilmPlaybackTarget target, JsonElement metadata,
        JsonElement? attempt = null, JsonElement? current = null, JsonElement? browsing = null, JsonElement? candidates = null,
        FilmPlaybackTarget? entryTarget = null, string? entrySurface = null, string? entryResolution = null)
    {
        JsonElement[] episodes = target.Kind == "movie" ? [] : FilmJson.Array(metadata, "seasons").SelectMany(row => FilmJson.Array(row, "episodes")).ToArray();
        object[] metadataSources = target.MetadataProvider is null ? [] : [new
        { id = target.MetadataProvider + ":" + target.MetadataId, provider = target.MetadataProvider, metadataId = target.MetadataId, kind = target.Kind, title = target.Title }];
        return FilmJson.Encode(new
        {
            sessionId = id.ToString(), sequence, target = target.Wire(), entryTarget = (entryTarget ?? target).Wire(),
            entrySurface, entryResolution, stage = "idle", error = (string?)null, metadata,
            metadataSources,
            workInfo = target.MetadataProvider is not null && target.Kind is ("movie" or "tv") &&
                metadata.ValueKind == JsonValueKind.Object ? WorkInfo(target, metadata) : JsonSerializer.SerializeToElement<object?>(null),
            metadataSourceId = target.MetadataProvider is null ? null : target.MetadataProvider + ":" + target.MetadataId,
            sources = Array.Empty<object>(), search = Array.Empty<object>(),
            browsing = browsing ?? FilmJson.Encode(new { sourceId = (string?)null, lineId = (string?)null, season = target.Season ?? 1,
                loading = false, error = (string?)null, lines = Array.Empty<object>(), episodes = Array.Empty<object>(),
                originalEpisodes = Array.Empty<object>() }),
            episodes, candidates = candidates ?? FilmJson.Encode(Array.Empty<object>()), attempt, current,
            searchComplete = false, preferences = FilmPreferences.Defaults, shortVideo = false, previewEpisodes = false
        });
    }
    internal static JsonElement WorkInfo(FilmPlaybackTarget target, JsonElement metadata) => JsonSerializer.SerializeToElement(new
    {
        provider = target.MetadataProvider, metadataId = target.MetadataId, kind = target.Kind,
        title = target.Title, summary = FilmJson.Text(metadata, "overview") is { Length: > 0 } overview ? overview : FilmJson.Text(metadata, "intro"),
        seasonCount = target.Kind == "tv" ? (int?)FilmJson.Array(metadata, "seasons").Length : null,
        episodeCount = target.Kind == "tv" ? (int?)FilmJson.Array(metadata, "seasons").Sum(row => FilmJson.Array(row, "episodes").Length) : null,
        metadata
    });
    internal static JsonElement Candidate(string id, int? episode = 1, object? detail = null, object? line = null, string quality = "1080p") =>
        FilmJson.Encode(new { id, businessId = "fixture:file:" + id,
            playbackTarget = new FilmPlaybackTarget("fixture:source", "source", $"资源 {id}", Source: FilmJson.Encode(new
            { key = "cat:fixture:site:1", engine = "cat", installationId = "fixture", siteId = "site",
                detailId = detail ?? 0, lineId = line ?? 0, candidateId = id })).Wire(),
            name = $"资源 {id}", path = $"/season/第{episode}集.mp4", quality, provider = "quark",
            lineId = line ?? 0, season = episode is null ? (int?)null : 1, episode, globalEpisode = episode,
            source = new { key = "cat:fixture:site:1", engine = "cat", installationId = "fixture", siteId = "site",
                generation = 1, name = "契约来源", detailId = detail ?? 0 } });
    internal static async Task Until(Func<bool> condition)
    {
        var limit = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= limit) throw new InvalidOperationException("Synthetic playback did not settle");
            await Task.Delay(10);
        }
    }
    internal static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    internal static async Task Error(string code, Func<Task> action)
    {
        try { await action(); }
        catch (FilmPlaybackException error) when (error.Code == code) { return; }
        catch (BridgeException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected fixed failure " + code);
    }
}

internal sealed class FilmMemoryStore : NativeTestDatabase
{
    internal bool FailWrite;
    internal byte[]? Data
    {
        get => Project(MeowFilm.Capabilities.FilmStorageRecords.Favorites, "device") is { } value
            ? JsonSerializer.SerializeToUtf8Bytes(value) : null;
        set => Seed(MeowFilm.Capabilities.FilmStorageRecords.Favorites,
            System.Text.Json.Nodes.JsonNode.Parse(value!)!.AsObject(), "device");
    }
    public override Task<T> WriteAsync<T>(Func<DatabaseSession, T> write, CancellationToken token = default,
        int retainedBytes = 0, Action<Action>? commit = null) =>
        FailWrite ? throw new DocumentStoreException(DocumentStoreError.Storage) : base.WriteAsync(write, token, retainedBytes, commit);
}

internal sealed class FilmRuntimeProbe : IRuntimeSession, IRuntimeConnectionSource, IRuntimeSettingsNotifications
{
    private readonly object gate = new();
    internal readonly CancellationTokenSource Owner = new();
    private readonly List<(string Method, JsonElement Parameters)> calls = [];
    internal IReadOnlyList<(string Method, JsonElement Parameters)> Calls { get { lock (gate) return calls.ToArray(); } }
    internal readonly List<string> Notifications = [];
    public void SettingsChanged(string block) => Notifications.Add(block);
    internal readonly ConcurrentDictionary<string, JsonElement> Receipts = new(StringComparer.Ordinal);
    private readonly HashSet<string> released = new(StringComparer.Ordinal);
    internal IReadOnlySet<string> Released { get { lock (gate) return new HashSet<string>(released, StringComparer.Ordinal); } }
    internal Func<string, JsonElement, CancellationToken, Task<JsonElement>>? Intercept;
    internal Func<Guid, JsonElement, JsonElement>? PlaybackOpen;
    private JsonElement value;
    internal JsonElement Value { get { lock (gate) return value; } set { lock (gate) this.value = value; } }
    internal bool Connected = true, Controlled = true;
    private long sequence;
    private string pendingId = "", currentId = "";
    private Channel<JsonElement>? events;
    internal Guid SessionId;
    internal FilmPlaybackTarget Target = FilmPlaybackFixture.Card().PlaybackContext!.Target with { Season = 1, Episode = 1, GlobalEpisode = 1 };
    internal JsonElement Metadata;
    internal JsonElement Candidates = FilmJson.Encode(Array.Empty<object>());
    internal FilmRuntimeProbe() => Reset();
    internal void Reset()
    {
        lock (gate)
        {
            SessionId = Guid.NewGuid(); sequence = 0; pendingId = ""; currentId = "";
            Metadata = FilmPlaybackFixture.Metadata(Target, FilmPlaybackFixture.Assets + FilmPlaybackFixture.Posters[6]);
            Value = FilmPlaybackFixture.Snapshot(SessionId, ++sequence, Target, Metadata, candidates: Candidates);
        }
    }
    public bool IsConnected => Connected && !Owner.IsCancellationRequested;
    public Task<RuntimeConnection> CaptureConnectionAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!IsConnected) throw new BridgeException("CLOSED");
        return Task.FromResult(new RuntimeConnection(this, new("http://127.0.0.1:41002/"), Owner.Token));
    }
    internal static JsonElement Ok(JsonElement value) => FilmJson.Encode(new { ok = true, contract = "film.playback.v1", value });
    internal static JsonElement Failure(string code) => FilmJson.Encode(new { ok = false, contract = "film.playback.v1", code });
    public async Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var input = parameters is JsonElement value ? value.Clone() : FilmJson.Encode(parameters ?? new { });
        lock (gate) calls.Add((method, input));
        if (Intercept is { } intercept) return await intercept(method, input, token);
        return Handle(method, input);
    }
    internal JsonElement Handle(string method, JsonElement input)
    {
        lock (gate) return HandleCore(method, input);
    }
    private JsonElement HandleCore(string method, JsonElement input)
    {
        switch (method)
        {
            case "film.session.open":
                Target = FilmJson.Target(FilmJson.Member(input, "target"));
                if (Target.Kind == "tv" && Target.Episode is null) Target = Target with { Season = 1, Episode = 1, GlobalEpisode = 1 };
                Metadata = FilmJson.Member(input, "metadata").ValueKind == JsonValueKind.Object
                    ? input.GetProperty("metadata").Clone() : FilmPlaybackFixture.Metadata(Target, "");
                SessionId = Guid.NewGuid(); sequence = 0;
                pendingId = ""; currentId = "";
                var visibleCandidates = Target.Kind == "movie" && FilmJson.Array(Candidates).Length == 0
                    ? FilmJson.Encode(new[] { FilmPlaybackFixture.Candidate("movie-file", episode: null) }) : Candidates;
                Value = PlaybackOpen?.Invoke(SessionId, input) ??
                    FilmPlaybackFixture.Snapshot(SessionId, ++sequence, Target, Metadata, candidates: visibleCandidates);
                sequence = FilmJson.Member(Value, "sequence").GetInt64();
                return Ok(Value);
            case "film.session.snapshot": return Ok(Value);
            case "film.session.release":
                if (!FilmJson.Bool(input, "keepMedia")) events?.Writer.TryComplete();
                return Ok(FilmJson.Encode(FilmJson.Bool(input, "keepMedia") ? new { retained = true } : (object)new { released = true }));
            case "film.session.media.release":
            {
                string attempt = FilmJson.RequiredText(input, "attemptId");
                if (!Receipts.TryGetValue(attempt, out var receipt) ||
                    FilmJson.Text(receipt, "id") != FilmJson.Text(input, "resourceId")) return Failure("STALE_SESSION");
                lock (gate) released.Add(attempt);
                Change(root =>
                {
                    if (root["attempt"]?["id"]?.GetValue<string>() == attempt) root["attempt"] = null;
                    if (root["current"]?["attemptId"]?.GetValue<string>() == attempt) root["current"] = null;
                });
                return Ok(FilmJson.Encode(new { released = true }));
            }
            case "film.session.attempt":
            {
                string attempt = FilmJson.RequiredText(input, "attemptId");
                return Released.Contains(attempt) || !Receipts.TryGetValue(attempt, out var receipt) ? Failure("STALE_SESSION") : Ok(receipt);
            }
            case "film.session.command":
                return Command(input);
            case "film.settings.get": return Ok(FilmJson.Encode(FilmPreferences.Defaults));
            case "film.settings.set": return Ok(input);
            case "film.library.get": return Ok(FilmJson.Encode(new { history = Array.Empty<object>(), manual = Array.Empty<object>(), blocks = Array.Empty<object>() }));
            case "film.manual.update": case "film.library.remove": return Ok(FilmJson.Encode(new { }));
            default: return Failure("METHOD_UNAVAILABLE");
        }
    }
    private JsonElement Command(JsonElement input)
    {
        string action = FilmJson.RequiredText(input, "action"), attempt = FilmJson.Text(input, "attemptId");
        if (action == "media.started")
        {
            if (Released.Contains(attempt)) return Failure("STALE_SESSION");
            var pending = FilmJson.Member(Value, "attempt");
            if (pendingId != attempt || FilmJson.Text(pending, "id") != attempt) return Failure("STALE_SESSION");
            currentId = attempt; pendingId = "";
            Change(root =>
            {
                root["current"] = JsonNode.Parse(FilmJson.Encode(new
                { attemptId = attempt, candidate = pending.GetProperty("candidate"), position = 0d, duration = 600d, paused = false }).GetRawText());
                root["attempt"] = null; root["stage"] = "playing";
            });
        }
        else if (action is "media.failed" or "media.ended")
        {
            if (attempt != currentId && (action != "media.failed" || attempt != pendingId)) return Failure("STALE_SESSION");
            if (pendingId == attempt) pendingId = "";
            if (currentId == attempt) currentId = "";
            Change(root =>
            {
                root["stage"] = currentId.Length > 0 ? "playing" : action == "media.ended" ? "ended" : "unavailable";
                if (root["attempt"]?["id"]?.GetValue<string>() == attempt) root["attempt"] = null;
                if (root["current"]?["attemptId"]?.GetValue<string>() == attempt) root["current"] = null;
            });
        }
        else if (action == "media.progress" && currentId != attempt) return Failure("STALE_SESSION");
        else if (action == "select.target")
        {
            Target = FilmJson.Target(FilmJson.Member(input, "target"));
            Target.Validate();
            Change(root => root["target"] = JsonNode.Parse(FilmJson.Encode(Target.Wire()).GetRawText()));
        }
        else if (action == "browse")
        {
            Change(root =>
            {
                root["browsing"]!["sourceId"] = FilmJson.Text(input, "sourceId") is { Length: > 0 } source ? source : null;
                if (input.TryGetProperty("metadataSourceId", out var metadata))
                    root["metadataSourceId"] = metadata.ValueKind == JsonValueKind.Null ? null : metadata.GetString();
                if (FilmJson.Member(input, "lineId").ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                    root["browsing"]!["lineId"] = JsonNode.Parse(input.GetProperty("lineId").GetRawText());
                else if (FilmJson.Member(input, "sourceId").ValueKind == JsonValueKind.Null)
                    root["browsing"]!["lineId"] = null;
                if (input.TryGetProperty("season", out var season))
                    root["browsing"]!["season"] = season.GetInt32();
                root["browsing"]!["loading"] = false;
            });
        }
        else if (action == "browse.metadata")
        {
            Change(root =>
            {
                root["metadataSourceId"] = FilmJson.RequiredText(input, "metadataSourceId");
                if (input.TryGetProperty("sourceId", out var source))
                    root["browsing"]!["sourceId"] = source.ValueKind == JsonValueKind.Null ? null : source.GetString();
                else if (!FilmJson.Bool(input, "keepSource")) root["browsing"]!["sourceId"] = null;
                if (input.TryGetProperty("lineId", out var line)) root["browsing"]!["lineId"] = JsonNode.Parse(line.GetRawText());
                if (input.TryGetProperty("season", out var season)) root["browsing"]!["season"] = season.GetInt32();
                root["browsing"]!["loading"] = false;
            });
        }
        else if (action == "browse.season")
        {
            Change(root => { root["browsing"]!["season"] = input.GetProperty("season").GetInt32(); root["browsing"]!["loading"] = false; });
        }
        else if (action == "stop")
        {
            pendingId = ""; currentId = "";
            Change(root => { root["attempt"] = null; root["current"] = null; root["stage"] = "stopped"; });
        }
        return Ok(Value);
    }
    internal void Change(Action<JsonObject> action)
    {
        lock (gate)
        {
            var root = JsonNode.Parse(Value.GetRawText())!.AsObject();
            action(root); root["sequence"] = ++sequence;
            Value = JsonSerializer.SerializeToElement(root);
            events?.Writer.TryWrite(Value);
        }
    }
    internal void Attempt(string id, JsonElement candidate, bool prepareOnly = false, FilmPlaybackTarget? target = null)
    {
        lock (gate) AttemptCore(id, candidate, prepareOnly, target);
    }
    private void AttemptCore(string id, JsonElement candidate, bool prepareOnly, FilmPlaybackTarget? target)
    {
        if (target is not null) Target = target;
        string resource = Guid.NewGuid().ToString("N");
        var receipt = FilmJson.Encode(new
        {
            attemptId = id, id = resource, url = Controlled ? $"http://127.0.0.1:41001/media/{resource}/{new string('Z', 32)}" : "https://example.org/video.mp4",
            mediaOrigin = Controlled ? "http://127.0.0.1:41001/" : null, title = Target.Title,
            position = 0d, paused = false, scope = new { }
        });
        Receipts[id] = receipt;
        pendingId = id;
        Change(root =>
        {
            root["target"] = JsonNode.Parse(FilmJson.Encode(Target.Wire()).GetRawText());
            root["attempt"] = JsonNode.Parse(FilmJson.Encode(new { id, candidate, prepareOnly, position = 0d, paused = false }).GetRawText());
            root["candidates"] = JsonNode.Parse(FilmJson.Encode(new[] { candidate }).GetRawText());
            root["stage"] = prepareOnly ? "prepared" : "pending";
        });
    }
    public async IAsyncEnumerable<JsonElement> EventsAsync(string module, Guid id, long after = 0,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        var channel = Channel.CreateBounded<JsonElement>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (gate) events = channel;
        try { await foreach (var value in channel.Reader.ReadAllAsync(token)) yield return value; }
        finally { lock (gate) if (ReferenceEquals(events, channel)) events = null; }
    }
    internal void DisconnectEvents() { lock (gate) events?.Writer.TryComplete(); }
    public Task<JsonElement> UploadSourceAsync(string module, string installation, long revision, ReadOnlyMemory<byte> source,
        string? basename = null, CancellationToken token = default) => throw new BridgeException("METHOD_UNAVAILABLE");
    public Task<byte[]> ReadImageAsync(string reference, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Image requests are not part of this fixture.");
    public ValueTask DisposeAsync() { Connected = false; Owner.Cancel(); events?.Writer.TryComplete(); Owner.Dispose(); return ValueTask.CompletedTask; }
}

internal sealed class FilmPlaybackRig : IAsyncDisposable
{
    internal readonly FilmRuntimeProbe Runtime = new();
    internal readonly FilmNativeProbe Native;
    internal readonly FilmMemoryStore Store = new();
    internal readonly FilmLibrary Library;
    internal readonly FilmPlaybackSession Session;
    internal readonly FilmNativeSettingsFixture PreferencesStore = new();
    internal readonly FilmPlaybackSettings Preferences;
    internal Func<CancellationToken, Task<RuntimeConnection>>? Capture;
    internal FilmPlaybackPage Page { get; private set; } = null!;
    internal FilmPlaybackRig(FrameworkElement? view = null)
    {
        Native = new(view);
        Library = new(Store);
        Session = new(token => (Capture ?? Runtime.CaptureConnectionAsync)(token), Native);
        Preferences = new(Runtime, PreferencesStore);
    }
    internal async Task OpenAsync(FilmCardItem? item = null, FilmPlaybackRequest? request = null, CancellationToken token = default)
    {
        item ??= FilmPlaybackFixture.Card();
        request ??= item.PlaybackContext!;
        Page = new(item, request, Session, Library, Preferences);
        await Session.OpenAsync(Page, new
        {
            platform = "windows", entrySurface = request.EntrySurface, target = request.Target.Wire(),
            resumeTarget = request.ResumeTarget?.Wire(), sources = request.Sources ?? [],
            query = request.Query, results = request.Results, search = request.Search, metadata = request.Metadata,
            cacheEntries = request.CacheEntries, resultHints = request.ResultHints, searchReference = request.SearchReference
        }, token);
        await Page.LoadFavoriteAsync();
    }
    public async ValueTask DisposeAsync()
    {
        try { await Session.DisposeAsync(); }
        finally { await Library.DisposeAsync(); await Runtime.DisposeAsync(); await PreferencesStore.DisposeAsync(); }
    }
}

internal sealed class FilmNativeProbe(FrameworkElement? view = null) : IFilmNativeMedia
{
    public FrameworkElement View { get; } = view ?? new ContentControl();
    public NativePlaybackScope? Scope { get; private set; }
    public string? Identity { get; private set; }
    public bool HasOutput { get; private set; }
    private bool buffering;
    public bool Buffering => HasOutput && !Paused && buffering;
    public string? FailureCode { get; private set; }
    public bool Paused { get; private set; }
    public double Position { get; private set; }
    public double Duration { get; private set; } = 600;
    public int Volume { get; set; } = 80;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? Failed;
    public event EventHandler? Ended;
    internal readonly List<RuntimeMediaPlaybackIntent> Intents = [];
    internal readonly List<(RuntimeMediaPlaybackIntent Intent, FilmMediaReceipt Receipt, INativeMediaResourceLease? Lease)> Inputs = [];
    internal readonly List<(NativePlaybackScope Scope, string Command, object? Value)> Controls = [];
    internal Func<CancellationToken, Task>? Preparing;
    internal Func<CancellationToken, Task>? Committing;
    internal Func<Task>? Stopping;
    internal INativeMediaResourceLease? AcceptedLease, PendingLease;
    private RuntimeMediaPlaybackIntent? intent;
    private NativePlaybackScope? preparedFrom;
    private (RuntimeMediaPlaybackIntent Intent, string Code)? inputFailure;
    private long generation;
    internal int Commits, Stops, Discards;
    internal string? SettledIdentity, DiscardedIdentity;
    internal double Rate = 1;
    internal string? Aspect;
    public async Task PrepareAsync(RuntimeMediaPlaybackIntent value, FilmMediaReceipt receipt, INativeMediaResourceLease? lease, CancellationToken token)
    {
        Inputs.Add((value, receipt, lease));
        Intents.Add(value); intent = value; preparedFrom = Scope; PendingLease = lease; inputFailure = null;
        try
        {
            if (value.PreserveFrom is { } donor && donor != Scope)
                throw new PlaybackException(PlaybackError.Superseded);
            if (Preparing is { } prepare) await prepare(token);
            token.ThrowIfCancellationRequested();
        }
        catch
        {
            if (ReferenceEquals(intent, value)) { intent = null; PendingLease = null; }
            if (lease is not null) await lease.DisposeAsync();
            throw;
        }
    }
    public async Task<bool> TryCommitAsync(RuntimeMediaPlaybackIntent expected, Func<bool> canCommit,
        Action committed, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var value = intent ?? throw new PlaybackException(PlaybackError.Superseded);
        if (!ReferenceEquals(value, expected)) throw new PlaybackException(PlaybackError.Superseded);
        var old = AcceptedLease;
        var donorScope = preparedFrom;
        var preparedLease = PendingLease;
        void CheckPrepared()
        {
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(intent, expected) || !ReferenceEquals(PendingLease, preparedLease) ||
                Scope != donorScope || donorScope is not null && !HasOutput)
                throw new PlaybackException(PlaybackError.Superseded);
            if (preparedLease?.Revoked.IsCancellationRequested == true ||
                old?.Revoked.IsCancellationRequested == true)
                throw new FilmPlaybackException(PlaybackError.ResourceRevoked);
            if (inputFailure is { } failure && ReferenceEquals(failure.Intent, expected))
                throw new FilmPlaybackException(failure.Code);
        }
        CheckPrepared();
        if (Committing is { } committing) await committing(token);
        CheckPrepared();
        if (!canCommit()) return false;
        if (value.PreserveFrom is null) { Position = value.Position; Paused = value.Paused; }
        AcceptedLease = PendingLease; PendingLease = null; intent = null;
        Identity = value.Identity; Scope = new("film", value.Identity, ++generation); HasOutput = true; Commits++;
        SetBuffering(false);
        committed(); Pulse("Identity");
        if (old is not null) await old.DisposeAsync();
        SettledIdentity = value.Identity;
        return true;
    }
    public async Task DiscardAsync(RuntimeMediaPlaybackIntent expected)
    {
        if (!ReferenceEquals(intent, expected)) return;
        Discards++;
        if (PendingLease is { } lease) { PendingLease = null; await lease.DisposeAsync(); }
        if (ReferenceEquals(intent, expected)) intent = null;
        DiscardedIdentity = expected.Identity;
    }
    internal void FailPendingInput(string code) =>
        inputFailure = (intent ?? throw new InvalidOperationException("No prepared input to fail"), code);
    public Task PauseAsync(NativePlaybackScope scope, bool paused) { Require(scope); Controls.Add((scope, "pause", paused)); Paused = paused; Pulse("IsPaused"); return Task.CompletedTask; }
    public Task SeekAsync(NativePlaybackScope scope, double value) { Require(scope); Controls.Add((scope, "seek", value)); Position = value; Pulse("PositionSeconds"); return Task.CompletedTask; }
    public Task RateAsync(NativePlaybackScope scope, double value) { Require(scope); Controls.Add((scope, "rate", value)); Rate = value; return Task.CompletedTask; }
    public Task AspectAsync(NativePlaybackScope scope, string? value) { Require(scope); Controls.Add((scope, "aspect", value)); Aspect = value; return Task.CompletedTask; }
    public async Task StopAsync(NativePlaybackScope scope)
    {
        Require(scope); Stops++;
        if (Stopping is { } stop) await stop();
        HasOutput = false; Scope = null;
        SetBuffering(false);
        if (AcceptedLease is { } lease) { AcceptedLease = null; await lease.DisposeAsync(); }
        Identity = null; Pulse("Identity");
    }
    internal async Task TerminalAsync(bool ended)
    {
        long terminalGeneration = generation;
        FailureCode = ended ? null : PlaybackError.PlaybackFailed;
        HasOutput = false; Scope = null;
        SetBuffering(false);
        if (AcceptedLease is { } lease) { AcceptedLease = null; await lease.DisposeAsync(); }
        if (terminalGeneration != generation) return;
        if (ended) Ended?.Invoke(this, EventArgs.Empty); else Failed?.Invoke(this, EventArgs.Empty);
    }
    internal async Task RetireScopeAsync()
    {
        if (AcceptedLease is { } lease) { AcceptedLease = null; await lease.DisposeAsync(); }
        Identity = null; Scope = null; HasOutput = false;
        SetBuffering(false);
        Pulse(nameof(Scope));
        Pulse(nameof(Identity));
        Pulse(nameof(HasOutput));
    }
    internal void SetBuffering(bool value)
    {
        if (buffering == value) return;
        buffering = value;
        Pulse(nameof(FilmPlayback.IsBuffering));
    }
    internal void Pulse(string property) => PropertyChanged?.Invoke(this, new(property));
    private void Require(NativePlaybackScope scope) { if (Scope != scope) throw new FilmPlaybackException("STALE_SESSION"); }
}
