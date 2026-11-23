using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

internal sealed record NovelBookRecord(
    string InstallationId,
    long Revision,
    string BookUrl,
    string Name,
    string Author,
    string CoverUrl,
    string Intro,
    string TocUrl,
    string LatestChapterTitle,
    StorageValue? Extras);

internal sealed record NovelEntryRecord(
    string BookId,
    string IdentityKey,
    NovelBookRecord Book,
    bool OnShelf,
    long? AddedAtMs,
    long? PinnedAtMs,
    long? ReadAtMs,
    NovelAnchor? Anchor,
    int ChapterCount,
    string ContentSignature);

internal static class NovelLibraryStore
{
    internal static (NovelEntryRecord[] Entries, NovelAppearance Appearance, NovelRule[] Rules) LoadAll(DatabaseSession db)
    {
        var extrasByBook = LoadAllBookVariables(db);
        var entries = db.Query("""
            SELECT b.book_id,b.identity_key,b.on_shelf,b.added_at_ms,b.pinned_at_ms,e.chapter_count,e.content_signature,
                   b.installation_id,e.book_revision,b.book_url,b.name,b.author,b.cover_url,b.intro,b.toc_url,b.latest_chapter_title,
                   p.chapter_url,p.chapter_title,p.chapter_index,p.block_index,p.utf16_offset,p.ratio,p.read_at_ms
            FROM novel_books b
            LEFT JOIN windows_novel_book_extras e ON e.book_id=b.book_id
            LEFT JOIN novel_progress p ON p.book_id=b.book_id
            ORDER BY e.position, b.book_id
            """, row => ReadEntry(row, extrasByBook.GetValueOrDefault(row.GetString(0)))).ToArray();

        var appearance = ReadAppearance(db) ?? new NovelAppearance();
        var rules = ReadRules(db);
        return (entries, appearance, rules);
    }

    internal static NovelEntryRecord? FindEntry(DatabaseSession db, string bookId)
    {
        var extras = TypedValueNodes.ReadObject(db, "novel_book_variables", [("book_id", bookId)]);
        return db.TryQuerySingle("""
            SELECT b.book_id,b.identity_key,b.on_shelf,b.added_at_ms,b.pinned_at_ms,e.chapter_count,e.content_signature,
                   b.installation_id,e.book_revision,b.book_url,b.name,b.author,b.cover_url,b.intro,b.toc_url,b.latest_chapter_title,
                   p.chapter_url,p.chapter_title,p.chapter_index,p.block_index,p.utf16_offset,p.ratio,p.read_at_ms
            FROM novel_books b
            LEFT JOIN windows_novel_book_extras e ON e.book_id=b.book_id
            LEFT JOIN novel_progress p ON p.book_id=b.book_id
            WHERE b.book_id=$id
            """, row => ReadEntry(row, extras), out var entry, ("$id", bookId))
            ? entry
            : null;
    }

