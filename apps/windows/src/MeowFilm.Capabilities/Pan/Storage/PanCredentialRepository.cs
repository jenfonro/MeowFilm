using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

internal sealed record PanAccountRecord(
    string Provider,
    long AccountRevision,
    long RowRevision,
    string? Cookie,
    string? Username,
    string? Password,
    string? Authorization,
    long? RefreshedAtMs)
{
    internal JsonObject ToWireJson()
    {
        var credentials = new JsonObject();
        if (Cookie is not null) credentials["cookie"] = Cookie;
        if (Username is not null) credentials["username"] = Username;
        if (Password is not null) credentials["password"] = Password;
        if (Authorization is not null) credentials["authorization"] = Authorization;
        if (RefreshedAtMs is { } refreshed) credentials["refreshedAt"] = refreshed;
        return new JsonObject
        {
            ["revision"] = AccountRevision,
            ["rowRevision"] = RowRevision,
            ["credentials"] = credentials
        };
    }
}

/// <summary>Five plaintext provider rows. Refreshes and replacements have separate durable revisions.</summary>
internal sealed class PanCredentialRepository(IStorageDatabase database)
{
    internal const string Owner = "pan.accounts";
    private const long Maximum = BridgeCodec.MaximumSafeInteger;

    private static PanAccountRecord ReadRow(Microsoft.Data.Sqlite.SqliteDataReader row) => new(
        row.GetString(0),
        row.GetInt64(1),
        row.GetInt64(2),
        row.GetNullableString(3),
        row.GetNullableString(4),
        row.GetNullableString(5),
        row.GetNullableString(6),
        row.GetNullableInt64(7));

    private static RuntimeSettingsSnapshot Snapshot(DatabaseSession db)
    {
        long revision = db.Revision(Owner).Revision;
        var rows = db.Query(
            "SELECT provider,account_revision,row_revision,cookie,username,password,authorization,refreshed_at_ms FROM pan_accounts WHERE account_revision>0 ORDER BY provider",
            ReadRow);
        if (revision == 0) return new(null, 0);
        var accounts = new JsonObject();
        foreach (var account in rows) accounts.Add(account.Provider, account.ToWireJson());
        return new(JsonSerializer.SerializeToElement(new JsonObject { ["version"] = 1, ["accounts"] = accounts }), revision);
    }

    internal Task<RuntimeSettingsSnapshot> SnapshotAsync(CancellationToken token) => database.ReadSnapshotAsync(Snapshot, token);

    internal Task<JsonElement> ReadAsync(string provider, CancellationToken token) => database.ReadAsync(db =>
    {
        ValidateProvider(provider);
        var account = db.Query(
            "SELECT provider,account_revision,row_revision,cookie,username,password,authorization,refreshed_at_ms FROM pan_accounts WHERE provider=$provider",
            ReadRow, ("$provider", provider)).Single();
        var accounts = new JsonObject();
        if (account.AccountRevision > 0) accounts.Add(provider, account.ToWireJson());
        return JsonSerializer.SerializeToElement(new JsonObject { ["version"] = 1, ["accounts"] = accounts });
    }, token);

    internal Task<JsonElement> ReplaceAsync(string provider, JsonElement credentials,
        long accountRevision, long rowRevision, CancellationToken token) => database.WriteAsync(db =>
    {
        var parsed = ParseCredentials(provider, credentials);
        Save(db, provider, parsed, accountRevision, rowRevision, false);
        return Snapshot(db).Document!.Value;
    }, token, credentials.GetRawText().Length * 2);

