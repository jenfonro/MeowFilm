using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

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
    private readonly Func<string, object?, CancellationToken, Task<JsonElement>> execute;
    private CancellationTokenSource? visibility;
    private Task? active;
    private Task retiring = Task.CompletedTask;
    private bool busy, disposed;
    private long revision, epoch;
    private string status = "", confirmation = "", confirmationToken = "";

    public MusicLibrarySyncViewModel(Func<string, object?, CancellationToken, Task<JsonElement>> execute)
    {
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    public ObservableCollection<MusicLibrarySyncPlatformRow> Platforms { get; } =
        [new("wy", "网易云音乐"), new("tx", "QQ 音乐"), new("kg", "酷狗音乐")];
    public bool IsActive => visibility is not null && !disposed;
    public bool CanEdit => IsActive && !busy;
    public bool HasConfirmation => confirmationToken.Length > 0;
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

    public Task RefreshStatusAsync() => Request("get", null, runAfterSave: false);

    public Task SetAsync(MusicLibrarySyncPlatformRow row, string kind, bool enabled, bool confirmed)
    {
        if (!confirmed || !CanEdit || !Platforms.Contains(row) || kind is not ("favorites" or "playlists"))
            return Task.CompletedTask;
        bool favorites = kind == "favorites" ? enabled : row.Favorites;
        bool playlists = kind == "playlists" ? enabled : row.Playlists;
        if (favorites == row.Favorites && playlists == row.Playlists) return Task.CompletedTask;
        return Request("set", new
        {
            platform = row.Platform,
            favorites,
            playlists,
            ifRevision = revision,
            confirm = true
        }, runAfterSave: true);
    }

    public Task ConfirmAsync(bool confirmed) => !confirmed || !CanEdit || !HasConfirmation
        ? Task.CompletedTask
        : Request("confirm", new { token = confirmationToken, ifRevision = revision, confirm = true },
            runAfterSave: true);

    private Task Request(string command, object? parameters, bool runAfterSave)
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
                var result = await execute("music.librarySync." + command, parameters, token);
                if (ticket != epoch || token.IsCancellationRequested) return;
                Apply(result);
                bool saved = Okay(result);
                if (!saved)
                {
                    string error = Status;
                    var latest = await execute("music.librarySync.get", null, token);
                    if (ticket != epoch || token.IsCancellationRequested) return;
                    Apply(latest);
                    saved = command == "set" && SetMatches(parameters);
                    if (!saved)
                    {
                        Status = error;
                        return;
                    }
                }
                if (runAfterSave)
                {
                    result = await execute("music.librarySync.run", null, token);
                    if (ticket != epoch || token.IsCancellationRequested) return;
                    Apply(result);
                    if (!Okay(result))
                    {
                        string error = Status;
                        var latest = await execute("music.librarySync.get", null, token);
                        if (ticket != epoch || token.IsCancellationRequested) return;
                        Apply(latest);
                        Status = error;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch
            {
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

    private bool SetMatches(object? parameters)
    {
        if (parameters is null) return false;
        var value = JsonSerializer.SerializeToElement(parameters);
        if (!value.TryGetProperty("platform", out var platform) ||
            !value.TryGetProperty("favorites", out var favorites) ||
            !value.TryGetProperty("playlists", out var playlists) ||
            platform.ValueKind != JsonValueKind.String ||
            favorites.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            playlists.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;
        var row = Platforms.FirstOrDefault(item => item.Platform == platform.GetString());
        return row is not null && row.Favorites == favorites.GetBoolean() &&
            row.Playlists == playlists.GetBoolean();
    }

    private void Apply(JsonElement result)
    {
        confirmationToken = "";
        Confirmation = "";
        if (result.TryGetProperty("confirmation", out var confirm) && confirm.ValueKind == JsonValueKind.Object)
        {
            confirmationToken = Text(confirm, "token");
            long count = confirm.GetProperty("count").GetInt64();
            Confirmation = Text(confirm, "code") switch
            {
                "DELETE_REMOTE_PLAYLIST" => $"删除远端歌单（{count} 首）？",
                "EMPTY_REMOTE" => $"确认远端为空并同步删除（{count} 项）？",
                _ => $"确认同步删除 {count} 项？"
            };
        }
        Changed(nameof(HasConfirmation));

        if (Okay(result) && result.TryGetProperty("revision", out var version) &&
            result.TryGetProperty("platforms", out var platforms))
        {
            long observed = version.GetInt64();
            if (observed >= revision)
            {
                var values = Platforms.Select(row =>
                {
                    var value = platforms.GetProperty(row.Platform);
                    return (Row: row, Favorites: value.GetProperty("favorites").GetBoolean(),
                        Playlists: value.GetProperty("playlists").GetBoolean());
                }).ToArray();
                revision = observed;
                foreach (var value in values) value.Row.Apply(value.Favorites, value.Playlists);
            }
        }

        string code = Text(result, "code"), phase = "";
        if (result.TryGetProperty("status", out var state))
        {
            if (code.Length == 0) code = Text(state, "code");
            phase = Text(state, "phase");
        }
        Status = code switch
        {
            "CONFIRMATION_REQUIRED" => "等待确认",
            "AUTH_REQUIRED" => "未登录",
            "ACCOUNT_REPLACED" or "ACCOUNT_REBIND_REQUIRED" => "账号已更换",
            "STALE_SESSION" or "SYNC_SUPERSEDED" => "同步已失效",
            "CANCELLED" => "",
            "SYNC_BUSY" => "正在同步",
            "COMMIT_UNKNOWN" or "PENDING_WRITE" => "同步设置保存失败",
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
            Changed(nameof(IsActive));
            Changed(nameof(CanEdit));
            await drain.WaitAsync(cancellationToken);
        }
        finally
        {
            previous?.Dispose();
            if (ticket == epoch)
            {
                busy = false;
                Changed(nameof(CanEdit));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        await DeactivateAsync();
        disposed = true;
    }

    private static bool Okay(JsonElement result) =>
        result.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
    private static string Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? "" : "";
    private void Changed([CallerMemberName] string name = "") =>
        PropertyChanged?.Invoke(this, new(name));
}
