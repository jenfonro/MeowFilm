// SPDX-License-Identifier: GPL-3.0-only
// Fixed meowmusic cc22a7e kugou/feedback.go. No grade/report fallback.
import Foundation
import CryptoKit
import Security
import MeowAccounts
import MeowMusicFeature

enum MusicAccountFeedbackKugouWire {
    static let salt = "LnT6xpN3khm36zse0QzvmgTZ3waWdRSA"
    // Public protocol key, NOT the ordinary Kugou API RSA key or a secret.
    static let publicKey = "MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDECi0Np2UR87scwrvTr72L6oO01rBbbBPriSDFPxr3Z5syug0O24QyQO8bg27+0+4kBzTBTBOZ/WWU0WryL1JSXRTXLgFVxtzIY41Pe7lPOgsfTCn5kZcvKhYKJesKnnJDNr5/abvTGf+rHG3YRwsCHcQ08/q6ifSioBszvb3QiwIDAQAB"
    static func rsa(_ input: Data) throws -> String {
        guard input.count <= 117, let spki = Data(base64Encoded: publicKey), spki.count == 162 else {
            throw MusicServiceError.upstreamInvalid
        }
        let attributes: [CFString: Any] = [kSecAttrKeyType: kSecAttrKeyTypeRSA, kSecAttrKeyClass: kSecAttrKeyClassPublic,
                                         kSecAttrKeySizeInBits: 1024]
        var error: Unmanaged<CFError>?
        guard let key = SecKeyCreateWithData(Data(spki.dropFirst(22)) as CFData, attributes as CFDictionary, &error),
              let bytes = SecKeyCreateEncryptedData(key, .rsaEncryptionPKCS1, input as CFData, &error) else {
            throw MusicServiceError.unavailable
        }
        return (bytes as Data).base64EncodedString()
    }
    static func ack(_ response: MusicAccountHTTPResponse) throws -> [String: Any] {
        guard response.status == 200 else { throw MusicServiceError.upstreamFailed }
        let root = try MusicQQWire.decode(response.data)
        guard try MusicQQWire.integer(root["status"]) == 1,
              (root["data"] as? [String: Any])?["grade_synced"] == nil else { throw MusicServiceError.upstreamInvalid }
        for key in ["errcode", "error_code"] {
            if let code = root[key] { guard try MusicKugouWire.integer(code) == 0 else { throw MusicServiceError.upstreamFailed } }
        }
        return root
    }
    static func fields(_ value: MusicPlaybackFeedbackReport, mix: String) -> [String] {
        var fields = ["type_id=" + (value.event == .start ? "20431" : "4"), "action=play", "mixsongid=" + mix,
                      (value.event == .start ? "spt=" : "st=") + String(Int((value.duration * 1000).rounded()))]
        if value.event == .end { fields.append("spt=" + String(Int((value.position * 1000).rounded()))) }
        fields.append("sty=" + (value.target.automatic ? "自动" : "手动"))
        if value.event == .end {
            let milliseconds = Int(floor(value.listened * 1000))
            let state = value.reason == .exception ? "播放错误" :
                value.reason == .playend && milliseconds > 0 ? "完整播放" : "被终止"
            fields += ["duration=\(milliseconds)", "state=" + state, "type=1",
                       "fs=" + String(format: "%.1f", locale: Locale(identifier: "en_US_POSIX"), value.rate)]
        }
        // Unknown native source/fo are omitted; do not invent album/FM cohorts.
        return fields
    }
    static func clean(_ text: String) -> Bool {
        !text.isEmpty && !text.unicodeScalars.contains { $0.value < 32 || $0.value == 127 || $0 == "&" }
    }
}

