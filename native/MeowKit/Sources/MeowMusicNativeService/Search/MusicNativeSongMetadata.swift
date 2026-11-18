import Foundation
import MeowMusicFeature

extension NativeMusicService {
    func sourceDiscoveryQuery(_ song: MJ) -> String { song["title"].string + " " + song["artist"].string }
    func beginSourceDiscoveryObservation(platform: String, query: String, page: Int = 1, limit: Int = 30,
        context: String = "public", scope: MusicSearchObservation.Scope = .recording) -> MusicSearchObservation {
        sourceDiscoveryObservationOrder += 1
        let at = now().timeIntervalSince1970 * 1000
        return .init(startedAt: at, order: sourceDiscoveryObservationOrder, checkedAt: at,
                     platform: platform, query: query, context: context, page: page, limit: limit, scope: scope, outcome: .incomplete)
    }
    func finishSourceDiscoveryObservation(_ request: MusicSearchObservation, failure: MusicServiceError? = nil) -> MusicSearchObservation {
        request.result(failure == nil ? .incomplete : .failed, checkedAt: now().timeIntervalSince1970 * 1000, failure: failure)
    }
    func retainSongMetadata(_ songs: [MJ]) async throws { _ = try adoptSongs(songs) }
    func sharedSongMetadata(_ song: MJ) async throws -> MJ { try await sharedSongMetadata([song])[0] }
    func sharedSongMetadata(_ songs: [MJ]) async throws -> [MJ] { try adoptSongs(songs) }
    func currentSongMetadata(_ song: MJ) throws -> MJ { try repositorySong(song["songId"].string, read()) }
    func projectSongMetadata(_ songs: [MJ]) async throws -> [MJ] {
        let state = try read()
        return try songs.map { try repositorySong($0["songId"].string, state) }
    }
    func metadataPage(_ value: MusicNativeTrackPage) async throws -> MusicBrowsePage {
        let candidates = value.items.map { try? MusicModel.normalize($0) }
        let songs = try adoptSongs(candidates.compactMap { $0 }, authoritative: true)
        return .init(items: try displayTracks(value.items, candidates: candidates, songs: songs),
            cursor: value.cursor, dailyStyle: value.dailyStyle)
    }
    func displayTracks(_ tracks: [MusicNativeTrackData], candidates: [MJ?], songs: [MJ]) throws -> [MusicRemoteTrack] {
        var adopted = songs.makeIterator()
        return try zip(tracks, candidates).map { track, candidate in
            guard candidate != nil, let song = adopted.next() else { return MusicModel.displayTrack(track) }
            return try repositoryTrack(song, occurrence: UUID().uuidString, sourcePosition: track.sourcePosition)
        }
    }
    func metadataTracks(_ songs: [MJ], cursor: String? = nil) throws -> MusicBrowsePage {
        .init(items: try songs.map { try repositoryTrack($0, occurrence: UUID().uuidString) }, cursor: cursor)
    }
    func searchObservationIsDedicated(_ observation: MusicSearchObservation, song: MJ) -> Bool {
        observation.scope == .recording || observation.page == 1 &&
            MusicRecordingIdentity.normalize(observation.query) == MusicRecordingIdentity.normalize(sourceDiscoveryQuery(song))
    }
    @discardableResult
    func recordSearchPage(_ page: MusicNativeTrackPage, observation: MusicSearchObservation) async throws -> [MJ] {
        let own = epoch
        let rows = page.items.compactMap { try? MusicModel.normalize($0) }
        // A single snapshot keeps row decisions together; no await intervenes
        // between the final owner read and applying the complete page.
        let checks = await sourceDiscovery.entries()
        try check(own)
        let songs = try adoptSearchSongs(rows, observation: observation, checks: checks)
        let found = Set(songs.map { $0["songId"].string })
        let state = try read()
        let dedicated = try state.songRows.querySongIDs(query: MusicRecordingIdentity.normalize(observation.query))
        let known = try found.union(dedicated).map { try state.song($0) }
        for song in known where found.contains(song["songId"].string) || searchObservationIsDedicated(observation, song: song) {
            let id = song["songId"].string
            let keys = found.contains(id) ? song["sources"].array.filter { $0["source"].string == observation.platform }.map { $0["sourceKey"].string } : []
            noteSourceObservation(observation, songID: id)
            await sourceDiscovery.observe(songID: id, observation: observation.result(.incomplete,
                scope: searchObservationIsDedicated(observation, song: song) ? .recording : .page), sourceKeys: keys)
            try check(own)
        }
        scheduleSourceDiscoveryPersist()
        return songs
    }
    func adoptSearchSongs(_ rows: [MJ], observation: MusicSearchObservation,
                          checks: [String: [String: MusicSourceDiscoveryEntry]]) throws -> [MJ] {
        let songs = try transaction(["library"], reportingCancellationAfterCommit: false) { state in
            var library = state.library["library"]
            let ids = try registerSongs(rows, library: &library, authoritative: true, playback: state.playback,
                acceptObservation: { id in
                    // Apply ordering to the same indexed match used by adoption;
                    // an unknown source is never matched twice.
                    self.sourceDiscoveryLatest[id]?[observation.platform]?.isNewer(than: observation) != true &&
                        checks[id]?[observation.platform]?.observations.contains(where: {
                            $0.isNewer(than: observation)
                        }) != true
                })
            state.library["library"] = library
            return try ids.map { try state.song($0) }
        }
        for song in songs { noteSourceObservation(observation, songID: song["songId"].string) }
        return songs
    }
    func noteSourceObservation(_ observation: MusicSearchObservation, songID: String) {
        if let previous = sourceDiscoveryLatest[songID]?[observation.platform], !observation.isNewer(than: previous) { return }
        sourceDiscoveryLatest[songID, default: [:]][observation.platform] = observation
    }
    func recordSearchFailure(_ request: MusicSearchObservation, failure: MusicServiceError) async {
        guard !Task.isCancelled, ![.cancelled, .closed, .accessUnavailable, .staleAccess].contains(failure),
              let state = try? read() else { return }
        let own = epoch, observation = finishSourceDiscoveryObservation(request, failure: failure)
        guard let ids = try? state.songRows.querySongIDs(query: MusicRecordingIdentity.normalize(observation.query)) else { return }
        for id in ids {
            guard let song = try? state.song(id), searchObservationIsDedicated(observation, song: song) else { continue }
            noteSourceObservation(observation, songID: song["songId"].string)
            await sourceDiscovery.observe(songID: song["songId"].string, observation: observation.result(.failed, scope: .recording, failure: failure), sourceKeys: [])
            if (try? check(own)) == nil { return }
        }
        scheduleSourceDiscoveryPersist()
    }
}

extension NativeMusicService: MusicSearchObserving {
    public func observeSearchPages(_ pages: [MusicSearchPage], references: [MusicReference]) async throws {
        let own = epoch, state = try read()
        let songs = try references.map { try song($0, state) }
        for page in pages {
            guard let observation = page.observation else { continue }
            let found = Set(page.items.map(\.songId))
            for target in songs where searchObservationIsDedicated(observation, song: target) {
                let id = target["songId"].string
                let keys = found.contains(id) ? target["sources"].array.filter { $0["source"].string == page.platform }.map { $0["sourceKey"].string } : []
                noteSourceObservation(observation, songID: id)
                await sourceDiscovery.observe(songID: id, observation: observation.result(.incomplete, scope: .recording), sourceKeys: keys)
                try check(own)
            }
        }
        scheduleSourceDiscoveryPersist()
    }
}
