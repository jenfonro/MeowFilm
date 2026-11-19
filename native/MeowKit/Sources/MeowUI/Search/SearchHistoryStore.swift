// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowStorage
#if os(iOS) || os(macOS)
import Combine

public struct SearchHistoryEntry: Codable, Equatable, Sendable, Identifiable {
    public let text: String
    // Match exact keyword identity; do not fold case or Unicode spelling.
    public var id: String { Data(text.utf8).base64EncodedString() }
    public init(text: String) { self.text = text }
}

/// Device-local keyword history, not music platform/account preferences.
/// Its typed keyword rows are independent of platform search preferences.
@MainActor public final class SearchHistoryStore: ObservableObject {
    @Published public private(set) var entries: [SearchHistoryEntry] = []
    @Published public private(set) var persistenceError: String?
    private var database: SQLiteDatabase?
    private var epoch = UUID()
    private var closed = false

    public init(database: SQLiteDatabase? = nil) { self.database = database }
    public func bind(database: SQLiteDatabase) async {
        guard self.database == nil, !closed else { return }
        self.database = database; await reload()
    }
    nonisolated private static func load(_ db: SQLiteConnection) throws -> [SearchHistoryEntry] {
        try db.query("SELECT text FROM ios_search_history ORDER BY used_at DESC,text").map { .init(text: try $0.text("text")) }
    }
    public func reload() async {
        guard !closed, let database else { return }
        let ticket = UUID(); epoch = ticket
        do {
            let result = try await database.read(Self.load)
            guard epoch == ticket, !closed else { return }
            entries = result; persistenceError = nil
        } catch { if epoch == ticket, !closed { persistenceError = "搜索历史读取失败" } }
    }
    public func record(_ keyword: String) async {
        let keyword = keyword.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !closed, !keyword.isEmpty else { return }
        let ticket = UUID(); epoch = ticket
        do {
            let result: [SearchHistoryEntry]
            if let database {
                result = try await database.transaction(ownedBytes: keyword.utf8.count) { db in
                    try db.execute("INSERT INTO ios_search_history(text,used_at) VALUES(?,?) " +
                        "ON CONFLICT(text) DO UPDATE SET used_at=excluded.used_at", [.text(keyword), .optional(Date())])
                    return try Self.load(db)
                }
            } else {
                let entry = SearchHistoryEntry(text: keyword)
                result = [entry] + entries.filter { $0.id != entry.id }
            }
            if epoch == ticket, !closed { entries = result; persistenceError = nil }
        } catch { if epoch == ticket, !closed { persistenceError = "搜索历史保存失败" } }
    }
    public func clear() async {
        guard !closed else { return }
        let ticket = UUID(); epoch = ticket
        do {
            if let database { try await database.transaction { try $0.execute("DELETE FROM ios_search_history") } }
            if epoch == ticket, !closed { entries = []; persistenceError = nil }
        } catch { if epoch == ticket, !closed { persistenceError = "搜索历史清除失败" } }
    }
    public func flush() async throws {
        if let database { try await database.flush() }
    }
    public func close() async throws {
        closed = true; epoch = UUID()
        try await flush()
    }
}
#endif
