import Foundation
import CommonCrypto
import CryptoKit
import MeowAccounts
import MeowMusicFeature
@testable import MeowMusicPlatform

// Synthetic HTTP peer, no platform connection, user credential or reference
// execution. Decrypts actual EAPI/XEAPI bytes and opt-in WEAPI payloads rather than mocking the provider.
enum MusicNeteaseFixture {
    static let uid = "42"
    static func credential(_ uid: String = "42") throws -> AccountCredential {
        try .init(data: MusicQQWire.json(["version": 1, "platform": "wy", "accountID": uid,
            "cookies": ["MUSIC_U": "synthetic-user-\(uid)", "__csrf": "synthetic-csrf"],
            "device": ["deviceId": "synthetic-device", "_ntes_nuid": "synthetic-nuid",
                       "_ntes_nnid": "synthetic-nuid,1800000000000", "WNMCID": "synthetic-wnmcid",
                       "NMTID": "synthetic-nmtid"]]))
    }
    static func response(_ body: [String: Any], headers: [String: String] = [:]) throws -> MusicAccountHTTPResponse {
        .init(status: 200, data: try MusicQQWire.json(body), headers: headers)
    }
    static func song(_ id: Int = 101) -> [String: Any] {
        ["id": id, "name": "合成歌曲\(id)", "ar": [["name": "合成歌手"]],
         "al": ["id": 301, "name": "合成专辑"], "dt": 180000, "h": ["size": 300], "sq": ["size": 400]]
    }
    static func collection(_ id: Int = 501, owner: Int = 42, special: Int = 5) -> [String: Any] {
        ["id": id, "name": "合成歌单\(id)", "creator": ["userId": owner], "specialType": special, "trackCount": 2]
    }
    static func detail(_ ids: [Int] = [101, 102], includeTracks: Bool = true) -> [String: Any] {
        var row = collection()
        row["trackCount"] = ids.count; row["trackIds"] = ids.map { ["id": $0] }
        row["tracks"] = includeTracks ? ids.map(song) : []
        return ["code": 200, "playlist": row]
    }
    static func audio(level: String = "lossless", id: Int = 101, trial: Any? = nil,
                      url: String = "https://m801.music.126.net/synthetic.flac") -> [String: Any] {
        var row: [String: Any] = ["id": id, "code": 200, "url": url, "level": level, "expi": 3600,
                                  "freeTrialInfo": NSNull(),
                                  "freeTrialPrivilege": ["resConsumable": false, "userConsumable": false]]
        if let trial { row["freeTrialPrivilege"] = trial }
        return ["code": 200, "data": [row]]
    }
    static func form(_ data: Data?) throws -> [String: String] {
        guard let data, let text = String(data: data, encoding: .utf8) else { throw MusicServiceError.invalidRequest }
        var result: [String: String] = [:]
        for entry in text.split(separator: "&") {
            let fields = entry.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false)
            guard fields.count == 2,
                  let name = String(fields[0]).replacingOccurrences(of: "+", with: " ").removingPercentEncoding,
                  let value = String(fields[1]).replacingOccurrences(of: "+", with: " ").removingPercentEncoding,
                  result[name] == nil else { throw MusicServiceError.invalidRequest }
            result[name] = value
        }
        return result
    }
    static func base64(_ value: String) throws -> Data {
        var text = value.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        text += String(repeating: "=", count: (4 - text.count % 4) % 4)
        guard let result = Data(base64Encoded: text) else { throw MusicServiceError.upstreamInvalid }; return result
    }
    static func hex(_ value: String) throws -> Data {
        guard value.count % 2 == 0 else { throw MusicServiceError.upstreamInvalid }
        var bytes: [UInt8] = [], index = value.startIndex
        while index < value.endIndex {
            let end = value.index(index, offsetBy: 2)
            guard let b = UInt8(value[index..<end], radix: 16) else { throw MusicServiceError.upstreamInvalid }
            bytes.append(b); index = end
        }
        return Data(bytes)
    }
    // Independent fixture implementation (not production aes()).
    static func aes(_ data: Data, key: Data, iv: Data? = nil, encrypt: Bool = false) throws -> Data {
        let input = Array(data), secret = Array(key)
        var output = [UInt8](repeating: 0, count: data.count + 16), written = 0
        let capacity = output.count, flags = CCOptions(kCCOptionPKCS7Padding | (iv == nil ? kCCOptionECBMode : 0))
        let status: CCCryptorStatus
        if let iv {
            status = CCCrypt(CCOperation(encrypt ? kCCEncrypt : kCCDecrypt), CCAlgorithm(kCCAlgorithmAES),
                             flags, secret, secret.count, Array(iv), input, input.count, &output, capacity, &written)
        } else {
            status = CCCrypt(CCOperation(encrypt ? kCCEncrypt : kCCDecrypt), CCAlgorithm(kCCAlgorithmAES),
                             flags, secret, secret.count, nil, input, input.count, &output, capacity, &written)
        }
        guard status == CCCryptorStatus(kCCSuccess) else { throw MusicServiceError.upstreamInvalid }
        return Data(output.prefix(written))
    }
    static func sign(_ timestamp: String, _ nonce: String) -> String {
        let key = SymmetricKey(data: Data("mUHCwVNWJbunMqAHf5MImuirT6plvs6VSFW62MGHstFQxhBGdEoIhLItH3djc4+FB/OKty3+lL2rGeoFBpVe5g==".utf8))
        return Data(HMAC<SHA256>.authenticationCode(for: Data((timestamp + nonce).utf8), using: key)).base64EncodedString()
    }
    static let staticKey = Data([0xab,0x1d,0x5a,0x43,0x0f,0x6b,0xb0,0x4a,0x3f,0x01,0xe8,0x1d,0xdd,0x72,0xbd,0x91,
                                0x6d,0x5c,0xe5,0x91,0x24,0x8a,0xc1,0x28,0x71,0x48,0x06,0xd7,0xf8,0xfb,0x1b,0x84])
    static func xeapiDynamicKey(_ form: [String: String], peer: Curve25519.KeyAgreement.PrivateKey) throws -> Data {
        let s = try base64(form["S"] ?? "")
        guard s.count > 60 else { throw MusicServiceError.upstreamInvalid }
        let ephemeral = Data(s.prefix(32))
        let shared = try peer.sharedSecretFromKeyAgreement(with: Curve25519.KeyAgreement.PublicKey(rawRepresentation: ephemeral))
        let raw = shared.withUnsafeBytes { Data($0) }
        let extract = Data(HMAC<SHA256>.authenticationCode(for: raw, using: SymmetricKey(data: Data(repeating: 0, count: 32))))
        let expanded = Data(HMAC<SHA256>.authenticationCode(for: ephemeral + Data([1]), using: SymmetricKey(data: extract)))
        let box = try AES.GCM.SealedBox(nonce: AES.GCM.Nonce(data: Data(s[32..<44])),
                                       ciphertext: Data(s[44..<(s.count - 16)]), tag: Data(s.suffix(16)))
        let plain = try AES.GCM.open(box, using: SymmetricKey(data: Data(expanded.prefix(16))))
        let parts = String(decoding: plain, as: UTF8.self).split(separator: "|", omittingEmptySubsequences: false)
        guard parts.count == 3, parts[1] == "android", parts[2] == "synthetic-sk" else { throw MusicServiceError.upstreamInvalid }
        return try base64(String(parts[0]))
    }
    static func xeapi(_ form: [String: String], peer: Curve25519.KeyAgreement.PrivateKey) throws -> [String: String] {
        let middle = try aes(base64(form["C"] ?? ""), key: xeapiDynamicKey(form, peer: peer))
        guard middle.count > 16 else { throw MusicServiceError.upstreamInvalid }
        let mask = Array(middle.prefix(16)), rotated = Array(middle.dropFirst(16))
        let rotation = Int(mask[0] & 15) % rotated.count
        let unrotated = Array(rotated.suffix(rotation)) + Array(rotated.prefix(rotated.count - rotation))
        let encrypted = Data(unrotated.enumerated().map { $0.element ^ mask[$0.offset & 15] })
        let content = try MusicQQWire.decode(aes(encrypted, key: staticKey))
        guard content["queryString"] as? String == "e_r=true" else { throw MusicServiceError.upstreamInvalid }
        let r = try aes(base64(form["R"] ?? ""), key: staticKey)
        guard String(decoding: r, as: UTF8.self).hasPrefix("1|") else { throw MusicServiceError.upstreamInvalid }
        return try self.form(Data((content["content"] as? String ?? "").utf8))
    }
}

