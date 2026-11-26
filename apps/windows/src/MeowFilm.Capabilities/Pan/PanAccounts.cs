using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeowFilm.Core.Debug;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

public sealed partial class PanAccounts(IRuntimeClient runtime, IStorageDatabase store) : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly PanSettingsStore settings = new(runtime, store);
    private readonly HashSet<Task> pending = [];
    private CancellationTokenSource? loading;
    private Task load = Task.CompletedTask;
    private Task nativeWrites = Task.CompletedTask;
    private long epoch;
    private bool active, stopping, reloadPending;
    private bool loadFailed;
    private string notice = "";
    public ObservableCollection<PanAccountRow> Rows { get; } = [];
    public bool IsLoading => loading is not null;
    public bool LoadFailed => loadFailed;
    public string Notice => notice;
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    internal void Notify(string message)
    {
        notice = message;
        Changed(nameof(Notice));
    }
    private async Task<JsonElement> Call(string operation, object? parameters = null,
        CancellationToken token = default, TimeSpan? timeout = null)
    {
        var envelope = timeout is { } deadline
            ? await runtime.CallAsync("pan." + operation, parameters, deadline, token)
            : await runtime.CallAsync("pan." + operation, parameters, token);
        if (envelope.GetProperty("contract").GetString() != "pan.runtime.v1")
            throw new BridgeException("INVALID_RESPONSE");
        if (!envelope.GetProperty("ok").GetBoolean())
            throw new PanAccountException(envelope.GetProperty("code").GetString()!);
        return envelope.GetProperty("value").Clone();
    }
    private Task Track(Task work)
    {
        pending.Add(work);
        return Observe();
        async Task Observe()
        {
            try { await work; }
            finally { pending.Remove(work); }
        }
    }
    private static string Error(Exception error) => error switch
    {
        PanAccountException { Code: "AUTH_REQUIRED" or "AUTH_FAILED" } => "网盘凭据已失效",
        PanAccountException { Code: "LOGIN_EXPIRED" } => "二维码已过期",
        PanAccountException { Code: "CHALLENGE_REQUIRED" } => "网盘需要登录验证",
        PanAccountException { Code: "STALE_ACCOUNT" } => "网盘账号已变更",
        BridgeException { Code: "STALE_ACCOUNT" } => "网盘账号已变更",
        PanAccountException { Code: "STORAGE_ERROR" or "CORRUPT_STORAGE" } => "网盘凭据保存失败",
        BridgeException { Code: "STORAGE_ERROR" or "CORRUPT_STORAGE" } => "网盘凭据保存失败",
        PanAccountException { Code: "INVALID_REQUEST" } => "请检查网盘凭据格式",
        BridgeException { Code: "INVALID_REQUEST" } => "请检查网盘凭据格式",
        _ => "网盘操作失败，请重试"
    };

    public Task ActivateAsync()
    {
        if (stopping) return Task.CompletedTask;
        if (active) return load;
        active = true;
        foreach (var row in Rows) { row.Active = true; row.Changed(); }
        return ReloadAsync();
    }

    public Task ReloadAsync()
    {
        if (!active || stopping) return load;
        if (loading is not null || LoginOpen || Rows.Any(row => row.IsBusy))
        {
            reloadPending = true;
            return load;
        }
        reloadPending = false;
        var request = new CancellationTokenSource();
        loading = request;
        long ticket = epoch;
        loadFailed = false;
        Notify("");
        Changed(nameof(IsLoading)); Changed(nameof(LoadFailed));
        return load = Track(Read());

        async Task Read()
        {
            try
            {
                await nativeWrites;
                request.Token.ThrowIfCancellationRequested();
                if (Rows.Count == 0)
                {
                    var rows = PanSettingsStore.Providers.Select(value => new PanAccountRow(value)).ToArray();
                    if (!active || ticket != epoch) return;
                    foreach (var row in rows) { row.Active = true; Rows.Add(row); }
                }
                var generations = Rows.ToDictionary(row => row.Provider, row => row.Generation);
                var accounts = (await settings.ReadAsync(request.Token))
                    .ToDictionary(account => account.Provider);
                if (!active || ticket != epoch) return;
                foreach (var row in Rows)
                    if (row.Generation == generations[row.Provider]) row.Publish(accounts[row.Provider]);
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (active && ticket == epoch) { loadFailed = true; Notify(Error(error)); }
            }
            finally
            {
                if (ReferenceEquals(loading, request)) loading = null;
                request.Dispose();
                Changed(nameof(IsLoading)); Changed(nameof(LoadFailed));
                FlushPendingReload();
            }
        }
    }

    private void FlushPendingReload()
    {
        if (reloadPending && active && !stopping && loading is null && !LoginOpen && !Rows.Any(row => row.IsBusy))
            _ = ReloadAsync();
    }

    public Task RefreshAsync(PanAccountRow row)
    {
        if (!active || stopping || !Rows.Contains(row) || !row.HasSnapshot || !row.Configured ||
            row.IsBusy || row.Editing || !row.SupportsRefresh) return Task.CompletedTask;
        var before = Task.WhenAll(cleanup, nativeWrites);
        return RunRow(row, async (request, current) =>
        {
            await before;
            if (!current()) return;
            var reply = await Call("refresh", new { provider = row.Provider }, request.Token);
            if (!reply.GetProperty("refreshed").GetBoolean()) throw new BridgeException("INVALID_RESPONSE");
            var reported = PanAccountSnapshot.Read(reply.GetProperty("account"));
            var saved = await settings.ReadAsync(row.Provider, request.Token);
            if (!current()) return;
            row.Publish(saved);
            ValidateOnlineAccount(row.Provider, reported, saved);
            row.Publish(saved with { Status = reported.Status });
        });
    }

    public Task LogoutAsync(PanAccountRow row)
    {
        if (!active || stopping || !Rows.Contains(row) || !row.CanLogout) return Task.CompletedTask;
        var before = nativeWrites;
        return nativeWrites = RunRow(row, async (request, current) =>
        {
            await Task.Yield(); // Publish the drain before an admitted clear can outlive its page.
            await before;
            if (!current()) return;
            var account = await settings.ClearAsync(row.Provider, request.Token);
            if (current()) { row.Publish(account); Notify("已退出网盘账号"); }
        });
    }

    private Task RunRow(PanAccountRow row, Func<CancellationTokenSource, Func<bool>, Task> execute)
    {
        var revision = row.Revision;
        long generation = ++row.Generation, ticket = epoch;
        var request = new CancellationTokenSource();
        row.Request = request; row.Changed();
        Notify("");
        return Track(Run());
        bool Current() => active && ticket == epoch && generation == row.Generation && !request.IsCancellationRequested;
        async Task Run()
        {
            try { await execute(request, Current); }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (Current())
                {
                    if (error is PanAccountException { Code: "AUTH_REQUIRED" or "AUTH_FAILED" or "STALE_ACCOUNT" } ||
                        error is BridgeException { Code: "STALE_ACCOUNT" })
                    {
                        try
                        {
                            var saved = await settings.ReadAsync(row.Provider, request.Token);
                            if (!Current()) return;
                            bool expired = error is PanAccountException { Code: "AUTH_REQUIRED" or "AUTH_FAILED" } &&
                                saved.Configured == true && saved.Revision == revision;
                            row.Publish(expired ? saved with { Status = "expired" } : saved);
                        }
                        catch (OperationCanceledException) when (request.IsCancellationRequested) { return; }
                        catch (Exception readError)
                        {
                            DebugLog.Warn(DebugCategories.Storage, "pan.accounts", "refresh.reload.error",
                                new { provider = row.Provider }, error: readError);
                            if (!Current()) return;
                        }
                    }
                    Notify(Error(error));
                }
            }
            finally
            {
                if (ReferenceEquals(row.Request, request)) row.Request = null;
                request.Dispose(); row.Changed();
                if (ReferenceEquals(login?.Row, row)) LoginChanged();
                FlushPendingReload();
            }
        }
    }

    private static void ValidateOnlineAccount(string provider, PanAccountSnapshot reported, PanAccountSnapshot saved)
    {
        if (reported.Provider != provider) throw new BridgeException("INVALID_RESPONSE");
        if (reported.ErrorCode is { } code) throw new PanAccountException(code);
        if (saved.ErrorCode is { } savedCode) throw new PanAccountException(savedCode);
        if (reported.Revision != saved.Revision) throw new PanAccountException("STALE_ACCOUNT");
        if (reported.Status != "signedIn" || saved.Configured != true)
            throw new BridgeException("INVALID_RESPONSE");
    }

    public Task DeactivateAsync()
    {
        active = false; reloadPending = false; epoch++;
        loading?.Cancel(); loading = null;
        foreach (var row in Rows)
        {
            row.Active = false; row.Generation++;
            var request = row.Request; row.Request = null;
            request?.Cancel(); row.Changed();
        }
        Changed(nameof(IsLoading));
        return Task.WhenAll(CloseLoginAsync(), nativeWrites);
    }

    internal async Task PrepareShutdownAsync()
    {
        stopping = true;
        await DeactivateAsync();
        while (pending.Count > 0) await Task.WhenAll(pending.ToArray());
    }
    public async ValueTask DisposeAsync() => await PrepareShutdownAsync();
}
