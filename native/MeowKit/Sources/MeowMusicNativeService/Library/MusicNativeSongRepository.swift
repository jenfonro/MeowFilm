import Foundation
import MeowMusicFeature

extension NativeMusicService {
    /// The only persistent song store. Sources keep their first registered
    /// owner; new sources match one candidate against its fixed baseline.
    func registerSongs(_ input: [MJ], library: inout MJ, authoritative: Bool = false, playback: MJ? = nil) throws -> [String] {
        let preferences = try playback ?? read().playback
        var store = library["songs"], owners: [String: String] = [:], candidates: [String: Set<String>] = [:]
        for (id, song) in store.fields {
            for source in song["sources"].array { owners[source["sourceKey"].string] = id }
            if let key = MusicModel.descriptor(song).lookupKey { candidates[key, default: []].insert(id) }
        }
        var result: [String] = []
        for raw in input {
            let proposed = try MusicModel.cleanSong(raw), incoming = proposed["sources"].array
            // A retained projection is a reference, not a new observation.
            // Reading it must never restore fields removed by a newer result.
            let explicit = proposed["songId"].string
            if !authoritative, !explicit.isEmpty, store.contains(explicit) {
                result.append(explicit)
                continue
            }
            let knownOwner = incoming.compactMap { owners[$0["sourceKey"].string] }.first
            let evidence = MusicModel.descriptor(proposed)
            let matches = evidence.lookupKey.map { key in
                (candidates[key] ?? []).filter { MusicModel.descriptor(store[$0]).matches(evidence) }
            } ?? []
            let id: String
            if let knownOwner { id = knownOwner }
            else if !explicit.isEmpty && store.contains(explicit) { id = explicit }
            else if matches.count == 1 { id = matches.first! }
            else { id = UUID().uuidString.lowercased() }

            let existing = store[id]
            var song = existing == .null ? proposed : existing
            let oldKey = MusicModel.descriptor(song).lookupKey
            song["songId"] = .string(id)
            let ownedSources = incoming.filter { owners[$0["sourceKey"].string].map { $0 == id } ?? true }
            let changedArtwork = ownedSources.filter { incoming in
                existing["sources"].array.first { $0["sourceKey"] == incoming["sourceKey"] }?["musicInfo"]["img"] != incoming["musicInfo"]["img"]
            }
            song = try MusicModel.mergeSources(song, adding: ownedSources, preferIncoming: authoritative)
            let currentPrimary = existing["sources"].array.first?["sourceKey"].string
            let incomingPrimary = incoming.first?["sourceKey"].string
            let updatesPrimary = authoritative && currentPrimary == incomingPrimary
            for field in ["title", "artist", "album", "duration", "artistNames"] {
                if proposed[field] != song[field] &&
                    (updatesPrimary || proposed[field] != .null && (song[field] == .null || song[field] == .string(""))) {
                    song[field] = proposed[field]
                }
            }
            if song["baselineDuration"] == .null, let duration = evidence.duration { song["baselineDuration"] = .number(duration) }
            selectSongArtwork(&song, playback: preferences, freshSources: authoritative ? changedArtwork : [])
            if let oldKey { candidates[oldKey]?.remove(id) }
            if let key = MusicModel.descriptor(song).lookupKey { candidates[key, default: []].insert(id) }
            store[id] = song
            for source in song["sources"].array { owners[source["sourceKey"].string] = id }
            result.append(id)
        }
        library["songs"] = store
        return result
    }

    func repositorySong(_ id: String, _ state: MusicDocument) throws -> MJ {
        let song = state.library["library"]["songs"][id]
        guard song.isObject else { throw MusicServiceError.notFound }
        return song
    }
    func registeredSongID(_ value: MusicNativeTrackData, library: MJ) throws -> String {
        let key = try MusicModel.normalize(value)["sources"].array[0]["sourceKey"].string
        guard let id = library["songs"].fields.first(where: {
            $0.1["sources"].array.contains { $0["sourceKey"].string == key }
        })?.0 else { throw MusicServiceError.notFound }
        return id
    }
    func entrySong(_ entry: MJ, _ state: MusicDocument) throws -> MJ {
        try repositorySong(entry["songId"].string, state)
    }
    func repositoryTrack(_ song: MJ, occurrence: String? = nil, sourcePosition: Int? = nil) throws -> MusicRemoteTrack {
        let current = try repositorySong(song["songId"].string, read())
        return try MusicModel.track(current, .init(kind: .library, id: current["songId"].string, occurrence: occurrence),
                                    sourcePosition: sourcePosition)
    }

    func adoptSongs(_ input: [MJ], authoritative: Bool = false) throws -> [MJ] {
        guard !input.isEmpty else { return [] }
        return try transaction(["library"], reportingCancellationAfterCommit: false) { state in
            var library = state.library["library"]
            let ids = try registerSongs(input, library: &library, authoritative: authoritative, playback: state.playback)
            state.library["library"] = library
            return ids.map { library["songs"][$0] }
        }
    }