actor MusicNeteaseTestHTTP: MusicAccountHTTPTransport {
    struct Record: Sendable { let request: MusicAccountHTTPRequest; let payload: Data? }
    let clock: MusicAccountTestClock
    let peer = try! Curve25519.KeyAgreement.PrivateKey(rawRepresentation: Data(repeating: 7, count: 32))
    private let weapiSecret: String?
    private var records: [Record] = []
    private var overrides: [String: [MusicAccountHTTPResponse]] = [:]
    private var block: (String, MusicAccountTestGate)?
    private var numericHandshakeTimestamp = true
    init(clock: MusicAccountTestClock, weapiSecret: String? = nil) {
        self.clock = clock; self.weapiSecret = weapiSecret
    }
    func useNumericHandshakeTimestamp(_ value: Bool) { numericHandshakeTimestamp = value }
    func replies(_ path: String, _ responses: [MusicAccountHTTPResponse]) { overrides[path] = responses }
    func hold(_ path: String, _ gate: MusicAccountTestGate) { block = (path, gate) }
    func recorded() -> [Record] { records }
    func send(_ request: MusicAccountHTTPRequest) async throws -> MusicAccountHTTPResponse {
        let path = request.url.path, form = try MusicNeteaseFixture.form(request.body)
        guard request.method == "POST", request.headers["Cache-Control"] == "no-store" else { throw MusicServiceError.invalidRequest }
        var payload: [String: Any] = [:]
        if path.hasPrefix("/eapi/") {
            let plain = try MusicNeteaseFixture.aes(MusicNeteaseFixture.hex(form["params"] ?? ""), key: Data("e82ckenh8dichen8".utf8))
            let fields = String(decoding: plain, as: UTF8.self).components(separatedBy: "-36cd479b6b5-")
            guard fields.count == 3, fields[0] == "/api/" + path.dropFirst(6),
                  fields[2] == MusicQQWire.md5("nobody" + fields[0] + "use" + fields[1] + "md5forencrypt") else {
                throw MusicServiceError.invalidRequest
            }
            payload = try MusicQQWire.decode(Data(fields[1].utf8))
        } else if path.hasPrefix("/xeapi/") {
            payload = try MusicNeteaseFixture.xeapi(form, peer: peer)
        } else if path.hasPrefix("/weapi/") {
            guard form["encSecKey"]?.count == 256, let params = form["params"], !params.isEmpty else { throw MusicServiceError.invalidRequest }
            if let weapiSecret {
                let iv = Data("0102030405060708".utf8)
                let middle = try MusicNeteaseFixture.aes(MusicNeteaseFixture.base64(params), key: Data(weapiSecret.utf8), iv: iv)
                let plain = try MusicNeteaseFixture.aes(
                    MusicNeteaseFixture.base64(String(decoding: middle, as: UTF8.self)),
                    key: Data("0CoJUm6Qyw8W8jud".utf8), iv: iv)
                payload = try MusicQQWire.decode(plain)
            }
        } else { payload = form }
        records.append(.init(request: request, payload: try MusicQQWire.json(payload)))
        if let block, block.0 == path { await block.1.wait() }
        if var values = overrides[path], !values.isEmpty {
            let response = values.count == 1 ? values[0] : values.removeFirst()
            overrides[path] = values; return response
        }
        switch path {
        case "/api/bsr/sk/get":
            guard let nonce = form["nonce"], let timestamp = form["timestamp"],
                  form["signature"] == MusicNeteaseFixture.sign(timestamp, nonce) else { throw MusicServiceError.invalidRequest }
            let data = try MusicQQWire.json(["publicKey": peer.publicKey.rawRepresentation.base64EncodedString(), "sk": "synthetic-sk", "version": "1"])
            let replyTime: Any
            if numericHandshakeTimestamp { replyTime = Int64(timestamp)! }
            else { replyTime = timestamp }
            return try MusicNeteaseFixture.response(["code": 200, "data": ["timestamp": replyTime,
                "signature": MusicNeteaseFixture.sign(timestamp, nonce),
                "encryptedData": MusicNeteaseFixture.aes(data, key: MusicNeteaseFixture.staticKey, encrypt: true).base64EncodedString()]])
        case "/xeapi/register/anonimous":
            return try MusicNeteaseFixture.response(["code": 200], headers: ["set-cookie": "MUSIC_A=synthetic-visitor; Path=/; HttpOnly"])
        case "/eapi/login/qrcode/unikey":
            return try MusicNeteaseFixture.response(["code": 200, "unikey": "synthetic-qr-key"])
        case "/eapi/login/qrcode/client/login":
            return try MusicNeteaseFixture.response(["code": 803],
                headers: ["set-cookie": "MUSIC_U=synthetic-user-42; Path=/; HttpOnly, __csrf=synthetic-csrf; Path=/"])
        case "/weapi/w/nuser/account/get":
            return try MusicNeteaseFixture.response(["code": 200, "profile": ["userId": 42, "nickname": "合成网易昵称"], "account": ["id": 42]])
        case "/xeapi/v3/discovery/recommend/songs":
            return try MusicNeteaseFixture.response(["code": 200, "data": ["dailySongs": (101..<136).map(MusicNeteaseFixture.song)]])
        case "/weapi/v1/radio/get":
            return try MusicNeteaseFixture.response(["code": 200, "data": [MusicNeteaseFixture.song()]])
        case "/weapi/user/playlist":
            return try MusicNeteaseFixture.response(["code": 200, "more": false, "playlist": [
                MusicNeteaseFixture.collection(), MusicNeteaseFixture.collection(502, owner: 99, special: 0)]])
        case "/eapi/v6/playlist/detail":
            return try MusicNeteaseFixture.response(MusicNeteaseFixture.detail())
        case "/weapi/v3/song/detail":
            return try MusicNeteaseFixture.response(["code": 200, "songs": [MusicNeteaseFixture.song(), MusicNeteaseFixture.song(102)]])
        case "/weapi/music-vip-membership/client/vip/info":
            let at = Int(clock.now().timeIntervalSince1970 * 1000)
            return try MusicNeteaseFixture.response(["code": 200, "data": ["userId": 42, "now": at,
                "associator": ["expireTime": at + 300_000], "redplus": ["expireTime": at + 300_000]]])
        case "/eapi/song/music/detail/get":
            var row: [String: Any] = ["songId": payload["songId"] ?? "101"]
            for key in ["l", "m", "h", "sq", "hr", "je", "db", "sk", "jm", "vi"] { row[key] = ["size": 123] }
            return try MusicNeteaseFixture.response(["code": 200, "data": row])
        case "/xeapi/song/enhance/player/url/v1":
            return try MusicNeteaseFixture.response(MusicNeteaseFixture.audio(level: payload["level"] as? String ?? ""),
                headers: ["x-encr-ssid": "synthetic-session",
                          "x-encr-sskey": "0123456789abcdef0123456789abcdef"])
        default: throw MusicServiceError.invalidRequest
        }
    }
}

struct MusicNeteaseNativeFixture {
    let identity = try! MusicAccountFixture.identity("wy")
    let store = MusicAccountTestStore()
    let clock = MusicAccountTestClock()
    let http: MusicNeteaseTestHTTP
    let accounts: AccountSessionCoordinator
    let service: NativeMusicAccountService
    init(restored: Bool = true) throws {
        http = MusicNeteaseTestHTTP(clock: clock); accounts = AccountSessionCoordinator(store: store)
        service = NativeMusicAccountService(accounts: accounts, scopes: [.netease: identity],
                                            transport: http, clock: { [clock] in clock.now() })
        if restored { try store.write(.init(credential: MusicNeteaseFixture.credential()), for: identity) }
    }
    func enable() async throws { _ = try await service.setAccess(available: true, generation: 1) }
}
