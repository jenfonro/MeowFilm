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
public sealed partial class MusicCapability
{
    private static readonly string[] OfficialPlatforms = ["wy", "tx", "kg"];
    private readonly Dictionary<string, MusicSourcePlatformState> officialChecks = new(StringComparer.Ordinal);
    private readonly HashSet<string> checkingSources = new(StringComparer.Ordinal);
    private HashSet<string> officialEnabled = new(OfficialPlatforms, StringComparer.Ordinal);
    private long officialSourcesRevision;
    private bool officialSourcesLoaded, scriptsUnavailable;
    private CancellationTokenSource? sourceTransfer;
    private long sourcesRevision;
    private long sourceManagementGeneration;
    private bool sourceManagementActive = true;
    private bool SourceManagementCurrent(long generation) => sourceManagementActive &&
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
    public bool HasSelectedSources => Sources.Any(row => row.Selected);
    public bool CanCheckSelectedSources => !stopping && Sources.Any(row => row.Selected && !row.Checking);
    public bool CanRemoveSelectedSources => CanEditScripts && Sources.Any(row => row.Selected && row.IsScript);
    public bool CanCancelSourceTransfer => sourceTransfer is not null;
    public bool AllSourcesSelected
    {
        get => Sources.Count > 0 && Sources.All(row => row.Selected);
        set { foreach (var row in Sources) row.Selected = value; }
    }
    private void SourceBindings()
    {
        foreach (var property in new[] { nameof(CanEditOfficialSources), nameof(CanEditScripts), nameof(HasSelectedSources),
            nameof(CanCheckSelectedSources), nameof(CanRemoveSelectedSources), nameof(CanCancelSourceTransfer),
            nameof(AllSourcesSelected) })
            Changed(property);
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
    private Task RunSourceManagementAsync(Func<long, Task> action)
    {
        long generation = sourceManagementGeneration;
        long stamp = ++statusEpoch;
        if (SourceManagementCurrent(generation)) { status = ""; catalogAccountNotice = ""; }
        return RunAsync(async () =>
        {
            try { await action(generation); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                if (SourceManagementCurrent(generation) && stamp == statusEpoch)
                { status = ErrorText(error); Changed(nameof(Status)); }
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
            status = ErrorText(error); Changed(nameof(Status));
        }
        PublishSources(scripts);
    }
    private void PublishSources(IEnumerable<JsonElement> scripts)
    {
        var selected = Sources.Where(row => row.Selected).Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var row in Sources) row.PropertyChanged -= SourceRowChanged;
        Sources.Clear();
        var rows = OfficialPlatforms.Select(platform => MusicSourceRow.BuiltIn(platform, officialEnabled.Contains(platform),
                officialChecks.GetValueOrDefault(platform)))
            .Concat(scripts.Select(MusicSourceRow.Read));
        foreach (var row in rows)
        {
            row.Selected = selected.Contains(row.Key);
            row.Checking = checkingSources.Contains(row.Key);
            row.PropertyChanged += SourceRowChanged;
            Sources.Add(row);
        }
        sourcesRevision++;
        SourceBindings();
        PlaybackSourceBindings();
    }
    private void SourceRowChanged(object? sender, PropertyChangedEventArgs args)
    {
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
        next.PropertyChanged += SourceRowChanged;
        Sources[index] = next;
        SourceBindings();
        PlaybackSourceBindings();
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
        if (changed) { NotifyMusicSourcesChanged(); CancelMusicPreload(); }
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
                try
                {
                    var reply = await CallSettledReceipt("music.officialSources.settings.save", new
                    {
                        ifRevision = officialSourcesRevision,
                        enabled = OfficialPlatforms.Where(next.Contains).ToArray()
                    }, lifetime.Token);
                    if (SourceManagementCurrent(generation)) PublishOfficialSources(reply.GetProperty("settings"));
                }
                catch (Exception error)
                {
                    if (SourceManagementCurrent(generation))
                    { status = ErrorText(error); Changed(nameof(Status)); }
                }
                finally
                {
                    if (SourceManagementCurrent(generation))
                    try { await ReloadSources(lifetime.Token, generation); }
                    catch (Exception error)
                    {
                        if (SourceManagementCurrent(generation))
                        { status = ErrorText(error); Changed(nameof(Status)); }
                    }
                }
            });
        }
        return ScriptActionAsync(row, enabled ? "enable" : "disable");
    }
    public async Task RemoveSourceAsync(MusicSourceRow? row)
    {
        if (row is not { IsScript: true } || !Sources.Contains(row) || !CanEditScripts) return;
        if (!await ConfirmAsync($"删除音源“{row.Name}”？", "删除音源", lifetime.Token)) return;
        await ScriptActionAsync(row, "remove");
    }
    public async Task RemoveSelectedSourcesAsync()
    {
        var rows = Sources.Where(row => row.Selected && row.IsScript).ToArray();
        if (!CanEditScripts || rows.Length == 0 || !await ConfirmAsync($"删除所选的 {rows.Length} 个音源？", "删除音源", lifetime.Token))
            return;
        await RunSourceManagementAsync(async generation =>
        {
            try
            {
                foreach (var row in rows)
                {
                    await BeforeSourceChange(row.InstallationId!, retire: true);
                    await CallSettledReceipt("music.sources.remove", new { installationId = row.InstallationId }, lifetime.Token);
                }
            }
            finally { NotifyMusicSourcesChanged(); await ReloadSources(lifetime.Token, generation); }
        });
    }
    public async Task RollbackSourceAsync(MusicSourceRow? row)
    {
        if (row is not { IsScript: true, RollbackAvailable: true } || !Sources.Contains(row) || !CanEditScripts) return;
        if (!await ConfirmAsync($"回退音源“{row.Name}”？", "回退音源", lifetime.Token)) return;
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
            finally { NotifyMusicSourcesChanged(); await ReloadSources(lifetime.Token, generation); }
        });
    }
    /// <summary>Content or enablement changes revoke new parsing, not already accepted historical media.</summary>
    private async Task BeforeSourceChange(string installation, bool retire)
    {
        NotifyMusicSourcesChanged();
        CancelMusicPreload();
        if (!retire) return;
        await RevokeChangedMusicSource(installation);
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
            if (SourceManagementCurrent(generation)) { status = ErrorText(error); Changed(nameof(Status)); }
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
                if (await dialogs.SelectScriptFileAsync(SettingsView) is not { } path) return;
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
            finally { EndSourceTransfer(request); NotifyMusicSourcesChanged(); await ReloadSources(lifetime.Token, generation); }
        });
    }
    public Task ImportSourceAsync()
    {
        if (!CanEditScripts) return Task.CompletedTask;
        return RunSourceManagementAsync(async generation =>
        {
            if (await dialogs.SelectScriptFilesAsync(SettingsView) is not { Length: > 0 } paths) return;
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
                try
                {
                    var reply = await application.ImportSourceAsync(snapshot.Bytes, snapshot.Basename,
                        update?.InstallationId, request.Token);
                    try { ValidateReply(update is null ? "music.sources.add" : "music.sources.update", reply); }
                    catch (MusicOperationException error) when (error.Code == "CANCELLED" && request.IsCancellationRequested)
                    { throw new OperationCanceledException(request.Token); }
                }
                finally { CryptographicOperations.ZeroMemory(snapshot.Bytes); }
            }
        }
        finally
        {
            EndSourceTransfer(request);
            if (update is not null) NotifyMusicSourcesChanged();
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
        foreach (var row in rows) { checkingSources.Add(row.Key); row.Checking = true; }
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
                bool entered = false;
                try
                {
                    await gate.WaitAsync(lifetime.Token); entered = true;
                    if (row.IsBuiltIn)
                    {
                        var reply = await Call("music.official.check", new { platform = row.Platform }, lifetime.Token);
                        if (!SourceManagementCurrent(generation)) return;
                        officialChecks[row.Platform!] = new(row.Platform!, MusicDto.Text(reply, "state") == "ok" ? "ok" : "failed");
                        if (Sources.FirstOrDefault(item => item.Key == row.Key) is { } current)
                            ReplaceSource(MusicSourceRow.BuiltIn(row.Platform!, current.Enabled, officialChecks[row.Platform!]));
                    }
                    else
                    {
                        var reply = await Call("music.sources.check", new { installationId = row.InstallationId }, lifetime.Token);
                        if (SourceManagementCurrent(generation)) ReplaceSource(MusicSourceRow.Read(reply.GetProperty("item")));
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception error) { failure ??= ErrorText(error); }
                finally
                {
                    if (entered) gate.Release();
                    checkingSources.Remove(row.Key);
                    // The pending check still owns this key after a page is reopened.
                    // Release its busy state without publishing the retired result.
                    if (sourceManagementActive && !stopping && !disposed)
                    {
                        if (Sources.FirstOrDefault(item => item.Key == row.Key) is { } current) current.Checking = false;
                        SourceBindings();
                    }
                }
            }));
            if (failure is not null && SourceManagementCurrent(generation)) { status = failure; Changed(nameof(Status)); }
        }
    }
    public Task CheckSelectedSourcesAsync() => CheckSourcesAsync(Sources.Where(row => row.Selected).ToArray());
}
