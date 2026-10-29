import Foundation

/// Optional v1 home lifecycle. A restored page is display data, never a
/// continuation or account grant. Refresh keeps the last successful page until
/// replacement, except when the account/protection binding is revoked.
public protocol MusicHomeBrowsing: MusicPublicBrowsing {
    func restoredHome() async throws -> MusicPublicBrowsePage?
    func homePage(cursor: String?, refresh: Bool) async throws -> MusicPublicBrowsePage
    func homeChanges() async -> AsyncStream<Void>
}
