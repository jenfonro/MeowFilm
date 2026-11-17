using System.Text.Json;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

internal sealed class PanAccountFixture : IRuntimeClient, IDocumentStore, IRuntimeSettingsNotifications
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
    internal readonly List<string> Notifications = [];
    internal readonly List<JsonElement> Writes = [];
    internal Func<CancellationToken, Task<DocumentSnapshot>>? ReadOverride;
    internal Func<ReadOnlyMemory<byte>, DocumentFingerprint, CancellationToken, Task<DocumentFingerprint>>? WriteOverride;
    internal bool Offline, NotificationFails;
    private byte[]? document;

    internal void Account(string provider, long revision, string status, params string[] fields)
    {
        Accounts[provider] = new(provider, revision, fields.Length > 0, status, fields);
        var root = Saved.GetProperty("accounts").EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone());
        root[provider] = JsonSerializer.SerializeToElement(new
        {
            revision, credentials = fields.ToDictionary(field => field, field =>
                field is "refreshedAt" or "expiresAt" ? (object)1730000000000L : "synthetic-" + field)
        }, Json);
        document = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, accounts = root }, Json);
    }
    internal JsonElement Saved
    {
        get
        {
            if (document is null) return JsonSerializer.SerializeToElement(new { version = 1, accounts = new { } }, Json);
            using var json = JsonDocument.Parse(document);
            return json.RootElement.Clone();
        }
    }
    internal void SetDocument(string json) => document = System.Text.Encoding.UTF8.GetBytes(json);
    internal DocumentSnapshot Snapshot() => new(document?.ToArray(),
        document is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(document));
    public Task<DocumentSnapshot> ReadAsync(string documentNamespace, CancellationToken cancellationToken = default)
    {
        Check(documentNamespace == "pan.accounts.v1", "only the protected pan namespace is used");
        if (ReadOverride is { } read) return read(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Snapshot());
    }
    public Task<DocumentFingerprint> WriteAsync(string documentNamespace, ReadOnlyMemory<byte> data,
        DocumentFingerprint expected, CancellationToken cancellationToken = default)
    {
        Check(documentNamespace == "pan.accounts.v1", "only the protected pan namespace is used");
        if (WriteOverride is { } write) return write(data, expected, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Commit(data, expected));
    }
    internal DocumentFingerprint Commit(ReadOnlyMemory<byte> data, DocumentFingerprint expected)
    {
        if (Snapshot().Fingerprint != expected) throw new DocumentStoreException(DocumentStoreError.Conflict);
        document = data.ToArray();
        var root = Saved;
        Writes.Add(root);
        foreach (var (provider, allowed) in Providers)
        {
            var rows = root.GetProperty("accounts").EnumerateObject().Where(item => item.NameEquals(provider)).ToArray();
            if (rows.Length == 0) { Accounts[provider] = new(provider, 0, false, "signedOut", []); continue; }
            try
            {
                if (rows.Length != 1) throw new InvalidOperationException();
                long revision = rows[0].Value.GetProperty("revision").GetInt64();
                if (revision is < 1 or > BridgeCodec.MaximumSafeInteger) throw new InvalidOperationException();
                var fields = rows[0].Value.GetProperty("credentials").EnumerateObject()
                    .Where(field => allowed.Any(name => field.NameEquals(name))).Where(field =>
                        field.Value.ValueKind == JsonValueKind.Number ? field.Value.GetInt64() != 0 :
                        field.Value.GetString()!.Length != 0).Select(field => field.Name).ToArray();
                Accounts[provider] = new(provider, revision, fields.Length > 0, fields.Length > 0 ? "signedIn" : "signedOut", fields);
            }
            catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or FormatException)
            { Accounts[provider] = PanAccountSnapshot.Corrupt(provider); }
        }
        return Snapshot().Fingerprint;
    }
    public void SettingsChanged(string block)
    {
        if (NotificationFails) throw new BridgeException("CLOSED");
        if (!Offline) Notifications.Add(block);
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
        if (Offline) throw new BridgeException("CLOSED");
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
                Account(id, (Accounts[id].Revision ?? throw new BridgeException("CORRUPT_STORAGE")) + 1, "signedOut", fields);
                result = Accounts[id];
                break;
            case "pan.refresh":
                string provider = value.GetProperty("provider").GetString()!;
                if (Accounts[provider].Configured != true) return Task.FromResult(Failure("AUTH_REQUIRED"));
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
    internal static TaskCompletionSource<T> Gate<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
