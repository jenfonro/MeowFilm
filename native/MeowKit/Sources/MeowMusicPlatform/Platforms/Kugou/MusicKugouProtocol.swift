// SPDX-License-Identifier: GPL-3.0-only
// Fixed meowmusic cc22a7e kugou/client.go; no public arbitrary URL/router API.
import Foundation
import MeowAccounts
import MeowMusicFeature
import MeowJSON

enum MusicKugouRoute: CaseIterable, Equatable {
    case register, qrCreate, qrPoll, refresh, profile, daily, fm, directory, members, subscription, membership, audio, url
    case smsSend, smsLogin, chorus
    var path: String {
        switch self {
        case .register: return "/risk/v2/r_register_dev"
        case .qrCreate: return "/v2/qrcode"
        case .qrPoll: return "/v2/get_userinfo_qrcode"
        case .refresh: return "/v5/login_by_token"
        case .profile: return "/v3/get_my_info"
        case .daily: return "/everyday_song_recommend"
        case .fm: return "/v2/personal_recommend"
        case .directory: return "/v7/get_all_list"
        case .members: return "/v4/get_list_all_file_v3"
        case .subscription: return "/pubsongs/v2/get_other_list_file_nofilt"
        case .membership: return "/v1/get_union_vip"
        case .audio: return "/v1/audio/audio"
        case .url: return "/v5/url"
        case .smsSend: return "/v7/send_mobile_code"
        case .smsLogin: return "/v7/login_by_verifycode"
        case .chorus: return "/v1/audio_climax/audio"
        }
    }
    var host: String {
        switch self {
        case .register: return "userservice.kugou.com"
        case .qrCreate, .qrPoll: return "login-user.kugou.com"
        case .membership: return "kugouvip.kugou.com"
        case .smsLogin: return "loginserviceretry.kugou.com"
        case .chorus: return "expendablekmrcdn.kugou.com"
        default: return "gateway.kugou.com"
        }
    }
    var router: String? {
        switch self {
        case .refresh: return "login.user.kugou.com"
        case .smsSend: return "login.user.kugou.com"
        case .profile: return "usercenter.kugou.com"
        case .daily: return "everydayrec.service.kugou.com"
        case .fm: return "persnfm.service.kugou.com"
        case .directory, .members: return "cloudlist.service.kugou.com"
        case .audio: return "kmr.service.kugou.com"
        case .url: return "trackercdn.kugou.com"
        default: return nil // Subscription uses the gateway path directly in cc22a7e.
        }
    }
    var method: String {
        switch self {
        case .register, .refresh, .profile, .daily, .fm, .directory, .members, .audio, .smsSend, .smsLogin: return "POST"
        default: return "GET"
        }
    }
    /// For the product-owned HTTP whitelist too. Headers are case-insensitive:
    /// reject duplicate router spellings, cross-route routers and arbitrary hosts.
    static func permits(_ request: MusicAccountHTTPRequest) -> Bool {
        guard request.url.scheme == "https", request.url.user == nil, request.url.password == nil,
              request.url.port == nil || request.url.port == 443, request.url.fragment == nil,
              let route = allCases.first(where: { $0.host == request.url.host && $0.path == request.url.path }),
              route.method == request.method else { return false }
        let routers = request.headers.filter { $0.key.lowercased() == "x-router" }
        if let router = route.router { return routers.count == 1 && routers.first?.value == router }
        return routers.isEmpty
    }
}

