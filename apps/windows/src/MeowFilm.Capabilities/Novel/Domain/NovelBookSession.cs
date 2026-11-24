using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using MeowFilm.Core.Debug;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities;

/// <summary>One retained native book identity. Source switching commits only after matching and content succeed.</summary>
public sealed class NovelBookSession(NovelSourceService sources, NovelLibrary library, NovelRuleProgram rules, NovelEntry entry)
{
    private sealed record PaidContext(NovelBook Book, NovelChapter Chapter, string OriginalChapterUrl, string InstallationId, long Revision)
    {
        internal bool MatchesChapterUrl(string chapterUrl) =>
            chapterUrl == Chapter.ChapterUrl || chapterUrl == OriginalChapterUrl;
    }
    private sealed record RawContentState(string Title, NovelBlock[] Blocks, string ImageStyle, long? RulesRevision, string? Conversion);
    private sealed record ContentResult(string Title, NovelBlock[] Blocks, string ImageStyle, NovelBook Book, NovelChapter Chapter,
        string RawTitle, NovelBlock[] RawBlocks, long? RulesRevision, string? Conversion, bool Processed);

    private readonly NovelWork work = new();
    private readonly SemaphoreSlim progressSerial = new(1, 1);
    private readonly object progressGate = new();
    private CancellationTokenSource? request;
    private long generation;
    private long sourceEpoch;
    private long progressSequence;
    private long lastSavedProgressSequence;
    private int activeProgressCalls;
    private TaskCompletionSource? progressDrained;
    private bool progressClosed;
    private bool progressDisposed;
    private PaidContext? paidContext;
    private RawContentState? rawContent;

