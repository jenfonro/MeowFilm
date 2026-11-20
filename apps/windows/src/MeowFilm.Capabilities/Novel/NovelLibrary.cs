using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

/// <summary>Native shelf, history, appearance and user rules. Never stores source definitions or cookies.</summary>
public sealed class NovelLibrary(IStorageDatabase database)
{
    public const string DocumentNamespace = "novel.library.v1";
    private sealed record State(NovelEntry[] Entries, NovelAppearance Appearance, NovelRule[] Rules);
    private NovelEntry[]? entries;
    private NovelAppearance? appearance;
    private NovelRule[]? rules;
    private bool initialized;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly NovelWork work = new();
    public IReadOnlyList<NovelEntry> Entries => entries ?? [];
    public NovelAppearance Appearance => appearance ?? new();
    public IReadOnlyList<NovelRule> Rules => rules ?? [];
    internal JsonElement RuleInputs => NovelJson.Value(Rules);
    public event EventHandler? Changed;

    public Task InitializeAsync(CancellationToken token = default) => work.Run(async ct =>
    {
        await serial.WaitAsync(ct);
        try { await LoadAsync(ct); return true; }
        finally { serial.Release(); }
    }, token);

    private async Task LoadAsync(CancellationToken token)
    {
        if (initialized) return;
        var loaded = await database.ReadAsync(db =>
        {
            var value = NovelStorageRecords.Library.Read(db, ["device"]);
            if (value is null)
            {
                if (db.Revision(DocumentNamespace) != StorageRevision.Empty) throw Corrupt();
                return new State([], new(), []);
            }
            try
            {
                if (value["version"]?.GetValue<long>() != 1) throw Corrupt();
                var state = value.Deserialize<State>(NovelJson.Options) ?? throw Corrupt();
                if (state.Entries is null || state.Appearance is null || state.Rules is null) throw Corrupt();
                foreach (var entry in state.Entries) Validate(entry, stored: true);
                if (state.Entries.Select(e => e.BookId).Distinct(StringComparer.Ordinal).Count() != state.Entries.Length)
                    throw Corrupt();
                ValidateRules(state.Rules, stored: true);
                return state;
            }
            catch (JsonException) { throw Corrupt(); }
            catch (InvalidOperationException) { throw Corrupt(); }
        }, token);
        entries = loaded.Entries; appearance = loaded.Appearance; rules = loaded.Rules;
        initialized = true;
    }

    private static void EnsureRoot(DatabaseSession db, bool create = true)
    {
        var rows = db.Query("SELECT version,shape_mask,null_mask FROM windows_novel_library WHERE scope_key='device'",
            row => (Version: row.IsDBNull(0) ? 0 : row.GetInt64(0), Shape: row.GetInt64(1), Nulls: row.GetInt64(2)));
        if (rows.Count != 0)
        {
            if (rows[0] != (1L, 15L, 0L)) throw Corrupt();
            return;
        }
        if (db.Revision(DocumentNamespace) != StorageRevision.Empty) throw Corrupt();
        if (!create) return;
        NovelStorageRecords.Library.Save(db, JsonSerializer.SerializeToNode(new
        { version = 1, entries = Array.Empty<NovelEntry>(), appearance = new NovelAppearance(), rules = Array.Empty<NovelRule>() },
            NovelJson.Options)!.AsObject(), null, ["device"]);
    }
    private static JsonObject Row<T>(T value) => JsonSerializer.SerializeToNode(value, NovelJson.Options)!.AsObject();
    private static DocumentStoreException Corrupt() => new(DocumentStoreError.Corrupt);
    private static void Validate(NovelEntry entry, bool stored = false)
    {
        bool valid = !string.IsNullOrEmpty(entry.BookId) && entry.IdentityKey is not null && entry.Book is { } book &&
            !string.IsNullOrEmpty(book.InstallationId) && !string.IsNullOrEmpty(book.BookUrl) && book.Revision > 0 &&
            book.Name is not null && book.Author is not null && book.CoverUrl is not null && book.Intro is not null &&
            book.TocUrl is not null && book.LatestChapterTitle is not null && entry.ContentSignature is not null && entry.ChapterCount >= 0 &&
            (entry.Anchor is null || entry.Anchor.ChapterUrl is not null && entry.Anchor.ChapterTitle is not null &&
                entry.Anchor.ChapterIndex >= 0 && entry.Anchor.BlockIndex >= 0 && entry.Anchor.Utf16Offset >= 0 &&
                double.IsFinite(entry.Anchor.Ratio) && entry.Anchor.Ratio is >= 0 and <= 1);
        if (!valid) throw new DocumentStoreException(stored ? DocumentStoreError.Corrupt : DocumentStoreError.InvalidRequest);
    }
    private static void ValidateRules(IReadOnlyList<NovelRule> rules, bool stored = false)
    {
        if (rules.Any(r => string.IsNullOrEmpty(r.Id) || r.Name is null || r.Group is null || r.Pattern is null ||
            r.Replacement is null || r.Scope is null || r.ExcludeScope is null) ||
            rules.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != rules.Count)
            throw new DocumentStoreException(stored ? DocumentStoreError.Corrupt : DocumentStoreError.InvalidRequest);
    }

