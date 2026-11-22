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

    public init(database: SQLiteDatabase) { self.database = database }

    private func checkOpen() throws {
        guard !closed else { throw NovelError.closed }
    }
    public func snapshot() async throws -> NovelLibrarySnapshot {
        try checkOpen()
        return try await database.snapshot { db in
            let result = try NovelLibraryRepository.loadSnapshot(db)
            guard Self.valid(result) else { throw NovelError.corruptStorage }
            return result
        }
    }
    @discardableResult public func retain(_ book: NovelBook, identity: String, shelf: Bool = false) async throws -> NovelBook {
        try checkOpen()
        return try await database.transaction { db in
            try NovelLibraryRepository.retain(book, identity: identity, shelf: shelf, validate: { entry in
                var value = NovelLibrarySnapshot(); value.entries = [entry]
                return Self.valid(value)
            }, db)
        }
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
    public func pin(_ id: String) async throws {
        try checkOpen()
        try await database.transaction { db in
            try NovelLibraryRepository.pin(id, db)
        }
    }
    public func deleteHistory(_ id: String) async throws {
        try checkOpen()
        try await database.transaction { db in
            try NovelLibraryRepository.deleteHistory(id, db)
        }
    }
    public func record(_ book: NovelBook, identity: String, anchor: NovelAnchor) async throws {
        try checkOpen()
        guard !anchor.chapterUrl.isEmpty, anchor.chapterIndex >= 0, anchor.blockIndex >= 0,
              anchor.utf16Offset >= 0, anchor.ratio.isFinite, (0...1).contains(anchor.ratio) else { throw NovelError.noChapter }
        try await database.transaction { db in
            try NovelLibraryRepository.record(book, identity: identity, anchor: anchor, validate: { entry in
                var value = NovelLibrarySnapshot(); value.entries = [entry]
                return Self.valid(value)
            }, db)
        }
    }
    public func saveSettings(_ settings: NovelReadingSettings) async throws {
        try checkOpen()
        var value = NovelLibrarySnapshot(); value.settings = settings
        guard Self.valid(value) else { throw NovelError.corruptStorage }
        try await database.transaction(ownedBytes: settings.backgroundImage?.count ?? 0) { db in
            try NovelLibraryRepository.saveSettings(settings, db)
        }
    }
    public func saveRules(_ rules: [NovelRule]) async throws {
        try checkOpen()
        var value = NovelLibrarySnapshot(); value.rules = rules
        guard Self.valid(value) else { throw NovelError.corruptStorage }
        try await database.transaction { db in
            try NovelLibraryRepository.saveRules(rules, db)
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
