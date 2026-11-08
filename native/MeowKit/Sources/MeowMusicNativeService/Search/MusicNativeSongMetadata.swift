// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowMusicFeature

extension NativeMusicService {
    func sourceDiscoveryQuery(_ song: MJ) -> String {
        String((song["title"].string + " " + song["artist"].string).prefix(200))
    }

    func beginSourceDiscoveryObservation(platform: String, query: String, page: Int = 1, limit: Int = 30,
        context: String = "public", scope: MusicSearchObservation.Scope = .recording) -> MusicSearchObservation {
        sourceDiscoveryObservationOrder += 1
        let at = now().timeIntervalSince1970 * 1000
        return .init(startedAt: at, order: sourceDiscoveryObservationOrder, checkedAt: at,
                     platform: platform, query: query, context: context, page: page, limit: limit,
                     scope: scope, outcome: .incomplete)
    }

    func finishSourceDiscoveryObservation(_ request: MusicSearchObservation,
        failure: MusicServiceError? = nil) -> MusicSearchObservation {
        request.result(failure == nil ? .incomplete : .failed,
                       checkedAt: now().timeIntervalSince1970 * 1000, failure: failure)
    }

    /// Import only already acquired, validated recording facts. No query time is
    /// manufactured and no URL, account authorization or queue identity is owned here.
    func retainSongMetadata(_ songs: [MJ]) async throws {
        try check()
        let own = epoch
        var seen = Set<Data>()
        var metadata: [MusicSourceDiscoveryMetadata] = []
        for raw in songs {
            let song = try MusicModel.cleanSong(raw), descriptor = MusicModel.descriptor(song)
            guard !descriptor.fingerprint.isEmpty else { continue }
            for platform in MusicNativeSearchProtocol.platforms {
                let rows = song["sources"].array.filter {
                    $0["source"].string == platform && MusicNativeCandidateDiscoveryMerge.metadataComplete($0)
                }
                guard !rows.isEmpty else { continue }
                let key = MJ.array([.string(descriptor.descriptorKey), .array(rows)]).bytes
                guard seen.insert(key).inserted else { continue }
                metadata.append(.init(recording: .init(fingerprint: descriptor.fingerprint, duration: descriptor.duration),
                    platform: platform, sources: rows.map {
                        .init(sourceKey: $0["sourceKey"].string, source: platform, musicInfo: $0["musicInfo"].json)
                    }))
            }
        }
        guard !metadata.isEmpty else { return }
        let changed = await sourceDiscovery.include(metadata)
        try check(own)
        if changed { scheduleSourceDiscoveryPersist() }
    }

    func retainLibrarySongMetadata() async throws {
        let state = try read(), revision = state.library["revision"].int
        guard sourceDiscoveryLibraryRevision != revision else { return }
        try await retainSongMetadata(state.library["library"]["songs"].fields.map(\.1))
        // Reentry can have committed another library revision. Remember only
        // the revision actually consumed; the next read imports any newer one.
        if try read().library["revision"].int == revision { sourceDiscoveryLibraryRevision = revision }
    }

    func sharedSongMetadata(_ song: MJ) async throws -> MJ {
        let result = try await sharedSongMetadata([song])
        return result[0]
    }

    func sharedSongMetadata(_ songs: [MJ]) async throws -> [MJ] {
        try check()
        let own = epoch
        try await retainLibrarySongMetadata()
        try check(own)
        try await retainSongMetadata(songs)
        return try await projectSongMetadata(songs)
    }

    /// Read-only projection for an executed search whose original observation
    /// has already been recorded. Raw late responses must not reenter as imports.
    func projectSongMetadata(_ songs: [MJ]) async throws -> [MJ] {
        try check()
        let own = epoch
        var result: [MJ] = []
        for song in songs {
            let state = await cachedSourceDiscoveryState(song)
            try check(own)
            result.append(mergeSourceDiscoveryCache(song, entries: state.entries))
        }
        return result
    }

    /// Synchronous handle registration remains a bounded snapshot operation;
    /// callers await metadata adoption before creating those snapshots.
    func metadataPage(_ value: MusicNativeTrackPage, maximumItems: Int = 30) async throws -> MusicBrowsePage {
        try musicRequire(maximumItems >= 0 && value.items.count <= maximumItems, .limit)
        let songs = try value.items.map(MusicModel.normalize)
        try musicRequire(songs.allSatisfy { $0.bytes.count <= 64 * 1024 } &&
            songs.reduce(0) { $0 + $1.bytes.count } <= 4 * 1024 * 1024, .limit)
        let shared = try await sharedSongMetadata(songs)
        // Preserve the existing page-level reservation and handle budgets.
        return try metadataHandles(shared, cursor: value.cursor)
    }

    func metadataHandles(_ songs: [MJ], cursor: String? = nil) throws -> MusicBrowsePage {
        if let cursor { _ = try musicText(cursor, maximum: 64) }
        let bytes = songs.reduce(0) { $0 + $1.bytes.count }
        try musicRequire(songs.allSatisfy { $0.bytes.count <= 64 * 1024 } && bytes <= 4 * 1024 * 1024, .limit)
        handles = handles.filter { now().timeIntervalSince($0.value.at) < 600 }
        let capacity = max(600, songs.count)
        while handles.count + songs.count > capacity ||
            handles.values.reduce(0, { $0 + $1.bytes }) + bytes > 4 * 1024 * 1024 {
            guard let oldest = handles.min(by: { $0.value.at < $1.value.at })?.key else { throw MusicServiceError.limit }
            handles.removeValue(forKey: oldest)
        }
        return .init(items: try songs.map { try remember($0, maximumHandles: capacity) }, cursor: cursor)
    }

