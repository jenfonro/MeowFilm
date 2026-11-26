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

    private static PanCredentialsInput ParseCredentials(string provider, IReadOnlyDictionary<string, object> credentials, string code) =>
        ParseFields(provider, credentials.Where(kv => Fields(provider, code).Contains(kv.Key)), code);

    internal static PanCredentialsInput ParseCredentials(string provider, JsonElement credentials, string code)
    {
        var allowed = Fields(provider, code);
        if (credentials.ValueKind != JsonValueKind.Object) throw new BridgeException(code);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pairs = new List<KeyValuePair<string, object>>();
        foreach (var field in credentials.EnumerateObject())
        {
            if (!seen.Add(field.Name) || !allowed.Contains(field.Name)) throw new BridgeException(code);
            pairs.Add(new(field.Name, field.Value));
        }
        return ParseFields(provider, pairs, code);
    }

    private static PanCredentialsInput ParseFields(string provider, IEnumerable<KeyValuePair<string, object>> entries, string code)
    {
        _ = Fields(provider, code);
        string? cookie = null, username = null, password = null, authorization = null;
        long? refreshedAt = null;
        foreach (var (name, value) in entries)
        {
            if (name == "refreshedAt")
            {
                long number = value switch
                {
                    int i => i,
                    long l => l,
                    JsonElement { ValueKind: JsonValueKind.Number } el when el.TryGetInt64(out long n) => n,
                    _ => throw new BridgeException(code)
                };
                if (number is < 0 or > BridgeCodec.MaximumSafeInteger) throw new BridgeException(code);
                refreshedAt = number;
                continue;
            }
            string? text = value switch
            {
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
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
        return new PanCredentialsInput(cookie, username, password, authorization, refreshedAt);
    }

    private static Exception MapStorageError(StorageException error) => error.Error switch
    {
        StorageError.Conflict => new PanAccountException("STALE_ACCOUNT"),
        StorageError.Corrupt or StorageError.UnsupportedVersion => new BridgeException("CORRUPT_STORAGE"),
        StorageError.Closed => new BridgeException("CLOSED"),
        _ => new BridgeException("STORAGE_ERROR")
    };

    private static HashSet<string> Fields(string provider, string code) =>
        (FindProvider(provider) ?? throw new BridgeException(code)).CredentialFields.ToHashSet(StringComparer.Ordinal);

    private static PanAccountSnapshot ToSnapshot(string provider, PanAccountRecord? record)
    {
        var allowed = Fields(provider, "INVALID_REQUEST");
        if (record is null || record.AccountRevision <= 0)
            return new(provider, 0, false, "signedOut", []);
        var fields = new List<string>();
        if (allowed.Contains("cookie") && record.Cookie is { Length: > 0 }) fields.Add("cookie");
        if (allowed.Contains("username") && record.Username is { Length: > 0 }) fields.Add("username");
        if (allowed.Contains("password") && record.Password is { Length: > 0 }) fields.Add("password");
        if (allowed.Contains("authorization") && record.Authorization is { Length: > 0 }) fields.Add("authorization");
        if (allowed.Contains("refreshedAt") && record.RefreshedAtMs is > 0) fields.Add("refreshedAt");
        return new(provider, record.AccountRevision, fields.Count > 0, fields.Count > 0 ? "unverified" : "signedOut", fields.ToArray());
    }
}
