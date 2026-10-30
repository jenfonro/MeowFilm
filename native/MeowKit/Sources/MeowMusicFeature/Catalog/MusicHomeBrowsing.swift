import Foundation

public struct MusicHomeSourceSelection: Sendable, Equatable {
    public let selected: MusicPublicBrowsePlatform?
    public let available: [MusicPublicBrowsePlatform]
    public init(selected: MusicPublicBrowsePlatform?, available: [MusicPublicBrowsePlatform]) {
        self.selected = selected; self.available = available
    }
}

/// Optional v1 home lifecycle. A restored page is display data, never a
/// continuation or account grant. Refresh keeps the last successful page until
/// replacement, except when the account/protection binding is revoked.
public protocol MusicHomeBrowsing: MusicPublicBrowsing {
    /// Independent of search and playback source selection. Only implemented
    /// home providers are available; selecting the current source is a no-op.
    func homeSourceSelection() async -> MusicHomeSourceSelection
    func selectHomeSource(_ source: MusicPublicBrowsePlatform) async throws
    func restoredHome() async throws -> MusicPublicBrowsePage?
    func homePage(cursor: String?, refresh: Bool) async throws -> MusicPublicBrowsePage
    func homeChanges() async -> AsyncStream<Void>
}
