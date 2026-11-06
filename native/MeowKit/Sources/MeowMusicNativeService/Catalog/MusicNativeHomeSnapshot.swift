import Foundation
import MeowMusicFeature
import MeowStorage

/// A disposable music-only display cache. It is not the music library and has
/// no authority to save credentials, media URLs, handles or continuation tokens.
public protocol MusicHomeSnapshotStoring: Sendable {
    func read() throws -> Data?
    func write(_ data: Data) throws
    func clear() throws
}

public final class MusicNativeHomeSnapshotDocument: MusicHomeSnapshotStoring, @unchecked Sendable {
    public static let namespace = "music.home.snapshot.v1"
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
    static let maximumBytes = 400_000
    private static func image(_ url: URL?) -> MJ {
        guard let url else { return .null }
        let value = MusicModel.publicURL(.string(url.absoluteString))
        return value.string.isEmpty ? .null : value
    }
    private static func text(_ value: MJ, maximum: Int = 1024) throws -> String {
        try value.text(maximum, empty: true)
    }
    private static func channel(_ value: MusicPublicBrowseChannel) -> MJ {
        .obj([("id", .string(value.id)), ("title", .string(value.title)),
              ("kind", .string(value.kind.rawValue)), ("pageCode", .string(value.pageCode)),
              ("platform", .string(value.platform.rawValue))])
    }
    private static func channel(_ value: MJ, source: MusicPublicBrowsePlatform) throws -> MusicPublicBrowseChannel {
        try value.requireFields(["id", "title", "kind", "pageCode", "platform"],
                                required: ["id", "title", "kind", "pageCode", "platform"])
        guard let kind = MusicPublicBrowseChannel.Kind(rawValue: value["kind"].string),
              MusicPublicBrowsePlatform(rawValue: value["platform"].string) == source else {
            throw MusicServiceError.invalidResponse
        }
        return .init(id: try value["id"].text(200), title: try text(value["title"]), kind: kind,
                     pageCode: try text(value["pageCode"], maximum: 100), platform: source)
    }
    private static func cleanTrack(_ raw: MusicNativeTrackData, source: MusicPublicBrowsePlatform) throws -> MJ {
        try musicRequire(raw.platform == source.rawValue, .invalidResponse)
        let song = try MusicModel.normalize(raw)
        guard let row = song["sources"].array.first else { throw MusicServiceError.sourceRequired }
        var locator = try MusicModel.locator(MusicModel.sourceData(row))
        locator["name"] = song["title"]; locator["singer"] = song["artist"]
        locator["albumName"] = song["album"]; locator["duration"] = song["duration"]
        locator["img"] = image(MusicNativePublicBrowseSupport.artwork(song["cover_url"]))
        if locator["img"] == .null { locator.remove("img") }
        try MusicModel.rejectSecrets(locator)
        return locator
    }
    /// A collection keeps the native ID form of the snapshot's own source.
    private static func collectionID(_ value: MJ, source: MusicPublicBrowsePlatform) throws -> String {
        guard source == .kugou else { return try MusicPlatformProtocol.numericID(value) }
        let id = value.string
        try musicRequire(id.range(of: "^[A-Za-z0-9_-]{1,200}$", options: .regularExpression) != nil &&
                         !id.hasPrefix("id_"), .invalidResponse)
        return id
    }
    static func encode(_ page: MusicNativePublicBrowseDiscovery, source: MusicPublicBrowsePlatform, scope: String) throws -> Data {
        try musicRequire(source == .netease || source == .kugou, .unsupported)
        try musicRequire(!scope.isEmpty && scope.utf8.count <= 256 && page.sections.count <= 60 &&
                         page.sections.reduce(0, { $0 + $1.items.count }) <= 600, .limit)
        let sections = try page.sections.map { section in
            MJ.obj([("id", .string(section.id)), ("title", .string(section.title)), ("layout", .string(section.layout.rawValue)),
                ("avatars", .array(section.avatars.prefix(3).map { image($0) })),
                ("channel", section.channel.map { Self.channel($0) } ?? .null),
                ("items", .array(try section.items.map { item in
                    var value = MJ.obj([("id", .string(item.id)), ("title", .string(item.title)),
                        ("subtitle", .string(item.subtitle)), ("artwork", image(item.artwork))])
                    if !item.recommendationReason.isEmpty { value["recommendationReason"] = .string(item.recommendationReason) }
                    if !item.coverLabel.isEmpty { value["coverLabel"] = .string(item.coverLabel) }
                    if let ranking = item.ranking {
                        value["ranking"] = .obj([
                            ("updateFrequency", .string(ranking.updateFrequency)), ("songArtwork", image(ranking.songArtwork)),
                            ("preview", .array(ranking.preview.prefix(3).map { row in
                                .obj([("id", .string(row.id)), ("title", .string(row.title)), ("artist", .string(row.artist)),
                                      ("artwork", image(row.artwork)), ("rank", row.rank.map(MJ.n) ?? .null),
                                      ("lastRank", row.lastRank.map(MJ.n) ?? .null)])
                            }))
                        ])
                    }
                    if !item.previewSongs.isEmpty {
                        value["previewSongs"] = .array(try item.previewSongs.prefix(3).map { try cleanTrack($0, source: source) })
                    }
                    switch item.content {
                    case .collection(let row):
                        try musicRequire(row.platform == source)
                        value["kind"] = .string("collection")
                        value["collection"] = .obj([("id", .string(row.nativeID)), ("kind", .string(row.kind.rawValue)),
                            ("title", .string(row.title)), ("creator", .string(row.creator)),
                            ("artwork", image(row.artwork)), ("count", row.count.map(MJ.n) ?? .null)])
                    case .song(let row):
                        value["kind"] = .string("song"); value["song"] = try cleanTrack(row, source: source)
                    case .channel(let row):
                        value["kind"] = .string("channel")
                        value["channel"] = Self.channel(row)
                    case .daily: value["kind"] = .string("daily")
                    case .unavailable: value["kind"] = .string("unavailable")
                    case .action(let action):
                        value["kind"] = .string("action")
                        value["action"] = try MJ.parse(JSONEncoder().encode(action), maximum: 32_000)
                    case .songID: throw MusicServiceError.invalidResponse
                    }
                    return value
                }))])
        }
        let root = MJ.obj([("schema", .n(2)), ("source", .string(source.rawValue)),
                           ("scope", .string(scope)), ("sections", .array(sections))])
        try MusicModel.rejectSecrets(root)
        let data = root.bytes
        try musicRequire(data.count <= maximumBytes, .limit)
        // Validate the complete serialized shape before replacing last success.
        _ = try decode(data, source: source, scope: scope)
        return data
    }
    static func decode(_ data: Data, source: MusicPublicBrowsePlatform, scope: String) throws -> MusicNativePublicBrowseDiscovery? {
        let root = try MJ.parse(data, maximum: maximumBytes)
        try root.requireFields(["schema", "source", "scope", "sections"], required: ["schema", "source", "scope", "sections"])
        try musicRequire(root["schema"] == .n(2))
        try MusicModel.rejectSecrets(root)
        guard root["source"].string == source.rawValue, root["scope"].string == scope else { return nil }
        try musicRequire(source == .netease || source == .kugou, .unsupported)
        try musicRequire(!scope.isEmpty && scope.utf8.count <= 256 &&
                         root["sections"].isArray && root["sections"].array.count <= 60, .limit)
        var count = 0, ids = Set<String>()
        let sections = try root["sections"].array.map { section -> MusicNativePublicBrowseSection in
            try section.requireFields(["id", "title", "layout", "items", "avatars", "channel"], required: ["id", "title", "layout", "items"])
            let id = try section["id"].text(1024), title = try text(section["title"])
            guard let layout = MusicPublicBrowseSection.Layout(rawValue: section["layout"].string) else { throw MusicServiceError.invalidResponse }
            try musicRequire(ids.insert(id).inserted && section["items"].isArray, .invalidResponse)
            count += section["items"].array.count; try musicRequire(count <= 600, .limit)
            var itemIDs = Set<String>()
            let items = try section["items"].array.map { item -> MusicNativePublicBrowseItem in
                try item.requireFields(["id", "title", "subtitle", "artwork", "kind", "collection", "song", "channel", "action",
                                        "ranking", "previewSongs", "recommendationReason", "coverLabel"],
                                       required: ["id", "title", "subtitle", "artwork", "kind"])
                let id = try item["id"].text(1024)
                try musicRequire(itemIDs.insert(id).inserted)
                let content: MusicNativePublicBrowseContent
                switch item["kind"].string {
                case "collection":
                    let value = item["collection"]
                    try value.requireFields(["id", "kind", "title", "creator", "artwork", "count"],
                                            required: ["id", "kind", "title", "creator", "artwork", "count"])
                    guard let kind = MusicPublicBrowseCollection.Kind(rawValue: value["kind"].string) else { throw MusicServiceError.invalidResponse }
                    content = .collection(.init(platform: source,
                        nativeID: try collectionID(value["id"], source: source), kind: kind,
                        title: try text(value["title"]), creator: try text(value["creator"]),
                        artwork: MusicNativePublicBrowseSupport.artwork(value["artwork"]),
                        count: value["count"] == .null ? nil : try value["count"].integer(1_000_000)))
                case "song":
                    let raw = MusicNativeTrackData(platform: source.rawValue, data: item["song"].bytes)
                    _ = try cleanTrack(raw, source: source); content = .song(raw)
                case "channel":
                    content = .channel(try Self.channel(item["channel"], source: source))
                case "daily": content = .daily
                case "unavailable": content = .unavailable
                case "action":
                    content = .action(try JSONDecoder().decode(MusicHomeAction.self, from: item["action"].bytes))
                default: throw MusicServiceError.invalidResponse
                }
                let ranking: MusicPublicBrowseRanking?
                if item["ranking"] == .null { ranking = nil }
                else {
                    let value = item["ranking"]
                    try value.requireFields(["preview", "songArtwork", "updateFrequency"],
                                            required: ["preview", "songArtwork", "updateFrequency"])
                    try musicRequire(value["preview"].isArray && value["preview"].array.count <= 3)
                    let preview = try value["preview"].array.map { row -> MusicPublicBrowseRanking.Entry in
                        try row.requireFields(["id", "title", "artist", "artwork", "rank", "lastRank"],
                                              required: ["id", "title", "artist", "artwork", "rank", "lastRank"])
                        let rank = row["rank"] == .null ? nil : try row["rank"].integer(Int.max)
                        let lastRank = row["lastRank"] == .null ? nil : try row["lastRank"].integer(Int.max)
                        try musicRequire(rank == nil || rank! > 0)
                        let id = try text(row["id"], maximum: 18)
                        try musicRequire(id.isEmpty || MusicNativeHomePresentation.positiveID(.string(id)) != nil)
                        return .init(id: id, title: try text(row["title"]), artist: try text(row["artist"]),
                                     artwork: MusicNativePublicBrowseSupport.artwork(image(MusicNativePublicBrowseSupport.artwork(row["artwork"]))),
                                     rank: rank, lastRank: lastRank)
                    }
                    ranking = .init(preview: preview,
                                    songArtwork: MusicNativePublicBrowseSupport.artwork(image(MusicNativePublicBrowseSupport.artwork(value["songArtwork"]))),
                                    updateFrequency: try text(value["updateFrequency"]))
                }
                let previews = item["previewSongs"]
                try musicRequire(previews == .null || previews.isArray && previews.array.count <= 3)
                let songs = try previews.array.map { value -> MusicNativeTrackData in
                    let raw = MusicNativeTrackData(platform: source.rawValue, data: value.bytes)
                    // Only locators and display fields survive restore; no ref,
                    // cursor or playback authority is ever serialized.
                    return .init(platform: source.rawValue, data: try cleanTrack(raw, source: source).bytes)
                }
                return .init(id: id, title: try text(item["title"]), subtitle: try text(item["subtitle"]),
                             artwork: MusicNativePublicBrowseSupport.artwork(item["artwork"]), content: content,
                             ranking: ranking, previewSongs: songs,
                             recommendationReason: item["recommendationReason"] == .null ? "" : try text(item["recommendationReason"]),
                             coverLabel: item["coverLabel"] == .null ? "" : try text(item["coverLabel"], maximum: 800))
            }
            let avatars = section["avatars"]
            try musicRequire(avatars == .null || avatars.isArray && avatars.array.count <= 3)
            let channel: MusicPublicBrowseChannel?
            if section["channel"] == .null { channel = nil }
            else {
                channel = try Self.channel(section["channel"], source: source)
            }
            return .init(id: id, title: title, items: items, layout: layout,
                         avatars: avatars.array.compactMap(MusicNativePublicBrowseSupport.artwork), channel: channel)
        }
        return .init(sections: sections)
    }
}
