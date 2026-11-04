// SPDX-License-Identifier: GPL-3.0-only
// Fixed meowmusic cc22a7e kugou/music.go identity and resource mapping.
import Foundation
import MeowMusicFeature
import MeowJSON

struct MusicKugouResource: Sendable, Equatable {
    let hash: String
    let size: Int
}
struct MusicKugouSong: Sendable {
    static let qualities = ["128k", "320k", "flac", "flac24bit"]
    let hash: String
    let audioID: String
    let albumID: String
    let albumAudioID: String
    let title: String
    let artist: String
    let album: String
    let duration: Double
    let image: String
    let resources: [String: MusicKugouResource]
    init(_ raw: [String: Any]) throws {
        let audio = raw["audio_info"] as? [String: Any] ?? [:]
        let album = (raw["albuminfo"] ?? raw["album_info"]) as? [String: Any] ?? [:]
        let base = raw["base"] as? [String: Any] ?? [:]
        hash = try MusicKugouWire.hash(MusicKugouWire.first(raw["hash"], raw["FileHash"], audio["hash"]))
        for value in [raw["hash"], raw["FileHash"], audio["hash"]].compactMap({ $0 }) {
            guard try MusicKugouWire.hash(value) == hash else { throw MusicServiceError.targetChanged }
        }
        var full = try MusicKugouWire.text(MusicKugouWire.first(raw["name"], raw["FileName"]) ?? "", empty: true)
        full = full.replacingOccurrences(of: "(?i)\\.(mp3|flac|m4a|wav|ape|ogg|aac|wma)$", with: "", options: .regularExpression)
        var singer = try MusicKugouWire.text(MusicKugouWire.first(raw["SingerName"], raw["author_name"], base["author_name"]) ?? "", empty: true)
        if let range = full.range(of: " - ") {
            if singer.isEmpty { singer = String(full[..<range.lowerBound]) }
            full = String(full[range.upperBound...])
        }
        if let values = MusicKugouWire.first(raw["singerinfo"], raw["Singers"]) {
            singer = try MusicKugouWire.rows(values, maximum: 64).compactMap {
                MusicKugouWire.first($0["name"], $0["author_name"])
            }.map { try MusicKugouWire.text($0, maximum: 256) }.joined(separator: "、")
        }
        if singer.isEmpty, let values = raw["authors"] {
            singer = try MusicKugouWire.rows(values, maximum: 64).compactMap {
                ($0["base"] as? [String: Any])?["author_name"]
            }.map { try MusicKugouWire.text($0, maximum: 256) }.joined(separator: "、")
        }
        artist = try MusicKugouWire.text(singer, empty: true)
        var name = try MusicKugouWire.text(MusicKugouWire.first(raw["OriSongName"], raw["songname"], raw["audio_name"], base["audio_name"], full) ?? "未知歌曲")
        if raw["OriSongName"] != nil, let suffix = raw["Suffix"] as? String, !suffix.isEmpty { name += " " + suffix }
        title = try MusicKugouWire.text(name)
        self.album = try MusicKugouWire.text(MusicKugouWire.first(raw["AlbumName"], album["name"], album["album_name"]) ?? "", empty: true)
        albumID = try MusicKugouWire.id(MusicKugouWire.first(raw["album_id"], raw["AlbumID"], album["id"], album["album_id"], base["album_id"]) ?? 0, zero: true)
        albumAudioID = try MusicKugouWire.id(MusicKugouWire.first(raw["mixsongid"], raw["add_mixsongid"], raw["album_audio_id"], raw["MixSongID"], base["album_audio_id"]) ?? 0, zero: true)
        var nativeID: String?
        for value in [raw["Audioid"], raw["audio_id"], audio["audio_id"]].compactMap({ $0 }) {
            let id = try MusicKugouWire.id(value, zero: true)
            if id == "0" { continue }
            guard nativeID == nil || nativeID == id else { throw MusicServiceError.targetChanged }
            nativeID = id
        }
        audioID = nativeID ?? "" // Never replace Audioid with hash/MixSongID/fileid.
        let rawTime = MusicKugouWire.first(raw["Duration"], raw["timelen"], raw["timelength"], audio["timelength"]) ?? 0
        let seconds = Double(try MusicKugouWire.integer(rawTime)) / (raw["Duration"] == nil ? 1000 : 1)
        guard seconds <= 604_800 else { throw MusicServiceError.upstreamInvalid }
        duration = seconds
        let trans = raw["trans_param"] as? [String: Any] ?? [:]
        let cover = MusicKugouWire.first(raw["cover"], raw["Image"], trans["union_cover"]) as? String ?? ""
        // Images are optional display data, not an arbitrary URL fetch facility.
        image = (try? Self.assetURL(cover.replacingOccurrences(of: "{size}", with: "480")).absoluteString) ?? ""
        let hashes: [Any?] = [hash, MusicKugouWire.first(raw["hash_320"], raw["HQFileHash"], audio["hash_320"]),
                             MusicKugouWire.first(raw["hash_flac"], raw["SQFileHash"], audio["hash_flac"]),
                             MusicKugouWire.first(raw["hash_high"], raw["ResFileHash"], audio["hash_high"])]
        let sizes: [Any?] = [MusicKugouWire.first(raw["filesize"], raw["FileSize"], raw["size"], audio["filesize"]),
                            MusicKugouWire.first(raw["filesize_320"], raw["HQFileSize"], audio["filesize_320"]),
                            MusicKugouWire.first(raw["filesize_flac"], raw["SQFileSize"], audio["filesize_flac"]),
                            MusicKugouWire.first(raw["filesize_high"], raw["ResFileSize"], audio["filesize_high"])]
        var resources: [String: MusicKugouResource] = [:]
        for i in Self.qualities.indices {
            if let hash = try? MusicKugouWire.hash(hashes[i]),
               let size = try? MusicKugouWire.integer(sizes[i], minimum: 1) {
                resources[Self.qualities[i]] = .init(hash: hash, size: size)
            }
        }
        self.resources = resources
    }
    static func assetURL(_ text: String) throws -> URL {
        guard text.utf8.count <= 8192,
              !text.utf8.contains(where: { $0 <= 32 || $0 == 127 || $0 == 92 }),
              let components = URLComponents(string: text), components.scheme == "https",
              components.user == nil, components.password == nil, components.port == nil,
              components.fragment == nil, let host = components.host?.lowercased(), !host.hasSuffix("."),
              ["kugou.com", "kugou.net", "kgimg.com"].contains(where: { host == $0 || host.hasSuffix("." + $0) }),
              host.split(separator: ".").allSatisfy({ !$0.isEmpty && $0.utf8.allSatisfy { (97...122).contains($0) || (48...57).contains($0) || $0 == 45 } }),
              let url = components.url else { throw MusicServiceError.invalidMediaCandidate }
        return url
    }
    var dto: JSONValue {
        var types: [JSONValue] = [], native: [String: JSONValue] = [:]
        for quality in Self.qualities {
            guard let resource = resources[quality] else { continue }
            let value = JSONValue.object(["type": .string(quality), "hash": .string(resource.hash), "size": .number(Double(resource.size))])
            types.append(value)
            native[quality] = .object(["hash": .string(resource.hash), "size": .number(Double(resource.size))])
        }
        return .object([
            "source": .string("kg"), "hash": .string(hash), "songmid": .string(hash),
            "name": .string(title), "singer": .string(artist), "albumName": .string(album),
            "albumId": .string(albumID), "album_id": .string(albumID), "albumAudioId": .string(albumAudioID),
            "duration": .number(duration), "img": .string(image), "types": .array(types), "_types": .object(native)
        ])
    }
}
