using System.IO;
using System.Text.Json;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

/// <summary>Native shelf, history, appearance and user rules. Never stores source definitions or cookies.</summary>
public sealed class NovelLibrary(IDocumentStore store)
{
    public const string DocumentNamespace = "novel.library.v1";
    private sealed record Document(JsonElement Raw, NovelEntry[] Entries, NovelAppearance Appearance, NovelRule[] Rules);
    private static readonly JsonSerializerOptions ReadOptions = new(NovelJson.Options) { PropertyNameCaseInsensitive = false };
    private Document? saved;
    private DocumentFingerprint fingerprint = DocumentFingerprint.Missing;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly NovelWork work = new();
    public IReadOnlyList<NovelEntry> Entries => saved?.Entries ?? [];
    public NovelAppearance Appearance => saved?.Appearance ?? new();
    public IReadOnlyList<NovelRule> Rules => saved?.Rules ?? [];
    private static readonly JsonElement EmptyRules = NovelJson.Value(Array.Empty<NovelRule>());
    internal JsonElement RuleInputs
    {
        get
        {
            var raw = saved is null ? default : Section(saved.Raw, "rules");
            return raw.ValueKind == JsonValueKind.Undefined ? EmptyRules : raw;
        }
    }
    public event EventHandler? Changed;
    public Task InitializeAsync(CancellationToken token = default) => work.Run(async ct =>
    {
        await serial.WaitAsync(ct);
        try { await LoadAsync(ct); return true; } finally { serial.Release(); }
    }, token);
    private async Task LoadAsync(CancellationToken token)
    {
        if (saved is not null) return;
        var snapshot = await store.ReadAsync(DocumentNamespace, token);
        Document next;
        try
        {
            using var json = JsonDocument.Parse(snapshot.Data ?? JsonSerializer.SerializeToUtf8Bytes(new
                { version = 1, entries = Array.Empty<NovelEntry>(), appearance = new NovelAppearance(), rules = Array.Empty<NovelRule>() }, NovelJson.Options));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Count(p => p.NameEquals("version")) != 1 ||
                !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetDecimal(out var number) || number != 1 ||
                new[] { "entries", "appearance", "rules" }.Any(key => root.EnumerateObject().Count(p => p.NameEquals(key)) > 1))
                throw new JsonException();
            next = Project(root.Clone());
        }
        catch (JsonException) { throw new DocumentStoreException(DocumentStoreError.Corrupt); }
        fingerprint = snapshot.Fingerprint; saved = next;
    }
    private Task Mutate(Func<Document, Document> update, CancellationToken token) => work.Run(async ct =>
    {
        await serial.WaitAsync(ct);
        try
        {
            await LoadAsync(ct);
            var prior = saved!; var proposed = update(prior);
            var changed = new Dictionary<string, JsonElement>();
            if (!ReferenceEquals(prior.Entries, proposed.Entries))
                changed["entries"] = MergeRecords(Section(prior.Raw, "entries"), prior.Entries, proposed.Entries, "bookId", e => e.BookId);
            if (!ReferenceEquals(prior.Rules, proposed.Rules))
                changed["rules"] = MergeRecords(Section(prior.Raw, "rules"), prior.Rules, proposed.Rules, "id", r => r.Id);
            if (prior.Appearance != proposed.Appearance)
            {
                var raw = Section(prior.Raw, "appearance");
                if (raw.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
                    throw new DocumentStoreException(DocumentStoreError.Corrupt);
                changed["appearance"] = MergeModel(raw, NovelJson.Value(prior.Appearance), NovelJson.Value(proposed.Appearance));
            }
            var rawNext = ReplaceMembers(prior.Raw, changed);
            var next = new Document(rawNext,
                changed.TryGetValue("entries", out var entries) ? ReadEntries(entries, proposed.Entries) : prior.Entries,
                changed.TryGetValue("appearance", out var appearance) ? ReadAppearance(appearance) : prior.Appearance,
                changed.TryGetValue("rules", out var rules) ? ReadRules(rules, prior.Rules) : prior.Rules);
            var receipt = await store.WriteAsync(DocumentNamespace,
                JsonSerializer.SerializeToUtf8Bytes(rawNext), fingerprint, ct);
            // The receipt, including an accepted write during cancellation, is the truth.
            fingerprint = receipt; saved = next; Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        finally { serial.Release(); }
    }, token);
    private static JsonElement Section(JsonElement raw, string key) => raw.TryGetProperty(key, out var value) ? value : default;
    private static Document Project(JsonElement raw) => new(raw,
        ReadRecords<NovelEntry>(Section(raw, "entries"), "bookId", EntryReadable),
        ReadAppearance(Section(raw, "appearance")), ReadRules(Section(raw, "rules"), []));
    private static NovelEntry[] ReadEntries(JsonElement raw, NovelEntry[] proposed)
    {
        var entries = ReadRecords<NovelEntry>(raw, "bookId", EntryReadable);
        var books = proposed.ToDictionary(entry => entry.BookId, entry => entry.Book, StringComparer.Ordinal);
        for (int index = 0; index < entries.Length; index++)
        {
            var book = entries[index].Book;
            if (books.TryGetValue(entries[index].BookId, out var original) &&
                (book with { Data = original.Data }) == original && book.Data.ValueKind == original.Data.ValueKind &&
                (book.Data.ValueKind == JsonValueKind.Undefined || JsonElement.DeepEquals(book.Data, original.Data)))
                entries[index] = entries[index] with { Book = original };
        }
        return entries;
    }
    private static NovelRule[] ReadRules(JsonElement raw, NovelRule[] before)
    {
        var rules = ReadRecords<NovelRule>(raw, "id", (r, value) =>
            SingleString(value, "pattern") && r.Name is not null && r.Group is not null && r.Replacement is not null &&
            r.Scope is not null && r.ExcludeScope is not null,
            value => ReplaceMembers(value, new Dictionary<string, JsonElement>
            {
                ["name"] = NovelJson.Value(Field(value, "name", "")),
                ["group"] = NovelJson.Value(Field(value, "group", ""))
            }).Deserialize<NovelRule>(ReadOptions)!);
        var previous = before.ToDictionary(r => r.Id, StringComparer.Ordinal);
        for (int index = 0; index < rules.Length; index++)
            if (previous.TryGetValue(rules[index].Id, out var old) && JsonElement.DeepEquals(NovelJson.Value(old), NovelJson.Value(rules[index])))
                rules[index] = old;
        return rules;
    }
    private static bool SingleString(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().Count(p => p.NameEquals(key)) == 1 && value.GetProperty(key).ValueKind == JsonValueKind.String;
    private static string[] Ids(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        ? value.EnumerateObject().Where(p => p.NameEquals(key) && p.Value.ValueKind == JsonValueKind.String)
            .Select(p => p.Value.GetString()!).Where(id => id.Length > 0).ToArray() : [];
    private static T[] ReadRecords<T>(JsonElement raw, string key, Func<T, JsonElement, bool> readable,
        Func<JsonElement, T>? project = null)
    {
        if (raw.ValueKind != JsonValueKind.Array) return [];
        var counts = raw.EnumerateArray().SelectMany(value => Ids(value, key)).GroupBy(id => id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var result = new List<T>();
        foreach (var value in raw.EnumerateArray())
        {
            var ids = Ids(value, key);
            if (ids.Length != 1 || !SingleString(value, key) || counts[ids[0]] != 1) continue;
            try
            {
                if ((project is null ? value.Deserialize<T>(ReadOptions) : project(value)) is { } record && readable(record, value)) result.Add(record);
            }
            catch (JsonException) { } // Keep the original slot; only its usable projection is absent.
        }
        return result.ToArray();
    }
    private static bool EntryReadable(NovelEntry e, JsonElement raw) => SingleString(raw, "identityKey") &&
        raw.EnumerateObject().Count(p => p.NameEquals("book")) == 1 && e.Book is not null &&
        SingleString(Section(raw, "book"), "installationId") && SingleString(Section(raw, "book"), "bookUrl") &&
        Section(raw, "book").EnumerateObject().Count(p => p.NameEquals("revision")) == 1 &&
        !string.IsNullOrEmpty(e.Book.InstallationId) && !string.IsNullOrEmpty(e.Book.BookUrl) && e.Book.Revision > 0 &&
        e.Book.Name is not null && e.Book.Author is not null && e.Book.CoverUrl is not null && e.Book.Intro is not null &&
        e.Book.TocUrl is not null && e.Book.LatestChapterTitle is not null && e.ContentSignature is not null && e.ChapterCount >= 0 &&
        (e.Anchor is null || e.Anchor.ChapterUrl is not null && e.Anchor.ChapterTitle is not null && e.Anchor.ChapterIndex >= 0 &&
            e.Anchor.BlockIndex >= 0 && e.Anchor.Utf16Offset >= 0 && double.IsFinite(e.Anchor.Ratio) && e.Anchor.Ratio is >= 0 and <= 1);
    private static T Field<T>(JsonElement raw, string key, T fallback)
    {
        if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty(key, out var value)) return fallback;
        try
        {
            if (value.Deserialize<T>(ReadOptions) is not { } result || result is double number && !double.IsFinite(number)) return fallback;
            return result;
        }
        catch (JsonException) { return fallback; }
    }
    private static NovelAppearance ReadAppearance(JsonElement raw)
    {
        var a = new NovelAppearance();
        return a with
        {
            Mode = Field(raw, "mode", a.Mode), Conversion = Field(raw, "conversion", a.Conversion), Theme = Field(raw, "theme", a.Theme),
            FontSize = Field(raw, "fontSize", a.FontSize), FontFamily = Field(raw, "fontFamily", a.FontFamily), Bold = Field(raw, "bold", a.Bold),
            LineHeight = Field(raw, "lineHeight", a.LineHeight), ParagraphSpacing = Field(raw, "paragraphSpacing", a.ParagraphSpacing),
            Margin = Field(raw, "margin", a.Margin), ContentWidth = Field(raw, "contentWidth", a.ContentWidth), AutoSpeed = Field(raw, "autoSpeed", a.AutoSpeed),
            Background = Field(raw, "background", a.Background), Foreground = Field(raw, "foreground", a.Foreground), BackgroundImage = Field(raw, "backgroundImage", a.BackgroundImage)
        };
    }
    // Only changed model fields are written. Raw extras and untouched numeric tokens never pass through CLR numbers.
    private static JsonElement MergeModel(JsonElement raw, JsonElement before, JsonElement after)
    {
        if (raw.ValueKind != JsonValueKind.Object) return after;
        var changes = new Dictionary<string, JsonElement>();
        foreach (var property in after.EnumerateObject())
        {
            if (before.TryGetProperty(property.Name, out var old) && JsonElement.DeepEquals(old, property.Value)) continue;
            changes[property.Name] = property.Name is "book" or "anchor" && old.ValueKind == JsonValueKind.Object &&
                property.Value.ValueKind == JsonValueKind.Object && raw.TryGetProperty(property.Name, out var nested)
                ? MergeModel(nested, old, property.Value) : property.Value;
        }
        var removed = before.EnumerateObject().Where(p => !after.TryGetProperty(p.Name, out _)).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        return ReplaceMembers(raw, changes, removed);
    }
    private static JsonElement ReplaceMembers(JsonElement raw, IReadOnlyDictionary<string, JsonElement> changes, HashSet<string>? removed = null) => Write(writer =>
    {
        writer.WriteStartObject();
        var written = new HashSet<string>(StringComparer.Ordinal);
        if (raw.ValueKind == JsonValueKind.Object)
            foreach (var property in raw.EnumerateObject())
            {
                if (removed?.Contains(property.Name) == true) continue;
                writer.WritePropertyName(property.Name);
                (changes.TryGetValue(property.Name, out var value) ? value : property.Value).WriteTo(writer); written.Add(property.Name);
            }
        foreach (var (key, value) in changes)
            if (written.Add(key)) { writer.WritePropertyName(key); value.WriteTo(writer); }
        writer.WriteEndObject();
    });
    private static JsonElement Write(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) write(writer);
        using var json = JsonDocument.Parse(buffer.ToArray()); return json.RootElement.Clone();
    }
    private static JsonElement MergeRecords<T>(JsonElement raw, T[] before, T[] after, string key, Func<T, string> identity)
    {
        if (raw.ValueKind is not (JsonValueKind.Array or JsonValueKind.Undefined)) throw new DocumentStoreException(DocumentStoreError.Corrupt);
        var previous = before.ToDictionary(identity, StringComparer.Ordinal);
        var ids = after.Select(identity).ToArray();
        if (ids.Any(string.IsNullOrEmpty) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new InvalidDataException("AMBIGUOUS_NOVEL_IDENTITY");
        if (after.Any(item => item is NovelEntry entry && (entry.Book is null || entry.IdentityKey is null ||
            string.IsNullOrEmpty(entry.Book.InstallationId) || string.IsNullOrEmpty(entry.Book.BookUrl) || entry.Book.Revision < 1)))
            throw new InvalidDataException("INVALID_NOVEL_IDENTITY");
        var slots = raw.ValueKind == JsonValueKind.Array ? raw.EnumerateArray().ToArray() : [];
        var reserved = slots.SelectMany(value => Ids(value, key)).ToHashSet(StringComparer.Ordinal);
        if (ids.Any(id => reserved.Contains(id) && !previous.ContainsKey(id))) throw new DocumentStoreException(DocumentStoreError.Corrupt);
        var originals = slots.Where(value => Ids(value, key) is [var id] && previous.ContainsKey(id))
            .ToDictionary(value => Ids(value, key)[0], StringComparer.Ordinal);
        int next = 0;
        return Write(writer =>
        {
            writer.WriteStartArray();
            void Item(T item)
            {
                string id = identity(item);
                var value = previous.TryGetValue(id, out var old)
                    ? MergeModel(originals[id], NovelJson.Value(old!), NovelJson.Value(item!)) : NovelJson.Value(item!);
                value.WriteTo(writer);
            }
            foreach (var slot in slots)
            {
                if (Ids(slot, key) is [var id] && previous.ContainsKey(id)) { if (next < after.Length) Item(after[next++]); }
                else slot.WriteTo(writer);
            }
            while (next < after.Length) Item(after[next++]);
            writer.WriteEndArray();
        });
    }
    public NovelEntry? Find(NovelBook book, string key)
    {
        var matches = Entries.Where(e => e.Book.SourceIdentity == book.SourceIdentity || key.Length > 0 && e.IdentityKey == key).ToArray();
        var raw = saved is null ? default : Section(saved.Raw, "entries");
        int rawMatches = raw.ValueKind != JsonValueKind.Array ? 0 : raw.EnumerateArray().Count(value =>
            value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Any(p =>
                p.NameEquals("identityKey") && p.Value.ValueKind == JsonValueKind.String && key.Length > 0 && p.Value.GetString() == key ||
                p.NameEquals("book") && p.Value.ValueKind == JsonValueKind.Object &&
                p.Value.EnumerateObject().Any(b => b.NameEquals("installationId") && b.Value.ValueKind == JsonValueKind.String && b.Value.GetString() == book.InstallationId) &&
                p.Value.EnumerateObject().Any(b => b.NameEquals("bookUrl") && b.Value.ValueKind == JsonValueKind.String && b.Value.GetString() == book.BookUrl)));
        if (matches.Length > 1 || rawMatches > matches.Length) throw new InvalidDataException("AMBIGUOUS_NOVEL_IDENTITY");
        return matches.SingleOrDefault();
    }
    private static Document EditEntry(Document document, string id, Func<NovelEntry, NovelEntry?> update)
    {
        var current = document.Entries.FirstOrDefault(e => e.BookId == id);
        var raw = Section(document.Raw, "entries");
        if (current is null && raw.ValueKind == JsonValueKind.Array && raw.EnumerateArray().Any(value => Ids(value, "bookId").Contains(id, StringComparer.Ordinal)))
            throw new DocumentStoreException(DocumentStoreError.Corrupt);
        if (current is null) return document;
        var next = update(current);
        return document with { Entries = next is null ? document.Entries.Where(e => e.BookId != id).ToArray()
            : document.Entries.Select(e => e.BookId == id ? next : e).ToArray() };
    }
    public Task SaveEntryAsync(NovelEntry entry, CancellationToken token = default) => Mutate(document => document with
    { Entries = document.Entries.Where(e => e.BookId != entry.BookId).Append(entry).ToArray() }, token);
    public Task RecordReadingAsync(NovelEntry entry, CancellationToken token = default) => Mutate(document =>
    {
        var saved = document.Entries.FirstOrDefault(e => e.BookId == entry.BookId);
        var next = saved is null ? entry : entry with { OnShelf = saved.OnShelf, AddedAt = saved.AddedAt, PinnedAt = saved.PinnedAt };
        return document with { Entries = document.Entries.Where(e => e.BookId != entry.BookId).Append(next).ToArray() };
    }, token);
    public Task SaveMetadataAsync(NovelEntry entry, CancellationToken token = default) => Mutate(document => EditEntry(document, entry.BookId, e => e with
        { Book = entry.Book, IdentityKey = entry.IdentityKey, ChapterCount = entry.ChapterCount }), token);
    public Task SetShelfAsync(string id, bool value, CancellationToken token = default) => Mutate(document => EditEntry(document, id, e =>
    {
        var next = e with { OnShelf = value, AddedAt = value ? DateTimeOffset.UtcNow : null, PinnedAt = value ? e.PinnedAt : null };
        return next.OnShelf || next.ReadAt is not null ? next : null;
    }), token);
    public Task ToggleShelfAsync(NovelEntry entry, CancellationToken token = default) => Mutate(document =>
    {
        var saved = document.Entries.FirstOrDefault(e => e.BookId == entry.BookId);
        if (saved is not null) return EditEntry(document, entry.BookId, e =>
        {
            var next = e with { OnShelf = !e.OnShelf, AddedAt = e.OnShelf ? null : DateTimeOffset.UtcNow, PinnedAt = e.OnShelf ? null : e.PinnedAt };
            return next.OnShelf || next.ReadAt is not null ? next : null;
        });
        var added = entry with { OnShelf = true, AddedAt = DateTimeOffset.UtcNow, PinnedAt = null, ReadAt = null, Anchor = null };
        return document with { Entries = document.Entries.Append(added).ToArray() };
    }, token);
    public Task DeleteHistoryAsync(string id, CancellationToken token = default) => Mutate(document => EditEntry(document, id, e =>
        e.OnShelf ? e with { ReadAt = null, Anchor = null } : null), token);
    public Task PinAsync(string id, CancellationToken token = default) => Mutate(document => EditEntry(document, id, e =>
        e with { PinnedAt = e.PinnedAt is null ? DateTimeOffset.UtcNow : null }), token);
    public Task SaveAppearanceAsync(NovelAppearance value, CancellationToken token = default) =>
        Mutate(document => document with { Appearance = value }, token);
    public Task SaveRulesAsync(IReadOnlyList<NovelRule> rules, CancellationToken token = default) =>
        Mutate(document => document with { Rules = rules.ToArray() }, token);
    public Task PrepareShutdownAsync() => work.PauseAsync();
    public void Resume() => work.Resume();
}
