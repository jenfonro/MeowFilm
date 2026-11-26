using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MeowFilm.Capabilities.Sync;
using MeowFilm.Core.Debug;

namespace MeowFilm.Capabilities;

public sealed class MusicLibrarySyncPlatformRow(string platform, string label) : INotifyPropertyChanged
{
    private bool favorites, playlists;
    public string Platform { get; } = platform;
    public string Label { get; } = label;
    public bool Favorites => favorites;
    public bool Playlists => playlists;
    internal void Apply(bool nextFavorites, bool nextPlaylists)
    {
        if (favorites != nextFavorites)
        {
            favorites = nextFavorites;
            Changed(nameof(Favorites));
        }
        if (playlists != nextPlaylists)
        {
            playlists = nextPlaylists;
            Changed(nameof(Playlists));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string name = "") =>
        PropertyChanged?.Invoke(this, new(name));
}

/// <summary>
/// Visible settings projection over the sole native sync owner. Switches submit
/// immediately; there is no draft, save button, reload button or second timer.
/// </summary>
public sealed class MusicLibrarySyncViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly MusicLibrarySyncService service;
    private readonly Func<string, CancellationToken, Task<bool>>? confirmResetAccount;
    private readonly Func<bool> isClosed;
    private CancellationTokenSource? visibility;
    private Task? active, disposal;
    private Task retiring = Task.CompletedTask;
    private bool busy, disposed;
    private long revision, epoch;
    private string status = "", confirmation = "", confirmationToken = "", rebindPlatform = "";

