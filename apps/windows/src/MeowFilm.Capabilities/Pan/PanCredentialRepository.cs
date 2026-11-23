using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

/// <summary>Five plaintext provider rows. Refreshes and replacements have separate durable revisions.</summary>
internal sealed class PanCredentialRepository(IStorageDatabase database)
{
    private const long Maximum = BridgeCodec.MaximumSafeInteger;
    private static readonly string[] Fields = ["cookie", "username", "password", "authorization", "refreshedAt"];

    private static JsonObject Account(Microsoft.Data.Sqlite.SqliteDataReader row)
    {
        var credentials = new JsonObject();
        for (int i = 3; i < row.FieldCount; i++)
            if (!row.IsDBNull(i)) credentials[row.GetName(i)] = row.GetName(i) == "refreshedAt" ?
                JsonValue.Create(row.GetInt64(i)) : JsonValue.Create(row.GetString(i));
        return new() { ["revision"] = row.GetInt64(1), ["rowRevision"] = row.GetInt64(2), ["credentials"] = credentials };
    }

    private static PanAccountSnapshot ReadSnapshotRow(Microsoft.Data.Sqlite.SqliteDataReader row, PanProviderDescriptor descriptor)
    {
        try
        {
            long accountRevision = row.GetInt64(1);
            if (accountRevision == 0) return new(descriptor.Provider, 0, false, "signedOut", []);
            if (accountRevision is < 0 or > Maximum) return PanAccountSnapshot.Corrupt(descriptor.Provider);
            var activeFields = new List<string>(descriptor.CredentialFields.Count);
            for (int i = 3; i < row.FieldCount; i++)
            {
                string name = row.GetName(i);
                if (!descriptor.HasField(name) || row.IsDBNull(i)) continue;
                if (name == "refreshedAt")
                {
                    long number = row.GetInt64(i);
                    if (number is < 0 or > Maximum) return PanAccountSnapshot.Corrupt(descriptor.Provider);
                    if (number > 0) activeFields.Add(name);
                }
                else
                {
                    string text = row.GetString(i);
                    if (!IsValidText(name, text)) return PanAccountSnapshot.Corrupt(descriptor.Provider);
                    if (text.Length > 0) activeFields.Add(name);
                }
            }
            return new(descriptor.Provider, accountRevision, activeFields.Count > 0,
                activeFields.Count > 0 ? "signedIn" : "signedOut", activeFields.ToArray());
        }
        catch (Exception error) when (error is InvalidCastException or FormatException or InvalidOperationException)
        {
            return PanAccountSnapshot.Corrupt(descriptor.Provider);
        }
    }

    private static RuntimeSettingsSnapshot Snapshot(DatabaseSession db)
    {
        long revision = db.Scalar<long>("SELECT revision FROM windows_pan_state WHERE singleton=1");
        var accounts = new JsonObject();
        foreach (var pair in db.Query("SELECT * FROM windows_pan_accounts WHERE account_revision>0 ORDER BY provider",
            row => (Name: row.GetString(0), Value: Account(row)))) accounts.Add(pair.Name, pair.Value);
        return new(revision == 0 ? null : JsonSerializer.SerializeToElement(new JsonObject { ["version"] = 1, ["accounts"] = accounts }), revision);
    }

    internal Task<RuntimeSettingsSnapshot> SnapshotAsync(CancellationToken token) => database.ReadAsync(Snapshot, token);

    internal Task<IReadOnlyList<PanAccountSnapshot>> ReadSnapshotsAsync(CancellationToken token) => database.ReadAsync<IReadOnlyList<PanAccountSnapshot>>(db =>
    {
        var byProvider = db.Query("SELECT * FROM windows_pan_accounts", row =>
        {
            string provider = row.GetString(0);
            return PanSettingsStore.FindProvider(provider) is { } descriptor
                ? (Provider: provider, Snapshot: ReadSnapshotRow(row, descriptor))
                : ((string Provider, PanAccountSnapshot Snapshot)?)null;
        }).Where(item => item is not null).ToDictionary(item => item!.Value.Provider, item => item!.Value.Snapshot, StringComparer.Ordinal);
        return PanSettingsStore.Providers.Select(descriptor =>
            byProvider.TryGetValue(descriptor.Provider, out var item)
                ? item
                : new PanAccountSnapshot(descriptor.Provider, 0, false, "signedOut", [])).ToArray();
    }, token);

    internal Task<PanAccountSnapshot> ReadSnapshotAsync(string provider, CancellationToken token) => database.ReadAsync(db =>
    {
        var descriptor = RequireProvider(provider);
        var rows = db.Query("SELECT * FROM windows_pan_accounts WHERE provider=$provider",
            row => ReadSnapshotRow(row, descriptor), ("$provider", provider));
        return rows.Count == 1 ? rows[0] : throw new DocumentStoreException(DocumentStoreError.Corrupt);
    }, token);

    internal Task<(long AccountRevision, long RowRevision)> ReadRevisionsAsync(string provider, CancellationToken token) => database.ReadAsync(db =>
    {
        _ = RequireProvider(provider);
        var rows = db.Query("SELECT account_revision,row_revision FROM windows_pan_accounts WHERE provider=$provider",
            row => (Account: row.GetInt64(0), Row: row.GetInt64(1)), ("$provider", provider));
        if (rows.Count != 1 || rows[0].Account is < 0 or > Maximum || rows[0].Row is < 0 or > Maximum)
            throw new DocumentStoreException(DocumentStoreError.Corrupt);
        return rows[0];
    }, token);

