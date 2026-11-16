using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

public sealed class FilmSourceService : IAsyncDisposable, IFilmSearchSources
{
    public const string DocumentNamespace = "film.video-sources.v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false };
    private readonly IDocumentStore store;
    private readonly CatScriptTransport transport;
    private readonly CatSourceSettings settings;
    private readonly IRuntimeRecovery? recovery;
    private readonly SemaphoreSlim stateGate = new(1, 1), managementGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource pending = new();
    private readonly object lifecycle = new();
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<FilmSourceKey, CancellationTokenSource> probes = [];
    private readonly HashSet<string> changingCats = new(StringComparer.Ordinal);
    private DocumentFingerprint fingerprint = DocumentFingerprint.Missing;
    private StoredState document = new(1, [], [], []);
    private JsonElement rawDocument;
    private Snapshot snapshot = new([], []);
    private bool initialized, closed, preparing;
    private string? runtimeError;
    private int active;
    private Task? closing;
    private TaskCompletionSource? preparationDrained;

    public FilmSourceService(IRuntimeClient runtime, IDocumentStore store)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(store);
        Runtime = runtime;
        transport = new(runtime);
        settings = new(store);
        recovery = runtime as IRuntimeRecovery;
        this.store = store;
    }

    internal IRuntimeClient Runtime { get; }
    internal IDocumentStore Storage => store;
    public IReadOnlyList<FilmCatSource> Cats => Volatile.Read(ref snapshot).Cats;
    public IReadOnlyList<FilmVideoSource> Videos => Volatile.Read(ref snapshot).Videos;
    public string? RuntimeError => Volatile.Read(ref runtimeError);
    public event EventHandler? Changed;

    public Task<IReadOnlyList<FilmSearchSource>> ReadSearchSourcesAsync(CancellationToken token) =>
        RunAsync(async ct =>
        {
            await InManagementAsync(() => SynchronizeAsync(null, ct), ct).ConfigureAwait(false);
            await AwaitStartupAsync(ct).ConfigureAwait(false);
            return CurrentSearchSources;
        }, token);

    public IReadOnlyList<FilmSearchSource> CurrentSearchSources
    {
        get
        {
            var current = Volatile.Read(ref snapshot);
            return Array.AsReadOnly(current.Videos
                .Where(site => site.Options.Enabled && site.Options.SearchEnabled &&
                    site.Actions.Contains("search", StringComparer.Ordinal))
                .Join(current.Cats, site => site.Key.InstallationId, cat => cat.InstallationId,
                    (site, cat) => new FilmSearchSource(cat, site), StringComparer.Ordinal)
                .Where(source => source.Cat.CanDetect).ToArray());
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        RunAsync(token => InStateAsync(LoadAsync, token), cancellationToken);

    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async token =>
        {
            bool recovered = false;
            await InManagementAsync(async () =>
            {
                await InitializeCoreAsync(token).ConfigureAwait(false);
                if (recovery?.CanRetry == true)
                {
                    await ReportRuntimeErrorAsync(new FilmSourceException("RECOVERING")).ConfigureAwait(false);
                    try { await recovery.RetryAsync(token).ConfigureAwait(false); recovered = true; }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        await ReportRuntimeErrorAsync(error).ConfigureAwait(false);
                        throw;
                    }
                }
                await SynchronizeAsync(null, token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            if (recovered) await AwaitStartupAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    public Task StartAsync(CancellationToken cancellationToken = default) => RunAsync(async token =>
    {
        try
        {
            await InitializeCoreAsync(token).ConfigureAwait(false);
            await InManagementAsync(async () =>
            {
                await transport.CallAsync("start", new { }, token).ConfigureAwait(false);
                await SynchronizeAsync(null, token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            await AwaitStartupAsync(token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await ReportRuntimeErrorAsync(error).ConfigureAwait(false);
            throw;
        }
    }, cancellationToken);

    private async Task AwaitStartupAsync(CancellationToken token)
    {
        // Observe actual "starting" completion independently of any page.
        while (Cats.Any(cat => cat.State == "starting"))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
            await InManagementAsync(() => SynchronizeAsync(null, token), token).ConfigureAwait(false);
        }
    }

    public async Task<FilmCatSource> ImportLocalAsync(string name, string filePath, string? installationId = null,
        CancellationToken cancellationToken = default)
    {
        CatSourceSettings.ValidateSource(name, "local", null);
        var input = await CatSourceSettings.SelectLocalAsync(filePath, cancellationToken).ConfigureAwait(false);
        string id = installationId ?? Guid.NewGuid().ToString("N");
        return await SaveCatConfigurationAsync(id, configured =>
            configured.Any(cat => cat.InstallationId == id) ? throw new FilmSourceException("STORAGE_CONFLICT") :
                [.. configured, new(id, name.Trim(), "local", null, input.Path, input.Hash, true, 1)],
            cancellationToken).ConfigureAwait(false);
    }

    public Task<FilmCatSource> ImportNetworkAsync(string name, string sourceUrl, string? installationId = null,
        CancellationToken cancellationToken = default)
    {
        CatSourceSettings.ValidateSource(name, "network", sourceUrl);
        string id = installationId ?? Guid.NewGuid().ToString("N");
        return SaveCatConfigurationAsync(id, configured =>
            configured.Any(cat => cat.InstallationId == id) ? throw new FilmSourceException("STORAGE_CONFLICT") :
                [.. configured, new(id, name.Trim(), "network", sourceUrl, null, null, true, 1)], cancellationToken);
    }

    public Task<FilmCatSource> EditCatAsync(string installationId, string name, string? sourceUrl = null,
        CancellationToken cancellationToken = default)
    {
        var current = FindCat(installationId);
        return EditCatAsync(installationId, name, current.SourceType, sourceUrl ?? current.SourceUrl,
            null, cancellationToken);
    }

    public async Task<FilmCatSource> EditCatAsync(string installationId, string name, string sourceType,
        string? sourceUrl, string? localPath, CancellationToken cancellationToken = default)
    {
        CatSourceSettings.ValidateSource(name, sourceType, sourceType == "network" ? sourceUrl : null);
        (string Path, string Hash)? input = localPath is null ? null :
            await CatSourceSettings.SelectLocalAsync(localPath, cancellationToken).ConfigureAwait(false);
        return await SaveCatConfigurationAsync(installationId, configured => configured.Select(cat =>
        {
            if (cat.InstallationId != installationId) return cat;
            string? path = sourceType == "local" ? input?.Path ?? (cat.SourceType == "local" ? cat.LocalPath : null) : null;
            string? hash = sourceType == "local" ? input?.Hash ?? (cat.SourceType == "local" ? cat.LocalHash : null) : null;
            if (sourceType == "local" && hash is null) throw new FilmSourceException("INVALID_ARGUMENT");
            string? url = sourceType == "network" ? sourceUrl : null;
            bool changed = cat.SourceType != sourceType || cat.SourceUrl != url ||
                cat.LocalPath != path || cat.LocalHash != hash;
            return cat with { Name = name.Trim(), SourceType = sourceType, SourceUrl = url,
                LocalPath = path, LocalHash = hash, InputRevision = cat.InputRevision + (changed ? 1 : 0) };
        }), cancellationToken, requireExisting: true).ConfigureAwait(false);
    }

    public Task<FilmCatSource> UpdateCatAsync(string installationId, string? filePath = null,
        CancellationToken cancellationToken = default)
    {
        var cat = FindCat(installationId);
        if (cat.SourceType == "local")
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            return EditCatAsync(installationId, cat.Name, "local", null, filePath, cancellationToken);
        }
        if (filePath is not null) throw new ArgumentException("Network updates use the saved URL.", nameof(filePath));
        return ManageAsync(installationId, token => transport.CallAsync("update", new { installationId }, token),
            cancellationToken);
    }

    public Task<FilmCatSource> RestartCatAsync(string installationId, CancellationToken cancellationToken = default) =>
        ManageAsync(installationId, token => transport.CallAsync("restart", new { installationId }, token),
            cancellationToken, requireEnabled: true);

    public Task<FilmCatSource> EnableCatAsync(string installationId, bool enabled,
        CancellationToken cancellationToken = default) => SaveCatConfigurationAsync(installationId,
        configured => configured.Select(cat => cat.InstallationId == installationId ? cat with { Enabled = enabled } : cat),
        cancellationToken, requireExisting: true);

    public Task DeleteCatAsync(string installationId, CancellationToken cancellationToken = default) =>
        RunAsync(token => InStateAsync(async ct =>
        {
            await LoadAsync(ct).ConfigureAwait(false);
            FindCat(installationId);
            var configured = await settings.ChangeAsync(cats => cats.Where(cat => cat.InstallationId != installationId), ct)
                .ConfigureAwait(false);
            ConfigurationChanged();
            InvalidateCat(installationId);
            ApplyConfiguration(configured);
        }, token), cancellationToken);

    private Task<FilmCatSource> SaveCatConfigurationAsync(string id,
        Func<IReadOnlyList<CatSourceConfiguration>, IEnumerable<CatSourceConfiguration>> edit,
        CancellationToken token, bool requireExisting = false) => RunAsync(async ct =>
    {
        FilmCatSource result = null!;
        await InStateAsync(async admission =>
        {
            await LoadAsync(admission).ConfigureAwait(false);
            if (requireExisting) FindCat(id);
            var configured = await settings.ChangeAsync(edit, admission).ConfigureAwait(false);
            // No runtime acknowledgement or cache write can revoke this native commit.
            ApplyConfiguration(configured);
            ConfigurationChanged();
            result = FindCat(id);
        }, ct).ConfigureAwait(false);
        return result;
    }, token);

    private bool RuntimeActive => recovery?.State is RuntimeConnectionState.Starting or RuntimeConnectionState.Ready ||
        Runtime is IRuntimeSession { IsConnected: true };

    private FilmCatSource[] ConfiguredCats(IReadOnlyList<CatSourceConfiguration> configured, IEnumerable<FilmCatSource> received)
    {
        var incoming = received.ToDictionary(cat => cat.InstallationId, StringComparer.Ordinal);
        return configured.Select(input =>
        {
            incoming.TryGetValue(input.InstallationId, out var observed);
            var cached = observed ?? Cats.FirstOrDefault(cat => cat.InstallationId == input.InstallationId);
            bool current = observed is not null && observed.InputRevision == input.InputRevision &&
                observed.Enabled == input.Enabled && observed.SourceType == input.SourceType && observed.SourceUrl == input.SourceUrl;
            var value = cached ?? new FilmCatSource(input.InstallationId, input.Name, input.SourceType, input.SourceUrl,
                input.Enabled, null, null, "idle", null, 0, "stopped", null, null, []);
            return value with
            {
                Name = input.Name, SourceType = input.SourceType, SourceUrl = input.SourceUrl,
                LocalPath = input.LocalPath, Enabled = input.Enabled, InputRevision = input.InputRevision,
                Generation = current ? value.Generation : 0,
                State = current ? value.State : !input.Enabled ? "disabled" : RuntimeActive ? "starting" : "stopped",
                Error = current ? value.Error : null,
                UpdateState = current || cached?.InputRevision == input.InputRevision ? value.UpdateState : "idle",
                UpdateError = current || cached?.InputRevision == input.InputRevision ? value.UpdateError : null
            };
        }).ToArray();
    }

    private void ApplyConfiguration(IReadOnlyList<CatSourceConfiguration> configured)
    {
        var cats = ConfiguredCats(configured, Cats);
        var ids = cats.Select(cat => cat.InstallationId).ToHashSet(StringComparer.Ordinal);
        foreach (var previous in Cats)
            if (cats.FirstOrDefault(cat => cat.InstallationId == previous.InstallationId) is not { } next ||
                next.InputRevision != previous.InputRevision || next.Enabled != previous.Enabled)
                InvalidateCat(previous.InstallationId);
        Volatile.Write(ref snapshot, new Snapshot(Freeze(cats), Freeze(Videos.Where(video => ids.Contains(video.Key.InstallationId)))));
    }

    private void ConfigurationChanged()
    {
        if (!RuntimeActive || Runtime is not IRuntimeConnectionSource source) return;
        var task = RunAsync(async token =>
        {
            try
            {
                var connection = await source.CaptureConnectionAsync(token).ConfigureAwait(false);
                using var activeConnection = CancellationTokenSource.CreateLinkedTokenSource(token, connection.Revoked);
                var reply = await connection.Client.CallAsync("runtime.settings.changed", new { block = "cat.sources" },
                    TimeSpan.FromSeconds(5), activeConnection.Token).ConfigureAwait(false);
                if (!reply.GetProperty("ok").GetBoolean()) throw new BridgeException(reply.GetProperty("code").GetString()!);
                await InManagementAsync(() => SynchronizeAsync(null, activeConnection.Token), activeConnection.Token).ConfigureAwait(false);
                await AwaitStartupAsync(activeConnection.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { await ReportRuntimeErrorAsync(error).ConfigureAwait(false); }
        }, CancellationToken.None);
        _ = ObserveAsync(task);
        static async Task ObserveAsync(Task work) { try { await work.ConfigureAwait(false); } catch { } }
    }

    public Task DeleteVideoAsync(FilmSourceKey key, CancellationToken cancellationToken = default) =>
        RunAsync(token => InStateAsync(async ct =>
        {
            await LoadAsync(ct).ConfigureAwait(false);
            FindVideo(key);
            InvalidateProbe(key);
            var deleted = document.Deleted.ToHashSet();
            deleted.Add(key);
            await CommitAsync(Cats, Sorted(Videos.Where(video => video.Key != key)), deleted, ct).ConfigureAwait(false);
        }, token), cancellationToken);

    public Task SaveVideoOptionsAsync(FilmSourceKey key, FilmVideoOptions options, int? order = null,
        CancellationToken cancellationToken = default) => RunAsync(token => InStateAsync(async ct =>
    {
        ArgumentNullException.ThrowIfNull(options);
        await LoadAsync(ct).ConfigureAwait(false);
        FindVideo(key);
        var videos = Videos.Select(video => video.Key == key ? video with { Options = options, Order = order ?? video.Order } :
            options.AggregateImages && video.Options.AggregateImages
                ? video with { Options = video.Options with { AggregateImages = false } } : video)
            .OrderBy(video => video.Order).ThenBy(video => order is not null && video.Key == key ? 0 : 1);
        await CommitAsync(Cats, videos, document.Deleted, ct).ConfigureAwait(false);
    }, token), cancellationToken);

    public Task ReorderVideosAsync(IReadOnlyList<FilmSourceKey> order, CancellationToken cancellationToken = default)
    {
        var keys = order.ToArray();
        return RunAsync(token => InStateAsync(async ct =>
        {
            await LoadAsync(ct).ConfigureAwait(false);
            var byKey = Videos.ToDictionary(video => video.Key);
            if (keys.Length != byKey.Count || keys.Distinct().Count() != keys.Length ||
                keys.Any(key => !byKey.ContainsKey(key))) throw new FilmSourceException("STORAGE_CONFLICT");
            if (keys.SequenceEqual(Videos.Select(video => video.Key))) return;
            await CommitAsync(Cats, keys.Select((key, index) => byKey[key] with { Order = index }),
                document.Deleted, ct).ConfigureAwait(false);
        }, token), cancellationToken);
    }

    public Task<FilmSourceDetection> DetectAsync(FilmSourceKey key, CancellationToken cancellationToken = default) =>
        RunAsync(async token =>
        {
            FilmCatSource cat = null!;
            FilmVideoSource video = null!;
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(token);
            await InStateAsync(async ct =>
            {
                await LoadAsync(ct).ConfigureAwait(false);
                video = FindVideo(key);
                cat = FindCat(key.InstallationId);
                if (!cat.Enabled) throw new FilmSourceException("DISABLED");
                if (changingCats.Contains(cat.InstallationId)) throw new FilmSourceException("BUSY");
                if (!cat.CanDetect) throw new FilmSourceException("NOT_READY");
                InvalidateProbe(key);
                probes[key] = probe;
            }, token).ConfigureAwait(false);
            try
            {
                var result = await new FilmSourceProbe(transport, cat, video)
                    .RunAsync(probe.Token).ConfigureAwait(false);
                await InStateAsync(async ct =>
                {
                    probe.Token.ThrowIfCancellationRequested();
                    var current = FindCat(key.InstallationId);
                    if (!probes.TryGetValue(key, out var running) || running != probe ||
                        !current.CanDetect || current.Generation != cat.Generation ||
                        !Videos.Any(item => item.Key == key)) throw new OperationCanceledException(probe.Token);
                    await CommitAsync(Cats, Videos.Select(item => item.Key == key
                        ? item with { Detection = result } : item), document.Deleted, ct).ConfigureAwait(false);
                }, probe.Token).ConfigureAwait(false);
                return result;
            }
            finally
            {
                await stateGate.WaitAsync().ConfigureAwait(false);
                try { if (probes.TryGetValue(key, out var current) && current == probe) probes.Remove(key); }
                finally { stateGate.Release(); }
            }
        }, cancellationToken);

    private Task<FilmCatSource> ManageAsync(string id, Func<CancellationToken, Task<JsonElement>> operation,
        CancellationToken cancellationToken, bool requireEnabled = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return RunAsync(async token =>
        {
            FilmCatSource? result = null;
            await InManagementAsync(async () =>
            {
                await InitializeCoreAsync(token).ConfigureAwait(false);
                await InStateAsync(_ =>
                {
                    var cat = FindCat(id);
                    if (requireEnabled && !cat.Enabled) throw new FilmSourceException("DISABLED");
                    InvalidateCat(id);
                    changingCats.Add(id);
                    return Task.CompletedTask;
                }, token).ConfigureAwait(false);
                try
                {
                    try { await operation(token).ConfigureAwait(false); }
                    catch (FilmSourceException)
                    {
                        // Failed management may have persisted updateState/updateError in Node.
                        await ReconcileManagementFailureAsync(id).ConfigureAwait(false);
                        throw;
                    }
                    await SynchronizeAsync(null, token).ConfigureAwait(false);
                    result = FindCat(id);
                }
                catch (Exception error) when (error is OperationCanceledException or BridgeException or DocumentStoreException)
                {
                    await ReconcileManagementFailureAsync(id).ConfigureAwait(false);
                    throw;
                }
                finally { await EndCatChangeAsync(id).ConfigureAwait(false); }
            }, token).ConfigureAwait(false);
            return result!;
        }, cancellationToken);
    }

    private async Task ReconcileManagementFailureAsync(string id)
    {
        // RPC failure or a failed local save can follow a committed Node change.
        // Retire the cached generation before observing the actual state.
        await InStateAsync(_ =>
        {
            InvalidateCat(id);
            Volatile.Write(ref snapshot, new Snapshot(Freeze(Cats.Select(cat => cat.InstallationId == id
                ? cat with { Generation = 0, State = "starting" } : cat)), Videos));
            return Task.CompletedTask;
        }, CancellationToken.None).ConfigureAwait(false);
        using var owner = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, pending.Token);
        try
        {
            do
            {
                // Never use a cancelled page token or replay the management call.
                // Failed management, including its local save, cannot restore tombstones.
                await SynchronizeAsync(null, owner.Token).ConfigureAwait(false);
                if (!Cats.Any(cat => cat.InstallationId == id && cat.State == "starting")) return;
                await Task.Delay(TimeSpan.FromMilliseconds(250), owner.Token).ConfigureAwait(false);
            } while (true);
        }
        catch (Exception error)
        {
            // Preserve the original management error, but never retain a ready retired
            // generation when reconciliation fails or shutdown interrupts it.
            await ReportRuntimeErrorAsync(error).ConfigureAwait(false);
        }
    }

    private async Task SynchronizeAsync(FilmCatSource? _, CancellationToken token)
    {
        await InitializeCoreAsync(token).ConfigureAwait(false);
        FilmCatSource[] incoming;
        try
        {
            var root = await transport.CallAsync("status", new { }, token).ConfigureAwait(false);
            CatScriptTransport.Require(root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("instances", out var values) && values.ValueKind == JsonValueKind.Array);
            incoming = root.GetProperty("instances").EnumerateArray().Select(ParseCat).ToArray();
            CatScriptTransport.Require(incoming.Select(cat => cat.InstallationId).Distinct(StringComparer.Ordinal).Count() == incoming.Length);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await ReportRuntimeErrorAsync(error).ConfigureAwait(false);
            throw;
        }
        await InStateAsync(async ct =>
        {
            var configured = await settings.ReadAsync(ct).ConfigureAwait(false);
            incoming = ConfiguredCats(configured, incoming);
            var ids = incoming.Select(cat => cat.InstallationId).ToHashSet(StringComparer.Ordinal);
            var deleted = document.Deleted.Where(key => ids.Contains(key.InstallationId)).ToHashSet();
            var videos = Videos.Where(video => ids.Contains(video.Key.InstallationId)).ToList();
            var readableKeys = document.Videos.Select(video => video.Key).ToHashSet();
            var isolatedKeys = FilmJson.Array(rawDocument, "videos").SelectMany(VideoKeyCandidates)
                .Where(key => !readableKeys.Contains(key)).Concat(AmbiguousDeletedKeys(rawDocument)).ToHashSet();
            var cats = new List<FilmCatSource>();
            foreach (var previous in Cats)
                if (!ids.Contains(previous.InstallationId)) InvalidateCat(previous.InstallationId);
            foreach (var received in incoming)
            {
                var previous = Cats.FirstOrDefault(item => item.InstallationId == received.InstallationId);
                bool authoritativeSites = received.CanDetect;
                var cat = authoritativeSites || previous is null ? received : received with { Sites = previous.Sites };
                if (previous is not null && (previous.Generation != cat.Generation || previous.State != cat.State ||
                    previous.Enabled != cat.Enabled || previous.SourceHash != cat.SourceHash))
                    InvalidateCat(cat.InstallationId);
                cats.Add(cat);
                if (!authoritativeSites) continue;
                var siteKeys = cat.Sites.Select(site => site.Key).ToHashSet(StringComparer.Ordinal);
                foreach (var removed in videos.Where(video => video.Key.InstallationId == cat.InstallationId &&
                    !siteKeys.Contains(video.Key.SiteKey))) InvalidateProbe(removed.Key);
                videos.RemoveAll(video => video.Key.InstallationId == cat.InstallationId && !siteKeys.Contains(video.Key.SiteKey));
                foreach (var site in cat.Sites)
                {
                    var key = new FilmSourceKey(cat.InstallationId, site.Key);
                    if (deleted.Contains(key)) continue;
                    int index = videos.FindIndex(video => video.Key == key);
                    if (index < 0)
                    {
                        // A cached name or action list may be rebuilt, but an unreadable
                        // or ambiguous user-options record must not become new defaults.
                        if (isolatedKeys.Contains(key)) continue;
                        int order = videos.Count == 0 ? 0 : (int)Math.Min((long)videos.Max(video => video.Order) + 1, int.MaxValue);
                        videos.Add(new(key, site.Name, site.Api, site.Actions, site.Methods, new(), order));
                    }
                    else
                    {
                        var old = videos[index];
                        if (old.Api != site.Api || !old.Actions.SequenceEqual(site.Actions) ||
                            !old.Methods.OrderBy(pair => pair.Key).SequenceEqual(site.Methods.OrderBy(pair => pair.Key)))
                            InvalidateProbe(key);
                        videos[index] = old with { Name = site.Name, Api = site.Api, Actions = site.Actions, Methods = site.Methods };
                    }
                }
            }
            await CommitAsync(cats, Sorted(videos), deleted, ct).ConfigureAwait(false);
            Volatile.Write(ref runtimeError, null);
        }, token).ConfigureAwait(false);
    }

    private Task ReportRuntimeErrorAsync(Exception error) => InStateAsync(_ =>
    {
        string code = error is FilmSourceException source ? source.Code : "RUNTIME_UNAVAILABLE";
        foreach (var key in probes.Keys.ToArray()) InvalidateProbe(key);
        Volatile.Write(ref runtimeError, code);
        Volatile.Write(ref snapshot, new Snapshot(Freeze(Cats.Select(cat => cat with
            { Generation = 0, State = cat.Enabled ? "failed" : "disabled", Error = code })), Videos));
        return Task.CompletedTask;
    }, CancellationToken.None);

    private async Task EndCatChangeAsync(string id)
    {
        await stateGate.WaitAsync().ConfigureAwait(false);
        try { changingCats.Remove(id); }
        finally { stateGate.Release(); }
    }

    private Task InitializeCoreAsync(CancellationToken token) => InStateAsync(LoadAsync, token);

    private async Task LoadAsync(CancellationToken token)
    {
        if (initialized) return;
        var stored = await store.ReadAsync(DocumentNamespace, token).ConfigureAwait(false);
        try
        {
            if (stored.Data is null && stored.Fingerprint != DocumentFingerprint.Missing)
                throw new FilmSourceException("INVALID_DOCUMENT");
            using var parsed = stored.Data is null ? null : JsonDocument.Parse(stored.Data);
            var root = parsed?.RootElement.Clone() ?? JsonSerializer.SerializeToElement(new StoredState(1, [], [], []), Json);
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Where(property => property.Name is "version" or "cats" or "videos" or "deleted")
                    .GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() > 1) ||
                FilmJson.Integer(root, "version") != 1 ||
                FilmJson.Member(root, "videos").ValueKind != JsonValueKind.Array ||
                FilmJson.Member(root, "deleted").ValueKind != JsonValueKind.Array)
                throw new FilmSourceException("INVALID_DOCUMENT");
            var deleted = FilmJson.Array(root, "deleted").Select(ReadKey).OfType<FilmSourceKey>().Distinct().ToArray();
            var loadedCats = ReadUnique(FilmJson.Array(root, "cats"), row => ReadText(row, "installationId"), ReadStoredCat);
            var rawVideos = FilmJson.Array(root, "videos");
            var counts = rawVideos.SelectMany(VideoKeyCandidates).GroupBy(key => key)
                .ToDictionary(group => group.Key, group => group.Count());
            var ambiguousDeleted = AmbiguousDeletedKeys(root).ToHashSet();
            var loadedVideos = ReadUnique(rawVideos, ReadVideoKey, ReadStoredVideo)
                .Where(video => counts[video.Key] == 1 && !deleted.Contains(video.Key) && !ambiguousDeleted.Contains(video.Key)).ToArray();
            var loaded = new StoredState(1, loadedCats, loadedVideos, deleted);
            var cats = ConfiguredCats(await settings.ReadAsync(token).ConfigureAwait(false), loaded.Cats.Select(cat => cat.ToSnapshot()));
            var ids = cats.Select(cat => cat.InstallationId).ToHashSet(StringComparer.Ordinal);
            var videos = loaded.Videos.Where(video => ids.Contains(video.Key.InstallationId)).OrderBy(video => video.Order).Select(FreezeVideo).ToArray();
            token.ThrowIfCancellationRequested();
            foreach (var key in probes.Keys.ToArray()) InvalidateProbe(key);
            document = loaded with { Videos = Freeze(videos) };
            rawDocument = root;
            fingerprint = stored.Fingerprint;
            snapshot = new(Freeze(cats), Freeze(videos));
            initialized = true;
        }
        catch (JsonException) { throw new FilmSourceException("INVALID_DOCUMENT"); }
        finally { if (stored.Data is not null) CryptographicOperations.ZeroMemory(stored.Data); }
    }

    private async Task CommitAsync(IEnumerable<FilmCatSource> cats, IEnumerable<FilmVideoSource> videos,
        IEnumerable<FilmSourceKey> deleted, CancellationToken token, string? removedInstallation = null)
    {
        var next = new Snapshot(Freeze(cats), Freeze(videos));
        var candidate = new StoredState(1, next.Cats.Select(StoredCat.From).ToArray(), next.Videos,
            deleted.OrderBy(key => key.InstallationId, StringComparer.Ordinal).ThenBy(key => key.SiteKey, StringComparer.Ordinal).ToArray());
        var root = FilmDocument.Set(rawDocument, "cats", MergeRows(FilmJson.Member(rawDocument, "cats"),
            document.Cats, candidate.Cats, cat => cat.InstallationId, row => ReadText(row, "installationId"),
            MergeCat, id => id == removedInstallation));
        root = FilmDocument.Set(root, "videos", MergeRows(FilmJson.Member(rawDocument, "videos"),
            document.Videos, candidate.Videos, video => video.Key, ReadVideoKey,
            (raw, before, after) => FilmDocument.Merge(raw, Encode(before), Encode(after)),
            key => key.InstallationId == removedInstallation));
        root = FilmDocument.Set(root, "deleted", MergeRows(FilmJson.Member(rawDocument, "deleted"),
            document.Deleted, candidate.Deleted, key => key, ReadKey,
            (raw, before, after) => FilmDocument.Merge(raw, Encode(before), Encode(after)),
            key => key.InstallationId == removedInstallation));
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(root);
        var after = DocumentFingerprint.FromBytes(bytes);
        try
        {
            token.ThrowIfCancellationRequested();
            if (after != fingerprint)
            {
                try
                {
                    // An admitted CAS must settle even if a page or the app cancels its wait.
                    var receipt = await store.WriteAsync(DocumentNamespace, bytes, fingerprint, CancellationToken.None).ConfigureAwait(false);
                    if (receipt != after) throw new FilmSourceException("COMMIT_UNKNOWN");
                }
                catch
                {
                    initialized = false;
                    var actual = await store.ReadAsync(DocumentNamespace, CancellationToken.None).ConfigureAwait(false);
                    try { if (actual.Fingerprint != after) throw; }
                    finally { if (actual.Data is not null) CryptographicOperations.ZeroMemory(actual.Data); }
                }
            }
            document = candidate;
            rawDocument = root;
            fingerprint = after;
            initialized = true;
            Volatile.Write(ref snapshot, next);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private FilmCatSource FindCat(string id) => Cats.FirstOrDefault(cat => cat.InstallationId == id)
        ?? throw new FilmSourceException("NOT_FOUND");
    private FilmVideoSource FindVideo(FilmSourceKey key) => Videos.FirstOrDefault(video => video.Key == key)
        ?? throw new FilmSourceException("NOT_FOUND");
    private void InvalidateCat(string id)
    {
        foreach (var key in probes.Keys.Where(key => key.InstallationId == id).ToArray()) InvalidateProbe(key);
    }
    private void InvalidateProbe(FilmSourceKey key)
    {
        if (probes.Remove(key, out var probe)) probe.Cancel();
    }

    private async Task InStateAsync(Func<CancellationToken, Task> action, CancellationToken token)
    {
        await stateGate.WaitAsync(token).ConfigureAwait(false);
        var before = snapshot;
        try { token.ThrowIfCancellationRequested(); await action(token).ConfigureAwait(false); }
        finally
        {
            stateGate.Release();
            if (!ReferenceEquals(before, snapshot)) Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task InManagementAsync(Func<Task> action, CancellationToken token)
    {
        await managementGate.WaitAsync(token).ConfigureAwait(false);
        try { await action().ConfigureAwait(false); }
        finally { managementGate.Release(); }
    }

    private Task RunAsync(Func<CancellationToken, Task> action, CancellationToken token) =>
        RunAsync(async ct => { await action(ct).ConfigureAwait(false); return true; }, token);

    private Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token)
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (preparing) throw new FilmSourceException("SHUTTING_DOWN");
            active++;
            return ExecuteAsync();
        }
        async Task<T> ExecuteAsync()
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, pending.Token);
                linked.Token.ThrowIfCancellationRequested();
                return await action(linked.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (lifecycle)
                {
                    if (--active == 0)
                    {
                        if (closed) drained.TrySetResult();
                        preparationDrained?.TrySetResult();
                        preparationDrained = null;
                    }
                }
            }
        }
    }

    public Task PrepareShutdownAsync(CancellationToken cancellationToken = default)
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            preparing = true;
            pending.Cancel();
            if (active == 0) return Task.CompletedTask;
            preparationDrained ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            return preparationDrained.Task.WaitAsync(cancellationToken);
        }
    }

    public void Resume()
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (!preparing) return;
            if (active != 0) throw new InvalidOperationException("Source operations have not drained.");
            pending.Dispose();
            pending = new();
            preparing = false;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (lifecycle)
        {
            if (closing is not null) return new(closing);
            closed = true;
            if (active == 0) drained.TrySetResult();
            closing = CloseAsync();
            return new(closing);
        }
        async Task CloseAsync()
        {
            lifetime.Cancel();
            await drained.Task.ConfigureAwait(false);
            lifetime.Dispose();
            pending.Dispose();
            managementGate.Dispose();
            stateGate.Dispose();
            Changed = null;
        }
    }

    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) => Array.AsReadOnly(values.ToArray());
    private static IReadOnlyDictionary<string, string> FreezeMethods(IEnumerable<KeyValuePair<string, string>> values) =>
        new ReadOnlyDictionary<string, string>(values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    private static FilmSourceSite FreezeSite(FilmSourceSite site) =>
        site with { Actions = Freeze(site.Actions), Methods = FreezeMethods(site.Methods) };
    private static FilmVideoSource FreezeVideo(FilmVideoSource video) => video with
    {
        Actions = Freeze(video.Actions), Methods = FreezeMethods(video.Methods),
        Detection = video.Detection is { } result ? result with { Steps = Freeze(result.Steps) } : null
    };
    private static IEnumerable<FilmVideoSource> Sorted(IEnumerable<FilmVideoSource> values) =>
        values.OrderBy(video => video.Order);

    private static string? ReadText(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            value.EnumerateObject().Count(property => property.NameEquals(name)) != 1) return null;
        var text = value.GetProperty(name);
        return text.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(text.GetString()) ? text.GetString() : null;
    }

    private static FilmSourceKey? ReadKey(JsonElement value) =>
        ReadText(value, "installationId") is { } installation && ReadText(value, "siteKey") is { } site
            ? new(installation, site) : null;

    private static JsonElement[] IdentityFields(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        ? value.EnumerateObject().Where(property => property.NameEquals(name)).Select(property => property.Value).ToArray() : [];

    private static FilmSourceKey? ReadVideoKey(JsonElement row)
    {
        var keys = IdentityFields(row, "key");
        return keys.Length == 1 ? ReadKey(keys[0]) : null;
    }

    private static IEnumerable<FilmSourceKey> KeyCandidates(JsonElement value)
    {
        var installations = IdentityFields(value, "installationId").Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.Ordinal);
        var sites = IdentityFields(value, "siteKey").Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.Ordinal).ToArray();
        return installations.SelectMany(installation => sites.Select(site => new FilmSourceKey(installation, site)));
    }

    private static IEnumerable<FilmSourceKey> VideoKeyCandidates(JsonElement row) =>
        IdentityFields(row, "key").SelectMany(KeyCandidates).Distinct();

    private static IEnumerable<FilmSourceKey> AmbiguousDeletedKeys(JsonElement root) =>
        FilmJson.Array(root, "deleted").Where(row => ReadKey(row) is null).SelectMany(KeyCandidates).Distinct();

    private static T[] ReadUnique<T, TKey>(IEnumerable<JsonElement> rows,
        Func<JsonElement, TKey?> key, Func<JsonElement, T?> read) where T : class where TKey : class =>
        rows.Select(row => (Row: row, Key: key(row))).Where(entry => entry.Key is not null)
            .GroupBy(entry => entry.Key!).Where(group => group.Count() == 1)
            .Select(group => read(group.Single().Row)).OfType<T>().ToArray();

    private static StoredCat? ReadStoredCat(JsonElement row)
    {
        try
        {
            var cat = JsonSerializer.Deserialize<StoredCat>(row, Json);
            if (cat is null || cat.Name is null || cat.SourceType is not ("local" or "network") ||
                cat.Sites is null || cat.Sites.Any(site => site is null || string.IsNullOrWhiteSpace(site.Key) ||
                    site.Name is null || site.Api is null || site.Actions is null || site.Methods is null ||
                    site.Actions.Any(action => action is null) || site.Methods.Any(method => method.Value is null)) ||
                cat.Sites.Select(site => site.Key).Distinct(StringComparer.Ordinal).Count() != cat.Sites.Count) return null;
            return cat with
            {
                UpdateState = cat.UpdateState ?? "",
                ImportedAt = cat.ImportedAt is >= 0 and <= 253402300799999 ? cat.ImportedAt : null,
                UpdatedAt = cat.UpdatedAt is >= 0 and <= 253402300799999 ? cat.UpdatedAt : null
            };
        }
        catch (JsonException) { return null; }
    }

    private static FilmVideoSource? ReadStoredVideo(JsonElement row)
    {
        var key = ReadVideoKey(row);
        var options = FilmJson.Member(row, "options");
        if (key is null || FilmJson.Integer(row, "order") is not { } order ||
            options.ValueKind != JsonValueKind.Object) return null;
        foreach (string name in new[] { "enabled", "homeVisible", "searchEnabled", "aggregateImages" })
            if (FilmJson.Member(options, name).ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
        FilmSourceDetection? detection = null;
        try
        {
            if (FilmJson.Member(row, "detection") is { ValueKind: JsonValueKind.Object } value)
                detection = JsonSerializer.Deserialize<FilmSourceDetection>(value, Json);
            if (detection?.Steps is null || detection.Steps.Any(step => step is null || step.Operation is null || step.State is null))
                detection = null;
        }
        catch (JsonException) { }
        var methods = FilmJson.Member(row, "methods");
        return new(key, FilmJson.Text(row, "name"), FilmJson.Text(row, "api"),
            Freeze(FilmJson.Array(row, "actions").Where(action => action.ValueKind == JsonValueKind.String)
                .Select(action => action.GetString()!)),
            FreezeMethods(methods.ValueKind == JsonValueKind.Object ? methods.EnumerateObject()
                .Where(method => method.Value.ValueKind == JsonValueKind.String)
                .GroupBy(method => method.Name, StringComparer.Ordinal).Where(group => group.Count() == 1)
                .Select(group => new KeyValuePair<string, string>(group.Key, group.Single().Value.GetString()!)) : []),
            new(FilmJson.Bool(options, "enabled"), FilmJson.Bool(options, "homeVisible"),
                FilmJson.Bool(options, "searchEnabled"), FilmJson.Bool(options, "aggregateImages")), order, detection);
    }

    private static JsonElement Encode(object? value) => value is null ? default : JsonSerializer.SerializeToElement(value, Json);

    private static JsonElement MergeRows<T, TKey>(JsonElement original, IReadOnlyList<T> before, IReadOnlyList<T> after,
        Func<T, TKey> key, Func<JsonElement, TKey?> readKey, Func<JsonElement, T?, T, JsonElement> merge,
        Func<TKey, bool>? remove = null) where T : class where TKey : class
    {
        if (original.ValueKind != JsonValueKind.Array && after.Count == 0 && original.ValueKind != JsonValueKind.Undefined)
            return original;
        var previous = before.ToDictionary(key);
        var next = after.ToDictionary(key);
        var rows = FilmJson.Array(original);
        var counts = rows.Select(readKey).OfType<TKey>().GroupBy(value => value)
            .ToDictionary(group => group.Key, group => group.Count());
        return FilmDocument.Write(writer =>
        {
            writer.WriteStartArray();
            var present = new HashSet<TKey>();
            foreach (var row in rows)
            {
                var id = readKey(row);
                if (id is null) { row.WriteTo(writer); continue; }
                present.Add(id);
                if (remove?.Invoke(id) == true) continue;
                if (previous.ContainsKey(id) && !next.ContainsKey(id)) continue;
                if (counts[id] != 1) { row.WriteTo(writer); continue; }
                if (next.TryGetValue(id, out var value))
                {
                    previous.TryGetValue(id, out var old);
                    merge(row, old, value).WriteTo(writer);
                }
                else if (!previous.ContainsKey(id)) row.WriteTo(writer);
            }
            foreach (var value in after)
                if (present.Add(key(value))) Encode(value).WriteTo(writer);
            writer.WriteEndArray();
        });
    }

    private static JsonElement MergeCat(JsonElement original, StoredCat? before, StoredCat after)
    {
        var merged = FilmDocument.Merge(original, Encode(before), Encode(after));
        return FilmDocument.Set(merged, "sites", MergeRows(FilmJson.Member(original, "sites"),
            before?.Sites ?? [], after.Sites, site => site.Key, row => ReadText(row, "key"),
            (raw, old, next) => FilmDocument.Merge(raw, Encode(old), Encode(next))));
    }

    private static FilmCatSource ParseCat(JsonElement value)
    {
        string Text(string property) => CatScriptTransport.Text(value, property);
        string? Optional(string property) => CatScriptTransport.OptionalText(value, property);
        long? Timestamp(string property) => FilmJson.Member(value, property) is { ValueKind: JsonValueKind.Number } item &&
            item.TryGetInt64(out long timestamp) && timestamp is >= 0 and <= 253402300799999 ? timestamp : null;
        CatScriptTransport.Require(value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("enabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            value.TryGetProperty("sites", out var sites) && sites.ValueKind == JsonValueKind.Array);
        string id = Text("installationId"), name = Text("name"), type = Text("sourceType");
        string update = Optional("updateState") ?? "";
        CatScriptTransport.Require(id.Length > 0 && name.Length > 0 && type is "local" or "network");
        var described = value.GetProperty("sites").EnumerateArray().Select(site =>
        {
            CatScriptTransport.Require(site.ValueKind == JsonValueKind.Object &&
                site.TryGetProperty("actions", out var actions) && actions.ValueKind == JsonValueKind.Array &&
                site.TryGetProperty("methods", out var methods) && methods.ValueKind == JsonValueKind.Object);
            string key = CatScriptTransport.Text(site, "key");
            CatScriptTransport.Require(key.Length > 0);
            return new FilmSourceSite(key, CatScriptTransport.Text(site, "name"), CatScriptTransport.Text(site, "api"),
                Freeze(site.GetProperty("actions").EnumerateArray().Select(action =>
                {
                    CatScriptTransport.Require(action.ValueKind == JsonValueKind.String);
                    return action.GetString()!;
                })), FreezeMethods(site.GetProperty("methods").EnumerateObject().Select(method =>
                {
                    CatScriptTransport.Require(method.Value.ValueKind == JsonValueKind.String);
                    return new KeyValuePair<string, string>(method.Name, method.Value.GetString()!);
                })));
        }).ToArray();
        CatScriptTransport.Require(described.Select(site => site.Key).Distinct(StringComparer.Ordinal).Count() == described.Length);
        return new FilmCatSource(id, name, type, Optional("sourceUrl"), value.GetProperty("enabled").GetBoolean(),
            Timestamp("importedAt"), Timestamp("updatedAt"), update, Optional("updateError"),
            CatScriptTransport.Integer(value, "generation"), Text("state"), Optional("sourceHash"), Optional("error"), Freeze(described))
            { InputRevision = FilmJson.Member(value, "inputRevision") is { ValueKind: JsonValueKind.Number } revision &&
                revision.TryGetInt64(out long number) && number is > 0 and <= BridgeCodec.MaximumSafeInteger ? number : 0 };
    }

    private sealed record Snapshot(IReadOnlyList<FilmCatSource> Cats, IReadOnlyList<FilmVideoSource> Videos);
    private sealed record StoredState(int Version, IReadOnlyList<StoredCat> Cats,
        IReadOnlyList<FilmVideoSource> Videos, IReadOnlyList<FilmSourceKey> Deleted);
    // Only a local cache of Node management metadata; no port, generation, source bytes or runtime ownership.
    private sealed record StoredCat(string InstallationId, string Name, string SourceType, string? SourceUrl,
        bool Enabled, long? ImportedAt, long? UpdatedAt, string UpdateState, string? UpdateError,
        string? SourceHash, IReadOnlyList<FilmSourceSite> Sites, long InputRevision = 0)
    {
        internal static StoredCat From(FilmCatSource cat) => new(cat.InstallationId, cat.Name, cat.SourceType,
            cat.SourceUrl, cat.Enabled, cat.ImportedAt, cat.UpdatedAt, cat.UpdateState, cat.UpdateError, cat.SourceHash, cat.Sites,
            cat.InputRevision);
        internal FilmCatSource ToSnapshot() => new(InstallationId, Name, SourceType, SourceUrl, Enabled,
            ImportedAt, UpdatedAt, UpdateState, UpdateError, 0, Enabled ? "stopped" : "disabled",
            SourceHash, null, Freeze(Sites.Select(FreezeSite))) { InputRevision = InputRevision };
    }
}
