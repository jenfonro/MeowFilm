import Foundation
import MeowDebug
import MeowStorage
import MeowRuntime

public struct NovelLibraryEntry: Codable, Equatable, Sendable, Identifiable {
    public var book: NovelBook
    public var identity: String
    public var onShelf: Bool
    public var pinnedAt: Date?
    public var addedAt: Date
    public var readAt: Date?
    public var anchor: NovelAnchor?
    public var id: String { book.id }
}
public struct NovelLibrarySnapshot: Codable, Equatable, Sendable {
    public var entries: [NovelLibraryEntry] = []
    public var settings = NovelReadingSettings()
    public var rules: [NovelRule] = []
    public init() {}
    public func books(history: Bool, query: String = "") -> [NovelLibraryEntry] {
        entries.filter { (history ? $0.readAt != nil && $0.anchor != nil : $0.onShelf) &&
            (query.isEmpty || [$0.book.name, $0.book.author, $0.anchor?.chapterTitle ?? ""].contains { $0.localizedCaseInsensitiveContains(query) })
        }.sorted {
            if !history, $0.pinnedAt != $1.pinnedAt { return ($0.pinnedAt ?? .distantPast) > ($1.pinnedAt ?? .distantPast) }
            return ($0.readAt ?? $0.addedAt) > ($1.readAt ?? $1.addedAt)
        }
    }
}

