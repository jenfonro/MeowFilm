// SPDX-License-Identifier: GPL-3.0-only
// Fixed meowmusic cc22a7e netease/{auth,audio}.go. No probing/fallback/played facts.
import Foundation
import MeowAccounts
import MeowMusicFeature
import MeowJSON

extension NeteaseMusicAccountAdapter {
    static let formats: [(quality: String, level: String, resource: String)] = [
        ("128k", "standard", "l"), ("192k", "higher", "m"), ("320k", "exhigh", "h"),
        ("flac", "lossless", "sq"), ("flac24bit", "hires", "hr"), ("jyeffect", "jyeffect", "je"),
        ("dolby", "dolby", "db"), ("sky", "sky", "sk"), ("master", "jymaster", "jm"), ("vivid", "vivid", "vi")
    ]
    struct Rights {
        let qualities: [String]
        let expires: Date
    }
    func membership(_ context: MusicNeteaseContext) async throws -> Rights {
        let own = epoch
        let root = try await request("/api/music-vip-membership/client/vip/info",
                                     data: ["userId": ""], crypto: "weapi", context: context)
        try check(own)
        let raw = root["data"] as? [String: Any] ?? root
        guard try MusicNeteaseWire.id(raw["userId"]) == context.accountID else { throw MusicServiceError.targetChanged }
        let current = now(), localMS = Int(current.timeIntervalSince1970 * 1000)
        let suppliedNow = try MusicQQWire.integer(raw["now"] ?? localMS)
        let upstreamMS = suppliedNow == 0 ? localMS : suppliedNow
        var expiry: [String: Int] = [:]
        for name in ["associator", "musicPackage", "redplus"] {
            if let value = raw[name], !(value is NSNull) {
                let row = try MusicQQWire.object(value)
                if let value = row["expireTime"] { expiry[name] = try MusicQQWire.integer(value) }
            }
        }
        guard !expiry.isEmpty else { throw MusicServiceError.verificationRequired }
        let vip = max(expiry["associator"] ?? 0, expiry["musicPackage"] ?? 0), svip = expiry["redplus"] ?? 0
        let high = svip > upstreamMS, remaining = max(0, (high ? svip : vip) - upstreamMS)
        return .init(qualities: remaining > 0 ? Array(Self.formats.prefix(high ? 10 : 6)).map { $0.quality } : [],
                     expires: current.addingTimeInterval(Double(remaining) / 1000))
    }
    func capabilities(mid: String, credential: AccountCredential) async throws -> [String] {
        let id = try MusicNeteaseWire.id(mid), own = epoch, context = try context(credential)
        let root = try await request("/api/song/music/detail/get", data: ["songId": id], context: context)
        try check(own)
        let raw = try MusicQQWire.object(root["data"])
        guard raw["songId"] != nil || raw["id"] != nil else { throw MusicServiceError.upstreamInvalid }
        for name in ["songId", "id"] {
            if let value = raw[name], try MusicNeteaseWire.id(value) != id { throw MusicServiceError.targetChanged }
        }
        let values = try Self.formats.filter { entry in
            guard let value = raw[entry.resource], !(value is NSNull) else { return false }
            if let number = value as? NSNumber, CFGetTypeID(number) == CFBooleanGetTypeID() { return number.boolValue }
            _ = try MusicQQWire.object(value); return true
        }.map(\.quality)
        return ["128k"] + values.filter { $0 != "128k" }
    }
    static func mediaURL(_ text: String) throws -> URL {
        guard let url = URL(string: text), ["http", "https"].contains(url.scheme ?? ""),
              url.user == nil, url.password == nil, url.fragment == nil, url.port == nil,
              let host = url.host?.lowercased(), !host.hasSuffix("."),
              host == "music.126.net" || host.hasSuffix(".music.126.net") ||
                host == "music.163.com" || host.hasSuffix(".music.163.com") else { throw MusicServiceError.invalidMediaCandidate }
        // These official CDN domains exclude IP literals, local names and
        // arbitrary redirects. No account Cookie/header is ever media data.
        // Official replies can still use HTTP for the same TLS-capable CDN.
        // Upgrade only after the complete allowlist check, without decoding or
        // rebuilding the signed path/query or enabling a global ATS exception.
        guard url.scheme == "http" else { return url }
        guard var components = URLComponents(url: url, resolvingAgainstBaseURL: false) else {
            throw MusicServiceError.invalidMediaCandidate
        }
        components.scheme = "https"
        guard let secure = components.url else { throw MusicServiceError.invalidMediaCandidate }
        return secure
    }
    func resolve(mid: String, quality: String, credential: AccountCredential) async throws -> JSONValue {
        let id = try MusicNeteaseWire.id(mid), own = epoch, context = try context(credential)
        guard let format = Self.formats.first(where: { $0.quality == quality }) else { throw MusicServiceError.qualityDisabled }
        try check(own)
        return try await resolveAudio(id: id, quality: quality, level: format.level, context: context)
    }
    func resolveAudio(id: String, quality: String, level: String, context: MusicNeteaseContext) async throws -> JSONValue {
        let own = epoch
        var data = ["ids": "[" + id + "]", "level": level, "encodeType": "flac"]
        if quality == "sky" { data["immerseType"] = "c51" }
        if quality == "vivid" { data["encodeType"] = "mp3" }
        let root = try await request("/api/song/enhance/player/url/v1", data: data, crypto: "xeapi", context: context)
        try check(own)
        let rows = try MusicNeteaseWire.rows(root["data"], maximum: 1)
        guard let row = rows.first else { throw MusicServiceError.audioUnavailable }
        guard try MusicNeteaseWire.id(row["id"]) == id else { throw MusicServiceError.targetChanged }
        let code = try MusicNeteaseWire.code(row["code"])
        if [301, 401].contains(code) { throw MusicServiceError.authRequired }
        if code == 403 { throw MusicServiceError.requestRejected }
        if code == 404 { throw MusicServiceError.notFound }
        if [429, 509].contains(code) {
            MusicRequestTrace(.audio, platform: .netease).record(.failed, code: code, failure: .rateLimited)
            throw MusicServiceError.rateLimited
        }
        if [250, 460, 461].contains(code) { throw MusicServiceError.verificationRequired }
        guard code == 0 || code == 200 else { throw MusicServiceError.audioUnavailable }
        for key in ["freeTrialInfo", "trial", "preview"] {
            guard let value = row[key], !(value is NSNull) else { continue }
            if let number = value as? NSNumber {
                if number.doubleValue == 0 { continue }
                guard number.doubleValue == 1 else { throw MusicServiceError.upstreamInvalid }
                throw MusicServiceError.previewOnly
            }
            if key == "freeTrialInfo", value is [String: Any] { throw MusicServiceError.previewOnly }
            throw MusicServiceError.upstreamInvalid
        }
        // freeTrialPrivilege describes eligibility to consume a trial, not
        // whether this URL IS a trial. Fixed iWebPlus checks freeTrialInfo.
        // Treating resConsumable/userConsumable as delivery would incorrectly
        // reject full, free standard recordings for non-members.
        // Specifying a level does not prove delivery. Never label a downgrade,
        // an unknown actual level, or an automatic retry as the selected one.
        let actual = Self.formats.first(where: { $0.level == row["level"] as? String })
        guard let value = row["url"], !(value is NSNull) else { throw MusicServiceError.audioUnavailable }
        guard let rawURL = value as? String else { throw MusicServiceError.upstreamInvalid }
        guard !rawURL.isEmpty else { throw MusicServiceError.audioUnavailable }
        let url = try Self.mediaURL(MusicQQWire.string(rawURL, maximum: 8192))
        let ttl = min(1800, try MusicQQWire.integer(row["expi"], maximum: Int(Int32.max)))
        let expires = now().addingTimeInterval(Double(ttl))
        guard expires > now() else { throw MusicServiceError.invalidMediaCandidate }
        return .object(["url": .string(url.absoluteString), "expiresAt": .number(expires.timeIntervalSince1970 * 1000),
                        "qualityVerified": .bool(actual != nil),
                        "actualQuality": actual.map { .string($0.quality) } ?? .null,
                        "bitrate": .number(Double(try MusicQQWire.integer(row["br"] ?? 0, maximum: 100_000_000))),
                        "sampleRate": .number(Double(try MusicQQWire.integer(row["sr"] ?? 0, maximum: 768_000))),
                        "duration": .number(Double(try MusicQQWire.integer(row["time"] ?? 0, maximum: 604_800_000)) / 1000),
                        "cacheAllowed": .bool(true),
                        "licenseExpiresAt": .null])
    }
}

