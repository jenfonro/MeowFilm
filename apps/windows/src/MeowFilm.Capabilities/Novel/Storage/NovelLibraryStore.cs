using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

internal static class NovelLibraryStore
{
    private static readonly HashSet<string> StandardBookFields = new(StringComparer.Ordinal)
    {
        "installationId", "revision", "bookUrl", "name", "author", "coverUrl", "intro", "tocUrl", "latestChapterTitle"
    };

    internal static (NovelEntry[] Entries, NovelAppearance Appearance, NovelRule[] Rules) LoadAll(DatabaseSession db)
    {
        var extrasByBook = LoadAllBookExtras(db);
        var entries = db.Query("""
            SELECT book_id,identity_key,on_shelf,added_at,pinned_at,read_at,chapter_count,content_signature,
                   installation_id,book_revision,book_url,name,author,cover_url,intro,toc_url,latest_chapter_title,
                   anchor_chapter_url,anchor_chapter_title,anchor_chapter_index,anchor_block_index,anchor_utf16_offset,anchor_ratio
            FROM windows_novel_entries
            ORDER BY position, book_id
            """, row => ReadEntry(row, extrasByBook.GetValueOrDefault(row.GetString(0)))).ToArray();

        var appearance = ReadAppearance(db) ?? new NovelAppearance();
        var rules = ReadRules(db);
        return (entries, appearance, rules);
    }

    internal static NovelEntry? FindEntry(DatabaseSession db, string bookId)
    {
        var extras = TypedValueNodes.ReadObject(db, "windows_novel_book_extras", [("book_id", bookId)]);
        return db.TryQuerySingle("""
            SELECT book_id,identity_key,on_shelf,added_at,pinned_at,read_at,chapter_count,content_signature,
                   installation_id,book_revision,book_url,name,author,cover_url,intro,toc_url,latest_chapter_title,
                   anchor_chapter_url,anchor_chapter_title,anchor_chapter_index,anchor_block_index,anchor_utf16_offset,anchor_ratio
            FROM windows_novel_entries
            WHERE book_id=$id
            """, row => ReadEntry(row, extras), out var entry, ("$id", bookId))
            ? entry
            : null;
    }

    internal static void UpsertEntry(DatabaseSession db, NovelEntry entry)
    {
        long position = db.TryQuerySingle(
            "SELECT position FROM windows_novel_entries WHERE book_id=$id",
            row => row.GetInt64(0), out var existingPos, ("$id", entry.BookId))
            ? existingPos
            : db.Scalar<long>("SELECT COALESCE(MAX(position) + 1, 0) FROM windows_novel_entries");

        db.Execute("""
            INSERT INTO windows_novel_entries(
                book_id,position,identity_key,on_shelf,added_at,pinned_at,read_at,chapter_count,content_signature,
                installation_id,book_revision,book_url,name,author,cover_url,intro,toc_url,latest_chapter_title,
                anchor_chapter_url,anchor_chapter_title,anchor_chapter_index,anchor_block_index,anchor_utf16_offset,anchor_ratio)
            VALUES(
                $bookId,$pos,$identity,$onShelf,$addedAt,$pinnedAt,$readAt,$chapterCount,$sig,
                $inst,$rev,$bookUrl,$name,$author,$coverUrl,$intro,$tocUrl,$latest,
                $aUrl,$aTitle,$aIdx,$aBlk,$aOff,$aRatio)
            ON CONFLICT(book_id) DO UPDATE SET
                identity_key=excluded.identity_key,
                on_shelf=excluded.on_shelf,
                added_at=excluded.added_at,
                pinned_at=excluded.pinned_at,
                read_at=excluded.read_at,
                chapter_count=excluded.chapter_count,
                content_signature=excluded.content_signature,
                installation_id=excluded.installation_id,
                book_revision=excluded.book_revision,
                book_url=excluded.book_url,
                name=excluded.name,
                author=excluded.author,
                cover_url=excluded.cover_url,
                intro=excluded.intro,
                toc_url=excluded.toc_url,
                latest_chapter_title=excluded.latest_chapter_title,
                anchor_chapter_url=excluded.anchor_chapter_url,
                anchor_chapter_title=excluded.anchor_chapter_title,
                anchor_chapter_index=excluded.anchor_chapter_index,
                anchor_block_index=excluded.anchor_block_index,
                anchor_utf16_offset=excluded.anchor_utf16_offset,
                anchor_ratio=excluded.anchor_ratio
            """,
            ("$bookId", entry.BookId),
            ("$pos", position),
            ("$identity", entry.IdentityKey),
            ("$onShelf", entry.OnShelf),
            ("$addedAt", FormatTimestamp(entry.AddedAt)),
            ("$pinnedAt", FormatTimestamp(entry.PinnedAt)),
            ("$readAt", FormatTimestamp(entry.ReadAt)),
            ("$chapterCount", entry.ChapterCount),
            ("$sig", entry.ContentSignature),
            ("$inst", entry.Book.InstallationId),
            ("$rev", entry.Book.Revision),
            ("$bookUrl", entry.Book.BookUrl),
            ("$name", entry.Book.Name),
            ("$author", entry.Book.Author),
            ("$coverUrl", entry.Book.CoverUrl),
            ("$intro", entry.Book.Intro),
            ("$tocUrl", entry.Book.TocUrl),
            ("$latest", entry.Book.LatestChapterTitle),
            ("$aUrl", entry.Anchor?.ChapterUrl),
            ("$aTitle", entry.Anchor?.ChapterTitle),
            ("$aIdx", entry.Anchor is { } a1 ? (long)a1.ChapterIndex : null),
            ("$aBlk", entry.Anchor is { } a2 ? (long)a2.BlockIndex : null),
            ("$aOff", entry.Anchor is { } a3 ? (long)a3.Utf16Offset : null),
            ("$aRatio", entry.Anchor?.Ratio));

        TypedValueNodes.Replace(db, "windows_novel_book_extras", [("book_id", entry.BookId)], ExtractBookExtras(entry.Book));
    }

