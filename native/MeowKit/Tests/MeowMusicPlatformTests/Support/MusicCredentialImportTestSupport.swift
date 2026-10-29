import Foundation
import XCTest
import MeowAccounts
import MeowMusicFeature
import MeowJSON
@testable import MeowMusicPlatform

enum MusicCredentialImportFixture {
    static let seed = "0123456789abcdef0123456789abcdef"
    static func snapshot(_ platform: MusicAccountPlatform, uid: String = "42") -> [String: Any] {
        switch platform {
        case .qq:
            return ["__private_qq_session": ["revision": "synthetic-revision",
                "credential": ["musicid": uid, "str_musicid": uid, "musickey": "synthetic-import-key"]]]
        case .netease:
            return ["__private_netease_session": "MUSIC_U=synthetic-import-key; __csrf=synthetic-csrf; deviceId=synthetic-device",
                    "__private_netease_cloud_scope": "synthetic-scope",
                    "__private_netease_profile_v1": ["scope": "synthetic-scope", "loggedIn": true,
                                                     "profile": ["userId": uid, "nickname": "not-in-preview"]]]
        case .kugou:
            return ["__private_kugou_session": ["scope": "synthetic-scope",
                                               "cookie": ["userid": uid, "token": "synthetic-import-key", "t1": "1"]],
                    "__private_kugou_module": ["deviceSeed": seed],
                    "__private_kugou_device": ["dfid": "synthetic-dfid"]]
        }
    }
    static func bytes(_ platform: MusicAccountPlatform = .qq, uid: String = "42") throws -> Data {
        try JSONSerialization.data(withJSONObject: snapshot(platform, uid: uid), options: .sortedKeys)
    }
    static func input(_ platform: MusicAccountPlatform = .qq, uid: String = "42") throws -> MusicCredentialImportInput {
        try .init(data: bytes(platform, uid: uid))
    }
    static func material(_ platform: MusicAccountPlatform = .qq, uid: String = "42") throws -> MusicCredentialImportMaterial {
        try MusicCredentialImportParser.parse(bytes(platform, uid: uid), platform: platform, format: .iwebPlusSnapshot)
    }
}

actor MusicCredentialImportProvider: MusicAccountProvider {
    private(set) var verifications = 0
    private(set) var refreshes = 0
    private(set) var began = 0
    private var gate: MusicAccountTestGate?
    func holdCapabilities(_ value: MusicAccountTestGate) { gate = value }
    func counts() -> (verify: Int, refresh: Int, begin: Int) { (verifications, refreshes, began) }
    func nativeID(_ credential: AccountCredential) throws -> String {
        let root = try MusicQQWire.decode(credential.dataRepresentation())
        guard root["kind"] == nil else { throw MusicServiceError.invalidResponse }
        if root["platform"] as? String == "tx" {
            return try MusicQQWire.id(MusicQQWire.object(root["auth"])["musicid"])
        }
        return try MusicQQWire.id(root["accountID"])
    }
    func verify(_ credential: AccountCredential) throws -> MusicQQVerified {
        verifications += 1
        return .init(credential: credential, nickname: "Synthetic", nativeID: try nativeID(credential))
    }
    func refresh(_ credential: AccountCredential) throws -> MusicQQVerified {
        refreshes += 1
        return .init(credential: credential, nickname: "Synthetic", nativeID: try nativeID(credential))
    }
    func createQR(id: UUID) -> MusicLoginChallenge {
        began += 1
        return .init(id: id, platform: .qq, image: Data([0]), expiresAt: Date().addingTimeInterval(180))
    }
    func pollQR(id: UUID) -> MusicQQLoginResult { .init(progress: .waiting, verified: nil) }
    func cancelQR(_ id: UUID) {}
    func revoke() {}
    func recommendations(kind: String, page: Int, credential: AccountCredential) -> MusicQQRecommendation { .init(rows: [], nextPage: nil) }
    func collectionRows(credential: AccountCredential) -> [JSONValue] { [] }
    func tracks(collectionID: String, credential: AccountCredential) -> [JSONValue] { [] }
    func capabilities(mid: String, credential: AccountCredential) async -> [String] {
        if let gate { await gate.wait() }; return ["128k"]
    }
    func resolve(mid: String, quality: String, credential: AccountCredential) -> JSONValue { .null }
}

struct MusicCredentialImportOwnerFixture {
    let identity: AccountIdentity
    let otherIdentity: AccountIdentity
    let store = MusicUpdateStore()
    let provider = MusicCredentialImportProvider()
    let accounts: AccountSessionCoordinator
    let service: NativeMusicAccountService
    init() throws {
        identity = try MusicAccountFixture.identity()
        otherIdentity = try MusicAccountFixture.identity("wy")
        accounts = .init(store: store)
        service = .init(accounts: accounts, scopes: [.qq: identity, .netease: otherIdentity],
                        providers: [.qq: provider, .netease: provider])
    }
    func enable() async throws { _ = try await service.setAccess(available: true, generation: 1) }
    func preview(_ platform: MusicAccountPlatform = .qq, uid: String = "42") async throws -> MusicCredentialImportPreview {
        try await service.previewCredentialImport(MusicCredentialImportFixture.input(platform, uid: uid),
            platform: platform, format: .iwebPlusSnapshot, requestID: UUID())
    }
    func signedIn() async throws {
        store.seed(try MusicCredentialImportFixture.material().credential, for: identity)
        _ = try await service.validate(platform: .qq)
    }
}
