import Foundation
import MeowMusicFeature

extension NativeMusicService: MusicSourceExclusionServing {
    public func sourceExclusions() async throws -> [MusicSourceExclusion] {
        try read().playback["sourceExclusions"].array.map {
            .init(id: $0["id"].string, songID: $0["songID"].string, title: $0["title"].string,
                  artist: $0["artist"].string, provider: $0["provider"].string, platform: $0["platform"].string)
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
                d.playback["sourceExclusions"] = .array(d.playback["sourceExclusions"].array.filter { $0["id"].string != id })
            }
        } catch MusicServiceError.commitUnconfirmed { unconfirmed = true }
        if let row { await currentSourceSession?.session.exclusionsChanged(songID: row.songID, provider: row.provider, platform: row.platform, excluded: false) }
        if unconfirmed || Task.isCancelled { throw MusicServiceError.commitUnconfirmed }
    }
    func sourceExcluded(_ song: MJ, provider: String, platform: String) throws -> Bool {
        let provider = MusicSourceExclusion.providerID(provider, platform: platform)
        return try read().playback["sourceExclusions"].array.contains {
            $0["songID"] == song["songId"] && $0["provider"].string == provider && $0["platform"].string == platform
        }
    }
    func admitSource(_ song: MJ, selection: MusicPlaybackSelection) throws {
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
