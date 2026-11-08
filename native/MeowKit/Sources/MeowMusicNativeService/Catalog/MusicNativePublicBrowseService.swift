import Foundation
import CryptoKit
import MeowMusicFeature

// Cursor is memory-only, not an authorization grant. It is bound to this
// service's protection epoch, exact request, expiry and validated native state.
struct MusicNativePublicBrowseCursor {
    let offset: Int
    let snapshot: String
    let remote: MJ
    let expires: Date
    static func read(_ text: String?, key: String, epoch: UUID, now: Date) throws -> Self {
        guard let text else { return .init(offset: 0, snapshot: "", remote: .obj(), expires: now.addingTimeInterval(600)) }
        try musicRequire(text.utf8.count <= 96_000, .limit)
        guard let data = Data(base64Encoded: text) else { throw MusicServiceError.invalidRequest }
        let row = try MJ.parse(data, maximum: 72_000)
        try row.requireFields(["v", "key", "epoch", "offset", "snapshot", "remote", "expires"],
                              required: ["v", "key", "epoch", "offset", "snapshot", "remote", "expires"])
        try musicRequire(row["v"] == .n(1) && row["key"].string == key && row["epoch"].string == epoch.uuidString, .staleResult)
        let offset = try row["offset"].integer()
        let snapshot = try row["snapshot"].text(64, empty: true)
        try musicRequire(row["expires"].isNumber && row["expires"].number.isFinite && row["remote"].isObject, .invalidRequest)
        let expires = Date(timeIntervalSince1970: row["expires"].number)
        try musicRequire(expires > now && expires <= now.addingTimeInterval(600), .handleExpired)
        return .init(offset: offset, snapshot: snapshot, remote: row["remote"], expires: expires)
    }
    func encode(key: String, epoch: UUID, offset: Int, snapshot: String = "", remote: MJ = .obj()) throws -> String {
        let data = MJ.obj([("v", .n(1)), ("key", .string(key)), ("epoch", .string(epoch.uuidString)),
            ("offset", .n(offset)), ("snapshot", .string(snapshot)), ("remote", remote),
            ("expires", .number(expires.timeIntervalSince1970))]).bytes
        try musicRequire(data.count <= 72_000, .limit)
        return data.base64EncodedString()
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
        let limit = 30
        let query = query.trimmingCharacters(in: .whitespacesAndNewlines)
        _ = try musicText(query, maximum: 800)
        try musicRequire(query.unicodeScalars.count <= 200)
        let key = "search:" + platform.rawValue + ":\(limit):" + query
        let own = epoch, position = try MusicNativePublicBrowseCursor.read(cursor, key: key, epoch: own, now: now())
        try musicRequire(position.offset % limit == 0)
        let page = position.offset / limit + 1
        let result = try await searchPublicPlaylists(platform: platform, query: query, page: page, limit: limit)
        try check(own)
        let next: String?
        if let n = result.nextPage {
            next = try position.encode(key: key, epoch: own, offset: (n - 1) * limit)
        } else { next = nil }
        return .init(items: result.items, total: result.total, cursor: next)
    }

    public func searchPublicPlaylists(platform: MusicPublicBrowsePlatform, query: String,
                                      page: Int, limit: Int) async throws -> MusicPlaylistSearchPage {
        try check(); try requirePlatform(platform.rawValue)
        _ = try MusicSearchPagination.offset(platform: platform, page: page, limit: limit)
        let query = query.trimmingCharacters(in: .whitespacesAndNewlines)
        _ = try musicText(query, maximum: 800)
        try musicRequire(query.unicodeScalars.count <= 200)
        let own = epoch
        let port = publicPlatforms
        let result = try await work(label: "publicBrowse") {
            try await port.browsePlaylists(platform: platform, query: query, page: page, limit: limit)
        }
        try check(own)
        try musicRequire(result.total >= 0 && result.items.count <= limit &&
            result.items.allSatisfy { $0.platform == platform && $0.kind == .playlist } &&
            Set(result.items.map(\.id)).count == result.items.count, .upstreamInvalid)
        try musicRequire(MusicSearchPagination.validNextPage(page: page, limit: limit,
            next: result.nextPage, hasItems: !result.items.isEmpty), .invalidResponse)
        return .init(platform: platform, query: query, page: page, limit: limit, total: result.total,
                     nextPage: result.nextPage, items: result.items)
    }