/// Books, memberships and reading positions share one native transaction owner.
/// A position update touches one book and one progress row, never the whole shelf.
public actor NovelLibrary {
    private let database: SQLiteDatabase
    private var closed = false
    private static let bookColumns = ["id", "installationId", "bookUrl", "identity", "name", "author",
                                      "tocUrl", "coverUrl", "intro", "latestChapterTitle", "onShelf", "pinnedAt", "addedAt"]
    private static let anchorColumns = ["chapterUrl", "chapterTitle", "chapterIndex", "blockIndex", "utf16Offset", "ratio"]
    private static let settingsColumns = ["mode", "fontSize", "fontWeight", "fontName", "lineSpacing",
        "paragraphSpacing", "margin", "contentWidth", "conversion", "theme", "foreground", "background", "automaticSpeed"]
    private static let ruleColumns = ["id", "name", "group", "isEnabled", "order", "timeoutMillisecond",
        "pattern", "replacement", "isRegex", "scope", "excludeScope", "scopeTitle", "scopeContent"]

    public init(database: SQLiteDatabase) { self.database = database }

    private func checkOpen() throws {
        guard !closed else { throw NovelError.closed }
    }
    public func snapshot() async throws -> NovelLibrarySnapshot {
        try checkOpen()
        return try await database.snapshot { db in
            var result = NovelLibrarySnapshot()
            let variables = try Dictionary(grouping: db.query("SELECT * FROM ios_novel_variable_nodes ORDER BY node_id")) {
                try $0.text("book_id")
            }
            result.entries = try db.query("SELECT b.*,p.chapterUrl,p.chapterTitle,p.chapterIndex,p.blockIndex,p.utf16Offset,p.ratio,p.readAt " +
                "FROM ios_novel_books b LEFT JOIN ios_novel_progress p ON p.book_id=b.id ORDER BY b.addedAt,b.id").map {
                    try Self.entry($0, nodes: variables[try $0.text("id")] ?? [])
                }
            if let row = try db.query("SELECT * FROM ios_novel_reading_settings WHERE singleton=1").first {
                result.settings = try Self.decodeSettings(row)
            }
            result.rules = try db.query("SELECT * FROM ios_novel_rules ORDER BY \"order\",id").map {
                try Self.decodeRule($0)
            }
            guard Self.valid(result) else { throw NovelError.corruptStorage }
            return result
        }
    }
    private static func entry(_ row: SQLiteRow, _ db: SQLiteConnection) throws -> NovelLibraryEntry {
        try entry(row, nodes: db.query("SELECT * FROM ios_novel_variable_nodes WHERE book_id=? ORDER BY node_id", [.text(try row.text("id"))]))
    }
    private static func entry(_ row: SQLiteRow, nodes: [SQLiteRow]) throws -> NovelLibraryEntry {
        let id = try row.text("id")
        var book = NovelBook(id: id, installationId: try row.text("installationId"), bookUrl: try row.text("bookUrl"),
            name: try row.text("name"), author: try row.text("author"), tocUrl: try row.text("tocUrl"),
            coverUrl: try row.text("coverUrl"), intro: try row.text("intro"), latestChapterTitle: try row.text("latestChapterTitle"))
        if !nodes.isEmpty {
            guard case .object(let fields) = try RuntimeValue(storageTree: SQLiteValueTree.value(nodes)) else {
                throw NovelError.corruptStorage
            }
            book.storedExtensions = fields
        }
        let readAt = try row.optionalDouble("readAt").map(Date.init(timeIntervalSince1970:))
        return .init(book: book, identity: try row.text("identity"), onShelf: try row.bool("onShelf"),
            pinnedAt: try row.optionalDouble("pinnedAt").map(Date.init(timeIntervalSince1970:)),
            addedAt: Date(timeIntervalSince1970: try row.double("addedAt")), readAt: readAt,
            anchor: readAt == nil ? nil : try decodeAnchor(row))
    }
    private static func decodeAnchor(_ row: SQLiteRow) throws -> NovelAnchor {
        NovelAnchor(
            chapterUrl: try row.text("chapterUrl"),
            chapterTitle: try row.text("chapterTitle"),
            chapterIndex: try row.integer("chapterIndex"),
            blockIndex: try row.integer("blockIndex"),
            utf16Offset: try row.integer("utf16Offset"),
            ratio: try row.double("ratio")
        )
    }
    private static func anchorValues(_ anchor: NovelAnchor) -> [SQLiteValue] {
        [
            .text(anchor.chapterUrl),
            .text(anchor.chapterTitle),
            .integer(anchor.chapterIndex),
            .integer(anchor.blockIndex),
            .integer(anchor.utf16Offset),
            .real(anchor.ratio)
        ]
    }
    private static func decodeSettings(_ row: SQLiteRow) throws -> NovelReadingSettings {
        guard let mode = NovelReadingMode(rawValue: try row.text("mode")) else {
            throw NovelError.corruptStorage
        }
        var settings = NovelReadingSettings()
        settings.mode = mode
        settings.fontSize = try row.double("fontSize")
        settings.fontWeight = try row.double("fontWeight")
        settings.fontName = try row.text("fontName")
        settings.lineSpacing = try row.double("lineSpacing")
        settings.paragraphSpacing = try row.double("paragraphSpacing")
        settings.margin = try row.double("margin")
        settings.contentWidth = try row.double("contentWidth")
        settings.conversion = try row.text("conversion")
        settings.theme = try row.integer("theme")
        settings.foreground = try row.text("foreground")
        settings.background = try row.text("background")
        settings.automaticSpeed = try row.double("automaticSpeed")
        switch row["background_image"] {
        case .blob(let image): settings.backgroundImage = image
        case .null: settings.backgroundImage = nil
        default: throw NovelError.corruptStorage
        }
        return settings
    }
    private static func settingsValues(_ settings: NovelReadingSettings) -> [SQLiteValue] {
        [
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
            .real(settings.automaticSpeed)
        ]
    }
    private static func decodeRule(_ row: SQLiteRow) throws -> NovelRule {
        var rule = NovelRule()
        rule.id = try row.text("id")
        rule.name = try row.text("name")
        rule.group = try row.text("group")
        rule.isEnabled = try row.bool("isEnabled")
        rule.order = try row.integer("order")
        rule.timeoutMillisecond = try row.integer("timeoutMillisecond")
        rule.pattern = try row.text("pattern")
        rule.replacement = try row.text("replacement")
        rule.isRegex = try row.bool("isRegex")
        rule.scope = try row.text("scope")
        rule.excludeScope = try row.text("excludeScope")
        rule.scopeTitle = try row.bool("scopeTitle")
        rule.scopeContent = try row.bool("scopeContent")
        return rule
    }
    private static func ruleValues(_ rule: NovelRule) -> [SQLiteValue] {
        [
            .text(rule.id),
            .text(rule.name),
            .text(rule.group),
            .bool(rule.isEnabled),
            .integer(rule.order),
            .integer(rule.timeoutMillisecond),
            .text(rule.pattern),
            .text(rule.replacement),
            .bool(rule.isRegex),
            .text(rule.scope),
            .text(rule.excludeScope),
            .bool(rule.scopeTitle),
            .bool(rule.scopeContent)
        ]
    }
    private static func saveBook(_ entry: NovelLibraryEntry, _ db: SQLiteConnection) throws {
        var value = NovelLibrarySnapshot(); value.entries = [entry]
        guard valid(value) else { throw NovelError.corruptStorage }
        let book = entry.book
        let values: [SQLiteValue] = [.text(book.id), .text(book.installationId), .text(book.bookUrl), .text(entry.identity),
            .text(book.name), .text(book.author), .text(book.tocUrl), .text(book.coverUrl), .text(book.intro),
            .text(book.latestChapterTitle), .bool(entry.onShelf), .optional(entry.pinnedAt), .optional(entry.addedAt)]
        let revision = try db.query("SELECT row_revision FROM ios_novel_books WHERE id=?", [.text(book.id)]).first?.integer("row_revision") ?? 1
        let changed = try db.upsert("ios_novel_books", columns: bookColumns + ["row_revision"], values: values + [.integer(revision)], key: ["id"])
        if changed > 0 {
            try db.execute("UPDATE ios_novel_books SET row_revision=row_revision+1 WHERE id=?", [.text(book.id)])
        }
        let nodes = try SQLiteValueTree.rows(RuntimeValue.object(book.storedExtensions).storageTree)
        try db.reconcile("ios_novel_variable_nodes", columns: ["book_id"] + SQLiteValueTree.columns,
            key: ["book_id", "node_id"], rows: nodes.map { [.text(book.id)] + $0 }, where: "book_id=?", bindings: [.text(book.id)])
    }
    @discardableResult public func retain(_ book: NovelBook, identity: String, shelf: Bool = false) async throws -> NovelBook {
        try checkOpen()
        return try await database.transaction { db in
            let row = try db.query("SELECT b.* FROM ios_novel_books b WHERE id=? OR (installationId=? AND bookUrl=?) " +
                "OR (?<>'' AND identity=?) ORDER BY CASE WHEN id=? THEN 0 WHEN installationId=? AND bookUrl=? THEN 1 ELSE 2 END LIMIT 1",
                [.text(book.id), .text(book.installationId), .text(book.bookUrl), .text(identity), .text(identity),
                 .text(book.id), .text(book.installationId), .text(book.bookUrl)]).first
            var local = book
            let current: NovelLibraryEntry
            if let row {
                current = try Self.entry(row, db)
                local.id = current.id
            } else {
                local.id = UUID().uuidString.lowercased()
                current = .init(book: local, identity: identity, onShelf: false, addedAt: Date())
            }
            var next = current; next.book = local; next.identity = identity
            if shelf { next.onShelf = true }
            try Self.saveBook(next, db)
            return local
        }
    }
    public func setShelf(_ id: String, present: Bool) async throws {
        try checkOpen()
        try await database.transaction { db in
            guard try db.execute("UPDATE ios_novel_books SET onShelf=?,row_revision=row_revision+1 WHERE id=?",
                [.bool(present), .text(id)]) == 1 else { throw NovelError.invalidResponse }
        }
        DebugRuntime.log(.novel, sub: "shelf", event: "set", level: .debug, data: .object([
            "bookId": .string(id),
            "present": .bool(present)
        ]))
    }
    public func pin(_ id: String) async throws {
        try checkOpen()
        try await database.transaction { db in
            guard try db.execute("UPDATE ios_novel_books SET pinnedAt=CASE WHEN pinnedAt IS NULL THEN ? ELSE NULL END," +
                "row_revision=row_revision+1 WHERE id=?", [.optional(Date()), .text(id)]) == 1 else { throw NovelError.invalidResponse }
        }
    }
    public func deleteHistory(_ id: String) async throws {
        try checkOpen()
        try await database.transaction { db in
            guard try !db.query("SELECT id FROM ios_novel_books WHERE id=?", [.text(id)]).isEmpty else { throw NovelError.invalidResponse }
            try db.execute("DELETE FROM ios_novel_progress WHERE book_id=?", [.text(id)])
        }
    }
    public func record(_ book: NovelBook, identity: String, anchor: NovelAnchor) async throws {
        try checkOpen()
        guard !anchor.chapterUrl.isEmpty, anchor.chapterIndex >= 0, anchor.blockIndex >= 0,
              anchor.utf16Offset >= 0, anchor.ratio.isFinite, (0...1).contains(anchor.ratio) else { throw NovelError.noChapter }
        try await database.transaction { db in
            guard let row = try db.query("SELECT * FROM ios_novel_books WHERE id=?", [.text(book.id)]).first else { throw NovelError.invalidResponse }
            var entry = try Self.entry(row, db); entry.book = book; entry.identity = identity
            try Self.saveBook(entry, db)
            try db.execute("INSERT INTO ios_novel_progress(book_id,chapterUrl,chapterTitle,chapterIndex,blockIndex,utf16Offset,ratio,readAt,row_revision) " +
                "VALUES(?,?,?,?,?,?,?,?,1) ON CONFLICT(book_id) DO UPDATE SET chapterUrl=excluded.chapterUrl,chapterTitle=excluded.chapterTitle," +
                "chapterIndex=excluded.chapterIndex,blockIndex=excluded.blockIndex,utf16Offset=excluded.utf16Offset,ratio=excluded.ratio," +
                "readAt=excluded.readAt,row_revision=ios_novel_progress.row_revision+1",
                [.text(book.id)] + Self.anchorValues(anchor) + [.optional(Date())])
        }
    }
    public func saveSettings(_ settings: NovelReadingSettings) async throws {
        try checkOpen()
        var value = NovelLibrarySnapshot(); value.settings = settings
        guard Self.valid(value) else { throw NovelError.corruptStorage }
        let fields = Self.settingsValues(settings)
        try await database.transaction(ownedBytes: settings.backgroundImage?.count ?? 0) { db in
            let revision = try db.query("SELECT row_revision FROM ios_novel_reading_settings WHERE singleton=1").first?.integer("row_revision") ?? 0
            try db.upsert("ios_novel_reading_settings", columns: ["singleton"] + Self.settingsColumns + ["background_image", "row_revision"],
                values: [.integer(1)] + fields + [settings.backgroundImage.map(SQLiteValue.blob) ?? .null, .integer(revision + 1)], key: ["singleton"])
        }
    }
    public func saveRules(_ rules: [NovelRule]) async throws {
        try checkOpen()
        var value = NovelLibrarySnapshot(); value.rules = rules
        guard Self.valid(value) else { throw NovelError.corruptStorage }
        let rows = rules.map(Self.ruleValues)
        try await database.transaction { db in
            try db.reconcile("ios_novel_rules", columns: Self.ruleColumns, key: ["id"], rows: rows)
        }
    }
    private static func valid(_ value: NovelLibrarySnapshot) -> Bool {
        let settings = value.settings
        func color(_ text: String) -> Bool {
            text.isEmpty || text.count == 7 && text.first == "#" && UInt64(text.dropFirst(), radix: 16) != nil
        }
        guard settings.fontSize.isFinite, settings.fontSize > 0,
              [0, 0.35].contains(settings.fontWeight),
              [settings.lineSpacing, settings.paragraphSpacing, settings.margin].allSatisfy({ $0.isFinite && $0 >= 0 }),
              settings.contentWidth.isFinite, settings.contentWidth > 0,
              settings.automaticSpeed.isFinite, (1...10).contains(settings.automaticSpeed),
              NovelReadingSettings.themes.indices.contains(settings.theme),
              ["none", "simplified", "traditional"].contains(settings.conversion),
              color(settings.foreground), color(settings.background),
              Set(value.entries.map(\.id)).count == value.entries.count,
              Set(value.rules.map(\.id)).count == value.rules.count,
              value.rules.allSatisfy({ !$0.id.isEmpty && !$0.pattern.isEmpty && $0.timeoutMillisecond > 0 }) else { return false }
        return value.entries.allSatisfy { entry in
            guard !entry.id.isEmpty, !entry.book.installationId.isEmpty, !entry.book.bookUrl.isEmpty else { return false }
            guard let anchor = entry.anchor else { return entry.readAt == nil }
            return !anchor.chapterUrl.isEmpty && anchor.chapterIndex >= 0 && anchor.blockIndex >= 0 &&
                anchor.utf16Offset >= 0 && anchor.ratio.isFinite && (0...1).contains(anchor.ratio)
        }
    }
    public func close() async {
        closed = true
        _ = try? await database.flush()
    }
}
