import Foundation
import MeowMusicFeature
import MeowMusicPlatform
import MeowRuntime

/// Borrows the single platform module. Native code consumes canonical results;
/// it does not construct or interpret upstream requests.
actor MusicNativePlatforms {
    let client: MusicPlatformClient
    init(client: MusicPlatformClient) { self.client = client }
    func invalidateCollection(_ collection: MusicPublicBrowseCollection) async {
        try? await client.clear(platform: collection.platform.rawValue)
    }
    func search(platform: String, query: String, page: Int, limit: Int) async throws -> MusicNativeTrackPage {
        let value = try await client.request(platform: platform, operation: "search", parameters: [
            "keyword": .string(query), "kind": .string("songs"), "page": .number(Double(page)), "limit": .number(Double(limit))
        ])
        return .init(items: try MusicPlatformDTO.tracks(value), total: try MusicPlatformDTO.count(value[music: "total"]),
            nextPage: value[music: "continuation"][music: "page"] == .null ? nil :
                try MusicPlatformDTO.count(value[music: "continuation"][music: "page"]))
    }
    func lyrics(platform: String, info: MJ) async throws -> MusicNativeLyrics {
        let value = try await client.request(platform: platform, operation: "lyrics", parameters: [
            "musicInfo": try RuntimeJSON.decode(info.bytes)
        ])
        guard case .string(let text) = value[music: "text"] else { throw MusicServiceError.upstreamInvalid }
        return .init(text: text, translation: value[music: "translation"].musicText,
                     romanization: value[music: "romanization"].musicText)
    }
    func browsePlaylists(platform: MusicPublicBrowsePlatform, query: String, page: Int,
                         limit: Int = 30) async throws -> MusicPublicBrowseCollectionSearch {
        let value = try await client.request(platform: platform.rawValue, operation: "search", parameters: [
            "keyword": .string(query), "kind": .string("playlists"), "page": .number(Double(page)), "limit": .number(Double(limit))
        ])
        return .init(items: try value[music: "items"].musicRequiredArray().map {
            try MusicNativePlatformResult.collection($0, platform: platform)
        }, total: try MusicPlatformDTO.count(value[music: "total"]),
            nextPage: value[music: "continuation"][music: "page"] == .null ? nil :
                try MusicPlatformDTO.count(value[music: "continuation"][music: "page"]))
    }
    func browseDetail(_ collection: MusicPublicBrowseCollection, continuation: MJ) async throws
        -> MusicNativePublicBrowseDetailPage {
        let value = try await client.request(platform: collection.platform.rawValue, operation: collection.kind.rawValue,
            parameters: ["id": .string(collection.nativeID), "page": .number(continuation["page"].number > 0 ?
                continuation["page"].number : 1), "limit": .number(30),
                "continuation": try RuntimeJSON.decode(continuation.bytes)])
        let tracks = try MusicPlatformDTO.tracks(value), positions = value[music: "sourcePositions"]
        let sourcePositions: [Int?]
        if positions == .null { sourcePositions = tracks.indices.map { _ in nil } }
        else { sourcePositions = try positions.musicRequiredArray().map {
            $0 == .null ? nil : try MusicPlatformDTO.count($0)
        } }
        return .init(collection: try MusicNativePlatformResult.collection(value, platform: collection.platform,
                kind: collection.kind), tracks: tracks, sourcePositions: sourcePositions,
            total: try MusicPlatformDTO.count(value[music: "total"], default: tracks.count),
            continuation: value[music: "continuation"] == .null ? nil : try MJ.parse(value[music: "continuation"].musicData()),
            consumed: try MusicPlatformDTO.count(value[music: "consumed"], default: tracks.count))
    }
    func browseComplete(_ collection: MusicPublicBrowseCollection) async throws -> MusicPublicBrowseNativeCollection {
        let value = try await client.request(platform: collection.platform.rawValue,
            operation: collection.kind == .album ? "album" : "playlist.complete",
            parameters: ["id": .string(collection.nativeID)])
        return .init(collection: try MusicNativePlatformResult.collection(value, platform: collection.platform, kind: collection.kind),
                     tracks: try MusicPlatformDTO.tracks(value))
    }
    func browseDiscovery(_ destination: MusicPublicBrowseDestination, source: MusicPublicBrowsePlatform,
                         remote: MJ) async throws -> MusicNativePublicBrowseDiscovery {
        let operation: String, parameters: RuntimeObject
        switch destination {
        case .home, .featured:
            operation = "home"; parameters = ["featured": .bool(destination == .featured),
                "continuation": try RuntimeJSON.decode(remote.bytes)]
        case .channels: operation = "channels"; parameters = [:]
        case .channel(let channel, let tag):
            if channel.kind == .artistActivity {
                operation = "artistActivity"; parameters = ["before": remote["before"] == .null ? .null : .string(remote["before"].string)]
            } else {
                operation = "channel"; parameters = ["id": .string(channel.id), "name": .string(channel.title),
                    "pageCode": .string(channel.pageCode), "tag": .string(tag),
                    "continuation": try RuntimeJSON.decode(remote.bytes)]
            }
        case .daily: throw MusicServiceError.unsupported
        }
        return try MusicNativePlatformResult.discovery(
            await client.request(platform: source.rawValue, operation: operation, parameters: parameters), platform: source)
    }
    func browseAccountHome(_ destination: MusicPublicBrowseDestination, remote: MJ,
                           account: any MusicNativeHomeProviding, binding: MusicNativeHomeBinding) async throws
        -> MusicNativePublicBrowseDiscovery {
        let request: MusicNativeHomeRequest
        if case .channel(let channel, _) = destination, channel.kind == .artistActivity {
            request = .artistActivity(before: remote["before"] == .null ? nil : remote["before"].string)
        } else {
            let continuation = remote.fields.isEmpty ? nil : try MusicNativeHomeContinuation(
                cursor: remote["cursor"].string, blockCodeOrderList: remote["blockCodeOrderList"] == .null ? nil :
                    remote["blockCodeOrderList"].string,
                loadedPositionCodes: (try? MJ.parse(Data(remote["loadedPositionCodes"].string.utf8)).strings) ?? [])
            request = destination == .home ? .home(continuation: continuation) : .featured(continuation: continuation)
        }
        return try MusicNativePlatformResult.discovery(
            RuntimeJSON.decode(await account.nativeHomePayload(request, binding: binding)), platform: .netease)
    }
    func browseVIP(binding: MusicNativeHomeBinding) async throws -> MusicNativePublicBrowseDiscovery {
        try binding.authorization.check()
        let value = try await client.request(platform: "wy", operation: "channel", parameters: ["id": .string("vip")])
        try binding.authorization.check()
        return try MusicNativePlatformResult.discovery(value, platform: .netease)
    }
    func homeCollectionSongs(_ ids: [String]) async throws -> [MusicNativeTrackData] {
        try MusicPlatformDTO.tracks(await client.request(platform: "wy", operation: "songs",
            parameters: ["ids": .array(ids.map(RuntimeValue.string))]))
    }
    func homeAction(_ action: MusicHomeAction, preview: Bool = false) async throws -> MusicNativeFMResponse {
        let (operation, parameters) = MusicPlatformDTO.action(action, preview: preview)
        let value = try await client.request(platform: "wy", operation: operation, parameters: parameters)
        return .init(tracks: value[music: "items"] == .null ? [] : try MusicPlatformDTO.tracks(value),
                     artwork: MusicArtworkURL.normalized(value[music: "cover"].musicText))
    }
    func similar(_ action: MusicHomeAction) async throws -> MusicNativeSimilarModeBatch {
        let (operation, parameters) = MusicPlatformDTO.action(action)
        let value = try await client.request(platform: "wy", operation: operation, parameters: parameters)
        let hasMore: Bool?
        if case .bool(let flag) = value[music: "hasMore"] { hasMore = flag } else { hasMore = nil }
        return .init(items: try MusicPlatformDTO.tracks(value), hasMore: hasMore)
    }
    func searchDiscovery(components: Set<MusicSearchDiscoveryFailure.Component>) async throws -> MusicSearchDiscoverySnapshot {
        let value = try await client.request(platform: "wy", operation: "searchDiscovery", parameters: [
            "components": .array(components.map { .string($0.rawValue) })
        ])
        let hot = try value[music: "hot"].musicRequiredArray().enumerated().map { index, row in
            MusicSearchHotKeyword(keyword: try row[music: "keyword"].musicRequiredText(),
                rank: try MusicPlatformDTO.count(row[music: "rank"], default: index + 1), score: row[music: "score"].musicNumber)
        }
        return .init(hot: hot, hotCanPlay: value[music: "hotCanPlay"].musicBool,
            charts: try value[music: "charts"].musicRequiredArray().map {
                .init(collection: try MusicNativePlatformResult.collection($0, platform: .netease))
            }, failures: value[music: "failures"].musicArray.compactMap { row in
                guard let component = MusicSearchDiscoveryFailure.Component(rawValue: row[music: "stage"].musicText) else { return nil }
                return .init(component: component, error: MusicPlatformClient.error(RuntimeHTTPError.remote(row[music: "code"].musicText)))
            }, validUntil: MusicPlatformDTO.date(value[music: "expiresAt"]) ?? Date())
    }
    func searchHotPlayback(keywords: [String]) async throws -> [MusicNativeTrackData] {
        try MusicPlatformDTO.tracks(await client.request(platform: "wy", operation: "searchHotPlayback",
            parameters: ["keywords": .array(keywords.map(RuntimeValue.string))]))
    }
}
