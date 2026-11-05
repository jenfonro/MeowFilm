// Fixed iWebPlus b900c25 and meowmusic cc22a7e importer/normalize.go.
import Foundation
import MeowAccounts
import MeowMusicFeature

struct MusicCredentialImportMaterial: Sendable, CustomStringConvertible, CustomReflectable {
    let credential: AccountCredential
    let nativeID: String
    let deviceState: MusicCredentialImportPreview.DeviceState
    var description: String { "MusicCredentialImportMaterial(<redacted>)" }
    var customMirror: Mirror { Mirror(self, children: ["value": "<redacted>"]) }
}

enum MusicCredentialImportParser {
    static func parse(_ bytes: Data, platform: MusicAccountPlatform,
                      format: MusicCredentialImportFormat) throws -> MusicCredentialImportMaterial {
        do {
            guard !bytes.isEmpty, bytes.count <= MusicCredentialImportInput.maximumBytes else {
                throw MusicCredentialImportError.sizeLimit
            }
            if format == .qqCookie {
                guard platform == .qq, let value = String(data: bytes, encoding: .utf8) else {
                    throw MusicCredentialImportError.invalidFormat
                }
                return try qq(cookie(value, allowed: qqKeys))
            }
            let root = try MusicCredentialImportJSON.object(bytes)
            if format == .iwebPlusSnapshot { return try iweb(root, platform: platform) }
            try keys(root, allowed: ["format", "version", "source", "platform", "accountId", "credentials", "device"])
            guard root.count == 7, root["format"] as? String == "meowfilm.music.credentials",
                  try MusicQQWire.integer(root["version"]) == 1, root["source"] as? String == "meowmusic",
                  root["platform"] as? String == platform.rawValue else { throw MusicCredentialImportError.invalidFormat }
            let id = try identity(root["accountId"]), auth = try object(root["credentials"]), device = try object(root["device"])
            switch platform {
            case .netease:
                try keys(auth, allowed: ["cookie"])
                return try netease(try cookie(text(auth["cookie"], maximum: 65_536), allowed: wyCookieKeys),
                                   id: id, device: device)
            case .qq:
                guard device.isEmpty else { throw MusicCredentialImportError.unsupportedDevice }
                let result = try qq(auth)
                guard result.nativeID == id else { throw MusicCredentialImportError.identityUnconfirmed }
                return result
            case .kugou:
                return try kugou(auth: auth, device: device, expectedID: id)
            }
        } catch let error as MusicCredentialImportError { throw error }
        catch { throw MusicCredentialImportError.invalidFormat }
    }

