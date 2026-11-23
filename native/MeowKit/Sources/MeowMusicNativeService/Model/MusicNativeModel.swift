// SPDX-License-Identifier: GPL-3.0-only
// Identity/order semantics adapted from music-core (Apache-2.0 iWebPlus origins).
import Foundation
import MeowMusicFeature
import MeowStorage

enum MusicModel {
    static let sites = ["wy", "tx", "kg", "kw", "mg"]
    static let qualityOrder = ["master", "spatial", "enhanced", "hires", "lossless", "high", "standard"]
    static let spatialOrder = ["dolby", "dts_x", "sony360", "sky", "vivid", "qq_atmos", "viper_atmos"]
    private static let secretField = try! NSRegularExpression(
        pattern: "cookie|token|password|secret|credential|authorization|api[-_]?key",
        options: [.caseInsensitive])
    /// Platform account and playback-grant field names, compared without case
    /// or separators. A durable image URL carrying one is not public artwork.
    private static let credentialQueryNames: Set<String> = [
        "musicu", "musica", "csrf", "csrftoken", "nmtid", "rememberme",
        "uin", "wxuin", "puin", "qqmusicuin", "musicid", "strmusicid", "euin", "encryptuin",
        "musickey", "qqmusickey", "qmkeyst", "wxkey", "vkey",
        "userid", "t1", "dfid", "sid", "sessionid", "jsessionid"
    ]

    static func freshCollection(_ id: String, _ name: String, _ kind: String = "playlist") -> MusicCollectionRecord {
        MusicCollectionRecord(
            id: id,
            name: name,
            kind: kind,
            order: [],
            revision: 0,
            manualOrder: false,
            deleted: false,
            locallyCreated: false,
            bindings: [],
            intents: []
        )
    }

    static func norm(_ text: String) -> String { MusicRecordingIdentity.normalize(text) }

    static func descriptor(_ song: MusicSongRecord) -> MusicRecordingIdentity {
        let duration = song.baselineDuration ?? song.duration ?? 0
        return .init(
            title: song.title,
            artist: song.artist,
            duration: duration,
            artistNames: song.artistNames.isEmpty ? nil : song.artistNames
        )
    }

    static func sourceKey(platform: String, songmid: String, hash: String?, albumAudioID: String?) throws -> String {
        try musicRequire(sites.contains(platform) && !songmid.isEmpty, .upstreamInvalid)
        let third: String
        if platform == "kg" {
            if let hash, !hash.isEmpty {
                third = norm(hash)
            } else {
                third = albumAudioID ?? ""
            }
        } else {
            third = ""
        }
        return MusicWireCodec.canonicalStrings([platform, songmid, third])
    }

    static func sourceKey(_ source: MusicSongSourceRecord) throws -> String {
        try sourceKey(
            platform: source.infoSource.isEmpty ? source.platform : source.infoSource,
            songmid: source.resolvedSongmid,
            hash: source.hash,
            albumAudioID: source.resolvedAlbumAudioID
        )
    }

    static func sourceKey(fromTrackData value: MusicNativeTrackData) throws -> String {
        let raw = try MusicWireCodec.decodeTree(value.data)
        let songmid = MusicWireCodec.exactID(MusicWireCodec.field("songmid", in: raw))
        let hash = MusicWireCodec.stringValue(MusicWireCodec.field("hash", in: raw))
        let albumAudioID = MusicWireCodec.exactID(MusicWireCodec.field("albumAudioId", in: raw))
        return try sourceKey(platform: value.platform, songmid: songmid, hash: hash, albumAudioID: albumAudioID)
    }

    private static func matches(_ expression: NSRegularExpression, _ value: String) -> Bool {
        expression.firstMatch(in: value, range: NSRange(value.startIndex..<value.endIndex, in: value)) != nil
    }

    static func publicURLString(_ raw: String) -> String {
        guard let url = MusicArtworkURL.normalized(raw),
              let parts = URLComponents(url: url, resolvingAgainstBaseURL: false),
              !(parts.queryItems ?? []).contains(where: {
                  matches(secretField, $0.name) ||
                      credentialQueryNames.contains($0.name.lowercased().filter { $0.isLetter || $0.isNumber })
              }) else { return "" }
        return url.absoluteString
    }

    static func artworkURL(_ raw: String) -> URL? {
        MusicArtworkURL.normalized(raw)
    }

    static func cleanSong(_ raw: MusicSongRecord) throws -> MusicSongRecord {
        var song = raw
        song.title = durableString(song.title)
        song.artist = durableString(song.artist)
        song.album = durableString(song.album)
        song.coverURL = publicURLString(song.coverURL)
        song.artistNames = song.artistNames.map(durableString)
        song.sources = try raw.sources.map(cleanSource)
        return song
    }

    static func cleanSource(_ raw: MusicSongSourceRecord) throws -> MusicSongSourceRecord {
        let infoTree = try MusicWireCodec.buildMusicInfoTree(raw)
        let extraSourceFields: [SQLiteTreeField]
        if let extraSrc = raw.extraRootField("source"), case .object(let fields) = durableTree(extraSrc) {
            extraSourceFields = fields
        } else {
            extraSourceFields = []
        }
        return try MusicWireCodec.sourceRecord(fromExternalInfo: infoTree, extraSourceFields: extraSourceFields)
    }

    static func orderedSources(_ rows: [MusicSongSourceRecord]) -> [MusicSongSourceRecord] {
        guard let first = rows.first else { return rows }
        return [first] + rows.dropFirst().sorted { $0.sourceKey < $1.sourceKey }
    }