    func searchObservationIsDedicated(_ observation: MusicSearchObservation, song: MJ) -> Bool {
        observation.scope == .recording || observation.page == 1 &&
            MusicRecordingIdentity.normalize(observation.query) == MusicRecordingIdentity.normalize(sourceDiscoveryQuery(song))
    }

    /// Known local recordings can be checked immediately; a round later binds
    /// these same original stamps to recordings revealed by another platform.
    @discardableResult
    func recordSearchPage(_ page: MusicNativeTrackPage, observation: MusicSearchObservation) async throws -> [MJ] {
        try check()
        let own = epoch
        let songs = try page.items.map(MusicModel.normalize)
        try musicRequire(songs.allSatisfy { $0.bytes.count <= 64 * 1024 } &&
            songs.reduce(0) { $0 + $1.bytes.count } <= 4 * 1024 * 1024, .limit)
        try await retainLibrarySongMetadata()
        try check(own)
        let rows = songs.flatMap { $0["sources"].array }
        var observed = Set<String>()
        var pageMetadata: [MusicSourceDiscoveryMetadata] = []
        for song in songs {
            guard observed.insert(MusicModel.descriptor(song).descriptorKey).inserted else { continue }
            if let metadata = sourceDiscoveryMetadata(song, platform: observation.platform, rows: rows) {
                pageMetadata.append(metadata)
            }
        }
        let state = try read()
        let known = state.library["library"]["songs"].fields.map(\.1) +
            state.queue["entries"].array.map { $0["song"] } + handles.values.map(\.song)
        var seen = Set<String>()
        var recordingMetadata: [MusicSourceDiscoveryMetadata] = []
        for song in known where searchObservationIsDedicated(observation, song: song) {
            let descriptor = MusicModel.descriptor(song)
            guard seen.insert(descriptor.descriptorKey).inserted else { continue }
            if let metadata = sourceDiscoveryMetadata(song, platform: observation.platform, rows: rows) {
                recordingMetadata.append(metadata)
            }
        }
        await sourceDiscovery.observe(pageMetadata, observation: observation)
        try check(own)
        await sourceDiscovery.observe(recordingMetadata, observation: observation.result(.incomplete, scope: .recording))
        try check(own)
        if !pageMetadata.isEmpty || !recordingMetadata.isEmpty { scheduleSourceDiscoveryPersist() }
        return songs
    }

    func recordSearchFailure(_ request: MusicSearchObservation, failure: MusicServiceError) async {
        guard !Task.isCancelled, ![.cancelled, .closed, .accessUnavailable, .staleAccess].contains(failure),
              let state = try? read() else { return }
        let observation = finishSourceDiscoveryObservation(request, failure: failure)
        let own = epoch
        let known = state.library["library"]["songs"].fields.map(\.1) +
            state.queue["entries"].array.map { $0["song"] } + handles.values.map(\.song)
        var seen = Set<String>()
        var metadata: [MusicSourceDiscoveryMetadata] = []
        for song in known where searchObservationIsDedicated(observation, song: song) {
            guard seen.insert(MusicModel.descriptor(song).descriptorKey).inserted else { continue }
            if let item = sourceDiscoveryMetadata(song, platform: observation.platform, rows: []) { metadata.append(item) }
        }
        await sourceDiscovery.observe(metadata, observation: observation.result(.failed, scope: .recording, failure: failure))
        if !metadata.isEmpty, (try? check(own)) != nil { scheduleSourceDiscoveryPersist() }
    }
}

extension NativeMusicService: MusicSearchObserving {
    public func observeSearchPages(_ pages: [MusicSearchPage], references: [MusicReference]) async throws {
        try check()
        let own = epoch, state = try read()
        // Page handles expire independently. Valid remaining native references
        // can still bind the original observation; never re-search for evidence.
        let songs = references.compactMap { try? song($0, state) }
        for page in pages {
            guard let observation = page.observation else { continue }
            try musicRequire(observation.platform == page.platform && observation.query == page.query &&
                observation.page == page.page && observation.limit == page.limit &&
                observation.context == "public", .invalidRequest)
            var metadata: [MusicSourceDiscoveryMetadata] = [], seen = Set<String>()
            for target in songs where searchObservationIsDedicated(observation, song: target) {
                guard seen.insert(MusicModel.descriptor(target).descriptorKey).inserted else { continue }
                let matches = page.items.filter { MusicModel.descriptor(target).isCompatible(with: .init($0)) }
                // Page DTOs only identify a hit. Native facts come from handles
                // or were already adopted by the executed search itself.
                var rows: [MJ] = []
                for match in matches {
                    if let native = try? song(match.ref, state) {
                        rows += native["sources"].array.filter { $0["source"].string == page.platform }
                    }
                }
                if !matches.isEmpty && rows.isEmpty { continue }
                if let item = sourceDiscoveryMetadata(target, platform: page.platform, rows: rows) { metadata.append(item) }
            }
            await sourceDiscovery.observe(metadata, observation: observation.result(.incomplete, scope: .recording))
            try check(own)
            if !metadata.isEmpty { scheduleSourceDiscoveryPersist() }
        }
    }
}
