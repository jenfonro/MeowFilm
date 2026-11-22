import Foundation
import MeowStorage
import MeowRuntime

enum NovelLibraryRepository {
    private static let bookSelectColumns =
        "b.book_id,b.installation_id,b.book_url,b.identity_key,b.name,b.author," +
        "b.toc_url,b.cover_url,b.intro,b.latest_chapter_title,b.on_shelf,b.pinned_at_ms,b.added_at_ms"

    private static let bookColumns = [
        "book_id", "installation_id", "book_url", "identity_key", "name", "author",
        "toc_url", "cover_url", "intro", "latest_chapter_title", "on_shelf", "pinned_at_ms", "added_at_ms",
    ]
    private static let settingsColumns = [
        "mode", "font_size", "font_weight", "font_family", "line_spacing",
        "paragraph_spacing", "margin", "content_width", "conversion", "theme",
        "foreground", "background", "background_image_path", "background_image_blob", "auto_speed",
    ]
    private static let variableInsertSQL: String = {
        let cols = ["book_id"] + SQLiteValueTree.columns
        let placeholders = Array(repeating: "?", count: cols.count).joined(separator: ",")
        return "INSERT INTO novel_book_variables(\(cols.joined(separator: ","))) VALUES(\(placeholders))"
    }()

    static func loadSnapshot(_ db: SQLiteConnection) throws -> NovelLibrarySnapshot {
        var result = NovelLibrarySnapshot()

        var variablesByBook: [String: [SQLiteTreeNodeRow]] = [:]
        try db.scan(
            "SELECT book_id,node_id,parent_id,prop_key,item_index,kind,bool_value,int_value,real_value,text_value " +
            "FROM novel_book_variables ORDER BY book_id,node_id"
        ) { c in
            let bookID = try c.text(0)
            variablesByBook[bookID, default: []].append(try SQLiteValueTree.readNodeRow(c, offset: 1))
        }

        result.entries = try db.query(
            "SELECT \(bookSelectColumns),p.chapter_url,p.chapter_title,p.chapter_index,p.block_index,p.utf16_offset,p.ratio,p.read_at_ms " +
            "FROM novel_books b LEFT JOIN novel_progress p ON p.book_id=b.book_id ORDER BY b.added_at_ms,b.book_id"
        ) { c in
            let bookID = try c.text(0)
            return try decodeJoinedEntry(c, nodes: variablesByBook[bookID] ?? [])
        }

        if let settings = try loadSettings(db) {
            result.settings = settings
        }

        result.rules = try db.query(
            "SELECT rule_id,name,rule_group,is_enabled,sort_order,timeout_ms," +
            "pattern,replacement,is_regex,scope,exclude_scope,scope_title,scope_content " +
            "FROM novel_rules ORDER BY sort_order,position,rule_id"
        ) { cursor in
            var rule = NovelRule()
            rule.id = try cursor.text(0)
            rule.name = try cursor.text(1)
            rule.group = try cursor.text(2)
            rule.isEnabled = try cursor.bool(3)
            rule.order = try cursor.integer(4)
            rule.timeoutMillisecond = try cursor.integer(5)
            rule.pattern = try cursor.text(6)
            rule.replacement = try cursor.text(7)
            rule.isRegex = try cursor.bool(8)
            rule.scope = try cursor.text(9)
            rule.excludeScope = try cursor.text(10)
            rule.scopeTitle = try cursor.bool(11)
            rule.scopeContent = try cursor.bool(12)
            return rule
        }
        return result
    }

    static func retain(_ book: NovelBook, identity: String, shelf: Bool, validate: (NovelLibraryEntry) -> Bool, _ db: SQLiteConnection) throws -> NovelBook {
        let existing = try db.queryOne(
            "SELECT \(bookSelectColumns) FROM novel_books b WHERE b.book_id=? OR (b.installation_id=? AND b.book_url=?) " +
            "OR (?<>'' AND b.identity_key=?) ORDER BY CASE WHEN b.book_id=? THEN 0 WHEN b.installation_id=? AND b.book_url=? THEN 1 ELSE 2 END LIMIT 1",
            [
                .text(book.id), .text(book.installationId), .text(book.bookUrl),
                .text(identity), .text(identity),
                .text(book.id), .text(book.installationId), .text(book.bookUrl)
            ],
            map: { try decodeBookOnly($0) }
        )
        var local = book
        let current: NovelLibraryEntry
        if let existing {
            var entry = existing
            entry.book.storedExtensions = try loadBookVariables(entry.id, db)
            current = entry
            local.id = current.id
        } else {
            local.id = UUID().uuidString.lowercased()
            current = .init(book: local, identity: identity, onShelf: false, addedAt: Date())
        }
        var next = current; next.book = local; next.identity = identity
        if shelf { next.onShelf = true }
        guard validate(next) else { throw NovelError.corruptStorage }
        try saveBook(next, db)
        return local
    }

