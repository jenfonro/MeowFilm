// SPDX-License-Identifier: GPL-3.0-only
// Fixed b900c25 / cc22a7e: single eapi Weblog, no paired scrobble endpoint.
import Foundation
import MeowAccounts
import MeowMusicFeature

enum MusicAccountFeedbackNeteaseWire {
    static func logs(_ value: MusicPlaybackFeedbackReport) throws -> String {
        var json: [String: Any] = ["id": value.target.nativeID, "type": "song",
                                  "mainsite": "1", "mainsiteWeb": "1", "content": ""]
        if !value.target.source.isEmpty { json["source"] = value.target.source; json["sourceId"] = value.target.sourceID }
        if !value.target.algorithm.isEmpty { json["alg"] = value.target.algorithm }
        if value.event == .end {
            json["download"] = 0; json["end"] = value.reason!.rawValue
            json["wifi"] = 0; json["time"] = Int(floor(value.listened))
        }
        let data = try JSONSerialization.data(withJSONObject: [
            ["action": value.event == .start ? "startplay" : "play", "json": json]
        ], options: [.sortedKeys, .withoutEscapingSlashes])
        guard data.count <= 16_384, let result = String(data: data, encoding: .utf8) else { throw MusicServiceError.limit }
        return result
    }
}
extension NeteaseMusicAccountAdapter: MusicAccountFeedbackProvider {
    func feedback(_ value: MusicPlaybackFeedbackReport, credential: AccountCredential,
                  session: MusicAccountFeedbackNativeSession?,
                  guard authorization: MusicPlaybackFeedbackAuthorization) async throws -> MusicAccountFeedbackNativeResult {
        try await authorization.check()
        guard value.target.platform == .netease else { throw MusicServiceError.targetChanged }
        let context = try context(credential), own = epoch
        try ensureDevice(context)
        var header = ["os": "pc", "appver": "3.1.17.204416",
            "osver": "Microsoft-Windows-10-Professional-build-19045-64bit", "channel": "netease"]
        for (key, text) in context.cookies.merging(context.device, uniquingKeysWith: { _, new in new })
            where ["osver", "deviceId", "os", "appver", "channel", "MUSIC_U", "MUSIC_A", "NMTID"].contains(key) {
            header[key] = text
        }
        header.merge(["versioncode": "140", "mobilename": "", "buildver": String(Int(now().timeIntervalSince1970)),
            "resolution": "1920x1080", "__csrf": context.cookies["__csrf"] ?? "",
            "requestId": "\(Int(now().timeIntervalSince1970 * 1000))_0" + (try MusicNeteaseCrypto.text(3, alphabet: "0123456789"))
        ]) { _, new in new }
        let form = try MusicNeteaseCrypto.eapi("/api/feedback/weblog", plain: MusicQQWire.json([
            "logs": MusicAccountFeedbackNeteaseWire.logs(value), "header": header, "e_r": false
        ]))
        let request = MusicAccountHTTPRequest(url: URL(string: "https://clientlog.music.163.com/eapi/feedback/weblog")!,
            method: "POST", headers: ["Content-Type": "application/x-www-form-urlencoded;charset=utf-8",
                "Cache-Control": "no-store", "Cookie": MusicNeteaseWire.cookieHeader(header),
                "User-Agent": "NeteaseMusic 9.0.90/5038 (iPhone; iOS 16.2; zh_CN)"],
            body: MusicQQWire.form(form), maximumBytes: 65536)
        let response = try await MusicAccountFeedbackWire.send(request, transport: transport, authorization: authorization)
        try check(context, epoch: own)
        // Do not update credentials from a feedback response.
        _ = try MusicNeteaseWire.response(response)
        return .init(receipt: .confirmed)
    }
}