    func bindQueueSource(_ source: MusicRemoteQueueSource) async -> MusicRemoteQueueSource {
        let own = epoch, authorization = source.authorization
        await source.bindSongRepository(adopt: { [weak self] tracks, intent in
            guard let self else { throw MusicServiceError.closed }
            return try await self.adoptQueueTracks(tracks, epoch: own, authorization: authorization, intent: intent)
        }, project: { [weak self] references in
            guard let self else { throw MusicServiceError.closed }
            return try await self.projectQueueTracks(references, epoch: own, authorization: authorization)
        })
        return source
    }
    func adoptQueueTracks(_ tracks: [MusicNativeTrackData], epoch own: UUID,
                          authorization: MusicQueueAuthorization, intent: MusicQueueAuthorization) throws -> [MusicQueueTrackReference] {
        try check(own)
        try musicRequire(tracks.reduce(0) { $0 + $1.data.count } <= MusicRemoteQueueSource.maximumBytes, .limit)
        let normalized = try tracks.map(MusicModel.normalize)
        let recommendations = try tracks.map(MusicNativeSimilarData.recommendation)
        return try authorization.perform {
            try intent.perform {
                let songs = try adoptSongs(normalized, authoritative: true)
                return tracks.indices.map { i in
                    .init(songID: songs[i]["songId"].string, occurrenceID: UUID().uuidString.lowercased(),
                        platform: tracks[i].platform, sourceKey: normalized[i]["sources"].array[0]["sourceKey"].string,
                        sourcePosition: tracks[i].sourcePosition, recommendation: recommendations[i]?.bytes,
                        byteCount: tracks[i].data.count)
                }
            }
        }
    }
    func projectQueueTracks(_ references: [MusicQueueTrackReference], epoch own: UUID,
                            authorization: MusicQueueAuthorization) throws -> [MusicNativeTrackData] {
        try check(own); try authorization.check()
        let state = try read()
        return try references.map { reference in
            let song = try repositorySong(reference.songID, state)
            guard let source = song["sources"].array.first(where: { $0["sourceKey"].string == reference.sourceKey }) else {
                throw MusicServiceError.notFound
            }
            var info = source["musicInfo"]
            if let recommendation = reference.recommendation { info["_recommendation"] = try MJ.parse(recommendation) }
            return .init(platform: reference.platform, data: info.bytes, sourcePosition: reference.sourcePosition)
        }
    }
    func metadataReferencePage(_ page: MusicQueueReferencePage) throws -> MusicBrowsePage {
        let state = try read()
        return .init(items: try page.items.map {
            try MusicModel.track(repositorySong($0.songID, state),
                .init(kind: .library, id: $0.songID, occurrence: $0.occurrenceID), sourcePosition: $0.sourcePosition)
        }, cursor: page.cursor, dailyStyle: page.dailyStyle)
    }

    /// New sources do not rotate a chosen image. Only a fresh result for the
    /// selected source or an explicit metadata-policy change can replace it.
    func selectSongArtwork(_ song: inout MJ, playback: MJ, freshSources: [MJ] = [], force: Bool = false,
                           failedURLs: Set<String> = []) {
        let defaults = MusicPublicBrowsePlatform.allCases.map(\.rawValue)
        let order = playback["metadataPlatforms"].isArray ? playback["metadataPlatforms"].strings : defaults
        let enabled = Set(playback["metadataEnabled"].isArray ? playback["metadataEnabled"].strings : defaults)
        let rows = song["sources"].array.filter {
            enabled.contains($0["source"].string) && platformEnabled($0["source"].string) &&
                !failedURLs.contains($0["musicInfo"]["img"].string)
        }
        let selected = song["artworkSourceKey"].string
        if !force, !selected.isEmpty, rows.contains(where: { $0["sourceKey"].string == selected }) {
            if let fresh = freshSources.first(where: { $0["sourceKey"].string == selected }) {
                song["cover_url"] = MusicModel.publicURL(fresh["musicInfo"]["img"])
            }
            if !song["cover_url"].string.isEmpty { return }
        }
        let entrance = (freshSources.first(where: { fresh in rows.contains { $0["sourceKey"] == fresh["sourceKey"] } }) ?? rows.first).flatMap { row in
            row["musicInfo"]["img"].string.isEmpty ? nil : row
        }
        let preferred = order.lazy.compactMap { platform in
            rows.first { $0["source"].string == platform && !$0["musicInfo"]["img"].string.isEmpty }
        }.first
        if let row = force ? preferred : entrance ?? preferred {
            song["artworkSourceKey"] = row["sourceKey"]
            song["cover_url"] = MusicModel.publicURL(row["musicInfo"]["img"])
        } else { song["artworkSourceKey"] = .null; song["cover_url"] = .string("") }
    }
}

extension NativeMusicService: MusicSongRepositoryReading {
    public func repositoryTracks(songIDs: Set<String>) async throws -> [String: MusicRemoteTrack] {
        let state = try read()
        return try Dictionary(uniqueKeysWithValues: songIDs.compactMap { id in
            guard state.library["library"]["songs"][id].isObject else { return nil }
            return (id, try MusicModel.track(state.library["library"]["songs"][id], .init(kind: .library, id: id)))
        })
    }
    public func songChanges() async -> AsyncStream<Void> {
        guard !closed else { return AsyncStream { $0.finish() } }
        let id = UUID()
        return AsyncStream(bufferingPolicy: .bufferingNewest(1)) { continuation in
            songObservers[id] = continuation
            continuation.onTermination = { [weak self] _ in
                Task { await self?.removeSongObserver(id) }
            }
        }
    }
    private func removeSongObserver(_ id: UUID) { songObservers.removeValue(forKey: id) }
}
