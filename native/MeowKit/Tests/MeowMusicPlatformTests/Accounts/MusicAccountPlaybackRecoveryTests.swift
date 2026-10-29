import Foundation
import XCTest
import MeowAccounts
import MeowMusicFeature
import MeowJSON
@testable import MeowMusicPlatform

// Only synthetic stores/providers. No URLSession, real accounts or playback.
private actor RecoveryAccountProvider: MusicAccountProvider {
    let platform: MusicAccountPlatform
    var calls: [String] = []
    var qualities = ["128k", "320k"]
    var capabilityGate: MusicAccountTestGate?
    var resolveGate: MusicAccountTestGate?
    var verifyGate: MusicAccountTestGate?
    var capabilityError: MusicServiceError?
    init(_ platform: MusicAccountPlatform) { self.platform = platform }
    func captured() -> [String] { calls }
    func allow(_ values: [String]) { qualities = values }
    func holdCapabilities(_ value: MusicAccountTestGate) { capabilityGate = value }
    func holdResolve(_ value: MusicAccountTestGate) { resolveGate = value }
    func holdVerify(_ value: MusicAccountTestGate) { verifyGate = value }
    func failCapabilities(_ value: MusicServiceError) { capabilityError = value }
    func nativeID(_ credential: AccountCredential) throws -> String {
        try MusicQQWire.string(MusicQQWire.decode(credential.dataRepresentation())["uid"])
    }
    func verify(_ credential: AccountCredential) async throws -> MusicQQVerified {
        calls.append("verify")
        if let gate = verifyGate { verifyGate = nil; await gate.wait() }
        return .init(credential: credential, nickname: "合成账号", nativeID: try nativeID(credential))
    }
    func refresh(_ credential: AccountCredential) async throws -> MusicQQVerified {
        calls.append("refresh")
        return .init(credential: credential, nickname: "合成账号", nativeID: try nativeID(credential))
    }
    func createQR(id: UUID) -> MusicLoginChallenge {
        calls.append("qr")
        return .init(id: id, platform: platform, image: Data([1]), expiresAt: .distantFuture)
    }
    func pollQR(id: UUID) -> MusicQQLoginResult { .init(progress: .waiting, verified: nil) }
    func cancelQR(_ id: UUID) {}
    func revoke() {}
    func recommendations(kind: String, page: Int, credential: AccountCredential) throws -> MusicQQRecommendation {
        throw MusicServiceError.unsupported
    }
    func collectionRows(credential: AccountCredential) throws -> [JSONValue] { throw MusicServiceError.unsupported }
    func tracks(collectionID: String, credential: AccountCredential) throws -> [JSONValue] { throw MusicServiceError.unsupported }
    func capabilities(mid: String, credential: AccountCredential) async throws -> [String] {
        calls.append("capabilities:\(mid):\(try nativeID(credential))")
        let result = qualities, failure = capabilityError
        if let gate = capabilityGate { capabilityGate = nil; await gate.wait() }
        if let failure { throw failure }
        return result
    }
    func resolve(mid: String, quality: String, credential: AccountCredential) async throws -> JSONValue {
        calls.append("resolve:\(mid):\(quality):\(try nativeID(credential))")
        if let gate = resolveGate { resolveGate = nil; await gate.wait() }
        return .object(["url": .string("https://media.example.invalid/recovered.mp3"),
                        "qualityVerified": .bool(false), "expiresAt": .null])
    }
}

