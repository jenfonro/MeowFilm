import Foundation
import XCTest
import MeowAccounts
import MeowContracts
import MeowJSON
import MeowMusicFeature
@testable import MeowMusicPlatform

private actor TransferProvider: MusicAccountProvider {
    var verified = 0
    var gate: MusicAccountTestGate?
    var failure: MusicServiceError?
    var wrongIdentity = false
    func configure(gate: MusicAccountTestGate? = nil, failure: MusicServiceError? = nil, wrongIdentity: Bool = false) {
        self.gate = gate; self.failure = failure; self.wrongIdentity = wrongIdentity
    }
    func count() -> Int { verified }
    func nativeID(_ credential: AccountCredential) throws -> String {
        let root = try MusicQQWire.decode(credential.dataRepresentation())
        if root["platform"] as? String == "tx" { return try MusicQQWire.id(MusicQQWire.object(root["auth"])["musicid"]) }
        return try MusicQQWire.id(root["accountID"])
    }
    func verify(_ credential: AccountCredential) async throws -> MusicQQVerified {
        verified += 1
        if let gate { await gate.wait() }
        if let failure { throw failure }
        return .init(credential: credential, nickname: "合成账号", nativeID: wrongIdentity ? "99999" : try nativeID(credential))
    }
    func refresh(_ credential: AccountCredential) async throws -> MusicQQVerified { try await verify(credential) }
    func createQR(id: UUID) throws -> MusicLoginChallenge { throw MusicServiceError.unsupported }
    func pollQR(id: UUID) throws -> MusicQQLoginResult { throw MusicServiceError.unsupported }
    func cancelQR(_ id: UUID) {}
    func revoke() {}
    func recommendations(kind: String, page: Int, credential: AccountCredential) -> MusicQQRecommendation { .init(rows: [], nextPage: nil) }
    func collectionRows(credential: AccountCredential) -> [MeowJSON.JSONValue] { [] }
    func tracks(collectionID: String, credential: AccountCredential) -> [MeowJSON.JSONValue] { [] }
    func capabilities(mid: String, credential: AccountCredential) -> [String] { [] }
    func resolve(mid: String, quality: String, credential: AccountCredential) throws -> MeowJSON.JSONValue { throw MusicServiceError.unsupported }
}

private struct TransferFixture {
    let store = MusicAccountTestStore()
    let coordinator: AccountSessionCoordinator
    let providers: [MusicAccountPlatform: TransferProvider]
    let scopes: [MusicAccountPlatform: AccountIdentity]
    let owner: NativeMusicAccountService
    init() throws {
        coordinator = AccountSessionCoordinator(store: store)
        providers = Dictionary(uniqueKeysWithValues: MusicAccountPlatform.allCases.map { ($0, TransferProvider()) })
        scopes = try Dictionary(uniqueKeysWithValues: MusicAccountPlatform.allCases.map { ($0, try MusicAccountFixture.identity($0.rawValue)) })
        owner = NativeMusicAccountService(accounts: coordinator, scopes: scopes, providers: providers.mapValues { $0 as any MusicAccountProvider })
    }
    func enable() async throws { _ = try await owner.setAccess(available: true, generation: 1) }
    func seed(_ row: MusicAccountTransferEntry) throws {
        try store.write(CredentialRecord(credential: MusicAccountTransferCodec.credential(row)), for: scopes[row.platform]!)
    }
}

final class MusicAccountTransferTests: XCTestCase {
    private func sharedEntries() throws -> [MusicAccountTransferEntry] {
        var root = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
        while !FileManager.default.fileExists(atPath: root.appendingPathComponent("tests/fixtures/music-account-transfer/accounts-v1.json").path) {
            let parent = root.deletingLastPathComponent()
            guard parent != root else { throw MusicAccountTransferError.invalidFile }
            root = parent
        }
        return try MusicAccountTransferCodec.decode(.init(fileData: Data(contentsOf:
            root.appendingPathComponent("tests/fixtures/music-account-transfer/accounts-v1.json"))))
    }
    private func file(_ entries: [MusicAccountTransferEntry]) throws -> MusicAccountFilePayload { try MusicAccountTransferCodec.encode(entries) }

