import Foundation
import MeowAccounts
import MeowMusicFeature
import MeowRuntime

/// Converts the shared result at the field that actually consumes it. This
/// adapter neither interprets upstream envelopes nor proves whole responses.
public enum MusicPlatformDTO {
    public static func nativeTrack(_ input: RuntimeValue) throws -> MusicNativeTrackData {
        let song = input
        let source = song[music: "sources"].musicArray.first ?? .null
        let platform = source[music: "source"].musicText
        let info = source[music: "musicInfo"]
        return .init(platform: platform, data: try info.musicData(),
            display: .init(title: song[music: "title"].musicText, artist: song[music: "artist"].musicText,
                album: song[music: "album"].musicText, duration: song[music: "duration"].musicNumber ?? 0,
                artwork: MusicArtworkURL.normalized(song[music: "cover_url"].musicText)))
    }
    public static func tracks(_ value: RuntimeValue) throws -> [MusicNativeTrackData] {
        let positions = value[music: "sourcePositions"].musicArray
        return try value[music: "items"].musicArray.enumerated().map { index, row in
            let track = try nativeTrack(row)
            let position = positions.indices.contains(index) ? optionalCount(positions[index]) : nil
            return .init(platform: track.platform, data: track.data, sourcePosition: position, display: track.display)
        }
    }
    public static func optionalCount(_ value: RuntimeValue) -> Int? {
        guard let number = value.musicNumber, number.isFinite, number >= 0 else { return nil }
        return Int(exactly: number)
    }
    public static func date(_ value: RuntimeValue) -> Date? {
        value.musicNumber.map { Date(timeIntervalSince1970: $0 / 1000) }
    }
    public static func count(_ value: RuntimeValue, default fallback: Int = 0) throws -> Int {
        if value == .null { return fallback }
        guard let number = value.musicNumber else { throw MusicServiceError.upstreamInvalid }
        guard number.isFinite, number.rounded(.towardZero) == number,
              number >= 0, number < Double(Int.max) else { throw MusicServiceError.upstreamInvalid }
        return Int(number)
    }
    public static func audio(_ value: RuntimeValue) throws -> MusicNativeAudio {
        guard let url = URL(string: try value[music: "url"].musicRequiredText()) else {
            throw MusicServiceError.upstreamInvalid
        }
        if value[music: "expiresAt"] != .null {
            guard let number = value[music: "expiresAt"].musicNumber, number.isFinite, number > 0 else {
                throw MusicServiceError.upstreamInvalid
            }
        }
        let delivery: MusicAudioDelivery?
        if !value[music: "actualQuality"].musicText.isEmpty {
            delivery = .init(quality: try value[music: "actualQuality"].musicRequiredText(), evidence: .officialResponse,
                bitrate: value[music: "bitrate"].musicNumber, sampleRate: value[music: "sampleRate"].musicNumber,
                duration: value[music: "duration"].musicNumber)
        } else { delivery = nil }
        // Official recipes return self-contained media URLs. Do not forward
        // platform credentials or unconsumed fields as playback headers.
        return .init(url: url, expiresAt: date(value[music: "expiresAt"]),
            fileCachePolicy: .init(allowed: value[music: "full"].musicBool),
            delivery: delivery, duration: value[music: "duration"].musicNumber)
    }
    public static func homeRequest(_ request: MusicNativeHomeRequest) throws -> (String, RuntimeObject) {
        switch request {
        case .home(let continuation), .featured(let continuation):
            var fields: RuntimeObject = [:]
            if let continuation {
                fields["continuation"] = .object([
                    "cursor": .string(continuation.cursor),
                    "blockCodeOrderList": continuation.blockCodeOrderList.map(RuntimeValue.string) ?? .null,
                    "loadedPositionCodes": .string(String(decoding:
                        try JSONEncoder().encode(continuation.loadedPositionCodes), as: UTF8.self))
                ])
            }
            if case .featured = request { fields["featured"] = .bool(true) }
            return ("home", fields)
        case .artistActivity(let before):
            return ("artistActivity", ["before": before.map(RuntimeValue.string) ?? .null])
        case .vipSongs: return ("channel", ["id": .string("vip")])
        case .songMetadata(let ids): return ("songs", ["ids": .array(ids.map(RuntimeValue.string))])
        case .playlistMetadata(let ids): return ("playlists", ["ids": .array(ids.map(RuntimeValue.string))])
        case .heartbeatSeed: return ("heartbeat.seed", [:])
        }
    }
    public static func action(_ action: MusicHomeAction, preview: Bool = false, continuation: Bool = false) -> (String, RuntimeObject) {
        switch action {
        case .fm(let mode, let submode, _):
            return ("fm", ["mode": .string(mode.rawValue), "submode": .string(submode),
                           "preview": .bool(preview), "continuation": .bool(continuation)])
        case .ruleFM(let rule, let tab, _):
            return ("fm", ["ruleId": .string(rule), "tabTagId": .string(tab),
                           "preview": .bool(preview), "continuation": .bool(continuation)])
        case .daily(let branch): return ("daily", ["branch": .string(branch.rawValue)])
        case .heartbeat: return ("heartbeat", ["entry": .string("favorites")])
        case .heartbeatSong(let id): return ("heartbeat", ["songId": .string(id), "entry": .string("favorites")])
        case .similar(let ids, _):
            return ("similar", ["sourceType": .string("song"), "sourceIds": .array(ids.map(RuntimeValue.string))])
        case .similarArtist(let ids, _):
            return ("similar", ["sourceType": .string("artist"), "sourceIds": .array(ids.map(RuntimeValue.string))])
        case .songCollection(let ids): return ("songs", ["ids": .array(ids.map(RuntimeValue.string))])
        }
    }
    static func rows(_ value: RuntimeValue) throws -> [RuntimeValue] {
        try value[music: "items"].musicArray.map { song in
            let track = try nativeTrack(song)
            return try RuntimeJSON.decode(track.data)
        }
    }
    static func recommendation(_ value: RuntimeValue) throws -> MusicAccountRecommendation {
        .init(rows: try rows(value), nextPage: value[music: "continuation"][music: "page"] == .null ? nil :
                optionalCount(value[music: "continuation"][music: "page"]),
              expiresAt: date(value[music: "expiresAt"]))
    }
}

/// The native secure store's only current representation. Platform protocol
/// device material is owned by Node, not reconstructed from a saved context.
enum MusicAccountCredential {
    static func make(platform: MusicAccountPlatform, userID: String, cookie: String) throws -> AccountCredential {
        guard !userID.isEmpty, !cookie.isEmpty else { throw MusicServiceError.invalidResponse }
        return try AccountCredential(data: RuntimeJSON.encode(.object([
            "platform": .string(platform.rawValue), "userId": .string(userID), "cookie": .string(cookie)
        ])))
    }
    static func read(_ credential: AccountCredential) throws -> RuntimeValue {
        let value = try RuntimeJSON.decode(credential.dataRepresentation())
        _ = try value[music: "userId"].musicRequiredText()
        _ = try value[music: "cookie"].musicRequiredText()
        return value
    }
}
