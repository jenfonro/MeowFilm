// SPDX-License-Identifier: GPL-3.0-only
// Fixed meowmusic cc22a7e protocols, via the audited platforms.mjs translation.
import Foundation
import MeowContracts
import CryptoKit
import CommonCrypto
import MeowMusicFeature

enum MusicPlatformProtocol {
    static func md5(_ text: String) -> String { Insecure.MD5.hash(data: Data(text.utf8)).map { String(format: "%02x", $0) }.joined() }
    static func cookieHeader(_ fields: [String: String]) -> String {
        let allowed = CharacterSet(charactersIn: "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.!~*'()")
        return fields.sorted { $0.key < $1.key }.map {
            ($0.key.addingPercentEncoding(withAllowedCharacters: allowed) ?? "") + "=" +
                ($0.value.addingPercentEncoding(withAllowedCharacters: allowed) ?? "")
        }.joined(separator: "; ")
    }
    static func netease(uri: String, data: MJ) throws -> Data {
        let plain = data.json, checksum = md5("nobody" + uri + "use" + plain + "md5forencrypt")
        let payload = Array((uri + "-36cd479b6b5-" + plain + "-36cd479b6b5-" + checksum).utf8)
        let key = Array("e82ckenh8dichen8".utf8)
        var output = [UInt8](repeating: 0, count: payload.count + kCCBlockSizeAES128), written = 0
        let capacity = output.count
        let status = CCCrypt(CCOperation(kCCEncrypt), CCAlgorithm(kCCAlgorithmAES), CCOptions(kCCOptionPKCS7Padding | kCCOptionECBMode),
                             key, key.count, nil, payload, payload.count, &output, capacity, &written)
        try musicRequire(status == kCCSuccess, .unavailable)
        return Data(("params=" + output.prefix(written).map { String(format: "%02X", $0) }.joined()).utf8)
    }
    static func kugou(_ params: [String: String]) -> String {
        let salt = "OIlwieks28dk2k092lksi2UIkp" // Public protocol salt, not an account key.
        return md5(salt + params.keys.filter { $0 != "signature" }.sorted().map { $0 + "=" + params[$0]! }.joined() + salt)
    }
    static func numericID(_ v: MJ, zero: Bool = false) throws -> String {
        let id = v.id
        try musicRequire(id.range(of: zero ? "^[0-9]{1,64}$" : "^[1-9][0-9]{0,63}$", options: .regularExpression) != nil, .upstreamInvalid)
        return id
    }
    static func hashID(_ v: MJ) throws -> String {
        let s = v.string.uppercased()
        try musicRequire(s.range(of: "^[A-F0-9]{32}$", options: .regularExpression) != nil && s != String(repeating: "0", count: 32), .upstreamInvalid)
        return s
    }
}

/// Search-only budgets. Migu counts groups, whose recording editions must not
/// be truncated to the group page size. Account/browse pages remain unchanged.
enum MusicNativeSearchProtocol {
    static let platforms = ["wy", "tx", "kg", "kw", "mg"]
    static func maximumItems(_ platform: String) -> Int { platform == "mg" ? 200 : 30 }
    static func validate(query: String, page: Int) throws {
        _ = try musicText(query, maximum: 800)
        try musicRequire(!query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty &&
            query.unicodeScalars.count <= 200 && (1...200).contains(page))
    }
    static func count(_ value: MJ, maximum: Int) throws -> Int {
        if value.isInteger && value.number <= Double(maximum) { return value.int }
        if value.isString, value.string.range(of: "^[0-9]{1,16}$", options: .regularExpression) != nil,
           let result = Int(value.string), result <= maximum { return result }
        throw MusicServiceError.upstreamInvalid
    }
    static func first(_ raw: MJ, _ keys: String...) -> MJ {
        keys.map { raw[$0] }.first { $0 != .null && (!$0.isString || !$0.string.isEmpty) } ?? .null
    }
    static func query(_ value: MusicNativeHTTPRequest, host: String, path: String,
                      keys: Set<String>) -> [String: String]? {
        guard value.method == "GET", value.body == nil, value.url.scheme == "https",
              value.url.host == host, value.url.path == path, value.url.user == nil,
              value.url.password == nil, value.url.fragment == nil,
              value.url.port == nil || value.url.port == 443,
              let items = URLComponents(url: value.url, resolvingAgainstBaseURL: false)?.queryItems,
              items.count == keys.count, Set(items.map(\.name)) == keys,
              items.allSatisfy({ $0.value != nil }) else { return nil }
        return Dictionary(uniqueKeysWithValues: items.map { ($0.name, $0.value!) })
    }
    static func matches(_ value: MusicNativeHTTPRequest, _ expected: MusicNativeHTTPRequest) -> Bool {
        // Exact headers exclude credentials and case-variant duplicate fields.
        value.url == expected.url && value.method == expected.method &&
            value.body == nil && value.headers == expected.headers
    }
}