enum MusicKugouWire {
    static func integer(_ value: Any?, maximum: Int = MusicPlatformJSON.maximumInteger, minimum: Int = 0) throws -> Int {
        if let value = value as? String {
            guard value.range(of: "^(0|[1-9][0-9]{0,15})$", options: .regularExpression) != nil,
                  let number = Int(value), number >= minimum, number <= maximum else { throw MusicServiceError.upstreamInvalid }
            return number
        }
        return try MusicQQWire.integer(value, maximum: maximum, minimum: minimum)
    }
    static func id(_ value: Any?, zero: Bool = false) throws -> String {
        String(try integer(value, minimum: zero ? 0 : 1))
    }
    static func hash(_ value: Any?) throws -> String {
        let text = try MusicQQWire.string(value, maximum: 32)
        guard text.range(of: "^[a-fA-F0-9]{32}$", options: .regularExpression) != nil,
              text != String(repeating: "0", count: 32) else { throw MusicServiceError.upstreamInvalid }
        return text.uppercased()
    }
    static func rows(_ raw: Any?) throws -> [[String: Any]] {
        guard let rows = raw as? [Any] else { throw MusicServiceError.upstreamInvalid }
        return try rows.map(MusicQQWire.object)
    }
    static func rows(_ raw: Any?, maximum: Int) throws -> [[String: Any]] {
        let rows = try self.rows(raw)
        guard rows.count <= maximum else { throw MusicServiceError.upstreamInvalid }
        return rows
    }
    static func first(_ values: Any?...) -> Any? {
        values.first { value in
            guard let value, !(value is NSNull) else { return false }
            return (value as? String) != ""
        } ?? nil
    }
    static func text(_ value: Any?, maximum: Int = 1024, empty: Bool = false) throws -> String {
        try MusicQQWire.string(value, maximum: maximum, empty: empty)
    }
    static func json(_ value: [String: Any]) throws -> Data {
        // Go JSON Encoder(SetEscapeHTML(false)) still escapes U+2028/U+2029.
        let data = try MusicQQWire.json(value)
        let text = String(decoding: data, as: UTF8.self)
            .replacingOccurrences(of: "\u{2028}", with: "\\u2028")
            .replacingOccurrences(of: "\u{2029}", with: "\\u2029")
        return Data(text.utf8)
    }
    static func query(_ base: String, _ fields: [String: String]) throws -> URL {
        guard var url = URLComponents(string: base) else { throw MusicServiceError.invalidRequest }
        // URLQueryItem leaves '+' literal; Kugou's form-query parser would turn
        // that into a space (and invalidate a token/signature). Match Go Values.Encode.
        url.percentEncodedQuery = String(decoding: MusicQQWire.form(fields), as: UTF8.self)
            .replacingOccurrences(of: "%20", with: "+")
        guard let result = url.url else { throw MusicServiceError.invalidRequest }; return result
    }
    static func publicText(_ text: String, auth: MusicKugouAuth?) throws -> String {
        var forms = [text], decoded = text
        for _ in 0..<3 {
            guard let next = decoded.removingPercentEncoding, next != decoded else { break }
            forms.append(next); decoded = next
        }
        for secret in [auth?.token, auth?.vipToken, auth?.t1?.secret].compactMap({ $0 }) where !secret.isEmpty {
            guard forms.allSatisfy({ !$0.contains(secret) }) else { throw MusicServiceError.upstreamInvalid }
        }
        return text
    }
    static func publicRow(_ row: JSONValue, context: MusicKugouContext) throws -> JSONValue {
        _ = try MusicPlatformJSON.bytes(row, maximum: 32_768)
        func visit(_ value: JSONValue) throws {
            switch value {
            case .string(let text): _ = try publicText(text, auth: context.auth)
            case .array(let values): for child in values { try visit(child) }
            case .object(let fields): for (key, child) in fields {
                _ = try publicText(key, auth: context.auth); try visit(child)
            }
            default: break
            }
        }
        try visit(row); return row
    }
    static func business(_ root: [String: Any], route: MusicKugouRoute) throws {
        for key in ["error_code", "errcode", "err_code"] {
            if let value = root[key] {
                let code = try integer(value)
                if code == 20018 { throw MusicServiceError.authRequired }
                if code == 20028 { throw MusicServiceError.verificationRequired }
                guard code == 0 else { throw MusicServiceError.upstreamFailed }
            }
        }
        if route == .smsLogin, (root["data"] as? [String: Any])?["info_list"] is [Any] {
            throw MusicSMSFailure(reason: .verificationRequired, detail: .accountSelectionRequired)
        }
        if root["ssaCode"] != nil || (root["data"] as? [String: Any])?["info_list"] is [Any] {
            throw MusicServiceError.verificationRequired
        }
        if let status = root["status"] {
            guard try integer(status) == 1 else { throw MusicServiceError.upstreamFailed }
        } else {
            guard route == .audio || route == .members,
                  ["error_code", "errcode", "err_code"].contains(where: { (try? integer(root[$0])) == 0 }) else {
                throw MusicServiceError.upstreamInvalid
            }
        }
        if let code = root["code"], try ![0, 200].contains(integer(code)) { throw MusicServiceError.upstreamFailed }
        if let error = root["error"] {
            let falseBoolean = (error as? NSNumber).map { CFGetTypeID($0) == CFBooleanGetTypeID() && !$0.boolValue } ?? false
            guard falseBoolean || (error as? String) == "" || (try? integer(error)) == 0 else { throw MusicServiceError.upstreamFailed }
        }
    }
}

