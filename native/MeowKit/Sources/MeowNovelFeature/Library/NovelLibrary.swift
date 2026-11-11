import Foundation
import MeowStorage

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

/// One atomic document holds both memberships and the UUID/locator mapping.
/// Mutations commit synchronously inside the actor before publishing a snapshot;
/// failed writes keep the previous memory and bytes intact.
public actor NovelLibrary {
    public static let namespace = "novel.library.v1"
    private let store: DocumentStore
    private var saved = NovelLibrarySnapshot()
    private var bytes: Data?
    private var loaded = false
    private var closed = false
    public init(store: DocumentStore) { self.store = store }
    public func snapshot() throws -> NovelLibrarySnapshot { try prepare(); return saved }
    private func prepare() throws {
        guard !closed else { throw NovelError.closed }
        guard !loaded else { return }
        let value = try store.read(key: "library")
        if let value {
            let decoded: NovelLibrarySnapshot
            do { decoded = try JSONDecoder().decode(NovelLibrarySnapshot.self, from: value) }
            catch { throw NovelError.corruptStorage }
            guard Self.valid(decoded) else { throw NovelError.corruptStorage }
            saved = decoded
        }
        bytes = value; loaded = true
    }
    private func commit(_ value: NovelLibrarySnapshot) throws {
        guard Self.valid(value) else { throw NovelError.corruptStorage }
        let replacement = try JSONEncoder().encode(value)
        try store.compareAndSwap(key: "library", expected: bytes, replacement: replacement)
        bytes = replacement; saved = value
    }
    @discardableResult public func retain(_ book: NovelBook, identity: String, shelf: Bool = false) throws -> NovelBook {
        try prepare()
        var next = saved
        let index = next.entries.firstIndex { $0.book.id == book.id } ??
            next.entries.firstIndex { $0.book.installationId == book.installationId && $0.book.bookUrl == book.bookUrl } ??
            next.entries.firstIndex { !identity.isEmpty && $0.identity == identity }
        if let index {
            let id = next.entries[index].book.id
            var enriched = book; enriched.id = id
            next.entries[index].book = enriched; next.entries[index].identity = identity
            if shelf { next.entries[index].onShelf = true }
            try commit(next); return enriched
        }
        var local = book; local.id = UUID().uuidString.lowercased()
        next.entries.append(.init(book: local, identity: identity, onShelf: shelf, addedAt: Date()))
        try commit(next); return local
    }
    public func setShelf(_ id: String, present: Bool) throws { try change(id) { $0.onShelf = present } }
    public func pin(_ id: String) throws { try change(id) { $0.pinnedAt = $0.pinnedAt == nil ? Date() : nil } }
    public func deleteHistory(_ id: String) throws { try change(id) { $0.readAt = nil; $0.anchor = nil } }
    public func record(_ book: NovelBook, identity: String, anchor: NovelAnchor) throws {
        guard !anchor.chapterUrl.isEmpty, anchor.chapterIndex >= 0 else { throw NovelError.noChapter }
        try change(book.id) { $0.book = book; $0.identity = identity; $0.anchor = anchor; $0.readAt = Date() }
    }
    public func saveSettings(_ settings: NovelReadingSettings) throws {
        try prepare(); var next = saved; next.settings = settings; try commit(next)
    }
    public func saveRules(_ rules: [NovelRule]) throws {
        try prepare(); var next = saved; next.rules = rules; try commit(next)
    }
    private func change(_ id: String, mutation: (inout NovelLibraryEntry) -> Void) throws {
        try prepare(); var next = saved
        guard let index = next.entries.firstIndex(where: { $0.id == id }) else { throw NovelError.invalidResponse }
        mutation(&next.entries[index]); try commit(next)
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
    public func close() { closed = true }
}
