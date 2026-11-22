import Foundation
import MeowStorage
import MeowRuntime

enum NovelLibraryRepository {
    private static let bookColumns = [
        "book_id", "installation_id", "book_url", "identity", "name", "author",
        "toc_url", "cover_url", "intro", "latest_chapter_title", "on_shelf", "pinned_at", "added_at",
    ]
    private static let settingsColumns = [
        "mode", "font_size", "font_weight", "font_name", "line_spacing",
        "paragraph_spacing", "margin", "content_width", "conversion", "theme",
        "foreground", "background", "automatic_speed",
    ]
    private static let ruleColumns = [
        "rule_id", "position", "name", "rule_group", "pattern", "replacement",
        "is_regex", "is_enabled", "scope_title", "scope_content",
        "scope", "exclude_scope", "sort_order", "timeout_ms",
    ]

    static func loadSnapshot(_ db: SQLiteConnection) throws -> NovelLibrarySnapshot {
        var result = NovelLibrarySnapshot()
        let variables = try Dictionary(grouping: db.query("SELECT * FROM ios_novel_variable_nodes ORDER BY node_id")) {
            try $0.text("book_id")
        }
        result.entries = try db.query(
            "SELECT b.*, p.chapter_url, p.chapter_title, p.chapter_index, p.block_index, p.utf16_offset, p.ratio, p.read_at " +
            "FROM ios_novel_books b LEFT JOIN ios_novel_progress p ON p.book_id=b.book_id ORDER BY b.added_at, b.book_id"
        ).map { row in
            try decodeEntry(row, nodes: variables[try row.text("book_id")] ?? [])
        }
        if let settings = try loadSettings(db) {
            result.settings = settings
        }
        result.rules = try db.query(
            "SELECT rule_id, name, rule_group, is_enabled, sort_order, timeout_ms, " +
            "pattern, replacement, is_regex, scope, exclude_scope, scope_title, scope_content " +
            "FROM novel_rules ORDER BY sort_order, position, rule_id"
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
        let row = try db.query(
            "SELECT b.* FROM ios_novel_books b WHERE book_id=? OR (installation_id=? AND book_url=?) " +
            "OR (?<>'' AND identity=?) ORDER BY CASE WHEN book_id=? THEN 0 WHEN installation_id=? AND book_url=? THEN 1 ELSE 2 END LIMIT 1",
            [.text(book.id), .text(book.installationId), .text(book.bookUrl), .text(identity), .text(identity),
             .text(book.id), .text(book.installationId), .text(book.bookUrl)]
        ).first
        var local = book
        let current: NovelLibraryEntry
        if let row {
            current = try decodeEntry(row, db)
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
            "UPDATE ios_novel_books SET on_shelf=?, row_revision=row_revision+1 WHERE book_id=?",
            [.bool(present), .text(id)]
        ) == 1 else { throw NovelError.invalidResponse }
    }

    static func pin(_ id: String, _ db: SQLiteConnection) throws {
        guard try db.execute(
            "UPDATE ios_novel_books SET pinned_at=CASE WHEN pinned_at IS NULL THEN ? ELSE NULL END, " +
            "row_revision=row_revision+1 WHERE book_id=?",
            [.optional(Date()), .text(id)]
        ) == 1 else { throw NovelError.invalidResponse }
    }

    static func deleteHistory(_ id: String, _ db: SQLiteConnection) throws {
        guard try db.queryOne("SELECT 1 FROM ios_novel_books WHERE book_id=?", [.text(id)], map: { _ in true }) == true else {
            throw NovelError.invalidResponse
        }
        try db.execute("DELETE FROM ios_novel_progress WHERE book_id=?", [.text(id)])
    }

    static func record(_ book: NovelBook, identity: String, anchor: NovelAnchor, validate: (NovelLibraryEntry) -> Bool, _ db: SQLiteConnection) throws {
        guard let row = try db.query("SELECT * FROM ios_novel_books WHERE book_id=?", [.text(book.id)]).first else {
            throw NovelError.invalidResponse
        }
        var entry = try decodeEntry(row, db)
        entry.book = book
        entry.identity = identity
        guard validate(entry) else { throw NovelError.corruptStorage }
        try saveBook(entry, db)
        try db.execute(
            "INSERT INTO ios_novel_progress(book_id, chapter_url, chapter_title, chapter_index, block_index, utf16_offset, ratio, read_at, row_revision) " +
            "VALUES(?,?,?,?,?,?,?,?,1) ON CONFLICT(book_id) DO UPDATE SET chapter_url=excluded.chapter_url, chapter_title=excluded.chapter_title, " +
            "chapter_index=excluded.chapter_index, block_index=excluded.block_index, utf16_offset=excluded.utf16_offset, ratio=excluded.ratio, " +
            "read_at=excluded.read_at, row_revision=ios_novel_progress.row_revision+1",
            [
                .text(book.id), .text(anchor.chapterUrl), .text(anchor.chapterTitle),
                .integer(anchor.chapterIndex), .integer(anchor.blockIndex),
                .integer(anchor.utf16Offset), .real(anchor.ratio), .optional(Date()),
            ]
        )
    }

    static func saveSettings(_ settings: NovelReadingSettings, _ db: SQLiteConnection) throws {
        let revision = try db.queryOne(
            "SELECT row_revision FROM ios_novel_reading_settings WHERE singleton=1"
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
            .real(settings.automaticSpeed),
            settings.backgroundImage.map(SQLiteValue.blob) ?? .null,
            .integer(revision + 1),
        ]
        try db.upsert(
            "ios_novel_reading_settings",
            columns: ["singleton"] + settingsColumns + ["background_image", "row_revision"],
            values: values,
            key: ["singleton"]
        )
    }

    static func saveRules(_ rules: [NovelRule], _ db: SQLiteConnection) throws {
        let rows: [[SQLiteValue]] = rules.enumerated().map { index, rule in
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
        }
        try db.reconcile("novel_rules", columns: ruleColumns, key: ["rule_id"], rows: rows)
    }

    private static func loadSettings(_ db: SQLiteConnection) throws -> NovelReadingSettings? {
        try db.queryOne(
            "SELECT mode, font_size, font_weight, font_name, line_spacing, paragraph_spacing, " +
            "margin, content_width, conversion, theme, foreground, background, automatic_speed, background_image " +
            "FROM ios_novel_reading_settings WHERE singleton=1"
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

    private static func decodeEntry(_ row: SQLiteRow, _ db: SQLiteConnection) throws -> NovelLibraryEntry {
        try decodeEntry(
            row,
            nodes: db.query("SELECT * FROM ios_novel_variable_nodes WHERE book_id=? ORDER BY node_id", [.text(try row.text("book_id"))])
        )
    }

    private static func decodeEntry(_ row: SQLiteRow, nodes: [SQLiteRow]) throws -> NovelLibraryEntry {
        let id = try row.text("book_id")
        var book = NovelBook(
            id: id,
            installationId: try row.text("installation_id"),
            bookUrl: try row.text("book_url"),
            name: try row.text("name"),
            author: try row.text("author"),
            tocUrl: try row.text("toc_url"),
            coverUrl: try row.text("cover_url"),
            intro: try row.text("intro"),
            latestChapterTitle: try row.text("latest_chapter_title")
        )
        if !nodes.isEmpty {
            guard case .object(let fields) = try RuntimeValue(storageTree: SQLiteValueTree.value(nodes)) else {
                throw NovelError.corruptStorage
            }
            book.storedExtensions = fields
        }
        let readAt = try row.optionalDouble("read_at").map(Date.init(timeIntervalSince1970:))
        return .init(
            book: book,
            identity: try row.text("identity"),
            onShelf: try row.bool("on_shelf"),
            pinnedAt: try row.optionalDouble("pinned_at").map(Date.init(timeIntervalSince1970:)),
            addedAt: Date(timeIntervalSince1970: try row.double("added_at")),
            readAt: readAt,
            anchor: readAt == nil ? nil : NovelAnchor(
                chapterUrl: try row.text("chapter_url"),
                chapterTitle: try row.text("chapter_title"),
                chapterIndex: try row.integer("chapter_index"),
                blockIndex: try row.integer("block_index"),
                utf16Offset: try row.integer("utf16_offset"),
                ratio: try row.double("ratio")
            )
        )
    }

    private static func saveBook(_ entry: NovelLibraryEntry, _ db: SQLiteConnection) throws {
        let book = entry.book
        let values: [SQLiteValue] = [
            .text(book.id), .text(book.installationId), .text(book.bookUrl), .text(entry.identity),
            .text(book.name), .text(book.author), .text(book.tocUrl), .text(book.coverUrl), .text(book.intro),
            .text(book.latestChapterTitle), .bool(entry.onShelf), .optional(entry.pinnedAt), .optional(entry.addedAt),
        ]
        let existingRevision = try db.queryOne(
            "SELECT row_revision FROM ios_novel_books WHERE book_id=?",
            [.text(book.id)]
        ) { try $0.int64(0) }
        let revision = existingRevision ?? 1
        let changed = try db.upsert(
            "ios_novel_books",
            columns: bookColumns + ["row_revision"],
            values: values + [.integer(revision)],
            key: ["book_id"]
        )
        if existingRevision != nil && changed > 0 {
            try db.execute("UPDATE ios_novel_books SET row_revision=row_revision+1 WHERE book_id=?", [.text(book.id)])
        }
        let nodes = try SQLiteValueTree.rows(RuntimeValue.object(book.storedExtensions).storageTree)
        try db.reconcile(
            "ios_novel_variable_nodes",
            columns: ["book_id"] + SQLiteValueTree.columns,
            key: ["book_id", "node_id"],
            rows: nodes.map { [.text(book.id)] + $0 },
            where: "book_id=?",
            bindings: [.text(book.id)]
        )
    }
}
