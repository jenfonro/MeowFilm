using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities;

public sealed partial class PanAccounts(IRuntimeClient runtime) : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly HashSet<Task> pending = [];
    private CancellationTokenSource? loading;
    private Task load = Task.CompletedTask;
    private long epoch;
    private bool active, stopping;
    private bool loadFailed;
    private string notice = "";
    public ObservableCollection<PanAccountRow> Rows { get; } = [];
    public bool IsLoading => loading is not null;
    public bool LoadFailed => loadFailed;
    public string Notice => notice;
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private void Notify(string message)
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
        if (!active || stopping || loading is not null || LoginOpen || Rows.Any(row => row.IsBusy)) return load;
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
                await cleanup;
                request.Token.ThrowIfCancellationRequested();
                if (Rows.Count == 0)
                {
                    var providers = await Call("providers", token: request.Token);
                    var rows = providers.GetProperty("providers").EnumerateArray().Select(value => new PanAccountRow(value)).ToArray();
                    if (!active || ticket != epoch) return;
                    foreach (var row in rows) { row.Active = true; Rows.Add(row); }
                }
                var generations = Rows.ToDictionary(row => row.Provider, row => row.Generation);
                var reply = await Call("settings.get", token: request.Token);
                var accounts = reply.GetProperty("accounts").EnumerateArray().Select(PanAccountSnapshot.Read)
                    .ToDictionary(account => account.Provider);
                if (!active || ticket != epoch) return;
                foreach (var row in Rows)
                    if (row.Generation == generations[row.Provider]) row.Publish(accounts[row.Provider]);
                await Task.WhenAll(Rows.Where(row => row.Configured && row.SupportsRefresh &&
                    row.Generation == generations[row.Provider]).Select(RefreshAsync));
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
            }
        }
    }

    public Task RefreshAsync(PanAccountRow row)
    {
        if (!active || stopping || !Rows.Contains(row) || !row.HasSnapshot || !row.Configured ||
            row.IsBusy || row.Editing || !row.SupportsRefresh) return Task.CompletedTask;
        var before = cleanup;
        return RunRow(row, async (request, current) =>
        {
            await before;
            if (!current()) return;
            var reply = await Call("refresh", new { provider = row.Provider }, request.Token);
            if (!reply.GetProperty("refreshed").GetBoolean()) throw new BridgeException("INVALID_RESPONSE");
            if (current()) row.Publish(PanAccountSnapshot.Read(reply.GetProperty("account")));
        });
    }

    public Task LogoutAsync(PanAccountRow row)
    {
        if (!active || stopping || !Rows.Contains(row) || !row.CanLogout) return Task.CompletedTask;
        long revision = row.Revision;
        var before = cleanup;
        return cleanup = RunRow(row, async (_, current) =>
        {
            await Task.Yield(); // Publish the drain before an admitted clear can outlive its page.
            await before;
            if (!current()) return;
            var account = PanAccountSnapshot.Read(await Call("settings.clear", new { provider = row.Provider }));
            if (account.Revision <= revision || account.Configured || account.Status != "signedOut")
                throw new BridgeException("INVALID_RESPONSE");
            if (current()) { row.Publish(account); Notify("已退出网盘账号"); }
        });
    }

    private Task RunRow(PanAccountRow row, Func<CancellationTokenSource, Func<bool>, Task> execute)
    {
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
                    if (error is PanAccountException { Code: "AUTH_REQUIRED" or "AUTH_FAILED" }) row.Expire();
                    Notify(Error(error));
                }
            }
            finally
            {
                if (ReferenceEquals(row.Request, request)) row.Request = null;
                request.Dispose(); row.Changed();
                if (ReferenceEquals(login?.Row, row)) LoginChanged();
            }
        }
    }

    public Task DeactivateAsync()
    {
        active = false; epoch++;
        loading?.Cancel(); loading = null;
        foreach (var row in Rows)
        {
            row.Active = false; row.Generation++;
            var request = row.Request; row.Request = null;
            request?.Cancel(); row.Changed();
        }
        Changed(nameof(IsLoading));
        return CloseLoginAsync();
    }

    internal async Task PrepareShutdownAsync()
    {
        stopping = true;
        await DeactivateAsync();
        while (pending.Count > 0) await Task.WhenAll(pending.ToArray());
    }
    internal void Resume() => stopping = false;
    public async ValueTask DisposeAsync() => await PrepareShutdownAsync();
}
