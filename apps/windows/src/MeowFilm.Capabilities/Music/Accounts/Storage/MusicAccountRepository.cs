using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Accounts;

internal sealed record AccountSnapshot(IReadOnlyDictionary<string, JsonObject> Entries, StorageRevision Revision);

/// <summary>Plaintext typed account rows; routine reads materialize only the requested platform.</summary>
internal sealed class MusicAccountRepository(IStorageDatabase database)
{
    internal Task<AccountSnapshot> ReadAsync(string? platform, CancellationToken token) => database.ReadAsync(db =>
    {
        var entries = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var name in platform is null ? AccountRules.Platforms : [AccountRules.Platform(platform)])
            if (AccountStorageRecords.Account.Read(db, [name]) is { } value)
                entries[name] = value;
        return new AccountSnapshot(entries, db.Revision(MusicAccountOwner.DocumentNamespace));
    }, token);
    internal Task<StorageRevision> WriteAsync(string platform, JsonObject value, StorageRevision expected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var target = AccountRules.Platform(platform);
        var payload = value.DeepClone().AsObject();
        return database.WriteAsync(db =>
        {
            if (db.Revision(MusicAccountOwner.DocumentNamespace) != expected) throw new DocumentStoreException(DocumentStoreError.Conflict);
            var previous = AccountStorageRecords.Account.Read(db, [target]);
            if (!JsonNode.DeepEquals(payload, previous))
            {
                if (payload["generation"]!.GetValue<long>() != (previous?["generation"]?.GetValue<long>() ?? 0) + 1)
                    throw new DocumentStoreException(DocumentStoreError.Conflict);
                AccountStorageRecords.Account.Save(db, payload, previous, [target]);
            }
            return db.CommitRevision(MusicAccountOwner.DocumentNamespace, expected, Guid.NewGuid().ToString("D"));
        }, token);
    }
}
