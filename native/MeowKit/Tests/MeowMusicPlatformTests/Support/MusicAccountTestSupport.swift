import Foundation
import MeowAccounts
import MeowContracts
import MeowMusicFeature
import MeowRuntime
import MeowStorage
import MeowMusicTestSupport
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
    private let onEnter: @Sendable () -> Void
    private var entered = false, released = false
    private var starts: [CheckedContinuation<Void, Never>] = []
    private var waits: [CheckedContinuation<Void, Never>] = []
    init(_ onEnter: @escaping @Sendable () -> Void = {}) { self.onEnter = onEnter }
    func wait() async {
        if !entered { onEnter() }
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
    static func credential(_ uid: Int = 101, platform: MusicAccountPlatform = .qq) throws -> AccountCredential {
        let cookie: String
        switch platform {
        case .qq:
            cookie = "musicid=\(uid); musickey=synthetic-account-key; encrypt_uin=synthetic-euin-\(uid)"
        case .netease:
            cookie = "MUSIC_U=synthetic-user-\(uid); __csrf=synthetic-csrf; deviceId=synthetic-device; " +
                "_ntes_nuid=synthetic-nuid; _ntes_nnid=synthetic-nuid,1800000000000; WNMCID=synthetic.1800000000000.01.0"
        case .kugou:
            cookie = "userid=\(uid); token=synthetic-token; KUGOU_API_GUID=0123456789abcdef0123456789abcdef; " +
                "KUGOU_API_MID=176905054734845483393488264506404844848; KUGOU_API_DEV=0123456789; " +
                "KUGOU_API_MAC=02:00:00:00:00:00; dfid=synthetic-dfid"
        }
        return try MusicAccountCredential.make(platform: platform, userID: String(uid), cookie: cookie)
    }
    static func userID(_ credential: AccountCredential) throws -> String {
        try MusicAccountCredential.read(credential)[music: "userId"].musicRequiredText()
    }
    static func account(_ request: MusicPlatformFixture.Request) throws -> RuntimeValue {
        let candidate = request.candidate
        let fallback = try MusicAccountCredential.read(credential(platform:
            MusicAccountPlatform(rawValue: request.platform) ?? .qq))
        let uid = candidate[music: "userId"] == .null ? fallback[music: "userId"].musicText : candidate[music: "userId"].musicText
        let cookie = candidate[music: "cookie"] == .null ? fallback[music: "cookie"].musicText : candidate[music: "cookie"].musicText
        return .object(["userId": .string(uid), "cookie": .string(cookie),
            "profile": .object(["displayName": .string("合成昵称")])])
    }
    static func track(_ mid: String = MusicAccountFixture.mid, platform: String = "tx") throws -> MusicNativeTrackData {
        .init(platform: platform, data: try RuntimeJSON.encode(.object(["songmid": .string(mid),
            "name": .string("合成歌曲"), "singer": .string("合成歌手"), "duration": .number(180),
            "types": .array([.object(["type": .string("128k")])])])))
    }
}