    func testSharedThreePlatformFixturePreservesIdentityEveryFieldAndDeviceThroughNativeFormats() throws {
        let rows = try sharedEntries()
        XCTAssertEqual(rows.map(\.platform), [.netease, .qq, .kugou])
        XCTAssertEqual(rows[0].fields["_ntes_nnid"], "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb,1700000000000")
        XCTAssertEqual(rows[1].fields["expired_at"], "1800000000")
        XCTAssertEqual(rows[2].fields["t1"], "SYNTHETIC_T1_OPAQUE")
        var exported: [MusicAccountTransferEntry] = []
        for row in rows {
            let credential = try MusicAccountTransferCodec.credential(row)
            let restored = try MusicAccountTransferCodec.entry(credential, platform: row.platform)
            XCTAssertEqual(restored.userID, row.userID)
            XCTAssertEqual(restored.fields, row.fields)
            XCTAssertFalse(String(reflecting: restored).contains(row.fields.values.first!))
            exported.append(restored)
        }
        let decoded = try MusicAccountTransferCodec.decode(file(exported))
        XCTAssertEqual(decoded.map(\.userID), rows.map(\.userID))
        XCTAssertEqual(decoded.map(\.fields), rows.map(\.fields))
    }

    func testQQAliasesNeteaseMusicAAndKugouNumericT1KeepWireTypesAndExactValues() throws {
        let rows = try sharedEntries()
        var qq = rows[1].fields
        qq["musickeyCreateTime"] = "1700000000"; qq["keyExpiresIn"] = "86400"
        qq["loginType"] = qq.removeValue(forKey: "login_type")
        let q = MusicAccountTransferEntry(platform: .qq, userID: rows[1].userID, fields: qq)
        let qCredential = try MusicAccountTransferCodec.credential(q)
        let qResult = try MusicAccountTransferCodec.entry(qCredential, platform: .qq)
        XCTAssertEqual(qResult.fields["musickey_create_time"], "1700000000")
        XCTAssertNil(qResult.fields["musickeyCreateTime"])
        let auth = try MusicQQWire.object(MusicQQWire.decode(qCredential.dataRepresentation())["auth"])
        XCTAssertEqual(auth["key_expires_in"] as? Int, 86400)
        var wy = rows[0].fields; wy["MUSIC_A"] = "SYNTHETIC_MUSIC_A"
        let w = MusicAccountTransferEntry(platform: .netease, userID: "9007199254740993", fields: wy)
        XCTAssertEqual(try MusicAccountTransferCodec.entry(MusicAccountTransferCodec.credential(w), platform: .netease).fields, wy)
        XCTAssertEqual(try MusicAccountTransferCodec.entry(MusicAccountTransferCodec.credential(w), platform: .netease).userID, w.userID)
        var kg = rows[2].fields; kg["t1"] = "1700000000"
        let k = MusicAccountTransferEntry(platform: .kugou, userID: rows[2].userID, fields: kg)
        let kCredential = try MusicAccountTransferCodec.credential(k)
        let kAuth = try MusicQQWire.object(MusicQQWire.decode(kCredential.dataRepresentation())["auth"])
        XCTAssertEqual(kAuth["t1"] as? Int, 1700000000)
        XCTAssertEqual(try MusicAccountTransferCodec.entry(kCredential, platform: .kugou).fields, kg)
    }

    func testQQPassiveExportNormalizesOnlyPresentFieldsWithoutInventingOptionalValues() async throws {
        let clock = MusicAccountTestClock()
        let http = MusicAccountTestHTTP(clock: clock)
        let adapter = QQMusicAccountAdapter(transport: http, now: { clock.now() })
        let credential = try AccountCredential(data: MusicQQWire.json([
            "version": 1, "platform": "tx",
            "auth": ["musicid": 42, "musickey": "synthetic-key", "keyExpiresIn": 3600]
        ]))
        let exported = try await adapter.fileCredential(credential)
        let auth = try MusicQQWire.object(MusicQQWire.decode(exported.dataRepresentation())["auth"])
        XCTAssertEqual(auth["key_expires_in"] as? Int, 3600)
        XCTAssertNil(auth["keyExpiresIn"])
        let row = try MusicAccountTransferCodec.entry(exported, platform: .qq)
        XCTAssertEqual(row.fields, ["musicid": "42", "musickey": "synthetic-key", "key_expires_in": "3600"])
        let calls = await http.recorded()
        XCTAssertTrue(calls.isEmpty)
    }