    internal static void UpsertEntry(DatabaseSession db, NovelEntryRecord entry)
    {
        long position = db.TryQuerySingle(
            "SELECT position FROM windows_novel_book_extras WHERE book_id=$id",
            row => row.GetInt64(0), out var existingPos, ("$id", entry.BookId))
            ? existingPos
            : db.Scalar<long>("SELECT COALESCE(MAX(position) + 1, 0) FROM windows_novel_book_extras");

        db.Execute("""
            INSERT INTO novel_books(
                book_id,installation_id,book_url,identity_key,name,author,toc_url,cover_url,intro,latest_chapter_title,
                on_shelf,pinned_at_ms,added_at_ms)
            VALUES(
                $bookId,$inst,$bookUrl,$identity,$name,$author,$tocUrl,$coverUrl,$intro,$latest,
                $onShelf,$pinnedAt,$addedAt)
            ON CONFLICT(book_id) DO UPDATE SET
                installation_id=excluded.installation_id,
                book_url=excluded.book_url,
                identity_key=excluded.identity_key,
                name=excluded.name,
                author=excluded.author,
                toc_url=excluded.toc_url,
                cover_url=excluded.cover_url,
                intro=excluded.intro,
                latest_chapter_title=excluded.latest_chapter_title,
                on_shelf=excluded.on_shelf,
                pinned_at_ms=excluded.pinned_at_ms,
                added_at_ms=excluded.added_at_ms
            """,
            ("$bookId", entry.BookId),
            ("$inst", entry.Book.InstallationId),
            ("$bookUrl", entry.Book.BookUrl),
            ("$identity", entry.IdentityKey),
            ("$name", entry.Book.Name),
            ("$author", entry.Book.Author),
            ("$tocUrl", entry.Book.TocUrl),
            ("$coverUrl", entry.Book.CoverUrl),
            ("$intro", entry.Book.Intro),
            ("$latest", entry.Book.LatestChapterTitle),
            ("$onShelf", entry.OnShelf),
            ("$pinnedAt", entry.PinnedAtMs),
            ("$addedAt", entry.AddedAtMs ?? 0L));

        db.Execute("""
            INSERT INTO windows_novel_book_extras(book_id,position,chapter_count,content_signature,book_revision)
            VALUES($bookId,$pos,$chapterCount,$sig,$rev)
            ON CONFLICT(book_id) DO UPDATE SET
                chapter_count=excluded.chapter_count,
                content_signature=excluded.content_signature,
                book_revision=excluded.book_revision
            """,
            ("$bookId", entry.BookId),
            ("$pos", position),
            ("$chapterCount", entry.ChapterCount),
            ("$sig", entry.ContentSignature),
            ("$rev", entry.Book.Revision));

        if (entry.Anchor is null && entry.ReadAtMs is null)
        {
            db.Execute("DELETE FROM novel_progress WHERE book_id=$id", ("$id", entry.BookId));
        }
        else
        {
            var anchor = entry.Anchor ?? new NovelAnchor();
            db.Execute("""
                INSERT INTO novel_progress(
                    book_id,chapter_url,chapter_title,chapter_index,block_index,utf16_offset,ratio,read_at_ms)
                VALUES($bookId,$aUrl,$aTitle,$aIdx,$aBlk,$aOff,$aRatio,$readAt)
                ON CONFLICT(book_id) DO UPDATE SET
                    chapter_url=excluded.chapter_url,
                    chapter_title=excluded.chapter_title,
                    chapter_index=excluded.chapter_index,
                    block_index=excluded.block_index,
                    utf16_offset=excluded.utf16_offset,
                    ratio=excluded.ratio,
                    read_at_ms=excluded.read_at_ms
                """,
                ("$bookId", entry.BookId),
                ("$aUrl", anchor.ChapterUrl),
                ("$aTitle", anchor.ChapterTitle),
                ("$aIdx", anchor.ChapterIndex),
                ("$aBlk", anchor.BlockIndex),
                ("$aOff", anchor.Utf16Offset),
                ("$aRatio", anchor.Ratio),
                ("$readAt", entry.ReadAtMs ?? 0L));
        }

        TypedValueNodes.Replace(db, "novel_book_variables", [("book_id", entry.BookId)], entry.Book.Extras);
    }

    internal static bool DeleteEntry(DatabaseSession db, string bookId) =>
        db.Execute("DELETE FROM novel_books WHERE book_id=$id", ("$id", bookId)) > 0;

