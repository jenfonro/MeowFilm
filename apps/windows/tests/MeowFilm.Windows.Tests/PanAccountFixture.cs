using System.Text.Json;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Windows.Tests;

internal sealed class PanAccountFixture : IRuntimeClient
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static readonly (string Id, string[] Fields)[] Providers =
    [
        ("baidu", ["cookie"]), ("quark", ["cookie"]), ("uc", ["cookie"]),
        ("tianyi", ["cookie", "username", "password"]), ("yidong", ["authorization", "refreshedAt"]),
        ("quark_tv", ["refreshToken", "deviceId", "accessToken", "expiresAt"]),
        ("uc_tv", ["refreshToken", "deviceId", "accessToken", "expiresAt"])
    ];
    internal readonly Dictionary<string, PanAccountSnapshot> Accounts = Providers.ToDictionary(
        item => item.Id, item => new PanAccountSnapshot(item.Id, 0, false, "signedOut", []));
    internal readonly List<(string Method, TimeSpan? Timeout)> Calls = [];
    internal readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<JsonElement>>> Overrides = [];
    internal readonly List<string> Cancelled = [];
    internal string ChallengeType = "url";
    internal string Challenge = "https://drive.fixture.invalid/登录?ticket=synthetic#qr";
    internal string LastSession = "";
    internal int Starts;

    internal void Account(string provider, long revision, string status, params string[] fields) =>
        Accounts[provider] = new(provider, revision, fields.Length > 0, status, fields);
    internal static JsonElement Ok(object value) =>
        JsonSerializer.SerializeToElement(new { ok = true, contract = "pan.runtime.v1", value }, Json);
    internal static JsonElement Failure(string code) =>
        JsonSerializer.SerializeToElement(new { ok = false, contract = "pan.runtime.v1", code }, Json);
    internal JsonElement Start(string provider, string? id = null) => Ok(new
    {
        sessionId = id ?? LastSession, provider, status = "pending",
        challenge = new { type = ChallengeType, url = Challenge },
        expiresAt = DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds()
    });
    internal JsonElement Poll(string provider, string status, PanAccountSnapshot? account = null, string? id = null) =>
        Ok(new { sessionId = id ?? LastSession, provider, status, account });

    public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default) =>
        Call(method, parameters, null, cancellationToken);
    public Task<JsonElement> CallAsync(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        Call(method, parameters, timeout, cancellationToken);
    private Task<JsonElement> Call(string method, object? parameters, TimeSpan? timeout, CancellationToken token)
    {
        Calls.Add((method, timeout));
        var value = JsonSerializer.SerializeToElement(parameters ?? new { }, Json);
        if (Overrides.TryGetValue(method, out var execute)) return execute(value, token);
        token.ThrowIfCancellationRequested();
        object result;
        switch (method)
        {
            case "pan.providers":
                result = new { providers = Providers.Select(item => new
                {
                    provider = item.Id, credentialFields = item.Fields, list = true, prepare = false,
                    modes = new[] { "streaming", "download" }, login = item.Id is "baidu" or "quark" or "uc", refresh = true
                }).ToArray() };
                break;
            case "pan.settings.get": result = new { accounts = Providers.Select(item => Accounts[item.Id]).ToArray() }; break;
            case "pan.settings.set":
            case "pan.settings.clear":
                string id = value.GetProperty("provider").GetString()!;
                string[] fields = method.EndsWith(".clear", StringComparison.Ordinal) ? [] :
                    value.GetProperty("credentials").EnumerateObject().Where(field =>
                        field.Value.ValueKind == JsonValueKind.Number ? field.Value.GetInt64() != 0 :
                        field.Value.GetString()!.Length != 0).Select(field => field.Name).ToArray();
                Account(id, Accounts[id].Revision + 1, "signedOut", fields);
                result = Accounts[id];
                break;
            case "pan.refresh":
                string provider = value.GetProperty("provider").GetString()!;
                if (!Accounts[provider].Configured) return Task.FromResult(Failure("AUTH_REQUIRED"));
                Accounts[provider] = Accounts[provider] with { Status = "signedIn" };
                result = new { refreshed = true, account = Accounts[provider] };
                break;
            case "pan.login.start":
                Starts++; LastSession = "session-" + Starts;
                return Task.FromResult(Start(value.GetProperty("provider").GetString()!));
            case "pan.login.poll": return Task.FromResult(Poll("baidu", "pending"));
            case "pan.login.cancel":
                string sessionId = value.GetProperty("sessionId").GetString()!;
                Cancelled.Add(sessionId);
                result = new { sessionId, status = "cancelled" };
                break;
            default: throw new InvalidOperationException("Unexpected fixture operation");
        }
        return Task.FromResult(Ok(result));
    }
    public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
        ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Pan accounts never upload source");

    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    internal sealed class DelayedClear
    {
        internal readonly TaskCompletionSource<bool> Entered = Gate<bool>(), Release = Gate<bool>();
        internal Task Receipt = Task.CompletedTask;
        internal DelayedClear(PanAccountFixture runtime)
        {
            runtime.Overrides["pan.settings.clear"] = (parameters, token) =>
            {
                runtime.Overrides.Remove("pan.settings.clear");
                var receipt = Commit(parameters.GetProperty("provider").GetString()!);
                Receipt = receipt;
                // Only HTTP observation can be cancelled; the admitted storage side effect remains.
                return receipt.WaitAsync(token);
            };
            async Task<JsonElement> Commit(string provider)
            {
                Entered.TrySetResult(true);
                await Release.Task;
                runtime.Account(provider, runtime.Accounts[provider].Revision + 1, "signedOut");
                return Ok(runtime.Accounts[provider]);
            }
        }
    }
    internal static TaskCompletionSource<T> Gate<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