    static func mergeCleanSources(_ input: MusicSongRecord,
                                  adding incoming: [MusicSongSourceRecord],
                                  preferIncoming: Bool) -> MusicSongRecord {
        var song = input
        var rows = song.sources
        var positions = Dictionary(rows.enumerated().map { ($0.element.sourceKey, $0.offset) }, uniquingKeysWith: min)
        for proposed in incoming {
            let key = proposed.sourceKey
            if let index = positions[key] {
                if preferIncoming {
                    rows[index] = proposed
                    continue
                }
                var existing = rows[index]
                if existing.songmid == nil || existing.songmid == "" { existing.songmid = proposed.songmid }
                if existing.mid == nil || existing.mid == "" { existing.mid = proposed.mid }
                if existing.nativeID == nil || existing.nativeID == "" { existing.nativeID = proposed.nativeID }
                if existing.hash == nil || existing.hash == "" { existing.hash = proposed.hash }
                if existing.name == nil || existing.name == "" { existing.name = proposed.name }
                if existing.singer == nil || existing.singer == "" { existing.singer = proposed.singer }
                if existing.albumName == nil || existing.albumName == "" { existing.albumName = proposed.albumName }
                if existing.duration == nil { existing.duration = proposed.duration }
                if existing.img == nil || existing.img == "" { existing.img = proposed.img }
                if existing.qualityKnown == nil { existing.qualityKnown = proposed.qualityKnown }
                if existing.artistNames.isEmpty && !proposed.artistNames.isEmpty { existing.artistNames = proposed.artistNames }
                existing.extras = MusicWireCodec.mergeObjectTrees(existing.extras, adding: proposed.extras)
                rows[index] = existing
            } else {
                positions[key] = rows.count
                rows.append(proposed)
            }
        }
        song.sources = orderedSources(rows)
        return song
    }

    static func normalize(_ value: MusicNativeTrackData) throws -> MusicSongRecord {
        let raw = try MusicWireCodec.decodeTree(value.data)
        let source = try MusicWireCodec.sourceRecord(fromExternalInfo: raw, platformOverride: value.platform)
        var title = source.name ?? ""
        var artist = source.singer ?? ""
        var album = source.albumName ?? ""
        var duration = source.duration
        var coverURL = publicURLString(source.img ?? "")
        if let display = value.display {
            title = display.title
            artist = display.artist
            album = display.album
            duration = display.duration > 0 ? display.duration : nil
            coverURL = publicURLString(display.artwork?.absoluteString ?? "")
        }
        return MusicSongRecord(
            songID: "",
            title: title,
            artist: artist,
            album: album,
            duration: duration,
            baselineDuration: nil,
            coverURL: coverURL,
            artistNames: source.artistNames,
            localFileID: nil,
            artworkSourceKey: nil,
            artworkSourceImage: nil,
            sources: [source]
        )
    }

    /// No durable identity or playback authority is created for a display-only row.
    static func displayTrack(_ value: MusicNativeTrackData) -> MusicRemoteTrack {
        let display = value.display
        return .init(
            ref: .init(kind: .library, id: "", occurrence: UUID().uuidString),
            songId: "",
            title: display?.title ?? "",
            artist: display?.artist ?? "",
            album: display?.album ?? "",
            duration: display?.duration ?? 0,
            artwork: display?.artwork,
            sourcePosition: value.sourcePosition
        )
    }

    static func sources(_ song: MusicSongRecord) throws -> [MusicNativeSource] {
        var seen = Set<String>()
        return try song.sources.map { row in
            let key = try sourceKey(row)
            try musicRequire(musicSame(key, row.sourceKey) && seen.insert(key).inserted, .nativeIDConflict)
            return .init(
                sourceKey: row.sourceKey,
                platform: row.infoSource.isEmpty ? row.platform : row.infoSource,
                nativeId: row.resolvedSongmid
            )
        }
    }

    static func track(_ song: MusicSongRecord, _ ref: MusicReference, sourcePosition: Int? = nil) throws -> MusicRemoteTrack {
        return .init(
            ref: ref,
            songId: song.songID,
            title: song.title,
            artist: song.artist,
            album: song.album,
            duration: song.effectiveDuration,
            sources: try sources(song),
            artwork: artworkURL(song.coverURL),
            artistNames: song.artistNames.isEmpty ? nil : song.artistNames,
            sourcePosition: sourcePosition
        )
    }

    static func policy(_ prefs: MusicPlaybackSettingsState) -> (enabled: [String], order: [String], spatial: [String], spatialOrder: [String]) {
        let order = prefs.qualityOrder + qualityOrder.filter { !prefs.qualityOrder.contains($0) }
        let spOrder = prefs.spatialOrder + spatialOrder.filter { !prefs.spatialOrder.contains($0) }
        let enabled = prefs.qualityEnabled
        let spatial = prefs.spatialEnabled
        return (order.filter(enabled.contains), order, spOrder.filter(spatial.contains), spOrder)
    }

    /// Built-in audio-source admission from validated playback preferences.
    static func officialSources(_ prefs: MusicPlaybackSettingsState) -> [MusicAccountPlatform] {
        let enabled = Set(prefs.officialEnabled)
        return MusicAccountPlatform.allCases.filter { enabled.contains($0.rawValue) }
    }

    static func qualities(_ qualities: [String], _ prefs: MusicPlaybackSettingsState) -> [String] {
        let p = policy(prefs)
        let map = ["128k": "standard", "192k": "high", "320k": "high", "flac": "lossless", "flac24bit": "hires", "master": "master"]
        return qualities.filter { p.enabled.contains(map[$0] ?? $0) }.sorted {
            (p.order.firstIndex(of: map[$0] ?? $0) ?? 100) < (p.order.firstIndex(of: map[$1] ?? $1) ?? 100)
        }
    }
}
