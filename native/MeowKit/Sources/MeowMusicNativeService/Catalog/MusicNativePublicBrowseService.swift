import Foundation
import MeowMusicFeature

/// Page continuation belongs to one native request/access lifetime. It carries
/// the provider's continuation, never a content digest or platform authority.
struct MusicNativePublicBrowseCursor {
    let offset: Int
    let remote: MJ
    static func read(_ text: String?, key: String, epoch: UUID) throws -> Self {
        guard let text else { return .init(offset: 0, remote: .obj()) }
        guard let data = Data(base64Encoded: text) else { throw MusicServiceError.invalidRequest }
        let row = try MJ.parse(data)
        try musicRequire(row["key"].string == key && row["epoch"].string == epoch.uuidString, .staleResult)
        return .init(offset: try row["offset"].integer(), remote: row["remote"])
    }
    func encode(key: String, epoch: UUID, offset: Int, remote: MJ = .obj()) throws -> String {
        MJ.obj([("key", .string(key)), ("epoch", .string(epoch.uuidString)),
            ("offset", .n(offset)), ("remote", remote)]).bytes.base64EncodedString()
    }
}

extension NativeMusicService: MusicPublicBrowsing, MusicPublicPlaylistSearching,
    MusicPublicBrowseQueueLoading, MusicPublicBrowseRefreshing {
    public func refreshPublicCollection(_ collection: MusicPublicBrowseCollection) async {
        guard !closed, available else { return }
        publicCollectionRefreshes += 1
        defer { publicCollectionRefreshes -= 1 }
        publicCollectionRevision = UUID()
        let retired = publicQueueSources.filter { $0.value.collectionID == collection.id }
        for (id, entry) in retired {
            entry.authorization.revoke(); publicQueueSources.removeValue(forKey: id); access.remove(id)
        }
        for entry in retired.values { await entry.reader.close() }
        await publicPlatforms.invalidateCollection(collection)
    }
    public func publicPlaylists(platform: MusicPublicBrowsePlatform, query: String,
                                cursor: String?) async throws -> MusicPublicBrowseCollectionPage {
        try check(); try requirePlatform(platform.rawValue)
        let limit = 30, key = "search:" + platform.rawValue + ":" + query, own = epoch
        let position = try MusicNativePublicBrowseCursor.read(cursor, key: key, epoch: own)
        let page = position.offset / limit + 1
        let result = try await searchPublicPlaylists(platform: platform, query: query, page: page, limit: limit)
        try check(own)
        let next = try result.nextPage.map { try position.encode(key: key, epoch: own, offset: ($0 - 1) * limit) }
        return .init(items: result.items, total: result.total, cursor: next)
    }
    public func searchPublicPlaylists(platform: MusicPublicBrowsePlatform, query: String,
                                      page: Int, limit: Int) async throws -> MusicPlaylistSearchPage {
        try check(); try requirePlatform(platform.rawValue)
        let own = epoch, port = publicPlatforms
        let result = try await work(label: "publicBrowse") {
            try await port.browsePlaylists(platform: platform, query: query, page: page, limit: limit)
        }
        try check(own)
        return .init(platform: platform, query: query, page: page, limit: limit, total: result.total,
                     nextPage: result.nextPage, items: result.items)
    }
    public func publicDetail(_ collection: MusicPublicBrowseCollection, cursor: String?) async throws -> MusicPublicBrowseDetail {
        try check(); try requirePlatform(collection.platform.rawValue)
        try musicRequire(publicCollectionRefreshes == 0, .busy)
        let key = "detail:" + collection.id, own = epoch, revision = publicCollectionRevision
        let position = try MusicNativePublicBrowseCursor.read(cursor, key: key, epoch: own)
        let port = publicPlatforms
        let result = try await work(label: "publicBrowse") {
            try await port.browseDetail(collection, continuation: position.remote)
        }
        try check(own); try musicRequire(revision == publicCollectionRevision, .staleResult)
        let next = try result.continuation.map { try position.encode(key: key, epoch: own,
            offset: position.offset + result.consumed, remote: $0) }
        let rows = try await metadataPage(.init(items: result.tracks)).items
        try check(own); try musicRequire(revision == publicCollectionRevision, .staleResult)
        let members = try rows.enumerated().map { index, track in
            MusicPublicBrowseMember(nativeID: try MJ.parse(result.tracks[index].data)["songmid"].id,
                sourcePosition: result.sourcePositions.indices.contains(index) ? result.sourcePositions[index] : nil, track: track)
        }
        return .init(collection: result.collection, members: members, total: result.total, cursor: next,
                     notice: result.notice, consumed: result.consumed)
    }
    public func publicQueueTracks(_ request: MusicPublicBrowseQueueRequest) async throws -> [MusicNativeTrackData] {
        let own = epoch, authorization = MusicQueueAuthorization()
        let snapshot = try await publicQueueSnapshot(request)
        return try projectQueueTracks(snapshot.tracks, epoch: own, authorization: authorization)
    }
    func publicQueueSnapshot(_ request: MusicPublicBrowseQueueRequest) async throws
        -> (collection: MusicPublicBrowseCollection, tracks: [MusicQueueTrackReference]) {
        try check(); try requirePlatform(request.collection.platform.rawValue)
        try musicRequire(publicCollectionRefreshes == 0, .busy)
        let port = publicPlatforms, own = epoch, revision = publicCollectionRevision
        let result = try await work(label: "publicBrowseQueue") { try await port.browseComplete(request.collection) }
        try check(own); try musicRequire(revision == publicCollectionRevision, .staleResult)
        try musicRequire(result.tracks.reduce(0, { $0 + $1.data.count }) <= MusicRemoteQueueSource.maximumBytes, .limit)
        let authorization = MusicQueueAuthorization()
        let references = try adoptQueueTracks(result.tracks, epoch: own, authorization: authorization, intent: authorization)
        try check(own)
        return (result.collection, references)
    }
    public func publicDiscovery(_ destination: MusicPublicBrowseDestination, cursor: String?) async throws -> MusicPublicBrowsePage {
        try check()
        if destination == .home { return try await homePage(cursor: cursor, refresh: false) }
        if destination == .featured { return try await scopedHomePage(destination, cursor: cursor, refresh: false) }
        if case .channel(let channel, _) = destination,
           channel.kind == .artistActivity || channel.kind == .featured || channel.kind == .vip {
            return try await scopedHomePage(destination, cursor: cursor, refresh: false)
        }
        if case .daily(let platform) = destination {
            try requirePlatform(platform.rawValue)
            let key = "daily:" + platform.rawValue, own = epoch
            let position = try MusicNativePublicBrowseCursor.read(cursor, key: key, epoch: own)
            guard let accounts else { throw MusicServiceError.authRequired }
            let remote = position.remote["cursor"].string
            let page = try await work(label: "publicBrowse") {
                try await accounts.nativeRecommendations(platform: platform, kind: "daily", cursor: remote.isEmpty ? nil : remote)
            }
            try check(own)
            let tracks = try await metadataPage(page).items
            let items = tracks.map { MusicPublicBrowseItem(id: $0.songId, title: $0.title, subtitle: $0.artist,
                artwork: $0.artwork, content: .track($0)) }
            let next = try page.cursor.map { try position.encode(key: key, epoch: own, offset: position.offset + items.count,
                remote: .obj([("cursor", .string($0))])) }
            return .init(sections: [.init(id: key, title: "每日推荐", items: items)], cursor: next)
        }
        let selectedSource = try currentHomeSource(), sourceRevision = homeSourceRevision, own = epoch
        let source: MusicPublicBrowsePlatform
        if case .channel(let channel, _) = destination {
            try requireHomeSource(channel.platform); source = channel.platform
        } else { source = selectedSource }
        let key = source.rawValue + ":" + MusicNativePublicBrowseDiscovery.key(destination) + ":" + sourceRevision.uuidString
        let position = try MusicNativePublicBrowseCursor.read(cursor, key: key, epoch: own), port = publicPlatforms
        let result = try await work(label: "musicHome") {
            try await port.browseDiscovery(destination, source: source, remote: position.remote)
        }
        try checkHomeSource(selectedSource, revision: sourceRevision, epoch: own)
        let songs = try await publicSongMetadata(result)
        try checkHomeSource(selectedSource, revision: sourceRevision, epoch: own)
        let sections = try publicSections(result, prefix: key + ":", songs: songs)
        let next = try result.next.map { try position.encode(key: key, epoch: own, offset: position.offset + 1, remote: $0) }
        return .init(sections: sections, cursor: next, filters: result.filters, selectedFilter: result.selected,
                     notice: result.notice, consumed: result.consumed)
    }
    func publicSections(_ result: MusicNativePublicBrowseDiscovery, prefix: String,
                        songs: [Data: MJ]) throws -> [MusicPublicBrowseSection] {
        try result.sections.map { section in
            let tabs = try section.tabs.map { tab in
                MusicPublicBrowseSectionTab(id: tab.id, title: tab.title,
                    items: try publicItems(tab.items, layout: section.layout, songs: songs))
            }
            let items = tabs.isEmpty ? try publicItems(section.items, layout: section.layout, songs: songs) :
                (tabs.first { $0.id == section.selectedTab } ?? tabs.first)?.items ?? []
            return .init(id: prefix + section.id, title: section.title, items: items, layout: section.layout,
                avatars: section.avatars, channel: section.channel, filters: section.filters,
                selectedFilter: section.selectedFilter, error: section.error, tabs: tabs, selectedTab: section.selectedTab)
        }
    }
    private func publicItems(_ rows: [MusicNativePublicBrowseItem], layout: MusicPublicBrowseSection.Layout,
                             songs: [Data: MJ]) throws -> [MusicPublicBrowseItem] {
        try rows.map { item in
            let content: MusicPublicBrowseContent
            var title = item.title, subtitle = item.subtitle, artwork = item.artwork
            switch item.content {
            case .collection(let value): content = .collection(value)
            case .channel(let value): content = .channel(value)
            case .daily: content = .daily(.netease)
            case .unavailable: content = .unavailable
            case .action(let action): content = .action(action)
            case .song(let value):
                guard let song = songs[publicSongMetadataKey(value)] else { throw MusicServiceError.notFound }
                let track = try repositoryTrack(song); content = .track(track)
                title = track.title
                subtitle = [track.artist, layout == .vipSongs ? "" : track.album].filter { !$0.isEmpty }.joined(separator: " · ")
                artwork = track.artwork
            case .record(let id):
                let track = try repositoryTrack(repositorySong(id, read())); content = .track(track)
                title = track.title; subtitle = track.artist; artwork = track.artwork
            }
            let preview = try item.previewSongs.map { value -> MusicRemoteTrack in
                guard let song = songs[publicSongMetadataKey(value)] else { throw MusicServiceError.notFound }
                return try repositoryTrack(song)
            } + item.previewSongIDs.map { try repositoryTrack(repositorySong($0, read())) }
            let ranking = item.ranking.map { ranking in
                MusicPublicBrowseRanking(preview: ranking.preview.map { entry in
                    guard let track = preview.first(where: {
                        $0.songId == entry.id || $0.sources.contains { $0.nativeId == entry.id }
                    }) else { return entry }
                    return .init(id: track.songId, title: track.title, artist: track.artist, artwork: track.artwork,
                                 rank: entry.rank, lastRank: entry.lastRank)
                }, songArtwork: preview.isEmpty ? ranking.songArtwork : preview[0].artwork,
                   updateFrequency: ranking.updateFrequency)
            }
            return .init(id: item.id, title: title, subtitle: subtitle, artwork: artwork, content: content,
                ranking: ranking, previewSongs: preview, recommendationReason: item.recommendationReason, coverLabel: item.coverLabel)
        }
    }
}

enum MusicNativePublicBrowseSupport {
    static func artwork(_ value: MJ) -> URL? { MusicArtworkURL.normalized(value.string) }
}