    func testPassiveNeteaseExportDoesNotGenerateMissingClientDevice() async throws {
        let document = QQPublicPersistenceMemory(), clock = MusicAccountTestClock()
        let clients = NativeMusicClientService(documents: [.netease: document])
        try clients.setAccess(available: true, generation: 1)
        let http = MusicNeteaseTestHTTP(clock: clock)
        let adapter = NeteaseMusicAccountAdapter(transport: http, now: { clock.now() }, clientContexts: clients)
        let provider: any MusicAccountProvider = adapter
        var root = try MusicQQWire.decode(MusicNeteaseFixture.credential().dataRepresentation())
        root["device"] = [String: String]()
        let credential = try AccountCredential(data: MusicQQWire.json(root))
        do { _ = try await adapter.fileCredential(credential); XCTFail("Missing device cannot be invented") }
        catch { XCTAssertEqual(error as? MusicServiceError, .verificationRequired) }
        do { _ = try await provider.fileCredential(credential); XCTFail("Owner-facing export cannot invent a device") }
        catch { XCTAssertEqual(error as? MusicServiceError, .verificationRequired) }
        XCTAssertNil(document.peek()); XCTAssertEqual(document.counts().commits, 0)
        // Once another legitimate consumer creates the installation identity,
        // passive export must reuse it exactly, without generating or rewriting.
        let existing = try clients.installationDevice(.netease)
        let saved = document.peek(), commits = document.counts().commits
        let exported = try await provider.fileCredential(credential)
        let fields = try MusicAccountTransferCodec.entry(exported, platform: .netease).fields
        for (key, value) in existing.fields { XCTAssertEqual(fields[key], value) }
        XCTAssertEqual(document.peek(), saved); XCTAssertEqual(document.counts().commits, commits)
        let calls = await http.recorded()
        XCTAssertTrue(calls.isEmpty)
        clients.close()
    }

    func testNeteaseCandidatePatchesAssociatedDeviceOnlyAfterSuccessfulIdentityValidation() async throws {
        for succeeds in [false, true] {
            let credential = try MusicNeteaseFixture.credential()
            let root = try MusicQQWire.decode(credential.dataRepresentation())
            let originalDevice = try MusicQQWire.object(root["device"])
            let document = QQPublicPersistenceMemory(try MusicQQWire.json([
                "schema": 1, "platform": "wy", "device": originalDevice
            ]))
            let baseline = document.peek(), clock = MusicAccountTestClock()
            let clients = NativeMusicClientService(documents: [.netease: document])
            try clients.setAccess(available: true, generation: 1)
            let http = MusicNeteaseTestHTTP(clock: clock)
            let adapter = NeteaseMusicAccountAdapter(transport: http, now: { clock.now() }, clientContexts: clients)
            await http.replies("/weapi/w/nuser/account/get", [try MusicNeteaseFixture.response([
                "code": 200, "profile": ["userId": succeeds ? 42 : 99, "nickname": "合成账号"]
            ], headers: ["set-cookie": "NMTID=synthetic-validated-new; Path=/; Domain=music.163.com"])])
            if succeeds {
                let verified = try await adapter.verifyForImport(credential)
                let fileCredential = try await adapter.fileCredential(verified.credential)
                let row = try MusicAccountTransferCodec.entry(fileCredential, platform: .netease)
                XCTAssertEqual(row.fields["NMTID"], "synthetic-validated-new")
                XCTAssertEqual(try clients.existingDevice(.netease)?.fields["NMTID"], "synthetic-validated-new")
                XCTAssertEqual(document.counts().commits, 1)
            } else {
                do { _ = try await adapter.verifyForImport(credential); XCTFail("Wrong identity") } catch {}
                XCTAssertEqual(document.peek(), baseline); XCTAssertEqual(document.counts().commits, 0)
            }
            clients.close()
        }
    }

    func testKugouCandidateRegistrationDoesNotChangeInstallationOnFailedProfile() async throws {
        for succeeds in [false, true] {
            var root = try MusicQQWire.decode(MusicKugouFixture.credential().dataRepresentation())
            var device = try MusicQQWire.object(root["device"]); device.removeValue(forKey: "dfid")
            root["device"] = device
            let document = QQPublicPersistenceMemory(try MusicQQWire.json([
                "schema": 1, "platform": "kg", "device": device
            ]))
            let baseline = document.peek(), clock = MusicAccountTestClock()
            let clients = NativeMusicClientService(documents: [.kugou: document])
            try clients.setAccess(available: true, generation: 1)
            let http = MusicKugouTestHTTP(clock: clock)
            let adapter = KugouMusicAccountAdapter(transport: http, now: { clock.now() }, clientContexts: clients)
            if !succeeds {
                await http.respond("/v3/get_my_info", [try MusicKugouFixture.response([
                    "status": 1, "data": ["userid": "99", "nickname": "合成错误账号"]
                ])])
            }
            let credential = try AccountCredential(data: MusicQQWire.json(root))
            if succeeds {
                let verified = try await adapter.verifyForImport(credential)
                let fileCredential = try await adapter.fileCredential(verified.credential)
                let row = try MusicAccountTransferCodec.entry(fileCredential, platform: .kugou)
                XCTAssertEqual(row.fields["dfid"], "synthetic-dfid")
                XCTAssertEqual(try clients.existingDevice(.kugou)?.fields["dfid"], "synthetic-dfid")
                XCTAssertEqual(document.counts().commits, 1)
                let current = try XCTUnwrap(clients.existingDevice(.kugou))
                _ = try clients.update(current, fields: ["dfid": "synthetic-owner-latest"])
                let saved = document.peek(), commits = document.counts().commits
                let provider: any MusicAccountProvider = adapter
                let latest = try await provider.fileCredential(verified.credential)
                XCTAssertEqual(try MusicAccountTransferCodec.entry(latest, platform: .kugou).fields["dfid"],
                               "synthetic-owner-latest")
                XCTAssertEqual(document.peek(), saved); XCTAssertEqual(document.counts().commits, commits)
            } else {
                do { _ = try await adapter.verifyForImport(credential); XCTFail("Wrong identity") } catch {}
                XCTAssertEqual(document.peek(), baseline); XCTAssertEqual(document.counts().commits, 0)
            }
            clients.close()
        }
    }

