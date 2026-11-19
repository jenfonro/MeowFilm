using System.Text.Json;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

internal sealed class PanSettingsStore(IRuntimeClient runtime, IStorageDatabase database)
{
    private readonly PanCredentialRepository repository = new(database);
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { version = 1, accounts = new { } });
    internal static IReadOnlyList<JsonElement> Providers { get; } = new[]
    {
        Provider("baidu", ["cookie"], true), Provider("quark", ["cookie"], true), Provider("uc", ["cookie"], true),
        Provider("tianyi", ["cookie", "username", "password"], false), Provider("yidong", ["authorization", "refreshedAt"], false)
    };
    private static JsonElement Provider(string id, string[] fields, bool qr) =>
        JsonSerializer.SerializeToElement(new { provider = id, credentialFields = fields, login = qr, refresh = true });

    internal async Task<IReadOnlyList<PanAccountSnapshot>> ReadAsync(CancellationToken token)
    {
        try
        {
            var saved = (await repository.SnapshotAsync(token).ConfigureAwait(false)).Document ?? Empty;
            var accounts = Accounts(saved);
            return Providers.Select(provider => Snapshot(accounts, provider.GetProperty("provider").GetString()!)).ToArray();
        }
        catch (DocumentStoreException error) { throw StorageError(error); }
    }

    internal async Task<PanAccountSnapshot> ReadAsync(string provider, CancellationToken token)
    {
        try { return Snapshot(Accounts(await repository.ReadAsync(provider, token).ConfigureAwait(false)), provider); }
        catch (DocumentStoreException error) { throw StorageError(error); }
    }

    internal Task<PanAccountSnapshot> SaveAsync(string provider, IReadOnlyDictionary<string, object> credentials,
        CancellationToken token = default) => ChangeAsync(provider, credentials, token);
    internal Task<PanAccountSnapshot> ClearAsync(string provider, CancellationToken token = default) =>
        ChangeAsync(provider, new Dictionary<string, object>(), token);

    private async Task<PanAccountSnapshot> ChangeAsync(string provider, IReadOnlyDictionary<string, object> credentials, CancellationToken token)
    {
        var fields = Fields(provider, "INVALID_REQUEST");
        var replacement = JsonSerializer.SerializeToElement(credentials.Where(item => fields.Contains(item.Key))
            .ToDictionary(item => item.Key, item => item.Value));
        ValidateCredentials(provider, replacement, "INVALID_REQUEST");
        JsonElement saved;
        try
        {
            var current = Accounts(await repository.ReadAsync(provider, token).ConfigureAwait(false));
            var account = Account(current, provider);
            if (Snapshot(current, provider).ErrorCode is not null) throw new BridgeException("CORRUPT_STORAGE");
            long revision = account is null ? 0 : Revision(account.Value);
            long rowRevision = account is null ? 0 : account.Value.GetProperty("rowRevision").GetInt64();
            saved = await repository.ReplaceAsync(provider, replacement, revision, rowRevision, token).ConfigureAwait(false);
        }
        catch (DocumentStoreException error) { throw StorageError(error); }
        // A runtime failure cannot change the successful local commit.
        try { (runtime as IRuntimeSettingsNotifications)?.SettingsChanged("pan.accounts"); }
        catch (Exception) { }
        return Snapshot(Accounts(saved), provider);
    }

    private static Exception StorageError(DocumentStoreException error) => error.Error switch
    {
        DocumentStoreError.Conflict => new PanAccountException("STALE_ACCOUNT"),
        DocumentStoreError.Corrupt or DocumentStoreError.UnsupportedVersion => new BridgeException("CORRUPT_STORAGE"),
        DocumentStoreError.Closed => new BridgeException("CLOSED"),
        _ => new BridgeException("STORAGE_ERROR")
    };

    private static JsonElement Accounts(JsonElement root)
    {
        try
        {
            if (BridgeCodec.RequiredProperty(root, "version").GetInt32() != 1) throw new BridgeException("CORRUPT_STORAGE");
            var accounts = BridgeCodec.RequiredProperty(root, "accounts");
            if (accounts.ValueKind != JsonValueKind.Object) throw new BridgeException("CORRUPT_STORAGE");
            return accounts;
        }
        catch { throw new BridgeException("CORRUPT_STORAGE"); }
    }

    private static JsonElement? Account(JsonElement accounts, string provider)
    {
        var matches = accounts.EnumerateObject().Where(item => item.NameEquals(provider)).ToArray();
        if (matches.Length > 1) throw new BridgeException("CORRUPT_STORAGE");
        return matches.Length == 0 ? null : matches[0].Value;
    }

    private static long Revision(JsonElement account)
    {
        try
        {
            long revision = BridgeCodec.RequiredProperty(account, "revision").GetInt64();
            if (revision is <= 0 or > BridgeCodec.MaximumSafeInteger) throw new BridgeException("CORRUPT_STORAGE");
            return revision;
        }
        catch { throw new BridgeException("CORRUPT_STORAGE"); }
    }

    private static HashSet<string> Fields(string provider, string code)
    {
        var declaration = Providers.SingleOrDefault(value => value.GetProperty("provider").GetString() == provider);
        if (declaration.ValueKind == JsonValueKind.Undefined) throw new BridgeException(code);
        return declaration.GetProperty("credentialFields").EnumerateArray().Select(value => value.GetString()!).ToHashSet();
    }

    private static JsonElement PreserveExtensions(JsonElement original, JsonElement replacement, IReadOnlySet<string> fields) =>
        SettingsDocument.Patch(original, replacement.EnumerateObject().ToDictionary(field => field.Name, field => field.Value), fields);

    private static void ValidateCredentials(string provider, JsonElement credentials, string code)
    {
        var allowed = Fields(provider, code);
        if (credentials.ValueKind != JsonValueKind.Object) throw new BridgeException(code);
        var fields = credentials.EnumerateObject().Where(item => allowed.Any(name => item.NameEquals(name))).ToArray();
        if (fields.Select(item => item.Name).Distinct().Count() != fields.Length) throw new BridgeException(code);
        foreach (var field in fields)
        {
            if (field.Name is "refreshedAt")
            {
                if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt64(out long number) ||
                    number is < 0 or > BridgeCodec.MaximumSafeInteger) throw new BridgeException(code);
            }
            else if (field.Value.ValueKind != JsonValueKind.String ||
                field.Value.GetString() is not { } text || text.Contains('\0') ||
                field.Name != "password" && text.IndexOfAny(['\r', '\n']) >= 0)
                throw new BridgeException(code);
        }
    }

    private static PanAccountSnapshot Snapshot(JsonElement accounts, string provider)
    {
        var allowed = Fields(provider, "INVALID_REQUEST");
        try
        {
            var value = Account(accounts, provider);
            if (value is null) return new(provider, 0, false, "signedOut", []);
            long revision = Revision(value.Value);
            var credentials = BridgeCodec.RequiredProperty(value.Value, "credentials");
            ValidateCredentials(provider, credentials, "CORRUPT_STORAGE");
            var fields = credentials.EnumerateObject().Where(item => allowed.Any(name => item.NameEquals(name))).Where(item =>
                item.Value.ValueKind == JsonValueKind.String ? item.Value.GetString()!.Length > 0 : item.Value.GetInt64() > 0)
                .Select(item => item.Name).ToArray();
            return new(provider, revision, fields.Length > 0, fields.Length > 0 ? "signedIn" : "signedOut", fields);
        }
        catch (BridgeException error) when (error.Code is "CORRUPT_STORAGE" or "INVALID_MESSAGE")
        { return PanAccountSnapshot.Corrupt(provider); }
        catch (InvalidOperationException) { return PanAccountSnapshot.Corrupt(provider); }
    }
}