    internal static void DeleteEntry(DatabaseSession db, string bookId) =>
        db.Execute("DELETE FROM windows_novel_entries WHERE book_id=$id", ("$id", bookId));

    internal static NovelAppearance? ReadAppearance(DatabaseSession db) =>
        db.TryQuerySingle("""
            SELECT mode,font_size,font_family,bold,line_height,paragraph_spacing,margin,content_width,
                   conversion,theme,background,foreground,background_image,auto_speed
            FROM windows_novel_appearance WHERE singleton=1
            """, row => new NovelAppearance
            {
                Mode = row.GetString(0),
                FontSize = row.GetDouble(1),
                FontFamily = row.GetString(2),
                Bold = row.GetBool(3),
                LineHeight = row.GetDouble(4),
                ParagraphSpacing = row.GetDouble(5),
                Margin = row.GetDouble(6),
                ContentWidth = row.GetDouble(7),
                Conversion = row.GetString(8),
                Theme = row.GetInt32(9),
                Background = row.GetString(10),
                Foreground = row.GetString(11),
                BackgroundImage = row.GetString(12),
                AutoSpeed = row.GetDouble(13)
            }, out var value)
            ? value
            : null;

    internal static void SaveAppearance(DatabaseSession db, NovelAppearance value) =>
        db.Execute("""
            INSERT INTO windows_novel_appearance(
                singleton,mode,font_size,font_family,bold,line_height,paragraph_spacing,margin,content_width,
                conversion,theme,background,foreground,background_image,auto_speed)
            VALUES(
                1,$mode,$fontSize,$fontFamily,$bold,$lineHeight,$paragraphSpacing,$margin,$contentWidth,
                $conversion,$theme,$background,$foreground,$backgroundImage,$autoSpeed)
            ON CONFLICT(singleton) DO UPDATE SET
                mode=excluded.mode,font_size=excluded.font_size,font_family=excluded.font_family,bold=excluded.bold,
                line_height=excluded.line_height,paragraph_spacing=excluded.paragraph_spacing,margin=excluded.margin,
                content_width=excluded.content_width,conversion=excluded.conversion,theme=excluded.theme,
                background=excluded.background,foreground=excluded.foreground,background_image=excluded.background_image,
                auto_speed=excluded.auto_speed
            """,
            ("$mode", value.Mode),
            ("$fontSize", value.FontSize),
            ("$fontFamily", value.FontFamily),
            ("$bold", value.Bold),
            ("$lineHeight", value.LineHeight),
            ("$paragraphSpacing", value.ParagraphSpacing),
            ("$margin", value.Margin),
            ("$contentWidth", value.ContentWidth),
            ("$conversion", value.Conversion),
            ("$theme", value.Theme),
            ("$background", value.Background),
            ("$foreground", value.Foreground),
            ("$backgroundImage", value.BackgroundImage),
            ("$autoSpeed", value.AutoSpeed));

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

