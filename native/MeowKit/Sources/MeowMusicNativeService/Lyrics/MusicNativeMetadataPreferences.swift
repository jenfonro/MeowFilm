// SPDX-License-Identifier: GPL-3.0-only
// iWebPlus b900c259: account-settings.js:4,90-100,127-140; player.js:47-63,83-84.
import Foundation
import MeowMusicPlatform

/// Playback preferences. Present malformed fields are never repaired by a read
/// or overwritten by an unrelated preference update.
private struct NativeMetadataPreferences {
    var order: [MusicPublicBrowsePlatform]
    var enabled: Set<MusicPublicBrowsePlatform>
    private static let defaults: [MusicPublicBrowsePlatform] = [.netease, .qq, .kugou, .kuwo, .migu]

    init(_ playback: MusicPlaybackSettingsState) {
        var seen = Set<MusicPublicBrowsePlatform>()
        order = playback.metadataPlatforms.compactMap(MusicPublicBrowsePlatform.init(rawValue:))
            .filter { seen.insert($0).inserted }
        enabled = Set(playback.metadataEnabled.compactMap(MusicPublicBrowsePlatform.init(rawValue:)))
    }

    func visible(revision: Int, available: Set<MusicPublicBrowsePlatform>) -> MusicMetadataPreferences {
        let displayed = order + Self.defaults.filter { !order.contains($0) }
        return .init(revision: revision, platforms: displayed.filter(available.contains),
                     enabledPlatforms: enabled.intersection(available))
    }

    mutating func apply(_ change: MusicMetadataPreferencesChange,
                        available: Set<MusicPublicBrowsePlatform>,
                        to playback: inout MusicPlaybackSettingsState) throws {
        if let platforms = change.platforms {
            try musicRequire(platforms.count == available.count && Set(platforms) == available)
            order += Self.defaults.filter { available.contains($0) && !order.contains($0) }
            var index = 0
            order = order.map { platform in
                guard available.contains(platform) else { return platform }
                defer { index += 1 }
                return platforms[index]
            }
            playback.metadataPlatforms = order.map(\.rawValue)
        }
        if let platforms = change.enabledPlatforms {
            try musicRequire(platforms.isSubset(of: available))
            enabled = enabled.subtracting(available).union(platforms)
            let before = order
            order += Self.defaults.filter { platforms.contains($0) && !order.contains($0) }
            if order != before { playback.metadataPlatforms = order.map(\.rawValue) }
            playback.metadataEnabled = Self.defaults.filter(enabled.contains).map(\.rawValue)
        }
    }
}

extension NativeMusicService {
    public func metadataPreferences() async throws -> MusicMetadataPreferences {
        let own = epoch, d = try read()
        let value = NativeMetadataPreferences(d.playback)
            .visible(revision: d.playback.revision, available: enabledPlatforms)
        try check(own)
        return value
    }

    public func updateMetadataPreferences(_ change: MusicMetadataPreferencesChange) async throws -> MusicMetadataPreferences {
        try check()
        try musicRequire((0...musicMaxSafeInteger).contains(change.ifRevision))
        if change.platforms == nil && change.enabledPlatforms == nil {
            let d = try read(), prefs = NativeMetadataPreferences(d.playback)
            try musicRequire(d.playback.revision == change.ifRevision, .staleRevision)
            try check()
            return prefs.visible(revision: change.ifRevision, available: enabledPlatforms)
        }
        let result = try transaction([.playback, .library]) { d -> (MusicMetadataPreferences, Bool) in
            try musicRequire(d.playback.revision == change.ifRevision, .staleRevision)
            try musicRequire(change.ifRevision < musicMaxSafeInteger, .limit)
            var prefs = NativeMetadataPreferences(d.playback)
            let before = prefs
            try prefs.apply(change, available: enabledPlatforms, to: &d.playback)
            d.playback.revision = change.ifRevision + 1
            if before.order != prefs.order || before.enabled != prefs.enabled {
                for id in try d.songRows.allSongIDs() {
                    var song = try d.song(id)
                    selectSongArtwork(&song, playback: d.playback, force: true)
                    try saveRepositorySong(song)
                }
            }
            return (prefs.visible(revision: change.ifRevision + 1, available: enabledPlatforms),
                    before.order != prefs.order || before.enabled != prefs.enabled)
        }
        if result.1 {
            lyricEpoch = UUID(); cancelWork("lyrics")
        }
        return result.0
    }

    public func metadataCandidates(occurrenceID: String) async throws -> MusicMetadataCandidates {
        try musicRequire(!occurrenceID.isEmpty)
        let d = try read()
        try musicRequire(d.queue.currentID == occurrenceID, .targetChanged)
        let ref = MusicReference(kind: .queue, id: occurrenceID)
        let song = try self.song(ref, d), track = MusicModel.track(song, ref)
        let prefs = NativeMetadataPreferences(d.playback)
            .visible(revision: d.playback.revision, available: enabledPlatforms)
        let sources = MusicModel.sources(song)
        let rows = song.sources.filter { platformEnabled($0.platform) }
        var candidates: [MusicMetadataCandidate] = []
        for platform in prefs.platforms where prefs.enabledPlatforms.contains(platform) {
            let ordered = rows.filter { $0.platform == platform.rawValue }
                .sorted { $0.sourceKey.compare($1.sourceKey,
                    locale: Self.metadataSortLocale) == .orderedAscending }
            for row in ordered {
                guard let source = sources.first(where: { $0.sourceKey == row.sourceKey }) else { continue }
                let artwork = MusicModel.artworkURL(row.img ?? "")
                candidates.append(.init(source: source, artwork: artwork))
            }
        }
        return .init(occurrenceID: occurrenceID, queueRevision: d.queue.revision,
                     preferences: prefs, track: track, candidates: candidates)
    }

    private static let metadataSortLocale = Locale(identifier: "en_US")
}

extension NativeMusicService {
    public func failedArtwork(songID: String, url: URL, attempted: Set<URL>) throws -> URL? {
        try transaction([.library]) { state in
            var song = try repositorySong(songID, state)
            guard song.coverURL == url.absoluteString else {
                return MusicModel.artworkURL(song.coverURL)
            }
            selectSongArtwork(&song, playback: state.playback, force: true,
                              failedURLs: Set(attempted.map(\.absoluteString)).union([url.absoluteString]))
            try saveRepositorySong(song)
            return MusicModel.artworkURL(song.coverURL)
        }
    }

    public func succeededArtwork(songID: String, url: URL, replacing requested: URL) throws -> URL? {
        try transaction([.library]) { state in
            var song = try repositorySong(songID, state)
            let current = song.coverURL
            guard current.isEmpty || current == requested.absoluteString,
                  MusicArtworkURL.normalized(url.absoluteString) == url,
                  MusicModel.publicURLString(url.absoluteString) == url.absoluteString,
                  let row = song.sources.first(where: {
                      ($0.sourceKey == song.artworkSourceKey || $0.img == requested.absoluteString)
                          && platformEnabled($0.platform)
                  }) else { return MusicModel.artworkURL(song.coverURL) }
            let preferences = NativeMetadataPreferences(state.playback)
            guard let platform = MusicPublicBrowsePlatform(rawValue: row.platform),
                  preferences.enabled.contains(platform) else { return MusicModel.artworkURL(song.coverURL) }
            song.artworkSourceKey = row.sourceKey
            song.coverURL = url.absoluteString
            try saveRepositorySong(song)
            return url
        }
    }
}