    internal Task<RuntimeSettingsSnapshot> UpdateAsync(JsonElement command, CancellationToken token)
    {
        var provider = BridgeCodec.RequiredProperty(command, "provider").GetString()!;
        var mode = BridgeCodec.RequiredProperty(command, "mode").GetString();
        var accountRevision = BridgeCodec.RequiredProperty(command, "accountRevision").GetInt64();
        var rowRevision = BridgeCodec.RequiredProperty(command, "rowRevision").GetInt64();
        var credentials = BridgeCodec.RequiredProperty(command, "credentials").Clone();
        if (mode is not ("replace" or "refresh")) throw new BridgeException("INVALID_REQUEST");
        var parsed = ParseCredentials(provider, credentials);
        return database.WriteAsync(db =>
        {
            Save(db, provider, parsed, accountRevision, rowRevision, mode == "refresh");
            return Snapshot(db);
        }, token, credentials.GetRawText().Length * 2);
    }

    private readonly record struct ParsedCredentials(
        string? Cookie, string? Username, string? Password, string? Authorization, long? RefreshedAtMs);

    private static void Save(DatabaseSession db, string provider, ParsedCredentials credentials,
        long accountRevision, long rowRevision, bool refresh)
    {
        if (accountRevision is < 0 or > Maximum || rowRevision is < 0 or > Maximum || refresh && accountRevision == 0)
            throw new BridgeException("INVALID_REQUEST");
        if (!refresh && accountRevision == Maximum || rowRevision == Maximum)
            throw new DocumentStoreException(DocumentStoreError.Limit);
        if (db.Execute("""
            UPDATE pan_accounts
            SET account_revision=$next, row_revision=row_revision+1,
                cookie=$cookie, username=$username, password=$password,
                authorization=$authorization, refreshed_at_ms=$refreshedAt
            WHERE provider=$provider AND account_revision=$account AND row_revision=$row
            """,
            ("$provider", provider),
            ("$account", accountRevision),
            ("$row", rowRevision),
            ("$next", accountRevision + (refresh ? 0 : 1)),
            ("$cookie", credentials.Cookie),
            ("$username", credentials.Username),
            ("$password", credentials.Password),
            ("$authorization", credentials.Authorization),
            ("$refreshedAt", credentials.RefreshedAtMs)) != 1)
            throw new DocumentStoreException(DocumentStoreError.Conflict);
        var current = db.Revision(Owner);
        if (current.Revision >= Maximum) throw new DocumentStoreException(DocumentStoreError.Limit);
        db.CommitRevision(Owner, current, Guid.NewGuid().ToString("D"));
    }

    private static void ValidateProvider(string provider)
    {
        if (provider is not ("baidu" or "quark" or "uc" or "tianyi" or "yidong")) throw new BridgeException("INVALID_REQUEST");
    }

    private static ParsedCredentials ParseCredentials(string provider, JsonElement credentials)
    {
        ValidateProvider(provider);
        string[] allowed = provider switch
        {
            "tianyi" => ["cookie", "username", "password"],
            "yidong" => ["authorization", "refreshedAt"],
            _ => ["cookie"]
        };
        if (credentials.ValueKind != JsonValueKind.Object) throw new BridgeException("INVALID_REQUEST");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cookie = null, username = null, password = null, authorization = null;
        long? refreshedAt = null;
        foreach (var field in credentials.EnumerateObject())
        {
            if (!seen.Add(field.Name) || !allowed.Contains(field.Name)) throw new BridgeException("INVALID_REQUEST");
            if (field.Name == "refreshedAt")
            {
                if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt64(out var number) || number is < 0 or > Maximum)
                    throw new BridgeException("INVALID_REQUEST");
                refreshedAt = number;
            }
            else
            {
                if (field.Value.ValueKind != JsonValueKind.String || field.Value.GetString() is not { } text ||
                    text.Contains('\0') || field.Name != "password" && text.IndexOfAny(['\r', '\n']) >= 0)
                    throw new BridgeException("INVALID_REQUEST");
                switch (field.Name)
                {
                    case "cookie": cookie = text; break;
                    case "username": username = text; break;
                    case "password": password = text; break;
                    case "authorization": authorization = text; break;
                }
            }
        }
        return new ParsedCredentials(cookie, username, password, authorization, refreshedAt);
    }
}