    internal static void ReplaceRules(DatabaseSession db, IReadOnlyList<NovelRule> rules)
    {
        var existing = ReadRules(db);
        var prevMap = existing.Select((r, i) => (r.Id, Pos: i, Rule: r))
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

    internal static bool EntryEquals(NovelEntry? first, NovelEntry? second)
    {
        if (ReferenceEquals(first, second)) return true;
        if (first is null || second is null) return false;
        return first.BookId == second.BookId &&
               first.IdentityKey == second.IdentityKey &&
               first.OnShelf == second.OnShelf &&
               first.AddedAt == second.AddedAt &&
               first.PinnedAt == second.PinnedAt &&
               first.ReadAt == second.ReadAt &&
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
               JsonNode.DeepEquals(ExtractBookExtras(first.Book), ExtractBookExtras(second.Book));
    }

    internal static bool RulesEqual(IReadOnlyList<NovelRule> first, IReadOnlyList<NovelRule> second)
    {
        if (first.Count != second.Count) return false;
        for (int i = 0; i < first.Count; i++)
        {
            var a = first[i];
            var b = second[i];
            if (a.Id != b.Id || a.Name != b.Name || a.Group != b.Group || a.Pattern != b.Pattern ||
                a.Replacement != b.Replacement || a.IsRegex != b.IsRegex || a.Enabled != b.Enabled ||
                a.ScopeTitle != b.ScopeTitle || a.ScopeContent != b.ScopeContent || a.Scope != b.Scope ||
                a.ExcludeScope != b.ExcludeScope || a.Order != b.Order || a.TimeoutMillisecond != b.TimeoutMillisecond)
                return false;
        }
        return true;
    }

    private static Dictionary<string, JsonObject> LoadAllBookExtras(DatabaseSession db)
    {
        var grouped = new Dictionary<string, List<TypedValueNodes.NodeRow>>(StringComparer.Ordinal);
        foreach (var item in db.Query("""
            SELECT book_id,node_id,parent_id,prop_key,item_index,kind,bool_value,int_value,real_value,text_value
            FROM windows_novel_book_extras
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
        var result = new Dictionary<string, JsonObject>(grouped.Count, StringComparer.Ordinal);
        foreach (var (bookId, rows) in grouped)
            result[bookId] = TypedValueNodes.Decode(rows) as JsonObject
                ?? throw new DocumentStoreException(DocumentStoreError.Corrupt);
        return result;
    }

    private static NovelEntry ReadEntry(Microsoft.Data.Sqlite.SqliteDataReader row, JsonObject? extras)
    {
        var dataElement = extras is { Count: > 0 }
            ? JsonSerializer.SerializeToElement(extras, NovelJson.Options)
            : default;
        var book = new NovelBook(
            row.GetString(8),
            row.GetInt64(9),
            row.GetString(10),
            row.GetString(11),
            row.GetString(12),
            row.GetString(13),
            row.GetString(14),
            row.GetString(15),
            row.GetString(16),
            dataElement);

        NovelAnchor? anchor = null;
        var aUrl = row.GetNullableString(17);
        var aTitle = row.GetNullableString(18);
        var aIdx = row.GetNullableInt32(19);
        var aBlk = row.GetNullableInt32(20);
        var aOff = row.GetNullableInt32(21);
        var aRatio = row.GetNullableDouble(22);
        if (aUrl is not null || aTitle is not null || aIdx is not null || aBlk is not null || aOff is not null || aRatio is not null)
        {
            if (aUrl is null || aTitle is null || aIdx is null || aBlk is null || aOff is null || aRatio is null)
                throw new DocumentStoreException(DocumentStoreError.Corrupt);
            anchor = new NovelAnchor(aUrl, aTitle, aIdx.Value, aBlk.Value, aOff.Value, aRatio.Value);
        }

        return new NovelEntry(
            row.GetString(0),
            row.GetString(1),
            book,
            row.GetBool(2),
            ParseTimestamp(row.GetNullableString(3)),
            ParseTimestamp(row.GetNullableString(4)),
            ParseTimestamp(row.GetNullableString(5)),
            anchor,
            row.GetInt32(6),
            row.GetString(7));
    }

    private static JsonObject? ExtractBookExtras(NovelBook book)
    {
        if (book.Data.ValueKind != JsonValueKind.Object) return null;
        JsonObject? extras = null;
        foreach (var prop in book.Data.EnumerateObject())
        {
            if (StandardBookFields.Contains(prop.Name)) continue;
            extras ??= new JsonObject();
            extras[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
        }
        return extras;
    }

    private static string? FormatTimestamp(DateTimeOffset? value) =>
        value?.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseTimestamp(string? text)
    {
        if (text is null) return null;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
            throw new DocumentStoreException(DocumentStoreError.Corrupt);
        return value;
    }
}
