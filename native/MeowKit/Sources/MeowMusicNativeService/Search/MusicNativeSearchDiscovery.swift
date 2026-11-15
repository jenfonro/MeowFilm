import Foundation
import MeowMusicFeature

extension NativeMusicService: MusicSearchDiscoveryProviding, MusicSearchHotPlaybackProviding {
    public func searchDiscovery(components: Set<MusicSearchDiscoveryFailure.Component>) async throws -> MusicSearchDiscoverySnapshot {
        try check(); try requirePlatform("wy")
        let own = epoch, port = publicPlatforms
        let result = try await work(label: "musicSearchDiscovery") { try await port.searchDiscovery(components: components) }
        try check(own); return result
    }
    public func searchHotPlayback(keywords: [String]) async throws -> [MusicRemoteTrack] {
        try check(); try requirePlatform("wy")
        let own = epoch, port = publicPlatforms
        let result = try await work(label: "musicSearchHotPlayback") { try await port.searchHotPlayback(keywords: keywords) }
        try check(own)
        return try await metadataPage(.init(items: result)).items
    }
}
