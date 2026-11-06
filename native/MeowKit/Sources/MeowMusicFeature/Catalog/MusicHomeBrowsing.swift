import Foundation

public struct MusicHomeSourceSelection: Sendable, Equatable {
    public let selected: MusicPublicBrowsePlatform
    /// Durable preference revision; saves are compare-and-swap against it.
    public let revision: Int
    public let available: [MusicPublicBrowsePlatform]
    public init(selected: MusicPublicBrowsePlatform, revision: Int, available: [MusicPublicBrowsePlatform]) {
        self.selected = selected; self.revision = revision; self.available = available
    }
}

/// Optional v1 home lifecycle. A restored page is display data, never a
/// continuation or account grant. Refresh keeps the last successful page until
/// replacement, except when the account/protection binding is revoked.
public protocol MusicHomeBrowsing: MusicPublicBrowsing {
    /// Independent of search and playback source selection. Only implemented
    /// home providers are available; selecting the current source is a no-op.
    /// A save whose revision is no longer current fails with `.staleRevision`.
    func homeSourceSelection() async throws -> MusicHomeSourceSelection
    func selectHomeSource(_ source: MusicPublicBrowsePlatform, ifRevision: Int) async throws -> MusicHomeSourceSelection
    func restoredHome() async throws -> MusicPublicBrowsePage?
    func homePage(cursor: String?, refresh: Bool) async throws -> MusicPublicBrowsePage
    func homeChanges() async -> AsyncStream<Void>
}