    public func publicDetail(_ collection: MusicPublicBrowseCollection, cursor: String?) async throws -> MusicPublicBrowseDetail {
        try check(); try requirePlatform(collection.platform.rawValue)
        try musicRequire(publicCollectionRefreshes == 0, .busy)
        let key = "detail:" + collection.id, own = epoch, revision = publicCollectionRevision
        let position = try MusicNativePublicBrowseCursor.read(cursor, key: key, epoch: own, now: now())
        let port = publicPlatforms
        let result = try await work(label: "publicBrowse") {
            try await port.browseDetail(collection, offset: position.offset, snapshot: position.snapshot)
        }
        try check(own)
        try musicRequire(revision == publicCollectionRevision, .staleResult)
        let consumed = result.consumed ?? result.tracks.count
        try musicRequire(result.collection.id == collection.id && result.tracks.count <= 30 &&
            (result.tracks.count...30).contains(consumed) && result.total >= position.offset + consumed &&
            result.sourcePositions.count == result.tracks.count &&
            result.sourcePositions == result.sourcePositions.sorted() &&
            Set(result.sourcePositions).count == result.sourcePositions.count &&
            result.sourcePositions.allSatisfy { (position.offset..<(position.offset + consumed)).contains($0) } &&
            (!result.more || consumed > 0) &&
            result.tracks.allSatisfy { $0.platform == collection.platform.rawValue }, .upstreamInvalid)
        let next = result.more ? try position.encode(key: key, epoch: own,
            offset: position.offset + consumed, snapshot: result.snapshot) : nil
        // Reserve the full bounded page before registering any handles. Cached
        // native data never grants the lifetime of a previously issued handle.
        let rows = try page(.init(items: result.tracks)).items
        let members = try zip(result.sourcePositions, rows).map { sourcePosition, track -> MusicPublicBrowseMember in
            guard let source = track.sources.first(where: { $0.platform == collection.platform.rawValue }) else {
                throw MusicServiceError.upstreamInvalid
            }
            return .init(nativeID: source.nativeId, sourcePosition: sourcePosition, track: track)
        }
        return .init(collection: result.collection, members: members, total: result.total, cursor: next,
                     snapshot: result.snapshot, notice: result.notice, consumed: consumed)
    }