private struct RecoveryAccountFixture {
    let identity = try! MusicAccountFixture.identity("wy")
    let otherIdentity = try! MusicAccountFixture.identity("tx")
    let store = MusicUpdateStore()
    let clock = MusicAccountTestClock()
    let provider = RecoveryAccountProvider(.netease)
    let otherProvider = RecoveryAccountProvider(.qq)
    let service: NativeMusicAccountService
    init() throws {
        let accounts = AccountSessionCoordinator(store: store)
        store.seed(try MusicUpdateProvider.credential(), for: identity)
        store.seed(try MusicUpdateProvider.credential("other", uid: "99"), for: otherIdentity)
        service = .init(accounts: accounts, scopes: [.netease: identity, .qq: otherIdentity],
                        providers: [.netease: provider, .qq: otherProvider], clock: { [clock] in clock.now() })
    }
    func enable(_ generation: Int = 1) async throws {
        _ = try await service.setAccess(available: true, generation: generation)
    }
    func selection() async throws -> MusicPlaybackSelection {
        let options = try await service.nativeOptions(platform: .netease, nativeID: "101", sourceKey: "official:wy:101")
        return try XCTUnwrap(options.first).selection(quality: "320k")
    }
}

@MainActor final class MusicAccountPlaybackRecoveryTests: XCTestCase {
    private let rejected = URL(string: "https://media.example.invalid/rejected.mp3")!
    private func waitForCapabilityReaders(_ count: Int, service: NativeMusicAccountService) async -> Bool {
        for _ in 0..<10_000 {
            if await service.pendingCapabilityReaderCount == count { return true }
            await Task.yield()
        }
        return false
    }

    func testInspectorAndRecoveryShareCapabilityReadWithoutSharingCancellation() async throws {
        let f = try RecoveryAccountFixture(); try await f.enable()
        let selection = try await f.selection(), gate = MusicAccountTestGate()
        await f.provider.holdCapabilities(gate)
        let inspector = Task {
            try await f.service.nativeOptions(platform: .netease, nativeID: "101", sourceKey: selection.sourceKey)
        }
        await gate.started()
        let recovery = Task {
            try await f.service.nativeRecoveryOptions(platform: .netease, nativeID: "101", selection: selection)
        }
        let joined = await waitForCapabilityReaders(2, service: f.service)
        XCTAssertTrue(joined, "Both consumers must join before cancelling the inspector")
        inspector.cancel()
        do { _ = try await inspector.value; XCTFail("Cancelled inspector published") } catch {}
        await gate.release()
        let values = try await recovery.value
        XCTAssertEqual(values.first?.selection(quality: "320k"), selection)
        let calls = await f.provider.captured()
        XCTAssertEqual(calls.filter { $0.hasPrefix("capabilities:") }, ["capabilities:101:42", "capabilities:101:42"])
        _ = try await f.service.nativeRefreshAudio(platform: .netease, nativeID: "101",
                                                  selection: selection, rejectedURL: rejected)
        _ = await f.service.close()
    }

    func testSharedCapabilityReadCannotPublishToEitherConsumerAfterLogout() async throws {
        let f = try RecoveryAccountFixture(); try await f.enable()
        let selection = try await f.selection(), gate = MusicAccountTestGate()
        await f.provider.holdCapabilities(gate)
        let inspector = Task {
            try await f.service.nativeOptions(platform: .netease, nativeID: "101", sourceKey: selection.sourceKey)
        }
        await gate.started()
        let recovery = Task {
            try await f.service.nativeRecoveryOptions(platform: .netease, nativeID: "101", selection: selection)
        }
        let joined = await waitForCapabilityReaders(2, service: f.service)
        XCTAssertTrue(joined)
        try await f.service.logout(platform: .netease)
        await gate.release()
        do { _ = try await inspector.value; XCTFail("Logged-out inspector published") } catch {}
        do { _ = try await recovery.value; XCTFail("Logged-out recovery published") } catch {}
        let calls = await f.provider.captured()
        XCTAssertEqual(calls.filter { $0.hasPrefix("capabilities:") }.count, 2)
        XCTAssertFalse(calls.contains { $0.hasPrefix("resolve:") })
        _ = await f.service.close()
    }