    internal static NovelAppearance? ReadAppearance(DatabaseSession db) =>
        db.TryQuerySingle("""
            SELECT s.mode,s.font_size,s.font_weight,s.font_family,s.line_spacing,s.paragraph_spacing,s.margin,s.content_width,
                   s.conversion,s.theme,s.foreground,s.background,COALESCE(bg.background_image_path,''),s.auto_speed
            FROM novel_reading_settings s
            LEFT JOIN windows_novel_reading_background bg ON bg.singleton=s.singleton
            WHERE s.singleton=1
            """, row => new NovelAppearance
            {
                Mode = row.GetString(0),
                FontSize = row.GetDouble(1),
                Bold = row.GetDouble(2) >= 600.0,
                FontFamily = row.GetString(3),
                LineHeight = row.GetDouble(4),
                ParagraphSpacing = row.GetDouble(5),
                Margin = row.GetDouble(6),
                ContentWidth = row.GetDouble(7),
                Conversion = row.GetString(8),
                Theme = row.GetInt32(9),
                Foreground = row.GetString(10),
                Background = row.GetString(11),
                BackgroundImage = row.GetString(12),
                AutoSpeed = row.GetDouble(13)
            }, out var value)
            ? value
            : null;

    internal static void SaveAppearance(DatabaseSession db, NovelAppearance value)
    {
        db.Execute("""
            INSERT INTO novel_reading_settings(
                singleton,mode,font_size,font_weight,font_family,line_spacing,paragraph_spacing,margin,content_width,
                conversion,theme,foreground,background,auto_speed)
            VALUES(
                1,$mode,$fontSize,$fontWeight,$fontFamily,$lineSpacing,$paragraphSpacing,$margin,$contentWidth,
                $conversion,$theme,$foreground,$background,$autoSpeed)
            ON CONFLICT(singleton) DO UPDATE SET
                mode=excluded.mode,
                font_size=excluded.font_size,
                font_weight=excluded.font_weight,
                font_family=excluded.font_family,
                line_spacing=excluded.line_spacing,
                paragraph_spacing=excluded.paragraph_spacing,
                margin=excluded.margin,
                content_width=excluded.content_width,
                conversion=excluded.conversion,
                theme=excluded.theme,
                foreground=excluded.foreground,
                background=excluded.background,
                auto_speed=excluded.auto_speed
            """,
            ("$mode", value.Mode),
            ("$fontSize", value.FontSize),
            ("$fontWeight", value.Bold ? 700.0 : 400.0),
            ("$fontFamily", value.FontFamily),
            ("$lineSpacing", value.LineHeight),
            ("$paragraphSpacing", value.ParagraphSpacing),
            ("$margin", value.Margin),
            ("$contentWidth", value.ContentWidth),
            ("$conversion", value.Conversion),
            ("$theme", value.Theme),
            ("$foreground", value.Foreground),
            ("$background", value.Background),
            ("$autoSpeed", value.AutoSpeed));
        if (string.IsNullOrEmpty(value.BackgroundImage))
        {
            db.Execute("DELETE FROM windows_novel_reading_background WHERE singleton=1");
        }
        else
        {
            db.Execute("""
                INSERT INTO windows_novel_reading_background(singleton,background_image_path)
                VALUES(1,$backgroundImage)
                ON CONFLICT(singleton) DO UPDATE SET background_image_path=excluded.background_image_path
                """,
                ("$backgroundImage", value.BackgroundImage));
        }
    }

    internal static NovelRule[] ReadRules(DatabaseSession db) =>
        db.Query("""
            SELECT rule_id,name,rule_group,pattern,replacement,is_regex,is_enabled,
                   scope_title,scope_content,scope,exclude_scope,sort_order,timeout_ms
            FROM novel_rules
            ORDER BY position, rule_id
            """, row => new NovelRule
            {
                Id = row.GetString(0),
                Name = row.GetString(1),
                Group = row.GetString(2),
                Pattern = row.GetString(3),
                Replacement = row.GetString(4),
                IsRegex = row.GetBool(5),
                Enabled = row.GetBool(6),
                ScopeTitle = row.GetBool(7),
                ScopeContent = row.GetBool(8),
                Scope = row.GetString(9),
                ExcludeScope = row.GetString(10),
                Order = row.GetInt64(11),
                TimeoutMillisecond = row.GetInt32(12)
            }).ToArray();

