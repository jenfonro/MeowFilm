import Foundation
import MeowMusicFeature

extension NativeMusicService {
    /// The only persistent song store. Sources keep their first registered
    /// owner; new sources match one candidate against its fixed baseline.
    func registerSongs(_ input: [MJ], library: inout MJ, authoritative: Bool = false, playback: MJ? = nil,
                       acceptObservation: ((String) -> Bool)? = nil,
                       project: ((String, MJ) -> Void)? = nil) throws -> [String] {
        guard !input.isEmpty else { return [] }
        let preferences = try playback ?? read().playback
        guard let writer = activeSongWriter else { throw MusicServiceError.storageConflict }
        let store = MusicNativeLibraryAccessReader(database: writer, access: access)
        let proposedInput = try input.map(MusicModel.cleanSong)
        var sourceOwners = try store.sourceOwners(keys: Set(proposedInput.flatMap { $0["sources"].array.map { $0["sourceKey"].string } }))
        let evidenceByInput = proposedInput.map { raw -> MusicRecordingIdentity? in
            guard !raw["sources"].array.isEmpty else { return nil }
            return MusicModel.descriptor(raw)
        }
        let lookupKeys = Set(proposedInput.indices.compactMap { index -> String? in
            guard proposedInput[index]["sources"].array.allSatisfy({ sourceOwners[$0["sourceKey"].string] == nil }) else { return nil }
            return evidenceByInput[index]?.lookupKey
        })
        var candidateIDs = try writer.candidateIDs(lookupKeys: lookupKeys)
        var result: [String] = []
        var previous: (input: MJ, id: String)?
        for (inputIndex, raw) in input.enumerated() {
            // Only adjacent identical registered observations are idempotent:
            // A/B/A must still apply the last A after B changes the source.
            if acceptObservation == nil, let previous, previous.input == raw {
                try access.check()
                result.append(previous.id)
                continue
            }
            let proposed = proposedInput[inputIndex], incoming = proposed["sources"].array
            // A retained projection is a reference, not a new observation.
            // Reading it must never restore fields removed by a newer result.
            let explicit = proposed["songId"].string
            if !authoritative, !explicit.isEmpty, let data = try store.songData(id: explicit) {
                let stored = try MJ.parse(data)
                try MusicLibraryValidation.validateSong(stored, id: explicit)
                project?(explicit, stored)
                result.append(explicit)
                previous = (raw, explicit)
                continue
            }
            let owners = incoming.map { sourceOwners[$0["sourceKey"].string] }
            let knownOwner = owners.compactMap { $0 }.first
            let evidence = evidenceByInput[inputIndex] ?? MusicModel.descriptor(proposed)
            let id: String
            if let knownOwner { id = knownOwner }
            else if try !explicit.isEmpty && store.containsSong(id: explicit) { id = explicit }
            else {
                let matches = try evidence.lookupKey.map { key in
                    try candidateIDs[key, default: []].filter { candidate in
                        guard let data = try store.songData(id: candidate) else { return false }
                        return MusicModel.descriptor(try MJ.parse(data)).matches(evidence)
                    }
                } ?? []
                id = matches.count == 1 ? matches[0] : UUID().uuidString.lowercased()
            }

            let existing = try store.songData(id: id).map { try MJ.parse($0) } ?? .null
            if existing != .null && acceptObservation?(id) == false {
                project?(id, existing)
                result.append(id)
                continue
            }
            var song = existing == .null ? proposed : existing
            song["songId"] = .string(id)
            let ownedSources = zip(incoming, owners).compactMap { source, owner in owner == nil || owner == id ? source : nil }
            let changedArtwork = ownedSources.filter { incoming in
                existing["sources"].array.first { $0["sourceKey"] == incoming["sourceKey"] }?["musicInfo"]["img"] != incoming["musicInfo"]["img"]
            }
            song = MusicModel.mergeCleanSources(song, adding: ownedSources, preferIncoming: authoritative)
            let currentPrimary = existing["sources"].array.first?["sourceKey"].string
            let incomingPrimary = incoming.first?["sourceKey"].string
            let updatesPrimary = authoritative && currentPrimary == incomingPrimary
            for (field, value) in proposed.fields where
                !["songId", "sources", "baselineDuration", "artworkSourceKey", "cover_url"].contains(field) {
                if proposed[field] != song[field] &&
                    (updatesPrimary || value != .null && (song[field] == .null || song[field] == .string(""))) {
                    song[field] = value
                }
            }
            if song["baselineDuration"] == .null, let duration = evidence.duration { song["baselineDuration"] = .number(duration) }
            selectSongArtwork(&song, playback: preferences, freshSources: authoritative ? changedArtwork : [])
            if existing == .null || song.stable != existing.stable {
                try writer.saveSong(song.bytes)
                for source in ownedSources { sourceOwners[source["sourceKey"].string] = id }
            }
            project?(id, song)
            result.append(id)
            if existing == .null { candidateIDs[evidence.lookupKey ?? "", default: []].append(id) }
            // Source-less inputs may have no matching evidence or stable ID.
            previous = incoming.isEmpty ? nil : (raw, id)
        }
        return result
    }

