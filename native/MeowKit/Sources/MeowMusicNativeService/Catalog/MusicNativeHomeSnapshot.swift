import Foundation
import MeowMusicFeature
import MeowStorage

/// Disposable page display state. Songs are permanent repository references;
/// no credentials, media bytes or continuation authority are stored here.
public protocol MusicHomeSnapshotStoring: Sendable {
    func read() throws -> Data?
    func write(_ data: Data) throws
    func clear() throws
}

public final class MusicNativeHomeSnapshotDocument: MusicHomeSnapshotStoring, @unchecked Sendable {
    public static let namespace = "music.home.display.v1"
    private let lock = NSLock()
    private let factory: @Sendable () throws -> DocumentStore
    private var store: DocumentStore?
    public init(storeFactory: @escaping @Sendable () throws -> DocumentStore) { factory = storeFactory }
    private func storage() throws -> DocumentStore {
        if let store { return store }
        let value = try factory(); store = value; return value
    }
    public func read() throws -> Data? {
        lock.lock(); defer { lock.unlock() }
        do {
            let data = try storage().read(key: "last-success")
            guard (data?.count ?? 0) <= MusicNativeHomeSnapshot.maximumBytes else { return nil }
            return data
        } catch { throw MusicServiceError.storageFailed }
    }
    public func write(_ data: Data) throws {
        guard data.count <= MusicNativeHomeSnapshot.maximumBytes else { throw MusicServiceError.limit }
        lock.lock(); defer { lock.unlock() }
        do { try storage().write(data, key: "last-success") }
        catch { throw MusicServiceError.storageFailed }
    }
    public func clear() throws {
        lock.lock(); defer { lock.unlock() }
        do { try storage().remove(key: "last-success") }
        catch { throw MusicServiceError.storageFailed }
    }
}

enum MusicNativeHomeSnapshot {
    static let maximumBytes = 8 * 1024 * 1024

    static func references(_ page: MusicNativePublicBrowseDiscovery, sourceOwner: (String) throws -> String?) throws -> MusicNativePublicBrowseDiscovery {
        func id(_ track: MusicNativeTrackData) throws -> String? {
            var info = try MJ.parse(track.data); info["source"] = .string(track.platform)
            guard let key = try? MusicModel.sourceKey(info) else { return nil }
            return try sourceOwner(key)
        }
        func items(_ rows: [MusicNativePublicBrowseItem]) throws -> [MusicNativePublicBrowseItem] {
            try rows.map { row in
                var item = row
                if case .song(let track) = item.content {
                    item.content = try id(track).map(MusicNativePublicBrowseContent.record) ?? .unavailable
                }
                if let ranking = item.ranking {
                    item.ranking = .init(preview: try ranking.preview.map { entry in
                        guard let track = item.previewSongs.first(where: {
                            (try? MusicModel.normalize($0)["sources"].array.first?["musicInfo"]["songmid"].string) == entry.id
                        }) else { return entry }
                        guard let reference = try id(track) else { return entry }
                        return .init(id: reference, title: entry.title, artist: entry.artist, artwork: entry.artwork,
                            rank: entry.rank, lastRank: entry.lastRank)
                    }, songArtwork: item.previewSongs.isEmpty && item.previewSongIDs.isEmpty ? ranking.songArtwork : nil,
                       updateFrequency: ranking.updateFrequency)
                }
                if item.previewSongIDs.isEmpty {
                    var displayOnly: [MusicNativeTrackData] = []
                    for track in item.previewSongs {
                        let reference = try id(track)
                        item.previewSongIDs.append(reference)
                        if reference == nil { displayOnly.append(track) }
                    }
                    item.previewSongs = displayOnly
                }
                return item
            }
        }
        var result = page
        result.sections = try page.sections.map { row in
            var section = row
            section.items = row.tabs.isEmpty ? try items(row.items) : []
            section.tabs = try row.tabs.map { .init(id: $0.id, title: $0.title, items: try items($0.items)) }
            return section
        }
        return result
    }
    static func encode(_ page: MusicNativePublicBrowseDiscovery, source: MusicPublicBrowsePlatform) throws -> Data {
        let value = HomeDisplay(source: source, sections: try page.sections.map(HomeSection.init))
        let data = try JSONEncoder().encode(value)
        try musicRequire(data.count <= maximumBytes, .limit)
        return data
    }
    static func decode(_ data: Data, source: MusicPublicBrowsePlatform) throws -> MusicNativePublicBrowseDiscovery? {
        let value = try read(data)
        return value.source == source ? value.page : nil
    }
    static func restore(_ data: Data) throws -> (source: MusicPublicBrowsePlatform, page: MusicNativePublicBrowseDiscovery) {
        let value = try read(data)
        return (value.source, value.page)
    }
    private static func read(_ data: Data) throws -> HomeDisplay {
        try musicRequire(data.count <= maximumBytes, .limit)
        return try JSONDecoder().decode(HomeDisplay.self, from: data)
    }
}

