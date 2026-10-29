// SPDX-License-Identifier: GPL-3.0-only
// Fixed cc22a7e kugou/{feedback,music,library}.go. Exact recording lookups,
// never title search or a first-candidate guess, before any remote mutation.
import Foundation
import MeowMusicFeature

extension KugouMusicAccountAdapter {
    func syncAddition(_ member: MusicLibrarySyncMember, context: MusicKugouContext,
                      guard authorization: MusicAccountSyncAuthorization) async throws -> [String: Any] {
        let hash = try MusicKugouWire.hash(member.nativeID), hint = try MusicQQWire.decode(member.track)
        guard try MusicKugouWire.hash(hint["hash"] ?? hint["musicId"]) == hash else { throw MusicServiceError.targetChanged }
        let song = try await audioDetail(hash, context: context)
        func checkHints(album: String, mix: String) throws {
            for (raw, verified) in [(hint["albumId"] ?? hint["album_id"], album),
                                     (hint["albumAudioId"] ?? hint["album_audio_id"], mix)] {
                guard let raw else { continue }
                let value = try MusicKugouWire.id(raw, zero: true)
                if value != "0", verified != "0", value != verified { throw MusicServiceError.targetChanged }
            }
        }
        try checkHints(album: song.albumID, mix: song.albumAudioID)
        var title = song.title, album = song.albumID, mix = song.albumAudioID
        if title == "未知歌曲" || album == "0" || mix == "0" {
            let suggested = try MusicKugouWire.id(hint["albumAudioId"] ?? hint["album_audio_id"] ?? mix, zero: true)
            let raw = try await syncMixRow(hash: hash, mix: suggested, context: context, guard: authorization)
            let metadata = try MusicKugouSong(raw)
            guard song.duration <= 0 || metadata.duration <= 0 || abs(song.duration - metadata.duration) <= 10 else {
                throw MusicServiceError.targetChanged
            }
            for (old, new) in [(album, metadata.albumID), (mix, metadata.albumAudioID), (song.audioID, metadata.audioID)] {
                if !old.isEmpty, old != "0", !new.isEmpty, new != "0", old != new { throw MusicServiceError.targetChanged }
            }
            if title == "未知歌曲" { title = metadata.title }
            if album == "0" { album = metadata.albumID }
            if mix == "0" { mix = metadata.albumAudioID }
        }
        try checkHints(album: album, mix: mix)
        guard title != "未知歌曲", !title.isEmpty, album != "0", mix != "0",
              let albumID = Int(album), let mixID = Int(mix) else { throw MusicServiceError.notFound }
        try await authorization.check()
        return ["number": 1, "name": title, "hash": hash, "size": 0, "sort": 0, "timelen": 0,
                "bitrate": 0, "album_id": albumID, "mixsongid": mixID]
    }

    func syncMixRow(hash: String, mix input: String, context: MusicKugouContext,
                    guard authorization: MusicAccountSyncAuthorization) async throws -> [String: Any] {
        var mix = input, matched: String?
        if mix == "0" || mix.isEmpty {
            let seconds = Int(now().timeIntervalSince1970)
            let root = try await syncRequest(.hashMapping, payload: [
                "appid": 1005, "clienttime": seconds, "clientver": 20489, "data": [["hash": hash.lowercased()]],
                "dfid": context.device.dfid ?? "-", "key": MusicKugouCrypto.paramsKey(seconds), "mid": context.device.mid,
                "show_privilege": 0, "show_author_alias": 0, "show_rel_album_audio_info": 0, "show_remarks": 0
            ], context: context, guard: authorization)
            guard let values = root["data"] as? [Any], values.count == 1 else { throw MusicServiceError.targetChanged }
            let value: Any
            if let nested = values[0] as? [Any] {
                guard nested.count == 1 else { throw MusicServiceError.targetChanged }; value = nested[0]
            } else { value = values[0] }
            guard let row = value as? [String: Any] else { throw MusicServiceError.targetChanged }
            mix = try MusicKugouWire.id(row["album_audio_id"])
            matched = try MusicKugouWire.hash((row["audio_info"] as? [String: Any])?["hash"] ?? row["hash"])
        }
        let id = try MusicKugouWire.id(mix)
        let root = try await syncRequest(.metadata, payload: [
            "data": [["entity_id": Int(id)!]], "fields": "base,audio_info,album_info,authors.base"
        ], context: context, guard: authorization)
        let rows = try MusicKugouWire.rows(root["data"], maximum: 1)
        guard let row = rows.first, let audio = row["audio_info"] as? [String: Any],
              let base = row["base"] as? [String: Any],
              try MusicKugouWire.integer(row["__status"]) == 1,
              try MusicKugouWire.id(base["album_audio_id"]) == id,
              ["hash", "hash_320", "hash_flac", "hash_high"].contains(where: { (try? MusicKugouWire.hash(audio[$0])) == hash }),
              let primary = try? MusicKugouWire.hash(audio["hash"]),
              matched == nil || matched == primary else { throw MusicServiceError.targetChanged }
        return row
    }
}