    public func publicQueueTracks(_ request: MusicPublicBrowseQueueRequest) async throws -> [MusicNativeTrackData] {
        try await publicQueueSnapshot(request).tracks
    }
    func publicQueueSnapshot(_ request: MusicPublicBrowseQueueRequest) async throws -> MusicPublicBrowseNativeCollection {
        try check(); try requirePlatform(request.collection.platform.rawValue)
        try musicRequire(publicCollectionRefreshes == 0, .busy)
        try musicRequire(request.snapshot.range(of: "^[a-f0-9]{64}$", options: .regularExpression) != nil)
        let port = publicPlatforms, own = epoch, revision = publicCollectionRevision
        let result = try await work(label: "publicBrowseQueue") {
            let all = try await port.browseComplete(request.collection)
            try await port.validateCompleteBrowse(all, snapshot: request.snapshot)
            return all
        }
        try check(own)
        try musicRequire(revision == publicCollectionRevision, .staleResult)
        let rows = result.tracks
        try musicRequire(result.collection.id == request.collection.id &&
            (result.collection.count ?? rows.count) >= rows.count, .upstreamInvalid)
        try musicRequire(rows.reduce(0, { $0 + $1.data.count }) <= MusicRemoteQueueSource.maximumBytes &&
            rows.allSatisfy { $0.platform == request.collection.platform.rawValue }, .limit)
        _ = try rows.map(MusicModel.normalize) // Fully validate before giving the queue owner any rows.
        return result
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
            let source = MusicPublicBrowsePlatform(accountPlatform: platform)
            try requirePlatform(source.rawValue)
            let key = source.rawValue + ":" + MusicNativePublicBrowseDiscovery.key(destination), own = epoch
            let position = try MusicNativePublicBrowseCursor.read(cursor, key: key, epoch: own, now: now())
            guard let accounts else { throw MusicServiceError.authRequired }
            let remote = position.remote["cursor"].string
            let page = try await work(label: "publicBrowse") {
                try await accounts.nativeRecommendations(platform: platform, kind: "daily", cursor: remote.isEmpty ? nil : remote)
            }
            try check(own)
            try musicRequire(page.items.count <= 30 && page.items.allSatisfy { $0.platform == platform.rawValue }, .upstreamInvalid)
            let items = try page.items.map { raw -> MusicPublicBrowseItem in
                let track = try remember(MusicModel.normalize(raw))
                return .init(id: track.songId, title: track.title, subtitle: track.artist, artwork: track.artwork,
                             content: .track(track))
            }
            let next = try page.cursor.map { value in
                try musicRequire(!items.isEmpty && value != remote, .staleResult)
                return try position.encode(key: key, epoch: own, offset: position.offset + items.count,
                                           remote: .obj([("cursor", .string(value))]))
            }
            return .init(sections: [.init(id: key + ":\(position.offset)", title: "每日推荐", items: items)], cursor: next)
        }
        // Directory and channel reads belong to the current home source; a
        // channel from another directory never routes to this provider.
        if case .channel(let channel, _) = destination { try requireHomeSource(channel.platform) }
        let source = try currentHomeSource(), sourceRevision = homeSourceRevision
        if case .channel(let channel, _) = destination {
            try musicRequire(channel.platform == source, .staleResult)
        }
        let key = source.rawValue + ":" + MusicNativePublicBrowseDiscovery.key(destination) +
            ":" + sourceRevision.uuidString, own = epoch
        let position = try MusicNativePublicBrowseCursor.read(cursor, key: key, epoch: own, now: now())
        let port = publicPlatforms
        // Source changes cancel these reads with the rest of the home content.
        let result = try await work(label: "musicHome") {
            try await port.browseDiscovery(destination, source: source, remote: position.remote, offset: position.offset)
        }
        try checkHomeSource(source, revision: sourceRevision, epoch: own)
        try musicRequire(result.sections.reduce(0) { $0 + $1.items.count } <= 300, .limit)
        let sectionPrefix: String
        if case .channel(let channel, _) = destination, channel.kind == .cube {
            // A feed remains one section as its cursor advances. Request keys
            // still include the full group/algorithm identity for admission.
            sectionPrefix = MusicNativePublicBrowseDiscovery.key(.channel(channel, tag: "")) + ":"
        } else if result.stableSections {
            // Provider pages continue the same sections; the view merges by id.
            sectionPrefix = key + ":"
        } else { sectionPrefix = key + ":\(position.offset):" }
        let sections = try publicSections(result, prefix: sectionPrefix)
        let next = try result.next.map { remote in
            try musicRequire(remote != position.remote && !sections.isEmpty, .limit)
            return try position.encode(key: key, epoch: own, offset: position.offset + 1, remote: remote)
        }
        return .init(sections: sections, cursor: next, filters: result.filters, selectedFilter: result.selected,
                     notice: result.notice, consumed: result.consumed)
    }

    func publicSections(_ result: MusicNativePublicBrowseDiscovery, prefix: String,
                        binding: MusicNativeHomeBinding? = nil) throws -> [MusicPublicBrowseSection] {
        try musicRequire(result.sections.count <= 60 && result.sections.reduce(0, { count, section in
            count + (section.tabs.isEmpty ? section.items.count : section.tabs.reduce(0) { $0 + $1.items.count })
        }) <= 600, .limit)
        return try convertedPublicSections(result, prefix: prefix, binding: binding)
    }

    /// Already validated pages may accumulate beyond a single response's
    /// protocol limits. Conversion registers fresh handles, never authority.
    func convertedPublicSections(_ result: MusicNativePublicBrowseDiscovery, prefix: String,
                                 binding: MusicNativeHomeBinding? = nil) throws -> [MusicPublicBrowseSection] {
        return try result.sections.map { section -> MusicPublicBrowseSection in
            let tabs = try section.tabs.map { tab in
                let pending = tab.items.contains { item in
                    switch item.content {
                    case .songID: return true
                    // The selected tab was already enriched; an absent upstream
                    // cover there is final. Other tabs fill covers when chosen.
                    case .collection(let value):
                        return value.title.isEmpty || (tab.id != section.selectedTab && item.needsPlaylistSummary)
                    default: return false
                    }
                }
                if pending {
                    let own = epoch
                    return MusicPublicBrowseSectionTab(id: tab.id, title: tab.title, items: [],
                        loadItems: { [weak self] in
                            guard let self else { throw MusicServiceError.closed }
                            return try await self.publicTabItems(tab.items, layout: section.layout,
                                                                 binding: binding, epoch: own)
                        })
                }
                return MusicPublicBrowseSectionTab(id: tab.id, title: tab.title,
                                                   items: try publicItems(tab.items, layout: section.layout))
            }
            let items: [MusicPublicBrowseItem]
            if tabs.isEmpty { items = try publicItems(section.items, layout: section.layout) }
            else {
                guard let selected = tabs.first(where: { $0.id == section.selectedTab }) else {
                    throw MusicServiceError.upstreamInvalid
                }
                items = selected.items
            }
            return .init(id: prefix + section.id, title: section.title, items: items, layout: section.layout,
                         avatars: section.avatars, channel: section.channel,
                         filters: section.filters, selectedFilter: section.selectedFilter, error: section.error,
                         tabs: tabs, selectedTab: section.selectedTab)
        }
    }

    private func publicTabItems(_ items: [MusicNativePublicBrowseItem], layout: MusicPublicBrowseSection.Layout,
                                binding: MusicNativeHomeBinding?, epoch own: UUID) async throws -> [MusicPublicBrowseItem] {
        try check(own)
        if let binding { try await checkHomeBinding(binding, epoch: own) }
        let port = publicPlatforms, account = accounts as? any MusicNativeHomeProviding
        let metadata: (@Sendable (MusicNativeBrowseMetadataRequest) async throws -> Data)?
        if let binding, binding.loggedIn, let account {
            metadata = { request in
                switch request {
                case .songIDs(let ids): return try await account.nativeHomePayload(.songMetadata(ids: ids), binding: binding)
                case .playlistIDs(let ids): return try await account.nativeHomePayload(.playlistMetadata(ids: ids), binding: binding)
                }
            }
        } else { metadata = nil }
        let result = try await work(label: "publicBrowseTab") {
            try await port.browseEnrich(.init(sections: [.init(id: "tab", title: "", items: items, layout: layout)]),
                                       metadata: metadata)
        }
        if let binding { try await checkHomeBinding(binding, epoch: own) }
        try check(own)
        return try publicItems(result.sections.first?.items ?? [], layout: layout)
    }

    // Handle registration and the final admission check stay on this actor.
    private func publicItems(_ rows: [MusicNativePublicBrowseItem],
                             layout: MusicPublicBrowseSection.Layout) throws -> [MusicPublicBrowseItem] {
        try rows.map { item -> MusicPublicBrowseItem in
            let content: MusicPublicBrowseContent
            var title = item.title, subtitle = item.subtitle, artwork = item.artwork
            switch item.content {
            case .collection(let value): content = .collection(value)
            case .channel(let value): content = .channel(value)
            case .daily: content = .daily(.netease)
            case .unavailable: content = .unavailable
            case .action(let action): content = .action(action)
            case .song(let value):
                let track = try remember(MusicModel.normalize(value))
                content = .track(track)
                if title.isEmpty { title = track.title }
                if subtitle.isEmpty {
                    subtitle = [track.artist, layout == .vipSongs ? "" : track.album]
                        .filter { !$0.isEmpty }.joined(separator: " · ")
                }
                artwork = artwork ?? track.artwork
            case .songID: throw MusicServiceError.upstreamInvalid
            }
            let preview = try item.previewSongs.prefix(3).map { try remember(MusicModel.normalize($0)) }
            return .init(id: item.id, title: title, subtitle: subtitle, artwork: artwork, content: content,
                         ranking: item.ranking, previewSongs: preview, recommendationReason: item.recommendationReason,
                         coverLabel: item.coverLabel)
        }
    }
}

enum MusicNativePublicBrowseSupport {
    static func digestData(_ value: Data) -> String {
        SHA256.hash(data: value).map { String(format: "%02x", $0) }.joined()
    }
    static func digest(_ value: MJ) -> String {
        digestData(value.bytes)
    }
    static func artwork(_ value: MJ) -> URL? {
        guard value.isString, value.string.utf8.count <= 4096 else { return nil }
        return MusicArtworkURL.normalized(value.string)
    }
}
