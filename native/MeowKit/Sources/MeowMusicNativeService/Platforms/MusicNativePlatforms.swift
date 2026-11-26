import Foundation
import MeowMusicPlatform
import MeowRuntime

struct MusicNativePublicBrowseDetailPage: Sendable {
    let collection: MusicPublicBrowseCollection
    let tracks: [MusicNativeTrackData]
    let sourcePositions: [Int?]
    let total: Int
    let continuation: RuntimeValue?
    let consumed: Int
    var notice: String? = nil
}

extension NativeMusicService {
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

/// Borrows the single platform module. Native code consumes canonical results;
/// it does not construct or interpret upstream requests.
actor MusicNativePlatforms {
    let client: MusicPlatformClient
    private var playlistArtwork: [String: (revision: UUID?, collection: MusicPublicBrowseCollection)] = [:]
    init(client: MusicPlatformClient) { self.client = client }
    func invalidateCollection(_ collection: MusicPublicBrowseCollection) async {
        playlistArtwork[collection.id] = nil
        try? await client.clear(platform: collection.platform.rawValue)
    }
    func search(platform: String, query: String, page: Int, limit: Int) async throws -> MusicNativeTrackPage {
        let value = try await client.request(platform: platform, operation: "search", parameters: [
            "keyword": .string(query), "kind": .string("songs"), "page": .number(Double(page)), "limit": .number(Double(limit))
        ])
        return .init(items: try MusicPlatformDTO.tracks(value), total: MusicPlatformDTO.optionalCount(value[music: "total"]) ?? 0,
            nextPage: value[music: "continuation"][music: "page"] == .null ? nil :
                MusicPlatformDTO.optionalCount(value[music: "continuation"][music: "page"]))
    }
    func lyrics(platform: String, info: MusicSongSourceRecord) async throws -> MusicNativeLyrics {
        let value = try await client.request(platform: platform, operation: "lyrics", parameters: [
            "musicInfo": try MusicWireCodec.buildMusicInfoRuntimeValue(info, cleanedLocator: true)
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
        return .init(items: try MusicNativePlatformResult.collections(value[music: "items"], platform: platform), total: MusicPlatformDTO.optionalCount(value[music: "total"]) ?? 0,
            nextPage: value[music: "continuation"][music: "page"] == .null ? nil :
                MusicPlatformDTO.optionalCount(value[music: "continuation"][music: "page"]))
    }
    func playlistSummaries(platform: MusicPublicBrowsePlatform, ids: [String]) async throws -> [MusicPublicBrowseCollection] {
        let revision = try await client.contentRevision(platform: platform.rawValue)
        var results: [MusicPublicBrowseCollection] = [], missing: [String] = []
        for id in ids {
            let key = MusicPublicBrowseCollection(platform: platform, nativeID: id, title: "").id
            if let saved = playlistArtwork[key], saved.revision == revision { results.append(saved.collection) }
            else { missing.append(id) }
        }
        if !missing.isEmpty {
            let value = try await client.request(platform: platform.rawValue, operation: "playlists",
                parameters: ["ids": .array(missing.map(RuntimeValue.string))])
            results += try MusicNativePlatformResult.collections(value[music: "items"], platform: platform)
        }
        guard try await client.contentRevision(platform: platform.rawValue) == revision else { throw MusicServiceError.staleResult }
        return results
    }
    private func rememberArtwork(_ collection: MusicPublicBrowseCollection, revision: UUID?) {
        guard collection.kind == .playlist, collection.artwork != nil else { return }
        // Reuse recent detail/preview roots, never member song artwork or a durable collection store.
        if playlistArtwork.count >= 128, let key = playlistArtwork.keys.first { playlistArtwork[key] = nil }
        playlistArtwork[collection.id] = (revision, collection)
    }
    func browseDetail(_ collection: MusicPublicBrowseCollection, continuation: RuntimeValue) async throws
        -> MusicNativePublicBrowseDetailPage {
        let revision = try await client.contentRevision(platform: collection.platform.rawValue)
        let pageNumber = (continuation[music: "page"].musicNumber ?? 0) > 0 ? continuation[music: "page"].musicNumber! : 1
        let value = try await client.request(platform: collection.platform.rawValue, operation: collection.kind.rawValue,
            parameters: ["id": .string(collection.nativeID), "page": .number(pageNumber), "limit": .number(30),
                "continuation": continuation])
        let tracks = try MusicPlatformDTO.tracks(value)
        let sourcePositions = tracks.map(\.sourcePosition)
        let root = try MusicNativePlatformResult.collection(value, platform: collection.platform, kind: collection.kind)
        guard try await client.contentRevision(platform: collection.platform.rawValue) == revision else { throw MusicServiceError.staleResult }
        rememberArtwork(root, revision: revision)
        return .init(collection: root, tracks: tracks, sourcePositions: sourcePositions,
            total: MusicPlatformDTO.optionalCount(value[music: "total"]) ?? tracks.count,
            continuation: value[music: "continuation"] == .null ? nil : value[music: "continuation"],
            consumed: MusicPlatformDTO.optionalCount(value[music: "consumed"]) ?? tracks.count)
    }
    func browseComplete(_ collection: MusicPublicBrowseCollection) async throws -> MusicPublicBrowseNativeCollection {
        let revision = try await client.contentRevision(platform: collection.platform.rawValue)
        let value = try await client.request(platform: collection.platform.rawValue,
            operation: collection.kind == .album ? "album" : "playlist.complete",
            parameters: ["id": .string(collection.nativeID)])
        let root = try MusicNativePlatformResult.collection(value, platform: collection.platform, kind: collection.kind)
        guard try await client.contentRevision(platform: collection.platform.rawValue) == revision else { throw MusicServiceError.staleResult }
        rememberArtwork(root, revision: revision)
        return .init(collection: root, tracks: try MusicPlatformDTO.tracks(value))
    }
    func browseDiscovery(_ destination: MusicPublicBrowseDestination, source: MusicPublicBrowsePlatform,
                         remote: RuntimeValue) async throws -> MusicNativePublicBrowseDiscovery {
        let operation: String, parameters: RuntimeObject
        switch destination {
        case .home, .featured:
            operation = "home"; parameters = ["featured": .bool(destination == .featured),
                "continuation": remote]
        case .channels: operation = "channels"; parameters = [:]
        case .channel(let channel, let tag):
            if channel.kind == .chart {
                operation = "charts"; parameters = ["kind": .string(channel.id)]
            } else if channel.kind == .artistActivity {
                operation = "artistActivity"; parameters = ["before": remote[music: "before"] == .null ? .null : .string(remote[music: "before"].musicText)]
            } else {
                operation = "channel"; parameters = ["id": .string(channel.id), "name": .string(channel.title),
                    "pageCode": .string(channel.pageCode), "tag": .string(tag),
                    "continuation": remote]
            }
        case .daily: throw MusicServiceError.unsupported
        }
        let result = try await client.request(platform: source.rawValue, operation: operation, parameters: parameters)
        return try operation == "channels" ? MusicNativePlatformResult.directory(result, platform: source) :
            MusicNativePlatformResult.discovery(result, platform: source)
    }
    func browseAccountHome(_ destination: MusicPublicBrowseDestination, remote: RuntimeValue,
                           account: NativeMusicAccountService, binding: MusicNativeHomeBinding) async throws
        -> MusicNativePublicBrowseDiscovery {
        let request: MusicNativeHomeRequest
        if case .channel(let channel, _) = destination, channel.kind == .artistActivity {
            request = .artistActivity(before: remote[music: "before"] == .null ? nil : remote[music: "before"].musicText)
        } else {
            let isEmptyRemote: Bool
            if case .object(let fields) = remote { isEmptyRemote = fields.isEmpty } else { isEmptyRemote = true }
            let continuation: MusicNativeHomeContinuation?
            if isEmptyRemote {
                continuation = nil
            } else {
                let rawCodes = remote[music: "loadedPositionCodes"].musicText
                let loadedCodes: [String]
                if !rawCodes.isEmpty, let decoded = try? RuntimeJSON.decode(Data(rawCodes.utf8)) {
                    loadedCodes = decoded.musicArray.map(\.musicText)
                } else {
                    loadedCodes = []
                }
                continuation = try MusicNativeHomeContinuation(
                    cursor: remote[music: "cursor"].musicText,
                    blockCodeOrderList: remote[music: "blockCodeOrderList"] == .null ? nil : remote[music: "blockCodeOrderList"].musicText,
                    loadedPositionCodes: loadedCodes
                )
            }
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
        let hot = value[music: "hot"].musicArray.enumerated().map { index, row in
            MusicSearchHotKeyword(keyword: row[music: "keyword"].musicText,
                rank: MusicPlatformDTO.optionalCount(row[music: "rank"]) ?? index + 1, score: row[music: "score"].musicNumber)
        }
        return .init(hot: hot, hotCanPlay: value[music: "hotCanPlay"].musicBool,
            charts: try value[music: "charts"].musicArray.map {
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