protocol MusicNeteasePublicAudioProvider: Sendable {
    func publicStandardAudio(nativeID: String) async throws -> JSONValue
    func publicChorus(nativeID: String) async throws -> MusicChorusPoint?
}
extension NeteaseMusicAccountAdapter: MusicNeteasePublicAudioProvider {
    func publicAudioRequestContext() throws -> MusicNeteaseContext {
        try check(epoch)
        let context: MusicNeteaseContext
        if let saved = publicAudioContext { context = saved }
        else {
            context = MusicNeteaseContext()
            try ensureDevice(context)
            publicAudioContext = context
        }
        // Public standard/chorus endpoints do not require anonymous
        // registration. XEAPI negotiates its own keys on demand; account login
        // uses a separate challenge-local context.
        guard context.accountID.isEmpty, context.cookies["MUSIC_U"] == nil else { throw MusicServiceError.targetChanged }
        return context
    }
    func publicStandardAudio(nativeID: String) async throws -> JSONValue {
        let id = try MusicNeteaseWire.id(nativeID)
        let context = try publicAudioRequestContext()
        return try await resolveAudio(id: id, quality: "128k", level: "standard", context: context)
    }
    func publicChorus(nativeID: String) async throws -> MusicChorusPoint? {
        let id = try MusicNeteaseWire.id(nativeID), own = epoch
        let context = try publicAudioRequestContext()
        try check(own)
        let root = try await request("/api/song/chorus", data: ["ids": "[\"" + id + "\"]"],
                                     crypto: "weapi", context: context)
        try check(own)
        return try MusicChorusWire.netease(root, id: id)
    }
}
