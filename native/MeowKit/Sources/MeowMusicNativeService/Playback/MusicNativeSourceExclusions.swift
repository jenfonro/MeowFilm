import Foundation
import MeowMusicFeature

extension NativeMusicService: MusicSourceExclusionServing {
    public func sourceExclusions() async throws -> [MusicSourceExclusion] {
        let playback = try read().playback
        return playback.sourceExclusions.map {
            MusicSourceExclusion(id: $0.id, songID: $0.songID, title: $0.title,
                  artist: $0.artist, provider: $0.provider, platform: $0.platform)
        } + playback.recordingExclusions.map {
            MusicSourceExclusion(id: $0.id, songID: $0.songID, title: $0.title,
                  artist: $0.artist, provider: $0.provider, platform: $0.platform,
                  scope: .recording(sourceKey: $0.sourceKey))
        }
    }

    public func excludeSource(for reference: MusicReference, provider: String, platform: String) async throws {
        _ = try musicText(provider, maximum: 128)
        try musicRequire(MusicModel.sites.contains(platform))
        let provider = MusicSourceExclusion.providerID(provider, platform: platform)
        let songID = try song(reference, read()).songID
        func matches(_ selection: MusicPlaybackSelection) -> Bool {
            selection.siteId == platform && MusicSourceExclusion.providerID(selection.installationId, platform: platform) == provider
        }
        var unconfirmed = false
        do {
            try transaction([.playback]) { d in
                let song = try song(reference, d)
                guard !d.playback.sourceExclusions.contains(where: {
                    $0.songID == song.songID && $0.provider == provider && $0.platform == platform
                }) else { return }
                d.playback.sourceExclusions.append(MusicSourceExclusionRecord(
                    id: UUID().uuidString.lowercased(),
                    songID: song.songID,
                    provider: provider,
                    platform: platform,
                    providerName: "",
                    title: song.title,
                    artist: song.artist,
                    excludedAtMs: Int64((now().timeIntervalSince1970 * 1000).rounded())
                ))
            }
        } catch MusicServiceError.commitUnconfirmed { unconfirmed = true }
        for (id, value) in preparedSources where value.seed.song.songID == songID && matches(value.result.choice.selection) {
            discardSource(attemptID: id)
        }
        for (id, value) in preparedCacheSources where value.cache.identity.songID == songID && matches(value.playback.selection) {
            discardSource(attemptID: id)
        }
        if let session = await sourceSessionStore.current()?.session {
            await session.exclusionsChanged(songID: songID, provider: provider, platform: platform, excluded: true)
        }
        if unconfirmed || Task.isCancelled { throw MusicServiceError.commitUnconfirmed }
    }

    public func restoreSourceExclusion(id: String) async throws {
        let matches = try await sourceExclusions().filter { $0.id == id }
        try musicRequire(!id.isEmpty && matches.count <= 1, .corruptStorage)
        let row = matches.first
        var unconfirmed = false
        do {
            try transaction([.playback]) { d in
                try musicRequire(d.playback.sourceExclusions.filter { $0.id == id }.count <= 1, .corruptStorage)
                try musicRequire(d.playback.recordingExclusions.filter { $0.id == id }.count <= 1, .corruptStorage)
                d.playback.sourceExclusions.removeAll { $0.id == id }
                d.playback.recordingExclusions.removeAll { $0.id == id }
            }
        } catch MusicServiceError.commitUnconfirmed { unconfirmed = true }
        if let row, let session = await sourceSessionStore.current()?.session {
            switch row.scope {
            case .providerPlatform:
                await session.exclusionsChanged(songID: row.songID, provider: row.provider, platform: row.platform, excluded: false)
            case .recording(let sourceKey):
                await session.recordingRestored(songID: row.songID, sourceKey: sourceKey)
            }
        }
        if unconfirmed || Task.isCancelled { throw MusicServiceError.commitUnconfirmed }
    }

