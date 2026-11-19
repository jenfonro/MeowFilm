using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeowFilm.Platform.Storage;

/// <summary>An encoded projection exists only in caller memory, never as a database payload.</summary>
public sealed record RecordSnapshot(byte[]? Data, StorageRevision Revision);

/// <summary>One owner-declared fixed relation. The App database and its lane are borrowed.</summary>
public sealed class TypedRecordRepository(IStorageDatabase database, string owner, RelationalRecord record,
    Func<JsonObject, JsonObject>? toRows = null, Func<JsonObject, JsonObject>? fromRows = null) : IAsyncDisposable
{
    private readonly object?[] key = ["device"];
    private int closed;
    private JsonObject? Project(DatabaseSession db)
    {
        var value = record.Read(db, key);
        return value is null ? null : fromRows?.Invoke(value) ?? value;
    }
    private void Check() => ObjectDisposedException.ThrowIf(Volatile.Read(ref closed) != 0, this);
    public Task<RecordSnapshot> ReadAsync(CancellationToken token = default)
    {
        Check();
        return database.ReadAsync(db =>
        {
            var value = Project(db);
            return new RecordSnapshot(value is null ? null : JsonSerializer.SerializeToUtf8Bytes(value), db.Revision(owner));
        }, token);
    }
    public Task<StorageRevision> WriteAsync(ReadOnlyMemory<byte> data, StorageRevision expected, CancellationToken token = default)
    {
        Check(); token.ThrowIfCancellationRequested();
        if (data.Length > 16 * 1024 * 1024) throw new DocumentStoreException(DocumentStoreError.Limit);
        var value = JsonNode.Parse(data.Span) as JsonObject ?? throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
        if (toRows is not null) value = toRows(value);
        return database.WriteAsync(db =>
        {
            if (db.Revision(owner) != expected) throw new DocumentStoreException(DocumentStoreError.Conflict);
            var previous = record.Read(db, key);
            if (JsonNode.DeepEquals(value, previous)) return expected;
            record.Save(db, value, previous, key);
            return db.CommitRevision(owner, expected, Guid.NewGuid().ToString("D"));
        }, token, data.Length);
    }
    public Task<JsonElement> UpdateAsync(Func<JsonElement?, JsonElement> edit, CancellationToken token = default)
    {
        Check();
        return database.WriteAsync(db =>
        {
            Check();
            var previous = record.Read(db, key);
            var projection = previous is null ? null : fromRows?.Invoke(previous.DeepClone().AsObject()) ?? previous;
            var next = edit(projection is null ? null : JsonSerializer.SerializeToElement(projection));
            var value = JsonNode.Parse(next.GetRawText()) as JsonObject ??
                throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
            if (toRows is not null) value = toRows(value);
            if (!JsonNode.DeepEquals(value, previous))
            {
                record.Save(db, value, previous, key);
                db.CommitRevision(owner, db.Revision(owner), Guid.NewGuid().ToString("D"));
            }
            return next.Clone();
        }, token);
    }
    public Task DrainAsync() => database.DrainAsync();
    public ValueTask DisposeAsync() { Interlocked.Exchange(ref closed, 1); return new(database.DrainAsync()); }
}