private struct HomeDisplay: Codable {
    let source: MusicPublicBrowsePlatform
    let sections: [HomeSection]
    var page: MusicNativePublicBrowseDiscovery { .init(sections: sections.map(\.native)) }
}
private struct HomeSection: Codable {
    let id: String
    let title: String
    let items: [HomeItem]
    let layout: MusicPublicBrowseSection.Layout
    let avatars: [URL]
    let channel: MusicPublicBrowseChannel?
    let filters: [MusicPublicBrowseFilter]
    let selectedFilter: String
    let tabs: [HomeTab]
    let selectedTab: String
    init(_ value: MusicNativePublicBrowseSection) throws {
        id = value.id; title = value.title; layout = value.layout
        items = try value.items.map(HomeItem.init); avatars = value.avatars
        channel = value.channel; filters = value.filters; selectedFilter = value.selectedFilter
        tabs = try value.tabs.map(HomeTab.init); selectedTab = value.selectedTab
    }
    var native: MusicNativePublicBrowseSection {
        .init(id: id, title: title, items: items.map(\.native), layout: layout, avatars: avatars,
              channel: channel, filters: filters, selectedFilter: selectedFilter,
              tabs: tabs.map(\.native), selectedTab: selectedTab)
    }
}
private struct HomeTab: Codable {
    let id: String
    let title: String
    let items: [HomeItem]
    init(_ value: MusicNativePublicBrowseSectionTab) throws {
        id = value.id; title = value.title; items = try value.items.map(HomeItem.init)
    }
    var native: MusicNativePublicBrowseSectionTab { .init(id: id, title: title, items: items.map(\.native)) }
}
private enum HomeContent: Codable {
    case collection(MusicPublicBrowseCollection), record(String), channel(MusicPublicBrowseChannel)
    case daily, unavailable, action(MusicHomeAction)
    init(_ value: MusicNativePublicBrowseContent) throws {
        switch value {
        case .collection(let value):
            self = .collection(.init(platform: value.platform, nativeID: value.nativeID, kind: value.kind,
                title: value.title, creator: value.creator,
                artwork: MusicArtworkURL.normalized(MusicModel.publicURL(.string(value.artwork?.absoluteString ?? "")).string),
                count: value.count))
        case .record(let id): self = .record(id)
        case .channel(let channel): self = .channel(channel)
        case .daily: self = .daily
        case .unavailable: self = .unavailable
        case .action(let action): self = .action(action)
        case .song: throw MusicServiceError.invalidRequest
        }
    }
    var native: MusicNativePublicBrowseContent {
        switch self {
        case .collection(let value): return .collection(value)
        case .record(let id): return .record(id)
        case .channel(let value): return .channel(value)
        case .daily: return .daily
        case .unavailable: return .unavailable
        case .action(let value): return .action(value)
        }
    }
}
private struct HomeItem: Codable {
    let id: String
    let title: String
    let subtitle: String
    let artwork: URL?
    let content: HomeContent
    let ranking: MusicPublicBrowseRanking?
    let previewSongIDs: [String?]
    let recommendationReason: String
    let coverLabel: String
    let requiresAccount: Bool
    let kind: MusicPublicBrowseItem.Kind?
    let label: String
    let presentation: String
    let coverRef: MusicPublicBrowseItem.CoverReference?
    let displayOnlyPreviews: [MusicNativeTrackData.Display]
    init(_ value: MusicNativePublicBrowseItem) throws {
        id = value.id; content = try HomeContent(value.content)
        if case .record = value.content, value.kind == nil { title = ""; subtitle = ""; artwork = nil }
        else {
            title = value.title; subtitle = value.subtitle
            artwork = MusicNativePublicBrowseSupport.artwork(MusicModel.publicURL(.string(value.artwork?.absoluteString ?? "")))
        }
        ranking = value.ranking; previewSongIDs = value.previewSongIDs
        recommendationReason = value.recommendationReason; coverLabel = value.coverLabel
        requiresAccount = value.requiresAccount
        kind = value.kind; label = value.label; presentation = value.presentation; coverRef = value.coverRef
        displayOnlyPreviews = value.previewSongs.compactMap(\.display)
    }
    var native: MusicNativePublicBrowseItem {
        .init(id: id, title: title, subtitle: subtitle, artwork: artwork, content: content.native,
              ranking: ranking, previewSongs: displayOnlyPreviews.map {
                  .init(platform: "", data: Data("null".utf8), display: $0)
              }, recommendationReason: recommendationReason, coverLabel: coverLabel,
              previewSongIDs: previewSongIDs, requiresAccount: requiresAccount,
              kind: kind, label: label, presentation: presentation, coverRef: coverRef)
    }
}
