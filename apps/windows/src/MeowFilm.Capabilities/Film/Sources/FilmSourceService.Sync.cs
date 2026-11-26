using System.Collections.ObjectModel;
using System.Text.Json;
using MeowFilm.Core.Debug;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

public sealed partial class FilmSourceService
{
    private async Task AwaitStartupAsync(CancellationToken token)
    {
        // Observe actual "starting" completion independently of any page, bounded by timeout.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(StartupConvergenceTimeout);
        try
        {
            while (Cats.Any(cat => cat.State is "starting" or "stopping"))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token).ConfigureAwait(false);
                await InManagementAsync(() => SynchronizeAsync(deadline.Token), deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            DebugLog.Warn(DebugCategories.FilmCatalog, "sources", "startup.convergence.timeout",
                new { timeoutMs = (int)StartupConvergenceTimeout.TotalMilliseconds });
        }
    }

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
        var task = TryRunAsync(async token =>
        {
            try
            {
                var connection = await source.CaptureConnectionAsync(token).ConfigureAwait(false);
                using var activeConnection = CancellationTokenSource.CreateLinkedTokenSource(token, connection.Revoked);
                var reply = await connection.Client.CallAsync("runtime.settings.changed", new { block = "cat.sources" },
                    TimeSpan.FromSeconds(5), activeConnection.Token).ConfigureAwait(false);
                if (!reply.GetProperty("ok").GetBoolean()) throw new BridgeException(reply.GetProperty("code").GetString()!);
                await InManagementAsync(() => SynchronizeAsync(activeConnection.Token), activeConnection.Token).ConfigureAwait(false);
                await AwaitStartupAsync(activeConnection.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { await ReportRuntimeErrorAsync(error).ConfigureAwait(false); }
        }, CancellationToken.None);
        if (task is not null) _ = ObserveAsync(task);
        static async Task ObserveAsync(Task work)
        {
            try { await work.ConfigureAwait(false); }
            catch (Exception error)
            {
                DebugLog.Warn(DebugCategories.FilmCatalog, "sources", "config.notify.failed", error: error);
            }
        }
    }

    private async Task<bool> ReconcileManagementFailureOnceAsync(string id)
    {
        // RPC failure or a failed local save can follow a committed Node change.
        // Retire the cached generation before observing the actual state once under managementGate.
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
            // Never use a cancelled page token or replay the management call.
            // Failed management, including its local save, cannot restore tombstones.
            await SynchronizeAsync(owner.Token).ConfigureAwait(false);
            return Cats.Any(cat => cat.InstallationId == id && cat.State is "starting" or "stopping");
        }
        catch (Exception error)
        {
            // Preserve the original management error, but never retain a ready retired
            // generation when reconciliation fails or shutdown interrupts it.
            await ReportRuntimeErrorAsync(error).ConfigureAwait(false);
            return false;
        }
    }

    private void ScheduleReconcileConvergence(string id)
    {
        var task = TryRunAsync(async token =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(StartupConvergenceTimeout);
            try
            {
                while (Cats.Any(cat => cat.InstallationId == id && cat.State is "starting" or "stopping"))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token).ConfigureAwait(false);
                    await InManagementAsync(() => SynchronizeAsync(deadline.Token), deadline.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                DebugLog.Warn(DebugCategories.FilmCatalog, "sources", "reconcile.convergence.timeout",
                    new { installationId = id, timeoutMs = (int)StartupConvergenceTimeout.TotalMilliseconds });
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                await ReportRuntimeErrorAsync(error).ConfigureAwait(false);
            }
        }, CancellationToken.None);
        if (task is not null) _ = ObserveReconcileAsync(task);

        static async Task ObserveReconcileAsync(Task work)
        {
            try { await work.ConfigureAwait(false); }
            catch (Exception error)
            {
                DebugLog.Warn(DebugCategories.FilmCatalog, "sources", "reconcile.convergence.failed", error: error);
            }
        }
    }

    private async Task SynchronizeAsync(CancellationToken token)
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
        var beforeVideos = document.Videos;
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
        bool videoConfigChanged = !VideoSettingsEqual(beforeVideos, candidate.Videos);
        document = candidate;
        storageRevision = after;
        initialized = true;
        Volatile.Write(ref snapshot, next);
        if (videoConfigChanged)
        {
            await NotifyVideoSourcesChangedAsync().ConfigureAwait(false);
        }
    }

    private static bool VideoSettingsEqual(IReadOnlyList<FilmVideoSource> a, IReadOnlyList<FilmVideoSource> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Key != b[i].Key || a[i].Name != b[i].Name || a[i].Order != b[i].Order || a[i].Options != b[i].Options)
                return false;
        }
        return true;
    }

    private async Task NotifyVideoSourcesChangedAsync()
    {
        if (!RuntimeActive) return;
        if (Runtime is IRuntimeConnectionSource source)
        {
            try
            {
                var connection = await source.CaptureConnectionAsync().ConfigureAwait(false);
                await connection.Client.CallAsync("runtime.settings.changed", new { block = "film.video-sources" },
                    TimeSpan.FromSeconds(5), connection.Revoked).ConfigureAwait(false);
                return;
            }
            catch
            {
                // Durable storage is already committed; fall through to async notification if supported.
            }
        }
        (Runtime as IRuntimeSettingsNotifications)?.SettingsChanged("film.video-sources");
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
}
