// SPDX-License-Identifier: GPL-3.0-only
// Native detail actions from iWebPlus b900c259 browse.js / browse-data.js.
import Foundation
import MeowMusicFeature

extension NativeMusicService: MusicRemoteCollectionServing {
    public func saveRemoteCollection(source: MusicRemoteQueueSource, name: String,
                                     intent: MusicQueueAuthorization) async throws -> MusicCollection {
        try check(); try intent.check(); try source.authorization.check()
        try requirePlatform(source.context.platform)
        let name = name.trimmingCharacters(in: .whitespacesAndNewlines)
        _ = try musicText(name, maximum: 600)
        _ = try remoteContext(source)
        guard source.context.kind == .playlist, let collection = source.context.collection, collection.kind == .playlist,
              collection.platform.rawValue == source.context.platform else { throw MusicServiceError.unsupported }
        let own = epoch
        try await work { try await source.validate() }
        try check(own); try intent.check(); try source.authorization.check()
        // All suspended work is over. Account/source and UI intent revocation
        // serialize with the one existing protected-document transaction.
        return try source.authorization.perform {
            try saveNetworkPlaylist(.init(platform: collection.platform, nativeID: collection.nativeID,
                title: name, creator: collection.creator, artwork: collection.artwork, count: collection.count), intent: intent)
        }
    }

    public func filteredRemoteQueue(source: MusicRemoteQueueSource, query: String,
                                    intent: MusicQueueAuthorization) async throws -> MusicRemoteQueueSource {
        try check(); try intent.check(); try source.authorization.check()
        try requirePlatform(source.context.platform)
        try musicRequire(query.utf8.count <= 4096, .limit)
        _ = try remoteContext(source)
        let own = epoch
        let raw = try await work { try await source.completeTracks(intent: intent) }
        try check(own); try intent.check()
        try musicRequire(raw.allSatisfy { $0.platform == source.context.platform }, .invalidResponse)
        let query = query.trimmingCharacters(in: .whitespacesAndNewlines).localizedLowercase
        var filtered: [MusicNativeTrackData] = []
        var songs: [MJ] = []
        for row in raw {
            try intent.check()
            // Validate every row, even one the query would otherwise exclude.
            // Do not use identity normalization (NFKC/diacritic folding) for a
            // literal UI search, or merge repeated queue occurrences.
            let song = try MusicModel.normalize(row)
            songs.append(song)
            if MusicRemoteCollectionFilter.matches(title: song["title"].string, artist: song["artist"].string,
                                                    album: song["album"].string, query: query) { filtered.append(row) }
        }
        try await work { try await source.validate() }
        try check(own); try intent.check(); try source.authorization.check()
        try await retainSongMetadata(songs)
        try check(own); try intent.check()
        try musicRequire(!filtered.isEmpty, .notFound)
        return try source.authorization.perform {
            try intent.perform {
                try check(own)
                let reader = MusicNativeFilteredCollectionRows(rows: filtered, access: access,
                    authorization: MusicQueueAuthorization(parent: source.authorization))
                reader.observeAccess()
                try reader.authorization.check()
                let parent = source.context
                // A filtered FM preview is a finite queue, not an FM generator.
                let context = MusicRemoteQueueContext(kind: parent.kind == .fm ? .recommendation : parent.kind,
                    id: parent.id, title: parent.title, platform: parent.platform, collection: parent.collection)
                return MusicRemoteQueueSource(context: context, authorization: reader.authorization,
                    loadPage: { try reader.page($0) },
                    validate: { [weak self] in
                        guard let self else { throw MusicServiceError.closed }
                        try await self.check(own)
                        try await source.validate()
                        try await self.check(own)
                        try reader.authorization.check()
                    }, close: { reader.close() })
            }
        }
    }
}

/// Only owns the derived rows and its access registration. There is no account
/// lease, parent close, background observer task, global registry or media work.
/// Weak registration + deinit also releases resources if a prepared result is
/// discarded before the UI accepts it, rather than requiring close to prevent
/// the access fence from retaining an abandoned snapshot.
private final class MusicNativeFilteredCollectionRows: @unchecked Sendable {
    let authorization: MusicQueueAuthorization
    private let lock = NSLock()
    private let access: MusicNativeAccess
    private let registration = UUID()
    private var rows: [MusicNativeTrackData]?

    init(rows: [MusicNativeTrackData], access: MusicNativeAccess, authorization: MusicQueueAuthorization) {
        self.rows = rows; self.access = access; self.authorization = authorization
    }
    func observeAccess() {
        access.register(registration) { [weak self] in self?.close() }
    }
    func page(_ cursor: String?) throws -> MusicNativeTrackPage {
        try Task.checkCancellation(); try authorization.check()
        lock.lock(); defer { lock.unlock() }
        guard let rows else { throw MusicServiceError.closed }
        let text = cursor ?? "0"
        try musicRequire(text.range(of: "^(0|[1-9][0-9]{0,17})$", options: .regularExpression) != nil)
        guard let offset = Int(text), offset % 30 == 0, offset < rows.count else { throw MusicServiceError.staleResult }
        let end = min(offset + 30, rows.count)
        return .init(items: Array(rows[offset..<end]), total: rows.count, cursor: end < rows.count ? String(end) : nil)
    }
    func close() {
        authorization.revoke()
        lock.lock(); rows = nil; lock.unlock()
        access.remove(registration)
    }
    deinit {
        authorization.revoke()
        access.remove(registration)
    }
}
