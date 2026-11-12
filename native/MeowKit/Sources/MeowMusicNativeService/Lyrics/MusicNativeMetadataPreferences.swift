// SPDX-License-Identifier: GPL-3.0-only
// iWebPlus b900c259: account-settings.js:4,90-100,127-140; player.js:47-63,83-84.
import Foundation
import MeowMusicFeature

/// Playback preferences. Missing fields use the
/// five-platform defaults; present malformed fields are never repaired by a read
/// or overwritten by an unrelated preference update.
private struct NativeMetadataPreferences {
    var order: [MusicPublicBrowsePlatform]
    var enabled: Set<MusicPublicBrowsePlatform>
    private static let defaults: [MusicPublicBrowsePlatform] = [.netease, .qq, .kugou, .kuwo, .migu]

    init(_ playback: MJ) throws {
        func decode(_ field: String, complete: Bool) throws -> [MusicPublicBrowsePlatform] {
            guard playback.contains(field) else { return Self.defaults }
            let value = playback[field]
            try musicRequire(value.isArray && value.array.allSatisfy(\.isString), .corruptDocument)
            let platforms = value.strings.compactMap(MusicPublicBrowsePlatform.init(rawValue:))
            try musicRequire(platforms.count == value.array.count && Set(platforms).count == platforms.count &&
                             (!complete || platforms.count == Self.defaults.count), .corruptDocument)
            return platforms
        }
        order = try decode("metadataPlatforms", complete: true)
        enabled = Set(try decode("metadataEnabled", complete: false))
    }

    func visible(revision: Int, available: Set<MusicPublicBrowsePlatform>) -> MusicMetadataPreferences {
        .init(revision: revision, platforms: order.filter(available.contains),
              enabledPlatforms: enabled.intersection(available))
    }

    mutating func apply(_ change: MusicMetadataPreferencesChange,
                        available: Set<MusicPublicBrowsePlatform>, to playback: inout MJ) throws {
        if let platforms = change.platforms {
            try musicRequire(platforms.count == available.count && Set(platforms) == available)
            // Replace only visible slots. A three-platform product must not erase
            // or move the persisted positions of temporarily unavailable kw/mg.
            var index = 0
            order = order.map { platform in
                guard available.contains(platform) else { return platform }
                defer { index += 1 }
                return platforms[index]
            }
            playback["metadataPlatforms"] = .strings(order.map(\.rawValue))
        }
        if let platforms = change.enabledPlatforms {
            try musicRequire(platforms.isSubset(of: available))
            enabled = enabled.subtracting(available).union(platforms)
            playback["metadataEnabled"] = .strings(Self.defaults.filter(enabled.contains).map(\.rawValue))
        }
    }
}

extension NativeMusicService {
    public func metadataPreferences() async throws -> MusicMetadataPreferences {
        let own = epoch, d = try read()
        let value = try NativeMetadataPreferences(d.playback)
            .visible(revision: d.playback["revision"].int, available: enabledPlatforms)
        try check(own)
        return value
    }

    public func updateMetadataPreferences(_ change: MusicMetadataPreferencesChange) async throws -> MusicMetadataPreferences {
        try check()
        try musicRequire((0...9_007_199_254_740_991).contains(change.ifRevision))
        if change.platforms == nil && change.enabledPlatforms == nil {
            // Even a no-op must not acknowledge an obsolete settings snapshot.
            let d = try read(), prefs = try NativeMetadataPreferences(d.playback)
            try musicRequire(d.playback["revision"].int == change.ifRevision, .staleRevision)
            try check()
            return prefs.visible(revision: change.ifRevision, available: enabledPlatforms)
        }
        let result: (MusicMetadataPreferences, Bool)
        do {
            result = try transaction(["playback"]) { d -> (MusicMetadataPreferences, Bool) in
                try musicRequire(d.playback["revision"].int == change.ifRevision, .staleRevision)
                try musicRequire(change.ifRevision < 9_007_199_254_740_991, .limit)
                var prefs = try NativeMetadataPreferences(d.playback)
                let before = prefs
                try prefs.apply(change, available: enabledPlatforms, to: &d.playback)
                d.playback["revision"] = .n(change.ifRevision + 1)
                return (prefs.visible(revision: change.ifRevision + 1, available: enabledPlatforms),
                        before.order != prefs.order || before.enabled != prefs.enabled)
            }
        } catch MusicServiceError.commitUnconfirmed {
            // The CAS may already have committed despite caller cancellation.
            // Do not let an old lyric producer publish under the old policy.
            lyricEpoch = UUID(); cancelWork("lyrics")
            throw MusicServiceError.commitUnconfirmed
        }
        if result.1 {
            // Revoke only metadata work, not the audio attempt or its position.
            // The feature still owns restarting its independent metadata session.
            lyricEpoch = UUID(); cancelWork("lyrics")
        }
        return result.0
    }

    public func metadataCandidates(occurrenceID: String) async throws -> MusicMetadataCandidates {
        let own = epoch, d = try read()
        try musicRequire(!occurrenceID.isEmpty)
        try musicRequire(musicSame(d.queue["currentId"].string, occurrenceID), .targetChanged)
        let ref = MusicReference(kind: .queue, id: occurrenceID)
        let song = try await sharedSongMetadata(playbackSong(ref, d)), track = try MusicModel.track(song, ref)
        let prefs = try NativeMetadataPreferences(d.playback)
            .visible(revision: d.playback["revision"].int, available: enabledPlatforms)
        let sources = try MusicModel.sources(song)
        let rows = song["sources"].array.filter { row in
            let key = row["sourceKey"].string, info = MusicModel.sourceData(row)
            return !key.isEmpty && musicSame(key, MusicModel.nativeKey(row)) && MusicModel.compatible(song, row) &&
                platformEnabled(info["source"].string)
        }
        var candidates: [MusicMetadataCandidate] = []
        for platform in prefs.platforms where prefs.enabledPlatforms.contains(platform) {
            let ordered = rows.filter { $0["source"].string == platform.rawValue }
                .sorted { $0["sourceKey"].string.compare($1["sourceKey"].string,
                    locale: Locale(identifier: "en_US")) == .orderedAscending }
            for row in ordered.prefix(2) {
                guard let source = sources.first(where: { musicSame($0.sourceKey, row["sourceKey"].string) }) else { continue }
                let info = MusicModel.sourceData(row)
                let cover = info["img"]
                let artwork = platform == .kuwo || platform == .migu
                    ? MusicNativePublicBrowseWire.artwork(cover, platform: platform)
                    : MusicNativePublicBrowseSupport.artwork(cover)
                candidates.append(.init(source: source, artwork: artwork))
            }
        }
        // Recording identity does not change when metadata discovery adds a
        // locator, renews an artwork URL, or the queue creates a new occurrence.
        let identity = MJ.array([song["songId"], song["duration"]])
        let result = MusicMetadataCandidates(occurrenceID: occurrenceID, queueRevision: d.queue["revision"].int,
            preferences: prefs, track: track, contentRevision: nil,
            contentFingerprint: identity.stable.digest, candidates: candidates)
        try check(own)
        let latest = try read()
        try musicRequire(latest.queue["currentId"].string == occurrenceID &&
            latest.queue["revision"] == d.queue["revision"] &&
            latest.playback["revision"] == d.playback["revision"], .staleRevision)
        return result
    }
}
