import Foundation

/// Remote content identity. Neither this value nor a saved reference owns members.
public struct MusicNetworkPlaylistReference: Codable, Equatable, Sendable {
    public let platform: MusicPublicBrowsePlatform
    public let remoteID: String
    public let creator: String
    public init(platform: MusicPublicBrowsePlatform, remoteID: String, creator: String = "") {
        self.platform = platform; self.remoteID = remoteID; self.creator = creator
    }
}

public protocol MusicNetworkPlaylistServing: Sendable {
    /// Saves an identity, without opening a playback source or reading members.
    /// Official subscription is a separate, authorized synchronization result.
    func saveNetworkPlaylist(_ collection: MusicPublicBrowseCollection,
                             intent: MusicQueueAuthorization) async throws -> MusicCollection
    func removeNetworkPlaylist(_ collection: MusicCollection,
                               intent: MusicQueueAuthorization) async throws
}

#if os(iOS) || os(macOS)
@MainActor
extension MusicViewModel {
    public func saveNetworkPlaylist(_ collection: MusicPublicBrowseCollection) async throws {
        let epoch = try enter()
        guard let provider = service as? any MusicNetworkPlaylistServing else { throw MusicServiceError.unsupported }
        let intent = MusicQueueAuthorization()
        defer { intent.revoke() }
        _ = try await withTaskCancellationHandler {
            try await provider.saveNetworkPlaylist(collection, intent: intent)
        } onCancel: { intent.revoke() }
        try check(epoch); try intent.check()
        let values = try await requireService().collections()
        try check(epoch); publishCollections(values)
    }

    public func removeNetworkPlaylist(_ collection: MusicCollection) async {
        await updateLibrary(operation: "取消收藏歌单") {
            guard let provider = self.service as? any MusicNetworkPlaylistServing else { throw MusicServiceError.unsupported }
            let intent = MusicQueueAuthorization()
            defer { intent.revoke() }
            try await withTaskCancellationHandler {
                try await provider.removeNetworkPlaylist(collection, intent: intent)
            } onCancel: { intent.revoke() }
            try intent.check()
            return try await self.requireService().collections()
        }
    }
}
#endif
