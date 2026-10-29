import Foundation
import MeowAccounts
import MeowContracts
import MeowMusicFeature
import MeowJSON
@testable import MeowMusicPlatform

// Entirely synthetic fixtures. Never reads the user's Keychain or contacts a
// platform. This store exercises the real injected AccountSessionCoordinator.
final class MusicAccountTestStore: CredentialStore, @unchecked Sendable {
    private let lock = NSLock()
    private var records: [AccountIdentity: CredentialRecord] = [:]
    private var events: [String] = []
    private var failure: String?
    func fail(_ operation: String?) { lock.lock(); failure = operation; lock.unlock() }
    func read(for identity: AccountIdentity) throws -> CredentialRecord? {
        lock.lock(); defer { lock.unlock() }; events.append("read")
        if failure == "read" { throw CredentialStoreError.interactionNotAllowed }
        return records[identity]
    }
    func write(_ record: CredentialRecord, for identity: AccountIdentity) throws {
        lock.lock(); defer { lock.unlock() }; events.append("write")
        if failure == "write" { throw CredentialStoreError.backendFailure }
        records[identity] = record
    }
    func remove(for identity: AccountIdentity) throws {
        lock.lock(); defer { lock.unlock() }; events.append("remove")
        if failure == "remove" { throw CredentialStoreError.backendFailure }
        records.removeValue(forKey: identity)
    }
    func peek(_ identity: AccountIdentity) -> CredentialRecord? {
        lock.lock(); defer { lock.unlock() }; return records[identity]
    }
    func calls() -> [String] { lock.lock(); defer { lock.unlock() }; return events }
}

final class MusicAccountTestClock: @unchecked Sendable {
    private let lock = NSLock()
    private var date = Date(timeIntervalSince1970: 1_800_000_000)
    func now() -> Date { lock.lock(); defer { lock.unlock() }; return date }
    func advance(_ seconds: TimeInterval) { lock.lock(); date.addTimeInterval(seconds); lock.unlock() }
}

actor MusicAccountTestGate {
    private var entered = false, released = false
    private var starts: [CheckedContinuation<Void, Never>] = []
    private var waits: [CheckedContinuation<Void, Never>] = []
    func wait() async {
        entered = true; starts.forEach { $0.resume() }; starts.removeAll()
        if !released { await withCheckedContinuation { waits.append($0) } }
    }
    func started() async {
        if !entered { await withCheckedContinuation { starts.append($0) } }
    }
    func release() { released = true; waits.forEach { $0.resume() }; waits.removeAll() }
}

enum MusicAccountFixture {
    static let mid = "SyntheticMID"
    static func identity(_ site: String = "tx", account: UUID = UUID()) throws -> AccountIdentity {
        .init(source: try SourceAddress(installationID: UUID(uuidString: "11111111-1111-4111-8111-111111111111")!,
                                        siteKey: site), accountID: account)
    }
    static func auth(_ uid: Int = 101) -> [String: Any] {
        ["musicid": uid, "musickey": "synthetic-account-key", "encrypt_uin": "synthetic-euin-\(uid)"]
    }
    static func credential(_ uid: Int = 101) throws -> AccountCredential {
        try .init(data: MusicQQWire.json(["version": 1, "platform": "tx", "auth": auth(uid)]))
    }
    static func song(_ mid: String = "SyntheticMID", id: Int = 101) -> [String: Any] {
        ["mid": mid, "id": id, "type": 0, "title": "合成歌曲", "interval": 180,
         "singer": [["name": "合成歌手"]], "album": ["mid": "SyntheticAlbum", "name": "合成专辑"],
         "file": ["size_128mp3": 100, "size_320mp3": 200, "size_flac": 300, "size_new": [400]]]
    }
    static func response(_ fields: [String: Any], status: Int = 200) throws -> MusicAccountHTTPResponse {
        .init(status: status, data: try MusicQQWire.json(fields))
    }
    static func rpc(_ fields: [String: Any]) throws -> MusicAccountHTTPResponse {
        try response(["code": 0, "req_0": ["code": 0, "data": fields]])
    }
}