    private static func iweb(_ root: [String: Any], platform: MusicAccountPlatform) throws -> MusicCredentialImportMaterial {
        switch platform {
        case .netease:
            try keys(root, allowed: ["__private_netease_session", "__private_netease_cloud_scope", "__private_netease_profile_v1"])
            let scope = try text(root["__private_netease_cloud_scope"], maximum: 1024)
            let profile = try storedObject(root["__private_netease_profile_v1"])
            guard scope != "anonymous", profile["scope"] as? String == scope,
                  let logged = profile["loggedIn"] as? NSNumber, CFGetTypeID(logged) == CFBooleanGetTypeID(),
                  logged.boolValue else { throw MusicCredentialImportError.identityUnconfirmed }
            let id = try identity(object(profile["profile"])["userId"])
            return try netease(cookie(text(root["__private_netease_session"], maximum: 65_536), allowed: wyCookieKeys), id: id)
        case .qq:
            // Full runtime-device snapshots cannot be consumed by the existing
            // auth-only QQ credential schema. Never silently discard them.
            if root["device.json"] != nil || root["device.cache.json"] != nil { throw MusicCredentialImportError.unsupportedDevice }
            try keys(root, allowed: ["__private_qq_session"])
            let session = try storedObject(root["__private_qq_session"])
            try keys(session, allowed: ["credential", "revision"])
            _ = try text(session["revision"], maximum: 1024)
            return try qq(object(session["credential"]))
        case .kugou:
            try keys(root, allowed: ["__private_kugou_session", "__private_kugou_module", "__private_kugou_device"])
            let session = try storedObject(root["__private_kugou_session"])
            try keys(session, allowed: ["cookie", "scope"])
            _ = try text(session["scope"], maximum: 1024)
            let cookie = try object(session["cookie"]), module = try storedObject(root["__private_kugou_module"])
            // The old module also contains runtime/install configuration. It
            // grants no runtime, proxy, update, or contribution permissions.
            let seed = try text(module["deviceSeed"], maximum: 32)
            guard seed.range(of: "^[a-f0-9]{32}$", options: .regularExpression) != nil else {
                throw MusicCredentialImportError.deviceMissing
            }
            let fingerprint = try root["__private_kugou_device"].map(storedObject) ?? [:]
            try keys(fingerprint, allowed: ["dfid"])
            let authKeys: Set<String> = ["userid", "token", "t1", "vip_type", "vip_token"]
            let deviceKeys: Set<String> = ["KUGOU_API_GUID", "KUGOU_API_MID", "KUGOU_API_DEV", "KUGOU_API_MAC", "KUGOU_API_WEBGL", "dfid"]
            try keys(cookie, allowed: authKeys.union(deviceKeys))
            var device = cookie.filter { deviceKeys.contains($0.key) }
            device["seed"] = seed
            if let dfid = fingerprint["dfid"] {
                if let old = device["dfid"], try text(old) != text(dfid) { throw MusicCredentialImportError.deviceConflict }
                device["dfid"] = dfid
            }
            return try kugou(auth: cookie.filter { authKeys.contains($0.key) }, device: device)
        }
    }

    private static let qqKeys: Set<String> = [
        "musicid", "str_musicid", "qqmusic_uin", "uin", "wxuin", "p_uin",
        "musickey", "qqmusic_key", "qm_keyst", "wxkey", "openid", "refresh_token", "access_token", "unionid", "refresh_key",
        "encrypt_uin", "encryptUin", "euin", "expired_at", "musickey_create_time", "musickeyCreateTime",
        "key_expires_in", "keyExpiresIn", "login_type", "loginType"
    ]
    private static func qq(_ raw: [String: Any]) throws -> MusicCredentialImportMaterial {
        try keys(raw, allowed: qqKeys)
        var ids = Set<String>()
        for key in ["musicid", "str_musicid", "qqmusic_uin", "uin", "wxuin", "p_uin"] {
            if let value = raw[key] {
                var s = try scalar(value)
                if key == "str_musicid", s.isEmpty { continue }
                if s.hasPrefix("o") { s.removeFirst() }
                while s.hasPrefix("0") { s.removeFirst() }
                ids.insert(try identity(s))
            }
        }
        guard ids.count == 1, let id = ids.first else { throw MusicCredentialImportError.identityUnconfirmed }
        var auth: [String: Any] = ["musicid": Int(id)!, "str_musicid": id]
        guard let key = try alias(raw, ["musickey", "qqmusic_key", "qm_keyst", "wxkey"], required: true) else {
            throw MusicCredentialImportError.invalidFormat
        }
        auth["musickey"] = key
        for key in ["openid", "refresh_token", "access_token", "unionid", "refresh_key"] {
            auth[key] = try raw[key].map { try secret($0, empty: true) } ?? ""
        }
        if let value = try alias(raw, ["encrypt_uin", "encryptUin", "euin"]) {
            guard value.range(of: "^[a-zA-Z0-9=_-]{1,256}$", options: .regularExpression) != nil else {
                throw MusicCredentialImportError.invalidFormat
            }
            auth["encrypt_uin"] = value
        }
        for names in [["expired_at"], ["musickey_create_time", "musickeyCreateTime"],
                      ["key_expires_in", "keyExpiresIn"], ["login_type", "loginType"]] {
            var numbers = Set<Int>()
            for key in names where raw[key] != nil { numbers.insert(try MusicKugouWire.integer(raw[key])) }
            guard numbers.count <= 1 else { throw MusicCredentialImportError.ambiguous }
            auth[names[0]] = numbers.first
        }
        if auth["login_type"] == nil { auth["login_type"] = (auth["musickey"] as? String ?? "").hasPrefix("W_X") ? 1 : 2 }
        return try material(["version": 1, "platform": "tx", "auth": auth], id: id, device: .requiresVerification)
    }
    private static func alias(_ raw: [String: Any], _ names: [String], required: Bool = false) throws -> String? {
        var values = Set<String>()
        for name in names {
            if let value = raw[name] { values.insert(try secret(value)) }
        }
        guard values.count <= 1 else { throw MusicCredentialImportError.ambiguous }
        if required && values.isEmpty { throw MusicCredentialImportError.invalidFormat }
        return values.first
    }