actor MusicAccountRuntimePeer: MusicPlatformFixtureResponding {
    let clock: MusicAccountTestClock
    private var requests: [MusicPlatformFixture.Request] = []
    private var overrides: [String: RuntimeValue] = [:]
    private var failures: [String: String] = [:]
    private var block: (String, MusicAccountTestGate)?
    init(clock: MusicAccountTestClock) { self.clock = clock }
    func replies(_ operation: String, _ value: RuntimeValue) { overrides[operation] = value }
    func clearReply(_ operation: String) { overrides.removeValue(forKey: operation) }
    func fail(_ operation: String, code: String) { failures[operation] = code }
    func clearFailure(_ operation: String) { failures.removeValue(forKey: operation) }
    func hold(_ operation: String, _ gate: MusicAccountTestGate) { block = (operation, gate) }
    func recorded() -> [MusicPlatformFixture.Request] { requests }
    func respond(_ request: MusicPlatformFixture.Request) async throws -> RuntimeValue {
        requests.append(request)
        if let block, block.0 == request.operation { await block.1.wait() }
        if let code = failures[request.operation] { throw MusicPlatformFixture.Failure.remote(code) }
        if let value = overrides[request.operation] { return value }
        switch request.operation {
        case "account.verify", "account.refresh", "sms.login": return try MusicAccountFixture.account(request)
        case "account.profile":
            return .object(["profile": .object(["displayName": .string("合成昵称"),
                "avatar": .string("https://images.invalid/avatar.jpg")]),
                "entitlements": .object(["membership": .object(["vip": .bool(true)]),
                    "recheckAt": .number(clock.now().addingTimeInterval(300).timeIntervalSince1970 * 1000)])])
        case "qr.create":
            return .object(["url": .string("https://login.invalid/synthetic"),
                "expiresAt": .number(clock.now().addingTimeInterval(180).timeIntervalSince1970 * 1000)])
        case "qr.poll": return .object(["status": .string("authenticated"), "account": try MusicAccountFixture.account(request)])
        case "qr.cancel": return .object(["cancelled": .bool(true)])
        case "sms.send": return .object(["confirmed": .bool(true), "retryAfterSeconds": .number(60)])
        case "chorus": return .object(["point": .object(["start": .number(71.2), "end": .number(122.2)])])
        case "recommendations":
            return try MusicPlatformFixture.page([MusicAccountFixture.track(platform: request.platform)], platform: request.platform)
        case "personalPlaylists": return .object(["items": .array([.object(["id": .string("501"),
            "name": .string("合成歌单"), "count": .number(1), "owned": .bool(true),
            "action": .object(["type": .string("playlist"), "id": .string("501")])])]), "more": .bool(false)])
        case "playlist.complete": return try MusicPlatformFixture.page([MusicAccountFixture.track(platform: request.platform)], platform: request.platform)
        case "audio.options": return .object(["qualities": .array([.string("128k"), .string("320k"), .string("flac")])])
        case "audio": return .object(["url": .string("https://media.invalid/synthetic.mp3"),
            "requestQuality": request.parameters[music: "quality"],
            "actualQuality": request.parameters[music: "quality"], "full": .bool(true),
            "expiresAt": .number(clock.now().addingTimeInterval(120).timeIntervalSince1970 * 1000)])
        case "feedback.send":
            let start = request.parameters[music: "event"] == .string("start")
            return .object(["status": .string(start && request.platform == "tx" ? "registered" : "confirmed"),
                "sessionId": start ? .string(UUID().uuidString) : request.parameters[music: "sessionId"]])
        case "feedback.upload": return .object(["status": .string("confirmed")])
        case "feedback.release": return .object(["released": .bool(true)])
        default: throw MusicPlatformFixture.Failure.remote("UNSUPPORTED")
        }
    }
}

struct MusicNativeFixture {
    let accountPlatform: MusicAccountPlatform
    let identity: AccountIdentity
    let store = MusicAccountTestStore()
    let clock = MusicAccountTestClock()
    let platform: MusicAccountRuntimePeer
    let runtime: MusicPlatformFixture
    let accounts: AccountSessionCoordinator
    let service: NativeMusicAccountService
    init(platform accountPlatform: MusicAccountPlatform = .qq, restored: Bool = true,
         feedbackOutboxStoreFactory: (@Sendable () throws -> DocumentStore)? = nil) throws {
        self.accountPlatform = accountPlatform
        identity = try MusicAccountFixture.identity(accountPlatform.rawValue)
        platform = MusicAccountRuntimePeer(clock: clock)
        runtime = try MusicPlatformFixture { [platform] in try await platform.respond($0) }
        accounts = AccountSessionCoordinator(store: store)
        service = NativeMusicAccountService(accounts: accounts, scopes: [accountPlatform: identity],
            platformClient: runtime.makeClient(), feedbackOutboxStoreFactory: feedbackOutboxStoreFactory,
            clock: { [clock] in clock.now() })
        if restored { try store.write(CredentialRecord(credential:
            MusicAccountFixture.credential(platform: accountPlatform)), for: identity) }
    }
    func enable(_ generation: Int = 1) async throws {
        _ = try await service.setAccess(available: true, generation: generation)
    }
    func invoke(_ operation: String, _ fields: RuntimeObject = [:]) async throws -> RuntimeObject {
        do {
            var input = fields
            let requested = try input["operation"].map { try MusicAccountFields.string($0) } ?? operation
            input["operation"] = nil
            var reply = try await service.nativeBusiness(requested, platform: accountPlatform, fields: input)
            reply["version"] = .number(1); reply["ok"] = .bool(true)
            return reply
        } catch let failure as MusicServiceError {
            return ["version": .number(1), "ok": .bool(false), "code": .string(failure.rawValue)]
        }
    }
}

