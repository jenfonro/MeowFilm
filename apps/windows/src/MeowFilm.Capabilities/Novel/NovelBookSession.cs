using System.IO;
using System.Text.Json;
using System.Security.Cryptography;

namespace MeowFilm.Capabilities;

/// <summary>One retained native book identity. Source switching commits only after matching and content succeed.</summary>
public sealed class NovelBookSession(NovelSourceService sources, NovelLibrary library, NovelRuleProgram rules, NovelEntry entry)
{
    private readonly NovelWork work = new();
    private CancellationTokenSource? request;
    private long generation;
    public NovelEntry Entry { get; private set; } = entry;
    public NovelBook Book => Entry.Book;
    public IReadOnlyList<NovelChapter> Chapters { get; private set; } = [];
    public NovelChapter? Chapter { get; private set; }
    public IReadOnlyList<NovelBlock> Blocks { get; private set; } = [];
    public string ContentTitle { get; private set; } = "";
    public string ImageStyle { get; private set; } = "";
    public bool HasContent { get; private set; }
    public bool Loading { get; private set; }
    public bool RestoreByRatio { get; private set; }
    internal bool ConsumeRatioRestore() { bool value = RestoreByRatio; RestoreByRatio = false; return value; }
    private (string Title, NovelBlock[] Blocks, string ImageStyle, IReadOnlyList<NovelRule> Rules, string Conversion)? rawContent;
    public event EventHandler? Changed;
    public Func<IReadOnlyList<NovelChapter>, CancellationToken, Task<int?>>? SelectChapterAsync { get; set; }
    private async Task<(NovelBook Book, NovelChapter[] Chapters, string IdentityKey)> MetadataAsync(NovelBook book, CancellationToken token)
    {
        var source = await sources.RequireAsync(book, token);
        var info = await sources.InvokeAsync(source, "bookInfo", book.Payload(), token);
        var detail = NovelBook.From(info.GetProperty("book"), source);
        // Stable source URL comes from the runtime. Local identity remains independent of author enrichment.
        var toc = await sources.InvokeAsync(source, "toc", detail.Payload(), token);
        if (toc.TryGetProperty("book", out var updated) && updated.ValueKind == JsonValueKind.Object) detail = NovelBook.From(updated, source);
        string key = await rules.IdentityAsync(detail, token);
        return (detail, NovelJson.Decode<NovelChapter[]>(toc.GetProperty("chapters")), key);
    }
    public Task LoadMetadataAsync(CancellationToken token = default) => Start(LoadMetadataCoreAsync, token);
    private async Task LoadMetadataCoreAsync(long id, CancellationToken ct)
    {
        var result = await MetadataAsync(Book, ct);
        ct.ThrowIfCancellationRequested(); if (id != generation) return;
        var next = Entry with { Book = result.Book, IdentityKey = result.IdentityKey, ChapterCount = result.Chapters.Length };
        ContentResult? content = null; var anchor = Entry.Anchor;
        if (HasContent)
        {
            int? index = await MatchAsync(result.Chapters, anchor, ct);
            if (index is null) throw new OperationCanceledException(ct);
            content = await ContentAsync(result.Book, result.Chapters, result.Chapters[index.Value], ct);
            ct.ThrowIfCancellationRequested(); if (id != generation) return;
            next = ContentEntry(result.Book, result.IdentityKey, result.Chapters, content, anchor);
        }
        if (library.Entries.Any(e => e.BookId == Entry.BookId)) await library.SaveMetadataAsync(next, ct);
        ct.ThrowIfCancellationRequested(); if (id != generation) return;
        if (content is not null) CommitContent(next, result.Chapters, content, anchor is not null);
        else { Chapters = result.Chapters; Entry = next; }
    }
    public Task ReadAsync(int index, NovelAnchor? anchor = null, CancellationToken token = default) =>
        Start((id, ct) => ReadCoreAsync(index, anchor, id, ct), token);
    private async Task ReadCoreAsync(int index, NovelAnchor? anchor, long id, CancellationToken ct)
    {
        await sources.EnsureReadyAsync(ct);
        var book = Book; var chapters = Chapters; string key = Entry.IdentityKey;
        bool refresh = chapters.Count == 0 || sources.Require(book).Revision != book.Revision;
        if (refresh)
        {
            NovelAnchor? target = anchor;
            if (target is null && chapters.Count > 0)
            {
                var selected = chapters[Math.Clamp(index, 0, chapters.Count - 1)];
                target = new(selected.ChapterUrl, selected.Name, selected.Index);
            }
            var next = await MetadataAsync(book, ct);
            book = next.Book; chapters = next.Chapters; key = next.IdentityKey;
            if (target is not null)
            {
                int? matched = await MatchAsync(chapters, target, ct);
                if (matched is null) throw new OperationCanceledException(ct);
                index = matched.Value;
            }
        }
        ct.ThrowIfCancellationRequested(); if (id != generation) return;
        if (chapters.Count == 0) throw new InvalidDataException("EMPTY_TOC");
        index = Math.Clamp(index, 0, chapters.Count - 1);
        var result = await ContentAsync(book, chapters, chapters[index], ct);
        ct.ThrowIfCancellationRequested(); if (id != generation) return;
        var entry = ContentEntry(book, key, chapters, result, anchor);
        if (refresh && library.Entries.Any(e => e.BookId == Entry.BookId)) await library.SaveMetadataAsync(entry, ct);
        ct.ThrowIfCancellationRequested(); if (id != generation) return;
        CommitContent(entry, chapters, result, anchor is not null);
    }
    public Task ContinueAsync(CancellationToken token = default) => Start(async (id, ct) =>
    {
        await sources.EnsureReadyAsync(ct);
        if (Chapters.Count == 0 || sources.Require(Book).Revision != Book.Revision)
        {
            await LoadMetadataCoreAsync(id, ct);
            ct.ThrowIfCancellationRequested(); if (id != generation || HasContent) return;
        }
        ct.ThrowIfCancellationRequested(); if (id != generation) return;
        if (HasContent && Chapter is not null && Chapters.Any(chapter => ReferenceEquals(chapter, Chapter)))
        {
            await ReprocessCoreAsync(id, ct); return;
        }
        int? index = await MatchAsync(Chapters, Entry.Anchor, ct);
        if (index is null) throw new OperationCanceledException(ct);
        await ReadCoreAsync(index.Value, Entry.Anchor, id, ct);
    }, token);
    private async Task<int?> MatchAsync(IReadOnlyList<NovelChapter> chapters, NovelAnchor? anchor, CancellationToken token)
    {
        if (chapters.Count == 0) throw new InvalidDataException("EMPTY_TOC");
        if (anchor is null) return 0;
        var match = await rules.InvokeAsync(new { operation = "matchChapter", chapters, anchor }, token);
        return match.GetProperty("requiresSelection").GetBoolean()
            ? SelectChapterAsync is null ? null : await SelectChapterAsync(chapters, token)
            : match.GetProperty("index").GetInt32();
    }
    private sealed record ContentResult(string Title, NovelBlock[] Blocks, string ImageStyle, NovelChapter Chapter,
        string RawTitle, NovelBlock[] RawBlocks, IReadOnlyList<NovelRule> Rules, string Conversion);
    private async Task<ContentResult> ContentAsync(NovelBook book,
        IReadOnlyList<NovelChapter> chapters, NovelChapter chapter, CancellationToken token)
    {
        var source = await sources.RequireAsync(book, token);
        var value = await sources.InvokeAsync(source, "content", book.Payload(chapter, chapters), token);
        var blocks = NovelJson.Decode<NovelBlock[]>(value.GetProperty("blocks"));
        string title = NovelJson.Text(value, "title");
        if (title.Length == 0) title = chapter.Name;
        var resolvedChapter = value.TryGetProperty("chapter", out var resolved) && resolved.ValueKind == JsonValueKind.Object
            ? chapter.Update(resolved)
            : chapter;
        var clean = await ProcessAsync(title, blocks, book, source, token);
        return new(clean.Title, clean.Blocks, NovelJson.Text(value, "imageStyle"), resolvedChapter,
            title, blocks, clean.Rules, clean.Conversion);
    }
    private bool SettingsCurrent(IReadOnlyList<NovelRule> selected, string conversion) =>
        conversion == library.Appearance.Conversion && selected.SequenceEqual(library.Rules);
    private async Task<(string Title, NovelBlock[] Blocks, IReadOnlyList<NovelRule> Rules, string Conversion)> ProcessAsync(
        string title, NovelBlock[] blocks, NovelBook book, NovelSource source, CancellationToken token)
    {
        while (true)
        {
            var selected = library.Rules; string conversion = library.Appearance.Conversion;
            var clean = await rules.TransformAsync(title, blocks, book, source, selected, conversion, token);
            token.ThrowIfCancellationRequested();
            if (!sources.IsCurrent(source)) throw new OperationCanceledException(token);
            if (SettingsCurrent(selected, conversion))
                return (NovelJson.Text(clean, "title"), NovelJson.Decode<NovelBlock[]>(clean.GetProperty("blocks")), selected, conversion);
        }
    }
    private NovelEntry ContentEntry(NovelBook book, string key, IReadOnlyList<NovelChapter> chapters, ContentResult content, NovelAnchor? anchor) =>
        Entry with { Book = book, IdentityKey = key, ChapterCount = chapters.Count, ContentSignature = Signature(content.Blocks),
            Anchor = (anchor ?? new()) with { ChapterUrl = content.Chapter.ChapterUrl, ChapterTitle = content.Chapter.Name,
                ChapterIndex = content.Chapter.Index } };
    private void CommitContent(NovelEntry next, IReadOnlyList<NovelChapter> chapters, ContentResult content, bool restoring)
    {
        RestoreByRatio = restoring && Entry.ContentSignature.Length > 0 && Entry.ContentSignature != next.ContentSignature;
        Entry = next;
        Chapters = chapters.Select(c => c.Index == content.Chapter.Index ? content.Chapter : c).ToArray(); Chapter = content.Chapter; Blocks = content.Blocks; ContentTitle = content.Title;
        ImageStyle = content.ImageStyle; HasContent = true;
        rawContent = (content.RawTitle, content.RawBlocks, content.ImageStyle, content.Rules, content.Conversion);
    }
    private static string Signature(IReadOnlyList<NovelBlock> blocks) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(blocks, NovelJson.Options)));
    internal bool NeedsReprocessing => rawContent is { } raw && !SettingsCurrent(raw.Rules, raw.Conversion);
    public Task ReprocessAsync(CancellationToken token = default) =>
        NeedsReprocessing ? Start(ReprocessCoreAsync, token) : Task.CompletedTask;
    private async Task ReprocessCoreAsync(long id, CancellationToken ct)
    {
        if (rawContent is not { } raw || Chapter is null || SettingsCurrent(raw.Rules, raw.Conversion)) return;
        var clean = await ProcessAsync(raw.Title, raw.Blocks, Book, sources.Require(Book), ct);
        ct.ThrowIfCancellationRequested(); if (id != generation) return;
        string signature = Signature(clean.Blocks);
        RestoreByRatio |= signature != Entry.ContentSignature;
        Entry = Entry with { ContentSignature = signature };
        ContentTitle = clean.Title; Blocks = clean.Blocks;
        rawContent = (raw.Title, raw.Blocks, raw.ImageStyle, clean.Rules, clean.Conversion);
    }
    public Task SwitchSourceAsync(NovelBook candidate, CancellationToken token = default) => Start(async (id, ct) =>
    {
        var next = await MetadataAsync(candidate, ct);
        if (next.Chapters.Length == 0) throw new InvalidDataException("EMPTY_TOC");
        ct.ThrowIfCancellationRequested(); if (id != generation) return;
        var anchor = Entry.Anchor ?? new();
        int? index = await MatchAsync(next.Chapters, anchor, ct);
        if (index is null) throw new OperationCanceledException(ct);
        var chapter = next.Chapters[index.Value];
        var result = await ContentAsync(next.Book, next.Chapters, chapter, ct);
        ct.ThrowIfCancellationRequested(); if (id != generation) return;
        CommitContent(ContentEntry(next.Book, next.IdentityKey, next.Chapters, result, anchor), next.Chapters, result, true);
        if (library.Entries.Any(e => e.BookId == Entry.BookId)) await library.RecordReadingAsync(CurrentEntry(), ct);
    }, token);
    public async Task SaveProgressAsync(NovelAnchor anchor, CancellationToken token = default)
    {
        if (!HasContent || Chapter is null || anchor.ChapterUrl != Chapter.ChapterUrl) return;
        long id = generation; var chapter = Chapter;
        var next = CurrentEntry() with { Anchor = anchor, ReadAt = DateTimeOffset.UtcNow, ChapterCount = Chapters.Count };
        await library.RecordReadingAsync(next, token);
        if (id == generation && ReferenceEquals(chapter, Chapter))
            Entry = Entry with { Anchor = anchor, ReadAt = next.ReadAt };
    }
    private NovelEntry CurrentEntry()
    {
        var saved = library.Entries.FirstOrDefault(e => e.BookId == Entry.BookId);
        return saved is null ? Entry : Entry with { OnShelf = saved.OnShelf, AddedAt = saved.AddedAt, PinnedAt = saved.PinnedAt,
            ReadAt = saved.ReadAt, Anchor = Entry.Anchor ?? saved.Anchor };
    }
    public async Task ShelfAsync(CancellationToken token = default)
    {
        await library.ToggleShelfAsync(Entry, token);
        var saved = library.Entries.FirstOrDefault(e => e.BookId == Entry.BookId);
        Entry = Entry with { OnShelf = saved?.OnShelf == true, AddedAt = saved?.AddedAt, PinnedAt = saved?.PinnedAt };
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public Task PurchaseAsync(CancellationToken token = default) => Start(async (id, ct) =>
    {
        if (Chapter is null || !Chapter.IsVip || Chapter.IsPay) return;
        var chapter = Chapter; var source = await sources.RequireAsync(Book, ct); var anchor = Entry.Anchor;
        if (!source.Actions.Contains("payAction")) throw new InvalidDataException("UNSUPPORTED_ACTION");
        var paid = await sources.InvokeAsync(source, "payAction", Book.Payload(chapter, Chapters), ct);
        ct.ThrowIfCancellationRequested(); if (id != generation) return;
        var paidContext = paid.TryGetProperty("chapter", out var response) && response.ValueKind == JsonValueKind.Object
            ? chapter.Update(response) : chapter;
        var metadata = await MetadataAsync(Book, ct);
        int? index = await MatchAsync(metadata.Chapters, new(chapter.ChapterUrl, chapter.Name, chapter.Index), ct);
        if (index is null) throw new OperationCanceledException(ct);
        var refreshed = metadata.Chapters[index.Value];
        if (refreshed.ChapterUrl == chapter.ChapterUrl)
        {
            // Fresh catalog flags win, but rule context and variables written by content/pay
            // must not disappear just because the catalog does not return those fields.
            refreshed = paidContext.Update(NovelJson.Value(refreshed)) with { Variable = paidContext.Variable };
            metadata.Chapters[index.Value] = refreshed;
        }
        var content = await ContentAsync(metadata.Book, metadata.Chapters, refreshed, ct);
        ct.ThrowIfCancellationRequested(); if (id != generation) return;
        CommitContent(ContentEntry(metadata.Book, metadata.IdentityKey, metadata.Chapters, content, anchor),
            metadata.Chapters, content, anchor is not null);
    }, token);
    private Task Start(Func<long, CancellationToken, Task> action, CancellationToken token)
    {
        Cancel(); request?.Dispose(); request = CancellationTokenSource.CreateLinkedTokenSource(token);
        long id = generation; Loading = true; Changed?.Invoke(this, EventArgs.Empty);
        return work.Run(async ct =>
        {
            try { await action(id, ct); return true; }
            finally { if (id == generation) { Loading = false; Changed?.Invoke(this, EventArgs.Empty); } }
        }, request.Token);
    }
    public void Cancel() { ++generation; request?.Cancel(); Loading = false; }
    public async Task PrepareShutdownAsync() { Cancel(); await work.PauseAsync(); request?.Dispose(); request = null; }
    public void Resume() => work.Resume();
}
