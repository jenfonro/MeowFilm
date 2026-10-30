import Foundation

/// Private protocol-client facts, not account authorization, track metadata or
/// a UI model. The admission and revision bind a trusted response to its read.
public struct MusicNativeClientDevice: Sendable {
    public let platform: MusicAccountPlatform
    public let fields: [String: String]
    public let admission: UUID
    public let revision: UUID
    public init(platform: MusicAccountPlatform, fields: [String: String], admission: UUID, revision: UUID) {
        self.platform = platform; self.fields = fields; self.admission = admission; self.revision = revision
    }
}
public protocol MusicNativeClientContextProviding: Sendable {
    func nativeClientDevice(platform: MusicAccountPlatform) async throws -> MusicNativeClientDevice
    func acceptNativeClientResponse(_ device: MusicNativeClientDevice, url: URL,
                                   headers: [String: String]) async throws -> MusicNativeClientDevice
}