actor MusicNativePlatforms {
    let transport: any MusicNativeHTTPTransport
    let qq: any MusicPublicQQProviding
    let clients: (any MusicNativeClientContextProviding)?
    let now: @Sendable () -> Date
    var epoch = UUID()
    var volatileClients: [MusicAccountPlatform: MusicNativeClientDevice] = [:]
    let playlistHeads: AsyncValueCache<String, MJ>
    let songBatches: AsyncValueCache<String, [String: MusicNativeTrackData]>
    let completeCollections: AsyncValueCache<String, MusicPublicBrowseNativeCollection>
    var detailWitnesses: [String: MusicNativePublicBrowseWitness] = [:]
    let discoveryHot: AsyncValueCache<String, MusicNativeSearchDiscoveryValue<MusicNativeSearchHotValue>>
    let discoveryCharts: AsyncValueCache<String, MusicNativeSearchDiscoveryValue<[MusicPublicBrowseCollection]>>
    init(transport: any MusicNativeHTTPTransport, qq: any MusicPublicQQProviding, clock: @escaping @Sendable () -> Date,
         clients: (any MusicNativeClientContextProviding)? = nil) {
        self.transport = transport; self.qq = qq; now = clock; self.clients = clients
        playlistHeads = .init(capacity: 24, maximumCost: 4 * 1024 * 1024, maximumFlights: 8,
                              cost: { $0.bytes.count }, clock: clock)
        songBatches = .init(capacity: 48, maximumCost: 4 * 1024 * 1024, maximumFlights: 8,
                            cost: { $0.values.reduce(0) { $0 + $1.data.count } }, clock: clock)
        completeCollections = .init(capacity: 6, maximumCost: 12 * 1024 * 1024, maximumFlights: 4,
                                    cost: { $0.tracks.reduce(0) { $0 + $1.data.count } }, clock: clock)
        discoveryHot = .init(capacity: 1, maximumCost: 32 * 1024, maximumFlights: 2,
                             cost: { $0.value.keywords.reduce(0) {
                                 $0 + $1.keyword.utf8.count + 16
                             } + 1 }, clock: clock)
        discoveryCharts = .init(capacity: 1, maximumCost: 32 * 1024, maximumFlights: 2,
                                cost: { $0.value.reduce(0) {
                                    $0 + $1.id.utf8.count + $1.title.utf8.count + $1.creator.utf8.count +
                                        ($1.artwork?.absoluteString.utf8.count ?? 0) + 16
                                } }, clock: clock)
    }
    func revoke() async {
        epoch = UUID(); volatileClients = [:]
        detailWitnesses = [:]
        await playlistHeads.invalidate(); await songBatches.invalidate(); await completeCollections.invalidate()
        await discoveryHot.invalidate(); await discoveryCharts.invalidate()
    }
    func invalidateCollection(_ collection: MusicPublicBrowseCollection) async {
        detailWitnesses = detailWitnesses.filter { $0.value.collectionID != collection.id }
        await playlistHeads.invalidate(collection.nativeID)
        await completeCollections.invalidate(collection.id)
        // Song metadata is anonymous but can change on a user's explicit refresh.
        await songBatches.invalidate()
    }
    private func requestResponse(_ value: MusicNativeHTTPRequest) async throws -> MusicNativeHTTPResponse {
        let own = epoch
        try Task.checkCancellation()
        let response = try await transport.send(value)
        try Task.checkCancellation(); try musicRequire(own == epoch, .staleResult)
        if let failure = MusicPlatformFailure.http(response.status) {
            MusicRequestTrace(.publicAPI).record(.failed, http: response.status, failure: failure)
            throw failure
        }
        try musicRequire(response.data.count <= value.maximumBytes && response.data.count <= 2 * 1024 * 1024, .limit)
        return response
    }
    func requestData(_ value: MusicNativeHTTPRequest) async throws -> Data {
        try await requestResponse(value).data
    }
    func request(_ value: MusicNativeHTTPRequest, ignoringUpstreamKeys: Set<String> = [],
                 checkingNeteaseCode: Bool = false, client: MusicNativeClientDevice? = nil) async throws -> MJ {
        let own = epoch
        let response = try await requestResponse(value)
        let result: MJ
        do {
            result = try MJ.parse(response.data, maximum: 2 * 1024 * 1024, mode: .upstream,
                                  ignoringUpstreamKeys: ignoringUpstreamKeys)
            try musicRequire(result.isObject, .upstreamInvalid)
        } catch { throw MusicServiceError.upstreamInvalid }
        // Keep the response header beside its own body across suspension.
        // A shared "last response" would attribute concurrent limits wrongly.
        let checked = checkingNeteaseCode
            ? try await checkedWYCode(result, url: value.url, retryAfter: response.header("retry-after"))
            : result
        try musicRequire(own == epoch, .staleResult)
        if let client, let clients, response.header("set-cookie") != nil {
            _ = try await clients.acceptNativeClientResponse(client, url: value.url, headers: response.headers)
            try musicRequire(own == epoch, .staleResult)
        }
        return checked
    }
    func wy(_ path: String, _ params: MJ) async throws -> MJ {
        let client = try await clientDevice(.netease)
        let milliseconds = Int(now().timeIntervalSince1970 * 1000)
        let header = MJ.obj([("os", .string("pc")), ("appver", .string("3.1.17.204416")), ("osver", .string("Microsoft-Windows-10-Professional-build-19045-64bit")),
            ("channel", .string("netease")), ("deviceId", .string(client.fields["deviceId"]!)), ("versioncode", .string("140")), ("mobilename", .string("")),
            ("buildver", .string(String(milliseconds / 1000))), ("resolution", .string("1920x1080")), ("__csrf", .string("")),
            ("requestId", .string(String(milliseconds) + "_0" + String(format: "%03d", Int.random(in: 0..<1000))))])
        var input = params; input["e_r"] = .bool(false); input["header"] = header
        let body = try MusicPlatformProtocol.netease(uri: path, data: input)
        var cookieFields = client.fields
        for (key, value) in header.fields { cookieFields[key] = value.string }
        let cookie = MusicPlatformProtocol.cookieHeader(cookieFields)
        return try await request(.init(url: URL(string: "https://interfacepc.music.163.com/eapi/" + path.dropFirst(5))!, method: "POST",
            headers: ["Content-Type": "application/x-www-form-urlencoded;charset=utf-8",
                      "User-Agent": "NeteaseMusic 9.0.90/5038 (iPhone; iOS 16.2; zh_CN)", "Cookie": cookie], body: body),
            // The official toplist and some official chart playlist details
            // repeat these unused fields with different values. Discard them
            // only on their exact routes, without passing identities through
            // JSONSerialization/Double rounding.
            ignoringUpstreamKeys: path == "/api/toplist" ? ["updateFrequency"]
                : path == "/api/v6/playlist/detail" ? ["alg"] : [],
            checkingNeteaseCode: true, client: client)
    }
    func kg(_ path: String, _ params: [String: String], lyric: Bool = false, clear: Bool = false) async throws -> MJ {
        let client = try await clientDevice(.kugou), mid = client.fields["KUGOU_API_MID"]!, dfid = client.fields["dfid"] ?? "-"
        let time = String(Int(now().timeIntervalSince1970))
        var p = clear ? [:] : ["dfid": dfid, "mid": mid, "uuid": "-", "appid": "1005", "clientver": "20489", "clienttime": time]
        p.merge(params) { _, new in new }; p["signature"] = MusicPlatformProtocol.kugou(p)
        var url = URLComponents(string: (lyric ? "https://lyrics.kugou.com" : "https://gateway.kugou.com") + path)!
        url.queryItems = p.sorted { $0.key < $1.key }.map { URLQueryItem(name: $0.key, value: $0.value) }
        var headers = ["User-Agent": "Android15-1070-11083-46-0-DiscoveryDRADProtocol-wifi", "dfid": dfid, "mid": mid,
            "clienttime": time, "kg-rc": "1", "kg-thash": "5d816a0", "kg-rec": "1", "kg-rf": "B9EDA08A64250DEFFBCADDEE00F8F25F"]
        if !lyric { headers["x-router"] = "complexsearch.kugou.com" }
        let data = try await request(.init(url: url.url!, headers: headers))
        let codes = ["error_code", "errcode", "err_code"].filter(data.contains).map { data[$0].id }
        if codes.contains("20018") { throw MusicServiceError.authRequired }
        if codes.contains("20028") || data.contains("ssaCode") { throw MusicServiceError.verificationRequired }
        if path == "/v1/search" {
            try musicRequire(["200", "404"].contains(data["status"].id) && data["status"].id == data["errcode"].id && data["candidates"].isArray, .upstreamFailed)
        } else {
            try musicRequire(["1", "200"].contains(data["status"].id) && codes.allSatisfy { $0 == "0" || lyric && $0 == "200" }, .upstreamFailed)
        }
        return data
    }
    func search(platform: String, query: String, page: Int) async throws -> MusicNativeTrackPage {
        let own = epoch
        try Task.checkCancellation()
        try musicRequire((1...200).contains(page))
        if platform == "kw" {
            let request = try MusicNativeKuwoSearch.request(query: query, page: page)
            let data = try await requestData(request)
            return try MusicNativeKuwoSearch.decode(data, page: page)
        }
        if platform == "mg" {
            let request = try MusicNativeMiguSearch.request(query: query, page: page, now: now())
            let data = try await self.request(request)
            return try MusicNativeMiguSearch.decode(data, page: page)
        }
        if platform == "tx" {
            let value = try await qq.search(query: query, page: page)
            try musicRequire(own == epoch, .staleResult); return value
        }
        let rows: [MJ], total: Int
        if platform == "wy" {
            let data = try await wy("/api/cloudsearch/pc", .obj([("s", .string(query)), ("type", .n(1)), ("limit", .n(30)), ("offset", .n((page - 1) * 30)), ("total", .bool(true))]))
            let result = data["result"]; total = try result["songCount"].integer(1_000_000)
            try musicRequire(result["songs"].isArray || total == 0 && !result.contains("songs"), .upstreamInvalid)
            rows = result["songs"].array
        } else if platform == "kg" {
            let data = try await kg("/v2/search/song", ["keyword": query, "page": String(page), "pagesize": "30", "platform": "AndroidFilter",
                "iscorrection": "1", "privilegefilter": "0", "area_code": "1", "dopicfull": "1"])
            let result = data["data"]; total = try result["total"].integer(1_000_000)
            let list = result.contains("lists") ? result["lists"] : result["info"]
            try musicRequire(list.isArray, .upstreamInvalid); rows = list.array
        } else { throw MusicServiceError.sourceUnavailable }
        try musicRequire(rows.count <= 30 && (rows.isEmpty ? (page - 1) * 30 >= total : (page - 1) * 30 + rows.count <= total), .upstreamInvalid)
        let items = try rows.map { platform == "wy" ? try Self.neteaseSong($0) : try kugouSong($0) }
        return .init(items: items, total: total, nextPage: page * 30 < total && page < 200 && !rows.isEmpty ? page + 1 : nil)
    }
    static func neteaseSong(_ raw: MJ) throws -> MusicNativeTrackData {
        let id = try MusicPlatformProtocol.numericID(raw["id"]), album = raw.contains("al") ? raw["al"] : raw["album"]
        let artists = raw.contains("ar") ? raw["ar"] : raw["artists"]
        try musicRequire(artists.isArray && artists.array.count <= 64, .upstreamInvalid)
        // A public recording can have no album (missing/null/zero ID). This
        // optional display field must not invalidate the entire search/detail
        // page. Song identity and non-sentinel album IDs remain strict.
        let albumID: String
        if album["id"] == .null || album["id"].id == "0" { albumID = "" }
        else { albumID = try MusicPlatformProtocol.numericID(album["id"]) }
        let native = MJ.obj([("source", .string("wy")), ("musicId", .string(id)), ("songmid", .string(id)),
            ("name", .string(try raw["name"].text(1024))), ("singer", .string(try artists.array.map { try $0["name"].text(256, empty: true) }.joined(separator: "、"))),
            ("album", .string(album["name"].string)), ("albumId", .string(albumID)),
            ("duration", .number((raw.contains("dt") ? raw["dt"] : raw["duration"]).number / 1000)), ("img", album["picUrl"] == .null ? .string("") : album["picUrl"])])
        return .init(platform: "wy", data: native.bytes)
    }
    func kugouSong(_ raw: MJ) throws -> MusicNativeTrackData {
        let audio = raw["audio_info"], album = raw.contains("albuminfo") ? raw["albuminfo"] : raw["album_info"]
        func first(_ values: MJ...) -> MJ { values.first { $0 != .null } ?? .null }
        let hash = try MusicPlatformProtocol.hashID(first(raw["hash"], raw["FileHash"], audio["hash"]))
        let full = first(raw["name"], raw["FileName"]).string.replacingOccurrences(of: "\\.(mp3|flac|m4a|wav|ape|ogg|aac|wma)$", with: "", options: [.regularExpression, .caseInsensitive])
        var parts = full.components(separatedBy: " - ")
        let fallbackSinger = parts.count > 1 ? parts.removeFirst() : ""
        var singer = first(raw["SingerName"], raw["author_name"], .string(fallbackSinger)).string
        let singers = first(raw["singerinfo"], raw["Singers"])
        if singers != .null {
            try musicRequire(singers.isArray && singers.array.count <= 64, .upstreamInvalid)
            singer = try singers.array.map { try first($0["name"], $0["author_name"]).text(256, empty: true) }.joined(separator: "、")
        }
        var title = first(raw["OriSongName"], raw["songname"], raw["audio_name"], .string(parts.joined(separator: " - "))).string
        if raw.contains("OriSongName") && !raw["Suffix"].string.isEmpty { title += " " + raw["Suffix"].string }
        _ = try musicText(title, maximum: 1024)
        let ids = try [raw["Audioid"], raw["audio_id"], audio["audio_id"]]
            .filter { $0 != .null && !($0.isString && $0.string.isEmpty) && $0.id != "0" }
            .map { try MusicPlatformProtocol.numericID($0) }
        try musicRequire(Set(ids).count <= 1, .upstreamInvalid)
        let hashes = [MJ.string(hash), first(raw["hash_320"], raw["HQFileHash"], audio["hash_320"]),
            first(raw["hash_flac"], raw["SQFileHash"], audio["hash_flac"]), first(raw["hash_high"], raw["ResFileHash"], audio["hash_high"])]
        let sizes = [first(raw["filesize"], raw["FileSize"], audio["filesize"]), first(raw["filesize_320"], raw["HQFileSize"], audio["filesize_320"]),
            first(raw["filesize_flac"], raw["SQFileSize"], audio["filesize_flac"]), first(raw["filesize_high"], raw["ResFileSize"], audio["filesize_high"])]
        var types: [MJ] = [], nativeTypes = MJ.obj()
        for (i, quality) in ["128k", "320k", "flac", "flac24bit"].enumerated() {
            if sizes[i].isInteger && sizes[i].int > 0 && hashes[i].string != String(repeating: "0", count: 32) && !hashes[i].string.isEmpty {
                let h = try MusicPlatformProtocol.hashID(hashes[i])
                types.append(.obj([("type", .string(quality)), ("hash", .string(h)), ("size", sizes[i])]))
                nativeTypes[quality] = .obj([("hash", .string(h)), ("size", sizes[i])])
            }
        }
        let albumID = first(raw["album_id"], raw["AlbumID"], album["id"])
        let mix = first(raw["mixsongid"], raw["add_mixsongid"], raw["album_audio_id"], raw["MixSongID"])
        let duration = raw.contains("Duration") ? raw["Duration"].number : first(raw["timelen"], raw["timelength"], audio["timelength"]).number / 1000
        let info = MJ.obj([("source", .string("kg")), ("musicId", .string(hash)), ("hash", .string(hash)), ("songmid", .string(ids.first ?? "")),
            ("name", .string(title)), ("singer", .string(singer)), ("album", first(raw["AlbumName"], album["name"], album["album_name"], .string(""))),
            ("albumId", .string(albumID == .null || albumID == .string("") ? "0" : try MusicPlatformProtocol.numericID(albumID, zero: true))),
            ("albumAudioId", .string(mix == .null || mix == .string("") ? "0" : try MusicPlatformProtocol.numericID(mix, zero: true))),
            ("duration", .number(duration)), ("img", first(raw["cover"], raw["Image"], raw["trans_param"]["union_cover"], .string(""))),
            ("types", .array(types)), ("_types", nativeTypes)])
        return .init(platform: "kg", data: info.bytes)
    }
    func lyrics(platform: String, info: MJ) async throws -> MusicNativeLyrics {
        let own = epoch
        try Task.checkCancellation()
        if platform == "kw" {
            let request = try MusicNativeKuwoLyrics.request(info: info)
            let data = try await requestData(request)
            let value = try MusicNativeKuwoLyrics.decode(data)
            try musicRequire(own == epoch, .staleResult); return value
        }
        if platform == "mg" {
            let value = try await miguLyrics(info: info)
            try musicRequire(own == epoch, .staleResult); return value
        }
        if platform == "tx" {
            let value = try await qq.lyrics(mid: info["songmid"].id.isEmpty ? info["musicId"].id : info["songmid"].id)
            try musicRequire(own == epoch, .staleResult); return value
        }
        if platform == "wy" {
            let id = try MusicPlatformProtocol.numericID(info.contains("musicId") ? info["musicId"] : info["songmid"])
            let data = try await wy("/api/song/lyric/v1", .obj([("id", .string(id)), ("cp", .bool(false)), ("tv", .n(0)), ("lv", .n(0)),
                ("rv", .n(0)), ("kv", .n(0)), ("yv", .n(0)), ("ytv", .n(0)), ("yrv", .n(0))]))
            let yrc = MusicNativeLyricsParser.timed(data["yrc"]["lyric"].string)
            let text = yrc ? data["yrc"]["lyric"].string : data["lrc"]["lyric"].string
            let translated = yrc && MusicNativeLyricsParser.timed(data["ytlrc"]["lyric"].string) ? data["ytlrc"]["lyric"].string : data["tlyric"]["lyric"].string
            return .init(text: text, translation: translated)
        }
        try musicRequire(platform == "kg", .sourceUnavailable)
        let hash = try MusicPlatformProtocol.hashID(info["hash"]), duration = Int((info["duration"].number * 1000).rounded())
        let mix = info["albumAudioId"].id.isEmpty ? "0" : info["albumAudioId"].id
        _ = try MusicPlatformProtocol.numericID(.string(mix), zero: true)
        let data = try await kg("/v1/search", ["album_audio_id": mix, "appid": "1005", "clientver": "20489", "duration": String(duration),
            "hash": hash, "keyword": "", "lrctxt": "1", "man": "no"], lyric: true, clear: true)
        let rows = data["candidates"].array
        if rows.isEmpty { return .init(text: "") }
        try musicRequire(rows.count == 1, .lyricIdentityMismatch)
        let row = rows[0], id = try MusicPlatformProtocol.numericID(row["id"]), access = row["accesskey"].string
        try musicRequire(access.range(of: "^[a-fA-F0-9]{32}$", options: .regularExpression) != nil && row["contenttype"].id == "0" &&
            row["duration"].isInteger && row["duration"].number > 0 &&
            (!row.contains("hash") || row["hash"].string.uppercased() == hash) &&
            (!row.contains("album_audio_id") || row["album_audio_id"].id == mix) &&
            (duration == 0 || abs(row["duration"].number - Double(duration)) <= max(3000, min(row["duration"].number, Double(duration)) * 0.02)), .lyricIdentityMismatch)
        let reply = try await kg("/download", ["ver": "1", "client": "android", "id": id, "accesskey": access, "fmt": "krc", "charset": "utf8"], lyric: true)
        try musicRequire(reply["id"].id == id && reply["fmt"].string == "krc" && reply["contenttype"].id == "0", .lyricIdentityMismatch)
        return .init(text: reply["content"].string, encoding: .kugouKRC)
    }
}