    func testDifferentRecordingDoesNotJoinSuspendedCapabilityRead() async throws {
        let f = try RecoveryAccountFixture(); try await f.enable()
        _ = try await f.selection()
        let gate = MusicAccountTestGate(); await f.provider.holdCapabilities(gate)
        let first = Task {
            try await f.service.nativeOptions(platform: .netease, nativeID: "101", sourceKey: "official:wy:101")
        }
        await gate.started()
        let second = Task {
            try await f.service.nativeOptions(platform: .netease, nativeID: "102", sourceKey: "official:wy:102")
        }
        // Release only after observing a second physical provider call.
        var secondEntered = false
        for _ in 0..<10_000 {
            if await f.provider.captured().contains("capabilities:102:42") { secondEntered = true; break }
            await Task.yield()
        }
        XCTAssertTrue(secondEntered)
        await gate.release()
        let a = try await first.value, b = try await second.value
        XCTAssertEqual(a.first?.sourceKey, "official:wy:101")
        XCTAssertEqual(b.first?.sourceKey, "official:wy:102")
        _ = await f.service.close()
    }

    private func changed(_ old: MusicPlaybackSelection, installation: String? = nil,
                         generation: Int? = nil, site: String? = nil) -> MusicPlaybackSelection {
        .init(installationId: installation ?? old.installationId, generation: generation ?? old.generation,
              siteId: site ?? old.siteId, sourceKey: old.sourceKey, quality: old.quality)
    }
    private func refuseBoth(_ service: NativeMusicAccountService, _ selection: MusicPlaybackSelection,
                            expected: MusicServiceError = .staleResult,
                            file: StaticString = #filePath, line: UInt = #line) async {
        // The existential is also the conformance contract consumed by mainline.
        let port: any MusicNativePlaybackRecoveryAuthorizing = service
        do {
            _ = try await port.nativeRecoveryOptions(platform: .netease, nativeID: "101", selection: selection)
            XCTFail("Rejected binding produced recovery options", file: file, line: line)
        } catch { XCTAssertEqual(error as? MusicServiceError, expected, file: file, line: line) }
        do {
            _ = try await port.nativeRefreshAudio(platform: .netease, nativeID: "101", selection: selection, rejectedURL: rejected)
            XCTFail("Rejected binding resolved audio", file: file, line: line)
        } catch { XCTAssertEqual(error as? MusicServiceError, expected, file: file, line: line) }
    }

    func testFirstAwaitAdmissionRejectsWrongSelectionWithoutStoreOrProvider() async throws {
        let f = try RecoveryAccountFixture(); try await f.enable()
        let selected = try await f.selection(), reads = f.store.counts().reads
        let before = await f.provider.captured()
        await refuseBoth(f.service, changed(selected, installation: "official:foreign"))
        await refuseBoth(f.service, changed(selected, generation: selected.generation + 1))
        await refuseBoth(f.service, changed(selected, site: "tx"), expected: .targetChanged)
        XCTAssertEqual(f.store.counts().reads, reads)
        let after = await f.provider.captured(), other = await f.otherProvider.captured()
        XCTAssertEqual(after, before); XCTAssertTrue(other.isEmpty)
        _ = await f.service.close()
    }

    func testColdOwnerLockLogoutAndNewAccountNeverRestoreForOldSelection() async throws {
        let f = try RecoveryAccountFixture(); try await f.enable()
        let old = try await f.selection()
        let cold = try RecoveryAccountFixture(); try await cold.enable()
        await refuseBoth(cold.service, old)
        XCTAssertEqual(cold.store.counts().reads, 0)
        let coldCalls = await cold.provider.captured(); XCTAssertTrue(coldCalls.isEmpty)
        _ = await cold.service.close()

        _ = try await f.service.setAccess(available: false, generation: 2)
        try await f.enable(3)
        var reads = f.store.counts().reads
        var calls = await f.provider.captured()
        await refuseBoth(f.service, old)
        XCTAssertEqual(f.store.counts().reads, reads)
        var captured = await f.provider.captured(); XCTAssertEqual(captured, calls)

        let current = try await f.selection()
        try await f.service.logout(platform: .netease)
        reads = f.store.counts().reads; calls = await f.provider.captured()
        await refuseBoth(f.service, current)
        XCTAssertEqual(f.store.counts().reads, reads)
        captured = await f.provider.captured(); XCTAssertEqual(captured, calls)

        f.store.seed(try MusicUpdateProvider.credential("new-account", uid: "99"), for: f.identity)
        _ = try await f.selection()
        reads = f.store.counts().reads; calls = await f.provider.captured()
        await refuseBoth(f.service, current)
        XCTAssertEqual(f.store.counts().reads, reads)
        captured = await f.provider.captured(); XCTAssertEqual(captured, calls, "Must not ask the new account about the old selection")
        _ = await f.service.close()
    }

