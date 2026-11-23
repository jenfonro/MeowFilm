using System.IO;
using System.Text.Json;
using MeowFilm.Capabilities.Library;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

/// <summary>Native shelf, history, appearance and user rules. Never stores source definitions or cookies.</summary>
public sealed class NovelLibrary(IStorageDatabase database)
{
    public const string StorageOwner = "novel.library.v1";
    private static readonly HashSet<string> StandardBookFields = new(StringComparer.Ordinal)
    {
        "installationId", "revision", "bookUrl", "name", "author", "coverUrl", "intro", "tocUrl", "latestChapterTitle"
    };
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
        var loaded = await database.ReadSnapshotAsync(db =>
        {
            var (loadedRecords, loadedAppearance, loadedRules) = NovelLibraryStore.LoadAll(db);
            var loadedEntries = loadedRecords.Select(FromRecord).ToArray();
            foreach (var entry in loadedEntries) Validate(entry, stored: true);
            if (loadedEntries.Select(e => e.BookId).Distinct(StringComparer.Ordinal).Count() != loadedEntries.Length)
                throw Corrupt();
            ValidateRules(loadedRules, stored: true);
            return new State(loadedEntries, loadedAppearance, loadedRules);
        }, token);
        entries = loaded.Entries; appearance = loaded.Appearance; rules = loaded.Rules;
        initialized = true;
    }

    private static StorageException Corrupt() => new(StorageError.Corrupt);
    private static void Validate(NovelEntry entry, bool stored = false)
    {
        bool valid = !string.IsNullOrEmpty(entry.BookId) && entry.IdentityKey is not null && entry.Book is { } book &&
            !string.IsNullOrEmpty(book.InstallationId) && !string.IsNullOrEmpty(book.BookUrl) && book.Revision > 0 &&
            book.Name is not null && book.Author is not null && book.CoverUrl is not null && book.Intro is not null &&
            book.TocUrl is not null && book.LatestChapterTitle is not null && entry.ContentSignature is not null && entry.ChapterCount >= 0 &&
            (entry.Anchor is null || entry.Anchor.ChapterUrl is not null && entry.Anchor.ChapterTitle is not null &&
                entry.Anchor.ChapterIndex >= 0 && entry.Anchor.BlockIndex >= 0 && entry.Anchor.Utf16Offset >= 0 &&
                double.IsFinite(entry.Anchor.Ratio) && entry.Anchor.Ratio is >= 0 and <= 1);
        if (!valid) throw new StorageException(stored ? StorageError.Corrupt : StorageError.InvalidRequest);
    }
    private static void ValidateRules(IReadOnlyList<NovelRule> rules, bool stored = false)
    {
        if (rules.Any(r => string.IsNullOrEmpty(r.Id) || r.Name is null || r.Group is null || r.Pattern is null ||
            r.Replacement is null || r.Scope is null || r.ExcludeScope is null) ||
            rules.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != rules.Count)
            throw new StorageException(stored ? StorageError.Corrupt : StorageError.InvalidRequest);
    }

    private static NovelEntry FromRecord(NovelEntryRecord record)
    {
        var dataElement = record.Book.Extras is { Kind: StorageValueKind.Object, ObjectFields.Count: > 0 } extras
            ? StorageValueJson.ToJsonElement(extras)
            : default;
        var book = new NovelBook(
            record.Book.InstallationId,
            record.Book.Revision,
            record.Book.BookUrl,
            record.Book.Name,
            record.Book.Author,
            record.Book.CoverUrl,
            record.Book.Intro,
            record.Book.TocUrl,
            record.Book.LatestChapterTitle,
            dataElement);
        return new NovelEntry(
            record.BookId,
            record.IdentityKey,
            book,
            record.OnShelf,
            record.AddedAtMs is { } added ? DateTimeOffset.FromUnixTimeMilliseconds(added) : null,
            record.PinnedAtMs is { } pinned ? DateTimeOffset.FromUnixTimeMilliseconds(pinned) : null,
            record.ReadAtMs is { } readAt ? DateTimeOffset.FromUnixTimeMilliseconds(readAt) : null,
            record.Anchor,
            record.ChapterCount,
            record.ContentSignature);
    }

    private static NovelEntryRecord ToRecord(NovelEntry entry)
    {
        StorageValue? extras = null;
        if (entry.Book.Data.ValueKind == JsonValueKind.Object)
        {
            List<StorageValueField>? fields = null;
            foreach (var prop in entry.Book.Data.EnumerateObject())
            {
                if (StandardBookFields.Contains(prop.Name)) continue;
                fields ??= [];
                fields.Add(new StorageValueField(prop.Name, StorageValueJson.FromJsonElement(prop.Value)));
            }
            if (fields is { Count: > 0 })
                extras = StorageValue.FromObject(fields);
        }
        var book = new NovelBookRecord(
            entry.Book.InstallationId,
            entry.Book.Revision,
            entry.Book.BookUrl,
            entry.Book.Name,
            entry.Book.Author,
            entry.Book.CoverUrl,
            entry.Book.Intro,
            entry.Book.TocUrl,
            entry.Book.LatestChapterTitle,
            extras);
        return new NovelEntryRecord(
            entry.BookId,
            entry.IdentityKey,
            book,
            entry.OnShelf,
            entry.AddedAt?.ToUnixTimeMilliseconds(),
            entry.PinnedAt?.ToUnixTimeMilliseconds(),
            entry.ReadAt?.ToUnixTimeMilliseconds(),
            entry.Anchor,
            entry.ChapterCount,
            entry.ContentSignature);
    }

    private Task EditEntry(string id, Func<NovelEntry?, NovelEntry?> edit, CancellationToken token, bool consumeCurrent = true) => work.Run(async ct =>
    {
        await serial.WaitAsync(ct);
        try
        {
            var result = await database.WriteAsync(db =>
            {
                NovelEntryRecord? currentRecord = consumeCurrent ? NovelLibraryStore.FindEntry(db, id) : null;
                NovelEntry? current = currentRecord is null ? null : FromRecord(currentRecord);
                if (current is not null) Validate(current, stored: true);
                var next = edit(current);
                if (next is not null)
                {
                    Validate(next);
                    if (next.BookId != id) throw new StorageException(StorageError.InvalidRequest);
                }
                var nextRecord = next is null ? null : ToRecord(next);
                if (consumeCurrent && NovelLibraryStore.EntryEquals(nextRecord, currentRecord))
                    return (Changed: false, Entry: next);
                if (nextRecord is null)
                {
                    if (!consumeCurrent && NovelLibraryStore.FindEntry(db, id) is null)
                        return (Changed: false, Entry: null);
                    NovelLibraryStore.DeleteEntry(db, id);
                }
                else
                {
                    NovelLibraryStore.UpsertEntry(db, nextRecord);
                }
                db.CommitRevision(StorageOwner, db.Revision(StorageOwner));
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
                var previous = NovelLibraryStore.ReadAppearance(db);
                if (Equals(previous, value)) return false;
                NovelLibraryStore.SaveAppearance(db, value);
                db.CommitRevision(StorageOwner, db.Revision(StorageOwner));
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
                var previous = NovelLibraryStore.ReadRules(db);
                if (NovelLibraryStore.RulesEqual(previous, values)) return false;
                NovelLibraryStore.ReplaceRules(db, previous, values);
                db.CommitRevision(StorageOwner, db.Revision(StorageOwner));
                return true;
            }, ct);
            this.rules = values;
            if (changed) Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        finally { serial.Release(); }
    }, token);
    public Task PrepareShutdownAsync() => work.PauseAsync();
}
