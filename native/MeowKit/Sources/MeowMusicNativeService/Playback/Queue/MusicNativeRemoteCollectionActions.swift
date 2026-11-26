// SPDX-License-Identifier: GPL-3.0-only
// Native remote playlist detail actions.
import Foundation
import MeowMusicPlatform

extension NativeMusicService {
    public func saveRemoteCollection(source: MusicRemoteQueueSource, name: String,
                                     intent: MusicQueueAuthorization) async throws -> MusicCollection {
        try check(); try intent.check(); try source.authorization.check()
        try requirePlatform(source.context.platform)
        let name = name.trimmingCharacters(in: .whitespacesAndNewlines)
        try musicRequire(!name.isEmpty, .invalidResponse)
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
                title: name, creator: collection.creator, artwork: collection.artwork, count: collection.count,
                playCount: collection.playCount), intent: intent)
        }
    }

    public func filteredRemoteQueue(source: MusicRemoteQueueSource, query: String,
                                    intent: MusicQueueAuthorization) async throws -> MusicRemoteQueueSource {
        try check(); try intent.check(); try source.authorization.check()
        try requirePlatform(source.context.platform)
        _ = try remoteContext(source)
        let own = epoch
        _ = await bindQueueSource(source)
        try check(own); try intent.check(); try source.authorization.check()
        let references = try await work { try await source.completeReferences(intent: intent) }
        try check(own); try intent.check()
        let query = query.trimmingCharacters(in: .whitespacesAndNewlines).localizedLowercase
        try await work { try await source.validate() }
        try check(own); try intent.check(); try source.authorization.check()
        let state = try read()
        var matches: [String: Bool] = [:]
        let filtered = try references.filter { reference in
            try check(own); try intent.check(); try source.authorization.check()
            if let matched = matches[reference.songID] { return matched }
            let song = try repositorySong(reference.songID, state)
            let matched = MusicRemoteCollectionFilter.matches(title: song.title, artist: song.artist,
                album: song.album, query: query)
            matches[reference.songID] = matched
            return matched
        }
        try musicRequire(!filtered.isEmpty, .notFound)
        let child = try source.authorization.perform {
            try intent.perform {
                try check(own)
                let reader = MusicNativeFilteredCollectionRows(rows: filtered,
                    authorization: MusicQueueAuthorization(parent: source.authorization))
                try reader.authorization.check()
                let parent = source.context
                // A filtered FM preview is a finite queue, not an FM generator.
                let context = MusicRemoteQueueContext(kind: parent.kind == .fm ? .recommendation : parent.kind,
                    id: parent.id, title: parent.title, platform: parent.platform, collection: parent.collection)
                return MusicRemoteQueueSource(context: context, authorization: reader.authorization,
                    loadReferences: { cursor, _ in try reader.page(cursor) },
                    validate: { [weak self] in
                        guard let self else { throw MusicServiceError.closed }
                        try await self.check(own)
                        try await source.validate()
                        try await self.check(own)
                        try reader.authorization.check()
                    }, close: { reader.close() })
            }
        }
        _ = await bindQueueSource(child)
        do {
            try check(own); try intent.check(); try child.authorization.check()
            return child
        } catch { await child.close(); throw error }
    }
}

private final class MusicNativeFilteredCollectionRows: @unchecked Sendable {
    let authorization: MusicQueueAuthorization
    private let lock = NSLock()
    private var rows: [MusicQueueTrackReference]?

    init(rows: [MusicQueueTrackReference], authorization: MusicQueueAuthorization) {
        self.rows = rows; self.authorization = authorization
    }
    func page(_ cursor: String?) throws -> MusicQueueReferencePage {
        try Task.checkCancellation(); try authorization.check()
        lock.lock(); defer { lock.unlock() }
        guard let rows else { throw MusicServiceError.closed }
        let text = cursor ?? "0"
        let bytes = text.utf8
        let validCursor = text == "0" || (
            !bytes.isEmpty && bytes.count <= 18 &&
            (49...57).contains(bytes.first!) &&
            bytes.allSatisfy { (48...57).contains($0) }
        )
        try musicRequire(validCursor)
        guard let offset = Int(text), offset % 30 == 0, offset < rows.count else { throw MusicServiceError.staleResult }
        let end = min(offset + 30, rows.count)
        return .init(items: Array(rows[offset..<end]), total: rows.count, cursor: end < rows.count ? String(end) : nil)
    }
    func close() {
        authorization.revoke()
        lock.lock(); rows = nil; lock.unlock()
    }
    deinit {
        authorization.revoke()
    }
}