    static func setShelf(_ id: String, present: Bool, _ db: SQLiteConnection) throws {
        guard try db.execute(
            "UPDATE novel_books SET on_shelf=?, row_revision=row_revision+1 WHERE book_id=?",
            [.bool(present), .text(id)]
        ) == 1 else { throw NovelError.invalidResponse }
    }

    static func pin(_ id: String, _ db: SQLiteConnection) throws {
        guard try db.execute(
            "UPDATE novel_books SET pinned_at_ms=CASE WHEN pinned_at_ms IS NULL THEN ? ELSE NULL END, " +
            "row_revision=row_revision+1 WHERE book_id=?",
            [.epochMillis(Date()), .text(id)]
        ) == 1 else { throw NovelError.invalidResponse }
    }

    static func deleteHistory(_ id: String, _ db: SQLiteConnection) throws {
        guard try db.queryOne("SELECT 1 FROM novel_books WHERE book_id=?", [.text(id)], map: { _ in true }) == true else {
            throw NovelError.invalidResponse
        }
        try db.execute("DELETE FROM novel_progress WHERE book_id=?", [.text(id)])
    }

    static func record(_ book: NovelBook, identity: String, anchor: NovelAnchor, validate: (NovelLibraryEntry) -> Bool, _ db: SQLiteConnection) throws {
        guard var entry = try db.queryOne(
            "SELECT \(bookSelectColumns) FROM novel_books b WHERE b.book_id=?",
            [.text(book.id)],
            map: { try decodeBookOnly($0) }
        ) else {
            throw NovelError.invalidResponse
        }
        entry.book = book
        entry.identity = identity
        guard validate(entry) else { throw NovelError.corruptStorage }
        try saveBook(entry, db)
        try db.execute(
            "INSERT INTO novel_progress(book_id, chapter_url, chapter_title, chapter_index, block_index, utf16_offset, ratio, read_at_ms, row_revision) " +
            "VALUES(?,?,?,?,?,?,?,?,1) ON CONFLICT(book_id) DO UPDATE SET chapter_url=excluded.chapter_url, chapter_title=excluded.chapter_title, " +
            "chapter_index=excluded.chapter_index, block_index=excluded.block_index, utf16_offset=excluded.utf16_offset, ratio=excluded.ratio, " +
            "read_at_ms=excluded.read_at_ms, row_revision=novel_progress.row_revision+1",
            [
                .text(book.id), .text(anchor.chapterUrl), .text(anchor.chapterTitle),
                .integer(anchor.chapterIndex), .integer(anchor.blockIndex),
                .integer(anchor.utf16Offset), .real(anchor.ratio), .epochMillis(Date()),
            ]
        )
    }

    static func saveSettings(_ settings: NovelReadingSettings, _ db: SQLiteConnection) throws {
        let revision = try db.queryOne(
            "SELECT row_revision FROM novel_reading_settings WHERE singleton=1"
        ) { try $0.int64(0) } ?? 0
        guard revision < 9_007_199_254_740_991 else { throw SQLiteStorageError.conflict }
        let values: [SQLiteValue] = [
            .integer(1),
            .text(settings.mode.rawValue),
            .real(settings.fontSize),
            .real(settings.fontWeight),
            .text(settings.fontName),
            .real(settings.lineSpacing),
            .real(settings.paragraphSpacing),
            .real(settings.margin),
            .real(settings.contentWidth),
            .text(settings.conversion),
            .integer(settings.theme),
            .text(settings.foreground),
            .text(settings.background),
            .text(""),
            settings.backgroundImage.map(SQLiteValue.blob) ?? .null,
            .real(settings.automaticSpeed),
            .integer(revision + 1),
        ]
        try db.upsert(
            "novel_reading_settings",
            columns: ["singleton"] + settingsColumns + ["row_revision"],
            values: values,
            key: ["singleton"]
        )
    }

