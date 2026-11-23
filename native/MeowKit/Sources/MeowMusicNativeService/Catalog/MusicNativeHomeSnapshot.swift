import Foundation
import MeowDebug
import MeowMusicFeature
import MeowStorage

/// Disposable page display state. Songs are permanent repository references;
/// no credentials, media bytes or continuation authority are stored here.
public protocol MusicHomeSnapshotStoring: Sendable {
    func clear() throws
}

protocol MusicHomeDisplaySnapshotStoring: MusicHomeSnapshotStoring {
    func readDisplay() throws -> (source: MusicPublicBrowsePlatform, page: MusicNativePublicBrowseDiscovery)?
    func writeDisplay(_ page: MusicNativePublicBrowseDiscovery, source: MusicPublicBrowsePlatform) throws
}

public final class MusicNativeHomeSnapshotStorage: MusicHomeDisplaySnapshotStoring, @unchecked Sendable {
    private let database: SQLiteDatabase
    public init(database: SQLiteDatabase) { self.database = database }
    func readDisplay() throws -> (source: MusicPublicBrowsePlatform, page: MusicNativePublicBrowseDiscovery)? {
        do {
            return try database.readSync { db in
                try SQLiteDatabase.readSnapshot(db) { db in
                    try MusicHomeSnapshotStore.read(db)
                }
            }
        } catch { throw MusicServiceError.storageFailed }
    }
    func writeDisplay(_ page: MusicNativePublicBrowseDiscovery, source: MusicPublicBrowsePlatform) throws {
        let bytes = MusicHomeSnapshotStore.estimatedBytes(page)
        guard bytes <= MusicNativeHomeSnapshot.maximumBytes else { throw MusicServiceError.limit }
        do {
            try database.transactionSync { db in
                try MusicHomeSnapshotStore.write(db, page: page, source: source)
            }
        }
        catch { throw MusicServiceError.storageFailed }
    }
    public func clear() throws {
        do { try database.transactionSync { try MusicHomeSnapshotStore.clear($0) } }
        catch { throw MusicServiceError.storageFailed }
    }
    public func debugStats() -> DebugJSONValue {
        let (hasState, sectionCount, itemCount): (Bool, Int, Int) = (try? database.readSync { db in
            let s = (try db.queryOne("SELECT count(*) FROM ios_music_home_state") { try $0.integer(0) } ?? 0) > 0
            let sec = try db.queryOne("SELECT count(*) FROM ios_music_home_sections") { try $0.integer(0) } ?? 0
            let itm = try db.queryOne("SELECT count(*) FROM ios_music_home_items") { try $0.integer(0) } ?? 0
            return (s, sec, itm)
        }) ?? (false, 0, 0)
        return .object([
            "hasSnapshot": .bool(hasState),
            "sectionCount": .int(sectionCount),
            "itemCount": .int(itemCount),
            "maximumBytes": .int(MusicNativeHomeSnapshot.maximumBytes)
        ])
    }
}

enum MusicNativeHomeSnapshot {
    static let maximumBytes = 8 * 1024 * 1024

    static func references(_ page: MusicNativePublicBrowseDiscovery, sourceOwner: (String) throws -> String?) throws -> MusicNativePublicBrowseDiscovery {
        func id(_ track: MusicNativeTrackData) throws -> String? {
            guard let key = try? MusicModel.sourceKey(fromTrackData: track) else { return nil }
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
                            (try? MusicModel.normalize($0).sources.first?.resolvedSongmid) == entry.id
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
}
