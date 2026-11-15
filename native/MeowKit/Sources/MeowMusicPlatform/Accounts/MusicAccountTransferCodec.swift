import Foundation
import CoreFoundation
import MeowAccounts
import MeowMusicFeature
import MeowRuntime

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

    private static let allowed: [MusicAccountPlatform: Set<String>] = [
        .netease: ["MUSIC_U", "MUSIC_A", "__csrf", "JSESSIONID", "NMTID", "deviceId", "_ntes_nuid", "_ntes_nnid", "WNMCID"],
        .qq: ["musicid", "str_musicid", "musickey", "openid", "refresh_token", "access_token", "unionid", "refresh_key",
              "encrypt_uin", "expired_at", "musickey_create_time", "key_expires_in", "login_type"],
        .kugou: ["userid", "token", "t1", "vip_type", "vip_token", "KUGOU_API_GUID", "KUGOU_API_MID", "KUGOU_API_DEV",
                 "KUGOU_API_MAC", "dfid"]
    ]
    private static let qqNumbers: Set<String> = ["expired_at", "musickey_create_time", "key_expires_in", "login_type"]
    private static let qqAliases = ["musickeyCreateTime": "musickey_create_time", "keyExpiresIn": "key_expires_in",
        "loginType": "login_type", "encryptUin": "encrypt_uin"]
    private static func canonical(_ fields: [String: String], platform: MusicAccountPlatform) throws -> [String: String] {
        var result = fields
        if platform == .qq {
            for (alias, key) in qqAliases {
                if let value = result.removeValue(forKey: alias) {
                    guard result[key] == nil || result[key] == value else { throw MusicAccountTransferError.invalidFile }
                    result[key] = value
                }
            }
            for key in qqNumbers {
                if let value = result[key] {
                    guard !value.isEmpty, value.utf8.allSatisfy({ (48...57).contains($0) }),
                          value == "0" || !value.hasPrefix("0"),
                          let number = UInt64(value), number <= 9_007_199_254_740_991 else {
                        throw MusicAccountTransferError.invalidFile
                    }
                }
            }
        }
        if platform == .kugou, let value = result["vip_type"] {
            guard !value.isEmpty, value.utf8.allSatisfy({ (48...57).contains($0) }),
                  value == "0" || !value.hasPrefix("0"),
                  let number = UInt64(value), number <= 9_007_199_254_740_991 else {
                throw MusicAccountTransferError.invalidFile
            }
        }
        return result
    }
    static func credential(_ row: MusicAccountTransferEntry) throws -> AccountCredential {
        let fields = try canonical(row.fields, platform: row.platform)
        guard !row.userID.isEmpty, row.userID.utf8.count <= 20, !row.userID.hasPrefix("0"),
              row.userID.utf8.allSatisfy({ (48...57).contains($0) }),
              Set(fields.keys).isSubset(of: allowed[row.platform]!),
              fields.allSatisfy({ key, value in
                  value.utf8.allSatisfy { $0 >= 33 && $0 <= 126 && ![34, 59, 92].contains($0) &&
                      ($0 != 44 || key == "_ntes_nnid") }
              }) else { throw MusicAccountTransferError.invalidFile }
        switch row.platform {
        case .netease:
            guard ["MUSIC_U", "deviceId", "_ntes_nuid", "_ntes_nnid", "WNMCID"].allSatisfy({
                fields[$0]?.isEmpty == false
            }) else { throw MusicAccountTransferError.invalidFile }
        case .qq:
            guard (fields["musicid"] ?? fields["str_musicid"]) == row.userID,
                  fields["musicid"] == nil || fields["musicid"] == row.userID,
                  fields["str_musicid"] == nil || fields["str_musicid"] == row.userID,
                  fields["musickey"]?.isEmpty == false else { throw MusicAccountTransferError.invalidFile }
        case .kugou:
            guard fields["userid"] == row.userID,
                  ["token", "KUGOU_API_GUID", "KUGOU_API_MID", "KUGOU_API_DEV", "KUGOU_API_MAC"].allSatisfy({
                      fields[$0]?.isEmpty == false
                  }), let seed = fields["KUGOU_API_GUID"], seed.utf8.count == 32,
                  seed.utf8.allSatisfy({ (48...57).contains($0) || (65...70).contains($0) || (97...102).contains($0) }),
                  let mid = fields["KUGOU_API_MID"], mid.utf8.count <= 39,
                  mid.utf8.allSatisfy({ (48...57).contains($0) }) else {
                throw MusicAccountTransferError.invalidFile
            }
        }
        let cookie = fields.keys.sorted().map { $0 + "=" + fields[$0]! }.joined(separator: "; ")
        return try MusicAccountCredential.make(platform: row.platform, userID: row.userID, cookie: cookie)
    }
    static func entry(_ credential: AccountCredential, platform: MusicAccountPlatform) throws -> MusicAccountTransferEntry {
        let value = try MusicAccountCredential.read(credential)
        guard value[music: "platform"] == .string(platform.rawValue) else { throw MusicServiceError.invalidResponse }
        let cookie = try value[music: "cookie"].musicRequiredText()
        var fields: [String: String] = [:]
        for item in cookie.split(separator: ";") {
            guard let at = item.firstIndex(of: "=") else { throw MusicServiceError.invalidResponse }
            let name = item[..<at].trimmingCharacters(in: .whitespaces)
            if allowed[platform]!.contains(name) || platform == .qq && qqAliases[name] != nil {
                fields[name] = item[item.index(after: at)...].trimmingCharacters(in: .whitespaces)
            }
        }
        let result = MusicAccountTransferEntry(platform: platform, userID: try value[music: "userId"].musicRequiredText(),
                                              fields: try canonical(fields, platform: platform))
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
