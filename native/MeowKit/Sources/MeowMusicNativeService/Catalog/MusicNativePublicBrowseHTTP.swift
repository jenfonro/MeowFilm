// SPDX-License-Identifier: GPL-3.0-only
// Fixed meowmusic cc22a7e netease crypto/discovery/catalog and kugou music protocols.
import Foundation
import CommonCrypto
import Security
import MeowMusicFeature

enum MusicNativePublicBrowseRoutes {
    static let eapi: Set<String> = [
        "/eapi/v6/playlist/detail", "/eapi/link/page/rcmd/resource/show",
        "/eapi/link/position/show/resource", "/eapi/tag/tab/tags",
        "/eapi/tag/tab/alg/recommends", "/eapi/tag/tab/playlists",
        "/eapi/toplist", "/eapi/search/pc/chart/list",
        "/eapi/search/pc/chart/detail", "/eapi/hot/search/relate/song/get"
    ]
    static let weapi: Set<String> = [
        "/weapi/v3/song/detail", "/weapi/cube/render/page/protocol",
        "/weapi/playlist/square/block/page", "/weapi/toplist/detail/v2",
        "/weapi/v1/discovery/new/songs"
    ]
    static func allows(_ request: MusicNativeHTTPRequest) -> Bool {
        let url = request.url
        guard url.scheme == "https", url.user == nil, url.password == nil,
              url.port == nil || url.port == 443, url.fragment == nil else { return false }
        switch url.host {
        case "interfacepc.music.163.com": return request.method == "POST" && eapi.contains(url.path) && url.query == nil
        case "music.163.com":
            let album = url.path.range(of: "^/weapi/v1/album/[1-9][0-9]{0,19}$", options: .regularExpression) != nil
            return request.method == "POST" && (weapi.contains(url.path) || album) && url.query == nil
        case "gateway.kugou.com":
            return request.method == "GET" && ["/v1/search/special", "/pubsongs/v2/get_other_list_file_nofilt"].contains(url.path)
        case "search.kuwo.cn", "nplserver.kuwo.cn":
            return MusicNativePublicBrowseKuwo.permits(request)
        case "jadeite.migu.cn", "c.musicapp.migu.cn", "app.c.nf.migu.cn":
            return MusicNativePublicBrowseMigu.permits(request)
        default: return false
        }
    }
}

/// Data-only anonymous GETs. Exact request reconstruction excludes extra
/// parameters, credentials, redirects, arbitrary hosts and case-variant headers.
enum MusicNativePublicBrowseWire {
    static func get(host: String, path: String, values: [String: String], headers: [String: String]) throws -> MusicNativeHTTPRequest {
        var url = URLComponents(string: "https://" + host + path)!
        url.queryItems = values.sorted { $0.key < $1.key }.map { .init(name: $0.key, value: $0.value) }
        url.percentEncodedQuery = url.percentEncodedQuery?.replacingOccurrences(of: "+", with: "%2B")
        guard let target = url.url else { throw MusicServiceError.invalidRequest }
        return .init(url: target, headers: headers)
    }
    static func playlistID(_ value: MJ) throws -> String {
        let id = try MusicPlatformProtocol.numericID(value)
        try musicRequire(id.range(of: "^[1-9][0-9]{0,19}$", options: .regularExpression) != nil, .upstreamInvalid)
        return id
    }
    static func count(_ value: MJ, maximum: Int = 9_007_199_254_740_991) throws -> Int {
        let number = try MusicNativeSearchProtocol.count(value, maximum: 9_007_199_254_740_991)
        try musicRequire(number >= 0, .upstreamInvalid)
        try musicRequire(number <= maximum, .limit)
        return number
    }
    static func searchPage(_ items: [MusicPublicBrowseCollection], total: Int, page: Int,
                           limit: Int = 30) throws -> MusicPublicBrowseQQSearch {
        try musicRequire(page > 0 && page <= Int.max / 100 && (1...100).contains(limit) && total >= 0, .upstreamInvalid)
        // A short or empty page ends the search; it does not imply missing
        // members or authorize a skipped range.
        try musicRequire(items.count <= limit && Set(items.map(\.id)).count == items.count, .upstreamInvalid)
        return .init(items: items, total: total,
                     nextPage: try MusicSearchPagination.nextPage(page: page, limit: limit,
                        count: items.count, total: total))
    }
    static func artwork(_ value: MJ, platform: MusicPublicBrowsePlatform) -> URL? {
        var text = value.string
        if platform == .migu && text.hasPrefix("/") && !text.hasPrefix("//") { text = "https://d.musicapp.migu.cn" + text }
        guard text.utf8.count <= 4096, var url = URLComponents(string: text),
              url.user == nil, url.password == nil, url.fragment == nil,
              url.port == nil || url.port == 443, let host = url.host, !host.isEmpty,
              url.scheme == "https" || url.scheme == "http" else { return nil }
        url.scheme = "https"; return url.url
    }
}

