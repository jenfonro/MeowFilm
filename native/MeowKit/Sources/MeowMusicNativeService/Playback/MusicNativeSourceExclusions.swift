import Foundation
import MeowMusicFeature

extension NativeMusicService: MusicSourceExclusionServing {
    func exclusionRows(_ playback: MJ, key: String) throws -> [MJ] {
        guard playback.contains(key) else { return [] }
        try musicRequire(playback[key].isArray, .corruptDocument)
        return playback[key].array
    }
    public func sourceExclusions() async throws -> [MusicSourceExclusion] {
        let playback = try read().playback
        return try exclusionRows(playback, key: "sourceExclusions").map {
            MusicSourceExclusion(id: $0["id"].string, songID: $0["songID"].string, title: $0["title"].string,
                  artist: $0["artist"].string, provider: $0["provider"].string, platform: $0["platform"].string)
        } + exclusionRows(playback, key: "recordingSourceExclusions").map {
            MusicSourceExclusion(id: $0["id"].string, songID: $0["songID"].string, title: $0["title"].string,
                  artist: $0["artist"].string, provider: "", platform: $0["platform"].string,
                  scope: .recording(sourceKey: $0["sourceKey"].string))
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
                var rows = try exclusionRows(d.playback, key: "sourceExclusions")
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
        let matches = try await sourceExclusions().filter { $0.id == id }
        try musicRequire(!id.isEmpty && matches.count <= 1, .corruptDocument)
        let row = matches.first
        var unconfirmed = false
        do {
            try transaction(["playback"]) { d in
                for key in ["sourceExclusions", "recordingSourceExclusions"] {
                    let rows = try exclusionRows(d.playback, key: key)
                    try musicRequire(rows.filter { $0["id"].string == id }.count <= 1, .corruptDocument)
                    d.playback[key] = .array(rows.filter { $0["id"].string != id })
                }
            }
        } catch MusicServiceError.commitUnconfirmed { unconfirmed = true }
        if let row {
            switch row.scope {
            case .providerPlatform:
                await currentSourceSession?.session.exclusionsChanged(songID: row.songID, provider: row.provider, platform: row.platform, excluded: false)
            case .recording(let sourceKey):
                await currentSourceSession?.session.recordingRestored(songID: row.songID, sourceKey: sourceKey)
            }
        }
        if unconfirmed || Task.isCancelled { throw MusicServiceError.commitUnconfirmed }
    }
    func sourceExcluded(_ song: MJ, provider: String, platform: String) throws -> Bool {
        let provider = MusicSourceExclusion.providerID(provider, platform: platform)
        return try exclusionRows(read().playback, key: "sourceExclusions").contains {
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
    func recordingSourceExclusions(_ song: MJ) throws -> Set<String> {
        var keys = Set(try exclusionRows(read().playback, key: "recordingSourceExclusions").filter {
            $0["songID"] == song["songId"]
        }.map { $0["sourceKey"].string })
        if let primary = song["sources"].array.first?["sourceKey"].string { keys.remove(primary) }
        return keys
    }

    func excludeRecordingSource(_ song: MJ, sourceKey: String) async throws {
        try musicRequire(!song["songId"].string.isEmpty && !sourceKey.isEmpty &&
            song["sources"].array.first?["sourceKey"].string != sourceKey, .invalidRequest)
        // Discovered, not-yet-adopted sources can be rejected too.
        let key = try MJ.parse(Data(sourceKey.utf8))
        let platform = key.array.first?.string ?? ""
        try musicRequire(MusicModel.sites.contains(platform), .invalidRequest)
        var unconfirmed = false
        do {
            try transaction(["playback"]) { d in
                var rows = try exclusionRows(d.playback, key: "recordingSourceExclusions")
                guard !rows.contains(where: { $0["songID"] == song["songId"] && $0["sourceKey"].string == sourceKey }) else { return }
                rows.append(.obj([("id", .string(UUID().uuidString.lowercased())),
                    ("songID", song["songId"]), ("title", song["title"]), ("artist", song["artist"]),
                    ("platform", .string(platform)), ("sourceKey", .string(sourceKey))]))
                d.playback["recordingSourceExclusions"] = .array(rows)
            }
        } catch MusicServiceError.commitUnconfirmed { unconfirmed = true }
        // Even an uncertain receipt may have committed. Fence prepared results
        // before reporting it, without retiring already armed playback.
        for (id, value) in preparedSources where value.seed.song["songId"] == song["songId"] &&
            value.result.option.sourceKey == sourceKey && !value.armed { discardSource(attemptID: id) }
        for (id, value) in preparedCacheSources where value.cache.identity.sourceKey == sourceKey &&
            value.cache.identity.songID == song["songId"].string && !value.armed { discardSource(attemptID: id) }
        sourceResults[song["songId"].string]?.removeAll { $0.option.sourceKey == sourceKey }
        await currentSourceSession?.session.recordingExcluded(songID: song["songId"].string, sourceKey: sourceKey)
        if unconfirmed || Task.isCancelled { throw MusicServiceError.commitUnconfirmed }
    }

}
