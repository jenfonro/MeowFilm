// SPDX-License-Identifier: GPL-3.0-only
import Foundation
#if os(iOS) || os(macOS)
import Combine

public struct SearchHistoryEntry: Codable, Equatable, Sendable, Identifiable {
    public let text: String
    // Match exact keyword identity; do not fold case or Unicode spelling.
    public var id: String { Data(text.utf8).base64EncodedString() }
    public init(text: String) { self.text = text }
}

/// Device-local keyword history, not music platform/account preferences.
/// Only this key is read/written; former development keys are not imported.
@MainActor public final class SearchHistoryStore: ObservableObject {
    public static let storageKey = "search.history"
    @Published public private(set) var entries: [SearchHistoryEntry] = []
    private let defaults: UserDefaults

    public init(defaults: UserDefaults = .standard) {
        self.defaults = defaults; reload()
    }
    public func reload() {
        guard let data = defaults.data(forKey: Self.storageKey),
              let saved = try? JSONDecoder().decode([SearchHistoryEntry].self, from: data) else {
            entries = []; return
        }
        var seen = Set<String>()
        entries = Array(saved.filter { !$0.text.isEmpty && seen.insert($0.id).inserted }.prefix(60))
    }
    public func record(_ keyword: String) {
        let keyword = keyword.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !keyword.isEmpty else { return }
        reload()
        let entry = SearchHistoryEntry(text: keyword)
        entries.removeAll { $0.id == entry.id }
        entries.insert(entry, at: 0)
        entries = Array(entries.prefix(60))
        save()
    }
    public func clear() { entries = []; save() }
    private func save() {
        if let data = try? JSONEncoder().encode(entries) { defaults.set(data, forKey: Self.storageKey) }
    }
}
#endif
