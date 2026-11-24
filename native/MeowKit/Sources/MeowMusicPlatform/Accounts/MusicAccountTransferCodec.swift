import Foundation
import CoreFoundation
import MeowAccounts
import MeowMusicFeature
import MeowRuntime
import MeowJSON

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
            guard String(data: data, encoding: .utf8) != nil,
                  let root = try? JSONObject.decode(data),
                  root["format"] as? String == "meowfilm.music-accounts",
                  let version = root["version"] as? NSNumber,
                  CFGetTypeID(version) != CFBooleanGetTypeID(), version == 1,
                  let rows = root["accounts"] as? [[String: Any]], rows.count <= 3 else {
                throw MusicAccountTransferError.invalidFile
            }
            var seen = Set<MusicAccountPlatform>()
            let entries = try rows.map { row -> MusicAccountTransferEntry in
                guard let name = row["platform"] as? String, let platform = MusicAccountPlatform(rawValue: name),
                      seen.insert(platform).inserted, let id = row["userId"] as? String,
                      let credentials = row["credentials"] as? [String: Any] else {
                    throw MusicAccountTransferError.invalidFile
                }
                let projected = credentials.filter { consumed(platform: platform, key: $0.key) }
                guard let fields = projected as? [String: String] else { throw MusicAccountTransferError.invalidFile }
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
    private static let qqAliases: [String: String] = [
        "strMusicid": "str_musicid",
        "refreshToken": "refresh_token",
        "accessToken": "access_token",
        "refreshKey": "refresh_key",
        "expiredAt": "expired_at",
        "musickeyCreateTime": "musickey_create_time",
        "keyExpiresIn": "key_expires_in",
        "loginType": "login_type",
        "encryptUin": "encrypt_uin"
    ]

    private static func consumed(platform: MusicAccountPlatform, key: String) -> Bool {
        allowed[platform]!.contains(key) || (platform == .qq && qqAliases[key] != nil)
    }

    private static func canonical(_ fields: [String: String], platform: MusicAccountPlatform) throws -> [String: String] {
        var result = fields.filter { consumed(platform: platform, key: $0.key) }
        if platform == .qq {
            for (alias, key) in qqAliases {
                if let value = result.removeValue(forKey: alias) {
                    guard result[key] == nil || result[key] == value else { throw MusicAccountTransferError.invalidFile }
                    result[key] = value
                }
            }
        }
        return result
    }

    static func credential(_ row: MusicAccountTransferEntry) throws -> AccountCredential {
        let fields = try canonical(row.fields, platform: row.platform)
        guard !row.userID.isEmpty, row.userID.utf8.count <= 128,
              row.userID.utf8.allSatisfy({ $0 >= 33 && $0 <= 126 }),
              fields.allSatisfy({ key, value in
                  value.utf8.allSatisfy { $0 >= 33 && $0 <= 126 && ![34, 59, 92].contains($0) &&
                      ($0 != 44 || key == "_ntes_nnid") }
              }) else { throw MusicAccountTransferError.invalidFile }
        switch row.platform {
        case .netease:
            guard fields["MUSIC_U"]?.isEmpty == false else { throw MusicAccountTransferError.invalidFile }
        case .qq:
            guard (fields["musicid"] ?? fields["str_musicid"]) == row.userID,
                  fields["musicid"] == nil || fields["musicid"] == row.userID,
                  fields["str_musicid"] == nil || fields["str_musicid"] == row.userID,
                  fields["musickey"]?.isEmpty == false else { throw MusicAccountTransferError.invalidFile }
        case .kugou:
            guard fields["userid"] == row.userID,
                  fields["token"]?.isEmpty == false else { throw MusicAccountTransferError.invalidFile }
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
            if consumed(platform: platform, key: name) {
                fields[name] = item[item.index(after: at)...].trimmingCharacters(in: .whitespaces)
            }
        }
        let result = MusicAccountTransferEntry(platform: platform, userID: try value[music: "userId"].musicRequiredText(),
                                               fields: try canonical(fields, platform: platform))
        _ = try self.credential(result)
        return result
    }
}
