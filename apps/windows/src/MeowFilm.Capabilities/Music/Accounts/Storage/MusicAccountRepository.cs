using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Accounts;

/// <summary>Plaintext typed account rows; routine reads materialize only the requested platform.</summary>
internal sealed class MusicAccountRepository(IStorageDatabase database)
{
    internal Task<RecordSnapshot> ReadAsync(string? platform, CancellationToken token) => database.ReadAsync(db =>
    {
        var entries = new JsonArray();
        foreach (var name in platform is null ? AccountRules.Platforms : [AccountRules.Platform(platform)])
            if (AccountStorageRecords.Account.Read(db, [name]) is { } value)
                entries.Add(new JsonObject { ["key"] = AccountDocument.Key(name), ["value"] = value });
        var root = new JsonObject { ["version"] = 1, ["entries"] = entries };
        return new RecordSnapshot(JsonSerializer.SerializeToUtf8Bytes(root), db.Revision(MusicAccountOwner.DocumentNamespace));
    }, token);
    internal Task<StorageRevision> WriteAsync(ReadOnlyMemory<byte> data, StorageRevision expected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var root = JsonNode.Parse(data.Span) as JsonObject ?? throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
        var values = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var row in root["entries"]!.AsArray().OfType<JsonObject>())
        {
            var platform = AccountRules.Platforms.SingleOrDefault(name => AccountDocument.Key(name) == row["key"]!.GetValue<string>()) ??
                throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
            values.Add(platform, row["value"]!.DeepClone().AsObject());
        }
        return database.WriteAsync(db =>
        {
            if (db.Revision(MusicAccountOwner.DocumentNamespace) != expected) throw new DocumentStoreException(DocumentStoreError.Conflict);
            foreach (var (platform, value) in values)
            {
                var previous = AccountStorageRecords.Account.Read(db, [platform]);
                if (JsonNode.DeepEquals(value, previous)) continue;
                if (value["generation"]!.GetValue<long>() != (previous?["generation"]?.GetValue<long>() ?? 0) + 1)
                    throw new DocumentStoreException(DocumentStoreError.Conflict);
                AccountStorageRecords.Account.Save(db, value, previous, [platform]);
            }
            return db.CommitRevision(MusicAccountOwner.DocumentNamespace, expected, Guid.NewGuid().ToString("D"));
        }, token, data.Length);
    }
}
