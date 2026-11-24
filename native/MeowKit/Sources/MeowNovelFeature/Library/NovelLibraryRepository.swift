import Foundation
import MeowStorage

struct NovelBookStorageRecord: Equatable, Sendable {
    var id: String
    var installationId: String
    var bookUrl: String
    var identity: String
    var name: String
    var author: String
    var tocUrl: String
    var coverUrl: String
    var intro: String
    var latestChapterTitle: String
    var onShelf: Bool
    var pinnedAt: Date?
    var addedAt: Date
    var readAt: Date?
    var anchor: NovelAnchor?
    var variables: SQLiteTreeValue?
}

struct NovelLibraryStorageSnapshot: Equatable, Sendable {
    var entries: [NovelBookStorageRecord] = []
    var settings = NovelReadingSettings()
    var rules: [NovelRule] = []
}

/// Strongly-typed SQLite repository for iOS novel shelf, progress, reading settings and purification rules.
/// Zero `MeowRuntime` / `RuntimeValue` dependency; projection to `NovelBook` extensions lives in `NovelLibrary`.
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
        "foreground", "background", "auto_speed",
    ]
    private static let ruleColumns = [
        "rule_id", "position", "name", "rule_group", "pattern", "replacement",
        "is_regex", "is_enabled", "scope_title", "scope_content",
        "scope", "exclude_scope", "sort_order", "timeout_ms",
    ]
    private static let variableInsertSQL: String = {
        let cols = ["book_id"] + SQLiteValueTree.columns
        let placeholders = Array(repeating: "?", count: cols.count).joined(separator: ",")
        return "INSERT INTO novel_book_variables(\(cols.joined(separator: ","))) VALUES(\(placeholders))"
    }()

    static func loadSnapshot(_ db: SQLiteConnection) throws -> NovelLibraryStorageSnapshot {
        var result = NovelLibraryStorageSnapshot()

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
            return try decodeJoinedRecord(c, nodes: variablesByBook[bookID] ?? [])
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

    static func retain(_ incoming: NovelBookStorageRecord, shelf: Bool, validate: (NovelBookStorageRecord) -> Bool, _ db: SQLiteConnection) throws -> String {
        let existing = try findExisting(incoming, db)
        var next = incoming
        if let existing {
            next.id = existing.id
            next.onShelf = existing.onShelf || shelf
            next.pinnedAt = existing.pinnedAt
            next.addedAt = (!existing.onShelf && shelf) ? Date() : existing.addedAt
        } else {
            next.id = UUID().uuidString.lowercased()
            next.onShelf = shelf
            next.pinnedAt = nil
            next.addedAt = Date()
        }
        let inheritedProgress = try resolveConflicts(&next, preserveProgress: true, db)
        guard validate(next) else { throw NovelError.invalidInput("书籍数据无效") }
        try saveBook(next, db)
        if let inheritedProgress {
            let hasProgress = try db.queryOne("SELECT 1 FROM novel_progress WHERE book_id=?", [.text(next.id)], map: { _ in true }) == true
            if !hasProgress {
                try db.upsert(
                    "novel_progress",
                    columns: ["book_id", "chapter_url", "chapter_title", "chapter_index", "block_index", "utf16_offset", "ratio", "read_at_ms"],
                    values: [
                        .text(next.id), .text(inheritedProgress.anchor.chapterUrl), .text(inheritedProgress.anchor.chapterTitle),
                        .integer(inheritedProgress.anchor.chapterIndex), .integer(inheritedProgress.anchor.blockIndex),
                        .integer(inheritedProgress.anchor.utf16Offset), .real(inheritedProgress.anchor.ratio), .epochMillis(inheritedProgress.readAt),
                    ],
                    key: ["book_id"]
                )
            }
        }
        return next.id
    }

    static func setShelf(_ id: String, present: Bool, _ db: SQLiteConnection) throws {
        let changed: Int
        if present {
            changed = try db.execute(
                "UPDATE novel_books SET on_shelf=1, added_at_ms=CASE WHEN on_shelf=0 THEN ? ELSE added_at_ms END WHERE book_id=?",
                [.epochMillis(Date()), .text(id)]
            )
        } else {
            changed = try db.execute(
                "UPDATE novel_books SET on_shelf=0, pinned_at_ms=NULL WHERE book_id=?",
                [.text(id)]
            )
        }
        guard changed == 1 else { throw NovelError.invalidResponse }
        if !present {
            try pruneOrphanBook(id, db)
        }
    }

    static func record(_ incoming: NovelBookStorageRecord, anchor: NovelAnchor, validate: (NovelBookStorageRecord) -> Bool, _ db: SQLiteConnection) throws -> String {
        let existing = try findExisting(incoming, db)
        var next = incoming
        if let existing {
            next.id = existing.id
            next.onShelf = existing.onShelf
            next.pinnedAt = existing.pinnedAt
            next.addedAt = existing.addedAt
        } else {
            next.id = UUID(uuidString: incoming.id) != nil ? incoming.id : UUID().uuidString.lowercased()
            next.onShelf = false
            next.pinnedAt = nil
            next.addedAt = Date()
        }
        _ = try resolveConflicts(&next, preserveProgress: false, db)
        guard validate(next) else { throw NovelError.invalidInput("书籍数据无效") }
        try saveBook(next, db)
        try db.upsert(
            "novel_progress",
            columns: ["book_id", "chapter_url", "chapter_title", "chapter_index", "block_index", "utf16_offset", "ratio", "read_at_ms"],
            values: [
                .text(next.id), .text(anchor.chapterUrl), .text(anchor.chapterTitle),
                .integer(anchor.chapterIndex), .integer(anchor.blockIndex),
                .integer(anchor.utf16Offset), .real(anchor.ratio), .epochMillis(Date()),
            ],
            key: ["book_id"]
        )
        return next.id
    }

    private static func resolveConflicts(_ next: inout NovelBookStorageRecord, preserveProgress: Bool, _ db: SQLiteConnection) throws -> (anchor: NovelAnchor, readAt: Date)? {
        let conflicts = try db.query(
            "SELECT \(bookSelectColumns),p.chapter_url,p.chapter_title,p.chapter_index,p.block_index,p.utf16_offset,p.ratio,p.read_at_ms " +
            "FROM novel_books b LEFT JOIN novel_progress p ON p.book_id=b.book_id " +
            "WHERE b.book_id<>? AND ((b.installation_id=? AND b.book_url=?) OR (?<>'' AND b.identity_key=?)) " +
            "ORDER BY COALESCE(p.read_at_ms, 0) DESC, b.added_at_ms ASC, b.book_id ASC",
            [.text(next.id), .text(next.installationId), .text(next.bookUrl), .text(next.identity), .text(next.identity)]
        ) { c in
            try decodeJoinedRecord(c, nodes: [])
        }
        guard !conflicts.isEmpty else { return nil }
        var inherited: (anchor: NovelAnchor, readAt: Date)?
        for conflict in conflicts {
            if conflict.onShelf {
                if !next.onShelf {
                    next.onShelf = true
                    next.addedAt = conflict.addedAt
                } else {
                    next.addedAt = min(next.addedAt, conflict.addedAt)
                }
            }
            if next.onShelf {
                if let currentPin = next.pinnedAt, let conflictPin = conflict.pinnedAt {
                    next.pinnedAt = max(currentPin, conflictPin)
                } else {
                    next.pinnedAt = next.pinnedAt ?? conflict.pinnedAt
                }
            } else {
                next.pinnedAt = nil
            }
            if preserveProgress, inherited == nil, let anchor = conflict.anchor, let readAt = conflict.readAt {
                inherited = (anchor, readAt)
            }
            try db.execute("DELETE FROM novel_books WHERE book_id=?", [.text(conflict.id)])
        }
        return inherited
    }

    private static func findExisting(_ incoming: NovelBookStorageRecord, _ db: SQLiteConnection) throws -> NovelBookStorageRecord? {
        try db.queryOne(
            "SELECT \(bookSelectColumns) FROM novel_books b LEFT JOIN novel_progress p ON p.book_id=b.book_id " +
            "WHERE b.book_id=? OR (b.installation_id=? AND b.book_url=?) " +
            "OR (?<>'' AND b.identity_key=?) ORDER BY CASE WHEN b.book_id=? THEN 0 WHEN b.installation_id=? AND b.book_url=? THEN 1 ELSE 2 END, " +
            "b.on_shelf DESC, COALESCE(p.read_at_ms, 0) DESC, b.added_at_ms DESC, b.book_id ASC LIMIT 1",
            [
                .text(incoming.id), .text(incoming.installationId), .text(incoming.bookUrl),
                .text(incoming.identity), .text(incoming.identity),
                .text(incoming.id), .text(incoming.installationId), .text(incoming.bookUrl)
            ],
            map: { try decodeBookOnly($0) }
        )
    }

    private static func pruneOrphanBook(_ id: String, _ db: SQLiteConnection) throws {
        let keep = try db.queryOne(
            "SELECT CASE WHEN b.on_shelf = 1 OR EXISTS(SELECT 1 FROM novel_progress p WHERE p.book_id=b.book_id) THEN 1 ELSE 0 END " +
            "FROM novel_books b WHERE b.book_id=?",
            [.text(id)],
            map: { try $0.bool(0) }
        )
        if keep == false {
            try db.execute("DELETE FROM novel_books WHERE book_id=?", [.text(id)])
        }
    }

    static func saveSettings(_ settings: NovelReadingSettings, _ db: SQLiteConnection) throws {
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
        ]
        try db.upsert(
            "novel_reading_settings",
            columns: ["singleton"] + settingsColumns,
            values: values,
            key: ["singleton"]
        )
        if let blob = settings.backgroundImage {
            try db.upsert(
                "ios_novel_reading_background",
                columns: ["singleton", "background_image_blob"],
                values: [.integer(1), .blob(blob)],
                key: ["singleton"]
            )
        } else {
            try db.execute("DELETE FROM ios_novel_reading_background WHERE singleton=1")
        }
    }

    static func saveRules(_ rules: [NovelRule], previous: [NovelRule]?, _ db: SQLiteConnection) throws {
        let nextIDs = Set(rules.map(\.id))
        if let previous {
            var prevByID: [String: (index: Int, rule: NovelRule)] = [:]
            for (index, item) in previous.enumerated() { prevByID[item.id] = (index, item) }
            for oldID in prevByID.keys where !nextIDs.contains(oldID) {
                try db.execute("DELETE FROM novel_rules WHERE rule_id=?", [.text(oldID)])
            }
            for (index, rule) in rules.enumerated() {
                if let old = prevByID[rule.id], old.index == index, old.rule == rule { continue }
                try upsertRule(rule, index: index, db)
            }
        } else {
            if rules.isEmpty {
                try db.execute("DELETE FROM novel_rules")
            } else {
                let placeholders = Array(repeating: "?", count: rules.count).joined(separator: ",")
                try db.execute(
                    "DELETE FROM novel_rules WHERE rule_id NOT IN (\(placeholders))",
                    rules.map { .text($0.id) }
                )
                for (index, rule) in rules.enumerated() {
                    try upsertRule(rule, index: index, db)
                }
            }
        }
    }

    private static func upsertRule(_ rule: NovelRule, index: Int, _ db: SQLiteConnection) throws {
        try db.upsert(
            "novel_rules",
            columns: ruleColumns,
            values: [
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
            ],
            key: ["rule_id"]
        )
    }

    private static func loadSettings(_ db: SQLiteConnection) throws -> NovelReadingSettings? {
        try db.queryOne(
            "SELECT s.mode,s.font_size,s.font_weight,s.font_family,s.line_spacing,s.paragraph_spacing," +
            "s.margin,s.content_width,s.conversion,s.theme,s.foreground,s.background,s.auto_speed,b.background_image_blob " +
            "FROM novel_reading_settings s LEFT JOIN ios_novel_reading_background b ON b.singleton=s.singleton " +
            "WHERE s.singleton=1"
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

    private static func decodeBookOnly(_ c: SQLiteCursor) throws -> NovelBookStorageRecord {
        NovelBookStorageRecord(
            id: try c.text(0),
            installationId: try c.text(1),
            bookUrl: try c.text(2),
            identity: try c.text(3),
            name: try c.text(4),
            author: try c.text(5),
            tocUrl: try c.text(6),
            coverUrl: try c.text(7),
            intro: try c.text(8),
            latestChapterTitle: try c.text(9),
            onShelf: try c.bool(10),
            pinnedAt: try c.optionalDateFromMillis(11),
            addedAt: try c.dateFromMillis(12),
            readAt: nil,
            anchor: nil,
            variables: nil
        )
    }

    private static func decodeJoinedRecord(_ c: SQLiteCursor, nodes: [SQLiteTreeNodeRow]) throws -> NovelBookStorageRecord {
        var record = try decodeBookOnly(c)
        if !nodes.isEmpty {
            record.variables = try SQLiteValueTree.decode(nodes)
        }
        let readAt = try c.optionalDateFromMillis(19)
        record.readAt = readAt
        if readAt != nil {
            record.anchor = NovelAnchor(
                chapterUrl: try c.text(13),
                chapterTitle: try c.text(14),
                chapterIndex: try c.integer(15),
                blockIndex: try c.integer(16),
                utf16Offset: try c.integer(17),
                ratio: try c.double(18)
            )
        }
        return record
    }

    private static func saveBook(_ record: NovelBookStorageRecord, _ db: SQLiteConnection) throws {
        let values: [SQLiteValue] = [
            .text(record.id), .text(record.installationId), .text(record.bookUrl), .text(record.identity),
            .text(record.name), .text(record.author), .text(record.tocUrl), .text(record.coverUrl), .text(record.intro),
            .text(record.latestChapterTitle), .bool(record.onShelf),
            .optionalEpochMillis(record.pinnedAt), .epochMillis(record.addedAt),
        ]
        try db.upsert(
            "novel_books",
            columns: bookColumns,
            values: values,
            key: ["book_id"]
        )
        try db.execute("DELETE FROM novel_book_variables WHERE book_id=?", [.text(record.id)])
        if let variables = record.variables {
            let nodes = try SQLiteValueTree.rows(variables)
            for node in nodes {
                try db.execute(variableInsertSQL, [.text(record.id)] + node)
            }
        }
    }
}
