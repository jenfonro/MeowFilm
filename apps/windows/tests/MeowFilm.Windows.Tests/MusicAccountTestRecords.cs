using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

/// <summary>Current account relations, exposed only as in-memory test diagnostics.</summary>
internal static class MusicAccountTestRecords
{
    internal static Task<RecordSnapshot> ReadAccountsAsync(this IStorageDatabase database, CancellationToken token = default) =>
        new MusicAccountRepository(database).ReadAsync(null, token);
    internal static Task<StorageRevision> WriteAccountsAsync(this IStorageDatabase database, ReadOnlyMemory<byte> value,
        StorageRevision expected, CancellationToken token = default) =>
        new MusicAccountRepository(database).WriteAsync(value, expected, token);
    internal static JsonObject Accounts(this IStorageDatabase database) =>
        JsonNode.Parse(database.ReadAccountsAsync().GetAwaiter().GetResult().Data!)!.AsObject();
    internal static void SeedAccounts(this IStorageDatabase database, JsonObject root) =>
        database.WriteAsync(db =>
        {
            foreach (var row in root["entries"]!.AsArray().OfType<JsonObject>())
            {
                var platform = AccountRules.Platforms.Single(name => AccountDocument.Key(name) == row["key"]!.GetValue<string>());
                AccountStorageRecords.Account.Save(db, row["value"]!.AsObject(),
                    AccountStorageRecords.Account.Read(db, [platform]), [platform]);
            }
            return db.CommitRevision(MusicAccountOwner.DocumentNamespace, db.Revision(MusicAccountOwner.DocumentNamespace),
                Guid.NewGuid().ToString("D"));
        }).GetAwaiter().GetResult();
    internal static Task<JsonObject> ReadDetailsAsync(this IStorageDatabase database) =>
        database.ReadAsync(db => new JsonObject(AccountDetailsRecords.Details.ReadMany(db, []).Select(row =>
            new KeyValuePair<string, JsonNode?>(row["profile"]!["platform"]!.GetValue<string>(), row))));
    internal static Task SeedDetailsAsync(this IStorageDatabase database, JsonObject root) =>
        database.WriteAsync(db =>
        {
            foreach (var platform in AccountRules.Platforms.Where(platform => root[platform] is null))
                AccountDetailsRecords.Details.Save(db, null, AccountDetailsRecords.Details.Read(db, [platform]), [platform]);
            foreach (var (platform, row) in root)
                AccountDetailsRecords.Details.Save(db, row!.AsObject(),
                    AccountDetailsRecords.Details.Read(db, [platform]), [platform]);
            return true;
        });
}