struct MusicKugouDevice: Sendable {
    let seed: String
    let mid: String
    let dev: String
    let mac: String
    var dfid: String?
    init(saved: [String: Any] = [:]) throws {
        let allowed: Set<String> = ["seed", "guid", "KUGOU_API_GUID", "mid", "KUGOU_API_MID", "KUGOU_API_DEV", "KUGOU_API_MAC", "dfid"]
        guard Set(saved.keys).isSubset(of: allowed) else { throw MusicServiceError.invalidResponse }
        var seed = try MusicKugouWire.text(MusicKugouWire.first(saved["seed"], saved["KUGOU_API_GUID"], saved["guid"])
                                          ?? MusicKugouCrypto.randomHex(16), maximum: 36)
        if seed.count == 36 { seed = MusicKugouCrypto.md5(seed) }
        guard seed.range(of: "^[a-fA-F0-9]{32}$", options: .regularExpression) != nil else { throw MusicServiceError.invalidResponse }
        for key in ["seed", "guid", "KUGOU_API_GUID"] {
            if let raw = saved[key] {
                let text = try MusicKugouWire.text(raw, maximum: 36)
                guard (text.count == 36 ? MusicKugouCrypto.md5(text) : text) == seed else { throw MusicServiceError.targetChanged }
            }
        }
        self.seed = seed; mid = MusicKugouCrypto.decimalMD5(seed)
        for key in ["KUGOU_API_MID", "mid"] {
            if let value = saved[key], value as? String != mid { throw MusicServiceError.targetChanged }
        }
        dev = try MusicKugouWire.text(saved["KUGOU_API_DEV"] ?? String(seed.prefix(10)).uppercased(), maximum: 128)
        mac = try MusicKugouWire.text(saved["KUGOU_API_MAC"] ?? "02:00:00:00:00:00", maximum: 32)
        if let raw = saved["dfid"] {
            let value = try MusicKugouWire.text(raw, maximum: 128)
            guard value.range(of: "^[A-Za-z0-9_-]{8,128}$", options: .regularExpression) != nil else { throw MusicServiceError.upstreamInvalid }
            dfid = value
        }
    }
    var fields: [String: Any] {
        var value: [String: Any] = ["seed": seed, "KUGOU_API_GUID": seed, "KUGOU_API_MID": mid, "KUGOU_API_DEV": dev, "KUGOU_API_MAC": mac]
        if let dfid { value["dfid"] = dfid }; return value
    }
}

struct MusicKugouAuth: Sendable {
    /// Existing numeric t1 remains byte-compatible. SMS may return an opaque
    /// string (b900c259 login()); preserve it rather than coercing or dropping it.
    /// Older native readers only accept numeric t1 and require re-login for a
    /// newly imported opaque value; no existing credential is rewritten here.
    enum T1: Sendable {
        case number(Int), text(String)
        var raw: Any { switch self { case .number(let n): return n; case .text(let s): return s } }
        var secret: String? { if case .text(let s) = self { return s }; return nil }
    }
    let uid: String
    let token: String
    let t1: T1?
    let vipType: Int?
    let vipToken: String?
    init(_ raw: [String: Any], strict: Bool = false) throws {
        if strict && !Set(raw.keys).isSubset(of: ["userid", "token", "t1", "vip_type", "vip_token"]) {
            throw MusicServiceError.invalidResponse
        }
        uid = try MusicKugouWire.id(raw["userid"])
        token = try MusicKugouWire.text(raw["token"], maximum: 4096)
        guard !token.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { throw MusicServiceError.authRequired }
        t1 = try raw["t1"].map {
            if let number = try? MusicKugouWire.integer($0) { return .number(number) }
            return .text(try MusicKugouWire.text($0, maximum: 4096))
        }
        vipType = try raw["vip_type"].map { try MusicKugouWire.integer($0) }
        vipToken = try raw["vip_token"].map { try MusicKugouWire.text($0, maximum: 4096, empty: true) }
    }
    var fields: [String: Any] {
        var value: [String: Any] = ["userid": uid, "token": token]
        if let t1 { value["t1"] = t1.raw }
        if let vipType { value["vip_type"] = vipType }
        if let vipToken { value["vip_token"] = vipToken }; return value
    }
}

struct MusicKugouContext: Sendable {
    let epoch: UUID
    var device: MusicKugouDevice
    var auth: MusicKugouAuth?
    var qrID: UUID?
    var qrNonce: UUID?
    var clientDevice: MusicNativeClientDevice?
}

