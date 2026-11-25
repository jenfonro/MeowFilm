import Foundation
import MeowMusicFeature

extension NativeMusicService {
    /// The only persistent song store. Sources keep their first registered
    /// owner; new sources match one candidate against its fixed baseline.
    func registerSongs(_ input: [MusicSongRecord],
                       library: inout MusicLibraryState,
                       authoritative: Bool = false,
                       playback: MusicPlaybackSettingsState? = nil,
                       acceptObservation: ((String) -> Bool)? = nil,
                       project: ((String, MusicSongRecord) -> Void)? = nil) throws -> [String] {
        guard !input.isEmpty else { return [] }
        let preferences = try playback ?? read().playback
        guard let writer = activeSongWriter else { throw MusicServiceError.storageConflict }
        let store: any MusicNativeLibraryReading = writer
        let proposedInput = try input.map(MusicModel.cleanSong)
        var sourceOwners = try store.sourceOwners(keys: Set(proposedInput.flatMap { $0.sources.map(\.sourceKey) }))
        let evidenceByInput = proposedInput.map { raw -> MusicRecordingIdentity? in
            guard !raw.sources.isEmpty else { return nil }
            return MusicModel.descriptor(raw)
        }
        let matchKeys = Set(proposedInput.indices.compactMap { index -> String? in
            guard proposedInput[index].sources.allSatisfy({ sourceOwners[$0.sourceKey] == nil }) else { return nil }
            return evidenceByInput[index]?.lookupKey
        })
        var candidateIDs = try writer.candidateIDs(matchKeys: matchKeys)
        var result: [String] = []
        var previous: (input: MusicSongRecord, id: String)?
        for (inputIndex, raw) in input.enumerated() {
            if acceptObservation == nil, let previous, previous.input == raw {
                try access.check()
                result.append(previous.id)
                continue
            }
            let proposed = proposedInput[inputIndex], incoming = proposed.sources
            let explicit = proposed.songID
            if !authoritative, !explicit.isEmpty, let stored = try store.song(id: explicit) {
                project?(explicit, stored)
                result.append(explicit)
                previous = (raw, explicit)
                continue
            }
            let owners = incoming.map { sourceOwners[$0.sourceKey] }
            let knownOwner = owners.compactMap { $0 }.first
            let evidence = evidenceByInput[inputIndex] ?? MusicModel.descriptor(proposed)
            let id: String
            if let knownOwner { id = knownOwner }
            else if try !explicit.isEmpty && store.containsSong(id: explicit) { id = explicit }
            else {
                let matches = try evidence.lookupKey.map { key in
                    try candidateIDs[key, default: []].filter { candidate in
                        guard let stored = try store.song(id: candidate) else { return false }
                        return MusicModel.descriptor(stored).matches(evidence)
                    }
                } ?? []
                id = matches.count == 1 ? matches[0] : UUID().uuidString.lowercased()
            }

            let existing = try store.song(id: id)
            if let existing, acceptObservation?(id) == false {
                project?(id, existing)
                result.append(id)
                continue
            }
            var song = existing ?? proposed
            song.songID = id
            let ownedSources = zip(incoming, owners).compactMap { source, owner in owner == nil || owner == id ? source : nil }
            let changedArtwork = ownedSources.filter { inc in
                existing?.sources.first { $0.sourceKey == inc.sourceKey }?.img != inc.img
            }
            song = MusicModel.mergeCleanSources(song, adding: ownedSources, preferIncoming: authoritative)
            let currentPrimary = existing?.sources.first?.sourceKey
            let incomingPrimary = incoming.first?.sourceKey
            let updatesPrimary = authoritative && currentPrimary == incomingPrimary
            if existing != nil {
                if proposed.title != song.title && (updatesPrimary || (!proposed.title.isEmpty && song.title.isEmpty)) {
                    song.title = proposed.title
                }
                if proposed.artist != song.artist && (updatesPrimary || (!proposed.artist.isEmpty && song.artist.isEmpty)) {
                    song.artist = proposed.artist
                }
                if proposed.album != song.album && (updatesPrimary || (!proposed.album.isEmpty && song.album.isEmpty)) {
                    song.album = proposed.album
                }
                if proposed.duration != song.duration && (updatesPrimary || (proposed.duration != nil && song.duration == nil)) {
                    song.duration = proposed.duration
                }
                if proposed.artistNames != song.artistNames && (updatesPrimary || (!proposed.artistNames.isEmpty && song.artistNames.isEmpty)) {
                    song.artistNames = proposed.artistNames
                }
                if proposed.artworkSourceImage != song.artworkSourceImage && (updatesPrimary || (proposed.artworkSourceImage != nil && song.artworkSourceImage == nil)) {
                    song.artworkSourceImage = proposed.artworkSourceImage
                }
            }
            if song.baselineDuration == nil, let duration = evidence.duration {
                song.baselineDuration = duration
            }
            selectSongArtwork(&song, playback: preferences, freshSources: authoritative ? changedArtwork : [])
            if existing == nil || song != existing {
                try saveRepositorySong(song)
                for source in ownedSources { sourceOwners[source.sourceKey] = id }
            }
            project?(id, song)
            result.append(id)
            if let matchKey = MusicModel.descriptor(song).lookupKey, !matchKey.isEmpty {
                var list = candidateIDs[matchKey, default: []]
                if !list.contains(id) {
                    list.append(id)
                    candidateIDs[matchKey] = list
                }
            }
            previous = incoming.isEmpty ? nil : (raw, id)
        }
        return result
    }

