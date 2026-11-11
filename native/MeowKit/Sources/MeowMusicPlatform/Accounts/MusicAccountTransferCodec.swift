import Foundation
import CoreFoundation
import MeowAccounts
import MeowMusicFeature

/// File-only representation; never used by account presentation or diagnostics.
struct MusicAccountTransferEntry: Sendable, CustomStringConvertible, CustomDebugStringConvertible, CustomReflectable {
    let platform: MusicAccountPlatform
    let userID: String
    let fields: [String: String]
    var description: String { "MusicAccountTransferEntry(<redacted>)" }
    var debugDescription: String { description }
    var customMirror: Mirror { Mirror(self, children: ["value": "<redacted>"]) }
}

enum MusicAccountTransferCodec {
    static func decode(_ payload: MusicAccountFilePayload) throws -> [MusicAccountTransferEntry] {
        do {
            var data = payload.dataForFile()
            if data.starts(with: [0xEF, 0xBB, 0xBF]) { data.removeFirst(3) }
            guard String(data: data, encoding: .utf8) != nil else { throw MusicAccountTransferError.invalidFile }
            var scanner = UniqueKeys(bytes: Array(data))
            try scanner.value(depth: 0)
            scanner.space()
            guard scanner.offset == scanner.bytes.count,
                  let root = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  Set(root.keys) == ["format", "version", "accounts"],
                  root["format"] as? String == "meowfilm.music-accounts",
                  let version = root["version"] as? NSNumber,
                  CFGetTypeID(version) != CFBooleanGetTypeID(), version == 1,
                  let rows = root["accounts"] as? [[String: Any]], rows.count <= 3 else {
                throw MusicAccountTransferError.invalidFile
            }
            var seen = Set<MusicAccountPlatform>()
            let entries = try rows.map { row -> MusicAccountTransferEntry in
                guard Set(row.keys) == ["platform", "userId", "credentials"],
                      let name = row["platform"] as? String, let platform = MusicAccountPlatform(rawValue: name),
                      seen.insert(platform).inserted, let id = row["userId"] as? String,
                      let fields = row["credentials"] as? [String: String] else {
                    throw MusicAccountTransferError.invalidFile
                }
                return .init(platform: platform, userID: id, fields: fields)
            }
            return MusicAccountPlatform.allCases.compactMap { p in entries.first { $0.platform == p } }
        } catch { throw MusicAccountTransferError.invalidFile }
    }

    static func encode(_ entries: [MusicAccountTransferEntry]) throws -> MusicAccountFilePayload {
        let rows: [[String: Any]] = MusicAccountPlatform.allCases.compactMap { platform in
            guard let row = entries.first(where: { $0.platform == platform }) else { return nil }
            return ["platform": platform.rawValue, "userId": row.userID, "credentials": row.fields]
        }
        do {
            return try .init(fileData: JSONSerialization.data(withJSONObject: [
                "format": "meowfilm.music-accounts", "version": 1, "accounts": rows
            ], options: [.sortedKeys, .prettyPrinted, .withoutEscapingSlashes]))
        } catch { throw MusicAccountTransferError.invalidFile }
    }

    private static let qqNumbers = ["expired_at", "musickey_create_time", "key_expires_in", "login_type"]
    private static let qqStrings: Set<String> = ["musicid", "str_musicid", "musickey", "openid", "refresh_token", "access_token", "unionid", "refresh_key", "encrypt_uin"]
    private static let aliases = ["musickeyCreateTime": "musickey_create_time", "keyExpiresIn": "key_expires_in", "loginType": "login_type", "encryptUin": "encrypt_uin"]
    static func normalizedQQ(_ input: [String: String]) throws -> [String: String] {
        var fields = input
        for (alias, key) in aliases {
            if let value = fields.removeValue(forKey: alias) {
                guard fields[key] == nil || fields[key] == value else { throw MusicServiceError.invalidResponse }
                fields[key] = value
            }
        }
        guard Set(fields.keys).isSubset(of: qqStrings.union(qqNumbers)) else { throw MusicServiceError.invalidResponse }
        return fields
    }
    private static func number(_ value: String) throws -> Int {
        guard value.range(of: "^(0|[1-9][0-9]{0,15})$", options: .regularExpression) != nil,
              let result = Int(value), result <= MusicPlatformJSON.maximumInteger else { throw MusicServiceError.invalidResponse }
        return result
    }
    static func credential(_ row: MusicAccountTransferEntry) throws -> AccountCredential {
        let id: String
        switch row.platform {
        case .netease: id = try MusicNeteaseWire.id(row.userID)
        case .qq: id = try MusicQQWire.id(row.userID)
        case .kugou: id = try MusicKugouWire.id(row.userID)
        }
        let fields = row.fields
        let root: [String: Any]
        switch row.platform {
        case .netease:
            guard Set(fields.keys).isSubset(of: MusicNeteaseWire.credentialKeys.union(MusicNeteaseWire.deviceKeys)),
                  fields["MUSIC_U"]?.isEmpty == false else { throw MusicServiceError.invalidResponse }
            let cookies = try fields.filter { MusicNeteaseWire.credentialKeys.contains($0.key) }
                .mapValues { try MusicNeteaseWire.cookieValue($0, empty: true) }
            let device = try fields.filter { MusicNeteaseWire.deviceKeys.contains($0.key) }
                .mapValues { try MusicNeteaseWire.cookieValue($0, empty: true) }
            guard ["deviceId", "_ntes_nuid", "_ntes_nnid", "WNMCID"].allSatisfy({ device[$0]?.isEmpty == false }) else {
                throw MusicServiceError.invalidResponse
            }
            root = ["version": 1, "platform": "wy", "accountID": id, "cookies": cookies, "device": device]
        case .qq:
            let canonical = try normalizedQQ(fields)
            guard try MusicQQWire.id(canonical["musicid"] ?? canonical["str_musicid"]) == id,
                  canonical["str_musicid"] == nil || canonical["str_musicid"] == id,
                  canonical["musicid"] == nil || canonical["musicid"] == id else { throw MusicServiceError.targetChanged }
            var auth: [String: Any] = canonical
            for key in qqNumbers { if let value = canonical[key] { auth[key] = try number(value) } }
            root = ["version": 1, "platform": "tx", "auth": auth]
        case .kugou:
            let authKeys: Set<String> = ["userid", "token", "t1", "vip_type", "vip_token"]
            let deviceKeys: Set<String> = ["KUGOU_API_GUID", "KUGOU_API_MID", "KUGOU_API_DEV", "KUGOU_API_MAC", "dfid"]
            guard Set(fields.keys).isSubset(of: authKeys.union(deviceKeys)), fields["userid"] == id,
                  let seed = fields["KUGOU_API_GUID"], seed.count == 32,
                  ["KUGOU_API_MID", "KUGOU_API_DEV", "KUGOU_API_MAC"].allSatisfy({ fields[$0] != nil }) else {
                throw MusicServiceError.invalidResponse
            }
            var auth: [String: Any] = fields.filter { authKeys.contains($0.key) }
            if let value = fields["vip_type"] { auth["vip_type"] = try number(value) }
            if let value = fields["t1"], let integer = try? number(value) { auth["t1"] = integer }
            var device: [String: Any] = fields.filter { deviceKeys.contains($0.key) }; device["seed"] = seed
            _ = try MusicKugouAuth(auth, strict: true); _ = try MusicKugouDevice(saved: device)
            root = ["version": 1, "platform": "kg", "accountID": id, "auth": auth, "device": device]
        }
        let data = try MusicQQWire.json(root)
        if row.platform != .qq, data.count > 32_768 { throw MusicServiceError.limit }
        return try AccountCredential(data: data)
    }

