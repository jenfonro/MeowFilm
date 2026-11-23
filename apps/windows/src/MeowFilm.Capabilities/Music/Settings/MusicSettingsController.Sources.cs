using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using MeowFilm.Platform.Files;

namespace MeowFilm.Capabilities;

/// <summary>
/// Source management projection: built-in providers and script installations in one table.
/// The shared music module owns script facts; built-in check results live for this session.
/// </summary>
public sealed partial class MusicSettingsController
{
    private static readonly string[] OfficialPlatforms = ["wy", "tx", "kg"];
    private readonly Dictionary<string, MusicSourcePlatformState> officialChecks = new(StringComparer.Ordinal);
    private readonly HashSet<string> checkingSources = new(StringComparer.Ordinal);
    private HashSet<string> officialEnabled = new(OfficialPlatforms, StringComparer.Ordinal);
    private long officialSourcesRevision;
    private bool officialSourcesLoaded, scriptsUnavailable;
    private CancellationTokenSource? sourceTransfer;
    private long sourceManagementGeneration;
    private bool sourceManagementActive = true;
    private bool suppressSourceRowBindings;

    internal bool SourceManagementCurrent(long generation) => sourceManagementActive &&
        generation == sourceManagementGeneration && !stopping && !disposed;

    internal void RetireSourceManagement()
    {
        sourceManagementActive = false;
        sourceManagementGeneration++;
        officialSourcesLoaded = false;
    }

    public ObservableCollection<MusicSourceRow> Sources { get; } = [];
    public bool CanEditOfficialSources => CanEdit && officialSourcesLoaded;
    public bool CanEditScripts => CanEdit && !scriptsUnavailable;
    public bool CanCheckSelectedSources => !stopping && Sources.Any(row => row.Selected && !row.Checking);
    public bool CanRemoveSelectedSources => CanEditScripts && Sources.Any(row => row.Selected && row.IsScript);
    public bool CanCancelSourceTransfer => sourceTransfer is not null;

    internal void SourceBindings()
    {
        bool editOfficial = CanEditOfficialSources;
        bool editScripts = CanEditScripts;
        foreach (var row in Sources)
            row.CanToggle = row.IsBuiltIn ? editOfficial : editScripts;
        Changed(nameof(CanEditOfficialSources));
        Changed(nameof(CanEditScripts));
        Changed(nameof(CanCheckSelectedSources));
        Changed(nameof(CanRemoveSelectedSources));
        Changed(nameof(CanCancelSourceTransfer));
    }

    internal void SetSelectedSources(IReadOnlySet<string> keys)
    {
        bool changed = false;
        suppressSourceRowBindings = true;
        try
        {
            foreach (var row in Sources)
            {
                bool next = keys.Contains(row.Key);
                if (row.Selected == next) continue;
                row.Selected = next;
                changed = true;
            }
        }
        finally { suppressSourceRowBindings = false; }
        if (changed) SourceBindings();
    }

    public Task RefreshSourceManagementAsync()
    {
        sourceManagementActive = true;
        return RunSourceManagementAsync(async generation =>
        {
            await ReadOnlineSettings(lifetime.Token, generation);
            if (SourceManagementCurrent(generation)) await ReloadSources(lifetime.Token, generation);
        });
    }

    internal async Task ReadOnlineSettings(CancellationToken token, long? sourceGeneration = null)
    {
        var reply = await CallSettledReceipt("music.quality.settings.get", null, token);
        token.ThrowIfCancellationRequested();
        if (sourceGeneration is { } generation && !SourceManagementCurrent(generation)) return;
        AcceptQuality(reply);
        var settings = await Call("music.settings.get", null, token);
        token.ThrowIfCancellationRequested();
        if (sourceGeneration is { } current && !SourceManagementCurrent(current)) return;
        OnPublishOnlineLyricsSettings(settings.GetProperty("settings"));
    }

    private Task RunSourceManagementAsync(Func<long, Task> action)
    {
        long generation = sourceManagementGeneration;
        long stamp = ctx.NextStatusEpoch();
        if (SourceManagementCurrent(generation)) ctx.ClearTransientStatus();
        return RunAsync(async () =>
        {
            try { await action(generation); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                if (SourceManagementCurrent(generation) && stamp == ctx.StatusEpoch)
                    ctx.SetStatus(ErrorText(error));
            }
        }, publishStatus: false);
    }