    static func saveRules(_ rules: [NovelRule], _ db: SQLiteConnection) throws {
        try db.execute("DELETE FROM novel_rules")
        for (index, rule) in rules.enumerated() {
            try db.execute(
                "INSERT INTO novel_rules(" +
                "rule_id,position,name,rule_group,pattern,replacement,is_regex,is_enabled," +
                "scope_title,scope_content,scope,exclude_scope,sort_order,timeout_ms" +
                ") VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                [
                    .text(rule.id),
                    .integer(index),
                    .text(rule.name),
                    .text(rule.group),
                    .text(rule.pattern),
                    .text(rule.replacement),
                    .bool(rule.isRegex),
                    .bool(rule.isEnabled),
                    .bool(rule.scopeTitle),
                    .bool(rule.scopeContent),
                    .text(rule.scope),
                    .text(rule.excludeScope),
                    .integer(rule.order),
                    .integer(rule.timeoutMillisecond),
                ]
            )
        }
    }

    private static func loadSettings(_ db: SQLiteConnection) throws -> NovelReadingSettings? {
        try db.queryOne(
            "SELECT mode,font_size,font_weight,font_family,line_spacing,paragraph_spacing," +
            "margin,content_width,conversion,theme,foreground,background,auto_speed,background_image_blob " +
            "FROM novel_reading_settings WHERE singleton=1"
        ) { cursor in
            guard let mode = NovelReadingMode(rawValue: try cursor.text(0)) else {
                throw NovelError.corruptStorage
            }
            var settings = NovelReadingSettings()
            settings.mode = mode
            settings.fontSize = try cursor.double(1)
            settings.fontWeight = try cursor.double(2)
            settings.fontName = try cursor.text(3)
            settings.lineSpacing = try cursor.double(4)
            settings.paragraphSpacing = try cursor.double(5)
            settings.margin = try cursor.double(6)
            settings.contentWidth = try cursor.double(7)
            settings.conversion = try cursor.text(8)
            settings.theme = try cursor.integer(9)
            settings.foreground = try cursor.text(10)
            settings.background = try cursor.text(11)
            settings.automaticSpeed = try cursor.double(12)
            settings.backgroundImage = try cursor.optionalBlob(13)
            return settings
        }
    }

    private static func loadBookVariables(_ bookID: String, _ db: SQLiteConnection) throws -> RuntimeObject {
        let rows = try db.query(
            "SELECT node_id,parent_id,prop_key,item_index,kind,bool_value,int_value,real_value,text_value " +
            "FROM novel_book_variables WHERE book_id=? ORDER BY node_id",
            [.text(bookID)]
        ) { try SQLiteValueTree.readNodeRow($0, offset: 0) }
        guard !rows.isEmpty else { return RuntimeObject() }
        guard case .object(let fields) = try RuntimeValue(storageTree: SQLiteValueTree.decode(rows)) else {
            throw NovelError.corruptStorage
        }
        return fields
    }

    private static func decodeBookOnly(_ c: SQLiteCursor) throws -> NovelLibraryEntry {
        let book = NovelBook(
            id: try c.text(0),
            installationId: try c.text(1),
            bookUrl: try c.text(2),
            name: try c.text(4),
            author: try c.text(5),
            tocUrl: try c.text(6),
            coverUrl: try c.text(7),
            intro: try c.text(8),
            latestChapterTitle: try c.text(9)
        )
        return NovelLibraryEntry(
            book: book,
            identity: try c.text(3),
            onShelf: try c.bool(10),
            pinnedAt: try c.optionalDateFromMillis(11),
            addedAt: try c.dateFromMillis(12),
            readAt: nil,
            anchor: nil
        )
    }

    private static func decodeJoinedEntry(_ c: SQLiteCursor, nodes: [SQLiteTreeNodeRow]) throws -> NovelLibraryEntry {
        var entry = try decodeBookOnly(c)
        if !nodes.isEmpty {
            guard case .object(let fields) = try RuntimeValue(storageTree: SQLiteValueTree.decode(nodes)) else {
                throw NovelError.corruptStorage
            }
            entry.book.storedExtensions = fields
        }
        let readAt = try c.optionalDateFromMillis(19)
        entry.readAt = readAt
        if readAt != nil {
            entry.anchor = NovelAnchor(
                chapterUrl: try c.text(13),
                chapterTitle: try c.text(14),
                chapterIndex: try c.integer(15),
                blockIndex: try c.integer(16),
                utf16Offset: try c.integer(17),
                ratio: try c.double(18)
            )
        }
        return entry
    }

    private static func saveBook(_ entry: NovelLibraryEntry, _ db: SQLiteConnection) throws {
        let book = entry.book
        let values: [SQLiteValue] = [
            .text(book.id), .text(book.installationId), .text(book.bookUrl), .text(entry.identity),
            .text(book.name), .text(book.author), .text(book.tocUrl), .text(book.coverUrl), .text(book.intro),
            .text(book.latestChapterTitle), .bool(entry.onShelf),
            .optionalEpochMillis(entry.pinnedAt), .epochMillis(entry.addedAt),
        ]
        let existingRevision = try db.queryOne(
            "SELECT row_revision FROM novel_books WHERE book_id=?",
            [.text(book.id)]
        ) { try $0.int64(0) }
        let revision = existingRevision ?? 1
        let changed = try db.upsert(
            "novel_books",
            columns: bookColumns + ["row_revision"],
            values: values + [.integer(revision)],
            key: ["book_id"]
        )
        if existingRevision != nil && changed > 0 {
            try db.execute("UPDATE novel_books SET row_revision=row_revision+1 WHERE book_id=?", [.text(book.id)])
        }
        try db.execute("DELETE FROM novel_book_variables WHERE book_id=?", [.text(book.id)])
        if !book.storedExtensions.isEmpty {
            let nodes = try SQLiteValueTree.rows(RuntimeValue.object(book.storedExtensions).storageTree)
            for node in nodes {
                try db.execute(variableInsertSQL, [.text(book.id)] + node)
            }
        }
    }
}
