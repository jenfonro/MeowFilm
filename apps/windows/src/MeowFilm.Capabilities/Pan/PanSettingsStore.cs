using System.Text.Json;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

internal sealed class PanSettingsStore(IRuntimeClient runtime, IDocumentStore store)
{
    private readonly SettingsDocument document = new(store, "pan.accounts.v1");
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
            var saved = await document.ReadAsync(token).ConfigureAwait(false) ?? Empty;
            Validate(saved);
            return Providers.Select(provider => Snapshot(saved, provider.GetProperty("provider").GetString()!)).ToArray();
        }
        catch (DocumentStoreException error) { throw StorageError(error); }
    }

    internal async Task<PanAccountSnapshot> ReadAsync(string provider, CancellationToken token) =>
        (await ReadAsync(token).ConfigureAwait(false)).Single(account => account.Provider == provider);

    internal Task<PanAccountSnapshot> SaveAsync(string provider, IReadOnlyDictionary<string, object> credentials,
        CancellationToken token = default) => ChangeAsync(provider, credentials, token);
    internal Task<PanAccountSnapshot> ClearAsync(string provider, CancellationToken token = default) =>
        ChangeAsync(provider, new Dictionary<string, object>(), token);

    private async Task<PanAccountSnapshot> ChangeAsync(string provider, IReadOnlyDictionary<string, object> credentials, CancellationToken token)
    {
        if (credentials is null || credentials.Values.Any(value => value is not (string or long)))
            throw new BridgeException("INVALID_REQUEST");
        var replacement = JsonSerializer.SerializeToElement(credentials);
        ValidateCredentials(provider, replacement, "INVALID_REQUEST");
        JsonElement saved;
        try
        {
            saved = await document.UpdateAsync(current =>
            {
                var root = current ?? Empty;
                Validate(root);
                var accounts = root.GetProperty("accounts").EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone());
                long revision = Snapshot(root, provider).Revision + 1;
                if (revision > BridgeCodec.MaximumSafeInteger) throw new BridgeException("STORAGE_ERROR");
                accounts[provider] = JsonSerializer.SerializeToElement(new { revision, credentials = replacement });
                return JsonSerializer.SerializeToElement(new { version = 1, accounts });
            }, token).ConfigureAwait(false);
        }
        catch (DocumentStoreException error) { throw StorageError(error); }
        // A runtime failure cannot change the successful local commit.
        try { (runtime as IRuntimeSettingsNotifications)?.SettingsChanged("pan.accounts"); }
        catch (Exception) { }
        return Snapshot(saved, provider);
    }

    private static Exception StorageError(DocumentStoreException error) => error.Error switch
    {
        DocumentStoreError.Conflict => new PanAccountException("STALE_ACCOUNT"),
        DocumentStoreError.Corrupt or DocumentStoreError.UnsupportedVersion => new BridgeException("CORRUPT_STORAGE"),
        DocumentStoreError.Closed => new BridgeException("CLOSED"),
        _ => new BridgeException("STORAGE_ERROR")
    };

    private static void Shape(JsonElement value, string code, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.EnumerateObject().Select(item => item.Name).Order().SequenceEqual(names.Order()))
            throw new BridgeException(code);
    }

    private static void Validate(JsonElement root)
    {
        const string code = "CORRUPT_STORAGE";
        Shape(root, code, "version", "accounts");
        if (root.GetProperty("version").ValueKind != JsonValueKind.Number ||
            !root.GetProperty("version").TryGetInt32(out int version) || version != 1 ||
            root.GetProperty("accounts").ValueKind != JsonValueKind.Object)
            throw new BridgeException(code);
        var accounts = root.GetProperty("accounts").EnumerateObject().ToArray();
        if (accounts.Select(item => item.Name).Distinct().Count() != accounts.Length)
            throw new BridgeException(code);
        foreach (var account in accounts)
        {
            Shape(account.Value, code, "revision", "credentials");
            if (account.Value.GetProperty("revision").ValueKind != JsonValueKind.Number ||
                !account.Value.GetProperty("revision").TryGetInt64(out long revision) ||
                revision is <= 0 or > BridgeCodec.MaximumSafeInteger)
                throw new BridgeException(code);
            ValidateCredentials(account.Name, account.Value.GetProperty("credentials"), code);
        }
    }

    private static void ValidateCredentials(string provider, JsonElement credentials, string code)
    {
        var declaration = Providers.SingleOrDefault(value => value.GetProperty("provider").GetString() == provider);
        if (declaration.ValueKind == JsonValueKind.Undefined || credentials.ValueKind != JsonValueKind.Object)
            throw new BridgeException(code);
        var allowed = declaration.GetProperty("credentialFields").EnumerateArray().Select(value => value.GetString()!).ToHashSet();
        var fields = credentials.EnumerateObject().ToArray();
        if (fields.Select(item => item.Name).Distinct().Count() != fields.Length)
            throw new BridgeException(code);
        foreach (var field in fields)
        {
            if (!allowed.Contains(field.Name)) throw new BridgeException(code);
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

    private static PanAccountSnapshot Snapshot(JsonElement root, string provider)
    {
        var accounts = root.GetProperty("accounts");
        if (!accounts.TryGetProperty(provider, out var value)) return new(provider, 0, false, "signedOut", []);
        var fields = value.GetProperty("credentials").EnumerateObject().Where(item =>
            item.Value.ValueKind == JsonValueKind.String ? item.Value.GetString()!.Length > 0 :
                item.Value.ValueKind == JsonValueKind.Number && item.Value.GetInt64() > 0).Select(item => item.Name).ToArray();
        return new(provider, value.GetProperty("revision").GetInt64(), fields.Length > 0,
            fields.Length > 0 ? "signedIn" : "signedOut", fields);
    }
}
