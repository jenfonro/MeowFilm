// SPDX-License-Identifier: GPL-3.0-only
// Fixed meowmusic cc22a7e netease/transport.go; no old server or JS gateway.
import Foundation
import CryptoKit
import MeowAccounts
import MeowMusicFeature

enum MusicNeteaseWire {
    static let deviceKeys: Set<String> = ["deviceId", "_ntes_nuid", "_ntes_nnid", "WNMCID", "NMTID"]
    static let credentialKeys: Set<String> = ["MUSIC_U", "__csrf", "JSESSIONID"]
    static func id(_ value: Any?) throws -> String { try MusicQQWire.songID(value) }
    static func code(_ value: Any?) throws -> Int {
        guard let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID(),
              number.doubleValue.isFinite, number.doubleValue.rounded() == number.doubleValue,
              (-99_999...99_999).contains(number.doubleValue) else { throw MusicServiceError.upstreamInvalid }
        return number.intValue
    }
    static func bool(_ value: Any?) throws -> Bool {
        guard let n = value as? NSNumber, CFGetTypeID(n) == CFBooleanGetTypeID() else { throw MusicServiceError.upstreamInvalid }
        return n.boolValue
    }
    static func rows(_ value: Any?) throws -> [[String: Any]] {
        guard let rows = value as? [Any] else { throw MusicServiceError.upstreamInvalid }
        return try rows.map(MusicQQWire.object)
    }
    static func rows(_ value: Any?, maximum: Int) throws -> [[String: Any]] {
        let rows = try self.rows(value)
        guard rows.count <= maximum else { throw MusicServiceError.upstreamInvalid }
        return rows
    }
    static func cookieValue(_ value: Any?, empty: Bool = false) throws -> String {
        let s = try MusicQQWire.string(value, maximum: 8192, empty: empty)
        guard s.utf8.allSatisfy({ $0 >= 33 && $0 <= 126 && $0 != 34 && $0 != 59 && $0 != 92 }) else { throw MusicServiceError.upstreamInvalid }
        return s
    }
    static func cookieHeader(_ values: [String: String]) -> String {
        String(decoding: MusicQQWire.form(values), as: UTF8.self).replacingOccurrences(of: "&", with: "; ")
    }
    static func cookies(_ response: MusicAccountHTTPResponse, host: String) throws -> [String: String?] {
        guard let value = response.header("set-cookie") else { return [:] }
        guard value.utf8.count <= 32_768 else { throw MusicServiceError.limit }
        guard !value.utf8.contains(where: { $0 == 0 || $0 == 10 || $0 == 13 }) else { throw MusicServiceError.upstreamInvalid }
        let text = value as NSString, re = try NSRegularExpression(pattern: "(?:^|,\\s*)([A-Za-z0-9_\\-]+)=([^;,]*)")
        let matches = re.matches(in: value, range: NSRange(location: 0, length: text.length))
        var result: [String: String?] = [:]
        guard matches.count <= 32 else { throw MusicServiceError.limit }
        for (i, match) in matches.enumerated() {
            let name = text.substring(with: match.range(at: 1))
            guard deviceKeys.contains(name) || credentialKeys.contains(name) || name == "MUSIC_A" else { continue }
            let raw = text.substring(with: match.range(at: 2))
            let end = i + 1 < matches.count ? matches[i + 1].range.location : text.length
            let suffix = text.substring(with: NSRange(location: NSMaxRange(match.range), length: end - NSMaxRange(match.range)))
            var remove = raw.isEmpty
            for field in suffix.split(separator: ";") {
                let pair = field.split(separator: "=", maxSplits: 1).map { $0.trimmingCharacters(in: .whitespaces) }
                if pair.count != 2 { continue }
                if pair[0].lowercased() == "max-age", let age = Int(pair[1]), age <= 0 { remove = true }
                if pair[0].lowercased() == "domain" {
                    let domain = pair[1].lowercased().trimmingCharacters(in: CharacterSet(charactersIn: "."))
                    guard domain == host || host.hasSuffix("." + domain),
                          domain == "music.163.com" || domain.hasSuffix(".music.163.com") else { throw MusicServiceError.upstreamInvalid }
                }
            }
            let value: String?
            if remove { value = nil } else { value = try cookieValue(raw.removingPercentEncoding ?? raw) }
            result.updateValue(value, forKey: name)
        }
        return result
    }
    static func response(_ response: MusicAccountHTTPResponse, qr: Bool = false, smsPath: String? = nil) throws -> [String: Any] {
        if let failure = MusicPlatformFailure.http(response.status) { throw failure }
        guard (200...299).contains(response.status), response.data.count <= 2 * 1024 * 1024 else { throw MusicServiceError.upstreamFailed }
        var data = try MusicNeteaseCrypto.gunzip(response.data)
        if (try? MusicQQWire.decode(data)) == nil {
            data = try MusicNeteaseCrypto.gunzip(MusicNeteaseCrypto.aes(data, key: MusicNeteaseCrypto.eapiKey, decrypt: true))
        }
        let root = try MusicQQWire.decode(data)
        var code = try Self.code(root["code"])
        if code == 200, let nested = root["data"] as? [String: Any], let raw = nested["code"] {
            let nestedCode = try Self.code(raw)
            if nestedCode != 0 && nestedCode != 200 { code = nestedCode }
        }
        if let smsPath {
            if [405, 429, 509].contains(code) { throw MusicServiceError.rateLimited }
            if [400, 502, 503].contains(code) {
                throw MusicSMSFailure(reason: .upstreamFailed,
                                      detail: smsPath == "/api/sms/captcha/sent" ? .invalidPhone : .invalidCode)
            }
        }
        if qr && (800...803).contains(code) { return root }
        if let failure = MusicPlatformFailure.netease(code) {
            MusicRequestTrace(.accountAPI).record(.failed, code: code, failure: failure)
            throw failure
        }
        if let error = root["error"], !(error is NSNull), try bool(error) { throw MusicServiceError.upstreamFailed }
        if let success = root["success"], !(success is NSNull), try !bool(success) { throw MusicServiceError.upstreamFailed }
        return root
    }
}