    static func entry(_ credential: AccountCredential, platform: MusicAccountPlatform) throws -> MusicAccountTransferEntry {
        let root = try MusicQQWire.decode(credential.dataRepresentation())
        guard root["platform"] as? String == platform.rawValue else { throw MusicServiceError.invalidResponse }
        var fields: [String: String] = [:]
        let id: String
        func strings(_ raw: Any?) throws -> [String: String] {
            try MusicQQWire.object(raw).mapValues { value in
                if let text = value as? String { return text }
                return String(try MusicQQWire.integer(value))
            }
        }
        switch platform {
        case .netease:
            id = try MusicNeteaseWire.id(root["accountID"])
            fields = try strings(root["cookies"])
            let device = try strings(root["device"])
            fields.merge(device) { _, device in device }
        case .qq:
            fields = try normalizedQQ(strings(root["auth"]))
            id = try MusicQQWire.id(fields["musicid"] ?? fields["str_musicid"])
        case .kugou:
            id = try MusicKugouWire.id(root["accountID"])
            fields = try strings(root["auth"])
            let device = try MusicQQWire.object(root["device"])
            guard let seed = device["seed"] as? String, seed.count == 32,
                  ["KUGOU_API_MID", "KUGOU_API_DEV", "KUGOU_API_MAC"].allSatisfy({ device[$0] != nil }) else {
                throw MusicServiceError.verificationRequired
            }
            let bound = try MusicKugouDevice(saved: device)
            fields.merge(try strings(bound.fields)) { _, device in device }
            fields.removeValue(forKey: "seed")
        }
        let result = MusicAccountTransferEntry(platform: platform, userID: id, fields: fields)
        _ = try self.credential(result)
        return result
    }

    /// Foundation rejects invalid JSON; this pass additionally rejects repeated decoded keys.
    private struct UniqueKeys {
        let bytes: [UInt8]
        var offset = 0
        mutating func space() { while offset < bytes.count, [9, 10, 13, 32].contains(bytes[offset]) { offset += 1 } }
        mutating func take(_ byte: UInt8) -> Bool {
            space(); guard offset < bytes.count, bytes[offset] == byte else { return false }
            offset += 1; return true
        }
        mutating func string() throws -> String {
            space(); let start = offset
            guard take(34) else { throw MusicAccountTransferError.invalidFile }
            while offset < bytes.count {
                let byte = bytes[offset]; offset += 1
                if byte == 92 { offset += 1 }
                else if byte == 34 { return try JSONDecoder().decode(String.self, from: Data(bytes[start..<offset])) }
            }
            throw MusicAccountTransferError.invalidFile
        }
        mutating func value(depth: Int) throws {
            guard depth < 32 else { throw MusicAccountTransferError.invalidFile }
            space(); guard offset < bytes.count else { throw MusicAccountTransferError.invalidFile }
            if bytes[offset] == 34 { _ = try string() }
            else if take(123) {
                var keys = Set<String>()
                if take(125) { return }
                repeat {
                    guard keys.insert(try string()).inserted, take(58) else { throw MusicAccountTransferError.invalidFile }
                    try value(depth: depth + 1)
                } while take(44)
                guard take(125) else { throw MusicAccountTransferError.invalidFile }
            } else if take(91) {
                if take(93) { return }
                repeat { try value(depth: depth + 1) } while take(44)
                guard take(93) else { throw MusicAccountTransferError.invalidFile }
            } else {
                let start = offset
                while offset < bytes.count, ![9, 10, 13, 32, 44, 93, 125].contains(bytes[offset]) { offset += 1 }
                guard start < offset else { throw MusicAccountTransferError.invalidFile }
            }
        }
    }
}
