import Foundation
import XCTest
import MeowAccounts
import MeowContracts
import MeowJSON
import MeowMusicFeature
import MeowRuntime
import MeowMusicTestSupport
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
        try MusicAccountFixture.userID(credential)
    }
    func verify(_ credential: AccountCredential) async throws -> MusicAccountVerified {
        verified += 1
        if let gate { await gate.wait() }
        if let failure { throw failure }
        return .init(credential: credential, nickname: "合成账号", nativeID: wrongIdentity ? "99999" : try nativeID(credential))
    }
    func refresh(_ credential: AccountCredential) async throws -> MusicAccountVerified { try await verify(credential) }
    func createQR(id: UUID) throws -> MusicLoginChallenge { throw MusicServiceError.unsupported }
    func pollQR(id: UUID) throws -> MusicAccountLoginResult { throw MusicServiceError.unsupported }
    func cancelQR(_ id: UUID) {}
    func revoke() {}
    func recommendations(kind: String, page: Int) -> MusicAccountRecommendation { .init(rows: [], nextPage: nil) }
    func collectionRows() -> [MeowJSON.JSONValue] { [] }
    func tracks(collectionID: String) -> [MeowJSON.JSONValue] { [] }
    func capabilities(mid: String) -> [String] { [] }
    func resolve(mid: String, quality: String) throws -> MeowJSON.JSONValue { throw MusicServiceError.unsupported }
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
        XCTAssertEqual(qResult.fields["key_expires_in"], "86400")
        var wy = rows[0].fields; wy["MUSIC_A"] = "SYNTHETIC_MUSIC_A"
        let w = MusicAccountTransferEntry(platform: .netease, userID: "9007199254740993", fields: wy)
        XCTAssertEqual(try MusicAccountTransferCodec.entry(MusicAccountTransferCodec.credential(w), platform: .netease).fields, wy)
        XCTAssertEqual(try MusicAccountTransferCodec.entry(MusicAccountTransferCodec.credential(w), platform: .netease).userID, w.userID)
        var kg = rows[2].fields; kg["t1"] = "1700000000"
        let k = MusicAccountTransferEntry(platform: .kugou, userID: rows[2].userID, fields: kg)
        let kCredential = try MusicAccountTransferCodec.credential(k)
        XCTAssertEqual(try MusicAccountTransferCodec.entry(kCredential, platform: .kugou).fields["t1"], "1700000000")
        XCTAssertEqual(try MusicAccountTransferCodec.entry(kCredential, platform: .kugou).fields, kg)
    }

    func testQQPassiveExportNormalizesOnlyPresentFieldsWithoutInventingOptionalValues() async throws {
        let runtime = try MusicPlatformFixture(), adapter = MusicNodeAccountProvider(platform: .qq, client: runtime.makeClient())
        let row = MusicAccountTransferEntry(platform: .qq, userID: "42",
            fields: ["musicid": "42", "musickey": "synthetic-key", "keyExpiresIn": "3600"])
        let exported = try await adapter.fileCredential(MusicAccountTransferCodec.credential(row))
        let result = try MusicAccountTransferCodec.entry(exported, platform: .qq)
        XCTAssertEqual(result.fields, ["musicid": "42", "musickey": "synthetic-key", "key_expires_in": "3600"])
        XCTAssertTrue(runtime.requests().isEmpty, "Passive export cannot prepare or authenticate a device")
    }

    func testPassiveNeteaseExportDoesNotGenerateMissingClientDevice() async throws {
        let runtime = try MusicPlatformFixture(), adapter = MusicNodeAccountProvider(platform: .netease, client: runtime.makeClient())
        let incomplete = try MusicAccountCredential.make(platform: .netease, userID: "42", cookie: "MUSIC_U=synthetic-session")
        let passive = try await adapter.fileCredential(incomplete)
        XCTAssertEqual(passive, incomplete)
        XCTAssertThrowsError(try MusicAccountTransferCodec.entry(passive, platform: .netease))
        let row = try sharedEntries()[0], credential = try MusicAccountTransferCodec.credential(row)
        let complete = try await adapter.fileCredential(credential)
        XCTAssertEqual(try MusicAccountTransferCodec.entry(complete, platform: .netease).fields, row.fields)
        XCTAssertTrue(runtime.requests().isEmpty)
    }

    func testNeteaseCandidateOnlyCommitsReturnedDeviceAfterSuccessfulAuthentication() async throws {
        for succeeds in [false, true] {
            let f = try MusicNativeFixture(platform: .netease); try await f.enable()
            let original = f.store.peek(f.identity)
            if succeeds {
                let cookie = try MusicAccountCredential.read(XCTUnwrap(original).credential)[music: "cookie"].musicRequiredText() + "; NMTID=synthetic-validated-new"
                await f.platform.replies("account.verify", .object(["userId": .string("101"), "cookie": .string(cookie),
                    "profile": .object([:])]))
                _ = try await f.service.validate(platform: .netease)
                let saved = try MusicAccountTransferCodec.entry(XCTUnwrap(f.store.peek(f.identity)).credential, platform: .netease)
                XCTAssertEqual(saved.fields["NMTID"], "synthetic-validated-new")
            } else {
                await f.platform.fail("account.verify", code: "INVALID_CREDENTIALS")
                do { _ = try await f.service.validate(platform: .netease); XCTFail("Authentication failed") }
                catch { XCTAssertEqual(error as? MusicServiceError, .sessionExpired) }
                XCTAssertEqual(f.store.calls().filter { $0 == "write" }.count, 1, "No candidate was saved")
            }
            let calls = await f.platform.recorded()
            XCTAssertEqual(calls.map(\.operation), ["account.verify"])
            _ = await f.service.close()
        }
    }

    func testKugouCandidateFailureCannotPublishOrCommitAnUnverifiedDevice() async throws {
        let f = try MusicNativeFixture(platform: .kugou); try await f.enable()
        await f.platform.fail("account.verify", code: "INVALID_RESPONSE")
        let original = f.store.peek(f.identity)
        do { _ = try await f.service.validate(platform: .kugou); XCTFail("Invalid candidate") }
        catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
        XCTAssertEqual(f.store.peek(f.identity), original)
        XCTAssertFalse(f.runtime.requests().contains { $0.transportOperation == "session.update" && $0.fields[music: "session"] != .null })
        _ = await f.service.close()
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

    @MainActor func testSavedImportSurvivesPublicationFailureAndContinuesRemainingBatch() async throws {
        let store = MusicAccountTestStore(), accounts = AccountSessionCoordinator(store: store)
        let scopes = try Dictionary(uniqueKeysWithValues: MusicAccountPlatform.allCases.map {
            ($0, try MusicAccountFixture.identity($0.rawValue))
        })
        let identity = try XCTUnwrap(scopes[.netease]), rows = try sharedEntries()
        try store.write(CredentialRecord(credential: MusicAccountFixture.credential(platform: .netease)), for: identity)
        let original = try XCTUnwrap(store.peek(identity))
        let fixture = try MusicPlatformFixture(controlHandler: { request in
            if request.platform == "wy", request.fields[music: "session"] != .null,
               store.peek(identity)?.revision != original.revision {
                return .object(["published": .bool(false)])
            }
            return .object(["published": .bool(true)])
        }) { request in
            XCTAssertEqual(request.operation, "account.verify")
            return try MusicAccountFixture.account(request)
        }
        let client = fixture.makeClient()
        let owner = NativeMusicAccountService(accounts: accounts, scopes: scopes, platformClient: client)
        addTeardownBlock { _ = await owner.close(); await fixture.runtime.close() }
        _ = try await owner.setAccess(available: true, generation: 1)
        _ = try await owner.validate(platform: .netease)
        let oldQueue = try await owner.beginAccountQueue(platform: .netease)
        let ready = expectation(description: "account record observer ready")
        let changes = await owner.accountSessionChanges()
        let notification = MusicAccountQueueOwnerCall("saved record notified after publication failure") {
            var iterator = changes.makeAsyncIterator()
            for _ in MusicAccountPlatform.allCases { _ = await iterator.next() }
            ready.fulfill()
            while let platform = await iterator.next() {
                if platform == .netease, store.peek(identity)?.revision != original.revision { return platform }
            }
            throw MusicServiceError.closed
        }
        defer { notification.cancel() }
        await fulfillment(of: [ready], timeout: 3)
        let writes = store.calls().filter { $0 == "write" }.count
        let plan = try await owner.prepareAccountImport(file(rows))
        let report = await owner.importAccounts(plan, policy: .overwrite)
        XCTAssertEqual(report.imported, MusicAccountPlatform.allCases)
        XCTAssertTrue(report.failed.isEmpty); XCTAssertTrue(report.cancelled.isEmpty); XCTAssertTrue(report.skipped.isEmpty)
        await fulfillment(of: [notification.finished], timeout: 3)
        XCTAssertEqual(try notification.value(), .netease)
        let saved = try XCTUnwrap(store.peek(identity)), fields = try MusicAccountCredential.read(saved.credential)
        XCTAssertNotEqual(saved.revision, original.revision)
        XCTAssertEqual(fields[music: "userId"], .string(rows[0].userID))
        let requests = fixture.requests().filter { $0.transportOperation == "request" }
        XCTAssertEqual(requests.suffix(3).map(\.platform), ["wy", "tx", "kg"])
        XCTAssertEqual(requests.count, 4, "One original validation and one verification per imported item")
        let verification = try XCTUnwrap(requests.first {
            $0.platform == "wy" && $0.candidate[music: "userId"] == .string(rows[0].userID)
        })
        XCTAssertEqual(fields[music: "cookie"], verification.candidate[music: "cookie"])
        let publications = fixture.requests().filter {
            $0.transportOperation == "session.update" && $0.fields[music: "session"] != .null
        }
        XCTAssertEqual(publications.suffix(3).map(\.platform), ["wy", "tx", "kg"])
        XCTAssertEqual(publications.count, 4, "A failed publication is not automatically replayed")
        let statuses = try await owner.statuses()
        XCTAssertEqual(statuses.first { $0.platform == .netease }?.phase, .requiresValidation)
        XCTAssertTrue(statuses.filter { $0.platform != .netease }.allSatisfy { $0.phase == .signedIn })
        do { try await owner.checkAccountQueue(oldQueue); XCTFail("Saved replacement kept the old queue authorization") } catch {}
        XCTAssertEqual(store.calls().filter { $0 == "write" }.count, writes + 3)
        XCTAssertEqual(store.calls().filter { $0 == "remove" }.count, 0)
        let requestCount = fixture.requests().count
        let repeated = await owner.importAccounts(plan, policy: .overwrite)
        XCTAssertEqual(repeated, .init()); XCTAssertEqual(fixture.requests().count, requestCount)
        XCTAssertEqual(store.peek(identity), saved)
        XCTAssertEqual(store.calls().filter { $0 == "write" }.count, writes + 3)
    }

    @MainActor func testCancellationAfterSavingKeepsImportedItemAndPhysicallySettlesPublicationBeforeCancellingRemainingBatch() async throws {
        for holdPublishedSession in [false, true] {
            let store = MusicAccountTestStore(), accounts = AccountSessionCoordinator(store: store)
            let scopes = try Dictionary(uniqueKeysWithValues: MusicAccountPlatform.allCases.map {
                ($0, try MusicAccountFixture.identity($0.rawValue))
            })
            let identity = try XCTUnwrap(scopes[.netease]), rows = try sharedEntries()
            try store.write(CredentialRecord(credential: MusicAccountFixture.credential(platform: .netease)), for: identity)
            let original = try XCTUnwrap(store.peek(identity))
            let gate = MusicAccountQueueOwnerGate(holdPublishedSession ? "saved session publication" : "saved account retirement")
            let fixture = try MusicPlatformFixture(controlHandler: { request in
                if request.platform == "wy", store.peek(identity)?.revision != original.revision,
                   (request.fields[music: "session"] != .null) == holdPublishedSession {
                    await gate.wait()
                }
                return .object(["published": .bool(true)])
            }) { request in
                XCTAssertEqual(request.operation, "account.verify")
                return try MusicAccountFixture.account(request)
            }
            let client = fixture.makeClient()
            let owner = NativeMusicAccountService(accounts: accounts, scopes: scopes, platformClient: client)
            addTeardownBlock { await gate.release(); _ = await owner.close(); await fixture.runtime.close() }
            _ = try await owner.setAccess(available: true, generation: 1)
            _ = try await owner.validate(platform: .netease)
            let oldQueue = try await owner.beginAccountQueue(platform: .netease)
            let ready = expectation(description: "account record observer ready")
            let changes = await owner.accountSessionChanges()
            let notification = MusicAccountQueueOwnerCall("saved record notified despite caller cancellation") {
                var iterator = changes.makeAsyncIterator()
                for _ in MusicAccountPlatform.allCases { _ = await iterator.next() }
                ready.fulfill()
                while let platform = await iterator.next() {
                    if platform == .netease, store.peek(identity)?.revision != original.revision { return platform }
                }
                throw MusicServiceError.closed
            }
            defer { notification.cancel() }
            await fulfillment(of: [ready], timeout: 3)
            let writes = store.calls().filter { $0 == "write" }.count
            let plan = try await owner.prepareAccountImport(file(rows))
            let importing = MusicAccountQueueOwnerCall("saved import physical settlement") {
                await owner.importAccounts(plan, policy: .overwrite)
            }
            defer { importing.cancel() }
            await fulfillment(of: [gate.entered], timeout: 3)
            let saved = try XCTUnwrap(store.peek(identity))
            XCTAssertNotEqual(saved.revision, original.revision)
            XCTAssertNil(importing.result, "A held physical publication/retirement is not completion")
            do { try await owner.checkAccountQueue(oldQueue); XCTFail("Saved replacement kept the old queue authorization") } catch {}
            importing.cancel()
            XCTAssertNil(importing.result, "Caller cancellation must not complete a held publication")
            await gate.release()
            await fulfillment(of: [gate.departed, importing.finished, notification.finished], timeout: 3)
            let report = try importing.value()
            XCTAssertEqual(report.imported, [.netease])
            XCTAssertEqual(report.cancelled, [.qq, .kugou])
            XCTAssertTrue(report.failed.isEmpty); XCTAssertTrue(report.skipped.isEmpty)
            XCTAssertEqual(try notification.value(), .netease)
            XCTAssertEqual(store.peek(identity), saved, "Cancellation cannot roll back or rewrite a confirmed record")
            let fields = try MusicAccountCredential.read(saved.credential)
            XCTAssertEqual(fields[music: "userId"], .string(rows[0].userID))
            let requests = fixture.requests().filter { $0.transportOperation == "request" }
            XCTAssertEqual(requests.map(\.platform), ["wy", "wy"], "Remaining items never start authentication")
            let verification = try XCTUnwrap(requests.first {
                $0.platform == "wy" && $0.candidate[music: "userId"] == .string(rows[0].userID)
            })
            XCTAssertEqual(fields[music: "cookie"], verification.candidate[music: "cookie"])
            let publications = fixture.requests().filter {
                $0.transportOperation == "session.update" && $0.fields[music: "session"] != .null
            }
            XCTAssertEqual(publications.count, holdPublishedSession ? 2 : 1)
            let statuses = try await owner.statuses()
            XCTAssertEqual(statuses.first { $0.platform == .netease }?.phase, .requiresValidation)
            XCTAssertEqual(store.calls().filter { $0 == "write" }.count, writes + 1)
            XCTAssertEqual(store.calls().filter { $0 == "remove" }.count, 0)
            XCTAssertNil(store.peek(try XCTUnwrap(scopes[.qq]))); XCTAssertNil(store.peek(try XCTUnwrap(scopes[.kugou])))
            let requestCount = fixture.requests().count
            let repeated = await owner.importAccounts(plan, policy: .overwrite)
            XCTAssertEqual(repeated, .init()); XCTAssertEqual(fixture.requests().count, requestCount)
            XCTAssertEqual(store.calls().filter { $0 == "write" }.count, writes + 1)
        }
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

    func testNeteaseImportKeepsMusicACommaDeviceAndOriginalOnFailedAuthentication() async throws {
        let f = try MusicNativeFixture(platform: .netease); try await f.enable()
        var fields = try MusicAccountTransferCodec.entry(MusicAccountFixture.credential(platform: .netease), platform: .netease).fields
        fields["MUSIC_A"] = "synthetic-account-associated-a"
        let row = MusicAccountTransferEntry(platform: .netease, userID: "101", fields: fields)
        let plan = try await f.service.prepareAccountImport(file([row]))
        let report = await f.service.importAccounts(plan, policy: .overwrite)
        XCTAssertEqual(report.imported, [.netease])
        let exported = try MusicAccountTransferCodec.decode(await f.service.exportAccountFile())
        XCTAssertEqual(exported.first?.fields, fields)
        XCTAssertEqual(exported.first?.fields["_ntes_nnid"], "synthetic-nuid,1800000000000")
        let before = f.store.peek(f.identity)
        await f.platform.fail("account.verify", code: "INVALID_CREDENTIALS")
        let next = try await f.service.prepareAccountImport(file([row]))
        let failed = await f.service.importAccounts(next, policy: .overwrite)
        XCTAssertEqual(failed.failed, [.netease]); XCTAssertEqual(f.store.peek(f.identity), before)
        let statuses = try await f.service.statuses()
        XCTAssertEqual(statuses.first?.phase, .signedIn)
        _ = await f.service.close()
    }

    func testQQAuthenticationIdentityFailureRejectsImportAndKeepsOldSession() async throws {
        let f = try MusicNativeFixture(); try await f.enable()
        _ = try await f.service.validate(platform: .qq)
        let original = f.store.peek(f.identity), before = await f.accounts.snapshot(for: f.identity)
        let writes = f.store.calls().filter { $0 == "write" }.count
        let row = try MusicAccountTransferCodec.entry(MusicAccountFixture.credential(), platform: .qq)
        let plan = try await f.service.prepareAccountImport(file([row]))
        await f.platform.fail("account.verify", code: "INVALID_CREDENTIALS")
        let report = await f.service.importAccounts(plan, policy: .overwrite)
        XCTAssertEqual(report.failed, [.qq]); XCTAssertTrue(report.imported.isEmpty)
        XCTAssertEqual(f.store.peek(f.identity), original)
        let after = await f.accounts.snapshot(for: f.identity)
        XCTAssertEqual(after, before); XCTAssertEqual(f.store.calls().filter { $0 == "write" }.count, writes)
        _ = await f.service.close()
    }

    func testKugouImportRequiresAuthenticationWhileOrdinaryProfileConsumesOnlyDisplayFields() async throws {
        let f = try MusicNativeFixture(platform: .kugou); try await f.enable()
        let profile = try await f.service.refreshProfile(platform: .kugou)
        XCTAssertEqual(profile.nickname, "合成昵称", "A canonical profile does not require a second remote identity field")
        let original = f.store.peek(f.identity), before = await f.accounts.snapshot(for: f.identity)
        let row = try MusicAccountTransferCodec.entry(MusicAccountFixture.credential(platform: .kugou), platform: .kugou)
        let plan = try await f.service.prepareAccountImport(file([row]))
        await f.platform.fail("account.verify", code: "INVALID_CREDENTIALS")
        let report = await f.service.importAccounts(plan, policy: .overwrite)
        XCTAssertEqual(report.failed, [.kugou]); XCTAssertTrue(report.imported.isEmpty)
        XCTAssertEqual(f.store.peek(f.identity), original)
        let after = await f.accounts.snapshot(for: f.identity); XCTAssertEqual(after, before)
        _ = await f.service.close()
    }

    func testInvalidPlatformFieldsFailOnlyThatItemWithoutNetworkAndCannotInventKugouDevice() async throws {
        let fixture = try TransferFixture(); let rows = try sharedEntries()
        try fixture.seed(rows[2]); try await fixture.enable()
        let original = fixture.store.peek(fixture.scopes[.kugou]!)
        var fields = rows[2].fields; fields.removeValue(forKey: "KUGOU_API_GUID")
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