    func testUTF8BOMPreservesSharedFixtureAndDoesNotBypassStrictJSON() throws {
        let rows = try sharedEntries()
        let bom = Data([0xEF, 0xBB, 0xBF])
        let decoded = try MusicAccountTransferCodec.decode(.init(fileData: bom + file(rows).dataForFile()))
        XCTAssertEqual(decoded.map(\.platform), rows.map(\.platform))
        XCTAssertEqual(decoded.map(\.userID), rows.map(\.userID))
        XCTAssertEqual(decoded.map(\.fields), rows.map(\.fields))
        let duplicate = Data(#"{"format":"meowfilm.music-accounts","version":1,"version":1,"accounts":[]}"#.utf8)
        XCTAssertThrowsError(try MusicAccountTransferCodec.decode(.init(fileData: bom + duplicate))) {
            XCTAssertEqual($0 as? MusicAccountTransferError, .invalidFile)
        }
    }

    func testVersionAcceptsNumericOneRegardlessOfJSONNumberNotation() throws {
        for version in ["1", "1.0", "1e0"] {
            let bytes = Data(#"{"format":"meowfilm.music-accounts","version":\#(version),"accounts":[]}"#.utf8)
            XCTAssertTrue(try MusicAccountTransferCodec.decode(.init(fileData: bytes)).isEmpty)
        }
        for version in ["1.5", "2", "true", #""1""#] {
            let bytes = Data(#"{"format":"meowfilm.music-accounts","version":\#(version),"accounts":[]}"#.utf8)
            XCTAssertThrowsError(try MusicAccountTransferCodec.decode(.init(fileData: bytes))) {
                XCTAssertEqual($0 as? MusicAccountTransferError, .invalidFile)
            }
        }
    }

    func testMalformedWholeFilesAreRejectedBeforeAnyOwnerIO() async throws {
        let fixture = try TransferFixture(); try await fixture.enable()
        let valid = try file(sharedEntries()).dataForFile()
        var root = try XCTUnwrap(JSONSerialization.jsonObject(with: valid) as? [String: Any])
        let rows = root["accounts"] as! [[String: Any]]
        var variants: [Data] = [Data("[]".utf8), Data("{\"format\":\"secret\",\"format\":\"meowfilm.music-accounts\",\"version\":1,\"accounts\":[]}".utf8),
            Data("{\"format\":\"meowfilm.music-accounts\",\"version\":1,\"accounts\":[{\"platform\":\"tx\",\"userId\":\"1\",\"credentials\":{\"musickey\":\"a\",\"music\\u006bey\":\"b\"}}]}".utf8)]
        for version in [2, true, "1"] as [Any] { root["version"] = version; variants.append(try JSONSerialization.data(withJSONObject: root)) }
        root["version"] = 1
        root["accounts"] = [rows[0], rows[0]]; variants.append(try JSONSerialization.data(withJSONObject: root))
        var badRow = rows[0]; badRow["platform"] = "kw"
        root["accounts"] = [badRow]; variants.append(try JSONSerialization.data(withJSONObject: root))
        badRow = rows[0]; badRow["credentials"] = ["MUSIC_U": 123]
        root["accounts"] = [badRow]; variants.append(try JSONSerialization.data(withJSONObject: root))
        for data in variants {
            do { _ = try await fixture.owner.prepareAccountImport(.init(fileData: data)); XCTFail("must reject") }
            catch { XCTAssertEqual(error as? MusicAccountTransferError, .invalidFile) }
        }
        XCTAssertThrowsError(try MusicAccountFilePayload(fileData: Data(repeating: 32, count: MusicAccountFilePayload.maximumBytes + 1)))
        XCTAssertTrue(fixture.store.calls().isEmpty)
        for provider in fixture.providers.values { let count = await provider.count(); XCTAssertEqual(count, 0) }
    }

    func testFreshImportAndExportRoundTripUseSavedCredentialsWithoutExportNetwork() async throws {
        let fixture = try TransferFixture(); try await fixture.enable()
        let rows = try sharedEntries()
        let plan = try await fixture.owner.prepareAccountImport(file(rows))
        XCTAssertTrue(plan.conflicts.isEmpty)
        let report = await fixture.owner.importAccounts(plan, policy: .overwrite)
        XCTAssertEqual(report.imported, MusicAccountPlatform.allCases)
        XCTAssertTrue(report.failed.isEmpty)
        let exported = try await fixture.owner.exportAccountFile()
        let values = try MusicAccountTransferCodec.decode(exported)
        XCTAssertEqual(values.map(\.fields), rows.map(\.fields))
        for platform in MusicAccountPlatform.allCases {
            let count = await fixture.providers[platform]!.count(); XCTAssertEqual(count, 1)
            XCTAssertNotNil(fixture.store.peek(fixture.scopes[platform]!))
        }
        let writes = fixture.store.calls().filter { $0 == "write" }.count
        XCTAssertEqual(writes, 3)
        let statuses = try await fixture.owner.statuses()
        XCTAssertTrue(statuses.allSatisfy { $0.phase == .signedIn })
    }

    func testColdSavedUnvalidatedCredentialsExportWithoutRestoreValidationOrWrites() async throws {
        let fixture = try TransferFixture(); let rows = try sharedEntries()
        for row in rows { try fixture.seed(row) }
        try await fixture.enable()
        let calls = fixture.store.calls().filter { $0 == "write" }.count
        let exported = try MusicAccountTransferCodec.decode(await fixture.owner.exportAccountFile())
        XCTAssertEqual(exported.map(\.fields), rows.map(\.fields))
        XCTAssertEqual(fixture.store.calls().filter { $0 == "write" }.count, calls)
        for provider in fixture.providers.values { let count = await provider.count(); XCTAssertEqual(count, 0) }
        let initial = await fixture.coordinator.snapshot(for: fixture.scopes[.qq]!)
        XCTAssertEqual(initial.phase, .signedOut, "export does not manufacture a live coordinator session")
    }

    func testOneBatchSkipSkipsAllConflictsWithoutNetworkOrWritesAndNextBatchAsksAgain() async throws {
        let fixture = try TransferFixture(); let rows = try sharedEntries()
        try fixture.seed(rows[0]); try fixture.seed(rows[2]); try await fixture.enable()
        let originals = [fixture.store.peek(fixture.scopes[.netease]!), fixture.store.peek(fixture.scopes[.kugou]!)]
        let plan = try await fixture.owner.prepareAccountImport(file(rows))
        XCTAssertEqual(plan.conflicts, [.netease, .kugou])
        let report = await fixture.owner.importAccounts(plan, policy: .skip)
        XCTAssertEqual(report.skipped, [.netease, .kugou]); XCTAssertEqual(report.imported, [.qq])
        XCTAssertEqual(fixture.store.peek(fixture.scopes[.netease]!), originals[0])
        XCTAssertEqual(fixture.store.peek(fixture.scopes[.kugou]!), originals[1])
        let count = await fixture.providers[.netease]!.count(); XCTAssertEqual(count, 0)
        let next = try await fixture.owner.prepareAccountImport(file(rows))
        XCTAssertEqual(next.conflicts, MusicAccountPlatform.allCases)
        await fixture.owner.discardAccountImport(next)
        XCTAssertEqual(fixture.store.calls().filter { $0 == "write" }.count, 3)
    }

    func testOverwriteKeepsInvalidAndIdentityMismatchOriginalsAndSummarizesOnce() async throws {
        let fixture = try TransferFixture(); let rows = try sharedEntries()
        for row in rows { try fixture.seed(row) }; try await fixture.enable()
        let qqOriginal = fixture.store.peek(fixture.scopes[.qq]!), kgOriginal = fixture.store.peek(fixture.scopes[.kugou]!)
        await fixture.providers[.qq]!.configure(failure: .authRequired)
        await fixture.providers[.kugou]!.configure(wrongIdentity: true)
        let plan = try await fixture.owner.prepareAccountImport(file(rows))
        let report = await fixture.owner.importAccounts(plan, policy: .overwrite)
        XCTAssertEqual(report.imported, [.netease]); XCTAssertEqual(report.failed, [.qq, .kugou])
        XCTAssertEqual(fixture.store.peek(fixture.scopes[.qq]!), qqOriginal)
        XCTAssertEqual(fixture.store.peek(fixture.scopes[.kugou]!), kgOriginal)
        XCTAssertFalse(report.message.contains("SYNTHETIC"))
        XCTAssertEqual(fixture.store.calls().filter { $0 == "remove" }.count, 0)
    }

    func testConfirmationBaselineChangeFailsWithoutVerifyingOrOverwritingNewAccount() async throws {
        let fixture = try TransferFixture(); let row = try sharedEntries()[1]
        try fixture.seed(row); try await fixture.enable()
        let plan = try await fixture.owner.prepareAccountImport(file([row]))
        let replacement = CredentialRecord(credential: try MusicAccountFixture.credential(909))
        try fixture.store.write(replacement, for: fixture.scopes[.qq]!)
        let report = await fixture.owner.importAccounts(plan, policy: .overwrite)
        XCTAssertEqual(report.failed, [.qq]); XCTAssertTrue(report.imported.isEmpty)
        XCTAssertEqual(fixture.store.peek(fixture.scopes[.qq]!), replacement)
        let count = await fixture.providers[.qq]!.count(); XCTAssertEqual(count, 0)
    }

    func testValidationDoesNotRevokeOldSessionAndConcurrentLogoutRejectsLateCandidate() async throws {
        let fixture = try TransferFixture(); let row = try sharedEntries()[1]
        try fixture.seed(row); try await fixture.enable()
        _ = try await fixture.owner.validate(platform: .qq)
        let original = fixture.store.peek(fixture.scopes[.qq]!)
        let gate = MusicAccountTestGate(); await fixture.providers[.qq]!.configure(gate: gate)
        let plan = try await fixture.owner.prepareAccountImport(file([row]))
        let task = Task { await fixture.owner.importAccounts(plan, policy: .overwrite) }
        await gate.started()
        let live = try await fixture.owner.statuses()
        XCTAssertEqual(live.first { $0.platform == .qq }?.phase, .signedIn)
        XCTAssertEqual(fixture.store.peek(fixture.scopes[.qq]!), original)
        try await fixture.owner.logout(platform: .qq)
        await gate.release()
        let report = await task.value
        XCTAssertEqual(report.failed, [.qq]); XCTAssertTrue(report.imported.isEmpty)
        XCTAssertNil(fixture.store.peek(fixture.scopes[.qq]!))
    }

    func testCancelledPlanAndCloseNeverSaveHeldValidationOrStartRemainingItems() async throws {
        for closeOwner in [false, true] {
            let fixture = try TransferFixture(); let rows = try sharedEntries()
            try await fixture.enable()
            let gate = MusicAccountTestGate(); await fixture.providers[.netease]!.configure(gate: gate)
            let plan = try await fixture.owner.prepareAccountImport(file(rows))
            let task = Task { await fixture.owner.importAccounts(plan, policy: .overwrite) }
            await gate.started()
            if closeOwner { _ = await fixture.owner.close() } else { plan.cancel(); task.cancel() }
            await gate.release()
            let report = await task.value
            XCTAssertTrue(report.imported.isEmpty); XCTAssertEqual(report.cancelled, MusicAccountPlatform.allCases)
            XCTAssertEqual(fixture.store.calls().filter { $0 == "write" }.count, 0)
            let count = await fixture.providers[.qq]!.count(); XCTAssertEqual(count, 0)
        }
    }

    func testStorageFailurePreservesOldRecordAndDoesNotReportSuccess() async throws {
        let fixture = try TransferFixture(); let row = try sharedEntries()[1]
        try fixture.seed(row); try await fixture.enable()
        let original = fixture.store.peek(fixture.scopes[.qq]!)
        let plan = try await fixture.owner.prepareAccountImport(file([row]))
        fixture.store.fail("write")
        let report = await fixture.owner.importAccounts(plan, policy: .overwrite)
        XCTAssertEqual(report.failed, [.qq]); XCTAssertTrue(report.imported.isEmpty)
        XCTAssertEqual(fixture.store.peek(fixture.scopes[.qq]!), original)
    }

    func testEmptyFileAndNoAccountsAreExplicitAndCancelledConflictHasNoSideEffects() async throws {
        let fixture = try TransferFixture(); try await fixture.enable()
        do { _ = try await fixture.owner.exportAccountFile(); XCTFail("no accounts") }
        catch { XCTAssertEqual(error as? MusicAccountTransferError, .noAccounts) }
        let empty = try await fixture.owner.prepareAccountImport(file([]))
        let report = await fixture.owner.importAccounts(empty, policy: .overwrite)
        XCTAssertEqual(report, MusicAccountImportSummary())
        let row = try sharedEntries()[1]; try fixture.seed(row)
        let plan = try await fixture.owner.prepareAccountImport(file([row]))
        XCTAssertEqual(plan.conflicts, [.qq]); plan.cancel()
        let cancelled = await fixture.owner.importAccounts(plan, policy: .overwrite)
        XCTAssertTrue(cancelled.imported.isEmpty); XCTAssertTrue(cancelled.failed.isEmpty)
        let count = await fixture.providers[.qq]!.count(); XCTAssertEqual(count, 0)
        XCTAssertEqual(fixture.store.calls().filter { $0 == "write" }.count, 1)
    }
    func testSuccessfulImportInvalidatesOldPlaybackQueueSyncAndCacheAuthorizations() async throws {
        let fixture = try MusicNativeFixture(); try await fixture.enable()
        _ = try await fixture.service.validate(platform: .qq)
        let before = await fixture.accounts.snapshot(for: fixture.identity)
        let cache = try await fixture.service.nativeCacheScope()
        let queue = try await fixture.service.beginAccountQueue(platform: .qq)
        let sync = try await fixture.service.beginLibrarySync(platform: .qq)
        let options = try await fixture.service.nativeOptions(platform: .qq, nativeID: "SyntheticMID", sourceKey: "tx:SyntheticMID")
        let selection = try XCTUnwrap(options.first).selection(quality: "128k")
        let row = try MusicAccountTransferCodec.entry(MusicAccountFixture.credential(), platform: .qq)
        let plan = try await fixture.service.prepareAccountImport(file([row]))
        let report = await fixture.service.importAccounts(plan, policy: .overwrite)
        XCTAssertEqual(report.imported, [.qq])
        let after = await fixture.accounts.snapshot(for: fixture.identity)
        XCTAssertNotEqual(before.generation, after.generation)
        do { _ = try await fixture.service.nativeResolve(platform: .qq, nativeID: "SyntheticMID", selection: selection); XCTFail("old playback") } catch {}
        do { try await fixture.service.checkAccountQueue(queue); XCTFail("old queue") } catch {}
        do { try await fixture.service.checkLibrarySync(sync); XCTFail("old sync") } catch {}
        do { try await fixture.service.checkNativeCacheScope(cache); XCTFail("old cache") } catch {}
        XCTAssertEqual(fixture.store.calls().filter { $0 == "remove" }.count, 0)
        _ = await fixture.service.close()
    }

    func testRealNeteaseImportKeepsMusicACommaDeviceAndOriginalOnWrongProfile() async throws {
        let fixture = try MusicNeteaseNativeFixture(); try await fixture.enable()
        var fields = try MusicAccountTransferCodec.entry(MusicNeteaseFixture.credential(), platform: .netease).fields
        fields["MUSIC_A"] = "synthetic-account-associated-a"
        let row = MusicAccountTransferEntry(platform: .netease, userID: "42", fields: fields)
        let plan = try await fixture.service.prepareAccountImport(file([row]))
        let report = await fixture.service.importAccounts(plan, policy: .overwrite)
        XCTAssertEqual(report.imported, [.netease])
        let exported = try MusicAccountTransferCodec.decode(await fixture.service.exportAccountFile())
        XCTAssertEqual(exported.first?.fields, fields)
        XCTAssertEqual(exported.first?.fields["_ntes_nnid"], "synthetic-nuid,1800000000000")
        let before = fixture.store.peek(fixture.identity)
        await fixture.http.replies("/weapi/w/nuser/account/get", [try MusicNeteaseFixture.response(
            ["code": 200, "profile": ["userId": 99, "nickname": "合成错误账号"]])])
        let next = try await fixture.service.prepareAccountImport(file([row]))
        let failed = await fixture.service.importAccounts(next, policy: .overwrite)
        XCTAssertEqual(failed.failed, [.netease]); XCTAssertEqual(fixture.store.peek(fixture.identity), before)
        let statuses = try await fixture.service.statuses()
        XCTAssertEqual(statuses.first?.phase, .signedIn)
        _ = await fixture.service.close()
    }

    func testQQRemoteStringIdentityMismatchRejectsImportAndKeepsOldSession() async throws {
        for container in ["root", "userInfo", "data"] {
            let fixture = try MusicNativeFixture(); try await fixture.enable()
            _ = try await fixture.service.validate(platform: .qq)
            let original = fixture.store.peek(fixture.identity)
            let writes = fixture.store.calls().filter { $0 == "write" }.count
            let before = await fixture.accounts.snapshot(for: fixture.identity)
            let row = try MusicAccountTransferCodec.entry(MusicAccountFixture.credential(), platform: .qq)
            let plan = try await fixture.service.prepareAccountImport(file([row]))
            let remote: [String: Any] = ["musicid": 101, "str_musicid": "999"]
            let response: [String: Any] = container == "root" ? remote : [container: remote]
            await fixture.http.replies("GetLoginUserInfo", try MusicAccountFixture.rpc(response))
            let report = await fixture.service.importAccounts(plan, policy: .overwrite)
            XCTAssertEqual(report.failed, [.qq]); XCTAssertTrue(report.imported.isEmpty)
            XCTAssertEqual(fixture.store.peek(fixture.identity), original)
            let after = await fixture.accounts.snapshot(for: fixture.identity)
            XCTAssertEqual(after, before)
            XCTAssertEqual(fixture.store.calls().filter { $0 == "write" }.count, writes)
            _ = await fixture.service.close()
        }
    }

    func testKugouImportRequiresRemoteIdentityWhileOrdinaryProfileMayOmitIt() async throws {
        let responses: [[String: Any]] = [
            ["nickname": "合成资料无身份"],
            ["userid": NSNull(), "nickname": "合成资料空身份"],
            ["userid": "99", "nickname": "合成资料错误身份"]
        ]
        for response in responses {
            let identity = try MusicAccountFixture.identity("kg"), store = MusicAccountTestStore()
            let clock = MusicAccountTestClock(), http = MusicKugouTestHTTP(clock: clock)
            let accounts = AccountSessionCoordinator(store: store)
            let service = NativeMusicAccountService(accounts: accounts, scopes: [.kugou: identity],
                transport: http, clock: { clock.now() })
            try store.write(CredentialRecord(credential: MusicKugouFixture.credential()), for: identity)
            _ = try await service.setAccess(available: true, generation: 1)
            // Missing userid remains valid for an already-owned session's ordinary profile.
            await http.respond("/v3/get_my_info", [try MusicKugouFixture.response([
                "status": 1, "data": ["nickname": "合成既有资料"]
            ])])
            _ = try await service.validate(platform: .kugou)
            let original = store.peek(identity), writes = store.calls().filter { $0 == "write" }.count
            let before = await accounts.snapshot(for: identity)
            let row = try MusicAccountTransferCodec.entry(MusicKugouFixture.credential(), platform: .kugou)
            let plan = try await service.prepareAccountImport(file([row]))
            await http.respond("/v3/get_my_info", [try MusicKugouFixture.response([
                "status": 1, "data": response
            ])])
            let report = await service.importAccounts(plan, policy: .overwrite)
            XCTAssertEqual(report.failed, [.kugou]); XCTAssertTrue(report.imported.isEmpty)
            XCTAssertEqual(store.peek(identity), original)
            let after = await accounts.snapshot(for: identity)
            XCTAssertEqual(after, before)
            XCTAssertEqual(store.calls().filter { $0 == "write" }.count, writes)
            let requests = await http.recorded()
            XCTAssertEqual(requests.map(\.url.path), ["/v3/get_my_info", "/v3/get_my_info"])
            _ = await service.close()
        }
    }

    func testInvalidPlatformFieldsFailOnlyThatItemWithoutNetworkAndCannotInventKugouDevice() async throws {
        let fixture = try TransferFixture(); let rows = try sharedEntries()
        try fixture.seed(rows[2]); try await fixture.enable()
        let original = fixture.store.peek(fixture.scopes[.kugou]!)
        var fields = rows[2].fields; fields["KUGOU_API_MID"] = "1"
        let invalid = MusicAccountTransferEntry(platform: .kugou, userID: rows[2].userID, fields: fields)
        let plan = try await fixture.owner.prepareAccountImport(file([rows[1], invalid]))
        let report = await fixture.owner.importAccounts(plan, policy: .overwrite)
        XCTAssertEqual(report.imported, [.qq]); XCTAssertEqual(report.failed, [.kugou])
        XCTAssertEqual(fixture.store.peek(fixture.scopes[.kugou]!), original)
        let count = await fixture.providers[.kugou]!.count(); XCTAssertEqual(count, 0)
        fields = rows[2].fields; fields.removeValue(forKey: "KUGOU_API_GUID")
        XCTAssertThrowsError(try MusicAccountTransferCodec.credential(.init(platform: .kugou, userID: rows[2].userID, fields: fields)))
    }

}
