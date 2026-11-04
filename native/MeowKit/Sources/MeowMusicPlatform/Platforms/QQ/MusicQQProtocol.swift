// SPDX-License-Identifier: GPL-3.0-only
// Protocol port: meowmusic cc22a7e platform/qq/{account,client,device}.go.
import Foundation
import CryptoKit
import CommonCrypto
import Security
import MeowAccounts
import MeowMusicFeature

enum MusicQQWire {
    static func object(_ value: Any?) throws -> [String: Any] {
        guard let value = value as? [String: Any] else { throw MusicServiceError.upstreamInvalid }; return value
    }
    static func string(_ value: Any?, maximum: Int = 1024, empty: Bool = false) throws -> String {
        guard let value = value as? String, (empty || !value.isEmpty), value.utf8.count <= maximum,
              !value.unicodeScalars.contains(where: { $0.value < 32 || $0.value == 127 }) else {
            throw MusicServiceError.upstreamInvalid
        }; return value
    }
    static func integer(_ value: Any?, maximum: Int = 9_007_199_254_740_991, minimum: Int = 0) throws -> Int {
        guard let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID(),
              number.doubleValue.isFinite, number.doubleValue.rounded() == number.doubleValue,
              number.doubleValue >= Double(minimum), number.doubleValue <= Double(maximum) else {
            throw MusicServiceError.upstreamInvalid
        }; return number.intValue
    }
    static func id(_ value: Any?, mid: Bool = false) throws -> String {
        let value = try (value as? String) ?? String(integer(value, minimum: 1))
        let pattern = mid ? "^[a-zA-Z0-9]{1,64}$" : "^[1-9][0-9]{0,15}$"
        guard value.range(of: pattern, options: .regularExpression) != nil,
              mid || (UInt64(value) ?? UInt64.max) <= 9_007_199_254_740_991 else { throw MusicServiceError.upstreamInvalid }
        return value
    }
    /// Public song IDs can exceed IEEE-754 precision when carried as strings.
    /// Account IDs used in integer-only protocol fields keep the stricter id().
    static func songID(_ value: Any?) throws -> String {
        if let text = value as? String {
            guard text.range(of: "^[1-9][0-9]{0,63}$", options: .regularExpression) != nil else { throw MusicServiceError.upstreamInvalid }
            return text
        }
        return try String(integer(value, minimum: 1))
    }
    static func json(_ value: [String: Any]) throws -> Data {
        guard JSONSerialization.isValidJSONObject(value) else { throw MusicServiceError.invalidRequest }
        let bytes = try JSONSerialization.data(withJSONObject: value, options: [.sortedKeys, .withoutEscapingSlashes])
        guard bytes.count <= 256 * 1024 else { throw MusicServiceError.limit }; return bytes
    }
    static func decode(_ bytes: Data) throws -> [String: Any] {
        guard bytes.count <= 2 * 1024 * 1024 else { throw MusicServiceError.limit }
        let result: Any
        do { result = try JSONSerialization.jsonObject(with: bytes) } catch { throw MusicServiceError.upstreamInvalid }
        var nodes = 0
        func check(_ item: Any, _ depth: Int) throws {
            nodes += 1; guard depth <= 24, nodes <= 100_000 else { throw MusicServiceError.limit }
            if let values = item as? [String: Any] { for (key, child) in values {
                guard key.utf8.count <= 512 else { throw MusicServiceError.limit }; try check(child, depth + 1)
            } } else if let values = item as? [Any] { for child in values { try check(child, depth + 1) } }
        }
        try check(result, 0); return try object(result)
    }
    static func codes(_ object: [String: Any], requireCode: Bool = false) throws {
        if requireCode && object["code"] == nil { throw MusicServiceError.upstreamInvalid }
        for key in ["code", "subcode", "subCode", "retCode", "retcode", "Retcode"] {
            guard let value = object[key] else { continue }
            let code = try integer(value, maximum: Int(Int32.max), minimum: Int(Int32.min))
            if [1000, 1001, 104400, 104401].contains(code) { throw MusicServiceError.authRequired }
            if [20277, 20278, 20279, 20450].contains(code) { throw MusicServiceError.verificationRequired }
            if [104604, 2001, 20001, 429].contains(code) { throw MusicServiceError.rateLimited }
            guard code == 0 else { throw MusicServiceError.upstreamFailed }
        }
    }
    static func query(_ base: String, _ fields: [String: String]) throws -> URL {
        guard var value = URLComponents(string: base) else { throw MusicServiceError.invalidRequest }
        value.queryItems = fields.sorted { $0.key < $1.key }.map { URLQueryItem(name: $0.key, value: $0.value) }
        guard let url = value.url else { throw MusicServiceError.invalidRequest }; return url
    }
    static func form(_ fields: [String: String]) -> Data {
        let allowed = CharacterSet(charactersIn: "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~")
        return Data(fields.sorted { $0.key < $1.key }.map {
            ($0.key.addingPercentEncoding(withAllowedCharacters: allowed) ?? "") + "=" +
            ($0.value.addingPercentEncoding(withAllowedCharacters: allowed) ?? "")
        }.joined(separator: "&").utf8)
    }
    static func hash33(_ text: String, seed: UInt32) -> UInt32 {
        var value = seed
        for byte in text.utf8 { value = value &* 33 &+ UInt32(byte) }
        return value & 0x7fff_ffff
    }
    static func md5(_ text: String) -> String {
        Insecure.MD5.hash(data: Data(text.utf8)).map { String(format: "%02x", $0) }.joined()
    }
    static func randomHex(_ bytes: Int) throws -> String {
        var data = [UInt8](repeating: 0, count: bytes)
        guard SecRandomCopyBytes(kSecRandomDefault, bytes, &data) == errSecSuccess else { throw MusicServiceError.unavailable }
        return data.map { String(format: "%02x", $0) }.joined()
    }
    static func aes(_ text: String, key: String, iv: String) throws -> String {
        let source = Array(text.utf8), key = Array(key.utf8), iv = Array(iv.utf8)
        guard key.count == 16, iv.count == 16 else { throw MusicServiceError.invalidRequest }
        var out = [UInt8](repeating: 0, count: source.count + kCCBlockSizeAES128), written = 0
        let capacity = out.count
        let status = CCCrypt(CCOperation(kCCEncrypt), CCAlgorithm(kCCAlgorithmAES), CCOptions(kCCOptionPKCS7Padding),
                             key, key.count, iv, source, source.count, &out, capacity, &written)
        guard status == kCCSuccess else { throw MusicServiceError.unavailable }
        return Data(out.prefix(written)).base64EncodedString()
    }
    static func rsaQimei(_ text: String) throws -> String {
        // Public protocol key, not an account secret. Strip the SPKI wrapper for
        // Security's RSA PKCS#1 public-key representation; never use raw RSA here.
        let encoded = "MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDEIxgwoutfwoJxcGQeedgP7FG9qaIuS0qzfR8gWkrkTZKM2iWHn2ajQpBRZjMSoSf6+KJGvar2ORhBfpDXyVtZCKpqLQ+FLkpncClKVIrBwv6PHyUvuCb0rIarmgDnzkfQAqVufEtR64iazGDKatvJ9y6B9NMbHddGSAUmRTCrHQIDAQAB"
        guard let spki = Data(base64Encoded: encoded), spki.count == 162 else { throw MusicServiceError.unavailable }
        let attributes: [CFString: Any] = [kSecAttrKeyType: kSecAttrKeyTypeRSA, kSecAttrKeyClass: kSecAttrKeyClassPublic,
                                         kSecAttrKeySizeInBits: 1024]
        var error: Unmanaged<CFError>?
        guard let key = SecKeyCreateWithData(Data(spki.dropFirst(22)) as CFData, attributes as CFDictionary, &error),
              let encrypted = SecKeyCreateEncryptedData(key, .rsaEncryptionPKCS1, Data(text.utf8) as CFData, &error) else {
            throw MusicServiceError.unavailable
        }; return (encrypted as Data).base64EncodedString()
    }
    static func cookies(_ response: MusicAccountHTTPResponse) throws -> [String: String] {
        guard let header = response.header("set-cookie") else { return [:] }
        guard header.utf8.count <= 32_768 else { throw MusicServiceError.limit }
        // A comma in Expires is not followed by a cookie-name assignment.
        // Foundation can combine header lines; select the last same-name cookie.
        let regex = try NSRegularExpression(pattern: "(?:^|,\\s*)([A-Za-z0-9_\\-]+)=([^;,]*)")
        let source = header as NSString; var values: [String: String] = [:]
        for match in regex.matches(in: header, range: NSRange(location: 0, length: source.length)) {
            let name = source.substring(with: match.range(at: 1)), value = source.substring(with: match.range(at: 2))
            guard value.utf8.count <= 8192, value.utf8.allSatisfy({ $0 >= 33 && $0 <= 126 && $0 != 34 && $0 != 92 }) else {
                throw MusicServiceError.upstreamInvalid
            }
            values[name] = value
        }
        guard values.count <= 32 else { throw MusicServiceError.limit }; return values
    }
    static func callback(_ data: Data) throws -> [String] {
        guard data.count <= 16_384, let text = String(data: data, encoding: .utf8) else { throw MusicServiceError.upstreamInvalid }
        let input = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard input.hasPrefix("ptuiCB("), input.hasSuffix(");") || input.hasSuffix(")") else { throw MusicServiceError.upstreamInvalid }
        let body = String(input.dropFirst(7).dropLast(input.hasSuffix(");") ? 2 : 1)), ns = body as NSString
        let regex = try NSRegularExpression(pattern: "'((?:\\\\.|[^'])*)'")
        let matches = regex.matches(in: body, range: NSRange(location: 0, length: ns.length))
        guard (3...16).contains(matches.count) else { throw MusicServiceError.upstreamInvalid }
        var previous = 0, output: [String] = []
        for (index, match) in matches.enumerated() {
            let gap = ns.substring(with: NSRange(location: previous, length: match.range.location - previous))
                .trimmingCharacters(in: .whitespacesAndNewlines)
            guard gap == (index == 0 ? "" : ",") else { throw MusicServiceError.upstreamInvalid }
            output.append(ns.substring(with: match.range(at: 1)).replacingOccurrences(of: "\\/", with: "/"))
            previous = NSMaxRange(match.range)
        }
        guard ns.substring(from: previous).trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw MusicServiceError.upstreamInvalid
        }; return output
    }
}