    internal Task<PanAccountSnapshot> ReplaceSnapshotAsync(string provider, IReadOnlyDictionary<string, object> credentials,
        long accountRevision, long rowRevision, CancellationToken token)
    {
        var descriptor = RequireProvider(provider);
        var validated = ValidateTyped(descriptor, credentials, out int estimatedBytes);
        return database.WriteAsync(db =>
        {
            Save(db, provider, validated, accountRevision, rowRevision, false);
            var activeFields = descriptor.CredentialFields.Where(field =>
                validated.TryGetValue(field, out var value) && (value is string text ? text.Length > 0 : (long)value > 0)).ToArray();
            return new PanAccountSnapshot(provider, accountRevision + 1, activeFields.Length > 0,
                activeFields.Length > 0 ? "signedIn" : "signedOut", activeFields);
        }, token, estimatedBytes);
    }

    internal Task<RuntimeSettingsSnapshot> UpdateAsync(JsonElement command, CancellationToken token)
    {
        var provider = BridgeCodec.RequiredProperty(command, "provider").GetString()!;
        var mode = BridgeCodec.RequiredProperty(command, "mode").GetString();
        var accountRevision = BridgeCodec.RequiredProperty(command, "accountRevision").GetInt64();
        var rowRevision = BridgeCodec.RequiredProperty(command, "rowRevision").GetInt64();
        var credentials = BridgeCodec.RequiredProperty(command, "credentials").Clone();
        if (mode is not ("replace" or "refresh")) throw new BridgeException("INVALID_REQUEST");
        var validated = ValidateJson(provider, credentials);
        return database.WriteAsync(db =>
        {
            Save(db, provider, validated, accountRevision, rowRevision, mode == "refresh");
            return Snapshot(db);
        }, token, credentials.GetRawText().Length * 2);
    }

    private static void Save(DatabaseSession db, string provider, IReadOnlyDictionary<string, object> credentials,
        long accountRevision, long rowRevision, bool refresh)
    {
        if (accountRevision is < 0 or > Maximum || rowRevision is < 0 or > Maximum || refresh && accountRevision == 0)
            throw new BridgeException("INVALID_REQUEST");
        if (!refresh && accountRevision == Maximum || rowRevision == Maximum)
            throw new DocumentStoreException(DocumentStoreError.Limit);
        var values = new List<(string, object?)> { ("$provider", provider), ("$account", accountRevision),
            ("$row", rowRevision), ("$next", accountRevision + (refresh ? 0 : 1)) };
        foreach (var field in Fields)
            values.Add(("$" + field, credentials.TryGetValue(field, out var value) ? value : null));
        if (db.Execute("UPDATE windows_pan_accounts SET account_revision=$next,row_revision=row_revision+1," +
            string.Join(",", Fields.Select(field => field + "=$" + field)) +
            " WHERE provider=$provider AND account_revision=$account AND row_revision=$row", values.ToArray()) != 1)
            throw new DocumentStoreException(DocumentStoreError.Conflict);
        if (db.Execute("UPDATE windows_pan_state SET revision=revision+1 WHERE singleton=1 AND revision<9007199254740991") != 1)
            throw new DocumentStoreException(DocumentStoreError.Limit);
    }

    private static PanProviderDescriptor RequireProvider(string provider) =>
        PanSettingsStore.FindProvider(provider) ?? throw new BridgeException("INVALID_REQUEST");

    private static bool IsValidText(string field, string text) =>
        !text.Contains('\0') && (field == "password" || text.IndexOfAny(['\r', '\n']) < 0);

    private static Dictionary<string, object> ValidateTyped(PanProviderDescriptor descriptor,
        IReadOnlyDictionary<string, object> credentials, out int estimatedBytes)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        int chars = 0;
        foreach (var (name, raw) in credentials)
        {
            if (!descriptor.HasField(name)) continue;
            if (name == "refreshedAt")
            {
                long number = raw switch
                {
                    long l => l,
                    int i => i,
                    _ => throw new BridgeException("INVALID_REQUEST")
                };
                if (number is < 0 or > Maximum) throw new BridgeException("INVALID_REQUEST");
                result[name] = number;
                chars += 16;
            }
            else if (raw is string text && IsValidText(name, text))
            {
                result[name] = text;
                chars += text.Length;
            }
            else throw new BridgeException("INVALID_REQUEST");
        }
        estimatedBytes = chars * 2;
        return result;
    }

    private static Dictionary<string, object> ValidateJson(string provider, JsonElement credentials)
    {
        var descriptor = RequireProvider(provider);
        if (credentials.ValueKind != JsonValueKind.Object) throw new BridgeException("INVALID_REQUEST");
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var field in credentials.EnumerateObject())
        {
            if (result.ContainsKey(field.Name) || !descriptor.HasField(field.Name))
                throw new BridgeException("INVALID_REQUEST");
            if (field.Name == "refreshedAt")
            {
                if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt64(out var number) ||
                    number is < 0 or > Maximum) throw new BridgeException("INVALID_REQUEST");
                result[field.Name] = number;
            }
            else if (field.Value.ValueKind != JsonValueKind.String || field.Value.GetString() is not { } text ||
                !IsValidText(field.Name, text))
                throw new BridgeException("INVALID_REQUEST");
            else result[field.Name] = text;
        }
        return result;
    }
}