    func saveRepositorySong(_ song: MJ) throws {
        guard let writer = activeSongWriter else { throw MusicServiceError.storageConflict }
        try writer.saveSong(song.bytes)
    }
    func repositorySong(_ id: String, _ state: MusicDocument) throws -> MJ {
        let song = try state.song(id)
        guard song.isObject else { throw MusicServiceError.notFound }
        try musicRequire(UUID(uuidString: id) != nil && musicSame(song["songId"].string, id), .songIDConflict)
        _ = try MusicModel.sources(song)
        return song
    }
    func registeredSongID(_ value: MusicNativeTrackData) throws -> String {
        var info = try MJ.parse(value.data); info["source"] = .string(value.platform)
        let key = try MusicModel.sourceKey(info)
        guard let id = try repositoryReader.sourceOwner(key: key) else { throw MusicServiceError.notFound }
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
            var projected: [String: MJ] = [:]
            let ids = try registerSongs(input, library: &library, authoritative: authoritative, playback: state.playback,
                                        project: { projected[$0] = $1 })
            state.library["library"] = library
            return try ids.map { id in
                try access.check()
                guard let song = projected[id] else { throw MusicServiceError.storageConflict }
                return song
            }
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
        var normalized: [MJ] = [], recommendations: [Data?] = []
        var previous: (track: MusicNativeTrackData, song: MJ, recommendation: Data?)?
        for track in tracks {
            // Position belongs to the occurrence, not to either pure projection.
            if let previous, musicSame(previous.track.platform, track.platform), previous.track.data == track.data {
                normalized.append(previous.song); recommendations.append(previous.recommendation)
            } else {
                let song = try MusicModel.normalize(track)
                let recommendation = try MusicNativeSimilarData.recommendation(track)?.bytes
                normalized.append(song); recommendations.append(recommendation)
                previous = (track, song, recommendation)
            }
        }
        return try authorization.perform {
            try intent.perform {
                let songs = try adoptSongs(normalized, authoritative: true)
                return tracks.indices.map { i in
                    .init(songID: songs[i]["songId"].string, occurrenceID: UUID().uuidString.lowercased(),
                        platform: tracks[i].platform, sourceKey: normalized[i]["sources"].array[0]["sourceKey"].string,
                        sourcePosition: tracks[i].sourcePosition, recommendation: recommendations[i])
                }
            }
        }
    }
    func projectQueueTracks(_ references: [MusicQueueTrackReference], epoch own: UUID,
                            authorization: MusicQueueAuthorization) throws -> [MusicNativeTrackData] {
        try check(own); try authorization.check()
        let state = try read()
        var songs: [String: MJ] = [:]
        var previous: (reference: MusicQueueTrackReference, data: Data)?
        return try references.map { reference in
            try access.check()
            if let previous, musicSame(previous.reference.songID, reference.songID),
               musicSame(previous.reference.sourceKey, reference.sourceKey),
               previous.reference.recommendation == reference.recommendation {
                return .init(platform: reference.platform, data: previous.data, sourcePosition: reference.sourcePosition)
            }
            let song: MJ
            if let cached = songs[reference.songID] { song = cached }
            else { song = try repositorySong(reference.songID, state); songs[reference.songID] = song }
            guard let source = song["sources"].array.first(where: { $0["sourceKey"].string == reference.sourceKey }) else {
                throw MusicServiceError.notFound
            }
            var info = source["musicInfo"]
            if let recommendation = reference.recommendation { info["_recommendation"] = try MJ.parse(recommendation) }
            let data = info.bytes
            previous = (reference, data)
            return .init(platform: reference.platform, data: data, sourcePosition: reference.sourcePosition)
        }
    }
    func metadataReferencePage(_ page: MusicQueueReferencePage) throws -> MusicBrowsePage {
        let state = try read()
        var songs: [String: MJ] = [:]
        return .init(items: try page.items.map { reference in
            try access.check()
            let song: MJ
            if let cached = songs[reference.songID] { song = cached }
            else { song = try repositorySong(reference.songID, state); songs[reference.songID] = song }
            return try MusicModel.track(song,
                .init(kind: .library, id: reference.songID, occurrence: reference.occurrenceID), sourcePosition: reference.sourcePosition)
        }, cursor: page.cursor, dailyStyle: page.dailyStyle)
    }

    /// New sources do not rotate a chosen image. Only a fresh result for the
    /// selected source or an explicit metadata-policy change can replace it.
    func selectSongArtwork(_ song: inout MJ, playback: MJ, freshSources: [MJ] = [], force: Bool = false,
                           failedURLs: Set<String> = []) {
        let defaults = MusicPublicBrowsePlatform.allCases.map(\.rawValue)
        let savedOrder = playback["metadataPlatforms"].isArray ? playback["metadataPlatforms"].strings : defaults
        let savedEnabled = playback["metadataEnabled"].isArray ? playback["metadataEnabled"].strings : defaults
        let order = savedOrder + savedEnabled.filter { !savedOrder.contains($0) }
        let enabled = Set(savedEnabled)
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
            guard let data = try state.songRows.songData(id: id) else { return nil }
            let song = try MJ.parse(data)
            try musicRequire(UUID(uuidString: id) != nil && musicSame(song["songId"].string, id), .songIDConflict)
            return (id, try MusicModel.track(song, .init(kind: .library, id: id)))
        })
    }
    public func songChanges() async -> AsyncStream<Set<String>> {
        guard !closed else { return AsyncStream { $0.finish() } }
        let id = UUID()
        return AsyncStream(bufferingPolicy: .bufferingNewest(1)) { continuation in
            songObservers[id] = continuation
            continuation.onTermination = { [weak self] _ in
                Task { await self?.removeSongObserver(id) }
            }
        }
    }
    func emitSongChanges(_ ids: Set<String>) {
        for observer in songObservers.values {
            if case .dropped(let previous) = observer.yield(ids) {
                // Keep one pending batch, but never lose a different song's
                // update when a slow consumer has not read the previous batch.
                observer.yield(previous.union(ids))
            }
        }
    }
    private func removeSongObserver(_ id: UUID) { songObservers.removeValue(forKey: id) }
}
