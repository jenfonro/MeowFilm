import Foundation
import CryptoKit
import MeowAccounts
import MeowMusicFeature
import MeowJSON
@testable import MeowMusicPlatform

// Synthetic data and an injected peer only. No real platform/Keychain/old service.
enum MusicKugouFixture {
    static let baseHash = String(repeating: "A", count: 32)
    static let hqHash = String(repeating: "B", count: 32)
    static let flacHash = String(repeating: "C", count: 32)
    static let highHash = String(repeating: "D", count: 32)
    static let seed = "0123456789abcdef0123456789abcdef"
    static let token = "synthetic-kg-token"
    static func credential(uid: String = "42", token: String = "synthetic-kg-token") throws -> AccountCredential {
        let device: [String: Any] = ["seed": seed, "KUGOU_API_GUID": seed, "KUGOU_API_MID": "176905054734845483393488264506404844848",
                                     "KUGOU_API_DEV": "0123456789", "KUGOU_API_MAC": "02:00:00:00:00:00", "dfid": "synthetic-dfid"]
        return try .init(data: MusicQQWire.json(["version": 1, "platform": "kg", "accountID": uid,
            "auth": ["userid": uid, "token": token, "t1": 0, "vip_type": 1, "vip_token": "synthetic-private-vip"], "device": device]))
    }
    static func response(_ fields: [String: Any], status: Int = 200, headers: [String: String] = [:]) throws -> MusicAccountHTTPResponse {
        .init(status: status, data: try MusicQQWire.json(fields), headers: headers)
    }
    static func song(fileID: Int = 501, order: Int = 1) -> [String: Any] {
        ["hash": baseHash, "name": "合成歌手 - 合成歌曲.mp3", "filename": "合成歌手 - 合成歌曲.mp3",
         "Audioid": 21, "album_id": 11, "album_audio_id": 22, "AlbumName": "合成专辑", "album_name": "合成专辑",
         "timelen": 180000, "time_length": 180, "file_size": 100, "filesize": 100,
         "hash_320": hqHash, "filesize_320": 200, "hash_flac": flacHash, "filesize_flac": 300,
         "hash_high": highHash, "filesize_high": 400, "fileid": fileID, "sort": order]
    }
    static func collection(listID: Int = 7, type: Int = 0, owner: Int = 42, name: String = "我喜欢", special: Int = 1,
                           count: Int = 2, global: String = "synthetic-global") -> [String: Any] {
        ["type": type, "source": 0, "listid": listID, "list_create_userid": owner, "name": name,
         "is_def": special, "count": count, "list_ver": 5, "sort": 1, "global_collection_id": global]
    }
    static func directory(_ rows: [[String: Any]]? = nil, count: Int? = nil, version: Int = 9) throws -> MusicAccountHTTPResponse {
        let rows = rows ?? [collection()]
        return try response(["status": 1, "data": ["info": rows, "count": count ?? rows.count, "total_ver": version]])
    }
    static func members(_ rows: [[String: Any]]? = nil, count: Int? = nil, version: Int = 5) throws -> MusicAccountHTTPResponse {
        let rows = rows ?? [song(fileID: 502, order: 2), song(fileID: 501, order: 1)]
        return try response(["errcode": 0, "data": ["info": rows, "count": count ?? rows.count, "list_ver": version, "listid": 7]])
    }
    static func audio(hash: String = flacHash, size: Int = 300) -> [String: Any] {
        ["status": 1, "hash": hash, "url": ["https://fs.kugou.com/synthetic.flac"],
         "fileSize": size, "IsFreePart": 0, "album_id": 11, "album_audio_id": 22]
    }
    static func fields(_ request: MusicAccountHTTPRequest) throws -> [String: String] {
        let query = URLComponents(url: request.url, resolvingAgainstBaseURL: false)?.percentEncodedQuery ?? ""
        var result: [String: String] = [:]
        for pair in query.split(separator: "&") {
            let parts = pair.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false)
            guard parts.count == 2,
                  let name = String(parts[0]).replacingOccurrences(of: "+", with: " ").removingPercentEncoding,
                  let value = String(parts[1]).replacingOccurrences(of: "+", with: " ").removingPercentEncoding,
                  result[name] == nil else { throw MusicServiceError.invalidRequest }
            result[name] = value
        }
        return result
    }
    static func validateSignature(_ request: MusicAccountHTTPRequest) throws {
        let query = try fields(request)
        let salt = request.url.host == "login-user.kugou.com" ? "NVPh5oo715z5DIWAeQlhMDsWXXQV4hwt" : "OIlwieks28dk2k092lksi2UIkp"
        let keys = query.keys.filter { $0 != "signature" }.sorted()
        let prefix = salt + keys.map { $0 + "=" + query[$0]! }.joined()
        let bytes = Data(prefix.utf8) + (request.body ?? Data()) + Data(salt.utf8)
        let expected = Insecure.MD5.hash(data: bytes).map { String(format: "%02x", $0) }.joined()
        guard query["signature"] == expected else { throw MusicServiceError.invalidRequest }
    }
    static func object(_ value: JSONValue) throws -> [String: JSONValue] {
        guard case .object(let fields) = value else { throw MusicServiceError.invalidResponse }; return fields
    }
}

