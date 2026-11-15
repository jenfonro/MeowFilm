import Foundation
import MeowAccounts
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicPlatform

actor MusicAccountSyncRuntimePeer: MusicPlatformFixtureResponding {
    let platform: MusicAccountPlatform
    private let account: MusicAccountRuntimePeer
    private var requests: [MusicPlatformFixture.Request] = []
    private var gate: (String, MusicAccountTestGate)?
    private var failures: [String: String] = [:]
    private var receipt: RuntimeValue = .object(["status": .string("confirmed")])
    private var specialName = "特殊自有歌单"
    private var subscribed = true
    private var relationID = "relation-502"
    private var targetCreator = "202"
    init(platform: MusicAccountPlatform, clock: MusicAccountTestClock) {
        self.platform = platform; account = MusicAccountRuntimePeer(clock: clock)
    }
    func hold(_ operation: String, gate: MusicAccountTestGate) { self.gate = (operation, gate) }
    func fail(_ operation: String, code: String) { failures[operation] = code }
    func writeReceipt(_ value: RuntimeValue) { receipt = value }
    func renameSpecial(_ value: String) { specialName = value }
    func setSubscribed(_ value: Bool) { subscribed = value }
    func setRelationID(_ value: String) { relationID = value }
    func setTargetCreator(_ value: String) { targetCreator = value }
    func recorded() -> [MusicPlatformFixture.Request] { requests }
    private func directory() -> [RuntimeValue] {
        [
            .object(["id": .string("501"), "name": .string("自有歌单"), "kind": .string("playlist"),
                "owned": .bool(true), "writable": .bool(true), "ownerId": .string("101"), "count": .number(3),
                "locator": .object(["listId": .string("501")])]),
            .object(["id": .string("503"), "name": .string(specialName), "kind": .string("playlist"),
                "owned": .bool(true), "writable": .bool(false), "ownerId": .string("101"), "count": .number(0),
                "locator": .object(["listId": .string("503")])])
        ] + (subscribed ? [.object(["id": .string("502"), "name": .string("订阅歌单"), "kind": .string("playlist"),
                "owned": .bool(false), "writable": .bool(false), "ownerId": .string("202"), "count": .number(1),
                "locator": .object(["listId": .string("502"), "creatorId": .string("202")])])] : [])
    }
    private func snapshot(_ request: MusicPlatformFixture.Request) throws -> RuntimeValue {
        let directory = directory()
        let collections: [RuntimeValue]
        if request.parameters[music: "subscriptionsOnly"].musicBool ||
            request.parameters[music: "kinds"].musicArray == [.string("favorites")] {
            collections = []
        } else {
            let members = try ["101", "101", "102"].enumerated().map { index, id -> RuntimeValue in
                .object(["id": .string(id), "key": .string("member-\(index)"), "sort": .number(Double(index)),
                    "song": try MusicPlatformFixture.song(MusicAccountFixture.track(id, platform: platform.rawValue))])
            }
            collections = [.object(["id": .string("501"), "name": .string("自有歌单"), "kind": .string("playlist"),
                "writable": .bool(true), "owner": .string("101"), "members": .array(members),
                "locator": .object(["listId": .string("501")])])]
        }
        let subscriptions: [RuntimeValue] = subscribed ? [.object([
            "id": .string("502"), "remoteId": .string("502"), "relationId": .string(relationID),
            "name": .string("订阅歌单"), "count": .number(1), "cover": .null,
            "locator": .object(["listId": .string("502"), "creatorId": .string("202")])
        ])] : []
        return .object(["accountId": .string("101"), "directory": .array(directory),
            "collections": .array(collections), "subscriptions": .array(subscriptions)])
    }
    func respond(_ request: MusicPlatformFixture.Request) async throws -> RuntimeValue {
        requests.append(request)
        if let gate, gate.0 == request.operation { await gate.1.wait() }
        if let code = failures[request.operation] { throw MusicPlatformFixture.Failure.remote(code) }
        switch request.operation {
        case "sync.read": return try snapshot(request)
        case "sync.prepare": return .object(["preparationId": .string(UUID().uuidString)])
        case "sync.target": return .object(["id": request.parameters[music: "id"],
            "name": request.parameters[music: "name"], "locator": .object(["creatorId": .string(targetCreator)])])
        case "sync.write": return receipt
        default: return try await account.respond(request)
        }
    }
}

struct MusicAccountSyncRuntimeFixture {
    let identity: AccountIdentity
    let store = MusicAccountTestStore()
    let clock = MusicAccountTestClock()
    let peer: MusicAccountSyncRuntimePeer
    let client: MusicPlatformClient
    let owner: NativeMusicAccountService
    let platform: MusicAccountPlatform
    init(platform: MusicAccountPlatform = .netease) throws {
        self.platform = platform
        identity = try MusicAccountFixture.identity(platform.rawValue)
        peer = MusicAccountSyncRuntimePeer(platform: platform, clock: clock)
        client = try peer.makeClient()
        owner = NativeMusicAccountService(accounts: .init(store: store), scopes: [platform: identity],
            platformClient: client, clock: { [clock] in clock.now() })
        try store.write(.init(credential: MusicAccountFixture.credential(platform: platform)), for: identity)
    }
    func begin() async throws -> MusicLibrarySyncSession {
        _ = try await owner.setAccess(available: true, generation: 1)
        return try await owner.beginLibrarySync(platform: platform)
    }
    func close() async throws { _ = await owner.close(); try await client.close() }
}