    private Task EditEntry(string id, Func<NovelEntry?, NovelEntry?> edit, CancellationToken token, bool consumeCurrent = true) => work.Run(async ct =>
    {
        await serial.WaitAsync(ct);
        try
        {
            var result = await database.WriteAsync(db =>
            {
                EnsureRoot(db, create: false);
                var key = new object?[] { "device", id };
                var previous = NovelStorageRecords.Entry.Read(db, key);
                NovelEntry? current = null;
                if (consumeCurrent)
                {
                    try { current = previous?.Deserialize<NovelEntry>(NovelJson.Options); }
                    catch (JsonException) { throw Corrupt(); }
                    if (current is not null) Validate(current, stored: true);
                }
                var next = edit(current);
                if (next is not null)
                {
                    Validate(next);
                    if (next.BookId != id) throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
                }
                var row = next is null ? null : Row(next);
                if (JsonNode.DeepEquals(row, previous)) return (Changed: false, Entry: next);
                EnsureRoot(db);
                int ordinal = checked((int)(previous is null
                    ? db.Scalar<long>("SELECT coalesce(max(ordinal),-1)+1 FROM windows_novel_library_entries WHERE scope_key='device'")
                    : db.Scalar<long>("SELECT ordinal FROM windows_novel_library_entries WHERE scope_key='device' AND entries_key=$id", ("$id", id))));
                NovelStorageRecords.Entry.Save(db, row, previous, key, ordinal, ordinal);
                db.CommitRevision(DocumentNamespace, db.Revision(DocumentNamespace), Guid.NewGuid().ToString("D"));
                return (Changed: true, Entry: next);
            }, ct);
            if (result.Changed)
            {
                if (entries is not null)
                {
                    var remaining = entries.Where(e => e.BookId != id);
                    entries = result.Entry is null ? remaining.ToArray() : remaining.Append(result.Entry).ToArray();
                }
                Changed?.Invoke(this, EventArgs.Empty);
            }
            return true;
        }
        finally { serial.Release(); }
    }, token);

