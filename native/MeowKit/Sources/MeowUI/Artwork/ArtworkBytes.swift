import Foundation

public struct ArtworkBytes: Sendable {
    public let data: Data
    public let expiresAt: Date
    public init(data: Data, expiresAt: Date) { self.data = data; self.expiresAt = expiresAt }
}

/// Optional persistence for the existing image loader. Revision is a local
/// clear/write fence, never an account or resource identity.
public protocol ArtworkByteCaching: Sendable {
    var revision: UUID { get }
    func read(_ url: URL) async -> ArtworkBytes?
    func store(_ data: Data, for url: URL, revision: UUID) async -> ArtworkBytes?
    func remove(_ url: URL, revision: UUID) async
}
