using System.Collections.ObjectModel;
using System.Text.Json;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

public sealed class FilmSourceService : IAsyncDisposable, IFilmSearchSources
{
    public const string StorageOwner = "film.video-sources.v1";
    private readonly IStorageDatabase database;
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
    private StorageRevision storageRevision = StorageRevision.Empty;
    private StoredFilmSourceState document = new([], [], []);
    private Snapshot snapshot = new([], []);
    private bool initialized, closed, preparing;
    private string? runtimeError;
    private int active;
    private Task? closing;
    private TaskCompletionSource? preparationDrained;

    public FilmSourceService(IRuntimeClient runtime, IStorageDatabase database)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(database);
        Runtime = runtime;
        transport = new(runtime);
        settings = new(database);
        recovery = runtime as IRuntimeRecovery;
        this.database = database;
    }

    internal IRuntimeClient Runtime { get; }
    internal IStorageDatabase Storage => database;
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

    public Task<IReadOnlyDictionary<string, bool>> ReadPanInterceptionAsync(CancellationToken cancellationToken = default) =>
        RunAsync(token => settings.ReadPanInterceptionAsync(token), cancellationToken);

    public Task<IReadOnlyDictionary<string, bool>> SavePanInterceptionAsync(string provider, bool enabled,
        CancellationToken cancellationToken = default) => RunAsync(async token =>
    {
        IReadOnlyDictionary<string, bool> saved = null!;
        await InStateAsync(async ct =>
        {
            saved = await settings.SavePanInterceptionAsync(provider, enabled, ct).ConfigureAwait(false);
            foreach (var key in probes.Keys.ToArray()) InvalidateProbe(key);
            ConfigurationChanged();
        }, token).ConfigureAwait(false);
        return saved;
    }, cancellationToken);

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
        return await SaveCatConfigurationAsync(id, token => settings.AddAsync(
            new(id, name.Trim(), "local", null, input.Path, input.Hash, true, 1), token),
            cancellationToken).ConfigureAwait(false);
    }

    public Task<FilmCatSource> ImportNetworkAsync(string name, string sourceUrl, string? installationId = null,
        CancellationToken cancellationToken = default)
    {
        CatSourceSettings.ValidateSource(name, "network", sourceUrl);
        string id = installationId ?? Guid.NewGuid().ToString("N");
        return SaveCatConfigurationAsync(id, token => settings.AddAsync(
            new(id, name.Trim(), "network", sourceUrl, null, null, true, 1), token), cancellationToken);
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
        (string Path, string Hash)? input = sourceType != "local" || localPath is null ? null :
            await CatSourceSettings.SelectLocalAsync(localPath, cancellationToken).ConfigureAwait(false);
        return await SaveCatConfigurationAsync(installationId,
            token => settings.EditAsync(installationId, name.Trim(), sourceType, sourceUrl, input, token),
            cancellationToken).ConfigureAwait(false);
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
        token => settings.EnableAsync(installationId, enabled, token), cancellationToken);

    public Task DeleteCatAsync(string installationId, CancellationToken cancellationToken = default) =>
        RunAsync(token => InStateAsync(async ct =>
        {
            await LoadAsync(ct).ConfigureAwait(false);
            var configured = await settings.RemoveAsync(installationId, ct)
                .ConfigureAwait(false);
            ConfigurationChanged();
            InvalidateCat(installationId);
            ApplyConfiguration(configured);
        }, token), cancellationToken);

    private Task<FilmCatSource> SaveCatConfigurationAsync(string id,
        Func<CancellationToken, Task<IReadOnlyList<CatSourceConfiguration>>> change,
        CancellationToken token) => RunAsync(async ct =>
    {
        FilmCatSource result = null!;
        await InStateAsync(async admission =>
        {
            await LoadAsync(admission).ConfigureAwait(false);
            var configured = await change(admission).ConfigureAwait(false);
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
                catch (Exception error) when (error is OperationCanceledException or BridgeException or StorageException)
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
        try
        {
            var (loaded, rev) = await FilmSourceStore.ReadAsync(database, token).ConfigureAwait(false);
            var deletedSet = loaded.Deleted.ToHashSet();
            var configured = await settings.ReadAsync(token).ConfigureAwait(false);
            var configuredById = configured.ToDictionary(item => item.InstallationId, StringComparer.Ordinal);
            var cats = ConfiguredCats(configured, loaded.Cats
                .Where(cat => configuredById.ContainsKey(cat.InstallationId))
                .Select(cat => cat.ToConfigured(configuredById[cat.InstallationId]) with { Sites = Freeze(cat.Sites.Select(FreezeSite)) }));
            var ids = cats.Select(cat => cat.InstallationId).ToHashSet(StringComparer.Ordinal);
            var videos = loaded.Videos
                .Where(video => ids.Contains(video.Key.InstallationId) && !deletedSet.Contains(video.Key))
                .OrderBy(video => video.Order)
                .Select(FreezeVideo)
                .ToArray();
            token.ThrowIfCancellationRequested();
            foreach (var key in probes.Keys.ToArray()) InvalidateProbe(key);
            document = loaded;
            storageRevision = rev;
            snapshot = new(Freeze(cats), Freeze(videos));
            initialized = true;
        }
        catch (StorageException error) when (error.Error == StorageError.Corrupt)
        { throw new FilmSourceException("INVALID_DOCUMENT"); }
    }

    private async Task CommitAsync(IEnumerable<FilmCatSource> cats, IEnumerable<FilmVideoSource> videos,
        IEnumerable<FilmSourceKey> deleted, CancellationToken token)
    {
        var next = new Snapshot(Freeze(cats), Freeze(videos));
        var candidate = new StoredFilmSourceState(next.Cats.Select(StoredFilmCat.From).ToArray(), next.Videos,
            deleted.OrderBy(key => key.InstallationId, StringComparer.Ordinal).ThenBy(key => key.SiteKey, StringComparer.Ordinal).ToArray());
        var after = storageRevision;
        token.ThrowIfCancellationRequested();
        if (!FilmSourceStore.StateEquals(candidate, document))
        {
            try
            {
                after = await FilmSourceStore.SaveAsync(database, document, candidate, storageRevision, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                initialized = false;
                var (actualState, actualRev) = await FilmSourceStore.ReadAsync(database, CancellationToken.None).ConfigureAwait(false);
                if (actualRev == storageRevision || !FilmSourceStore.StateEquals(actualState, candidate)) throw;
                after = actualRev;
            }
        }
        document = candidate;
        storageRevision = after;
        initialized = true;
        Volatile.Write(ref snapshot, next);
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
}
