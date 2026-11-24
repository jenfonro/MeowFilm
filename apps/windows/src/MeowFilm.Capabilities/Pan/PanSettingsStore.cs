using System.Text.Json;
using MeowFilm.Core.Debug;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

internal sealed class PanSettingsStore(IRuntimeClient runtime, IStorageDatabase database)
{
    private readonly PanCredentialRepository repository = new(database);

    internal static IReadOnlyList<PanProviderDescriptor> Providers { get; } =
    [
        new("baidu", "百度网盘", ["cookie"], SupportsQr: true, SupportsRefresh: true),
        new("quark", "夸克网盘", ["cookie"], SupportsQr: true, SupportsRefresh: true),
        new("uc", "UC 网盘", ["cookie"], SupportsQr: true, SupportsRefresh: true),
        new("tianyi", "天翼云盘", ["cookie", "username", "password"], SupportsQr: false, SupportsRefresh: true),
        new("yidong", "移动云盘", ["authorization", "refreshedAt"], SupportsQr: false, SupportsRefresh: true)
    ];

    internal static PanProviderDescriptor? FindProvider(string provider)
    {
        foreach (var item in Providers)
            if (item.Provider == provider) return item;
        return null;
    }

    internal async Task<IReadOnlyList<PanAccountSnapshot>> ReadAsync(CancellationToken token)
    {
        try
        {
            var (rows, revision) = await repository.ReadAllAsync(token).ConfigureAwait(false);
            var byProvider = revision == 0
                ? new Dictionary<string, PanAccountRecord>(StringComparer.Ordinal)
                : rows.ToDictionary(r => r.Provider, StringComparer.Ordinal);
            return Providers.Select(provider =>
                ToSnapshot(provider.Provider, byProvider.GetValueOrDefault(provider.Provider))).ToArray();
        }
        catch (StorageException error) { throw MapStorageError(error); }
    }

    internal async Task<PanAccountSnapshot> ReadAsync(string provider, CancellationToken token)
    {
        try
        {
            var row = await repository.ReadOneAsync(provider, token).ConfigureAwait(false);
            return ToSnapshot(provider, row.AccountRevision > 0 ? row : null);
        }
        catch (StorageException error) { throw MapStorageError(error); }
    }

    internal Task<PanAccountSnapshot> SaveAsync(string provider, IReadOnlyDictionary<string, object> credentials,
        CancellationToken token = default) => ChangeAsync(provider, credentials, token);

    internal Task<PanAccountSnapshot> ClearAsync(string provider, CancellationToken token = default) =>
        ChangeAsync(provider, new Dictionary<string, object>(), token);

    private async Task<PanAccountSnapshot> ChangeAsync(string provider, IReadOnlyDictionary<string, object> credentials, CancellationToken token)
    {
        var parsed = ParseCredentials(provider, credentials, "INVALID_REQUEST");
        IReadOnlyList<PanAccountRecord> savedRows;
        try
        {
            var (revision, rowRevision) = await repository.ReadRevisionsAsync(provider, token).ConfigureAwait(false);
            (savedRows, _) = await repository.SaveAsync(
                provider, parsed, revision, rowRevision, refresh: false, token).ConfigureAwait(false);
        }
        catch (StorageException error) { throw MapStorageError(error); }
        // A runtime failure cannot change the successful local commit.
        try { (runtime as IRuntimeSettingsNotifications)?.SettingsChanged("pan.accounts"); }
        catch (Exception error)
        {
            DebugLog.Warn(DebugCategories.FilmCatalog, "pan", "settings.notify.failed", new { provider }, error: error);
        }
        return ToSnapshot(provider, savedRows.FirstOrDefault(r => r.Provider == provider));
    }

