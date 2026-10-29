// SPDX-License-Identifier: GPL-3.0-only
// Fixed meowmusic cc22a7e qq/feedback.go, one observed duration report.
import Foundation
import MeowAccounts
import MeowMusicFeature

enum MusicAccountFeedbackQQWire {
    static func xml(_ text: String) -> String {
        text.replacingOccurrences(of: "&", with: "&amp;").replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;").replacingOccurrences(of: "\"", with: "&quot;")
            .replacingOccurrences(of: "'", with: "&apos;")
    }
    static func confirmed(_ data: Data) -> Bool {
        guard data.count > 5, data.prefix(5) == Data(repeating: 0, count: 5),
              let plain = try? MusicAccountFeedbackCompression.inflateZlib(Data(data.dropFirst(5))),
              let root = try? MusicQQWire.decode(plain),
              let code = try? MusicQQWire.integer(root["code"], minimum: Int(Int32.min)) else { return false }
        return code == 0
    }
    static func document(comm: [String: Any], attributes: [String: String]) -> Data {
        var text = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<root>\r\n"
        for key in comm.keys.sorted() { text += "    <\(key)>\(xml(String(describing: comm[key]!)))</\(key)>\r\n" }
        text += "    <cid>228</cid>\r\n    <item "
        text += attributes.keys.sorted().map { "\($0)=\"\(xml(attributes[$0]!))\"" }.joined(separator: " ")
        text += "/>\r\n</root>"
        return Data(text.utf8)
    }
}

extension QQMusicAccountAdapter: MusicAccountFeedbackProvider {
    func feedback(_ value: MusicPlaybackFeedbackReport, credential: AccountCredential,
                  session: MusicAccountFeedbackNativeSession?,
                  guard authorization: MusicPlaybackFeedbackAuthorization) async throws -> MusicAccountFeedbackNativeResult {
        try await authorization.check()
        let auth = try auth(credential), own = deviceGeneration
        guard value.target.platform == .qq else { throw MusicServiceError.targetChanged }
        if value.event == .start { return .init(receipt: .registered) }
        let listened = Int(floor(value.listened))
        if listened < 1 { return .init(receipt: .unconfirmed) }
        let mid = try MusicQQWire.id(value.target.nativeID, mid: true)
        let track = try await track(mid, auth: auth)
        try await authorization.check()
        guard own == deviceGeneration else { throw MusicServiceError.staleResult }
        let duration = try MusicQQWire.integer(track["interval"], maximum: 86400, minimum: 1)
        let songID = try MusicQQWire.integer(track["id"], minimum: 1)
        let filename = "M500" + mid + mid + ".mp3"
        // A vkey is required by this fixed feedback protocol; do not impose
        // official-audio/VIP quality policy, download audio or resolve a URL.
        let data = try await rpc("music.vkey.GetVkey", "UrlGetVkey", [
            "uin": auth["str_musicid"]!, "filename": [filename], "guid": try MusicQQWire.randomHex(16),
            "songmid": [mid], "songtype": [0], "ctx": 0
        ], auth: auth)
        try await authorization.check()
        guard own == deviceGeneration else { throw MusicServiceError.staleResult }
        let rows = try array(data["midurlinfo"], maximum: 1)
        guard rows.count == 1 else { throw MusicServiceError.upstreamInvalid }
        let row = try MusicQQWire.object(rows[0])
        guard row["songmid"] as? String == mid, row["filename"] as? String == filename,
              try MusicQQWire.integer(row["result"]) == 0 else { throw MusicServiceError.targetChanged }
        let vkey = try MusicQQWire.string(row["vkey"], maximum: 8192)
        let device = try await ensureDevice()
        try await authorization.check()
        guard own == deviceGeneration else { throw MusicServiceError.staleResult }
        let timestamp = Int(now().timeIntervalSince1970), uid = auth["str_musicid"] as! String
        let singer = ((track["singer"] as? [[String: Any]])?.first)?["id"]
        let attrs = [
            "cmd": "1", "optime": String(timestamp), "QQ": uid,
            "uid": (device["session"] as? [String: Any])?["uid"] as? String ?? "",
            "songtype": "1", "playtype": "5", "time": String(listened), "play_duration_mi": String(listened * 1000),
            "audiotime": String(duration * 1000), "songid": String(songID),
            "singerid": String(try singer.map { try MusicQQWire.integer($0) } ?? 0), "vkey": vkey,
            "timekey": MusicQQWire.md5("\(timestamp)\(listened)\(uid)gk2$Lh-&l4#!4iow").uppercased()
        ]
        let bytes = MusicAccountFeedbackQQWire.document(comm: comm(device, auth: auth), attributes: attrs)
        let key = auth["musickey"] as! String
        let request = MusicAccountHTTPRequest(url: URL(string: "https://stat6.y.qq.com/android/fcgi-bin/imusic_tj")!,
            method: "POST", headers: ["Content-Encoding": "gzip", "Content-Type": "application/octet-stream",
                "User-Agent": "QQMusic 20090008(android 15)", "Referer": "https://y.qq.com/",
                "Cookie": "uin=\(uid); qqmusic_uin=\(uid); qm_keyst=\(key); qqmusic_key=\(key)"],
            body: try MusicAccountFeedbackCompression.compress(bytes, gzip: true), maximumBytes: 65536)
        let response = try await MusicAccountFeedbackWire.send(request, transport: transport, authorization: authorization)
        guard own == deviceGeneration else { throw MusicServiceError.staleResult }
        return .init(receipt: MusicAccountFeedbackQQWire.confirmed(response.data) ? .confirmed : .unconfirmed)
    }
}