actor MusicAccountTestHTTP: MusicAccountHTTPTransport {
    let clock: MusicAccountTestClock
    private var requests: [MusicAccountHTTPRequest] = []
    private var overrides: [String: MusicAccountHTTPResponse] = [:]
    private var block: (String, MusicAccountTestGate)?
    init(clock: MusicAccountTestClock) { self.clock = clock }
    func replies(_ method: String, _ response: MusicAccountHTTPResponse) { overrides[method] = response }
    func hold(_ method: String, _ gate: MusicAccountTestGate) { block = (method, gate) }
    func recorded() -> [MusicAccountHTTPRequest] { requests }
    static func fields(_ request: MusicAccountHTTPRequest) throws -> [String: Any] {
        try MusicQQWire.decode(request.body ?? Data())
    }
    static func method(_ request: MusicAccountHTTPRequest) throws -> String {
        if request.url.host == "u.y.qq.com" {
            return try MusicQQWire.string(MusicQQWire.object(fields(request)["req_0"])["method"])
        }
        return request.url.path
    }
    func send(_ request: MusicAccountHTTPRequest) async throws -> MusicAccountHTTPResponse {
        requests.append(request)
        let method = try Self.method(request)
        // Deliberately non-cooperative: access/close must not release its slot.
        if let block, block.0 == method { await block.1.wait() }
        if let override = overrides[method] { return override }
        switch method {
        case "/ptqrshow":
            let png = Data(base64Encoded: "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aVxkAAAAASUVORK5CYII=")!
            return .init(status: 200, data: png, headers: ["Set-Cookie": "qrsig=synthetic-qr; Path=/; HttpOnly"])
        case "/ptqrlogin":
            return .init(status: 200, data: Data("ptuiCB('0','0','https://ssl.ptlogin2.graph.qq.com/check_sig?uin=101&ptsigx=synthetic-sign','0','ok','Synthetic');".utf8))
        case "/check_sig":
            return .init(status: 302, data: Data(), headers: ["Set-Cookie":
                "p_skey=synthetic-pkey; Path=/, p_uin=o000101; Path=/, unexpected=synthetic-not-forwarded"])
        case "/oauth2.0/authorize":
            return .init(status: 302, data: Data(), headers: ["Location": "https://y.qq.com/portal/wx_redirect.html?code=synthetic-code"])
        case "/tme/trpc/proxy":
            return try MusicAccountFixture.response(["data": "{\"data\":{\"q16\":\"synthetic-q16\",\"q36\":\"synthetic-q36\"}}"])
        case "GetSession":
            return try MusicAccountFixture.rpc(["session": ["uid": 123, "sid": "synthetic-device-session"]])
        case "QQLogin": return try MusicAccountFixture.rpc(MusicAccountFixture.auth())
        case "GetLoginUserInfo": return try MusicAccountFixture.rpc(["musicid": 101])
        case "GetHomepageHeader":
            return try MusicAccountFixture.rpc(["Info": ["BaseInfo": ["EncryptedUin": "synthetic-euin-101", "Name": "合成昵称"]]])
        case "get_radio_track":
            return try MusicAccountFixture.rpc(["tracks": [MusicAccountFixture.song()]])
        case "GetRadarSong":
            return try MusicAccountFixture.rpc(["HasMore": false, "VecSongs":
                (0..<35).map { ["Track": MusicAccountFixture.song("SyntheticMID\($0)", id: 101 + $0)] }])
        case "GetPlaylistByUin":
            return try MusicAccountFixture.rpc(["v_playlist": [["id": 501, "dirid": 7, "uin": 101, "title": "合成歌单"]],
                                                "bFinish": 1, "total": 1])
        case "CgiGetPlaylistFavInfo":
            return try MusicAccountFixture.rpc(["v_list": [["id": 502, "dirid": 8, "uin": 999, "title": "合成订阅"]],
                                                "v_failTids": [], "total": 1, "hasmore": 0, "hide": false])
        case "CgiGetDiss":
            let params = try MusicQQWire.object(MusicQQWire.object(Self.fields(request)["req_0"])["param"])
            let id = try MusicQQWire.integer(params["disstid"]), dir = try MusicQQWire.integer(params["dirid"])
            return try MusicAccountFixture.rpc(["songlist": [MusicAccountFixture.song()], "total_song_num": 1,
                "songlist_size": 1, "hasmore": 0, "dirinfo": ["tid": id, "dirId": dir, "creator": ["musicid": id == 502 ? 999 : 101]]])
        case "vip_login_base":
            return try MusicAccountFixture.rpc(["svip": 1, "userinfo": ["expire": clock.now().timeIntervalSince1970 + 300]])
        case "CgiGetTrackInfo": return try MusicAccountFixture.rpc(["tracks": [MusicAccountFixture.song()]])
        case "UrlGetVkey":
            let params = try MusicQQWire.object(MusicQQWire.object(Self.fields(request)["req_0"])["param"])
            let file = try MusicQQWire.string((params["filename"] as? [String])?.first)
            return try MusicAccountFixture.rpc(["expiration": 3600, "midurlinfo":
                [["songmid": MusicAccountFixture.mid, "filename": file, "result": 0, "purl": file + "?vkey=synthetic-media-grant", "ekey": ""]]])
        default: throw MusicServiceError.invalidRequest
        }
    }
}

struct MusicNativeFixture {
    let identity: AccountIdentity
    let store = MusicAccountTestStore()
    let clock = MusicAccountTestClock()
    let http: MusicAccountTestHTTP
    let accounts: AccountSessionCoordinator
    let service: NativeMusicAccountService
    init(restored: Bool = true) throws {
        identity = try MusicAccountFixture.identity()
        http = MusicAccountTestHTTP(clock: clock)
        accounts = AccountSessionCoordinator(store: store)
        service = NativeMusicAccountService(accounts: accounts, scopes: [.qq: identity],
                                            transport: http, clock: { [clock] in clock.now() })
        if restored { try store.write(CredentialRecord(credential: MusicAccountFixture.credential()), for: identity) }
    }
    func enable(_ generation: Int = 1) async throws {
        _ = try await service.setAccess(available: true, generation: generation)
    }
    func invoke(_ operation: String, _ fields: [String: JSONValue] = [:]) async throws -> [String: JSONValue] {
        // A fixture-only assertion envelope over the production native guard.
        // No method registry, RPC transport or script runtime is involved.
        do {
            var input = fields
            let requested = try input.removeValue(forKey: "operation").map {
                try MusicPlatformJSON.string($0, maximum: 32)
            } ?? operation
            var reply = try await service.nativeBusiness(requested, platform: .qq, fields: input)
            reply["version"] = .number(1); reply["ok"] = .bool(true)
            return reply
        } catch let failure as MusicServiceError {
            return ["version": .number(1), "ok": .bool(false), "code": .string(failure.rawValue)]
        }
    }
}