    private static PanCredentialsInput ParseCredentials(string provider, IReadOnlyDictionary<string, object> credentials, string code)
    {
        var allowed = Fields(provider, code);
        string? cookie = null, username = null, password = null, authorization = null;
        long? refreshedAt = null;
        foreach (var (name, value) in credentials)
        {
            if (!allowed.Contains(name)) continue;
            if (name is "refreshedAt")
            {
                long number = value switch
                {
                    int i => i,
                    long l => l,
                    JsonElement el when el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out long n) => n,
                    _ => throw new BridgeException(code)
                };
                if (number is < 0 or > BridgeCodec.MaximumSafeInteger) throw new BridgeException(code);
                refreshedAt = number;
            }
            else
            {
                string? text = value switch
                {
                    string s => s,
                    JsonElement el when el.ValueKind == JsonValueKind.String => el.GetString(),
                    _ => null
                };
                if (text is null || text.Contains('\0') || name != "password" && text.IndexOfAny(['\r', '\n']) >= 0)
                    throw new BridgeException(code);
                switch (name)
                {
                    case "cookie": cookie = text; break;
                    case "username": username = text; break;
                    case "password": password = text; break;
                    case "authorization": authorization = text; break;
                }
            }
        }
        return new PanCredentialsInput(cookie, username, password, authorization, refreshedAt);
    }

    internal static PanCredentialsInput ParseCredentials(string provider, JsonElement credentials, string code)
    {
        var allowed = Fields(provider, code);
        if (credentials.ValueKind != JsonValueKind.Object) throw new BridgeException(code);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cookie = null, username = null, password = null, authorization = null;
        long? refreshedAt = null;
        foreach (var field in credentials.EnumerateObject())
        {
            if (!seen.Add(field.Name) || !allowed.Contains(field.Name)) throw new BridgeException(code);
            if (field.Name is "refreshedAt")
            {
                if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt64(out long number) ||
                    number is < 0 or > BridgeCodec.MaximumSafeInteger) throw new BridgeException(code);
                refreshedAt = number;
            }
            else
            {
                if (field.Value.ValueKind != JsonValueKind.String ||
                    field.Value.GetString() is not { } text || text.Contains('\0') ||
                    field.Name != "password" && text.IndexOfAny(['\r', '\n']) >= 0)
                    throw new BridgeException(code);
                switch (field.Name)
                {
                    case "cookie": cookie = text; break;
                    case "username": username = text; break;
                    case "password": password = text; break;
                    case "authorization": authorization = text; break;
                }
            }
        }
        return new PanCredentialsInput(cookie, username, password, authorization, refreshedAt);
    }

    private static Exception MapStorageError(StorageException error) => error.Error switch
    {
        StorageError.Conflict => new PanAccountException("STALE_ACCOUNT"),
        StorageError.Corrupt or StorageError.UnsupportedVersion => new BridgeException("CORRUPT_STORAGE"),
        StorageError.Closed => new BridgeException("CLOSED"),
        _ => new BridgeException("STORAGE_ERROR")
    };

    private static HashSet<string> Fields(string provider, string code)
    {
        var declaration = FindProvider(provider) ?? throw new BridgeException(code);
        return declaration.CredentialFields.ToHashSet(StringComparer.Ordinal);
    }

    private static void ValidateStoredString(string? value, bool allowNewlines)
    {
        if (value is null) return;
        if (value.Contains('\0') || !allowNewlines && value.IndexOfAny(['\r', '\n']) >= 0)
            throw new BridgeException("CORRUPT_STORAGE");
    }

    private static PanAccountSnapshot ToSnapshot(string provider, PanAccountRecord? record)
    {
        var allowed = Fields(provider, "INVALID_REQUEST");
        try
        {
            if (record is null) return new(provider, 0, false, "signedOut", []);
            if (record.AccountRevision <= 0)
                throw new BridgeException("CORRUPT_STORAGE");
            ValidateStoredString(record.Cookie, allowNewlines: false);
            ValidateStoredString(record.Username, allowNewlines: false);
            ValidateStoredString(record.Password, allowNewlines: true);
            ValidateStoredString(record.Authorization, allowNewlines: false);
            if (record.RefreshedAtMs is < 0)
                throw new BridgeException("CORRUPT_STORAGE");

            var fields = new List<string>();
            if (allowed.Contains("cookie") && record.Cookie is { Length: > 0 }) fields.Add("cookie");
            if (allowed.Contains("username") && record.Username is { Length: > 0 }) fields.Add("username");
            if (allowed.Contains("password") && record.Password is { Length: > 0 }) fields.Add("password");
            if (allowed.Contains("authorization") && record.Authorization is { Length: > 0 }) fields.Add("authorization");
            if (allowed.Contains("refreshedAt") && record.RefreshedAtMs is > 0) fields.Add("refreshedAt");
            return new(provider, record.AccountRevision, fields.Count > 0, fields.Count > 0 ? "unverified" : "signedOut", fields.ToArray());
        }
        catch (BridgeException error) when (error.Code is "CORRUPT_STORAGE" or "INVALID_MESSAGE")
        { return PanAccountSnapshot.Corrupt(provider); }
    }
}