actor KugouMusicAccountAdapter: MusicAccountProvider {
    let transport: any MusicAccountHTTPTransport
    let now: @Sendable () -> Date
    let clientContexts: NativeMusicClientService?
    var epoch = UUID()
    struct QR {
        var expires: Date
        var context: MusicKugouContext
        var key: String?
        var busy = true
        var lastPoll: Date?
    }
    var qr: [UUID: QR] = [:]
    var sms: [UUID: MusicKugouContext] = [:]
    var nextCreate = Date.distantPast
    struct Flight { let qrID: UUID?; let task: Task<MusicAccountHTTPResponse, Error> }
    var flights: [UUID: Flight] = [:]
    init(transport: any MusicAccountHTTPTransport, now: @escaping @Sendable () -> Date,
         clientContexts: NativeMusicClientService? = nil) {
        self.transport = transport; self.now = now; self.clientContexts = clientContexts
    }
    func revoke() {
        epoch = UUID(); qr.removeAll(); sms.removeAll()
        for flight in flights.values { flight.task.cancel() }
        // Requests stay tracked until their physical transport has returned.
    }
    func cancelQR(_ id: UUID) {
        qr.removeValue(forKey: id)
        for flight in flights.values where flight.qrID == id { flight.task.cancel() }
    }
    func check(_ context: MusicKugouContext) throws {
        try Task.checkCancellation()
        guard context.epoch == epoch else { throw MusicServiceError.staleResult }
        if let id = context.qrID {
            guard let live = qr[id], live.context.qrNonce == context.qrNonce, live.expires > now() else {
                throw MusicServiceError.staleResult
            }
        }
    }
    func raw(_ route: MusicKugouRoute, context: MusicKugouContext, query: [String: String] = [:],
             body: Data? = nil, legacy: Bool = false) async throws -> MusicAccountHTTPResponse {
        try check(context)
        let seconds = String(Int(now().timeIntervalSince1970)), dfid = context.device.dfid ?? "-"
        var params = ["dfid": dfid, "mid": context.device.mid, "uuid": "-", "appid": "1005", "clientver": "20489", "clienttime": seconds]
        if let auth = context.auth { params["userid"] = auth.uid; params["token"] = auth.token }
        for (key, value) in query { params[key] = value }
        params["signature"] = MusicKugouCrypto.signature(params, body: body, web: route == .qrCreate || route == .qrPoll)
        let url = try MusicKugouWire.query("https://" + route.host + route.path, params)
        var headers = ["User-Agent": "Android15-1070-11083-46-0-DiscoveryDRADProtocol-wifi",
                       "dfid": dfid, "mid": context.device.mid, "clienttime": params["clienttime"]!,
                       "kg-rc": "1", "kg-thash": "5d816a0", "kg-rec": "1",
                       "kg-rf": "B9EDA08A64250DEFFBCADDEE00F8F25F", "Cache-Control": "no-store"]
        if body != nil { headers["Content-Type"] = legacy ? "application/x-www-form-urlencoded" : "application/json;charset=utf-8" }
        if let router = route.router { headers["x-router"] = router }
        if route == .smsLogin {
            headers["support-calm"] = "1"
            headers["User-Agent"] = "Android16-1070-11440-130-0-LOGIN-wifi"
        }
        let request = MusicAccountHTTPRequest(url: url, method: route.method, headers: headers, body: body)
        guard MusicKugouRoute.permits(request), (body?.count ?? 0) <= 256 * 1024, url.absoluteString.utf8.count <= 16_384 else {
            throw MusicServiceError.invalidRequest
        }
        let id = UUID(), transport = self.transport
        let task = Task { try await transport.send(request) }
        flights[id] = .init(qrID: context.qrID, task: task)
        defer { flights.removeValue(forKey: id) }
        let response: MusicAccountHTTPResponse
        do {
            response = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
        } catch {
            try check(context)
            if error is CancellationError { throw MusicServiceError.cancelled }
            if let known = error as? MusicServiceError { throw known }
            throw MusicServiceError.unavailable // Never forward errors containing a credential-bearing URL.
        }
        try check(context)
        guard response.data.count <= 2 * 1024 * 1024 else { throw MusicServiceError.limit }
        if response.header("ssa-code")?.isEmpty == false { throw MusicServiceError.verificationRequired }
        if [401, 403].contains(response.status) { throw MusicServiceError.authRequired }
        if response.status == 429 { throw MusicServiceError.rateLimited }
        guard (200...299).contains(response.status) else { throw MusicServiceError.upstreamFailed }
        return response
    }
    func request(_ route: MusicKugouRoute, context: MusicKugouContext, query: [String: String] = [:],
                 body: [String: Any]? = nil) async throws -> [String: Any] {
        let bytes = try body.map(MusicKugouWire.json)
        let response = try await raw(route, context: context, query: query, body: bytes)
        let root = try MusicQQWire.decode(response.data)
        try MusicKugouWire.business(root, route: route)
        return root
    }
}