/// A single trusted operation owns these private bytes. Contexts never cross a
/// native account provider boundary or survive revoke in a provider cache.
final class MusicNeteaseContext {
    var cookies: [String: String]
    var device: [String: String]
    var keyState: MusicNeteaseKeyState?
    var accountID: String
    var qrID: UUID?
    // Only validation/refresh has an owner-held pre-request update lease.
    var permitsCredentialUpdate = false
    var clientDevice: MusicNativeClientDevice?
    init(cookies: [String: String] = [:], device: [String: String] = [:], accountID: String = "") {
        self.cookies = cookies; self.device = device; self.accountID = accountID
    }
}

actor NeteaseMusicAccountAdapter: MusicAccountProvider {
    let transport: any MusicAccountHTTPTransport
    let now: @Sendable () -> Date
    let clientContexts: NativeMusicClientService?
    var epoch = UUID()
    struct QR {
        let expires: Date
        let context: MusicNeteaseContext
        var key: String?
        var lastPoll: Date?
    }
    var qr: [UUID: QR] = [:]
    var sms: [UUID: MusicNeteaseContext] = [:]
    struct DailyCache {
        let credentialHash: Data
        let expires: Date
        let result: MusicQQRecommendation
    }
    var dailyCache: DailyCache?
    var publicAudioContext: MusicNeteaseContext?
    init(transport: any MusicAccountHTTPTransport, now: @escaping @Sendable () -> Date,
         clientContexts: NativeMusicClientService? = nil) {
        self.transport = transport; self.now = now; self.clientContexts = clientContexts
    }
    func revoke() { epoch = UUID(); qr.removeAll(); sms.removeAll(); dailyCache = nil; publicAudioContext = nil }
    func cancelQR(_ id: UUID) { qr.removeValue(forKey: id) }
    func check(_ own: UUID) throws {
        try Task.checkCancellation()
        guard own == epoch else { throw MusicServiceError.staleResult }
    }
    func currentQR(_ id: UUID) throws -> QR {
        try Task.checkCancellation()
        guard let value = qr[id], value.expires > now() else { throw MusicServiceError.staleResult }
        return value
    }
    func check(_ context: MusicNeteaseContext, epoch own: UUID) throws {
        try check(own)
        if let id = context.qrID {
            guard try currentQR(id).context === context else { throw MusicServiceError.staleResult }
        }
    }
    func ensureDevice(_ context: MusicNeteaseContext) throws {
        if let clientContexts {
            let device: MusicNativeClientDevice?
            if let current = context.clientDevice { device = try clientContexts.current(current) }
            else if context.device.isEmpty {
                guard context.accountID.isEmpty || context.permitsCredentialUpdate else { throw MusicServiceError.verificationRequired }
                device = try clientContexts.installationDevice(.netease)
            }
            else if let id = context.device["deviceId"], !id.isEmpty {
                device = try clientContexts.matchingDevice(.netease, identity: id)
            } else { throw MusicServiceError.invalidResponse }
            if let device { context.clientDevice = device; context.device = device.fields }
            return
        }
        for (key, count, alphabet) in [("deviceId", 52, "0123456789ABCDEF"), ("_ntes_nuid", 64, "0123456789abcdef"), ("WNMCID", 6, "abcdefghijklmnopqrstuvwxyz")] {
            if context.device[key] == nil {
                var value = try MusicNeteaseCrypto.text(count, alphabet: alphabet)
                if key == "WNMCID" { value += ".\(Int(now().timeIntervalSince1970 * 1000)).01.0" }
                context.device[key] = value
            }
        }
        if context.device["_ntes_nnid"] == nil { context.device["_ntes_nnid"] = context.device["_ntes_nuid"]! + ",\(Int(now().timeIntervalSince1970 * 1000))" }
    }
    func exchange(_ url: URL, form: [String: String], headers: [String: String],
                  context: MusicNeteaseContext, qr: Bool = false, smsPath: String? = nil) async throws -> [String: Any] {
        let own = epoch; try check(context, epoch: own)
        var headers = headers
        headers["Content-Type"] = "application/x-www-form-urlencoded;charset=utf-8"
        headers["Cache-Control"] = "no-store"
        let readCookies = context.cookies
        let response = try await transport.send(.init(url: url, method: "POST", headers: headers, body: MusicQQWire.form(form)))
        try check(context, epoch: own)
        let body = try MusicNeteaseWire.response(response, qr: qr, smsPath: smsPath)
        var devicePatch: [String: String] = [:], deviceRemovals = Set<String>()
        var nextCookies = context.cookies
        for (key, value) in try MusicNeteaseWire.cookies(response, host: url.host!) {
            if MusicNeteaseWire.deviceKeys.contains(key) {
                if key == "deviceId", context.qrID == nil, !context.accountID.isEmpty,
                   !context.permitsCredentialUpdate, context.clientDevice == nil,
                   context.device[key] != value {
                    throw MusicServiceError.verificationRequired
                }
                if let value { devicePatch[key] = value } else { deviceRemovals.insert(key) }
            }
            else {
                if context.qrID == nil, !context.accountID.isEmpty, MusicNeteaseWire.credentialKeys.contains(key),
                   readCookies[key] != value {
                    if key == "MUSIC_U", value == nil { throw MusicServiceError.authRequired }
                    guard context.permitsCredentialUpdate else { throw MusicServiceError.verificationRequired }
                }
                nextCookies[key] = value
            }
        }
        if let id = response.header("x-encr-ssid"), let key = response.header("x-encr-sskey") {
            guard !id.isEmpty, id.utf8.count <= 512,
                  [16, 24, 32].contains(key.utf8.count) else { throw MusicServiceError.upstreamInvalid }
        }
        if let client = context.clientDevice, let clientContexts {
            let saved = devicePatch.isEmpty && deviceRemovals.isEmpty ? try clientContexts.current(client)
                : try clientContexts.update(client, fields: devicePatch, removing: deviceRemovals)
            context.clientDevice = saved; context.device = saved.fields
        } else {
            for key in deviceRemovals { context.device.removeValue(forKey: key) }
            for (key, value) in devicePatch { context.device[key] = value }
        }
        context.cookies = nextCookies
        if let id = response.header("x-encr-ssid"), let key = response.header("x-encr-sskey") {
            context.keyState?.sessionID = id; context.keyState?.sessionKey = Data(key.utf8)
        }
        return body
    }
    func handshake(_ context: MusicNeteaseContext) async throws -> MusicNeteaseKeyState {
        if let state = context.keyState { return state }
        let nonce = try MusicNeteaseCrypto.text(16, alphabet: "0123456789")
        let timestamp = String(Int(now().timeIntervalSince1970 * 1000))
        let form = ["appVersion": "9.5.61", "currentKeyVersion": "", "deviceId": context.device["deviceId"]!,
            "nonce": nonce, "os": "android", "requestType": "active", "signature": MusicNeteaseCrypto.sign(timestamp: timestamp, nonce: nonce),
            "t1": "", "t2": "", "timestamp": timestamp, "uid": ""]
        let root = try await exchange(URL(string: "https://interface.music.163.com/api/bsr/sk/get")!, form: form,
            headers: ["User-Agent": Self.mobileUA, "Cookie": MusicNeteaseWire.cookieHeader(["deviceId": context.device["deviceId"]!])], context: context)
        let data = try MusicQQWire.object(root["data"])
        // The official handshake returns milliseconds as a JSON number; the
        // reference also accepts its decimal-string form. Canonicalize only a
        // positive, exactly representable integer before checking the HMAC.
        let time = try MusicQQWire.id(data["timestamp"])
        guard let signature = Data(base64Encoded: try MusicQQWire.string(data["signature"])),
              let expected = Data(base64Encoded: MusicNeteaseCrypto.sign(timestamp: time, nonce: nonce)), signature.count == expected.count,
              zip(signature, expected).reduce(UInt8(0), { $0 | ($1.0 ^ $1.1) }) == 0,
              let encrypted = Data(base64Encoded: try MusicQQWire.string(data["encryptedData"], maximum: 32_768)) else { throw MusicServiceError.upstreamInvalid }
        let decoded = try MusicQQWire.decode(MusicNeteaseCrypto.aes(encrypted, key: MusicNeteaseCrypto.staticKey, decrypt: true))
        guard let key = Data(base64Encoded: try MusicQQWire.string(decoded["publicKey"], maximum: 128)), key.count == 32 else { throw MusicServiceError.upstreamInvalid }
        let state = MusicNeteaseKeyState(publicKey: key, sk: try MusicQQWire.string(decoded["sk"], maximum: 4096),
                                        version: try MusicQQWire.string(decoded["version"], maximum: 128))
        context.keyState = state; return state
    }
    static let mobileUA = "NeteaseMusic/9.5.61.260802021928(9005061);Dalvik/2.1.0 (Linux; U; Android 12; HBN-AL00 Build/cd737a2.0)"
    func request(_ path: String, data: [String: Any] = [:], crypto: String = "eapi",
                 context: MusicNeteaseContext, qr: Bool = false) async throws -> [String: Any] {
        let own = epoch; try check(context, epoch: own); try ensureDevice(context)
        guard path.hasPrefix("/api/"), ["weapi", "eapi", "xeapi"].contains(crypto) else { throw MusicServiceError.invalidRequest }
        if crypto != "eapi", context.device["NMTID"] == nil,
           context.clientDevice != nil || context.accountID.isEmpty || context.permitsCredentialUpdate {
            context.device["NMTID"] = "00O" + (try MusicQQWire.randomHex(19))
            if let client = context.clientDevice, let clientContexts {
                let saved = try clientContexts.update(client, fields: ["NMTID": context.device["NMTID"]!])
                context.clientDevice = saved; context.device = saved.fields
            }
        }
        let xeapiState: MusicNeteaseKeyState?
        if crypto == "xeapi" {
            xeapiState = try await handshake(context)
            try check(context, epoch: own)
        } else { xeapiState = nil }
        var cookies = ["__remember_me": "true", "ntes_kaola_ad": "1", "WEVNSM": "1.0.0", "os": "pc",
            "appver": "3.1.17.204416", "osver": "Microsoft-Windows-10-Professional-build-19045-64bit", "channel": "netease"]
        cookies.merge(context.cookies) { _, value in value }; cookies.merge(context.device) { _, value in value }
        var body = data; body["e_r"] = false
        var headers: [String: String] = [:], form: [String: String], domain: String
        if crypto == "weapi" {
            domain = "music.163.com"; body["csrf_token"] = cookies["__csrf"] ?? ""
            headers = ["Referer": "https://music.163.com/", "User-Agent": "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36"]
            form = try MusicNeteaseCrypto.weapi(MusicQQWire.json(body), secret: MusicNeteaseCrypto.text(16))
        } else if crypto == "eapi" {
            domain = "interfacepc.music.163.com"
            var header = cookies.filter { ["osver", "deviceId", "os", "appver", "channel", "MUSIC_U", "MUSIC_A", "NMTID"].contains($0.key) }
            header.merge(["versioncode": "140", "mobilename": "", "buildver": String(Int(now().timeIntervalSince1970)),
                "resolution": "1920x1080", "__csrf": cookies["__csrf"] ?? "",
                "requestId": "\(Int(now().timeIntervalSince1970 * 1000))_0" + (try MusicNeteaseCrypto.text(3, alphabet: "0123456789"))]) { _, value in value }
            body["header"] = header; cookies = header
            headers["User-Agent"] = "NeteaseMusic 9.0.90/5038 (iPhone; iOS 16.2; zh_CN)"
            form = try MusicNeteaseCrypto.eapi(path, plain: MusicQQWire.json(body))
        } else {
            domain = "interface3.music.163.com"
            guard let state = xeapiState else { throw MusicServiceError.invalidRequest }
            cookies["os"] = "android"; cookies["osver"] = "16"; cookies["appver"] = data["level"] as? String == "vivid" ? "9.5.61" : "9.1.65"
            cookies["buildver"] = String(Int(now().timeIntervalSince1970)); cookies["sDeviceId"] = cookies["deviceId"]
            headers = ["User-Agent": Self.mobileUA, "X-Client-Enc-State": "ENCRYPTED", "X-Aeapi": "true"]
            for name in ["deviceId", "os", "osver", "appver", "sDeviceId", "buildver"] { headers["x-" + name.lowercased()] = cookies[name] }
            headers["x-music-u"] = cookies["MUSIC_U"]
            form = try MusicNeteaseCrypto.xeapi(path, data: body, state: state)
        }
        headers["Cookie"] = MusicNeteaseWire.cookieHeader(cookies)
        return try await exchange(URL(string: "https://" + domain + "/" + crypto + "/" + path.dropFirst(5))!,
                                  form: form, headers: headers, context: context, qr: qr,
                                  smsPath: ["/api/sms/captcha/sent", "/api/w/login/cellphone"].contains(path) ? path : nil)
    }
}