    private static bool HasVariable(JsonElement? variable) =>
        variable is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) };

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
    public event EventHandler? Changed;
    public Func<IReadOnlyList<NovelChapter>, CancellationToken, Task<int?>>? SelectChapterAsync { get; set; }
    public Func<NovelBook, NovelBook, CancellationToken, Task<bool>>? ConfirmUnknownSourceAsync { get; set; }
    public Action<string>? ReportError { get; set; }

    internal void InvalidateSources(IReadOnlySet<string> retired)
    {
        if (paidContext is { } paid && retired.Contains(paid.InstallationId))
            paidContext = null;
    }

    internal Task<NovelChapter[]> FormatChaptersForDisplayAsync(IReadOnlyList<NovelChapter> chapters, CancellationToken token = default) =>
        rules.ConvertChapterNamesAsync(chapters, library.Appearance.ReadingConversion, token);

    private PaidContext? AcceptedPurchase(NovelBook book, NovelSource source) =>
        paidContext is { } paid &&
        paid.InstallationId == source.InstallationId &&
        paid.Revision == source.Revision &&
        paid.Book.BookUrl == book.BookUrl
            ? paid
            : null;

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

    private async Task<(NovelBook Book, NovelChapter[] Chapters, string IdentityKey, int ChapterIndex)> RefreshPurchaseCatalogAsync(
        PaidContext paid,
        NovelSource source,
        CancellationToken token)
    {
        var toc = await sources.InvokeAsync(source, "toc", paid.Book.Payload(), token);
        var updatedBook = toc.TryGetProperty("book", out var updated) && updated.ValueKind == JsonValueKind.Object
            ? NovelBook.From(updated, source)
            : paid.Book;
        var chapters = NovelJson.Decode<NovelChapter[]>(toc.GetProperty("chapters"));
        int index = Array.FindIndex(chapters, c => c.ChapterUrl == paid.Chapter.ChapterUrl);
        if (index < 0 && paid.OriginalChapterUrl != paid.Chapter.ChapterUrl)
            index = Array.FindIndex(chapters, c => c.ChapterUrl == paid.OriginalChapterUrl);
        if (index < 0)
        {
            int? matched = await MatchAsync(chapters, new(paid.Chapter.ChapterUrl, paid.Chapter.Name, paid.Chapter.Index), "purchase_refresh", token);
            if (matched is null) throw new OperationCanceledException(token);
            index = matched.Value;
        }
        var refreshed = chapters[index];
        if (paid.MatchesChapterUrl(refreshed.ChapterUrl))
        {
            // Fresh catalog flags win, but rule context and variables written by content/pay
            // must not disappear just because the catalog does not return those fields.
            refreshed = paid.Chapter.Update(NovelJson.Value(refreshed)) with
            {
                Variable = HasVariable(paid.Chapter.Variable) ? paid.Chapter.Variable : refreshed.Variable
            };
            chapters[index] = refreshed;
        }
        if (HasVariable(paid.Chapter.Variable))
            updatedBook = updatedBook.WithVariable(paid.Chapter.Variable);
        paidContext = new PaidContext(updatedBook, refreshed, paid.OriginalChapterUrl, source.InstallationId, source.Revision);
        string key = await rules.IdentityAsync(updatedBook, token);
        return (updatedBook, chapters, key, index);
    }

    public Task LoadMetadataAsync(CancellationToken token = default) => Start(LoadMetadataCoreAsync, token);
    private async Task LoadMetadataCoreAsync(long id, CancellationToken ct)
    {
        var source = await sources.RequireAsync(Book, ct);
        NovelBook nextBook;
        NovelChapter[] nextChapters;
        string nextKey;
        if (AcceptedPurchase(Book, source) is { } paid)
        {
            var refreshed = await RefreshPurchaseCatalogAsync(paid, source, ct);
            nextBook = refreshed.Book;
            nextChapters = refreshed.Chapters;
            nextKey = refreshed.IdentityKey;
        }
        else
        {
            var result = await MetadataAsync(Book, ct);
            nextBook = result.Book;
            nextChapters = result.Chapters;
            nextKey = result.IdentityKey;
        }
        ct.ThrowIfCancellationRequested(); if (id != generation) return;
        var next = Entry with { Book = nextBook, IdentityKey = nextKey, ChapterCount = nextChapters.Length };
        ContentResult? content = null; var anchor = Entry.Anchor;
        if (HasContent)
        {
            int? index = await MatchAsync(nextChapters, anchor, "refresh_catalog", ct);
            if (index is null) throw new OperationCanceledException(ct);
            content = await ContentAsync(nextBook, nextChapters, nextChapters[index.Value], ct, requiresProcessedContent: true);
            ct.ThrowIfCancellationRequested(); if (id != generation) return;
            next = ContentEntry(content.Book, nextKey, nextChapters, content, anchor);
        }
        if (library.Entries.Any(e => e.BookId == Entry.BookId)) await library.SaveMetadataAsync(next, ct);
        ct.ThrowIfCancellationRequested(); if (id != generation) return;
        if (content is not null) CommitContent(next, nextChapters, content, restoring: anchor is not null);
        else { Chapters = nextChapters; Entry = next; }
    }

    public Task ReadAsync(int index, NovelAnchor? anchor = null, CancellationToken token = default) =>
        Start((id, ct) => ReadCoreAsync(index, anchor, id, ct), token);
    private async Task ReadCoreAsync(int index, NovelAnchor? anchor, long id, CancellationToken ct)
    {
        await sources.EnsureReadyAsync(ct);
        var book = Book; var chapters = Chapters; string key = Entry.IdentityKey;
        var source = sources.Require(book);
        if (AcceptedPurchase(book, source) is { } paid)
        {
            if (chapters.Count > 0 && paid.MatchesChapterUrl(chapters[Math.Clamp(index, 0, chapters.Count - 1)].ChapterUrl))
            {
                await RefreshPurchasedChapterCoreAsync(paid, source, anchor, id, ct);
                return;
            }
            if (HasVariable(paid.Book.Variable)) book = book.WithVariable(paid.Book.Variable);
        }
        bool refresh = chapters.Count == 0 || source.Revision != book.Revision;
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
                int? matched = await MatchAsync(chapters, target, "read_refresh", ct);
                if (matched is null) throw new OperationCanceledException(ct);
                index = matched.Value;
            }
        }
        ct.ThrowIfCancellationRequested(); if (id != generation) return;
        if (chapters.Count == 0) throw new InvalidDataException("EMPTY_TOC");
        index = Math.Clamp(index, 0, chapters.Count - 1);
        var result = await ContentAsync(book, chapters, chapters[index], ct, requiresProcessedContent: false);
        ct.ThrowIfCancellationRequested(); if (id != generation) return;
        var entry = ContentEntry(result.Book, key, chapters, result, anchor);
        if (refresh && library.Entries.Any(e => e.BookId == Entry.BookId)) await library.SaveMetadataAsync(entry, ct);
        ct.ThrowIfCancellationRequested(); if (id != generation) return;
        CommitContent(entry, chapters, result, restoring: anchor is not null);
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
        int? index = await MatchAsync(Chapters, Entry.Anchor, "continue", ct);
        if (index is null) throw new OperationCanceledException(ct);
        await ReadCoreAsync(index.Value, Entry.Anchor, id, ct);
    }, token);

    private async Task<int?> MatchAsync(IReadOnlyList<NovelChapter> chapters, NovelAnchor? anchor, string context, CancellationToken token)
    {
        if (chapters.Count == 0) throw new InvalidDataException("EMPTY_TOC");
        if (anchor is null) return 0;
        var match = await rules.InvokeAsync(new { operation = "matchChapter", chapters, anchor }, token);
        bool requiresSelection = match.GetProperty("requiresSelection").GetBoolean();
        int? matchedIndex = requiresSelection ? null : match.GetProperty("index").GetInt32();
        DebugLog.Debug(DebugCategories.Novel, "reader", "chapter.match", new
        {
            context,
            book = Book.Name,
            anchorChapterUrl = anchor.ChapterUrl,
            matchedIndex,
            requiresSelection
        });
        return requiresSelection
            ? SelectChapterAsync is null ? null : await SelectChapterAsync(chapters, token)
            : matchedIndex;
    }

    private async Task<ContentResult> ContentAsync(NovelBook book,
        IReadOnlyList<NovelChapter> chapters, NovelChapter chapter, CancellationToken token,
        bool requiresProcessedContent)
    {
        var source = await sources.RequireAsync(book, token);
        var value = await sources.InvokeAsync(source, "content", book.Payload(chapter, chapters), token);
        var blocks = NovelJson.Decode<NovelBlock[]>(value.GetProperty("blocks"));
        string title = NovelJson.Text(value, "title");
        if (title.Length == 0) title = chapter.Name;
        var resolvedChapter = value.TryGetProperty("chapter", out var resolved) && resolved.ValueKind == JsonValueKind.Object
            ? chapter.Update(resolved)
            : chapter;
        var updatedBook = HasVariable(resolvedChapter.Variable) ? book.WithVariable(resolvedChapter.Variable) : book;
        if (AcceptedPurchase(updatedBook, source) is { } paid)
        {
            var nextPaidChapter = paid.MatchesChapterUrl(chapter.ChapterUrl)
                ? resolvedChapter
                : paid.Chapter with
                {
                    Variable = HasVariable(resolvedChapter.Variable) ? resolvedChapter.Variable : paid.Chapter.Variable
                };
            paidContext = new PaidContext(updatedBook, nextPaidChapter, paid.OriginalChapterUrl, source.InstallationId, source.Revision);
        }
        try
        {
            var clean = await ProcessAsync(title, blocks, updatedBook, source, token);
            return new(clean.Title, clean.Blocks, NovelJson.Text(value, "imageStyle"), updatedBook, resolvedChapter,
                title, blocks, clean.RulesRevision, clean.Conversion, Processed: true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            if (requiresProcessedContent) throw;
            DebugLog.Warn(DebugCategories.Novel, "reader", "transform.fallback_raw", new
            {
                book = updatedBook.Name,
                chapterUrl = resolvedChapter.ChapterUrl,
                code = error is BridgeException bridge ? bridge.Code : error.GetType().Name
            }, error: error);
            ReportError?.Invoke(FormatRuleError(error));
            return new(title, blocks, NovelJson.Text(value, "imageStyle"), updatedBook, resolvedChapter,
                title, blocks, RulesRevision: null, Conversion: null, Processed: false);
        }
    }

    private static string FormatRuleError(Exception error) => error switch
    {
        BridgeException { Code: "TIMEOUT" } => "小说规则执行超时，已显示原文",
        BridgeException bridge => $"小说规则执行失败（{bridge.Code}），已显示原文",
        _ => "小说规则执行失败，已显示原文"
    };

    private bool SettingsCurrent(long rulesRevision, string conversion) =>
        conversion == library.Appearance.ReadingConversion && rulesRevision == library.RulesRevision;

    private async Task<(string Title, NovelBlock[] Blocks, long RulesRevision, string Conversion)> ProcessAsync(
        string title, NovelBlock[] blocks, NovelBook book, NovelSource source, CancellationToken token)
    {
        while (true)
        {
            long rulesRevision = library.RulesRevision; var selected = library.Rules; string conversion = library.Appearance.ReadingConversion;
            try
            {
                var clean = await rules.TransformAsync(title, blocks, book, source, selected, conversion, token);
                token.ThrowIfCancellationRequested();
                if (!sources.IsCurrent(source)) throw new OperationCanceledException(token);
                if (SettingsCurrent(rulesRevision, conversion))
                    return (NovelJson.Text(clean, "title"), NovelJson.Decode<NovelBlock[]>(clean.GetProperty("blocks")), rulesRevision, conversion);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                token.ThrowIfCancellationRequested();
                if (!sources.IsCurrent(source)) throw new OperationCanceledException(token);
                if (SettingsCurrent(rulesRevision, conversion)) throw;
            }
        }
    }

    private NovelEntry ContentEntry(NovelBook book, string key, IReadOnlyList<NovelChapter> chapters, ContentResult content, NovelAnchor? anchor) =>
        Entry with
        {
            Book = book,
            IdentityKey = key,
            ChapterCount = chapters.Count,
            ContentSignature = Signature(content.Blocks),
            Anchor = (anchor ?? new()) with
            {
                ChapterUrl = content.Chapter.ChapterUrl,
                ChapterTitle = content.Chapter.Name,
                ChapterIndex = content.Chapter.Index
            }
        };

    private void CommitContent(
        NovelEntry next,
        IReadOnlyList<NovelChapter> chapters,
        ContentResult content,
        bool restoring,
        bool crossSource = false,
        (NovelAnchor Anchor, NovelAnchorStrategy Strategy)? preResolved = null)
    {
        bool sameChapter = !crossSource &&
            Entry.Book.InstallationId == next.Book.InstallationId &&
            Entry.Anchor?.ChapterUrl == content.Chapter.ChapterUrl;
        NovelAnchor resolvedAnchor;
        NovelAnchorStrategy strategy;
        if (preResolved is { } resolved)
        {
            (resolvedAnchor, strategy) = resolved;
            RestoreByRatio = crossSource || strategy == NovelAnchorStrategy.Ratio;
        }
        else if (crossSource)
        {
            var resetAnchor = (next.Anchor ?? new()) with { BlockIndex = -1, Utf16Offset = 0 };
            (resolvedAnchor, strategy) = NovelAnchorResolver.Resolve(resetAnchor, previous: null, content.Blocks, content.Chapter);
            RestoreByRatio = true;
        }
        else if (restoring)
        {
            IReadOnlyList<NovelBlock>? previousBlocks = sameChapter && HasContent
                ? Blocks
                : sameChapter && (Entry.ContentSignature.Length == 0 || Entry.ContentSignature == next.ContentSignature)
                    ? content.Blocks
                    : null;
            (resolvedAnchor, strategy) = NovelAnchorResolver.Resolve(next.Anchor, previousBlocks, content.Blocks, content.Chapter);
            RestoreByRatio = strategy == NovelAnchorStrategy.Ratio;
        }
        else
        {
            resolvedAnchor = new NovelAnchor(content.Chapter.ChapterUrl, content.Chapter.Name, content.Chapter.Index, 0, 0, 0);
            strategy = NovelAnchorStrategy.Exact;
            RestoreByRatio = false;
        }

        DebugLog.Debug(DebugCategories.Novel, "reader", "anchor.resolve", new
        {
            context = crossSource ? "switch_source" : restoring ? "restore" : "load",
            strategy = strategy.ToString().ToLowerInvariant(),
            chapterIndex = content.Chapter.Index,
            blockIndex = resolvedAnchor.BlockIndex,
            utf16Offset = resolvedAnchor.Utf16Offset,
            ratio = resolvedAnchor.Ratio
        });

        if (paidContext is { } paid &&
            paid.InstallationId == next.Book.InstallationId &&
            paid.Revision == next.Book.Revision &&
            paid.Book.BookUrl == next.Book.BookUrl &&
            paid.MatchesChapterUrl(content.Chapter.ChapterUrl) &&
            content.Processed)
        {
            paidContext = null;
            DebugLog.Info(DebugCategories.Novel, "reader", "purchase.state", new
            {
                phase = "refresh_done",
                sourceId = next.Book.InstallationId,
                chapterUrl = content.Chapter.ChapterUrl
            });
        }

        Entry = next with { Anchor = resolvedAnchor };
        Chapters = chapters.Select(c => c.Index == content.Chapter.Index ? content.Chapter : c).ToArray();
        Chapter = content.Chapter;
        Blocks = content.Blocks;
        ContentTitle = content.Title;
        ImageStyle = content.ImageStyle;
        HasContent = true;
        rawContent = new RawContentState(content.RawTitle, content.RawBlocks, content.ImageStyle, content.RulesRevision, content.Conversion);
    }

    private static string Signature(IReadOnlyList<NovelBlock> blocks) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(blocks, NovelJson.Options)));

    internal bool NeedsReprocessing => rawContent is { } raw &&
        (raw.RulesRevision is null || raw.Conversion is null || !SettingsCurrent(raw.RulesRevision.Value, raw.Conversion));

    public Task ReprocessAsync(CancellationToken token = default) =>
        NeedsReprocessing ? Start(ReprocessCoreAsync, token) : Task.CompletedTask;

    private async Task ReprocessCoreAsync(long id, CancellationToken ct)
    {
        if (rawContent is not { } raw || Chapter is null ||
            (raw.RulesRevision is { } rev && raw.Conversion is { } conv && SettingsCurrent(rev, conv)))
            return;
        var clean = await ProcessAsync(raw.Title, raw.Blocks, Book, sources.Require(Book), ct);
        ct.ThrowIfCancellationRequested(); if (id != generation) return;
        string signature = Signature(clean.Blocks);
        var (resolvedAnchor, strategy) = NovelAnchorResolver.Resolve(Entry.Anchor, Blocks, clean.Blocks, Chapter);
        RestoreByRatio = strategy == NovelAnchorStrategy.Ratio;
        DebugLog.Debug(DebugCategories.Novel, "reader", "anchor.resolve", new
        {
            context = "reprocess",
            strategy = strategy.ToString().ToLowerInvariant(),
            chapterIndex = Chapter.Index,
            blockIndex = resolvedAnchor.BlockIndex,
            utf16Offset = resolvedAnchor.Utf16Offset,
            ratio = resolvedAnchor.Ratio
        });
        Entry = Entry with { ContentSignature = signature, Anchor = resolvedAnchor };
        ContentTitle = clean.Title; Blocks = clean.Blocks;
        rawContent = new RawContentState(raw.Title, raw.Blocks, raw.ImageStyle, clean.RulesRevision, clean.Conversion);
    }

    public Task SwitchSourceAsync(NovelBook candidate, CancellationToken token = default, bool confirmedUnknown = false) => Start(async (id, ct) =>
    {
        var next = await MetadataAsync(candidate, ct);
        if (next.Chapters.Length == 0) throw new InvalidDataException("EMPTY_TOC");
        ct.ThrowIfCancellationRequested(); if (id != generation) return;

        var identityCheck = await rules.SourceCandidatesAsync([next.Book], Book, ct);
        ct.ThrowIfCancellationRequested(); if (id != generation) return;
        var match = identityCheck.Candidates.FirstOrDefault(c => c.SameBook);
        DebugLog.Info(DebugCategories.Novel, "reader", "switch_source.identity", new
        {
            book = Book.Name,
            candidateSourceId = next.Book.InstallationId,
            sameBook = match is not null,
            sameSource = match?.SameSource ?? false,
            needsConfirmation = match?.NeedsConfirmation ?? false,
            confirmedUnknown
        });
        if (match is null) throw new InvalidDataException("所选书源与当前书籍不匹配");
        if (!confirmedUnknown && match.NeedsConfirmation)
        {
            if (ConfirmUnknownSourceAsync is null) throw new InvalidDataException("所选书源作者未知，请确认后切换");
            if (!await ConfirmUnknownSourceAsync(Book, next.Book, ct)) throw new OperationCanceledException(ct);
            ct.ThrowIfCancellationRequested(); if (id != generation) return;
        }

        var anchor = Entry.Anchor ?? new();
        int? index = await MatchAsync(next.Chapters, anchor, "switch_source", ct);
        if (index is null) throw new OperationCanceledException(ct);
        var chapter = next.Chapters[index.Value];
        var result = await ContentAsync(next.Book, next.Chapters, chapter, ct, requiresProcessedContent: true);
        ct.ThrowIfCancellationRequested(); if (id != generation) return;

        var nextEntry = ContentEntry(result.Book, next.IdentityKey, next.Chapters, result, anchor);
        var resetAnchor = (nextEntry.Anchor ?? new()) with { BlockIndex = -1, Utf16Offset = 0 };
        var preResolved = NovelAnchorResolver.Resolve(resetAnchor, previous: null, result.Blocks, result.Chapter);
        lock (progressGate)
        {
            if (progressClosed) throw new OperationCanceledException(ct);
            activeProgressCalls++;
        }
        bool acquiredProgress = false;
        try
        {
            await progressSerial.WaitAsync(ct);
            acquiredProgress = true;
            ct.ThrowIfCancellationRequested(); if (id != generation) return;
            lock (progressGate)
            {
                sourceEpoch++;
                lastSavedProgressSequence = progressSequence;
            }
            var saved = library.Entries.FirstOrDefault(e => e.BookId == Entry.BookId);
            if (saved is not null)
            {
                if (saved.ReadAt is not null)
                {
                    await library.RecordReadingAsync(nextEntry with
                    {
                        OnShelf = saved.OnShelf,
                        AddedAt = saved.AddedAt,
                        PinnedAt = saved.PinnedAt,
                        ReadAt = saved.ReadAt,
                        Anchor = preResolved.Anchor
                    }, ct);
                }
                else
                {
                    // Keep unread on-shelf books free of synthetic read_at_ms=0 progress rows.
                    await library.SaveMetadataAsync(nextEntry with { Anchor = null, ReadAt = null }, ct);
                }
            }
            ct.ThrowIfCancellationRequested(); if (id != generation) return;
            CommitContent(nextEntry, next.Chapters, result, restoring: true, crossSource: true, preResolved: preResolved);
        }
        finally
        {
            if (acquiredProgress) progressSerial.Release();
            TaskCompletionSource? drained = null;
            lock (progressGate)
            {
                if (--activeProgressCalls == 0 && progressClosed)
                    drained = progressDrained;
            }
            drained?.TrySetResult();
        }
    }, token);

    public async Task SaveProgressAsync(NovelAnchor anchor, CancellationToken token = default)
    {
        if (!HasContent || Chapter is null || anchor.ChapterUrl != Chapter.ChapterUrl) return;
        if (!Blocks.Any(b => b.Type == "image" || !string.IsNullOrEmpty(b.Text))) return;
        if (string.IsNullOrEmpty(anchor.ChapterUrl) || anchor.ChapterIndex < 0 || anchor.BlockIndex < 0 ||
            anchor.Utf16Offset < 0 || !double.IsFinite(anchor.Ratio) || anchor.Ratio is < 0 or > 1) return;
        long id = generation;
        var chapter = Chapter;
        string sourceIdentity = Book.SourceIdentity;
        long epoch;
        long seq;
        lock (progressGate)
        {
            if (progressClosed) return;
            activeProgressCalls++;
            epoch = sourceEpoch;
            seq = ++progressSequence;
        }
        bool acquired = false;
        try
        {
            await progressSerial.WaitAsync(token);
            acquired = true;
            token.ThrowIfCancellationRequested();
            lock (progressGate)
            {
                if (id != generation || epoch != sourceEpoch || !ReferenceEquals(chapter, Chapter) ||
                    Book.SourceIdentity != sourceIdentity || seq <= lastSavedProgressSequence)
                    return;
            }
            var next = CurrentEntry() with { Anchor = anchor, ReadAt = DateTimeOffset.UtcNow, ChapterCount = Chapters.Count };
            await library.RecordReadingAsync(next, token);
            bool commitInMemory;
            lock (progressGate)
            {
                if (epoch != sourceEpoch) return;
                if (seq > lastSavedProgressSequence) lastSavedProgressSequence = seq;
                commitInMemory = id == generation && ReferenceEquals(chapter, Chapter) && Book.SourceIdentity == sourceIdentity;
            }
            if (commitInMemory)
                Entry = Entry with { Anchor = anchor, ReadAt = next.ReadAt };
        }
        finally
        {
            if (acquired) progressSerial.Release();
            TaskCompletionSource? drained = null;
            lock (progressGate)
            {
                if (--activeProgressCalls == 0 && progressClosed)
                    drained = progressDrained;
            }
            drained?.TrySetResult();
        }
    }

    private NovelEntry CurrentEntry()
    {
        var saved = library.Entries.FirstOrDefault(e => e.BookId == Entry.BookId);
        return saved is null ? Entry : Entry with
        {
            OnShelf = saved.OnShelf,
            AddedAt = saved.AddedAt,
            PinnedAt = saved.PinnedAt,
            ReadAt = saved.ReadAt,
            Anchor = Entry.Anchor ?? saved.Anchor
        };
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
        if (AcceptedPurchase(Book, source) is { } existing && existing.MatchesChapterUrl(chapter.ChapterUrl))
        {
            DebugLog.Info(DebugCategories.Novel, "reader", "purchase.state", new
            {
                phase = "reused_receipt",
                sourceId = source.InstallationId,
                chapterUrl = chapter.ChapterUrl
            });
            await RefreshPurchasedChapterCoreAsync(existing, source, anchor, id, ct);
            return;
        }
        DebugLog.Info(DebugCategories.Novel, "reader", "purchase.state", new
        {
            phase = "pay_start",
            sourceId = source.InstallationId,
            chapterUrl = chapter.ChapterUrl
        });
        var paid = await sources.InvokeAsync(source, "payAction", Book.Payload(chapter, Chapters), ct);
        // Persist the accepted payment context immediately BEFORE cancellation/generation
        // checks so retrying after any downstream failure NEVER replays payAction.
        var paidChapter = chapter;
        var paidBook = Book;
        if (sources.IsCurrent(source))
            paidContext = new PaidContext(paidBook, paidChapter, chapter.ChapterUrl, source.InstallationId, source.Revision);
        if (paid.TryGetProperty("chapter", out var response) && response.ValueKind == JsonValueKind.Object)
        {
            paidChapter = chapter.Update(response);
            if (HasVariable(paidChapter.Variable)) paidBook = paidBook.WithVariable(paidChapter.Variable);
        }
        var accepted = new PaidContext(paidBook, paidChapter, chapter.ChapterUrl, source.InstallationId, source.Revision);
        if (sources.IsCurrent(source)) paidContext = accepted;
        DebugLog.Info(DebugCategories.Novel, "reader", "purchase.state", new
        {
            phase = "pay_accepted",
            sourceId = source.InstallationId,
            chapterUrl = paidChapter.ChapterUrl
        });
        ct.ThrowIfCancellationRequested(); if (id != generation) return;
        await RefreshPurchasedChapterCoreAsync(accepted, source, anchor, id, ct);
    }, token);

    private async Task RefreshPurchasedChapterCoreAsync(PaidContext paid, NovelSource source, NovelAnchor? anchor, long id, CancellationToken ct)
    {
        var refreshed = await RefreshPurchaseCatalogAsync(paid, source, ct);
        ct.ThrowIfCancellationRequested(); if (id != generation) return;
        var content = await ContentAsync(refreshed.Book, refreshed.Chapters, refreshed.Chapters[refreshed.ChapterIndex], ct, requiresProcessedContent: true);
        ct.ThrowIfCancellationRequested(); if (id != generation) return;
        CommitContent(ContentEntry(content.Book, refreshed.IdentityKey, refreshed.Chapters, content, anchor),
            refreshed.Chapters, content, restoring: anchor is not null);
    }

    private Task Start(Func<long, CancellationToken, Task> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Cancel(); request?.Dispose(); request = CancellationTokenSource.CreateLinkedTokenSource(token);
        long id = generation;
        try
        {
            Loading = true; Changed?.Invoke(this, EventArgs.Empty);
            return work.Run(async ct =>
            {
                try { await action(id, ct); return true; }
                finally { if (id == generation) { Loading = false; Changed?.Invoke(this, EventArgs.Empty); } }
            }, request.Token);
        }
        catch
        {
            if (id == generation) { Loading = false; Changed?.Invoke(this, EventArgs.Empty); }
            throw;
        }
    }
    public void Cancel() { ++generation; request?.Cancel(); Loading = false; }
    internal Task RetireAsync() { SelectChapterAsync = null; ConfirmUnknownSourceAsync = null; return PrepareShutdownAsync(); }
    public async Task PrepareShutdownAsync()
    {
        SelectChapterAsync = null; ConfirmUnknownSourceAsync = null; paidContext = null;
        Cancel();
        Task? drainProgress = null;
        lock (progressGate)
        {
            progressClosed = true;
            if (activeProgressCalls > 0)
            {
                progressDrained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                drainProgress = progressDrained.Task;
            }
        }
        await work.PauseAsync();
        if (drainProgress is not null) await drainProgress;
        bool disposeProgress = false;
        lock (progressGate)
        {
            if (!progressDisposed)
            {
                progressDisposed = true;
                disposeProgress = true;
            }
        }
        if (disposeProgress) progressSerial.Dispose();
        Interlocked.Exchange(ref request, null)?.Dispose();
    }
}