    func testPendingImportAndSigningInRefuseBeforeCredentialRead() async throws {
        let f = try RecoveryAccountFixture(); try await f.enable()
        let selected = try await f.selection(), id = UUID()
        _ = try await f.service.previewCredentialImport(MusicCredentialImportFixture.input(.netease),
            platform: .netease, format: .iwebPlusSnapshot, requestID: id)
        let reads = f.store.counts().reads, calls = await f.provider.captured()
        await refuseBoth(f.service, selected)
        XCTAssertEqual(f.store.counts().reads, reads)
        let captured = await f.provider.captured(); XCTAssertEqual(captured, calls)
        await f.service.cancelCredentialImport(id: id)
        let qr = try await f.service.beginLogin(platform: .netease)
        // Validated was removed by beginLogin; even the guessed new revision is
        // not permission to read the store or start session()/capabilities.
        let unvalidated = changed(selected, generation: selected.generation + 1)
        let afterQRReads = f.store.counts().reads, afterQRCalls = await f.provider.captured()
        await refuseBoth(f.service, unvalidated)
        XCTAssertEqual(f.store.counts().reads, afterQRReads)
        let after = await f.provider.captured(); XCTAssertEqual(after, afterQRCalls)
        await f.service.cancelLogin(challengeID: qr.id)
        _ = await f.service.close()
    }

    func testRecoveryPinsSourceNativeIDQualityAndRenewsOnly120SecondGrant() async throws {
        let f = try RecoveryAccountFixture(); try await f.enable()
        let selected = try await f.selection(), before = await f.provider.captured()
        let options = try await f.service.nativeRecoveryOptions(platform: .netease, nativeID: "101", selection: selected)
        XCTAssertEqual(options.count, 1)
        let option = try XCTUnwrap(options.first)
        XCTAssertEqual(option.qualities, ["320k"]); XCTAssertEqual(option.selection(quality: "320k"), selected)
        let calls = await f.provider.captured()
        XCTAssertEqual(Array(calls.dropFirst(before.count)), ["capabilities:101:42"])
        let others = await f.otherProvider.captured(); XCTAssertTrue(others.isEmpty)
        let audio = try await f.service.nativeRefreshAudio(platform: .netease, nativeID: "101", selection: selected, rejectedURL: rejected)
        XCTAssertNotEqual(audio.url, rejected)
        f.clock.advance(121)
        let reads = f.store.counts().reads, resolved = await f.provider.captured()
        do {
            _ = try await f.service.nativeRefreshAudio(platform: .netease, nativeID: "101", selection: selected, rejectedURL: rejected)
            XCTFail("Expired grant reused")
        } catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        XCTAssertEqual(f.store.counts().reads, reads)
        let after = await f.provider.captured(); XCTAssertEqual(after, resolved)
        _ = await f.service.close()
    }

    func testRevokedQualityDoesNotDowngradeOrLeaveEarlierGrant() async throws {
        let f = try RecoveryAccountFixture(); try await f.enable()
        let selected = try await f.selection()
        await f.provider.allow(["128k"])
        do {
            _ = try await f.service.nativeRecoveryOptions(platform: .netease, nativeID: "101", selection: selected)
            XCTFail("Downgraded recovery")
        } catch { XCTAssertEqual(error as? MusicServiceError, .qualityDisabled) }
        do {
            _ = try await f.service.nativeRefreshAudio(platform: .netease, nativeID: "101", selection: selected, rejectedURL: rejected)
            XCTFail("Earlier grant survived quality revocation")
        } catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        let calls = await f.provider.captured()
        XCTAssertEqual(calls.filter { $0.hasPrefix("capabilities:") }.count, 2)
        XCTAssertFalse(calls.contains { $0.hasPrefix("resolve:") })
        _ = await f.service.close()
    }

