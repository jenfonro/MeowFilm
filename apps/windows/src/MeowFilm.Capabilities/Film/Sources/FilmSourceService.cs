using System.Text.Json;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

public sealed partial class FilmSourceService : IAsyncDisposable
{
    public const string StorageOwner = "film.video-sources.v1";
    private static readonly TimeSpan StartupConvergenceTimeout = TimeSpan.FromSeconds(30);
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
    private Task? preparation, closing;
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
    public IReadOnlyList<FilmCatSource> Cats => Volatile.Read(ref snapshot).Cats;
    public IReadOnlyList<FilmVideoSource> Videos => Volatile.Read(ref snapshot).Videos;
    internal (IReadOnlyList<FilmCatSource> Cats, IReadOnlyList<FilmVideoSource> Videos) ReadSnapshot()
    {
        var current = Volatile.Read(ref snapshot);
        return (current.Cats, current.Videos);
    }
    public string? RuntimeError => Volatile.Read(ref runtimeError);
    public event EventHandler? Changed;

    public Task<IReadOnlyList<FilmSearchSource>> ReadSearchSourcesAsync(CancellationToken token) =>
        RunAsync(async ct =>
        {
            await InManagementAsync(() => SynchronizeAsync(ct), ct).ConfigureAwait(false);
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
                await SynchronizeAsync(token).ConfigureAwait(false);
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
                await SynchronizeAsync(token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            await AwaitStartupAsync(token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await ReportRuntimeErrorAsync(error).ConfigureAwait(false);
            throw;
        }
    }, cancellationToken);

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

    public async Task<FilmCatSource> UpdateCatAsync(string installationId, string? filePath = null,
        CancellationToken cancellationToken = default)
    {
        var cat = await RunAsync(async token =>
        {
            FilmCatSource found = null!;
            await InStateAsync(async ct =>
            {
                await LoadAsync(ct).ConfigureAwait(false);
                found = FindCat(installationId);
            }, token).ConfigureAwait(false);
            return found;
        }, cancellationToken).ConfigureAwait(false);
        if (cat.SourceType == "local")
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            return await EditCatAsync(installationId, cat.Name, "local", null, filePath, cancellationToken).ConfigureAwait(false);
        }
        if (filePath is not null) throw new ArgumentException("Network updates use the saved URL.", nameof(filePath));
        return await ManageAsync(installationId, token => transport.CallAsync("update", new { installationId }, token),
            cancellationToken).ConfigureAwait(false);
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
            ApplyConfiguration(configured);
            ConfigurationChanged();
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
            bool needsConvergence = false;
            try
            {
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
                            needsConvergence = await ReconcileManagementFailureOnceAsync(id).ConfigureAwait(false);
                            throw;
                        }
                        await SynchronizeAsync(token).ConfigureAwait(false);
                        result = FindCat(id);
                    }
                    catch (Exception error) when (error is OperationCanceledException or BridgeException or StorageException)
                    {
                        needsConvergence = await ReconcileManagementFailureOnceAsync(id).ConfigureAwait(false);
                        throw;
                    }
                    finally { await EndCatChangeAsync(id).ConfigureAwait(false); }
                }, token).ConfigureAwait(false);
            }
            finally
            {
                // Never hold managementGate while waiting for bounded starting/stopping convergence.
                if (needsConvergence) ScheduleReconcileConvergence(id);
            }
            return result!;
        }, cancellationToken);
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

    private Task? TryRunAsync(Func<CancellationToken, Task> action, CancellationToken token)
    {
        lock (lifecycle)
        {
            if (closed || preparing) return null;
            active++;
            return ExecuteRunAsync(async ct => { await action(ct).ConfigureAwait(false); return true; }, token);
        }
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
            return ExecuteRunAsync(action, token);
        }
    }

    private async Task<T> ExecuteRunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token)
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

    public Task PrepareShutdownAsync(CancellationToken cancellationToken = default)
    {
        Task prepare;
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            prepare = PrepareLocked();
        }
        return cancellationToken.CanBeCanceled ? prepare.WaitAsync(cancellationToken) : prepare;
    }

    private Task PrepareLocked()
    {
        if (preparation is not null) return preparation;
        preparing = true;
        Task drain = Task.CompletedTask;
        if (active > 0)
        {
            preparationDrained ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            drain = preparationDrained.Task;
        }
        return preparation = DrainPreparationAsync(pending.CancelAsync(), drain);

        static async Task DrainPreparationAsync(Task cancellation, Task work)
        {
            try { await cancellation.ConfigureAwait(false); }
            catch (AggregateException) { /* Callback errors must not skip physical drain. */ }
            await work.ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        Task? prepare;
        lock (lifecycle)
        {
            if (closing is not null) return new(closing);
            closed = true;
            prepare = preparation;
            if (active == 0) drained.TrySetResult();
            closing = CloseAsync(prepare);
            return new(closing);
        }
        async Task CloseAsync(Task? pendingPreparation)
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            if (pendingPreparation is not null) await pendingPreparation.ConfigureAwait(false);
            await drained.Task.ConfigureAwait(false);
            lifetime.Dispose();
            pending.Dispose();
            managementGate.Dispose();
            stateGate.Dispose();
            Changed = null;
        }
    }

    private sealed record Snapshot(IReadOnlyList<FilmCatSource> Cats, IReadOnlyList<FilmVideoSource> Videos);
}