    public NovelEntry? Find(NovelBook book, string key)
    {
        var matches = Entries.Where(e => e.Book.SourceIdentity == book.SourceIdentity || key.Length > 0 && e.IdentityKey == key).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("AMBIGUOUS_NOVEL_IDENTITY");
        return matches.SingleOrDefault();
    }
    public Task SaveEntryAsync(NovelEntry entry, CancellationToken token = default) => EditEntry(entry.BookId, _ => entry, token, consumeCurrent: false);
    public Task RecordReadingAsync(NovelEntry entry, CancellationToken token = default) => EditEntry(entry.BookId, current =>
        current is null ? entry : entry with { OnShelf = current.OnShelf, AddedAt = current.AddedAt, PinnedAt = current.PinnedAt }, token);
    public Task SaveMetadataAsync(NovelEntry entry, CancellationToken token = default) => EditEntry(entry.BookId, current =>
        current is null ? null : current with { Book = entry.Book, IdentityKey = entry.IdentityKey, ChapterCount = entry.ChapterCount }, token);
    public Task SetShelfAsync(string id, bool value, CancellationToken token = default) => EditEntry(id, current =>
    {
        if (current is null) return null;
        var next = current with { OnShelf = value, AddedAt = value ? DateTimeOffset.UtcNow : null, PinnedAt = value ? current.PinnedAt : null };
        return next.OnShelf || next.ReadAt is not null ? next : null;
    }, token);
    public Task ToggleShelfAsync(NovelEntry entry, CancellationToken token = default) => EditEntry(entry.BookId, current =>
    {
        if (current is null) return entry with { OnShelf = true, AddedAt = DateTimeOffset.UtcNow, PinnedAt = null, ReadAt = null, Anchor = null };
        var next = current with { OnShelf = !current.OnShelf, AddedAt = current.OnShelf ? null : DateTimeOffset.UtcNow,
            PinnedAt = current.OnShelf ? null : current.PinnedAt };
        return next.OnShelf || next.ReadAt is not null ? next : null;
    }, token);
    public Task DeleteHistoryAsync(string id, CancellationToken token = default) => EditEntry(id, current =>
        current?.OnShelf == true ? current with { ReadAt = null, Anchor = null } : null, token);
    public Task PinAsync(string id, CancellationToken token = default) => EditEntry(id, current =>
        current is null ? null : current with { PinnedAt = current.PinnedAt is null ? DateTimeOffset.UtcNow : null }, token);

    public Task SaveAppearanceAsync(NovelAppearance value, CancellationToken token = default) => work.Run(async ct =>
    {
        await serial.WaitAsync(ct);
        try
        {
            bool changed = await database.WriteAsync(db =>
            {
                EnsureRoot(db);
                var previous = NovelStorageRecords.Appearance.Read(db, ["device"]); var next = Row(value);
                if (JsonNode.DeepEquals(next, previous)) return false;
                NovelStorageRecords.Appearance.Save(db, next, previous, ["device"]);
                db.CommitRevision(DocumentNamespace, db.Revision(DocumentNamespace), Guid.NewGuid().ToString("D"));
                return true;
            }, ct);
            appearance = value;
            if (changed) Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        finally { serial.Release(); }
    }, token);

    public Task SaveRulesAsync(IReadOnlyList<NovelRule> rules, CancellationToken token = default) => work.Run(async ct =>
    {
        ValidateRules(rules); var values = rules.ToArray();
        await serial.WaitAsync(ct);
        try
        {
            bool changed = await database.WriteAsync(db =>
            {
                EnsureRoot(db);
                var previous = NovelStorageRecords.Rule.ReadMany(db, ["device"]);
                var identities = previous.Select(row => row["id"]?.GetValue<string>()).ToArray();
                if (identities.Any(string.IsNullOrEmpty) || identities.Distinct(StringComparer.Ordinal).Count() != identities.Length)
                    throw Corrupt();
                var before = previous.Select((row, ordinal) => (Row: row, Ordinal: ordinal))
                    .ToDictionary(r => r.Row["id"]!.GetValue<string>(), StringComparer.Ordinal);
                var ids = values.Select(r => r.Id).ToHashSet(StringComparer.Ordinal); bool any = false;
                foreach (var old in before.Where(r => !ids.Contains(r.Key)))
                { NovelStorageRecords.Rule.Save(db, null, old.Value.Row, ["device", old.Key]); any = true; }
                for (int ordinal = 0; ordinal < values.Length; ordinal++)
                {
                    var rule = values[ordinal]; var next = Row(rule);
                    before.TryGetValue(rule.Id, out var old);
                    if (old.Row is not null && old.Ordinal == ordinal && JsonNode.DeepEquals(next, old.Row)) continue;
                    NovelStorageRecords.Rule.Save(db, next, old.Row, ["device", rule.Id], ordinal, old.Ordinal); any = true;
                }
                if (any) db.CommitRevision(DocumentNamespace, db.Revision(DocumentNamespace), Guid.NewGuid().ToString("D"));
                return any;
            }, ct);
            this.rules = values;
            if (changed) Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        finally { serial.Release(); }
    }, token);
    public Task PrepareShutdownAsync() => work.PauseAsync();
    public void Resume() => work.Resume();
}