    func testSuspendedCapabilitiesCannotPublishAfterImportLockLogoutOrCancellation() async throws {
        for mode in ["import", "lock", "logout", "cancel"] {
            let f = try RecoveryAccountFixture(); try await f.enable()
            let selected = try await f.selection(), gate = MusicAccountTestGate(), id = UUID()
            await f.provider.holdCapabilities(gate)
            let pending = Task { try await f.service.nativeRecoveryOptions(platform: .netease, nativeID: "101", selection: selected) }
            await gate.started()
            switch mode {
            case "import":
                _ = try await f.service.previewCredentialImport(MusicCredentialImportFixture.input(.netease),
                    platform: .netease, format: .iwebPlusSnapshot, requestID: id)
            case "lock": _ = try await f.service.setAccess(available: false, generation: 2)
            case "logout": try await f.service.logout(platform: .netease)
            default: pending.cancel()
            }
            await gate.release()
            do { _ = try await pending.value; XCTFail("Late recovery published: \(mode)") } catch {}
            if mode == "import" { await f.service.cancelCredentialImport(id: id) }
            do {
                _ = try await f.service.nativeRefreshAudio(platform: .netease, nativeID: "101", selection: selected, rejectedURL: rejected)
                XCTFail("Late grant survived: \(mode)")
            } catch {}
            let calls = await f.provider.captured()
            XCTAssertEqual(calls.filter { $0.hasPrefix("capabilities:") }.count, 2)
            XCTAssertFalse(calls.contains { $0.hasPrefix("resolve:") })
            _ = await f.service.close()
        }
    }

    func testCredentialRevisionReplacementBeforeAndDuringCapabilitiesIsRejected() async throws {
        for during in [false, true] {
            let f = try RecoveryAccountFixture(); try await f.enable()
            let selected = try await f.selection(), gate = MusicAccountTestGate()
            if during { await f.provider.holdCapabilities(gate) }
            if !during { f.store.seed(try MusicUpdateProvider.credential("external"), for: f.identity) }
            let pending = Task { try await f.service.nativeRecoveryOptions(platform: .netease, nativeID: "101", selection: selected) }
            if during {
                await gate.started()
                f.store.seed(try MusicUpdateProvider.credential("external"), for: f.identity)
                await gate.release()
            }
            do { _ = try await pending.value; XCTFail("Replaced credential authorized recovery") } catch {}
            let calls = await f.provider.captured()
            XCTAssertEqual(calls.filter { $0.hasPrefix("capabilities:") }.count, during ? 2 : 1)
            XCTAssertEqual(calls.filter { $0 == "verify" }.count, 1, "No automatic revalidation")
            _ = await f.service.close()
        }
    }

    func testSuspendedURLCannotReturnAfterAccountChange() async throws {
        let f = try RecoveryAccountFixture(); try await f.enable()
        let selected = try await f.selection()
        _ = try await f.service.nativeRecoveryOptions(platform: .netease, nativeID: "101", selection: selected)
        let gate = MusicAccountTestGate(); await f.provider.holdResolve(gate)
        let pending = Task { try await f.service.nativeRefreshAudio(platform: .netease, nativeID: "101",
                                                                   selection: selected, rejectedURL: rejected) }
        await gate.started()
        try await f.service.logout(platform: .netease)
        await gate.release()
        do { _ = try await pending.value; XCTFail("Late URL returned") } catch {}
        let calls = await f.provider.captured()
        XCTAssertEqual(calls.filter { $0.hasPrefix("resolve:") }, ["resolve:101:320k:42"])
        _ = await f.service.close()
    }
}
