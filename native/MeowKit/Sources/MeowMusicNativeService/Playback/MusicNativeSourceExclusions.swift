import Foundation
import MeowMusicFeature

extension NativeMusicService: MusicSourceExclusionServing {
    public func sourceExclusions() async throws -> [MusicSourceExclusion] {
        let playback = try read().playback
        return playback["sourceExclusions"].array.map {
            MusicSourceExclusion(id: $0["id"].string, songID: $0["songID"].string, title: $0["title"].string,
                  artist: $0["artist"].string, provider: $0["provider"].string, platform: $0["platform"].string)
        } + playback["recordingSourceExclusions"].array.map {
            MusicSourceExclusion(id: $0["id"].string, songID: $0["songID"].string, title: $0["title"].string,
                  artist: $0["artist"].string, provider: "", platform: $0["platform"].string,
                  scope: .recording(fingerprint: $0["fingerprint"].string, duration: $0["duration"].number, sourceKey: $0["sourceKey"].string))
        }
    }
    public func excludeSource(for reference: MusicReference, provider: String, platform: String) async throws {
        _ = try musicText(provider, maximum: 128)
        try musicRequire(MusicModel.sites.contains(platform))
        let provider = MusicSourceExclusion.providerID(provider, platform: platform)
        let songID = try song(reference, read())["songId"].string
        func matches(_ selection: MusicPlaybackSelection) -> Bool {
            selection.siteId == platform && MusicSourceExclusion.providerID(selection.installationId, platform: platform) == provider
        }
        var unconfirmed = false
        do {
            try transaction(["playback"]) { d in
                let song = try song(reference, d)
                var rows = d.playback["sourceExclusions"].array
                guard !rows.contains(where: { $0["songID"] == song["songId"] &&
                    $0["provider"].string == provider && $0["platform"].string == platform }) else { return }
                rows.append(.obj([("id", .string(UUID().uuidString.lowercased())), ("songID", song["songId"]),
                    ("title", song["title"]), ("artist", song["artist"]), ("provider", .string(provider)), ("platform", .string(platform))]))
                d.playback["sourceExclusions"] = .array(rows)
            }
        } catch MusicServiceError.commitUnconfirmed { unconfirmed = true }
        // A cancelled receipt does not roll back CAS. Finish local admission
        // invalidation before reporting uncertainty, even after the page leaves.
        for (id, value) in preparedSources where value.seed.song["songId"].string == songID && matches(value.result.choice.selection) {
            discardSource(attemptID: id)
        }
        for (id, value) in preparedCacheSources where value.cache.identity.songID == songID && matches(value.playback.selection) {
            discardSource(attemptID: id)
        }
        await currentSourceSession?.session.exclusionsChanged(songID: songID, provider: provider, platform: platform, excluded: true)
        if unconfirmed || Task.isCancelled { throw MusicServiceError.commitUnconfirmed }
    }
    public func restoreSourceExclusion(id: String) async throws {
        let row = try await sourceExclusions().first { $0.id == id }
        var unconfirmed = false
        do {
            try transaction(["playback"]) { d in
                for key in ["sourceExclusions", "recordingSourceExclusions"] {
                    d.playback[key] = .array(d.playback[key].array.filter { $0["id"].string != id })
                }
            }
        } catch MusicServiceError.commitUnconfirmed { unconfirmed = true }
        if let row {
            switch row.scope {
            case .providerPlatform:
                await currentSourceSession?.session.exclusionsChanged(songID: row.songID, provider: row.provider, platform: row.platform, excluded: false)
            case .recording(let fingerprint, let duration, let sourceKey):
                await currentSourceSession?.session.recordingRestored(fingerprint: fingerprint, duration: duration, sourceKey: sourceKey)
            }
        }
        if unconfirmed || Task.isCancelled { throw MusicServiceError.commitUnconfirmed }
    }
    func sourceExcluded(_ song: MJ, provider: String, platform: String) throws -> Bool {
        let provider = MusicSourceExclusion.providerID(provider, platform: platform)
        return try read().playback["sourceExclusions"].array.contains {
            $0["songID"] == song["songId"] && $0["provider"].string == provider && $0["platform"].string == platform
        }
    }
    func admitSource(_ song: MJ, selection: MusicPlaybackSelection) throws {
        try musicRequire(!recordingSourceExclusions(song).contains(selection.sourceKey), .sourceUnavailable)
        try musicRequire(!sourceExcluded(song, provider: selection.installationId, platform: selection.siteId), .sourceUnavailable)
    }
}