    internal MusicLibrarySyncViewModel(
        MusicLibrarySyncService service,
        Func<string, CancellationToken, Task<bool>>? confirmResetAccount = null,
        Func<bool>? isClosed = null)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.confirmResetAccount = confirmResetAccount;
        this.isClosed = isClosed ?? (() => false);
    }

    public ObservableCollection<MusicLibrarySyncPlatformRow> Platforms { get; } =
        [new("wy", "网易云音乐"), new("tx", "QQ 音乐"), new("kg", "酷狗音乐")];
    public bool IsActive => visibility is not null && !disposed;
    public bool CanEdit => IsActive && !busy;
    public bool HasConfirmation => confirmationToken.Length > 0;
    public bool NeedsAccountRebind => rebindPlatform.Length > 0;
    public string RebindPlatform => rebindPlatform;
    public string Status { get => status; private set { if (status != value) { status = value; Changed(); } } }
    public string Confirmation { get => confirmation; private set { if (confirmation != value) { confirmation = value; Changed(); } } }
    public event PropertyChangedEventHandler? PropertyChanged;

    public Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        if (disposed) throw new ObjectDisposedException(nameof(MusicLibrarySyncViewModel));
        if (visibility is not null) return active ?? Task.CompletedTask;
        visibility = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        epoch++;
        Changed(nameof(IsActive));
        Changed(nameof(CanEdit));
        return RefreshStatusAsync();
    }

    public Task RefreshStatusAsync() => ExecuteWorkAsync(
        token => isClosed() ? Task.FromResult(MusicLibrarySyncResult.Failed("CLOSED")) : service.GetStateAsync(token),
        clearRebind: false,
        matchesReadback: null,
        rebindPlatformCandidate: null,
        retryAfterRebind: null,
        runAfterSave: false);

    public Task SetAsync(MusicLibrarySyncPlatformRow row, string kind, bool enabled, bool confirmed)
    {
        if (!confirmed || !CanEdit || !Platforms.Contains(row) || kind is not ("favorites" or "playlists"))
            return Task.CompletedTask;
        bool favorites = kind == "favorites" ? enabled : row.Favorites;
        bool playlists = kind == "playlists" ? enabled : row.Playlists;
        if (favorites == row.Favorites && playlists == row.Playlists) return Task.CompletedTask;
        string platform = row.Platform;
        return ExecuteWorkAsync(
            token => isClosed()
                ? Task.FromResult(MusicLibrarySyncResult.Failed("CLOSED"))
                : service.SetPlatformAsync(platform, favorites, playlists, revision, confirm: true, token),
            clearRebind: true,
            matchesReadback: () => row.Favorites == favorites && row.Playlists == playlists,
            rebindPlatformCandidate: platform,
            retryAfterRebind: token => service.SetPlatformAsync(platform, favorites, playlists, revision, confirm: true, token),
            runAfterSave: true);
    }

    public Task ConfirmAsync(bool confirmed)
    {
        if (!confirmed || !CanEdit || !HasConfirmation) return Task.CompletedTask;
        string tokenToConfirm = confirmationToken;
        return ExecuteWorkAsync(
            token => isClosed()
                ? Task.FromResult(MusicLibrarySyncResult.Failed("CLOSED"))
                : service.ConfirmAsync(tokenToConfirm, revision, confirm: true, token),
            clearRebind: true,
            matchesReadback: null,
            rebindPlatformCandidate: null,
            retryAfterRebind: null,
            runAfterSave: true);
    }

    public Task ResetAccountAsync(string platform, bool confirmed)
    {
        if (!confirmed || !CanEdit || string.IsNullOrEmpty(platform)) return Task.CompletedTask;
        var row = Platforms.FirstOrDefault(item => item.Platform == platform);
        if (row is null) return Task.CompletedTask;
        return ExecuteWorkAsync(
            token => isClosed()
                ? Task.FromResult(MusicLibrarySyncResult.Failed("CLOSED"))
                : service.ResetAccountAsync(platform, revision, confirm: true, token),
            clearRebind: true,
            matchesReadback: () => !row.Favorites && !row.Playlists,
            rebindPlatformCandidate: null,
            retryAfterRebind: null,
            runAfterSave: false);
    }

    private Task ExecuteWorkAsync(
        Func<CancellationToken, Task<MusicLibrarySyncResult>> action,
        bool clearRebind,
        Func<bool>? matchesReadback,
        string? rebindPlatformCandidate,
        Func<CancellationToken, Task<MusicLibrarySyncResult>>? retryAfterRebind,
        bool runAfterSave)
    {
        if (!CanEdit) return Task.CompletedTask;
        long ticket = epoch;
        var token = visibility!.Token;
        busy = true;
        Changed(nameof(CanEdit));
        return active = Work();

        async Task Work()
        {
            try
            {
                var result = await action(token);
                if (ticket != epoch || token.IsCancellationRequested) return;

                if (!result.Ok && result.Code == "ACCOUNT_REBIND_REQUIRED" && rebindPlatformCandidate is not null)
                {
                    SetRebindPlatform(rebindPlatformCandidate);
                    Apply(result);
                    if (confirmResetAccount is not null &&
                        await confirmResetAccount(rebindPlatformCandidate, token))
                    {
                        if (ticket != epoch || token.IsCancellationRequested) return;
                        var latestBeforeReset = await service.GetStateAsync(token);
                        if (ticket != epoch || token.IsCancellationRequested) return;
                        Apply(latestBeforeReset);
                        var resetResult = await service.ResetAccountAsync(rebindPlatformCandidate, revision, confirm: true, token);
                        if (ticket != epoch || token.IsCancellationRequested) return;
                        Apply(resetResult);
                        if (!resetResult.Ok) return;
                        SetRebindPlatform("");
                        if (retryAfterRebind is not null)
                        {
                            result = await retryAfterRebind(token);
                            if (ticket != epoch || token.IsCancellationRequested) return;
                        }
                        else
                        {
                            return;
                        }
                    }
                    else
                    {
                        return;
                    }
                }

                Apply(result);
                if (result.Ok)
                {
                    if (clearRebind) SetRebindPlatform("");
                }
                else
                {
                    string error = Status;
                    var latest = await service.GetStateAsync(token);
                    if (ticket != epoch || token.IsCancellationRequested) return;
                    Apply(latest);
                    if (!(latest.Ok && matchesReadback is not null && matchesReadback()))
                    {
                        Status = error;
                        return;
                    }
                }

                if (runAfterSave)
                {
                    result = await service.RunAsync(token);
                    if (ticket != epoch || token.IsCancellationRequested) return;
                    Apply(result);
                    if (!result.Ok)
                    {
                        if (result.Code is "ACCOUNT_REBIND_REQUIRED" or "ACCOUNT_REPLACED" && rebindPlatformCandidate is not null)
                            SetRebindPlatform(rebindPlatformCandidate);
                        string error = Status;
                        var latest = await service.GetStateAsync(token);
                        if (ticket != epoch || token.IsCancellationRequested) return;
                        Apply(latest);
                        Status = error;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                DebugLog.Warn(DebugCategories.MusicLibrary, "sync", "sync_viewmodel_failed",
                    new { error = error.Message });
                if (ticket == epoch) Status = "同步设置保存失败";
            }
            finally
            {
                if (ticket == epoch)
                {
                    busy = false;
                    Changed(nameof(CanEdit));
                }
            }
        }
    }

    private void SetRebindPlatform(string platform)
    {
        if (rebindPlatform == platform) return;
        rebindPlatform = platform;
        Changed(nameof(RebindPlatform));
        Changed(nameof(NeedsAccountRebind));
    }

    private void Apply(MusicLibrarySyncResult result)
    {
        confirmationToken = "";
        Confirmation = "";
        if (result.Confirmation is { } confirm)
        {
            confirmationToken = confirm.Token;
            Confirmation = confirm.Code switch
            {
                "DELETE_REMOTE_PLAYLIST" => $"删除远端歌单（{confirm.Count} 首）？",
                "EMPTY_REMOTE" => $"确认远端为空并同步删除（{confirm.Count} 项）？",
                _ => $"确认同步删除 {confirm.Count} 项？"
            };
        }
        Changed(nameof(HasConfirmation));

        if (result.Ok && result.Platforms is { } platforms)
        {
            long observed = result.Revision;
            if (observed >= revision)
            {
                var values = Platforms.Select(row =>
                {
                    bool found = platforms.TryGetValue(row.Platform, out var value);
                    return (Row: row, Favorites: found && value!.Favorites, Playlists: found && value!.Playlists);
                }).ToArray();
                revision = observed;
                foreach (var value in values) value.Row.Apply(value.Favorites, value.Playlists);
            }
        }

        var rebindIssue = result.Issues.FirstOrDefault(issue =>
            issue.Code is "ACCOUNT_REPLACED" or "ACCOUNT_REBIND_REQUIRED" && issue.Platform.Length > 0);
        if (rebindIssue is not null)
            SetRebindPlatform(rebindIssue.Platform);

        string code = result.Code, phase = result.Phase;
        Status = code switch
        {
            "CONFIRMATION_REQUIRED" => "等待确认",
            "AUTH_REQUIRED" => "未登录",
            "ACCOUNT_REPLACED" or "ACCOUNT_REBIND_REQUIRED" => "账号已更换",
            "STALE_SESSION" or "SYNC_SUPERSEDED" => "同步已失效",
            "CANCELLED" => "",
            "SYNC_BUSY" => "正在同步",
            "CREATE_UNCONFIRMED" or "WRITE_UNCONFIRMED" or "DELETE_UNCONFIRMED" => "远端写入未确认",
            "REMOTE_ORDER_UNSUPPORTED" => "远端排序不支持",
            "SYNC_CLOUD_TRANSPORT_REQUIRED" => "酷狗同步不可用",
            "REVISION_CONFLICT" => "同步设置已更新",
            "CLOSED" => "",
            "" => phase switch
            {
                "partial" => "部分同步失败",
                "pushing" or "pulling" => "正在同步",
                "blocked" => "同步受阻",
                _ => ""
            },
            _ => "同步受阻"
        };
    }

    public async Task DeactivateAsync(CancellationToken cancellationToken = default)
    {
        long ticket = ++epoch;
        var previous = visibility;
        var pending = active;
        visibility = null;
        active = null;
        busy = false;
        var drain = retiring = retiring.IsCompletedSuccessfully
            ? pending ?? Task.CompletedTask
            : pending is null ? retiring : Task.WhenAll(retiring, pending);
        try
        {
            previous?.Cancel();
            if (previous is not null)
            {
                if (pending is null || pending.IsCompleted) previous.Dispose();
                else _ = pending.ContinueWith(_ => previous.Dispose(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            Changed(nameof(IsActive));
            Changed(nameof(CanEdit));
            await drain.WaitAsync(cancellationToken);
        }
        finally
        {
            if (ticket == epoch)
            {
                busy = false;
                Changed(nameof(CanEdit));
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (disposal is not null) return new(disposal);
        disposed = true;
        return new(disposal = DeactivateAsync());
    }

    private void Changed([CallerMemberName] string name = "") =>
        PropertyChanged?.Invoke(this, new(name));
}