    private async Task ReloadSources(CancellationToken token, long generation)
    {
        if (!SourceManagementCurrent(generation)) return;
        await RefreshOfficialSources(token, generation);
        if (!SourceManagementCurrent(generation)) return;
        JsonElement[] scripts = [];
        try
        {
            var reply = await Call("music.sources.list", cancellationToken: token);
            if (!SourceManagementCurrent(generation)) return;
            scripts = reply.GetProperty("items").EnumerateArray().Select(item => item.Clone()).ToArray();
            scriptsUnavailable = false;
        }
        catch (MusicOperationException error) when (error.Code == "SCRIPT_RUNTIME_UNAVAILABLE")
        {
            if (!SourceManagementCurrent(generation)) return;
            scriptsUnavailable = true;
            ctx.SetStatus(ErrorText(error));
        }
        PublishSources(scripts);
    }

    private void PublishSources(IEnumerable<JsonElement> scripts)
    {
        bool editOfficial = CanEditOfficialSources;
        bool editScripts = CanEditScripts;
        var incoming = OfficialPlatforms.Select(platform => MusicSourceRow.BuiltIn(platform, officialEnabled.Contains(platform),
                officialChecks.GetValueOrDefault(platform)))
            .Concat(scripts.Select(MusicSourceRow.Read))
            .ToArray();
        var existingByKey = Sources.ToDictionary(row => row.Key, StringComparer.Ordinal);
        var nextRows = new MusicSourceRow[incoming.Length];
        suppressSourceRowBindings = true;
        try
        {
            for (int index = 0; index < incoming.Length; index++)
            {
                var candidate = incoming[index];
                if (existingByKey.TryGetValue(candidate.Key, out var existing) && existing.MatchesData(candidate))
                {
                    existing.Checking = checkingSources.Contains(existing.Key);
                    existing.CanToggle = existing.IsBuiltIn ? editOfficial : editScripts;
                    nextRows[index] = existing;
                }
                else
                {
                    if (existing is not null)
                    {
                        candidate.Selected = existing.Selected;
                        existing.PropertyChanged -= SourceRowChanged;
                    }
                    candidate.Checking = checkingSources.Contains(candidate.Key);
                    candidate.CanToggle = candidate.IsBuiltIn ? editOfficial : editScripts;
                    candidate.PropertyChanged += SourceRowChanged;
                    nextRows[index] = candidate;
                }
            }
            var retained = nextRows.ToHashSet(ReferenceEqualityComparer.Instance);
            for (int index = Sources.Count - 1; index >= 0; index--)
            {
                if (retained.Contains(Sources[index])) continue;
                Sources[index].PropertyChanged -= SourceRowChanged;
                Sources.RemoveAt(index);
            }
            for (int index = 0; index < nextRows.Length; index++)
            {
                var row = nextRows[index];
                if (index < Sources.Count && ReferenceEquals(Sources[index], row)) continue;
                int currentIndex = Sources.IndexOf(row);
                if (currentIndex >= 0) Sources.Move(currentIndex, index);
                else Sources.Insert(index, row);
            }
        }
        finally { suppressSourceRowBindings = false; }
        SourceBindings();
        OnPlaybackSourceBindings();
    }