    internal static void ReplaceRules(DatabaseSession db, IReadOnlyList<NovelRule> previous, IReadOnlyList<NovelRule> rules)
    {
        var prevMap = previous.Select((r, i) => (r.Id, Pos: i, Rule: r))
            .ToDictionary(x => x.Id, x => (x.Pos, x.Rule), StringComparer.Ordinal);
        var nextIds = rules.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var removed in prevMap.Keys)
            if (!nextIds.Contains(removed))
                db.Execute("DELETE FROM novel_rules WHERE rule_id=$id", ("$id", removed));

        for (int i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (prevMap.TryGetValue(rule.Id, out var old) && old.Pos == i && RuleEquals(old.Rule, rule)) continue;
            db.Execute("""
                INSERT INTO novel_rules(
                    rule_id,position,name,rule_group,pattern,replacement,is_regex,is_enabled,
                    scope_title,scope_content,scope,exclude_scope,sort_order,timeout_ms)
                VALUES(
                    $id,$pos,$name,$grp,$pattern,$rep,$regex,$enabled,
                    $st,$sc,$scope,$ex,$ord,$timeout)
                ON CONFLICT(rule_id) DO UPDATE SET
                    position=excluded.position,
                    name=excluded.name,
                    rule_group=excluded.rule_group,
                    pattern=excluded.pattern,
                    replacement=excluded.replacement,
                    is_regex=excluded.is_regex,
                    is_enabled=excluded.is_enabled,
                    scope_title=excluded.scope_title,
                    scope_content=excluded.scope_content,
                    scope=excluded.scope,
                    exclude_scope=excluded.exclude_scope,
                    sort_order=excluded.sort_order,
                    timeout_ms=excluded.timeout_ms
                """,
                ("$id", rule.Id),
                ("$pos", i),
                ("$name", rule.Name),
                ("$grp", rule.Group),
                ("$pattern", rule.Pattern),
                ("$rep", rule.Replacement),
                ("$regex", rule.IsRegex),
                ("$enabled", rule.Enabled),
                ("$st", rule.ScopeTitle),
                ("$sc", rule.ScopeContent),
                ("$scope", rule.Scope),
                ("$ex", rule.ExcludeScope),
                ("$ord", rule.Order),
                ("$timeout", rule.TimeoutMillisecond));
        }
    }

    private static bool RuleEquals(NovelRule a, NovelRule b) =>
        a.Id == b.Id && a.Name == b.Name && a.Group == b.Group && a.Pattern == b.Pattern &&
        a.Replacement == b.Replacement && a.IsRegex == b.IsRegex && a.Enabled == b.Enabled &&
        a.ScopeTitle == b.ScopeTitle && a.ScopeContent == b.ScopeContent && a.Scope == b.Scope &&
        a.ExcludeScope == b.ExcludeScope && a.Order == b.Order && a.TimeoutMillisecond == b.TimeoutMillisecond;

    internal static bool EntryEquals(NovelEntryRecord? first, NovelEntryRecord? second)
    {
        if (ReferenceEquals(first, second)) return true;
        if (first is null || second is null) return false;
        return first.BookId == second.BookId &&
               first.IdentityKey == second.IdentityKey &&
               first.OnShelf == second.OnShelf &&
               first.AddedAtMs == second.AddedAtMs &&
               first.PinnedAtMs == second.PinnedAtMs &&
               first.ReadAtMs == second.ReadAtMs &&
               first.ChapterCount == second.ChapterCount &&
               first.ContentSignature == second.ContentSignature &&
               Equals(first.Anchor, second.Anchor) &&
               first.Book.InstallationId == second.Book.InstallationId &&
               first.Book.Revision == second.Book.Revision &&
               first.Book.BookUrl == second.Book.BookUrl &&
               first.Book.Name == second.Book.Name &&
               first.Book.Author == second.Book.Author &&
               first.Book.CoverUrl == second.Book.CoverUrl &&
               first.Book.Intro == second.Book.Intro &&
               first.Book.TocUrl == second.Book.TocUrl &&
               first.Book.LatestChapterTitle == second.Book.LatestChapterTitle &&
               StorageValue.StructuralEquals(first.Book.Extras, second.Book.Extras);
    }

    internal static bool RulesEqual(IReadOnlyList<NovelRule> first, IReadOnlyList<NovelRule> second)
    {
        if (first.Count != second.Count) return false;
        for (int i = 0; i < first.Count; i++)
            if (!RuleEquals(first[i], second[i])) return false;
        return true;
    }

    private static Dictionary<string, StorageValue> LoadAllBookVariables(DatabaseSession db)
    {
        var grouped = new Dictionary<string, List<TypedValueNodes.NodeRow>>(StringComparer.Ordinal);
        foreach (var item in db.Query("""
            SELECT book_id,node_id,parent_id,prop_key,item_index,kind,bool_value,int_value,real_value,text_value
            FROM novel_book_variables
            ORDER BY book_id, node_id
            """, row => (BookId: row.GetString(0), Node: TypedValueNodes.ReadRow(row, 1))))
        {
            if (!grouped.TryGetValue(item.BookId, out var list))
            {
                list = [];
                grouped[item.BookId] = list;
            }
            list.Add(item.Node);
        }
        var result = new Dictionary<string, StorageValue>(grouped.Count, StringComparer.Ordinal);
        foreach (var (bookId, rows) in grouped)
        {
            var decoded = TypedValueNodes.Decode(rows);
            if (decoded is null || decoded.Kind != StorageValueKind.Object)
                throw new StorageException(StorageError.Corrupt);
            result[bookId] = decoded;
        }
        return result;
    }

    private static NovelEntryRecord ReadEntry(Microsoft.Data.Sqlite.SqliteDataReader row, StorageValue? extras)
    {
        int? chapterCount = row.GetNullableInt32(5);
        string? contentSignature = row.GetNullableString(6);
        long? bookRevision = row.GetNullableInt64(8);
        if (chapterCount is null || contentSignature is null || bookRevision is null)
            throw new StorageException(StorageError.Corrupt);

        var book = new NovelBookRecord(
            row.GetString(7),
            bookRevision.Value,
            row.GetString(9),
            row.GetString(10),
            row.GetString(11),
            row.GetString(12),
            row.GetString(13),
            row.GetString(14),
            row.GetString(15),
            extras);

        long addedAtRaw = row.GetInt64(3);
        long? addedAtMs = addedAtRaw > 0 ? addedAtRaw : null;
        long? pinnedAtMs = row.GetNullableInt64(4);

        NovelAnchor? anchor = null;
        long? readAtMs = null;
        var aUrl = row.GetNullableString(16);
        var aTitle = row.GetNullableString(17);
        var aIdx = row.GetNullableInt32(18);
        var aBlk = row.GetNullableInt32(19);
        var aOff = row.GetNullableInt32(20);
        var aRatio = row.GetNullableDouble(21);
        var rawReadAt = row.GetNullableInt64(22);
        if (rawReadAt is not null || aUrl is not null)
        {
            if (aUrl is null || aTitle is null || aIdx is null || aBlk is null || aOff is null || aRatio is null || rawReadAt is null)
                throw new StorageException(StorageError.Corrupt);
            anchor = new NovelAnchor(aUrl, aTitle, aIdx.Value, aBlk.Value, aOff.Value, aRatio.Value);
            readAtMs = rawReadAt.Value > 0 ? rawReadAt.Value : null;
        }

        return new NovelEntryRecord(
            row.GetString(0),
            row.GetString(1),
            book,
            row.GetBool(2),
            addedAtMs,
            pinnedAtMs,
            readAtMs,
            anchor,
            chapterCount.Value,
            contentSignature);
    }
}