    private static let wyCookieKeys = MusicNeteaseWire.credentialKeys.union(MusicNeteaseWire.deviceKeys).union([
        "sDeviceId", "MUSIC_A", "__remember_me", "ntes_kaola_ad", "WEVNSM", "os", "appver", "osver", "channel", "buildver"
    ])
    private static func netease(_ cookies: [String: Any], id: String, device supplied: [String: Any] = [:]) throws -> MusicCredentialImportMaterial {
        try keys(supplied, allowed: MusicNeteaseWire.deviceKeys.union(["sDeviceId"]))
        var auth: [String: String] = [:], device: [String: String] = [:]
        for (key, raw) in cookies {
            if MusicNeteaseWire.credentialKeys.contains(key) { auth[key] = try MusicNeteaseWire.cookieValue(raw, empty: key != "MUSIC_U") }
            if MusicNeteaseWire.deviceKeys.contains(key) { device[key] = try MusicNeteaseWire.cookieValue(raw) }
        }
        guard auth["MUSIC_U"]?.isEmpty == false else { throw MusicCredentialImportError.invalidFormat }
        for (key, raw) in supplied where key != "sDeviceId" {
            let value = try MusicNeteaseWire.cookieValue(raw)
            if let old = device[key], old != value { throw MusicCredentialImportError.deviceConflict }
            device[key] = value
        }
        for value in [cookies["sDeviceId"], supplied["sDeviceId"]].compactMap({ $0 }) {
            guard try text(value) == device["deviceId"] else { throw MusicCredentialImportError.deviceConflict }
        }
        return try material(["version": 1, "platform": "wy", "accountID": id, "cookies": auth, "device": device],
                            id: id, device: device["deviceId"] == nil ? .requiresVerification : .supplied)
    }
    private static func kugou(auth raw: [String: Any], device rawDevice: [String: Any],
                              expectedID: String? = nil) throws -> MusicCredentialImportMaterial {
        try keys(raw, allowed: ["userid", "token", "t1", "vip_type", "vip_token"])
        var auth = raw
        auth["token"] = try text(raw["token"], maximum: 4096)
        if let token = raw["vip_token"] { auth["vip_token"] = try text(token, maximum: 4096, empty: true) }
        let id = try identity(raw["userid"])
        if let expectedID, expectedID != id { throw MusicCredentialImportError.identityUnconfirmed }
        let value = try MusicKugouAuth(auth, strict: true)
        var saved = rawDevice
        guard saved["seed"] != nil || saved["KUGOU_API_GUID"] != nil || saved["guid"] != nil else {
            throw MusicCredentialImportError.deviceMissing
        }
        let webgl = saved.removeValue(forKey: "KUGOU_API_WEBGL")
        let device: MusicKugouDevice
        do { device = try MusicKugouDevice(saved: saved) }
        catch { throw MusicCredentialImportError.deviceConflict }
        if let webgl, try text(webgl) != device.seed { throw MusicCredentialImportError.deviceConflict }
        return try material(["version": 1, "platform": "kg", "accountID": id, "auth": value.fields, "device": device.fields],
                            id: id, device: device.dfid == nil ? .requiresVerification : .supplied)
    }
    private static func material(_ fields: [String: Any], id: String,
                                 device: MusicCredentialImportPreview.DeviceState) throws -> MusicCredentialImportMaterial {
        let bytes = try JSONSerialization.data(withJSONObject: fields, options: [.sortedKeys, .withoutEscapingSlashes])
        guard bytes.count <= 32_768 else { throw MusicCredentialImportError.sizeLimit }
        return .init(credential: try .init(data: bytes), nativeID: id, deviceState: device)
    }
    private static func cookie(_ raw: String, allowed: Set<String>) throws -> [String: Any] {
        _ = try text(raw, maximum: 65_536)
        var result: [String: Any] = [:]
        for field in raw.split(separator: ";", omittingEmptySubsequences: true) {
            let field = field.trimmingCharacters(in: .whitespaces)
            if ["httponly", "secure"].contains(field.lowercased()) { continue }
            let pair = field.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false)
            guard pair.count == 2 else { throw MusicCredentialImportError.invalidFormat }
            let key = String(pair[0])
            if ["path", "domain", "expires", "max-age", "samesite"].contains(key.lowercased()) { continue }
            guard allowed.contains(key), let value = String(pair[1]).removingPercentEncoding else {
                throw MusicCredentialImportError.invalidFormat
            }
            _ = try text(value, maximum: 8192, empty: true)
            if let old = result[key] as? String, old != value { throw MusicCredentialImportError.ambiguous }
            result[key] = value
        }
        return result
    }
    private static func storedObject(_ value: Any?) throws -> [String: Any] {
        if let string = value as? String { return try MusicCredentialImportJSON.object(Data(string.utf8)) }
        return try object(value)
    }
    private static func object(_ value: Any?) throws -> [String: Any] {
        guard let value = value as? [String: Any] else { throw MusicCredentialImportError.invalidFormat }; return value
    }
    private static func keys(_ value: [String: Any], allowed: Set<String>) throws {
        guard Set(value.keys).isSubset(of: allowed) else { throw MusicCredentialImportError.invalidFormat }
    }
    private static func identity(_ value: Any?) throws -> String {
        do { return try MusicQQWire.id(integerToken(value)) } catch { throw MusicCredentialImportError.identityUnconfirmed }
    }
    private static func scalar(_ value: Any) throws -> String {
        if let value = value as? String { return value }
        return String(try MusicQQWire.integer(integerToken(value)))
    }
    /// Decimal/exponent NSNumber values are not integer tokens. Opaque
    /// JSONNumber values also fail the ID consumer rather than being rounded;
    /// neither restriction applies to unused metadata.
    private static func integerToken(_ value: Any?) throws -> Any? {
        if let number = value as? NSNumber, ["d", "f"].contains(String(cString: number.objCType)) {
            throw MusicCredentialImportError.identityUnconfirmed
        }
        return value
    }
    private static func text(_ value: Any?, maximum: Int = 8192, empty: Bool = false) throws -> String {
        guard let value = value as? String, empty || !value.isEmpty, value.utf8.count <= maximum,
              !value.unicodeScalars.contains(where: { $0.value < 32 || $0.value == 127 }) else {
            throw MusicCredentialImportError.invalidFormat
        }; return value
    }
    private static func secret(_ value: Any?, empty: Bool = false) throws -> String {
        let value = try text(value, empty: empty)
        guard value.utf8.allSatisfy({ $0 >= 33 && $0 <= 126 && ![59, 44, 34, 92].contains($0) }) else {
            throw MusicCredentialImportError.invalidFormat
        }; return value
    }
}
