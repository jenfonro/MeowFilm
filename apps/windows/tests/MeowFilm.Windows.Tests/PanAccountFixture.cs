using System.Text.Json;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;
using MeowFilm.Platform.Runtime;

namespace MeowFilm.Windows.Tests;

internal sealed class PanAccountFixture : NativeTestDatabase, IRuntimeClient, IRuntimeSettingsNotifications
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static readonly (string Id, string[] Fields)[] Providers =
    [
        ("baidu", ["cookie"]), ("quark", ["cookie"]), ("uc", ["cookie"]),
        ("tianyi", ["cookie", "username", "password"]), ("yidong", ["authorization", "refreshedAt"])
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
    internal new readonly List<JsonElement> Writes = [];
    internal Func<CancellationToken, Task>? ReadOverride;
    internal Func<Action, CancellationToken, Task>? WriteOverride;
    internal bool Offline, NotificationFails;
    internal void Account(string provider, long revision, string status, params string[] fields)
    {
        var allowed=Providers.Single(item=>item.Id==provider).Fields;
        var values=new List<(string,object?)>{("$provider",provider),("$revision",revision)};
        foreach(var field in new[]{"cookie","username","password","authorization","refreshedAt"})
            values.Add(("$"+field,fields.Contains(field)?field=="refreshedAt"?(object)1730000000000L:"synthetic-"+field:null));
        Inner.WriteAsync(db=>{
            db.Execute("UPDATE windows_pan_accounts SET account_revision=$revision,row_revision=row_revision+1,cookie=$cookie,username=$username,password=$password,authorization=$authorization,refreshedAt=$refreshedAt WHERE provider=$provider",values.ToArray());
            db.Execute("UPDATE windows_pan_state SET revision=revision+1 WHERE singleton=1");return true;
        }).GetAwaiter().GetResult();
        Accounts[provider]=new(provider,revision,fields.Length>0,status,fields.Where(allowed.Contains).ToArray());
    }
    internal void Corrupt(string provider) => Inner.WriteAsync(db=>db.Execute(
        "UPDATE windows_pan_accounts SET "+(provider=="yidong"?"authorization":"cookie")+"=$value WHERE provider=$provider",
        ("$value","synthetic\r\ninvalid"),("$provider",provider))).GetAwaiter().GetResult();
    internal RuntimeSettingsSnapshot Snapshot()=>new PanCredentialRepository(Inner).SnapshotAsync(default).GetAwaiter().GetResult();
    internal JsonElement Saved=>Snapshot().Document??JsonSerializer.SerializeToElement(new{version=1,accounts=new{}},Json);
    public override async Task<T> ReadAsync<T>(Func<DatabaseSession,T> read,CancellationToken token=default)
    {
        var result=await base.ReadAsync(read,token);
        if(ReadOverride is { } hook)await hook(token);
        return result;
    }
    public override async Task<T> WriteAsync<T>(Func<DatabaseSession,T> write,CancellationToken token=default,
        int retainedBytes=0,Action<Action>? commit=null)
    {
        var result=await base.WriteAsync(db=>{
            T value=default!; bool executed=false;
            void Execute(){if(executed)throw new InvalidOperationException("Only one actual transaction is allowed");executed=true;value=write(db);}
            if(WriteOverride is { } hook)hook(Execute,CancellationToken.None).GetAwaiter().GetResult();else Execute();
            if(!executed)throw new DocumentStoreException(DocumentStoreError.Storage);return value;
        },token,retainedBytes,commit);
        Writes.Add(Saved);
        foreach(var provider in Providers.Select(item=>item.Id))
        {
            var account=new PanSettingsStore(this,Inner).ReadAsync(provider,default).GetAwaiter().GetResult();
            Accounts[provider]=account;
        }
        return result;
    }
    public void SettingsChanged(string block)
    {
        if (NotificationFails) throw new BridgeException("CLOSED");
        if (!Offline) Notifications.Add(block);
    }
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