actor QQMusicAccountAdapter {
    let transport: any MusicAccountHTTPTransport
    let now: @Sendable () -> Date
    /// Only the separate anonymous instance receives this dependency.
    let publicPersistence: MusicQQPublicPersistenceState?
    let clientDeviceProvider: (any MusicQQClientDeviceProviding)?
    var publicDeviceEpoch: UUID?
    var device: [String: Any] = [:]
    struct DeviceValue: @unchecked Sendable { let fields: [String: Any] }
    let deviceRequests = MusicAccountSingleFlight<UUID, DeviceValue>()
    var deviceGeneration = UUID()
    struct QR { let expires: Date; var signature: String }
    var qr: [UUID: QR] = [:]
    init(transport: any MusicAccountHTTPTransport, now: @escaping @Sendable () -> Date,
         publicPersistence: MusicQQPublicPersistenceState? = nil,
         clientDeviceProvider: (any MusicQQClientDeviceProviding)? = nil) {
        self.transport = transport; self.now = now; self.publicPersistence = publicPersistence
        self.clientDeviceProvider = clientDeviceProvider
    }
    func revoke() {
        qr.removeAll(); deviceRequests.cancel()
        deviceGeneration = UUID(); device = [:]; publicDeviceEpoch = nil
    }
    func setPublicAccess(available: Bool, generation: UInt64) throws {
        guard let publicPersistence else { throw MusicServiceError.invalidRequest }
        let token = try publicPersistence.setAccess(available: available, generation: generation)
        if publicDeviceEpoch != token { revoke() }
    }
    func checkPublicAccess(_ token: UUID?) throws {
        if let publicPersistence {
            guard let token else { throw MusicServiceError.accessUnavailable }
            try publicPersistence.check(token)
        } else if token != nil { throw MusicServiceError.invalidRequest }
    }
    func cancelQR(_ id: UUID) { qr.removeValue(forKey: id) }
    func checkQRCurrent(_ id: UUID) throws -> QR {
        try Task.checkCancellation()
        guard let result = qr[id], result.expires > now() else { throw MusicServiceError.staleResult }; return result
    }
    func request(_ url: URL, method: String = "GET", headers: [String: String] = [:],
                 cookies: [String: String] = [:], body: Data? = nil, redirects: Bool = false,
                 maximumBytes: Int = 2 * 1024 * 1024,
                 publicAccess: UUID? = nil) async throws -> MusicAccountHTTPResponse {
        try Task.checkCancellation()
        try checkPublicAccess(publicAccess)
        if publicPersistence != nil,
           !cookies.isEmpty || headers.keys.contains(where: {
               ["cookie", "authorization", "proxy-authorization"].contains($0.lowercased())
           }) { throw MusicServiceError.invalidRequest }
        var fields = headers
        fields["User-Agent"] = fields["User-Agent"] ?? "QQMusic 20090008(android 15)"
        fields["Referer"] = fields["Referer"] ?? "https://y.qq.com/"
        if !cookies.isEmpty { fields["Cookie"] = cookies.sorted { $0.key < $1.key }.map { $0.key + "=" + $0.value }.joined(separator: "; ") }
        let input = MusicAccountHTTPRequest(url: url, method: method, headers: fields, body: body, maximumBytes: maximumBytes)
        let result: MusicAccountHTTPResponse
        if let publicPersistence, let publicAccess {
            // A registered native request is physically cancelled by revoke,
            // including when the adapter actor is suspended in its transport.
            let transport = self.transport, id = UUID()
            let task = Task {
                try publicPersistence.check(publicAccess)
                return try await transport.send(input)
            }
            do { try publicPersistence.register(id, token: publicAccess) { task.cancel() } }
            catch { task.cancel(); throw error }
            defer { publicPersistence.remove(id) }
            result = try await withTaskCancellationHandler(operation: {
                try await task.value
            }, onCancel: { task.cancel() })
        } else {
            result = try await transport.send(input)
        }
        try Task.checkCancellation()
        try checkPublicAccess(publicAccess)
        guard result.data.count <= maximumBytes else { throw MusicServiceError.limit }
        if result.status == 429 { throw MusicServiceError.rateLimited }
        if result.status == 401 || result.status == 403 { throw MusicServiceError.authRequired }
        guard (200...299).contains(result.status) || redirects && (300...399).contains(result.status) else {
            throw MusicServiceError.upstreamFailed
        }; return result
    }
    func comm(_ device: [String: Any], auth: [String: Any] = [:], session: Bool = true) -> [String: Any] {
        let guid = device["open_udid"] as? String ?? ""
        let uid = auth["str_musicid"] as? String ?? guid
        var value: [String: Any] = [
            "ct": "11", "cv": "20090008", "v": "20090008", "chid": "10003505", "tmeAppID": "qqmusic",
            "traceid": "10002_\(uid)_\(Int(now().timeIntervalSince1970))",
            "QIMEI36": (device["qimei"] as? [String: Any])?["q36"] ?? "",
            "OpenUDID": guid, "udid": guid, "OpenUDID2": device["open_udid2"] ?? "",
            "aid": device["android_id"] ?? "", "os_ver": device["os_ver"] ?? "", "phonetype": device["model"] ?? ""
        ]
        if !auth.isEmpty {
            value["qq"] = uid; value["authst"] = auth["musickey"]; value["tmeLoginType"] = String((auth["login_type"] as? Int) ?? 2)
        }
        if session {
            let state = device["session"] as? [String: Any] ?? [:]
            value["uid"] = state["uid"] ?? ""; value["sid"] = state["sid"] ?? ""
        }
        return value
    }
    func rpc(_ module: String, _ method: String, _ params: [String: Any] = [:],
             auth: [String: Any] = [:], publicAccess: UUID? = nil) async throws -> [String: Any] {
        try checkPublicAccess(publicAccess)
        if publicPersistence != nil, !auth.isEmpty { throw MusicServiceError.invalidRequest }
        let device = try await ensureDevice(publicAccess: publicAccess)
        try checkPublicAccess(publicAccess)
        return try await rpcWith(module, method, params, comm: comm(device, auth: auth), auth: auth, publicAccess: publicAccess)
    }
    func rpcWith(_ module: String, _ method: String, _ params: [String: Any],
                 comm: [String: Any], auth: [String: Any] = [:],
                 publicAccess: UUID? = nil) async throws -> [String: Any] {
        try checkPublicAccess(publicAccess)
        if publicPersistence != nil,
           !auth.isEmpty || comm["qq"] != nil || comm["authst"] != nil || comm["tmeLoginType"] != nil {
            throw MusicServiceError.invalidRequest
        }
        var cookies: [String: String] = [:]
        if let uid = auth["str_musicid"] as? String, let key = auth["musickey"] as? String {
            cookies = ["uin": uid, "qqmusic_uin": uid, "qm_keyst": key, "qqmusic_key": key]
        }
        let response = try await request(URL(string: "https://u.y.qq.com/cgi-bin/musicu.fcg")!, method: "POST",
                                        headers: ["Content-Type": "application/json"], cookies: cookies,
                                        body: MusicQQWire.json(["comm": comm, "req_0": ["module": module, "method": method, "param": params]]),
                                        publicAccess: publicAccess)
        let root = try MusicQQWire.decode(response.data); try MusicQQWire.codes(root, requireCode: true)
        let entry = try MusicQQWire.object(root["req_0"]); try MusicQQWire.codes(entry, requireCode: true)
        let result = try MusicQQWire.object(entry["data"]); try MusicQQWire.codes(result); return result
    }
}