    func saveRepositorySong(_ song: MusicSongRecord) throws {
        guard let writer = activeSongWriter else { throw MusicServiceError.storageConflict }
        try MusicLibraryValidation.validateSong(song, id: song.songID)
        let evidence = MusicModel.descriptor(song)
        let query = MusicRecordingIdentity.normalize(song.title + " " + song.artist)
        var normalized = song
        if normalized.baselineDuration == nil, let duration = evidence.duration, duration.isFinite, duration > 0 {
            normalized.baselineDuration = duration
        }
        try writer.saveSong(normalized, matchKey: evidence.lookupKey, searchQuery: query)
    }

    func repositorySong(_ id: String, _ state: MusicDomainState) throws -> MusicSongRecord {
        try state.song(id)
    }

    func repositorySongMap(_ ids: [String], _ state: MusicDomainState) throws -> [String: MusicSongRecord] {
        try state.projectSongs(ids)
    }

    func repositorySongs(_ ids: [String], _ state: MusicDomainState) throws -> [MusicSongRecord] {
        let batch = try repositorySongMap(ids, state)
        return try ids.map { id in
            guard let song = batch[id] else { throw MusicServiceError.notFound }
            return song
        }
    }

    func registeredSongID(_ value: MusicNativeTrackData) throws -> String {
        let key = try MusicModel.sourceKey(fromTrackData: value)
        guard let id = try repositoryReader.sourceOwner(key: key) else { throw MusicServiceError.notFound }
        return id
    }

    func entrySong(_ entry: MusicQueueEntryRecord, _ state: MusicDomainState) throws -> MusicSongRecord {
        try repositorySong(entry.songID, state)
    }

    func repositoryTrack(_ song: MusicSongRecord, occurrence: String? = nil, sourcePosition: Int? = nil) throws -> MusicRemoteTrack {
        let current = try repositorySong(song.songID, read())
        return try MusicModel.track(current, .init(kind: .library, id: current.songID, occurrence: occurrence),
                                    sourcePosition: sourcePosition)
    }