extension NativeMusicService: MusicOfficialSourceChecking {
    public func checkOfficialSource(_ platform: MusicAccountPlatform) async throws {
        try check(); try requirePlatform(platform.rawValue)
        guard let checker = accounts as? any MusicOfficialSourceChecking else { throw MusicServiceError.unsupported }
        // Dedicated operation label: never touches playback, URL or complete-file caches.
        try await work(label: "source-check:" + platform.rawValue) { try await checker.checkOfficialSource(platform) }
    }
}

// User decisions belong to the business document, never the discovery cache.
extension NativeMusicService {
    private func sameRecording(_ row: MJ, _ descriptor: MusicRecordingIdentity) -> Bool {
        row["fingerprint"].string == descriptor.fingerprint &&
            MusicRecordingIdentity(title: "", artist: "", duration: row["duration"].number)
                .isCompatible(with: .init(title: "", artist: "", duration: descriptor.duration), sameNative: true)
    }

    func recordingSourceExclusions(_ song: MJ) throws -> Set<String> {
        let descriptor = MusicModel.descriptor(song)
        var keys = Set(try read().playback["recordingSourceExclusions"].array.filter {
            sameRecording($0, descriptor)
        }.map { $0["sourceKey"].string })
        if let primary = song["sources"].array.first?["sourceKey"].string { keys.remove(primary) }
        return keys
    }

    func excludeRecordingSource(_ song: MJ, sourceKey: String) async throws {
        let descriptor = MusicModel.descriptor(song)
        try musicRequire(!descriptor.fingerprint.isEmpty && !sourceKey.isEmpty &&
            song["sources"].array.first?["sourceKey"].string != sourceKey, .invalidRequest)
        // Discovered, not-yet-adopted sources can be rejected too.
        let key = try MJ.parse(Data(sourceKey.utf8))
        let platform = key.array.first?.string ?? ""
        try musicRequire(MusicModel.sites.contains(platform), .invalidRequest)
        var unconfirmed = false
        do {
            try transaction(["playback"]) { d in
                var rows = d.playback["recordingSourceExclusions"].array
                guard !rows.contains(where: { sameRecording($0, descriptor) && $0["sourceKey"].string == sourceKey }) else { return }
                rows.append(.obj([("id", .string(UUID().uuidString.lowercased())),
                    ("songID", song["songId"]), ("title", song["title"]), ("artist", song["artist"]),
                    ("fingerprint", .string(descriptor.fingerprint)), ("duration", .number(descriptor.duration)),
                    ("platform", .string(platform)), ("sourceKey", .string(sourceKey))]))
                d.playback["recordingSourceExclusions"] = .array(rows)
            }
        } catch MusicServiceError.commitUnconfirmed { unconfirmed = true }
        // Even an uncertain receipt may have committed. Fence prepared results
        // before reporting it, without retiring already armed playback.
        for (id, value) in preparedSources where sameRecordingSong(value.seed.song, descriptor) &&
            value.result.option.sourceKey == sourceKey && !value.armed { discardSource(attemptID: id) }
        for (id, value) in preparedCacheSources where value.cache.identity.sourceKey == sourceKey &&
            value.cache.identity.songID == song["songId"].string && !value.armed { discardSource(attemptID: id) }
        sourceResults[song["songId"].string]?.removeAll { $0.option.sourceKey == sourceKey }
        await currentSourceSession?.session.recordingExcluded(fingerprint: descriptor.fingerprint, duration: descriptor.duration, sourceKey: sourceKey)
        if unconfirmed || Task.isCancelled { throw MusicServiceError.commitUnconfirmed }
    }

    private func sameRecordingSong(_ song: MJ, _ descriptor: MusicRecordingIdentity) -> Bool {
        let other = MusicModel.descriptor(song)
        return other.fingerprint == descriptor.fingerprint && other.isCompatible(with: descriptor, sameNative: true)
    }
}