    func sourceExcluded(_ song: MusicSongRecord, provider: String, platform: String) throws -> Bool {
        let provider = MusicSourceExclusion.providerID(provider, platform: platform)
        return try read().playback.sourceExclusions.contains {
            $0.songID == song.songID && $0.provider == provider && $0.platform == platform
        }
    }

    func admitSource(_ song: MusicSongRecord, selection: MusicPlaybackSelection) throws {
        try musicRequire(!recordingSourceExclusions(song).contains(selection.sourceKey), .sourceUnavailable)
        try musicRequire(!sourceExcluded(song, provider: selection.installationId, platform: selection.siteId), .sourceUnavailable)
    }
}

extension NativeMusicService: MusicOfficialSourceChecking {
    public func checkOfficialSource(_ platform: MusicAccountPlatform) async throws {
        try check(); try requirePlatform(platform.rawValue)
        guard let checker = accounts as? any MusicOfficialSourceChecking else { throw MusicServiceError.unsupported }
        try await work(label: "source-check:" + platform.rawValue) { try await checker.checkOfficialSource(platform) }
    }
}

extension NativeMusicService {
    func recordingSourceExclusions(_ song: MusicSongRecord) throws -> Set<String> {
        var keys = Set(try read().playback.recordingExclusions.filter { $0.songID == song.songID }.map(\.sourceKey))
        // Single-recording guard: preserve valid exclusions on multi-recording songs
        // (including sources.first), only releasing the primary key if every recording
        // would otherwise be excluded.
        if let primary = song.sources.first?.sourceKey,
           song.sources.allSatisfy({ keys.contains($0.sourceKey) }) {
            keys.remove(primary)
        }
        return keys
    }

    func excludeRecordingSource(_ song: MusicSongRecord, sourceKey: String) async throws {
        try musicRequire(!song.songID.isEmpty && !sourceKey.isEmpty, .invalidRequest)
        let keyTree = try MusicWireCodec.decodeTree(Data(sourceKey.utf8))
        let platform: String
        if case .array(let items) = keyTree, let first = items.first, case .text(let s) = first {
            platform = s
        } else {
            platform = ""
        }
        try musicRequire(MusicModel.sites.contains(platform), .invalidRequest)
        var unconfirmed = false
        do {
            try transaction([.playback]) { d in
                let currentSong = (try? repositorySong(song.songID, d)) ?? song
                let existing = Set(d.playback.recordingExclusions.filter { $0.songID == song.songID }.map(\.sourceKey))
                let hasOtherPlayable = currentSong.sources.contains {
                    $0.sourceKey != sourceKey && !existing.contains($0.sourceKey)
                }
                try musicRequire(hasOtherPlayable, .invalidRequest)
                guard !existing.contains(sourceKey) else { return }
                d.playback.recordingExclusions.append(MusicRecordingExclusionRecord(
                    id: UUID().uuidString.lowercased(),
                    songID: song.songID,
                    provider: "",
                    platform: platform,
                    providerName: "",
                    title: currentSong.title,
                    artist: currentSong.artist,
                    sourceKey: sourceKey,
                    excludedAtMs: Int64((now().timeIntervalSince1970 * 1000).rounded())
                ))
            }
        } catch MusicServiceError.commitUnconfirmed { unconfirmed = true }
        for (id, value) in preparedSources where value.seed.song.songID == song.songID &&
            value.result.option.sourceKey == sourceKey && !value.armed { discardSource(attemptID: id) }
        for (id, value) in preparedCacheSources where value.cache.identity.sourceKey == sourceKey &&
            value.cache.identity.songID == song.songID && !value.armed { discardSource(attemptID: id) }
        await sourceSessionStore.forget(sourceKey: sourceKey, songID: song.songID)
        if let session = await sourceSessionStore.current()?.session {
            await session.recordingExcluded(songID: song.songID, sourceKey: sourceKey)
        }
        if unconfirmed || Task.isCancelled { throw MusicServiceError.commitUnconfirmed }
    }
}
