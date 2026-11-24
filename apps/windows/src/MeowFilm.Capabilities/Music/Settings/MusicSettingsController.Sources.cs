using System.Collections.ObjectModel;
using System.ComponentModel;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Scripting;
using MeowFilm.Core.Debug;

namespace MeowFilm.Capabilities;

/// <summary>
/// Source management projection: built-in providers and script installations in one table.
/// Delegates non-UI state, transfer cancellation, file import, and bounded parallel checks
/// to <see cref="MusicSourceManagementService"/>.
/// </summary>
public sealed partial class MusicSettingsController
{
    private long sourceManagementGeneration;
    private bool sourceManagementActive = true;
    private bool suppressSourceRowBindings;

    internal bool SourceManagementCurrent(long generation) => sourceManagementActive &&
        generation == sourceManagementGeneration && !stopping && !disposed;

    internal void RetireSourceManagement()
    {
        sourceManagementActive = false;
        sourceManagementGeneration++;
        sourceManagement.MarkOfficialUnloaded();
    }

    public ObservableCollection<MusicSourceRow> Sources { get; } = [];
    public bool CanEditOfficialSources => CanEdit && sourceManagement.OfficialSourcesLoaded;
    public bool CanEditScripts => CanEdit && !sourceManagement.ScriptsUnavailable;
    public bool CanCheckSelectedSources => !stopping && Sources.Any(row => row.Selected && !row.Checking);
    public bool CanRemoveSelectedSources => CanEditScripts && Sources.Any(row => row.Selected && row.IsScript);
    public bool CanCancelSourceTransfer => sourceManagement.HasActiveTransfer;

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
        lyricsPort.PublishOnlineSettings(settings.LyricsEnabled);
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
        var (scripts, unavailableError) = await sourceManagement.ListScriptsAsync(token);
        if (!SourceManagementCurrent(generation)) return;
        if (unavailableError is not null) ctx.SetStatus(unavailableError);
        PublishSources(scripts);
    }

    private void PublishSources(IEnumerable<MusicScriptSourceItem> scripts)
    {
        bool editOfficial = CanEditOfficialSources;
        bool editScripts = CanEditScripts;
        var incoming = sourceManagement.BuildRows(scripts);
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
                    existing.Checking = sourceManagement.IsChecking(existing.Key);
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
                    candidate.Checking = sourceManagement.IsChecking(candidate.Key);
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
        playbackPort.PlaybackSourceBindings();
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
        next.Checking = sourceManagement.IsChecking(next.Key);
        next.CanToggle = next.IsBuiltIn ? CanEditOfficialSources : CanEditScripts;
        next.PropertyChanged += SourceRowChanged;
        Sources[index] = next;
        SourceBindings();
        playbackPort.PlaybackSourceBindings();
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
            if (SourceManagementCurrent(generation)) { sourceManagement.MarkOfficialUnloaded(); SourceBindings(); }
            throw;
        }
    }

    private void PublishOfficialSources(MusicOfficialSourcesRecord value)
    {
        if (sourceManagement.ApplyOfficialSources(value))
        {
            ctx.NotifyMusicSourcesChanged();
            playbackPort.CancelMusicPreload();
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
                var nextEnabled = sourceManagement.ComputeNextOfficialEnabled(row.Platform!, enabled);
                bool saved = false;
                try
                {
                    var settings = await RunSettingReceiptAsync(MusicSettingReceiptKeys.OfficialSources,
                        ct => application.SaveOfficialSourcesAsync(sourceManagement.OfficialSourcesRevision, nextEnabled, ct), lifetime.Token);
                    if (SourceManagementCurrent(generation))
                    {
                        PublishOfficialSources(settings);
                        foreach (var platform in MusicSourceManagementService.OfficialPlatforms)
                        {
                            var updated = MusicSourceRow.BuiltIn(platform, sourceManagement.IsOfficialEnabled(platform),
                                sourceManagement.GetOfficialCheck(platform));
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
                    case "enable":
                        await application.SetSourceEnabledAsync(row.InstallationId!, enabled: true, lifetime.Token);
                        break;
                    case "disable":
                        await application.SetSourceEnabledAsync(row.InstallationId!, enabled: false, lifetime.Token);
                        break;
                    case "remove":
                        await application.RemoveSourceAsync(row.InstallationId!, lifetime.Token);
                        break;
                    case "rollback":
                        await application.RollbackSourceAsync(row.InstallationId!, lifetime.Token);
                        break;
                    default:
                        throw new MusicOperationException("INVALID_REQUEST");
                }
            }
            finally { ctx.NotifyMusicSourcesChanged(); await ReloadSources(lifetime.Token, generation); }
        });
    }

    private async Task BeforeSourceChange(string installation, bool retire)
    {
        ctx.NotifyMusicSourcesChanged();
        playbackPort.CancelMusicPreload();
        if (!retire) return;
        await playbackPort.RevokeChangedMusicSourceAsync(installation);
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
            var request = sourceManagement.BeginTransfer(SourceBindings);
            try
            {
                await BeforeSourceChange(row.InstallationId!, retire: true);
                await application.UpdateSourceAsync(row.InstallationId!, request.Token);
            }
            finally { sourceManagement.EndTransfer(request, SourceBindings); ctx.NotifyMusicSourcesChanged(); await ReloadSources(lifetime.Token, generation); }
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
            var request = sourceManagement.BeginTransfer(SourceBindings);
            try { await application.DownloadSourceAsync(address, request.Token); }
            finally { sourceManagement.EndTransfer(request, SourceBindings); await ReloadSources(lifetime.Token, generation); }
        });
    }

    private async Task ChangeSourceFilesAsync(string[] paths, MusicSourceRow? update, long generation)
    {
        var request = sourceManagement.BeginTransfer(SourceBindings);
        try
        {
            if (update is not null) await BeforeSourceChange(update.InstallationId!, retire: true);
            await sourceManagement.ImportFilesAsync(paths, update?.InstallationId, request.Token);
        }
        finally
        {
            sourceManagement.EndTransfer(request, SourceBindings);
            if (update is not null) ctx.NotifyMusicSourcesChanged();
            await ReloadSources(lifetime.Token, generation);
        }
    }

    public void CancelSourceTransfer() => sourceManagement.CancelTransfer();

    /// <summary>Light checks of the given rows, at most three at a time. A check never
    /// changes enablement, the current playback or any song's matching state.</summary>
    public Task CheckSourcesAsync(IEnumerable<MusicSourceRow> requested)
    {
        var rows = requested.Where(row => Sources.Contains(row) && !sourceManagement.IsChecking(row.Key)).ToArray();
        if (stopping || rows.Length == 0) return Task.CompletedTask;
        long generation = sourceManagementGeneration;
        suppressSourceRowBindings = true;
        try
        {
            foreach (var row in rows) { sourceManagement.TryMarkChecking(row.Key); row.Checking = true; }
        }
        finally { suppressSourceRowBindings = false; }
        SourceBindings();
        var work = Run();
        ctx.AddEventWrite(work); _ = Observe();
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
            finally { ctx.RemoveEventWrite(work); }
        }
        async Task Run()
        {
            string? failure = await sourceManagement.RunBoundedChecksAsync(
                rows,
                () => SourceManagementCurrent(generation),
                key => Sources.FirstOrDefault(item => item.Key == key),
                ReplaceSource,
                key =>
                {
                    if (!sourceManagementActive || stopping || disposed) return;
                    suppressSourceRowBindings = true;
                    try
                    {
                        if (Sources.FirstOrDefault(item => item.Key == key) is { } current) current.Checking = false;
                    }
                    finally { suppressSourceRowBindings = false; }
                    SourceBindings();
                },
                lifetime.Token);
            if (failure is not null && SourceManagementCurrent(generation)) ctx.SetStatus(failure);
        }
    }

    public Task CheckSelectedSourcesAsync() => CheckSourcesAsync(Sources.Where(row => row.Selected).ToArray());
}
