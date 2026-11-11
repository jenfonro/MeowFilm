using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

public sealed class FilmSourceService : IAsyncDisposable, IFilmSearchSources
{
    public const string DocumentNamespace = "film.video-sources.v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IDocumentStore store;
    private readonly CatScriptTransport transport;
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
        transport = new(runtime);
        recovery = runtime as IRuntimeRecovery;
        this.store = store;
    }

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
                .Select(site => new FilmSearchSource(current.Cats.Single(cat =>
                    cat.InstallationId == site.Key.InstallationId), site))
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

    public Task<FilmCatSource> ImportLocalAsync(string name, string source, string? installationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        string id = installationId ?? Guid.NewGuid().ToString("N");
        return ManageAsync(id, async token =>
        {
            long sourceRevision = await transport.UploadSourceAsync(id, source, token).ConfigureAwait(false);
            return await transport.CallAsync("install", new
            {
                installationId = id, sourceRevision, name, sourceType = "local", sourceUrl = (string?)null, enabled = true
            }, token).ConfigureAwait(false);
        }, reimport: true, cancellationToken);
    }

    public Task<FilmCatSource> ImportNetworkAsync(string name, string sourceUrl, string? installationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceUrl);
        string id = installationId ?? Guid.NewGuid().ToString("N");
        return ManageAsync(id, token => transport.CallAsync("import", new
        {
            installationId = id, name, sourceUrl, enabled = true
        }, token), reimport: true, cancellationToken);
    }

    public Task<FilmCatSource> EditCatAsync(string installationId, string name, string? sourceUrl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return ManageAsync(installationId, token => transport.CallAsync("edit",
            sourceUrl is null ? new { installationId, name } :
                (object)new { installationId, name, sourceUrl }, token), false, cancellationToken);
    }

    public Task<FilmCatSource> UpdateCatAsync(string installationId, string? source = null,
        CancellationToken cancellationToken = default) => ManageAsync(installationId, async token =>
    {
        var cat = FindCat(installationId);
        if (cat.SourceType == "local")
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(source);
            long sourceRevision = await transport.UploadSourceAsync(installationId, source, token).ConfigureAwait(false);
            return await transport.CallAsync("update", new { installationId, sourceRevision }, token).ConfigureAwait(false);
        }
        if (source is not null) throw new ArgumentException("Network updates use the saved URL.", nameof(source));
        return await transport.CallAsync("update", new { installationId }, token).ConfigureAwait(false);
    }, false, cancellationToken);

    public Task<FilmCatSource> RestartCatAsync(string installationId, CancellationToken cancellationToken = default) =>
        ManageAsync(installationId, token =>
        {
            if (!FindCat(installationId).Enabled) throw new FilmSourceException("DISABLED");
            return transport.CallAsync("restart", new { installationId }, token);
        }, false, cancellationToken);

    public Task<FilmCatSource> EnableCatAsync(string installationId, bool enabled,
        CancellationToken cancellationToken = default) => ManageAsync(installationId,
        token => transport.CallAsync("enable", new { installationId, enabled }, token), false, cancellationToken);

    public Task DeleteCatAsync(string installationId, CancellationToken cancellationToken = default) =>
        RunAsync(token => InManagementAsync(async () =>
        {
            await InitializeCoreAsync(token).ConfigureAwait(false);
            await InStateAsync(_ =>
            {
                FindCat(installationId);
                InvalidateCat(installationId);
                changingCats.Add(installationId);
                return Task.CompletedTask;
            }, token).ConfigureAwait(false);
            try
            {
                JsonElement receipt;
                try { receipt = await transport.CallAsync("uninstall", new { installationId }, token).ConfigureAwait(false); }
                catch (FilmSourceException)
                {
                    // An error receipt can follow a committed uninstall; status decides what remains.
                    await ReconcileManagementFailureAsync(installationId).ConfigureAwait(false);
                    throw;
                }
                string? cleanupError = null;
                if (receipt.ValueKind == JsonValueKind.Object && receipt.TryGetProperty("failures", out var failures) &&
                    failures.ValueKind == JsonValueKind.Array)
                {
                    foreach (var failure in failures.EnumerateArray())
                    {
                        if (CatScriptTransport.OptionalText(failure, "installationId") != installationId) continue;
                        cleanupError = CatScriptTransport.OptionalText(failure, "code") ?? "INVALID_RESPONSE";
                        break;
                    }
                }
                // The uninstall receipt commits this deletion even if the subsequent status call fails.
                await InStateAsync(async ct =>
                {
                    var cats = Cats.Where(cat => cat.InstallationId != installationId).ToArray();
                    var videos = Sorted(Videos.Where(video => video.Key.InstallationId != installationId));
                    await CommitAsync(cats, videos, document.Deleted.Where(key => key.InstallationId != installationId), ct)
                        .ConfigureAwait(false);
                }, token).ConfigureAwait(false);
                await SynchronizeAsync(null, token).ConfigureAwait(false);
                if (cleanupError is not null) throw new FilmSourceException(cleanupError);
            }
            catch (Exception error) when (error is OperationCanceledException or BridgeException or DocumentStoreException)
            {
                await ReconcileManagementFailureAsync(installationId).ConfigureAwait(false);
                throw;
            }
            finally { await EndCatChangeAsync(installationId).ConfigureAwait(false); }
        }, token), cancellationToken);

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
        bool reimport, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return RunAsync(async token =>
        {
            FilmCatSource? result = null;
            await InManagementAsync(async () =>
            {
                await InitializeCoreAsync(token).ConfigureAwait(false);
                IReadOnlySet<FilmSourceKey>? restoreKeys = null;
                await InStateAsync(_ =>
                {
                    if (!reimport) FindCat(id);
                    else restoreKeys = document.Deleted.Where(key => key.InstallationId == id).ToHashSet();
                    InvalidateCat(id);
                    changingCats.Add(id);
                    return Task.CompletedTask;
                }, token).ConfigureAwait(false);
                try
                {
                    JsonElement receipt;
                    try { receipt = await operation(token).ConfigureAwait(false); }
                    catch (FilmSourceException)
                    {
                        // Failed management may have persisted updateState/updateError in Node.
                        await ReconcileManagementFailureAsync(id).ConfigureAwait(false);
                        throw;
                    }
                    FilmCatSource? restored = null;
                    if (reimport)
                    {
                        CatScriptTransport.Require(receipt.ValueKind == JsonValueKind.Object);
                        var instance = receipt.TryGetProperty("instances", out var instances)
                            ? instances.EnumerateArray().FirstOrDefault(value => CatScriptTransport.OptionalText(value, "installationId") == id)
                            : receipt;
                        var imported = ParseCat(instance);
                        if (imported.InstallationId != id) throw new FilmSourceException("INVALID_RESPONSE");
                        if (imported.State == "ready" && imported.Enabled) restored = imported;
                    }
                    await SynchronizeAsync(restored, token, restoreKeys).ConfigureAwait(false);
                    result = FindCat(id);
                    if (reimport && restored is null) throw new FilmSourceException("IMPORT_FAILED", result.Error);
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

    private async Task SynchronizeAsync(FilmCatSource? restored, CancellationToken token, IReadOnlySet<FilmSourceKey>? restoreKeys = null)
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
                if (restored is not null && restored.InstallationId == cat.InstallationId &&
                    restored.Generation == cat.Generation && restored.SourceHash == cat.SourceHash)
                {
                    var imported = restored.Sites.Select(site => site.Key).ToHashSet(StringComparer.Ordinal);
                    deleted.RemoveWhere(key => key.InstallationId == cat.InstallationId &&
                        siteKeys.Contains(key.SiteKey) && imported.Contains(key.SiteKey) && restoreKeys?.Contains(key) == true);
                }
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
        var stored = await store.ReadAsync(DocumentNamespace, token).ConfigureAwait(false);
        StoredState loaded;
        try
        {
            loaded = stored.Data is null ? new(1, [], [], []) :
                JsonSerializer.Deserialize<StoredState>(stored.Data, Json) ?? throw new FilmSourceException("INVALID_DOCUMENT");
            if (loaded.Version != 1 || loaded.Cats is null || loaded.Videos is null || loaded.Deleted is null)
                throw new FilmSourceException("INVALID_DOCUMENT");
            if (loaded.Cats.Any(cat => cat is null || string.IsNullOrWhiteSpace(cat.InstallationId) ||
                    cat.SourceType is not ("local" or "network") || cat.Name is null || cat.Sites is null ||
                    cat.Sites.Any(site => site is null || site.Key is null || site.Name is null || site.Api is null ||
                        site.Actions is null || site.Methods is null)) ||
                loaded.Cats.Select(cat => cat.InstallationId).Distinct(StringComparer.Ordinal).Count() != loaded.Cats.Count ||
                loaded.Videos.Any(video => video is null || video.Key is null || video.Options is null ||
                    string.IsNullOrWhiteSpace(video.Key.SiteKey) || video.Name is null || video.Api is null ||
                    video.Actions is null || video.Methods is null || video.Detection is { Steps: null } ||
                    !loaded.Cats.Any(cat => cat.InstallationId == video.Key.InstallationId)) ||
                loaded.Videos.Select(video => video.Key).Distinct().Count() != loaded.Videos.Count ||
                loaded.Deleted.Any(key => key is null || !loaded.Cats.Any(cat => cat.InstallationId == key.InstallationId)) ||
                loaded.Videos.Any(video => loaded.Deleted.Contains(video.Key)) ||
                loaded.Videos.Count(video => video.Options.AggregateImages) > 1)
                throw new FilmSourceException("INVALID_DOCUMENT");
            var cats = loaded.Cats.Select(cat => cat.ToSnapshot()).ToArray();
            var videos = loaded.Videos.OrderBy(video => video.Order).Select(FreezeVideo).ToArray();
            token.ThrowIfCancellationRequested();
            foreach (var key in probes.Keys.ToArray()) InvalidateProbe(key);
            document = loaded with { Videos = Freeze(videos) };
            fingerprint = stored.Fingerprint;
            snapshot = new(Freeze(cats), Freeze(videos));
            initialized = true;
        }
        catch (JsonException) { throw new FilmSourceException("INVALID_DOCUMENT"); }
        finally { if (stored.Data is not null) CryptographicOperations.ZeroMemory(stored.Data); }
    }

    private async Task CommitAsync(IEnumerable<FilmCatSource> cats, IEnumerable<FilmVideoSource> videos,
        IEnumerable<FilmSourceKey> deleted, CancellationToken token)
    {
        var next = new Snapshot(Freeze(cats), Freeze(videos));
        var candidate = new StoredState(1, next.Cats.Select(StoredCat.From).ToArray(), next.Videos,
            deleted.OrderBy(key => key.InstallationId, StringComparer.Ordinal).ThenBy(key => key.SiteKey, StringComparer.Ordinal).ToArray());
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(candidate, Json);
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

    private static FilmCatSource ParseCat(JsonElement value)
    {
        string Text(string property) => CatScriptTransport.Text(value, property);
        string? Optional(string property) => CatScriptTransport.OptionalText(value, property);
        long? Timestamp(string property) => !value.TryGetProperty(property, out var item) || item.ValueKind == JsonValueKind.Null
            ? null : CatScriptTransport.Integer(value, property);
        CatScriptTransport.Require(value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("enabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            value.TryGetProperty("sites", out var sites) && sites.ValueKind == JsonValueKind.Array);
        string id = Text("installationId"), name = Text("name"), type = Text("sourceType"), update = Text("updateState");
        CatScriptTransport.Require(id.Length > 0 && name.Length > 0 && type is "local" or "network" &&
            update is "idle" or "updated" or "unchanged" or "failed");
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
        return new(id, name, type, Optional("sourceUrl"), value.GetProperty("enabled").GetBoolean(),
            Timestamp("importedAt"), Timestamp("updatedAt"), update, Optional("updateError"),
            CatScriptTransport.Integer(value, "generation"), Text("state"), Optional("sourceHash"), Optional("error"), Freeze(described));
    }

    private sealed record Snapshot(IReadOnlyList<FilmCatSource> Cats, IReadOnlyList<FilmVideoSource> Videos);
    private sealed record StoredState(int Version, IReadOnlyList<StoredCat> Cats,
        IReadOnlyList<FilmVideoSource> Videos, IReadOnlyList<FilmSourceKey> Deleted);
    // Only a local cache of Node management metadata; no port, generation, source bytes or runtime ownership.
    private sealed record StoredCat(string InstallationId, string Name, string SourceType, string? SourceUrl,
        bool Enabled, long? ImportedAt, long? UpdatedAt, string UpdateState, string? UpdateError,
        string? SourceHash, IReadOnlyList<FilmSourceSite> Sites)
    {
        internal static StoredCat From(FilmCatSource cat) => new(cat.InstallationId, cat.Name, cat.SourceType,
            cat.SourceUrl, cat.Enabled, cat.ImportedAt, cat.UpdatedAt, cat.UpdateState, cat.UpdateError, cat.SourceHash, cat.Sites);
        internal FilmCatSource ToSnapshot() => new(InstallationId, Name, SourceType, SourceUrl, Enabled,
            ImportedAt, UpdatedAt, UpdateState, UpdateError, 0, Enabled ? "stopped" : "disabled",
            SourceHash, null, Freeze(Sites.Select(FreezeSite)));
    }
}
