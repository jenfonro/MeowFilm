import Foundation
import MeowMusicFeature
import MeowStorage
import MeowRuntime

/// Disposable page display state. Songs are permanent repository references;
/// no credentials, media bytes or continuation authority are stored here.
public protocol MusicHomeSnapshotStoring: Sendable {
    func read() throws -> Data?
    func write(_ data: Data) throws
    func clear() throws
}

public final class MusicNativeHomeSnapshotDocument: MusicHomeSnapshotStoring, @unchecked Sendable {
    private let database: SQLiteDatabase
    public init(database: SQLiteDatabase) { self.database = database }
    public func read() throws -> Data? {
        do {
            let data = try database.readSync { db in
                try SQLiteDatabase.readSnapshot(db) { db in
                    try MusicStorageRecords.music_home_display.read(db, key: [.text("display")]).map { try RuntimeJSON.encode($0) }
                }
            }
            guard (data?.count ?? 0) <= MusicNativeHomeSnapshot.maximumBytes else { return nil }
            return data
        } catch { throw MusicServiceError.storageFailed }
    }
    public func write(_ data: Data) throws {
        guard data.count <= MusicNativeHomeSnapshot.maximumBytes else { throw MusicServiceError.limit }
        do {
            let value = try RuntimeJSON.decode(data), record = MusicStorageRecords.music_home_display
            try database.transactionSync(ownedBytes: data.count) { db in
                let key: [SQLiteValue] = [.text("display")]
                try record.save(db, value: value, previous: record.read(db, key: key), key: key)
            }
        }
        catch { throw MusicServiceError.storageFailed }
    }
    public func clear() throws {
        do { try database.transactionSync { try $0.execute("DELETE FROM music_home_display") } }
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
    private enum CodingKeys: String, CodingKey { case kind, collection, record, channel, action }
    init(from decoder: Decoder) throws {
        let fields = try decoder.container(keyedBy: CodingKeys.self)
        switch try fields.decode(String.self, forKey: .kind) {
        case "collection": self = .collection(try fields.decode(MusicPublicBrowseCollection.self, forKey: .collection))
        case "record": self = .record(try fields.decode(String.self, forKey: .record))
        case "channel": self = .channel(try fields.decode(MusicPublicBrowseChannel.self, forKey: .channel))
        case "action": self = .action(try fields.decode(MusicHomeAction.self, forKey: .action))
        case "daily": self = .daily
        case "unavailable": self = .unavailable
        default: throw MusicServiceError.corruptDocument
        }
    }
    func encode(to encoder: Encoder) throws {
        var fields = encoder.container(keyedBy: CodingKeys.self)
        switch self {
        case .collection(let value): try fields.encode("collection", forKey: .kind); try fields.encode(value, forKey: .collection)
        case .record(let value): try fields.encode("record", forKey: .kind); try fields.encode(value, forKey: .record)
        case .channel(let value): try fields.encode("channel", forKey: .kind); try fields.encode(value, forKey: .channel)
        case .action(let value): try fields.encode("action", forKey: .kind); try fields.encode(value, forKey: .action)
        case .daily: try fields.encode("daily", forKey: .kind)
        case .unavailable: try fields.encode("unavailable", forKey: .kind)
        }
    }
    init(_ value: MusicNativePublicBrowseContent) throws {
        switch value {
        case .collection(let value):
            self = .collection(.init(platform: value.platform, nativeID: value.nativeID, kind: value.kind,
                title: value.title, creator: value.creator,
                artwork: MusicArtworkURL.normalized(MusicModel.publicURL(.string(value.artwork?.absoluteString ?? "")).string),
                count: value.count, playCount: value.playCount))
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
    let contextText: String
    let playCount: Int?
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
        contextText = value.contextText; playCount = value.playCount
        kind = value.kind; label = value.label; presentation = value.presentation; coverRef = value.coverRef
        displayOnlyPreviews = value.previewSongs.compactMap(\.display)
    }
    var native: MusicNativePublicBrowseItem {
        .init(id: id, title: title, subtitle: subtitle, artwork: artwork, content: content.native,
              ranking: ranking, previewSongs: displayOnlyPreviews.map {
                  .init(platform: "", data: Data("null".utf8), display: $0)
              }, recommendationReason: recommendationReason, coverLabel: coverLabel,
              previewSongIDs: previewSongIDs, requiresAccount: requiresAccount,
              kind: kind, label: label, contextText: contextText, playCount: playCount,
              presentation: presentation, coverRef: coverRef)
    }
}