    func adoptSongs(_ input: [MusicSongRecord], authoritative: Bool = false) throws -> [MusicSongRecord] {
        guard !input.isEmpty else { return [] }
        return try transaction([.library], reportingCancellationAfterCommit: false) { state in
            var projected: [String: MusicSongRecord] = [:]
            let ids = try registerSongs(input, library: &state.library, authoritative: authoritative, playback: state.playback,
                                        project: { projected[$0] = $1 })
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
        var normalized: [MusicSongRecord] = [], recommendations: [Data?] = []
        var previous: (track: MusicNativeTrackData, song: MusicSongRecord, recommendation: Data?)?
        for track in tracks {
            if let previous, previous.track.platform == track.platform, previous.track.data == track.data {
                normalized.append(previous.song); recommendations.append(previous.recommendation)
            } else {
                let song = try MusicModel.normalize(track)
                let recommendation = try MusicWireCodec.extractRecommendationBytes(from: track.data)
                normalized.append(song); recommendations.append(recommendation)
                previous = (track, song, recommendation)
            }
        }
        return try authorization.perform {
            try intent.perform {
                let songs = try adoptSongs(normalized, authoritative: true)
                return tracks.indices.map { i in
                    .init(songID: songs[i].songID, occurrenceID: UUID().uuidString.lowercased(),
                          platform: tracks[i].platform, sourceKey: normalized[i].sources[0].sourceKey,
                          sourcePosition: tracks[i].sourcePosition, recommendation: recommendations[i])
                }
            }
        }
    }

    func projectQueueTracks(_ references: [MusicQueueTrackReference], epoch own: UUID,
                            authorization: MusicQueueAuthorization) throws -> [MusicNativeTrackData] {
        try check(own); try authorization.check()
        let state = try read()
        let songs = try state.songRows.songs(ids: Set(references.map(\.songID)))
        var previous: (reference: MusicQueueTrackReference, data: Data)?
        return try references.map { reference in
            try access.check()
            if let previous, previous.reference.songID == reference.songID,
               previous.reference.sourceKey == reference.sourceKey,
               previous.reference.recommendation == reference.recommendation {
                return .init(platform: reference.platform, data: previous.data, sourcePosition: reference.sourcePosition)
            }
            guard let song = songs[reference.songID] else { throw MusicServiceError.notFound }
            guard let source = song.sources.first(where: { $0.sourceKey == reference.sourceKey }) else {
                throw MusicServiceError.notFound
            }
            let data = try MusicWireCodec.buildMusicInfoData(source, recommendation: reference.recommendation)
            previous = (reference, data)
            return .init(platform: reference.platform, data: data, sourcePosition: reference.sourcePosition)
        }
    }

    func metadataReferencePage(_ page: MusicQueueReferencePage) throws -> MusicBrowsePage {
        let state = try read()
        let songs = try state.songRows.songs(ids: Set(page.items.map(\.songID)))
        return .init(items: try page.items.map { reference in
            try access.check()
            guard let song = songs[reference.songID] else { throw MusicServiceError.notFound }
            return try MusicModel.track(
                song,
                .init(kind: .library, id: reference.songID, occurrence: reference.occurrenceID),
                sourcePosition: reference.sourcePosition
            )
        }, cursor: page.cursor, dailyStyle: page.dailyStyle)
    }

    /// New sources do not rotate a chosen image. Only a fresh result for the
    /// selected source or an explicit metadata-policy change can replace it.
    func selectSongArtwork(_ song: inout MusicSongRecord,
                           playback: MusicPlaybackSettingsState,
                           freshSources: [MusicSongSourceRecord] = [],
                           force: Bool = false,
                           failedURLs: Set<String> = []) {
        let savedOrder = playback.metadataPlatforms
        let savedEnabled = playback.metadataEnabled
        let order = savedOrder + savedEnabled.filter { !savedOrder.contains($0) }
        let enabled = Set(savedEnabled)
        let rows = song.sources.filter {
            enabled.contains($0.platform) && platformEnabled($0.platform) &&
                !failedURLs.contains($0.img ?? "")
        }
        let selected = song.artworkSourceKey ?? ""
        if !force, !selected.isEmpty, rows.contains(where: { $0.sourceKey == selected }) {
            if let fresh = freshSources.first(where: { $0.sourceKey == selected }) {
                song.coverURL = MusicModel.publicURLString(fresh.img ?? "")
            }
            if !song.coverURL.isEmpty { return }
        }
        let preferred = order.lazy.compactMap { platform in
            rows.first { $0.platform == platform && !($0.img ?? "").isEmpty }
        }.first
        if let row = preferred {
            song.artworkSourceKey = row.sourceKey
            song.coverURL = MusicModel.publicURLString(row.img ?? "")
        } else {
            song.artworkSourceKey = nil
            song.coverURL = ""
        }
    }
}

extension NativeMusicService: MusicSongRepositoryReading {
    public func repositoryTracks(songIDs: Set<String>) async throws -> [String: MusicRemoteTrack] {
        let state = try read()
        let batch = try state.songRows.songs(ids: songIDs)
        return try Dictionary(uniqueKeysWithValues: batch.map { id, song in
            (id, try MusicModel.track(song, .init(kind: .library, id: id)))
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
                observer.yield(previous.union(ids))
            }
        }
    }

    private func removeSongObserver(_ id: UUID) { songObservers.removeValue(forKey: id) }
}
