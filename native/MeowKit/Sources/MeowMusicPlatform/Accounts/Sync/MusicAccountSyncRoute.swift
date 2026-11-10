// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowMusicFeature

enum MusicAccountSyncRoute {
    enum Kugou: String, CaseIterable {
        case create = "/cloudlist.service/v5/add_list"
        case delete = "/v2/delete_list"
        case rename = "/v1/modify_list"
        case add = "/cloudlist.service/v6/add_song"
        case remove = "/v4/delete_songs"
        case order = "/v1/modify_song_sort"
        case hashMapping = "/v2/album_audio/audio"
        case metadata = "/kmr/v2/audio"
        case subscriptionInfo = "/v3/get_list_info"
        var router: String? {
            switch self {
            case .create, .add: return nil
            case .hashMapping: return "kmr.service.kugou.com"
            case .metadata: return "openapi.kugou.com"
            case .subscriptionInfo: return "pubsongs.kugou.com"
            default: return "cloudlist.service.kugou.com"
            }
        }
        var encrypted: Bool { [.delete, .rename, .order].contains(self) }
        var cloud: Bool { self == .rename || self == .order }
    }
    static func permits(_ request: MusicAccountHTTPRequest) -> Bool {
        guard request.url.scheme == "https", request.url.user == nil, request.url.password == nil,
              request.url.port == nil || request.url.port == 443, request.url.fragment == nil,
              request.method == "POST" else { return false }
        let routers = request.headers.filter { $0.key.lowercased() == "x-router" }
        switch request.url.host {
        case "music.163.com":
            return routers.isEmpty && ["/weapi/playlist/create", "/weapi/playlist/remove", "/weapi/radio/like"].contains(request.url.path)
        case "interfacepc.music.163.com":
            return routers.isEmpty && ["/eapi/batch", "/eapi/playlist/manipulate/tracks",
                "/eapi/playlist/subscribe", "/eapi/playlist/unsubscribe"].contains(request.url.path)
        case "gateway.kugou.com":
            guard let route = Kugou(rawValue: request.url.path) else { return false }
            if let router = route.router { return routers.count == 1 && routers.first?.value == router }
            return routers.isEmpty
        default: return false
        }
    }
}

extension KugouMusicAccountAdapter {
    func syncRequest(_ route: MusicAccountSyncRoute.Kugou, payload: [String: Any],
                     context: MusicKugouContext, guard authorization: MusicAccountSyncAuthorization) async throws -> [String: Any] {
        try check(context); try await authorization.check()
        guard let auth = context.auth else { throw MusicServiceError.authRequired }
        let seconds = Int(now().timeIntervalSince1970), dfid = context.device.dfid ?? "-"
        var query = ["dfid": dfid, "mid": context.device.mid, "appid": "1005", "clientver": "20489", "clienttime": String(seconds)]
        if !route.cloud { query["uuid"] = "-"; query["userid"] = auth.uid; query["token"] = auth.token }
        if route == .hashMapping { query = [:] } // Fixed clear/unsigned identity lookup, no account data.
        var body = try MusicKugouWire.json(payload), aes: String?
        if route.encrypted {
            let seed = try MusicKugouCrypto.randomHex(3), key = MusicKugouCrypto.md5(seed)
            aes = key
            body = try MusicKugouCrypto.aes(body, key: String(key.prefix(16)), iv: String(key.suffix(16)))
            query["p"] = try MusicKugouCrypto.rsaPKCS1(MusicKugouWire.json(["aes": seed, "uid": auth.uid, "token": auth.token]))
            query["key"] = MusicKugouCrypto.paramsKey(seconds)
            if route == .delete { body = Data(body.base64EncodedString().utf8); query["last_area"] = "gztx"; query["last_time"] = String(seconds) }
        } else if route == .create && (payload["type"] as? Int) == 0 || route == .add {
            query["last_area"] = "gztx"; query["last_time"] = String(seconds)
        }
        if !route.cloud && route != .hashMapping { query["signature"] = MusicKugouCrypto.signature(query, body: body) }
        var headers = ["User-Agent": "Android15-1070-11083-46-0-DiscoveryDRADProtocol-wifi",
            "dfid": dfid, "mid": context.device.mid, "clienttime": String(seconds), "kg-rc": "1", "kg-thash": "5d816a0",
            "kg-rec": "1", "kg-rf": "B9EDA08A64250DEFFBCADDEE00F8F25F", "Cache-Control": "no-store",
            "Content-Type": route == .delete ? "application/x-www-form-urlencoded" : "application/json;charset=utf-8"]
        if let router = route.router { headers["x-router"] = router }
        if route == .metadata { headers["KG-TID"] = "238" }
        let request = MusicAccountHTTPRequest(url: try MusicKugouWire.query("https://gateway.kugou.com" + route.rawValue, query),
                                               method: "POST", headers: headers, body: body)
        guard MusicAccountSyncRoute.permits(request), body.count <= 256 * 1024 else { throw MusicServiceError.invalidRequest }
        try check(context); try await authorization.check()
        let response: MusicAccountHTTPResponse
        do { response = try await transport.send(request) }
        catch { throw MusicServiceError.sanitized(error) }
        try check(context); try await authorization.check()
        guard response.data.count <= 2 * 1024 * 1024 else { throw MusicServiceError.limit }
        if response.header("ssa-code")?.isEmpty == false { throw MusicServiceError.verificationRequired }
        if [401, 403].contains(response.status) { throw MusicServiceError.authRequired }
        if response.status == 429 { throw MusicServiceError.rateLimited }
        guard (200...299).contains(response.status) else { throw MusicServiceError.upstreamFailed }
        let plain: Data
        if let aes {
            if let decrypted = try? MusicKugouCrypto.aes(response.data, key: String(aes.prefix(16)), iv: String(aes.suffix(16)), decrypt: true) {
                plain = decrypted
            } else {
                // Only a well-formed explicit plaintext business rejection may
                // bypass decrypt, never an apparent success after bad padding.
                let rejection = try MusicQQWire.decode(response.data)
                do { try MusicKugouWire.business(rejection, route: .members) }
                catch { throw error }
                throw MusicServiceError.upstreamInvalid
            }
        } else { plain = response.data }
        let root = try MusicQQWire.decode(plain)
        try MusicKugouWire.business(root, route: .members)
        return root
    }
}
