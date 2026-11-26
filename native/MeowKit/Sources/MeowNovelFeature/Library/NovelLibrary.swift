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
}

/// Books, memberships and reading positions share one native transaction owner.
/// A position update touches one book and one progress row, never the whole shelf.
public actor NovelLibrary {
    private let database: SQLiteDatabase
    private var cachedRules: [NovelRule]?
    private var closed = false

    public init(database: SQLiteDatabase) { self.database = database }

    private func checkOpen() throws {
        guard !closed else { throw NovelError.closed }
    }
    public func snapshot() async throws -> NovelLibrarySnapshot {
        try checkOpen()
        let raw = try await database.snapshot { try NovelLibraryRepository.loadSnapshot($0) }
        let result = try Self.projectSnapshot(raw)
        cachedRules = result.rules
        return result
    }
    @discardableResult public func retain(_ book: NovelBook, identity: String, shelf: Bool = false) async throws -> NovelBook {
        try checkOpen()
        guard !book.installationId.isEmpty, !book.bookUrl.isEmpty else {
            throw NovelError.invalidInput("书籍数据无效")
        }
        let record = Self.makeStorageRecord(book, identity: identity, onShelf: shelf)
        let savedID = try await database.transaction { db in
            try NovelLibraryRepository.retain(record, shelf: shelf, db)
        }
        var updated = book
        updated.id = savedID
        return updated
    }
    public func setShelf(_ id: String, present: Bool) async throws {
        try checkOpen()
        try await database.transaction { db in
            try NovelLibraryRepository.setShelf(id, present: present, db)
        }
        DebugRuntime.log(.novel, sub: "shelf", event: "set", level: .debug, data: .object([
            "bookId": .string(id),
            "present": .bool(present)
        ]))
    }
    public func clearHistory() async throws {
        try checkOpen()
        try await database.transaction { db in
            try NovelLibraryRepository.clearHistory(db)
        }
        DebugRuntime.log(.novel, sub: "history", event: "clear", level: .debug)
    }
    @discardableResult
    public func record(_ book: NovelBook, identity: String, anchor: NovelAnchor) async throws -> String {
        try checkOpen()
        guard !anchor.chapterUrl.isEmpty, anchor.chapterIndex >= 0, anchor.blockIndex >= 0,
              anchor.utf16Offset >= 0, anchor.ratio.isFinite, (0...1).contains(anchor.ratio) else { throw NovelError.noChapter }
        guard !book.installationId.isEmpty, !book.bookUrl.isEmpty else {
            throw NovelError.invalidInput("书籍数据无效")
        }
        let record = Self.makeStorageRecord(book, identity: identity, onShelf: false)
        return try await database.transaction { db in
            try NovelLibraryRepository.record(record, anchor: anchor, db)
        }
    }
    public func saveSettings(_ settings: NovelReadingSettings) async throws {
        try checkOpen()
        guard Self.validSettings(settings) else { throw NovelError.invalidInput("阅读排版设置无效") }
        try await database.transaction { db in
            try NovelLibraryRepository.saveSettings(settings, db)
        }
    }
    public func saveRules(_ rules: [NovelRule]) async throws {
        try checkOpen()
        if rules.contains(where: { $0.id.isEmpty }) { throw NovelError.invalidInput("替换规则标识不能为空") }
        if Set(rules.map(\.id)).count != rules.count { throw NovelError.invalidInput("替换规则标识重复") }
        if rules.contains(where: { $0.pattern.isEmpty }) { throw NovelError.invalidInput("替换规则不能为空") }
        if rules.contains(where: { $0.timeoutMillisecond <= 0 }) { throw NovelError.invalidInput("超时毫秒数无效") }
        let previous = cachedRules
        try await database.transaction { db in
            try NovelLibraryRepository.saveRules(rules, previous: previous, db)
        }
        cachedRules = rules
    }
    public static func find(in entries: [NovelLibraryEntry], book: NovelBook, identity: String) -> NovelLibraryEntry? {
        if let byID = entries.first(where: { $0.id == book.id }) { return byID }
        let bySource = entries.filter { $0.book.installationId == book.installationId && $0.book.bookUrl == book.bookUrl }
        if let preferred = preferredEntry(bySource) { return preferred }
        guard !identity.isEmpty else { return nil }
        let byIdentity = entries.filter { $0.identity == identity }
        return preferredEntry(byIdentity)
    }
    private static func preferredEntry(_ matches: [NovelLibraryEntry]) -> NovelLibraryEntry? {
        guard matches.count > 1 else { return matches.first }
        return matches.sorted { lhs, rhs in
            if lhs.onShelf != rhs.onShelf { return lhs.onShelf }
            let lhsRead = lhs.readAt ?? .distantPast
            let rhsRead = rhs.readAt ?? .distantPast
            if lhsRead != rhsRead { return lhsRead > rhsRead }
            if lhs.addedAt != rhs.addedAt { return lhs.addedAt > rhs.addedAt }
            return lhs.id < rhs.id
        }.first
    }

    // MARK: - Domain <-> Repository Projection

    private static func projectSnapshot(_ raw: NovelLibraryStorageSnapshot) throws -> NovelLibrarySnapshot {
        var result = NovelLibrarySnapshot()
        result.entries = try raw.entries.map(projectEntry)
        result.settings = raw.settings
        result.rules = raw.rules
        return result
    }

    private static func projectEntry(_ record: NovelBookStorageRecord) throws -> NovelLibraryEntry {
        var book = NovelBook(
            id: record.id,
            installationId: record.installationId,
            bookUrl: record.bookUrl,
            name: record.name,
            author: record.author,
            tocUrl: record.tocUrl,
            coverUrl: record.coverUrl,
            intro: record.intro,
            latestChapterTitle: record.latestChapterTitle
        )
        if let tree = record.variables {
            guard case .object(let fields) = try RuntimeValue(storageTree: tree) else {
                throw NovelError.corruptStorage
            }
            book.storedExtensions = fields
        }
        return NovelLibraryEntry(
            book: book,
            identity: record.identity,
            onShelf: record.onShelf,
            pinnedAt: record.pinnedAt,
            addedAt: record.addedAt,
            readAt: record.readAt,
            anchor: record.anchor
        )
    }

    private static func makeStorageRecord(_ book: NovelBook, identity: String, onShelf: Bool) -> NovelBookStorageRecord {
        let variables: SQLiteTreeValue? = book.storedExtensions.isEmpty
            ? nil
            : RuntimeValue.object(book.storedExtensions).storageTree
        return NovelBookStorageRecord(
            id: book.id,
            installationId: book.installationId,
            bookUrl: book.bookUrl,
            identity: identity,
            name: book.name,
            author: book.author,
            tocUrl: book.tocUrl,
            coverUrl: book.coverUrl,
            intro: book.intro,
            latestChapterTitle: book.latestChapterTitle,
            onShelf: onShelf,
            pinnedAt: nil,
            addedAt: Date(),
            readAt: nil,
            anchor: nil,
            variables: variables
        )
    }

    private static func validSettings(_ settings: NovelReadingSettings) -> Bool {
        func color(_ text: String) -> Bool {
            text.isEmpty || text.count == 7 && text.first == "#" && UInt64(text.dropFirst(), radix: 16) != nil
        }
        return settings.fontSize.isFinite && settings.fontSize > 0 &&
            [0, 0.35].contains(settings.fontWeight) &&
            [settings.lineSpacing, settings.paragraphSpacing, settings.margin].allSatisfy({ $0.isFinite && $0 >= 0 }) &&
            settings.contentWidth.isFinite && settings.contentWidth > 0 &&
            settings.automaticSpeed.isFinite && (1...10).contains(settings.automaticSpeed) &&
            NovelReadingSettings.themes.indices.contains(settings.theme) &&
            ["none", "simplified", "traditional"].contains(settings.conversion) &&
            color(settings.foreground) && color(settings.background)
    }
    public func close() async {
        closed = true
        do {
            try await database.flush()
        } catch {
            DebugRuntime.logError(.novel, sub: "library", event: "close.flush_failed", error: error, level: .warn)
        }
    }
}
