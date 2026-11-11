using System.IO;
using System.Text.Json;
using System.Windows.Media;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

/// <summary>Native shelf, history, appearance and user rules. Never stores source definitions or cookies.</summary>
public sealed class NovelLibrary(IDocumentStore store)
{
    public const string DocumentNamespace = "novel.library.v1";
    private sealed record Document(int Version, NovelEntry[] Entries, NovelAppearance Appearance, NovelRule[] Rules);
    private Document? saved;
    private DocumentFingerprint fingerprint = DocumentFingerprint.Missing;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly NovelWork work = new();
    public IReadOnlyList<NovelEntry> Entries => saved?.Entries ?? [];
    public NovelAppearance Appearance => saved?.Appearance ?? new();
    public IReadOnlyList<NovelRule> Rules => saved?.Rules ?? [];
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
            next = snapshot.Data is null ? new(1, [], new(), []) :
                JsonSerializer.Deserialize<Document>(snapshot.Data, NovelJson.Options) ?? throw new JsonException();
            Validate(next);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or FormatException)
        { throw new DocumentStoreException(DocumentStoreError.Corrupt); }
        fingerprint = snapshot.Fingerprint; saved = next;
    }
    private Task Mutate(Func<Document, Document> update, CancellationToken token) => work.Run(async ct =>
    {
        await serial.WaitAsync(ct);
        try
        {
            await LoadAsync(ct);
            var next = update(saved!);
            Validate(next);
            var receipt = await store.WriteAsync(DocumentNamespace,
                JsonSerializer.SerializeToUtf8Bytes(next, NovelJson.Options), fingerprint, ct);
            // The receipt, including an accepted write during cancellation, is the truth.
            fingerprint = receipt; saved = next; Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        finally { serial.Release(); }
    }, token);
    private static void Validate(Document value)
    {
        if (value.Version != 1 || value.Entries is null || value.Appearance is null || value.Rules is null ||
            value.Entries.Any(e => e is null || string.IsNullOrEmpty(e.BookId) || e.IdentityKey is null || e.Book is null ||
                string.IsNullOrEmpty(e.Book.InstallationId) || string.IsNullOrEmpty(e.Book.BookUrl) || e.Book.Name is null ||
                e.Book.Author is null || e.Book.CoverUrl is null || e.Book.Intro is null || e.Book.TocUrl is null ||
                e.Book.LatestChapterTitle is null || e.Book.Revision < 1 || e.ContentSignature is null || e.ChapterCount < 0 ||
                e.Anchor is { } a && (a.ChapterUrl is null || a.ChapterTitle is null || a.ChapterIndex < 0 ||
                    a.BlockIndex < 0 || a.Utf16Offset < 0 || !double.IsFinite(a.Ratio) || a.Ratio is < 0 or > 1)) ||
            value.Entries.Select(e => e.BookId).Distinct().Count() != value.Entries.Length ||
            value.Rules.Any(r => r is null || string.IsNullOrEmpty(r.Id) || r.Name is null || r.Group is null ||
                r.Pattern is null || r.Replacement is null || r.Scope is null || r.ExcludeScope is null ||
                r.Order is < -9007199254740991 or > 9007199254740991 || r.TimeoutMillisecond <= 0) ||
            value.Rules.Select(r => r.Id).Distinct().Count() != value.Rules.Length)
            throw new InvalidDataException("INVALID_NOVEL_DOCUMENT");
        ValidateAppearance(value.Appearance);
    }
    internal static void ValidateAppearance(NovelAppearance a)
    {
        if (a.Mode is not ("cover" or "slide" or "none" or "scroll") ||
            a.Conversion is not ("none" or "simplified" or "traditional") || a.Theme < 0 || a.Theme >= NovelAppearance.Themes.Length ||
            !System.Windows.Documents.TextElement.FontSizeProperty.IsValidValue(a.FontSize) ||
            !double.IsFinite(a.LineHeight) || a.LineHeight <= 0 || !double.IsFinite(a.FontSize * a.LineHeight) ||
            !double.IsFinite(a.ParagraphSpacing) || a.ParagraphSpacing < 0 || !double.IsFinite(a.Margin) || a.Margin < 0 ||
            !double.IsFinite(a.ContentWidth) || a.ContentWidth <= 0 || !double.IsFinite(a.AutoSpeed) || a.AutoSpeed is < 1 or > 10 ||
            string.IsNullOrWhiteSpace(a.FontFamily) || a.BackgroundImage is null || string.IsNullOrWhiteSpace(a.Background) || string.IsNullOrWhiteSpace(a.Foreground))
            throw new InvalidDataException("INVALID_APPEARANCE");
        _ = new FontFamily(a.FontFamily);
        _ = ColorConverter.ConvertFromString(a.Background); _ = ColorConverter.ConvertFromString(a.Foreground);
    }
    public NovelEntry? Find(NovelBook book, string key) => Entries.FirstOrDefault(e =>
        e.Book.SourceIdentity == book.SourceIdentity || key.Length > 0 && e.IdentityKey == key);
    public Task SaveEntryAsync(NovelEntry entry, CancellationToken token = default) => Mutate(document => document with
    { Entries = document.Entries.Where(e => e.BookId != entry.BookId).Append(entry).ToArray() }, token);
    public Task RecordReadingAsync(NovelEntry entry, CancellationToken token = default) => Mutate(document =>
    {
        var saved = document.Entries.FirstOrDefault(e => e.BookId == entry.BookId);
        var next = saved is null ? entry : entry with { OnShelf = saved.OnShelf, AddedAt = saved.AddedAt, PinnedAt = saved.PinnedAt };
        return document with { Entries = document.Entries.Where(e => e.BookId != entry.BookId).Append(next).ToArray() };
    }, token);
    public Task SaveMetadataAsync(NovelEntry entry, CancellationToken token = default) => Mutate(document => document with
    {
        Entries = document.Entries.Select(e => e.BookId == entry.BookId ? e with
        { Book = entry.Book, IdentityKey = entry.IdentityKey, ChapterCount = entry.ChapterCount } : e).ToArray()
    }, token);
    public Task SetShelfAsync(string id, bool value, CancellationToken token = default) => Mutate(document => document with
    { Entries = document.Entries.Select(e => e.BookId == id ? e with { OnShelf = value, AddedAt = value ? DateTimeOffset.UtcNow : null, PinnedAt = value ? e.PinnedAt : null } : e).Where(e => e.OnShelf || e.ReadAt is not null).ToArray() }, token);
    public Task ToggleShelfAsync(NovelEntry entry, CancellationToken token = default) => Mutate(document =>
    {
        var saved = document.Entries.FirstOrDefault(e => e.BookId == entry.BookId);
        var next = saved is null
            ? entry with { OnShelf = true, AddedAt = DateTimeOffset.UtcNow, PinnedAt = null, ReadAt = null, Anchor = null }
            : saved with { OnShelf = !saved.OnShelf, AddedAt = saved.OnShelf ? null : DateTimeOffset.UtcNow,
                PinnedAt = saved.OnShelf ? null : saved.PinnedAt };
        var entries = document.Entries.Where(e => e.BookId != entry.BookId);
        if (next.OnShelf || next.ReadAt is not null) entries = entries.Append(next);
        return document with { Entries = entries.ToArray() };
    }, token);
    public Task DeleteHistoryAsync(string id, CancellationToken token = default) => Mutate(document => document with
    { Entries = document.Entries.Select(e => e.BookId == id ? e with { ReadAt = null, Anchor = null } : e).Where(e => e.OnShelf || e.ReadAt is not null).ToArray() }, token);
    public Task PinAsync(string id, CancellationToken token = default) => Mutate(document => document with
    { Entries = document.Entries.Select(e => e.BookId == id ? e with { PinnedAt = e.PinnedAt is null ? DateTimeOffset.UtcNow : null } : e).ToArray() }, token);
    public Task SaveAppearanceAsync(NovelAppearance value, CancellationToken token = default) =>
        Mutate(document => document with { Appearance = value }, token);
    public Task SaveRulesAsync(IReadOnlyList<NovelRule> rules, CancellationToken token = default) =>
        Mutate(document => document with { Rules = rules.ToArray() }, token);
    public Task PrepareShutdownAsync() => work.PauseAsync();
    public void Resume() => work.Resume();
}
