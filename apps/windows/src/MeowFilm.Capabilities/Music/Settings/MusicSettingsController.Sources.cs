using System.Collections.ObjectModel;
using System.ComponentModel;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Scripting;
using MeowFilm.Core.Debug;
using MeowFilm.Platform.Files;

namespace MeowFilm.Capabilities;

public sealed partial class MusicSettingsController
{
    private static readonly string[] OfficialSourcePlatforms = ["wy", "tx", "kg"];

    private readonly object sourceCheckGate = new();
    private readonly Dictionary<string, MusicSourcePlatformState> officialSourceChecks = new(StringComparer.Ordinal);
    private readonly HashSet<string> checkingSources = new(StringComparer.Ordinal);
    private HashSet<string> officialSourcesEnabled = new(OfficialSourcePlatforms, StringComparer.Ordinal);
    private long officialSourcesRevision;
    private bool officialSourcesLoaded;
    private bool scriptsUnavailable;
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

    private bool IsCheckingSource(string key) { lock (sourceCheckGate) return checkingSources.Contains(key); }
    private MusicSourcePlatformState? GetOfficialSourceCheck(string platform) { lock (sourceCheckGate) return officialSourceChecks.GetValueOrDefault(platform); }

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
        var quality = await RunSettingReceiptAsync(MusicSettingReceiptKeys.QualityPolicy,
            ct => application.GetQualityPolicyAsync(ct), token);
        token.ThrowIfCancellationRequested();
        if (sourceGeneration is { } generation && !SourceManagementCurrent(generation)) return;
        AcceptQuality(quality);
        var settings = await application.GetOnlineSettingsAsync(token);
        token.ThrowIfCancellationRequested();
        if (sourceGeneration is { } current && !SourceManagementCurrent(current)) return;
        OnlineLyricsEnabledChanged?.Invoke(settings.LyricsEnabled);
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
        MusicScriptSourceItem[] scripts;
        string? unavailableError = null;
        try
        {
            scripts = (await application.ListSourcesAsync(token)).Items;
            scriptsUnavailable = false;
        }
        catch (MusicOperationException error) when (error.Code == "SCRIPT_RUNTIME_UNAVAILABLE")
        {
            scriptsUnavailable = true;
            scripts = [];
            unavailableError = ErrorText(error);
        }
        if (!SourceManagementCurrent(generation)) return;
        if (unavailableError is not null) ctx.SetStatus(unavailableError);
        PublishSources(scripts);
    }

    private void PublishSources(IEnumerable<MusicScriptSourceItem> scripts)
    {
        bool editOfficial = CanEditOfficialSources;
        bool editScripts = CanEditScripts;
        var incoming = OfficialSourcePlatforms
            .Select(platform => MusicSourceRow.BuiltIn(platform, officialSourcesEnabled.Contains(platform), GetOfficialSourceCheck(platform)))
            .Concat(scripts.Select(MusicSourceRow.FromScript))
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
                    existing.Checking = IsCheckingSource(existing.Key);
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
                    candidate.Checking = IsCheckingSource(candidate.Key);
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
        SourcesListUpdated?.Invoke();
    }

    private void SourceRowChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (suppressSourceRowBindings) return;
        if (args.PropertyName is nameof(MusicSourceRow.Selected) or nameof(MusicSourceRow.Checking)) SourceBindings();
    }

    private void ReplaceSource(MusicSourceRow next)
    {
        int index = -1;
        for (int i = 0; i < Sources.Count; i++)
            if (Sources[i].Key == next.Key) { index = i; break; }
        if (index < 0) return;
        var previous = Sources[index];
        previous.PropertyChanged -= SourceRowChanged;
        next.Selected = previous.Selected;
        next.Checking = IsCheckingSource(next.Key);
        next.CanToggle = next.IsBuiltIn ? CanEditOfficialSources : CanEditScripts;
        next.PropertyChanged += SourceRowChanged;
        Sources[index] = next;
        SourceBindings();
        SourcesListUpdated?.Invoke();
    }

    private async Task RefreshOfficialSources(CancellationToken token, long generation)
    {
        try
        {
            var settings = await RunSettingReceiptAsync(MusicSettingReceiptKeys.OfficialSources,
                ct => application.GetOfficialSourcesAsync(ct), token);
            if (SourceManagementCurrent(generation)) PublishOfficialSources(settings);
        }
        catch
        {
            if (SourceManagementCurrent(generation)) { officialSourcesLoaded = false; SourceBindings(); }
            throw;
        }
    }

    private void PublishOfficialSources(MusicOfficialSourcesRecord value)
    {
        var enabled = value.Enabled.ToHashSet(StringComparer.Ordinal);
        bool changed = officialSourcesRevision != value.Revision || !officialSourcesEnabled.SetEquals(enabled);
        officialSourcesRevision = value.Revision;
        officialSourcesLoaded = true;
        officialSourcesEnabled = enabled;
        if (changed)
        {
            ctx.NotifyMusicSourcesChanged();
            PreloadInvalidated?.Invoke();
        }
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
                var nextSet = officialSourcesEnabled.ToHashSet(StringComparer.Ordinal);
                if (enabled) nextSet.Add(row.Platform!); else nextSet.Remove(row.Platform!);
                var nextEnabled = OfficialSourcePlatforms.Where(nextSet.Contains).ToArray();
                bool saved = false;
                try
                {
                    var settings = await RunSettingReceiptAsync(MusicSettingReceiptKeys.OfficialSources,
                        ct => application.SaveOfficialSourcesAsync(officialSourcesRevision, nextEnabled, ct), lifetime.Token);
                    if (SourceManagementCurrent(generation))
                    {
                        PublishOfficialSources(settings);
                        foreach (var platform in OfficialSourcePlatforms)
                        {
                            var updated = MusicSourceRow.BuiltIn(platform, officialSourcesEnabled.Contains(platform),
                                GetOfficialSourceCheck(platform));
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
                    await application.RemoveSourceAsync(row.InstallationId!, lifetime.Token);
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
            try
            {
                switch (action)
                {
                    case "enable": await application.SetSourceEnabledAsync(row.InstallationId!, enabled: true, lifetime.Token); break;
                    case "disable": await application.SetSourceEnabledAsync(row.InstallationId!, enabled: false, lifetime.Token); break;
                    case "remove": await application.RemoveSourceAsync(row.InstallationId!, lifetime.Token); break;
                    case "rollback": await application.RollbackSourceAsync(row.InstallationId!, lifetime.Token); break;
                    default: throw new MusicOperationException("INVALID_REQUEST");
                }
            }
            finally { ctx.NotifyMusicSourcesChanged(); await ReloadSources(lifetime.Token, generation); }
        });
    }

    private async Task BeforeSourceChange(string installation, bool retire)
    {
        ctx.NotifyMusicSourcesChanged();
        PreloadInvalidated?.Invoke();
        if (retire && SourceRevoking is { } revoke) await revoke(installation);
    }

    public async Task<string?> ReadSourceAddressAsync(MusicSourceRow row)
    {
        if (row is not { Kind: MusicSourceKind.Network } || !Sources.Contains(row)) return null;
        long generation = sourceManagementGeneration;
        try
        {
            var detail = await application.GetSourceDetailAsync(row.InstallationId!, lifetime.Token);
            return SourceManagementCurrent(generation) ? detail.Item.Origin.Url ?? "" : null;
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
        string? nextName = name.Trim() != row.Name ? name : null;
        bool includeUrl = row.Kind == MusicSourceKind.Network && url is not null;
        if (nextName is null && !includeUrl) return Task.CompletedTask;
        return RunSourceManagementAsync(async generation =>
        {
            try { await application.EditSourceAsync(row.InstallationId!, nextName, url, includeUrl, lifetime.Token); }
            finally { await ReloadSources(lifetime.Token, generation); }
        });
    }

    private CancellationTokenSource BeginSourceTransfer()
    {
        sourceTransfer?.Cancel();
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        sourceTransfer = request;
        SourceBindings();
        return request;
    }

    private void EndSourceTransfer(CancellationTokenSource request)
    {
        if (ReferenceEquals(sourceTransfer, request)) sourceTransfer = null;
        request.Dispose();
        SourceBindings();
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
                await application.UpdateSourceAsync(row.InstallationId!, request.Token);
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
            try { await application.DownloadSourceAsync(address, request.Token); }
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
                try
                {
                    _ = await application.ImportSourceAsync(snapshot.Bytes, snapshot.Basename,
                        update?.InstallationId, request.Token);
                }
                catch (MusicOperationException error) when (error.Code == "CANCELLED" && request.IsCancellationRequested)
                {
                    throw new OperationCanceledException(request.Token);
                }
            }
        }
        finally
        {
            EndSourceTransfer(request);
            if (update is not null) ctx.NotifyMusicSourcesChanged();
            await ReloadSources(lifetime.Token, generation);
        }
    }

    public void CancelSourceTransfer() => sourceTransfer?.Cancel();

    /// <summary>Light checks of the given rows, at most three at a time.</summary>
    public Task CheckSourcesAsync(IEnumerable<MusicSourceRow> requested)
    {
        var rows = requested.Where(row => Sources.Contains(row) && !IsCheckingSource(row.Key)).ToArray();
        if (stopping || rows.Length == 0) return Task.CompletedTask;
        long generation = sourceManagementGeneration;
        suppressSourceRowBindings = true;
        try
        {
            lock (sourceCheckGate) foreach (var row in rows) checkingSources.Add(row.Key);
            foreach (var row in rows) row.Checking = true;
        }
        finally { suppressSourceRowBindings = false; }
        SourceBindings();
        var work = Run();
        AddEventWrite(work); _ = Observe();
        return work;
        async Task Observe()
        {
            try { await work; }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                DebugLog.Warn(DebugCategories.MusicLibrary, "source-management", "check-sources-failed",
                    new { error = error.Message });
            }
            finally { RemoveEventWrite(work); }
        }
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
                        var check = await application.CheckOfficialSourceAsync(row.Platform!, lifetime.Token);
                        if (!SourceManagementCurrent(generation)) return;
                        var state = new MusicSourcePlatformState(row.Platform!, check.State == "ok" ? "ok" : "failed");
                        lock (sourceCheckGate) officialSourceChecks[row.Platform!] = state;
                        if (Sources.FirstOrDefault(item => item.Key == row.Key) is { } current)
                        {
                            lock (sourceCheckGate) checkingSources.Remove(row.Key);
                            ReplaceSource(MusicSourceRow.BuiltIn(row.Platform!, current.Enabled, state));
                            replaced = true;
                        }
                    }
                    else
                    {
                        var reply = await application.CheckSourceAsync(row.InstallationId!, lifetime.Token);
                        if (SourceManagementCurrent(generation))
                        {
                            lock (sourceCheckGate) checkingSources.Remove(row.Key);
                            ReplaceSource(MusicSourceRow.FromScript(reply.Item));
                            replaced = true;
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception error)
                {
                    var text = ErrorText(error);
                    lock (sourceCheckGate) failure ??= text;
                }
                finally
                {
                    if (entered) gate.Release();
                    bool removed;
                    lock (sourceCheckGate) removed = checkingSources.Remove(row.Key);
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
            string? finalFailure;
            lock (sourceCheckGate) finalFailure = failure;
            if (finalFailure is not null && SourceManagementCurrent(generation)) ctx.SetStatus(finalFailure);
        }
    }

    public Task CheckSelectedSourcesAsync() => CheckSourcesAsync(Sources.Where(row => row.Selected).ToArray());
}