/// Owner lifecycle tests need a deliberately non-cooperative worker: cancelling
/// URLSession ends its task and cannot prove an injected worker has drained.
struct MusicNativeOwnerFixture {
    let identity: AccountIdentity
    let store = MusicAccountTestStore()
    let provider = MusicAccountTestProvider()
    let accounts: AccountSessionCoordinator
    let service: NativeMusicAccountService
    init() throws {
        identity = try MusicAccountFixture.identity()
        accounts = AccountSessionCoordinator(store: store)
        service = NativeMusicAccountService(accounts: accounts, scopes: [.qq: identity], providers: [.qq: provider])
        try store.write(CredentialRecord(credential: MusicAccountFixture.credential()), for: identity)
    }
    func enable() async throws { _ = try await service.setAccess(available: true, generation: 1) }
}

/// Synthetic provider for local credential-owner tests, with current credentials.
actor MusicAccountTestProvider: MusicAccountProvider, MusicAccountSMSProvider {
    private(set) var verifications = 0
    private(set) var refreshes = 0
    private(set) var began = 0
    private var gate: MusicAccountTestGate?
    private var failure: MusicServiceError?
    private var smsGate: MusicAccountTestGate?
    func configure(gate: MusicAccountTestGate? = nil, failure: MusicServiceError? = nil) {
        self.gate = gate; self.failure = failure
    }
    func counts() -> (verify: Int, refresh: Int, begin: Int) { (verifications, refreshes, began) }
    func holdSMS(_ gate: MusicAccountTestGate) { smsGate = gate }
    func sendSMS(countryCode: String, phone: String, attemptID: UUID) async -> MusicSMSSendReceipt {
        if let smsGate { await smsGate.wait() }
        return .init(retryAt: .distantFuture)
    }
    func loginSMS(countryCode: String, phone: String, code: String, attemptID: UUID) throws -> MusicAccountVerified {
        throw MusicServiceError.unsupported
    }
    func cancelSMS(_ id: UUID) {}
    func nativeID(_ credential: AccountCredential) throws -> String {
        try MusicAccountFixture.userID(credential)
    }
    func verify(_ credential: AccountCredential) async throws -> MusicAccountVerified {
        verifications += 1
        let pending = gate, failed = failure
        if let pending { await pending.wait() }
        if let failed { throw failed }
        return .init(credential: credential, nickname: "Synthetic", nativeID: try nativeID(credential))
    }
    func refresh(_ credential: AccountCredential) throws -> MusicAccountVerified {
        refreshes += 1
        return .init(credential: credential, nickname: "Synthetic", nativeID: try nativeID(credential))
    }
    func createQR(id: UUID) -> MusicLoginChallenge {
        began += 1
        return .init(id: id, platform: .qq, image: Data([0]), expiresAt: Date().addingTimeInterval(180))
    }
    func pollQR(id: UUID) -> MusicAccountLoginResult { .init(progress: .waiting, verified: nil) }
    func cancelQR(_ id: UUID) {}
    func revoke() {}
    func recommendations(kind: String, page: Int) -> MusicAccountRecommendation { .init(rows: [], nextPage: nil) }
    func collectionRows() -> [RuntimeValue] { [] }
    func tracks(collectionID: String) -> [RuntimeValue] { [] }
    func capabilities(mid: String) -> [String] { ["128k"] }
    func resolve(mid: String, quality: String) -> RuntimeValue { .null }
}
