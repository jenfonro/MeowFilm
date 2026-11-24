using System.Text.Json;
using MeowFilm.Capabilities.Library;
using MeowFilm.Core.Debug;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

internal sealed class NovelLibraryChangeEventArgs(bool entries = false, bool appearance = false, bool rules = false) : EventArgs
{
    internal bool Entries => entries;
    internal bool Appearance => appearance;
    internal bool Rules => rules;
}

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
    private NovelAppearance? readingAppearance;
    private NovelRule[]? rules;
    private long rulesRevision;
    private bool initialized;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly NovelWork work = new();
    public IReadOnlyList<NovelEntry> Entries => entries ?? [];
    public NovelAppearance Appearance => appearance ?? new();
    internal NovelAppearance ReadingAppearance => readingAppearance ??= Appearance.ForReading();
    public IReadOnlyList<NovelRule> Rules => rules ?? [];
    internal long RulesRevision => rulesRevision;
    public event EventHandler? Changed;
    internal IEnumerable<NovelEntry> OrderedShelfEntries() => Entries
        .Where(e => e.OnShelf)
        .OrderByDescending(e => e.PinnedAt is not null)
        .ThenByDescending(e => e.PinnedAt)
        .ThenByDescending(e => e.ReadAt ?? e.AddedAt)
        .ThenBy(e => e.Book.Name, StringComparer.CurrentCulture);
    internal IEnumerable<NovelEntry> OrderedHistoryEntries() => Entries
        .Where(e => e.ReadAt is not null)
        .OrderByDescending(e => e.ReadAt);

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
            ValidateAppearance(loadedAppearance, stored: true);
            ValidateRules(loadedRules, stored: true);
            return new State(loadedEntries, loadedAppearance, loadedRules);
        }, token);
        entries = loaded.Entries; appearance = loaded.Appearance; readingAppearance = null; rules = loaded.Rules; rulesRevision++;
        initialized = true;
    }

    private static StorageException Corrupt() => new(StorageError.Corrupt);
    private static void Validate(NovelEntry entry, bool stored = false)
    {
        bool validAnchor = (entry.Anchor is null) == (entry.ReadAt is null) &&
            (entry.Anchor is null ||
             (!string.IsNullOrEmpty(entry.Anchor.ChapterUrl) && entry.Anchor.ChapterTitle is not null &&
              entry.Anchor.ChapterIndex >= 0 && entry.Anchor.BlockIndex >= 0 && entry.Anchor.Utf16Offset >= 0 &&
              double.IsFinite(entry.Anchor.Ratio) && entry.Anchor.Ratio is >= 0 and <= 1 &&
              entry.ReadAt!.Value.ToUnixTimeMilliseconds() > 0));
        bool valid = !string.IsNullOrEmpty(entry.BookId) && entry.IdentityKey is not null && entry.Book is { } book &&
            !string.IsNullOrEmpty(book.InstallationId) && !string.IsNullOrEmpty(book.BookUrl) && book.Revision > 0 &&
            book.Name is not null && book.Author is not null && book.CoverUrl is not null && book.Intro is not null &&
            book.TocUrl is not null && book.LatestChapterTitle is not null && entry.ContentSignature is not null && entry.ChapterCount >= 0 &&
            validAnchor;
        if (!valid) throw new StorageException(stored ? StorageError.Corrupt : StorageError.InvalidRequest);
    }
    private static void ValidateAppearance(NovelAppearance value, bool stored = false)
    {
        bool valid = value is not null &&
            value.Mode is "cover" or "slide" or "none" or "scroll" &&
            NovelAppearance.ValidSize(value.FontSize) &&
            NovelAppearance.ValidFamily(value.FontFamily) &&
            NovelAppearance.ValidLineHeight(value.FontSize, value.LineHeight) &&
            double.IsFinite(value.ParagraphSpacing) && value.ParagraphSpacing >= 0 &&
            double.IsFinite(value.Margin) && value.Margin >= 0 && double.IsFinite(value.Margin * 2) &&
            double.IsFinite(value.ContentWidth) && value.ContentWidth > 0 &&
            value.Conversion is "none" or "simplified" or "traditional" &&
            value.Theme >= 0 && value.Theme < NovelAppearance.Themes.Length &&
            NovelAppearance.TryParseColor(value.Foreground, out _, out _, out _, out _) &&
            NovelAppearance.TryParseColor(value.Background, out _, out _, out _, out _) &&
            value.BackgroundImage is not null &&
            double.IsFinite(value.AutoSpeed) && value.AutoSpeed is >= 1 and <= 10;
        if (!valid) throw new StorageException(stored ? StorageError.Corrupt : StorageError.InvalidRequest);
    }
    private static void ValidateRules(IReadOnlyList<NovelRule> rules, bool stored = false)
    {
        if (rules.Any(r => string.IsNullOrEmpty(r.Id) || r.Name is null || r.Group is null || string.IsNullOrEmpty(r.Pattern) ||
            r.Replacement is null || r.Scope is null || r.ExcludeScope is null || r.TimeoutMillisecond <= 0) ||
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

    private Task EditEntry(string id, Func<NovelEntry?, NovelEntry?> edit, CancellationToken token) => work.Run(async ct =>
    {
        await serial.WaitAsync(ct);
        try
        {
            var result = await database.WriteAsync(db =>
            {
                NovelEntryRecord? currentRecord = NovelLibraryStore.FindEntry(db, id);
                NovelEntry? current = currentRecord is null ? null : FromRecord(currentRecord);
                if (current is not null) Validate(current, stored: true);
                var next = edit(current);
                if (next is not null)
                {
                    Validate(next);
                    if (next.BookId != id) throw new StorageException(StorageError.InvalidRequest);
                }
                var nextRecord = next is null ? null : ToRecord(next);
                if (NovelLibraryStore.EntryEquals(nextRecord, currentRecord) &&
                    (nextRecord is null || NovelLibraryStore.FindConflictingEntries(db, nextRecord).Length == 0))
                    return (Changed: false, Entry: next, RemovedIds: Array.Empty<string>());
                if (nextRecord is null)
                {
                    if (!NovelLibraryStore.DeleteEntry(db, id))
                        return (Changed: false, Entry: null, RemovedIds: Array.Empty<string>());
                    db.CommitRevision(StorageOwner, db.Revision(StorageOwner));
                    return (Changed: true, Entry: null, RemovedIds: Array.Empty<string>());
                }
                var (savedRecord, removedIds) = NovelLibraryStore.UpsertEntry(db, nextRecord);
                var savedEntry = FromRecord(savedRecord);
                Validate(savedEntry);
                db.CommitRevision(StorageOwner, db.Revision(StorageOwner));
                return (Changed: true, Entry: savedEntry, RemovedIds: removedIds);
            }, ct);
            if (result.Changed)
            {
                if (entries is not null)
                {
                    var removed = result.RemovedIds.Length > 0 ? result.RemovedIds.ToHashSet(StringComparer.Ordinal) : null;
                    var remaining = entries.Where(e => e.BookId != id && (removed is null || !removed.Contains(e.BookId)));
                    entries = result.Entry is null ? remaining.ToArray() : remaining.Append(result.Entry).ToArray();
                }
                Changed?.Invoke(this, new NovelLibraryChangeEventArgs(entries: true));
            }
            return true;
        }
        finally { serial.Release(); }
    }, token);

    public NovelEntry? Find(NovelBook book, string key)
    {
        var exact = Entries.Where(e => e.Book.SourceIdentity == book.SourceIdentity).ToArray();
        if (exact.Length == 1) return exact[0];
        if (exact.Length > 1)
        {
            DebugLog.Warn(DebugCategories.Novel, "library", "find.multiple_exact", new
            {
                sourceIdentity = book.SourceIdentity,
                matchCount = exact.Length
            });
            return SelectPreferred(exact);
        }
        if (key.Length == 0) return null;
        var byIdentity = Entries.Where(e => e.IdentityKey == key).ToArray();
        if (byIdentity.Length <= 1) return byIdentity.FirstOrDefault();
        DebugLog.Warn(DebugCategories.Novel, "library", "find.multiple_identity", new
        {
            identityKey = key,
            bookName = book.Name,
            matchCount = byIdentity.Length
        });
        return SelectPreferred(byIdentity);
    }
    private static NovelEntry SelectPreferred(NovelEntry[] matches) => matches
        .OrderByDescending(e => e.OnShelf)
        .ThenByDescending(e => e.ReadAt ?? DateTimeOffset.MinValue)
        .ThenByDescending(e => e.AddedAt ?? DateTimeOffset.MinValue)
        .ThenBy(e => e.BookId, StringComparer.Ordinal)
        .First();
    public Task RecordReadingAsync(NovelEntry entry, CancellationToken token = default) => EditEntry(entry.BookId, current =>
        current is null ? entry : entry with { OnShelf = current.OnShelf, AddedAt = current.AddedAt, PinnedAt = current.PinnedAt }, token);
    public Task SaveMetadataAsync(NovelEntry entry, CancellationToken token = default) => EditEntry(entry.BookId, current =>
        current is null ? null : current with { Book = entry.Book, IdentityKey = entry.IdentityKey, ChapterCount = entry.ChapterCount }, token);
    public Task SetShelfAsync(string id, bool value, CancellationToken token = default) => EditEntry(id, current =>
    {
        if (current is null) return null;
        var next = current with
        {
            OnShelf = value,
            AddedAt = value ? (current.OnShelf ? current.AddedAt ?? DateTimeOffset.UtcNow : DateTimeOffset.UtcNow) : null,
            PinnedAt = value ? current.PinnedAt : null
        };
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
    public Task ClearHistoryAsync(CancellationToken token = default) => work.Run(async ct =>
    {
        await serial.WaitAsync(ct);
        try
        {
            var result = await database.WriteAsync(db =>
            {
                var (previousRecords, _, _) = NovelLibraryStore.LoadAll(db);
                bool any = false;
                var updated = new List<NovelEntry>(previousRecords.Length);
                foreach (var previousRecord in previousRecords)
                {
                    var current = FromRecord(previousRecord);
                    Validate(current, stored: true);
                    if (current.ReadAt is null)
                    {
                        updated.Add(current);
                        continue;
                    }
                    any = true;
                    var next = current.OnShelf ? current with { ReadAt = null, Anchor = null } : null;
                    if (next is null)
                    {
                        NovelLibraryStore.DeleteEntry(db, current.BookId);
                    }
                    else
                    {
                        NovelLibraryStore.UpsertEntry(db, ToRecord(next));
                        updated.Add(next);
                    }
                }
                if (any) db.CommitRevision(StorageOwner, db.Revision(StorageOwner));
                return (Changed: any, Entries: updated.ToArray());
            }, ct);
            if (result.Changed)
            {
                entries = result.Entries;
                Changed?.Invoke(this, new NovelLibraryChangeEventArgs(entries: true));
            }
            return true;
        }
        finally { serial.Release(); }
    }, token);
    public Task PinAsync(string id, CancellationToken token = default) => EditEntry(id, current =>
        current is null ? null : current with { PinnedAt = current.PinnedAt is null ? DateTimeOffset.UtcNow : null }, token);

    public Task SaveAppearanceAsync(NovelAppearance value, CancellationToken token = default) => work.Run(async ct =>
    {
        ValidateAppearance(value);
        await serial.WaitAsync(ct);
        try
        {
            if (initialized && Equals(appearance, value)) return true;
            bool changed = await database.WriteAsync(db =>
            {
                if (!initialized)
                {
                    var previous = NovelLibraryStore.ReadAppearance(db);
                    if (Equals(previous, value)) return false;
                }
                NovelLibraryStore.SaveAppearance(db, value);
                db.CommitRevision(StorageOwner, db.Revision(StorageOwner));
                return true;
            }, ct);
            appearance = value; readingAppearance = null;
            if (changed) Changed?.Invoke(this, new NovelLibraryChangeEventArgs(appearance: true));
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
            if (initialized && this.rules is not null && NovelLibraryStore.RulesEqual(this.rules, values)) return true;
            bool changed = await database.WriteAsync(db =>
            {
                var previous = initialized && this.rules is not null ? this.rules : NovelLibraryStore.ReadRules(db);
                if (NovelLibraryStore.RulesEqual(previous, values)) return false;
                NovelLibraryStore.ReplaceRules(db, previous, values);
                db.CommitRevision(StorageOwner, db.Revision(StorageOwner));
                return true;
            }, ct);
            this.rules = values;
            if (changed) { rulesRevision++; Changed?.Invoke(this, new NovelLibraryChangeEventArgs(rules: true)); }
            return true;
        }
        finally { serial.Release(); }
    }, token);
    public Task PrepareShutdownAsync() => work.PauseAsync();
}
