using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Scripting;
using MeowFilm.Platform.Files;

namespace MeowFilm.Capabilities;

/// <summary>
/// Standalone non-UI domain service for music source management:
/// official & script source state, bounded 3-way parallel check execution,
/// transfer cancellation ownership, and script file imports.
/// </summary>
internal sealed class MusicSourceManagementService(MusicRuntimeContext ctx)
{
    internal static readonly string[] OfficialPlatforms = ["wy", "tx", "kg"];

    private readonly object checkGate = new();
    private readonly Dictionary<string, MusicSourcePlatformState> officialChecks = new(StringComparer.Ordinal);
    private readonly HashSet<string> checkingSources = new(StringComparer.Ordinal);
    private HashSet<string> officialEnabled = new(OfficialPlatforms, StringComparer.Ordinal);
    private long officialSourcesRevision;
    private bool officialSourcesLoaded;
    private bool scriptsUnavailable;
    private CancellationTokenSource? sourceTransfer;

    internal bool OfficialSourcesLoaded => officialSourcesLoaded;
    internal bool ScriptsUnavailable => scriptsUnavailable;
    internal bool HasActiveTransfer => sourceTransfer is not null;
    internal long OfficialSourcesRevision => officialSourcesRevision;
    internal bool IsChecking(string key) { lock (checkGate) return checkingSources.Contains(key); }
    internal bool IsOfficialEnabled(string platform) => officialEnabled.Contains(platform);
    internal MusicSourcePlatformState? GetOfficialCheck(string platform) { lock (checkGate) return officialChecks.GetValueOrDefault(platform); }

    internal void MarkOfficialUnloaded() => officialSourcesLoaded = false;

    internal bool ApplyOfficialSources(MusicOfficialSourcesRecord value)
    {
        long revision = value.Revision;
        var enabled = value.Enabled.ToHashSet(StringComparer.Ordinal);
        bool changed = officialSourcesRevision != revision || !officialEnabled.SetEquals(enabled);
        officialSourcesRevision = revision;
        officialSourcesLoaded = true;
        officialEnabled = enabled;
        return changed;
    }

    internal string[] ComputeNextOfficialEnabled(string platform, bool enabled)
    {
        var next = officialEnabled.ToHashSet(StringComparer.Ordinal);
        if (enabled) next.Add(platform); else next.Remove(platform);
        return OfficialPlatforms.Where(next.Contains).ToArray();
    }

    internal async Task<(MusicScriptSourceItem[] Scripts, string? UnavailableError)> ListScriptsAsync(CancellationToken token)
    {
        try
        {
            var reply = await ctx.Application.ListSourcesAsync(token);
            scriptsUnavailable = false;
            return (reply.Items, null);
        }
        catch (MusicOperationException error) when (error.Code == "SCRIPT_RUNTIME_UNAVAILABLE")
        {
            scriptsUnavailable = true;
            return ([], MusicRuntimeContext.ErrorText(error));
        }
    }

    internal MusicSourceRow[] BuildRows(IEnumerable<MusicScriptSourceItem> scripts) =>
        OfficialPlatforms.Select(platform => MusicSourceRow.BuiltIn(platform, officialEnabled.Contains(platform),
                GetOfficialCheck(platform)))
            .Concat(scripts.Select(MusicSourceRow.FromScript))
            .ToArray();

    internal CancellationTokenSource BeginTransfer(Action onChanged)
    {
        sourceTransfer?.Cancel();
        var request = CancellationTokenSource.CreateLinkedTokenSource(ctx.Lifetime.Token);
        sourceTransfer = request;
        onChanged();
        return request;
    }

    internal void EndTransfer(CancellationTokenSource request, Action onChanged)
    {
        if (ReferenceEquals(sourceTransfer, request)) sourceTransfer = null;
        request.Dispose();
        onChanged();
    }

    internal void CancelTransfer() => sourceTransfer?.Cancel();

    internal async Task ImportFilesAsync(string[] paths, string? updateInstallationId, CancellationToken token)
    {
        foreach (var path in paths)
        {
            var snapshot = await SelectedScriptSnapshot.ReadAsync(path, token);
            try
            {
                _ = await ctx.Application.ImportSourceAsync(snapshot.Bytes, snapshot.Basename,
                    updateInstallationId, token);
            }
            catch (MusicOperationException error) when (error.Code == "CANCELLED" && token.IsCancellationRequested)
            {
                throw new OperationCanceledException(token);
            }
        }
    }

    internal bool TryMarkChecking(string key) { lock (checkGate) return checkingSources.Add(key); }

    internal async Task<string?> RunBoundedChecksAsync(
        MusicSourceRow[] rows,
        Func<bool> isCurrent,
        Func<string, MusicSourceRow?> findRow,
        Action<MusicSourceRow> replaceRow,
        Action<string> clearRowChecking,
        CancellationToken token)
    {
        using var gate = new SemaphoreSlim(3);
        string? failure = null;
        await Task.WhenAll(rows.Select(async row =>
        {
            bool entered = false, replaced = false;
            try
            {
                await gate.WaitAsync(token); entered = true;
                if (row.IsBuiltIn)
                {
                    var check = await ctx.Application.CheckOfficialSourceAsync(row.Platform!, token);
                    if (!isCurrent()) return;
                    var state = new MusicSourcePlatformState(row.Platform!, check.State == "ok" ? "ok" : "failed");
                    lock (checkGate) officialChecks[row.Platform!] = state;
                    if (findRow(row.Key) is { } current)
                    {
                        lock (checkGate) checkingSources.Remove(row.Key);
                        replaceRow(MusicSourceRow.BuiltIn(row.Platform!, current.Enabled, state));
                        replaced = true;
                    }
                }
                else
                {
                    var reply = await ctx.Application.CheckSourceAsync(row.InstallationId!, token);
                    if (isCurrent())
                    {
                        lock (checkGate) checkingSources.Remove(row.Key);
                        replaceRow(MusicSourceRow.FromScript(reply.Item));
                        replaced = true;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                var text = MusicRuntimeContext.ErrorText(error);
                lock (checkGate) failure ??= text;
            }
            finally
            {
                if (entered) gate.Release();
                bool removed;
                lock (checkGate) removed = checkingSources.Remove(row.Key);
                if (removed || !replaced) clearRowChecking(row.Key);
            }
        }));
        lock (checkGate) return failure;
    }
}