extension KugouMusicAccountAdapter: MusicAccountFeedbackProvider {
    private func feedbackCSCC(_ path: String, method: String, query: [String: String], body: Data? = nil,
                              context: MusicKugouContext, authorization: MusicPlaybackFeedbackAuthorization) async throws -> [String: Any] {
        try check(context)
        var headers = ["Cache-Control": "no-store"]
        if method == "POST" { headers["Content-Type"] = "application/octet-stream" }
        if path != "/v3/qrydid" {
            headers["KG-Rec"] = "1"
            headers["User-Agent"] = "Android9-1070-10597-18-0-Cscc" + (path == "/v2/post" ? "Post" : "Gen") + "-wifi"
        }
        let request = MusicAccountHTTPRequest(url: try MusicKugouWire.query("http://d.kugou.com" + path, query),
            method: method, headers: headers, body: body, maximumBytes: 65536)
        guard MusicAccountFeedbackRoute.permitsCleartext(request) else { throw MusicServiceError.invalidRequest }
        let result = try await MusicAccountFeedbackWire.send(request, transport: transport, authorization: authorization)
        try check(context)
        return try MusicAccountFeedbackKugouWire.ack(result)
    }
    private func feedbackSession(mix: String, context: MusicKugouContext,
                                 authorization: MusicPlaybackFeedbackAuthorization) async throws -> MusicAccountFeedbackNativeSession {
        let uuid = context.device.seed, mid = context.device.mid, model = context.device.dev
        let time = String(Int(now().timeIntervalSince1970))
        let body = try MusicKugouWire.json(["machine": model, "mid": mid, "uuid": uuid, "wh": [1920, 1080]])
        let sign = MusicKugouCrypto.md5("_t" + time + "appidand02pbKC7zn{4U*ydo2M1Rir" + String(decoding: body, as: UTF8.self))
        let first = try await feedbackCSCC("/v3/qrydid", method: "POST", query: ["appid": "and02", "_t": time, "sign": sign],
                                         body: body, context: context, authorization: authorization)
        let did = try MusicKugouWire.text((first["data"] as? [String: Any])?["deviceid"], maximum: 128)
        let field = try MusicKugouCrypto.randomHex(16)
        let portrait = try MusicAccountFeedbackKugouWire.rsa(Data(
            "\(uuid)\t\(field)\t\(Int(now().timeIntervalSince1970 * 1000))\t\(did)".utf8))
        let next = String(Int(now().timeIntervalSince1970))
        let second = try await feedbackCSCC("/v2/gen", method: "GET", query: ["s": portrait, "appid": "3116", "_t": next,
            "sign": MusicKugouCrypto.md5("_t" + next + "appid3116s" + portrait + MusicAccountFeedbackKugouWire.salt)
        ], context: context, authorization: authorization)
        guard let encrypted = Data(base64Encoded: try MusicKugouWire.text(second["data"], maximum: 65536)) else {
            throw MusicServiceError.upstreamInvalid
        }
        let plain = try MusicKugouCrypto.aes(encrypted, key: field, iv: String(uuid.prefix(16)), decrypt: true)
        let data = try MusicQQWire.decode(plain)
        let clienttime: String
        if let string = data["clienttime"] as? String { clienttime = try MusicKugouWire.text(string, maximum: 64) }
        else { clienttime = String(try MusicKugouWire.integer(data["clienttime"])) }
        return .init(mixsongID: mix, cookie: try MusicKugouWire.text(data["cookie"], maximum: 4096),
            clienttime: clienttime, serverstr: try MusicKugouWire.text(data["serverstr"], maximum: 4096), field: field)
    }
    func feedback(_ value: MusicPlaybackFeedbackReport, credential: AccountCredential,
                  session: MusicAccountFeedbackNativeSession?,
                  guard authorization: MusicPlaybackFeedbackAuthorization) async throws -> MusicAccountFeedbackNativeResult {
        try await authorization.check()
        guard value.target.platform == .kugou else { throw MusicServiceError.targetChanged }
        let context = try context(credential), uuid = context.device.seed
        guard let auth = context.auth, uuid.count == 32,
              MusicAccountFeedbackKugouWire.clean(context.device.mid), MusicAccountFeedbackKugouWire.clean(context.device.dev) else {
            throw MusicServiceError.invalidRequest
        }
        let state: MusicAccountFeedbackNativeSession
        if value.event == .start {
            // Reuse M3's fixed exact read-only hash/MixSongID verification.
            // It does not call any library write or mutate M3's algorithm.
            let readGuard = MusicAccountSyncAuthorization { try await authorization.check() }
            defer { readGuard.revoke() }
            let row = try await syncMixRow(hash: value.target.nativeID, mix: value.target.mixsongID ?? "0",
                                          context: context, guard: readGuard)
            let duration = try MusicKugouWire.integer((row["audio_info"] as? [String: Any])?["timelength"], minimum: 1)
            guard abs(Double(duration) / 1000 - value.duration) <= 10 else { throw MusicServiceError.targetChanged }
            let mix = try MusicKugouWire.id((row["base"] as? [String: Any])?["album_audio_id"])
            state = try await feedbackSession(mix: mix, context: context, authorization: authorization)
        } else {
            guard let session else { throw MusicServiceError.staleResult }
            state = session
        }
        try await authorization.check(); try check(context)
        let seconds = Int(now().timeIntervalSince1970), milliseconds = Int(now().timeIntervalSince1970 * 1000)
        var fields = MusicAccountFeedbackKugouWire.fields(value, mix: state.mixsongID)
        fields += ["mid=" + context.device.mid, "uuid=" + uuid, "sys=9", "mod=" + context.device.dev, "channelid=18",
            "ip=0.0.0.0", "net=1", "ver=10597", "time=\(milliseconds)", "userid=" + auth.uid,
            "ss3=" + MusicKugouCrypto.md5(try MusicKugouCrypto.randomHex(16))]
        let event = Data(fields.joined(separator: "&").utf8)
        let line1 = ["4", "1", uuid, "0", "3116", "10597", "18", "9", context.device.dev, uuid, context.device.mid,
                     "0", "000000000000000000000000000000000000"].joined(separator: "\t")
        let line2 = [String(event.count), "10048", "0", String(seconds), "1", auth.uid, "0", "0"].joined(separator: "\t")
        let plain = Data((line1 + "\r\n" + line2 + "\r\n").utf8) + (try MusicAccountFeedbackCompression.compress(event))
        let key = MusicKugouCrypto.md5(uuid + state.clienttime + state.field + state.serverstr)
        let encrypted = try MusicKugouCrypto.aes(MusicAccountFeedbackCompression.compress(plain), key: key, iv: String(uuid.prefix(16)))
        var query = ["cookie": state.cookie, "length": String(plain.count), "appid": "3116", "_t": String(seconds)]
        let signed = query.keys.sorted().map { $0 + query[$0]! }.joined()
        query["sign"] = Insecure.MD5.hash(data: Data((signed + MusicAccountFeedbackKugouWire.salt).utf8) + encrypted)
            .map { String(format: "%02x", $0) }.joined()
        _ = try await feedbackCSCC("/v2/post", method: "POST", query: query, body: encrypted, context: context, authorization: authorization)
        return .init(receipt: .confirmed, session: state)
    }
}