actor MusicKugouTestHTTP: MusicAccountHTTPTransport {
    let clock: MusicAccountTestClock
    private var history: [MusicAccountHTTPRequest] = []
    private var replies: [String: [MusicAccountHTTPResponse]] = [:]
    private var blocks: [String: MusicAccountTestGate] = [:]
    private var failing: String?
    private var cancelled = 0
    init(clock: MusicAccountTestClock) { self.clock = clock }
    func respond(_ path: String, _ values: [MusicAccountHTTPResponse]) { replies[path] = values }
    func hold(_ path: String, _ gate: MusicAccountTestGate) { blocks[path] = gate }
    func fail(_ path: String) { failing = path }
    func recorded() -> [MusicAccountHTTPRequest] { history }
    func cancellationCount() -> Int { cancelled }
    func send(_ request: MusicAccountHTTPRequest) async throws -> MusicAccountHTTPResponse {
        guard MusicKugouRoute.permits(request), request.headers["Cache-Control"] == "no-store",
              request.headers.keys.allSatisfy({ $0.lowercased() != "cookie" && $0.lowercased() != "authorization" }) else {
            throw MusicServiceError.invalidRequest
        }
        try MusicKugouFixture.validateSignature(request)
        history.append(request)
        let path = request.url.path, q = try MusicKugouFixture.fields(request)
        if let gate = blocks[path] { await gate.wait() } // Deliberately ignores cancellation.
        if Task.isCancelled { cancelled += 1 }
        if failing == path {
            throw URLError(.cannotConnectToHost, userInfo: [NSURLErrorFailingURLErrorKey: request.url])
        }
        if var values = replies[path], !values.isEmpty {
            let result = values.count == 1 ? values[0] : values.removeFirst()
            replies[path] = values; return result
        }
        switch path {
        case "/risk/v2/r_register_dev":
            guard q["token"] == nil, q["userid"] == nil, q["p"]?.count == 256,
                  request.headers["Content-Type"] == "application/x-www-form-urlencoded",
                  let body = request.body, let cipher = Data(base64Encoded: body), !cipher.isEmpty, cipher.count % 16 == 0 else {
                throw MusicServiceError.invalidRequest
            }
            return try MusicKugouFixture.response(["status": 1, "data": ["dfid": "synthetic-dfid"]])
        case "/v2/qrcode":
            guard q["appid"] == "1001", q["srcappid"] == "2919", q["token"] == nil, q["userid"] == nil else { throw MusicServiceError.invalidRequest }
            return try MusicKugouFixture.response(["status": 1, "data": ["qrcode": "synthetic-qr-key"]])
        case "/v2/get_userinfo_qrcode":
            guard q["appid"] == "1005", q["plat"] == "4", q["qrcode"] == "synthetic-qr-key",
                  q["dev"]?.isEmpty == false, q["token"] == nil else { throw MusicServiceError.invalidRequest }
            return try MusicKugouFixture.response(["status": 1, "data": ["status": 4, "userid": 42,
                "token": MusicKugouFixture.token, "vip_type": 1, "vip_token": "synthetic-private-vip", "ignored": "synthetic-extra"]])
        case "/v3/get_my_info":
            let body = try MusicQQWire.decode(request.body ?? Data())
            guard (body["p"] as? String)?.count == 256, (body["p"] as? String) == (body["p"] as? String)?.uppercased(),
                  try MusicKugouWire.id(body["userid"]) == q["userid"], q["token"] != nil else { throw MusicServiceError.invalidRequest }
            return try MusicKugouFixture.response(["status": 1, "data": ["userid": q["userid"]!, "nickname": "合成酷狗昵称"]])
        case "/everyday_song_recommend", "/v2/personal_recommend":
            return try MusicKugouFixture.response(["status": 1, "data": ["song_list": [MusicKugouFixture.song()],
                "mode": "normal", "cur_mark": "not-a-continuation", "token": "must-not-be-forwarded"]])
        case "/v7/get_all_list": return try MusicKugouFixture.directory()
        case "/v4/get_list_all_file_v3": return try MusicKugouFixture.members()
        case "/pubsongs/v2/get_other_list_file_nofilt":
            return try MusicKugouFixture.response(["status": 1, "data": ["songs": [MusicKugouFixture.song()],
                "list_info": ["global_collection_id": "synthetic-global", "count": 1, "list_ver": 5]]])
        case "/v1/get_union_vip":
            let at = Int(clock.now().timeIntervalSince1970)
            return try MusicKugouFixture.response(["status": 1, "data": ["userid": q["userid"]!, "is_vip": 1,
                "vip_begin_time": at - 100, "vip_end_time": at + 600]])
        case "/v1/audio/audio":
            return try MusicKugouFixture.response(["errcode": 0, "data": [MusicKugouFixture.song()]])
        case "/v5/url":
            let sizes = ["128": 100, "320": 200, "flac": 300, "high": 400]
            guard let quality = q["quality"], let size = sizes[quality], let hash = q["hash"] else { throw MusicServiceError.invalidRequest }
            return try MusicKugouFixture.response(MusicKugouFixture.audio(hash: hash, size: size))
        default: throw MusicServiceError.invalidRequest
        }
    }
}

struct MusicKugouTestSystem {
    let clock = MusicAccountTestClock()
    let http: MusicKugouTestHTTP
    let provider: KugouMusicAccountAdapter
    init() {
        http = MusicKugouTestHTTP(clock: clock)
        provider = KugouMusicAccountAdapter(transport: http, now: { [clock] in clock.now() })
    }
}
