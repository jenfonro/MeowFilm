// SPDX-License-Identifier: GPL-3.0-only
// Fixed cc22a7e kugou/{account,music}.go; explicit official source only.
import Foundation
import MeowAccounts
import MeowMusicFeature
import MeowJSON

struct MusicKugouRights: Sendable {
    let vip: Bool?
    let licenseExpires: Date?
    let observedUntil: Date
}
extension KugouMusicAccountAdapter {
    static func expiry(_ value: Any?) throws -> Date? {
        guard let value, !(value is NSNull), (value as? String) != "" else { return nil }
        if let number = try? MusicKugouWire.integer(value) {
            if number == 0 { return nil }
            return Date(timeIntervalSince1970: Double(number) / (number < 1_000_000_000_000 ? 1 : 1000))
        }
        let text = try MusicKugouWire.text(value, maximum: 64)
        let iso = ISO8601DateFormatter()
        if let date = iso.date(from: text) { return date }
        iso.formatOptions.insert(.withFractionalSeconds)
        if let date = iso.date(from: text) { return date }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX"); formatter.timeZone = TimeZone(secondsFromGMT: 8 * 3600)
        formatter.isLenient = false
        for format in ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd"] {
            formatter.dateFormat = format
            if let date = formatter.date(from: text), formatter.string(from: date) == text { return date }
        }
        throw MusicServiceError.upstreamInvalid
    }
    func membership(_ context: MusicKugouContext) async throws -> MusicKugouRights {
        let root = try await request(.membership, context: context, query: ["busi_type": "concept"])
        let at = now()
        guard let data = root["data"] as? [String: Any] else {
            return .init(vip: nil, licenseExpires: nil, observedUntil: at)
        }
        if let id = data["userid"], try MusicKugouWire.id(id) != context.auth!.uid { throw MusicServiceError.targetChanged }
        var rows = try data["busi_vip"].map { try MusicKugouWire.rows($0, maximum: 64) } ?? []
        if let value = data["is_vip"] {
            rows.append(["is_vip": value, "product_type": "main", "vip_begin_time": data["vip_begin_time"] ?? NSNull(),
                         "vip_end_time": data["vip_end_time"] ?? NSNull()])
        }
        var known = false, uncertain = false, active = false, license: Date?
        var until = at.addingTimeInterval(24 * 3600)
        for row in rows {
            if let id = row["userid"], try MusicKugouWire.id(id) != context.auth!.uid { throw MusicServiceError.targetChanged }
            guard row["busi_type"] == nil || row["busi_type"] as? String == "concept",
                  let product = row["product_type"] as? String,
                  ["svip", "tvip", "wvip", "qvip", "dvip", "evip", "main"].contains(product),
                  let flag = try? MusicKugouWire.integer(row["is_vip"], maximum: 1) else {
                uncertain = true; continue
            }
            let begin: Date?, end: Date?
            do { begin = try Self.expiry(row["vip_begin_time"]); end = try Self.expiry(row["vip_end_time"]) }
            catch { uncertain = true; continue }
            known = true
            for boundary in [begin, end].compactMap({ $0 }) where boundary > at { until = min(until, boundary) }
            if !active, flag == 1, (begin == nil || begin! <= at), (end == nil || end! > at) {
                active = true; license = end
            }
        }
        try check(context)
        if active {
            return .init(vip: true, licenseExpires: license, observedUntil: min(until, license ?? at.addingTimeInterval(60)))
        }
        return .init(vip: known && !uncertain ? false : nil, licenseExpires: nil, observedUntil: known && !uncertain ? until : at)
    }
    func audioDetail(_ hash: String, context: MusicKugouContext) async throws -> MusicKugouSong {
        let ms = Int(now().timeIntervalSince1970 * 1000)
        let root = try await request(.audio, context: context, body: [
            "appid": 1005, "clienttime": ms, "clientver": 20489, "data": [["hash": hash, "audio_id": 0]],
            "dfid": context.device.dfid ?? "-", "key": MusicKugouCrypto.paramsKey(ms), "mid": context.device.mid,
            "userid": context.auth!.uid, "token": context.auth!.token
        ])
        let rows = try MusicKugouWire.rows(root["data"], maximum: 1)
        guard let raw = rows.first else { throw MusicServiceError.targetChanged }
        let song = try MusicKugouSong(raw)
        guard song.hash == hash else { throw MusicServiceError.targetChanged }
        return song
    }
    func capabilities(mid: String, credential: AccountCredential) async throws -> [String] {
        let hash = try MusicKugouWire.hash(mid), context = try context(credential)
        let rights = try await membership(context), song = try await audioDetail(hash, context: context)
        try check(context)
        guard rights.vip == true, rights.observedUntil > now() else { return [] }
        return MusicKugouSong.qualities.filter { song.resources[$0] != nil }
    }
    func resolve(mid: String, quality: String, credential: AccountCredential) async throws -> JSONValue {
        let hash = try MusicKugouWire.hash(mid), context = try context(credential)
        let formats = ["128k": "128", "320k": "320", "flac": "flac", "flac24bit": "high"]
        guard let wireQuality = formats[quality] else { throw MusicServiceError.qualityDisabled }
        let rights = try await membership(context)
        guard rights.vip == true, rights.observedUntil > now() else { throw MusicServiceError.qualityDisabled }
        let song = try await audioDetail(hash, context: context)
        guard let resource = song.resources[quality] else { throw MusicServiceError.qualityDisabled }
        let queryHash = resource.hash.lowercased()
        let query = [
            "album_id": song.albumID, "area_code": "1", "hash": queryHash, "ssa_flag": "is_fromtrack",
            "version": "11430", "page_id": "151369488", "quality": wireQuality, "album_audio_id": song.albumAudioID,
            "behavior": "play", "pid": "2", "cmd": "26", "pidversion": "3001", "IsFreePart": "0",
            "ppage_id": "463467626,350369493,788954147", "cdnBackup": "1", "module": "", "clientver": "11430",
            "key": MusicKugouCrypto.trackerKey(hash: queryHash, mid: context.device.mid, uid: context.auth!.uid)
        ]
        let root = try await request(.url, context: context, query: query)
        try check(context)
        guard rights.observedUntil > now(), rights.licenseExpires == nil || rights.licenseExpires! > now() else {
            throw MusicServiceError.qualityDisabled
        }
        guard case .object(var result) = try Self.audioResult(root, song: song, resource: resource, context: context) else {
            throw MusicServiceError.invalidMediaCandidate
        }
        result["cacheAllowed"] = .bool(true)
        result["licenseExpiresAt"] = rights.licenseExpires.map { .number($0.timeIntervalSince1970 * 1000) } ?? .null
        return .object(result)
    }
    static func audioResult(_ root: [String: Any], song: MusicKugouSong, resource: MusicKugouResource,
                            context: MusicKugouContext) throws -> JSONValue {
        var data = root["data"] as? [String: Any]
        if root["url"] != nil {
            guard data?["url"] == nil else { throw MusicServiceError.upstreamInvalid }
            data = root
        }
        guard let data else { throw MusicServiceError.invalidMediaCandidate }
        let hashes = [data["hash"], data["fileHash"], data["req_hash"], root["hash"]].compactMap { $0 }
        guard !hashes.isEmpty else { throw MusicServiceError.targetChanged }
        for value in hashes {
            guard try MusicKugouWire.hash(value) == resource.hash else { throw MusicServiceError.targetChanged }
        }
        let trans = [root["trans_param"], data["trans_param"]].compactMap { $0 }
        let sources = [root, data] + (try trans.map(MusicQQWire.object))
        var whole = false, unknown = false
        for source in sources {
            for (key, expected) in [("album_id", song.albumID), ("album_audio_id", song.albumAudioID)] {
                if let value = source[key] {
                    let id = try MusicKugouWire.id(value, zero: true)
                    if id != "0", expected != "0", id != expected { throw MusicServiceError.targetChanged }
                }
            }
            for key in ["is_free_part", "IsFreePart", "free_part"] {
                if let value = source[key] {
                    if let n = value as? NSNumber, CFGetTypeID(n) == CFBooleanGetTypeID() {
                        guard !n.boolValue else { throw MusicServiceError.invalidMediaCandidate }
                    } else {
                        guard try MusicKugouWire.integer(value) == 0 else { throw MusicServiceError.invalidMediaCandidate }
                    }
                    whole = true
                }
            }
            // Presence (even null) matches the reference's conservative trial check.
            if source["freeTrialInfo"] != nil || source["freePart"] != nil { throw MusicServiceError.invalidMediaCandidate }
            for key in ["pay_type", "privilege", "fail_process"] {
                if let value = source[key], try MusicKugouWire.integer(value) != 0 { throw MusicServiceError.invalidMediaCandidate }
            }
            if let value = source["priv_status"] { _ = try MusicKugouWire.integer(value); unknown = true }
            for key in ["error_code", "errcode", "err_code"] {
                if let value = source[key], try MusicKugouWire.integer(value) != 0 { throw MusicServiceError.upstreamFailed }
            }
            if let status = source["status"], try MusicKugouWire.integer(status) != 1 { throw MusicServiceError.upstreamFailed }
            if let code = source["code"], try ![0, 200].contains(MusicKugouWire.integer(code)) { throw MusicServiceError.upstreamFailed }
            if let error = source["error"] {
                let falseBoolean = (error as? NSNumber).map { CFGetTypeID($0) == CFBooleanGetTypeID() && !$0.boolValue } ?? false
                guard falseBoolean || (error as? String) == "" || (try? MusicKugouWire.integer(error)) == 0 else { throw MusicServiceError.upstreamFailed }
            }
        }
        var size: Int?
        for key in ["fileSize", "filesize"] {
            if let value = data[key] {
                let n = try MusicKugouWire.integer(value, minimum: 1)
                guard n == resource.size, size == nil || size == n else { throw MusicServiceError.invalidMediaCandidate }; size = n
            }
        }
        // The native audio contract has no unknown-access variant. Do not turn
        // the reference's access=unknown result into a playable/full candidate.
        guard whole, !unknown, size == resource.size else { throw MusicServiceError.invalidMediaCandidate }
        let urls = (data["url"] as? [Any]) ?? data["url"].map { [$0] } ?? []
        guard !urls.isEmpty, urls.count <= 16 else { throw MusicServiceError.invalidMediaCandidate }
        let parsed = try urls.map { raw -> URL in
            let url = try MusicKugouSong.assetURL(MusicKugouWire.text(raw, maximum: 8192))
            let decoded = url.absoluteString.removingPercentEncoding ?? url.absoluteString
            // A signed CDN locator may contain its own key, but never account
            // token/vip_token or a credential-bearing header from this provider.
            for secret in [context.auth?.token, context.auth?.vipToken].compactMap({ $0 }) where !secret.isEmpty {
                guard !decoded.contains(secret) else { throw MusicServiceError.invalidMediaCandidate }
            }
            return url
        }
        // No generic URL TTL exists in cc22a7e. License expiry is NOT URL expiry.
        return .object(["url": .string(parsed[0].absoluteString), "expiresAt": .null, "qualityVerified": .bool(false)])
    }
}