extension MusicNativePlatforms {
    /// WEAPI endpoints keep their verified crypto/host, not an unverified EAPI substitution.
    func browseWEAPI(_ path: String, _ params: MJ) async throws -> MJ {
        try Task.checkCancellation()
        let client = try await clientDevice(.netease)
        var data = params; data["csrf_token"] = .string("")
        let secret = String(UUID().uuidString.replacingOccurrences(of: "-", with: "").prefix(16))
        func aes(_ bytes: Data, key: String) throws -> Data {
            let input = Array(bytes), key = Array(key.utf8), iv = Array("0102030405060708".utf8)
            var output = [UInt8](repeating: 0, count: input.count + 16), written = 0
            let capacity = output.count
            let status = CCCrypt(CCOperation(kCCEncrypt), CCAlgorithm(kCCAlgorithmAES), CCOptions(kCCOptionPKCS7Padding),
                                 key, key.count, iv, input, input.count, &output, capacity, &written)
            try musicRequire(status == kCCSuccess, .unavailable)
            return Data(output.prefix(written))
        }
        let first = try aes(data.bytes, key: "0CoJUm6Qyw8W8jud").base64EncodedString()
        let encrypted = try aes(Data(first.utf8), key: secret).base64EncodedString()
        let spki = Data(base64Encoded: "MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDgtQn2JZ34ZC28NWYpAUd98iZ37BUrX/aKzmFbt7clFSs6sXqHauqKWqdtLkF2KexO40H1YTX8z2lSgBBOAxLsvaklV8k4cBFK9snQXE9/DDaFt6Rr7iVZMldczhC0JNgTz+SHXT6CBHuX3e9SdB1Ua44oncaTWz7OBGLbCiK45wIDAQAB")!
        let attrs: [CFString: Any] = [kSecAttrKeyType: kSecAttrKeyTypeRSA, kSecAttrKeyClass: kSecAttrKeyClassPublic,
                                     kSecAttrKeySizeInBits: 1024]
        var error: Unmanaged<CFError>?
        let plain = Data(repeating: 0, count: 112) + Data(secret.utf8.reversed())
        guard let key = SecKeyCreateWithData(Data(spki.dropFirst(22)) as CFData, attrs as CFDictionary, &error),
              let rsa = SecKeyCreateEncryptedData(key, .rsaEncryptionRaw, plain as CFData, &error) else {
            throw MusicServiceError.unavailable
        }
        let allowed = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "-._~"))
        let body = "params=" + (encrypted.addingPercentEncoding(withAllowedCharacters: allowed) ?? "") +
            "&encSecKey=" + (rsa as Data).map { String(format: "%02x", $0) }.joined()
        let request = MusicNativeHTTPRequest(url: URL(string: "https://music.163.com/weapi/" + path.dropFirst(5))!,
            method: "POST", headers: ["Content-Type": "application/x-www-form-urlencoded", "Referer": "https://music.163.com/",
                                     "User-Agent": "Mozilla/5.0",
                                     "Cookie": MusicPlatformProtocol.cookieHeader(client.fields)],
            body: Data(body.utf8))
        try musicRequire(MusicNativePublicBrowseRoutes.allows(request))
        return try await self.request(request, checkingNeteaseCode: true, client: client)
    }
    func browseWYCode(_ response: MJ) throws -> MJ {
        func readCode(_ value: MJ) throws -> Int {
            try musicRequire(value.isNumber && value.number.isFinite && value.number.rounded() == value.number &&
                (-99_999...99_999).contains(value.number), .upstreamInvalid)
            return Int(value.number)
        }
        var code = try readCode(response["code"])
        if code == 200, response["data"].isObject, response["data"].contains("code") {
            let nested = try readCode(response["data"]["code"])
            if nested != 0 && nested != 200 { code = nested }
        }
        if let failure = MusicPlatformFailure.netease(code) {
            MusicRequestTrace(.publicAPI).record(.failed, code: code, failure: failure)
            throw failure
        }
        return response
    }
    func browseKGDetail(_ id: String, offset: Int, limit: Int) async throws -> MJ {
        try musicRequire(id.range(of: "^[A-Za-z0-9_-]{1,200}$", options: .regularExpression) != nil && !id.hasPrefix("id_"))
        let client = try await clientDevice(.kugou), mid = client.fields["KUGOU_API_MID"]!, dfid = client.fields["dfid"] ?? "-"
        let time = String(Int(now().timeIntervalSince1970))
        var params = ["dfid": dfid, "mid": mid, "uuid": "-", "appid": "1005", "clientver": "20489", "clienttime": time,
            "area_code": "1", "begin_idx": String(offset), "plat": "1", "type": "1", "mode": "1", "personal_switch": "1",
            "extend_fields": "abtags,hot_cmt,popularization", "pagesize": String(limit), "global_collection_id": id]
        params["signature"] = MusicPlatformProtocol.kugou(params)
        var url = URLComponents(string: "https://gateway.kugou.com/pubsongs/v2/get_other_list_file_nofilt")!
        url.queryItems = params.sorted { $0.key < $1.key }.map { .init(name: $0.key, value: $0.value) }
        // No complexsearch x-router on pubsongs; no borrowed user token/cookie.
        let response = try await request(.init(url: url.url!, headers: [
            "User-Agent": "Android15-1070-11083-46-0-DiscoveryDRADProtocol-wifi", "dfid": dfid, "mid": mid,
            "clienttime": time, "kg-rc": "1", "kg-thash": "5d816a0", "kg-rec": "1", "kg-rf": "B9EDA08A64250DEFFBCADDEE00F8F25F"]))
        let codes = ["error_code", "errcode", "err_code"].filter(response.contains).map { response[$0].id }
        if codes.contains("20018") { throw MusicServiceError.authRequired }
        if codes.contains("20028") || response.contains("ssaCode") { throw MusicServiceError.verificationRequired }
        // Out-of-range detail pages are an empty tail, not an upstream failure.
        if codes.contains("149") {
            return .obj([("status", .n(1)), ("error_code", .n(149)),
                         ("data", .obj([("count", .n(0)), ("songs", .array([])), ("list_info", .null)]))])
        }
        try musicRequire(["1", "200"].contains(response["status"].id) && codes.allSatisfy { $0 == "0" }, .upstreamFailed)
        return response
    }
}