    private void SourceRowChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (suppressSourceRowBindings) return;
        if (args.PropertyName is nameof(MusicSourceRow.Selected) or nameof(MusicSourceRow.Checking)) SourceBindings();
    }

    private void ReplaceSource(MusicSourceRow next)
    {
        int index = Sources.ToList().FindIndex(row => row.Key == next.Key);
        if (index < 0) return;
        var previous = Sources[index];
        previous.PropertyChanged -= SourceRowChanged;
        next.Selected = previous.Selected;
        next.Checking = checkingSources.Contains(next.Key);
        next.CanToggle = next.IsBuiltIn ? CanEditOfficialSources : CanEditScripts;
        next.PropertyChanged += SourceRowChanged;
        Sources[index] = next;
        SourceBindings();
        OnPlaybackSourceBindings();
    }

    private async Task RefreshOfficialSources(CancellationToken token, long generation)
    {
        try
        {
            var reply = await Call("music.officialSources.settings.get", cancellationToken: token);
            if (SourceManagementCurrent(generation)) PublishOfficialSources(reply.GetProperty("settings"));
        }
        catch
        {
            if (SourceManagementCurrent(generation)) { officialSourcesLoaded = false; SourceBindings(); }
            throw;
        }
    }

    private void PublishOfficialSources(JsonElement value)
    {
        long revision = MusicDto.Integer(value, "revision");
        var enabled = value.GetProperty("enabled").EnumerateArray().Select(item => item.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        bool changed = officialSourcesRevision != revision || !officialEnabled.SetEquals(enabled);
        officialSourcesRevision = revision;
        officialSourcesLoaded = true;
        officialEnabled = enabled;
        if (changed) { ctx.NotifyMusicSourcesChanged(); OnCancelMusicPreload(); }
        SourceBindings();
    }

    public Task SetSourceEnabledAsync(MusicSourceRow? row, bool enabled)
    {
        if (row is null || !Sources.Contains(row) || row.Enabled == enabled) return Task.CompletedTask;
        if (row.IsBuiltIn)
        {
            if (!CanEditOfficialSources) return Task.CompletedTask;
            return RunSourceManagementAsync(async generation =>
            {
                var next = officialEnabled.ToHashSet(StringComparer.Ordinal);
                if (enabled) next.Add(row.Platform!); else next.Remove(row.Platform!);
                bool saved = false;
                try
                {
                    var reply = await CallSettledReceipt("music.officialSources.settings.save", new
                    {
                        ifRevision = officialSourcesRevision,
                        enabled = OfficialPlatforms.Where(next.Contains).ToArray()
                    }, lifetime.Token);
                    if (SourceManagementCurrent(generation))
                    {
                        PublishOfficialSources(reply.GetProperty("settings"));
                        foreach (var platform in OfficialPlatforms)
                        {
                            var updated = MusicSourceRow.BuiltIn(platform, officialEnabled.Contains(platform),
                                officialChecks.GetValueOrDefault(platform));
                            if (Sources.FirstOrDefault(item => item.Key == updated.Key) is { } current && !current.MatchesData(updated))
                                ReplaceSource(updated);
                        }
                        saved = true;
                    }
                }
                catch (Exception error)
                {
                    if (SourceManagementCurrent(generation)) ctx.SetStatus(ErrorText(error));
                }
                finally
                {
                    if (!saved && SourceManagementCurrent(generation))
                    try { await ReloadSources(lifetime.Token, generation); }
                    catch (Exception error)
                    {
                        if (SourceManagementCurrent(generation)) ctx.SetStatus(ErrorText(error));
                    }
                }
            });
        }
        return ScriptActionAsync(row, enabled ? "enable" : "disable");
    }

    public async Task RemoveSourceAsync(MusicSourceRow? row)
    {
        if (row is not { IsScript: true } || !Sources.Contains(row) || !CanEditScripts) return;
        long confirmGeneration = sourceManagementGeneration;
        if (!await ConfirmAsync($"删除音源“{row.Name}”？", "删除音源", lifetime.Token)) return;
        if (!SourceManagementCurrent(confirmGeneration) || !Sources.Contains(row)) return;
        await ScriptActionAsync(row, "remove");
    }

    public async Task RemoveSelectedSourcesAsync()
    {
        long confirmGeneration = sourceManagementGeneration;
        var rows = Sources.Where(row => row.Selected && row.IsScript).ToArray();
        if (!CanEditScripts || rows.Length == 0 || !await ConfirmAsync($"删除所选的 {rows.Length} 个音源？", "删除音源", lifetime.Token))
            return;
        if (!CanEditScripts || !SourceManagementCurrent(confirmGeneration)) return;
        rows = rows.Where(row => Sources.Contains(row)).ToArray();
        if (rows.Length == 0) return;
        await RunSourceManagementAsync(async generation =>
        {
            if (!CanEditScripts || !SourceManagementCurrent(generation)) return;
            bool removed = false;
            try
            {
                foreach (var row in rows)
                {
                    if (!SourceManagementCurrent(generation) || !Sources.Contains(row)) continue;
                    await BeforeSourceChange(row.InstallationId!, retire: true);
                    await CallSettledReceipt("music.sources.remove", new { installationId = row.InstallationId }, lifetime.Token);
                    removed = true;
                }
            }
            finally
            {
                if (removed) ctx.NotifyMusicSourcesChanged();
                if (SourceManagementCurrent(generation)) await ReloadSources(lifetime.Token, generation);
            }
        });
    }

    public async Task RollbackSourceAsync(MusicSourceRow? row)
    {
        if (row is not { IsScript: true, RollbackAvailable: true } || !Sources.Contains(row) || !CanEditScripts) return;
        long confirmGeneration = sourceManagementGeneration;
        if (!await ConfirmAsync($"回退音源“{row.Name}”？", "回退音源", lifetime.Token)) return;
        if (!SourceManagementCurrent(confirmGeneration) || !Sources.Contains(row)) return;
        await ScriptActionAsync(row, "rollback");
    }

    private Task ScriptActionAsync(MusicSourceRow row, string action)
    {
        if (!CanEditScripts) return Task.CompletedTask;
        return RunSourceManagementAsync(async generation =>
        {
            if (!Sources.Contains(row)) return;
            await BeforeSourceChange(row.InstallationId!, retire: action != "enable");
            try { await CallSettledReceipt("music.sources." + action, new { installationId = row.InstallationId }, lifetime.Token); }
            finally { ctx.NotifyMusicSourcesChanged(); await ReloadSources(lifetime.Token, generation); }
        });
    }

    /// <summary>Content or enablement changes revoke new parsing, not already accepted historical media.</summary>
    private async Task BeforeSourceChange(string installation, bool retire)
    {
        ctx.NotifyMusicSourcesChanged();
        OnCancelMusicPreload();
        if (!retire) return;
        await OnRevokeChangedMusicSourceAsync(installation);
    }

    /// <summary>Full management details of one script source, for its edit surface.</summary>
    public async Task<string?> ReadSourceAddressAsync(MusicSourceRow row)
    {
        if (row is not { Kind: MusicSourceKind.Network } || !Sources.Contains(row)) return null;
        long generation = sourceManagementGeneration;
        try
        {
            var reply = await Call("music.sources.detail", new { installationId = row.InstallationId }, lifetime.Token);
            return SourceManagementCurrent(generation)
                ? MusicDto.Text(reply.GetProperty("item").GetProperty("origin"), "url") : null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception error)
        {
            if (SourceManagementCurrent(generation)) ctx.SetStatus(ErrorText(error));
            return null;
        }
    }

    public Task EditSourceAsync(MusicSourceRow? row, string name, string? url)
    {
        if (row is not { IsScript: true } || !Sources.Contains(row) || !CanEditScripts) return Task.CompletedTask;
        var parameters = new Dictionary<string, object> { ["installationId"] = row.InstallationId! };
        if (name.Trim() != row.Name) parameters["name"] = name;
        if (row.Kind == MusicSourceKind.Network && url is not null) parameters["url"] = url;
        if (parameters.Count == 1) return Task.CompletedTask;
        return RunSourceManagementAsync(async generation =>
        {
            try { await CallSettledReceipt("music.sources.edit", parameters, lifetime.Token); }
            finally { await ReloadSources(lifetime.Token, generation); }
        });
    }

    public Task UpdateSourceAsync(MusicSourceRow? row)
    {
        if (row is not { IsScript: true } || !Sources.Contains(row) || !CanEditScripts) return Task.CompletedTask;
        return RunSourceManagementAsync(async generation =>
        {
            if (row.Kind == MusicSourceKind.Local)
            {
                if (await MusicDialogs.SelectScriptFileAsync(ctx.ModalOwner(MusicModalAnchor.Settings)) is not { } path) return;
                if (!SourceManagementCurrent(generation) || !Sources.Contains(row)) return;
                await ChangeSourceFilesAsync([path], row, generation);
                return;
            }
            var request = BeginSourceTransfer();
            try
            {
                await BeforeSourceChange(row.InstallationId!, retire: true);
                await CallSettledReceipt("music.sources.update", new { installationId = row.InstallationId }, request.Token);
            }
            finally { EndSourceTransfer(request); ctx.NotifyMusicSourcesChanged(); await ReloadSources(lifetime.Token, generation); }
        });
    }

    public Task ImportSourceAsync()
    {
        if (!CanEditScripts) return Task.CompletedTask;
        return RunSourceManagementAsync(async generation =>
        {
            if (await MusicDialogs.SelectScriptFilesAsync(ctx.ModalOwner(MusicModalAnchor.Settings)) is not { Length: > 0 } paths) return;
            if (!SourceManagementCurrent(generation)) return;
            await ChangeSourceFilesAsync(paths, null, generation);
        });
    }

    public Task ImportSourceFromUrlAsync(string address)
    {
        if (!CanEditScripts) return Task.CompletedTask;
        return RunSourceManagementAsync(async generation =>
        {
            var request = BeginSourceTransfer();
            try { await CallSettledReceipt("music.sources.download", new { url = address }, request.Token); }
            finally { EndSourceTransfer(request); await ReloadSources(lifetime.Token, generation); }
        });
    }

    private async Task ChangeSourceFilesAsync(string[] paths, MusicSourceRow? update, long generation)
    {
        var request = BeginSourceTransfer();
        try
        {
            if (update is not null) await BeforeSourceChange(update.InstallationId!, retire: true);
            foreach (var path in paths)
            {
                var snapshot = await SelectedScriptSnapshot.ReadAsync(path, request.Token);
                var reply = await application.ImportSourceAsync(snapshot.Bytes, snapshot.Basename,
                    update?.InstallationId, request.Token);
                try { ValidateReply(update is null ? "music.sources.add" : "music.sources.update", reply); }
                catch (MusicOperationException error) when (error.Code == "CANCELLED" && request.IsCancellationRequested)
                { throw new OperationCanceledException(request.Token); }
            }
        }
        finally
        {
            EndSourceTransfer(request);
            if (update is not null) ctx.NotifyMusicSourcesChanged();
            await ReloadSources(lifetime.Token, generation);
        }
    }

    private CancellationTokenSource BeginSourceTransfer()
    {
        sourceTransfer?.Cancel();
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        sourceTransfer = request; SourceBindings();
        return request;
    }

    private void EndSourceTransfer(CancellationTokenSource request)
    {
        if (ReferenceEquals(sourceTransfer, request)) sourceTransfer = null;
        request.Dispose(); SourceBindings();
    }

    public void CancelSourceTransfer() => sourceTransfer?.Cancel();

    /// <summary>Light checks of the given rows, at most three at a time. A check never
    /// changes enablement, the current playback or any song's matching state.</summary>
    public Task CheckSourcesAsync(IEnumerable<MusicSourceRow> requested)
    {
        var rows = requested.Where(row => Sources.Contains(row) && !checkingSources.Contains(row.Key)).ToArray();
        if (stopping || rows.Length == 0) return Task.CompletedTask;
        long generation = sourceManagementGeneration;
        suppressSourceRowBindings = true;
        try
        {
            foreach (var row in rows) { checkingSources.Add(row.Key); row.Checking = true; }
        }
        finally { suppressSourceRowBindings = false; }
        SourceBindings();
        var work = Run();
        eventWrites.Add(work); _ = Observe();
        return work;
        async Task Observe() { try { await work; } catch { } finally { eventWrites.Remove(work); } }
        async Task Run()
        {
            using var gate = new SemaphoreSlim(3);
            string? failure = null;
            await Task.WhenAll(rows.Select(async row =>
            {
                bool entered = false, replaced = false;
                try
                {
                    await gate.WaitAsync(lifetime.Token); entered = true;
                    if (row.IsBuiltIn)
                    {
                        var reply = await Call("music.official.check", new { platform = row.Platform }, lifetime.Token);
                        if (!SourceManagementCurrent(generation)) return;
                        officialChecks[row.Platform!] = new(row.Platform!, MusicDto.Text(reply, "state") == "ok" ? "ok" : "failed");
                        if (Sources.FirstOrDefault(item => item.Key == row.Key) is { } current)
                        {
                            checkingSources.Remove(row.Key);
                            ReplaceSource(MusicSourceRow.BuiltIn(row.Platform!, current.Enabled, officialChecks[row.Platform!]));
                            replaced = true;
                        }
                    }
                    else
                    {
                        var reply = await Call("music.sources.check", new { installationId = row.InstallationId }, lifetime.Token);
                        if (SourceManagementCurrent(generation))
                        {
                            checkingSources.Remove(row.Key);
                            ReplaceSource(MusicSourceRow.Read(reply.GetProperty("item")));
                            replaced = true;
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception error) { failure ??= ErrorText(error); }
                finally
                {
                    if (entered) gate.Release();
                    bool removed = checkingSources.Remove(row.Key);
                    // The pending check still owns this key after a page is reopened.
                    // Release its busy state without publishing the retired result.
                    if ((removed || !replaced) && sourceManagementActive && !stopping && !disposed)
                    {
                        suppressSourceRowBindings = true;
                        try
                        {
                            if (Sources.FirstOrDefault(item => item.Key == row.Key) is { } current) current.Checking = false;
                        }
                        finally { suppressSourceRowBindings = false; }
                        SourceBindings();
                    }
                }
            }));
            if (failure is not null && SourceManagementCurrent(generation)) ctx.SetStatus(failure);
        }
    }

    public Task CheckSelectedSourcesAsync() => CheckSourcesAsync(Sources.Where(row => row.Selected).ToArray());
}
