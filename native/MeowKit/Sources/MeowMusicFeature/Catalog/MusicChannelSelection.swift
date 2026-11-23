import Foundation
import Combine
import MeowStorage

/// Music channel selection and restoration only. Opening an app page is
/// an explicit application action, never a side effect of catalogue loading.
@MainActor public final class MusicChannelSelection: ObservableObject {
    @Published public private(set) var channel: MusicPublicBrowseChannel?
    @Published public private(set) var persistenceError: String?
    private var database: SQLiteDatabase?
    private var remembered: String?
    private var pending: String?
    private var writer: Task<Void, Never>?
    private var closed = false
    public init() {}
    public func bind(database: SQLiteDatabase) async throws {
        guard self.database == nil, !closed else { return }
        let saved = try await database.read { try MusicChannelSelectionRepository.load($0) }
        guard !closed else { return }
        self.database = database
        if remembered == nil { remembered = saved }
        persist()
    }
    private func remember(_ value: String) {
        guard !closed, value != remembered else { return }
        remembered = value; pending = value; persist()
    }
    private func persist() {
        guard writer == nil, let database, pending != nil else { return }
        writer = Task {
            while let next = pending {
                pending = nil
                do {
                    try await database.transaction { db in
                        try MusicChannelSelectionRepository.save(next, db)
                    }
                    persistenceError = nil
                } catch { persistenceError = "频道选择保存失败" }
            }
            writer = nil
        }
    }

    /// browse.js openChannel/rememberChannel: channels are a primary page,
    /// not pushed detail pages and not another product navigation stack.
    public func selectChannel(_ value: MusicPublicBrowseChannel) {
        channel = value
        remember(value.identity)
    }
    public func restoreChannel(from catalogue: [MusicPublicBrowseChannel], catalogueLoaded: Bool) {
        guard !catalogue.isEmpty else { return }
        if let channel, !catalogue.contains(where: { $0.platform == channel.platform }) {
            // The directory changed source. Retire the in-memory choice only;
            // the remembered identity is never rewritten by restoration.
            self.channel = nil
        }
        if let channel {
            if let current = catalogue.first(where: { $0.platform == channel.platform &&
                ($0.id == channel.id || !channel.pageCode.isEmpty && $0.pageCode == channel.pageCode) }) {
                if current != channel { self.channel = current }
                if current.identity != channel.identity {
                    remember(current.identity)
                }
            }
            return
        }
        if let key = remembered {
            if let current = catalogue.first(where: { $0.identity == key }) {
                // A late catalogue read must not switch the user's selected tab.
                channel = current
                return
            }
        }
        guard catalogueLoaded else { return }
        channel = catalogue[0]
    }
    public func close() async {
        closed = true
        await writer?.value
        _ = try? await database?.flush()
    }
}
