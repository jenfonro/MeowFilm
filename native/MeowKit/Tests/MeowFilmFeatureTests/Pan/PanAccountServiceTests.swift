import XCTest
import MeowRuntime
import MeowStorage
import MeowStorageTestSupport
@testable import MeowFilmFeature

final class PanAccountServiceTests: XCTestCase {
    func testAllFiveProvidersExposeExactManualFieldsAndQRCapabilities() async throws {
        let runtime = try PanTestRuntime()
        let providers = runtime.service.providers()
        XCTAssertEqual(providers.map(\.id), [.baidu, .quark, .uc, .tianyi, .yidong])
        XCTAssertEqual(providers.map(\.id), PanProviderID.allCases)
        XCTAssertEqual(providers.filter(\.login).map(\.id), [.baidu, .quark, .uc])
        XCTAssertTrue(providers.allSatisfy(\.refresh))
        for provider in providers.prefix(3) { XCTAssertEqual(provider.credentialFields, [.cookie]) }
        XCTAssertEqual(providers[3].credentialFields, [.cookie, .username, .password])
        XCTAssertEqual(providers[4].credentialFields, [.authorization, .refreshedAt])
    }

    func testStatusIsRequiredAndConfiguredNeverActsAsValidityProof() throws {
        let configuredSignedOut = try PanAccount(panAccountValue(.baidu, fields: ["cookie"]))
        XCTAssertEqual(configuredSignedOut.configured, true)
        XCTAssertEqual(configuredSignedOut.status, .signedOut)
        var fields: RuntimeObject = ["provider": .string("baidu"), "revision": .number(1),
            "configured": .bool(true), "fields": .array([.string("cookie")])]
        XCTAssertThrowsError(try PanAccount(.object(fields)))
        fields["status"] = .string("loading")
        XCTAssertThrowsError(try PanAccount(.object(fields)))
    }

    func testCorruptAccountProjectionHasOnlyIdentityAndExplicitError() throws {
        let value: RuntimeValue = .object(["provider": .string("baidu"), "errorCode": .string("CORRUPT_STORAGE")])
        let account = try PanAccount(value)
        XCTAssertEqual(account.provider, .baidu)
        XCTAssertEqual(account.errorCode, .corruptStorage)
        XCTAssertNil(account.revision); XCTAssertNil(account.configured)
        XCTAssertNil(account.status); XCTAssertNil(account.fields)
        for invalid: RuntimeValue in [
            .object(["provider": .string("unknown"), "errorCode": .string("CORRUPT_STORAGE")]),
            .object(["provider": .string("baidu"), "errorCode": .string("UNKNOWN_ERROR")]),
            .object(["provider": .string("baidu"), "errorCode": .null]),
        ] {
            XCTAssertThrowsError(try PanAccount(invalid)) {
                XCTAssertEqual($0 as? PanAccountError, .invalidResponse)
            }
        }
        for progress in [PanLoginProgress.pending, .confirmed] {
            XCTAssertThrowsError(try PanLoginReceipt(panReceipt(progress, account: value))) {
                XCTAssertEqual($0 as? RuntimeHTTPError, .remote("CORRUPT_STORAGE"))
            }
        }
    }

    func testRefreshCannotAcceptCorruptAccountAsSuccessfulReceipt() async throws {
        let storage = try PanTestStorage()
        try storage.seed(.baidu, credentials: panSyntheticCredentials(.baidu), revision: 4)
        let before = try storage.read()
        let service = PanAccountService(document: storage.document, request: { _, _, _ in
            .object(["refreshed": .bool(true), "account": .object([
                "provider": .string("baidu"), "errorCode": .string("CORRUPT_STORAGE")])])
        })
        do { _ = try await service.refresh(.baidu); XCTFail("An error row cannot confirm an account") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .remote("CORRUPT_STORAGE")) }
        storageTestEqual(try await service.accounts()[0].status, .unverified)
        storageTestEqual(try await service.accounts()[0].revision, 4)
        XCTAssertEqual(try storage.read(), before)
        XCTAssertEqual(storage.changes.values, [])
    }

    func testManualCredentialsFollowProtocolAndPasswordSpacesArePreserved() throws {
        for id in [PanProviderID.baidu, .quark, .uc] {
            let provider = try PanProvider(panProviderValue(id))
            XCTAssertEqual(try provider.credentials(from: ["cookie": "synthetic-cookie"]),
                           .object(["cookie": .string("synthetic-cookie")]))
            XCTAssertThrowsError(try provider.credentials(from: ["cookie": "synthetic-cookie", "unknown": "synthetic"])) {
                XCTAssertEqual($0 as? RuntimeHTTPError, .remote("INVALID_REQUEST"))
            }
        }
        let mobile = try PanProvider(panProviderValue(.yidong))
        XCTAssertEqual(try mobile.credentials(from: ["authorization": "synthetic-authorization"]),
                       .object(["authorization": .string("synthetic-authorization")]))
        XCTAssertEqual(try mobile.credentials(from: ["refreshedAt": "1700000000000"]).panField("refreshedAt"),
                       .number(1_700_000_000_000))
        let cloud = try PanProvider(panProviderValue(.tianyi))
        XCTAssertEqual(try cloud.credentials(from: ["username": "synthetic-user", "password": "  synthetic pass  "]),
                       .object(["username": .string("synthetic-user"), "password": .string("  synthetic pass  ")]))
        XCTAssertEqual(try cloud.credentials(from: ["cookie": "synthetic-cookie"]),
                       .object(["cookie": .string("synthetic-cookie")]))
        for bad in ["-1", "1.25", "NaN", "infinity", "9007199254740992", "not-a-number"] {
            XCTAssertThrowsError(try mobile.credentials(from: ["refreshedAt": bad]))
        }
        XCTAssertEqual(try mobile.credentials(from: ["refreshedAt": "0"]).panField("refreshedAt"), .number(0))
        for field in [PanCredentialField.cookie, .password, .authorization] { XCTAssertTrue(field.isSecret) }
        for field in [PanCredentialField.username, .refreshedAt] { XCTAssertFalse(field.isSecret) }
    }

    func testLoginURLIsKeptIntactAndImageDataURIKeepsItsRealMediaType() throws {
        let text = "custom-login://local.example.test/q?code=synthetic#fragment"
        let url = try PanLoginChallenge(panChallengeValue(url: text))
        XCTAssertEqual(url.payload, .url(text))
        for type in ["image/jpeg", "image/png", "image/gif"] {
            let bytes = Data("synthetic-image-payload".utf8)
            let image = try PanLoginChallenge(panChallengeValue(type: "image", url: "data:\(type);base64," + bytes.base64EncodedString()))
            XCTAssertEqual(image.payload, .image(bytes, mediaType: type))
        }
        for invalid in ["https://images.example.test/q.jpg", "data:text/html;base64,QQ==", "data:image/png;base64,???"] {
            XCTAssertThrowsError(try PanLoginChallenge(panChallengeValue(type: "image", url: invalid)))
        }
    }

    func testBaiduPollUses45SecondsOnlyOnThatSingleCall() async throws {
        let runtime = try PanTestRuntime(), service = runtime.service
        _ = try await service.poll("synthetic-session", provider: .baidu)
        _ = try await service.poll("synthetic-session", provider: .quark)
        _ = service.providers()
        let calls = await runtime.calls
        XCTAssertEqual(calls.map(\.operation), ["login.poll", "login.poll"])
        XCTAssertEqual(calls.map(\.timeout), [45, nil])
        XCTAssertEqual(calls[0].parameters, .object(["sessionId": .string("synthetic-session")]))
    }

    func testMalformedStartWithSessionIDIsCancelledAndConfirmedNeedsSignedInReceipt() async throws {
        let storage = try PanTestStorage()
        let cancelled = expectation(description: "malformed start session cancelled")
        let service = PanAccountService(document: storage.document, request: { operation, parameters, _ in
            if operation == "login.start" {
                return panChallengeValue(type: "image", url: "data:image/jpeg;base64,???")
            }
            XCTAssertEqual(operation, "login.cancel")
            XCTAssertEqual(parameters.panField("sessionId"), .string("synthetic-session"))
            cancelled.fulfill(); return .object([:])
        })
        do { _ = try await service.start(.baidu); XCTFail("Malformed challenge must fail") } catch {}
        await fulfillment(of: [cancelled], timeout: 2)
        XCTAssertThrowsError(try PanLoginReceipt(panReceipt(.confirmed)))
        XCTAssertThrowsError(try PanLoginReceipt(panReceipt(.confirmed, account: panAccountValue(.baidu, .expired))))
    }

    func testPollRejectsAnAccountFromAnotherSessionEvenWhenItsProviderMatches() async throws {
        let storage = try PanTestStorage()
        let service = PanAccountService(document: storage.document, request: { operation, _, _ in
            XCTAssertEqual(operation, "login.poll")
            return panReceipt(.confirmed, id: "late-session",
                              account: panAccountValue(.baidu, .signedIn, revision: 1, fields: ["cookie"]))
        })
        do {
            _ = try await service.poll("current-session", provider: .baidu)
            XCTFail("A receipt for another session must be rejected")
        } catch {
            XCTAssertEqual(error as? PanAccountError, .invalidResponse)
        }
    }

    func testOfflineReadSaveClearAndReopenNeverOpenOrQueueAConnection() async throws {
        let storage = try PanTestStorage(), connections = PanTestChanges()
        let service = PanAccountService(document: storage.document, connection: {
            connections.record("connection")
            throw RuntimeHTTPError.closed
        })
        XCTAssertEqual(service.providers().count, 5)
        storageTestTrue(try await service.accounts().allSatisfy { $0.configured == false && $0.status == .signedOut })
        XCTAssertTrue(FileManager.default.fileExists(atPath: storage.database.url.path))
        for provider in PanProviderID.allCases {
            let saved = try await service.replace(provider, credentials: panSyntheticCredentials(provider))
            XCTAssertEqual(saved.revision, 1); XCTAssertEqual(saved.configured, true)
            XCTAssertEqual(saved.status, .unverified)
        }
        let reopenedDocument = FilmSettingsDocument(block: .panAccounts, database: storage.database)
        let reopened = PanAccountService(document: reopenedDocument, request: { _, _, _ in
            XCTFail("Reopening native settings cannot request runtime"); throw RuntimeHTTPError.closed
        })
        storageTestEqual(try await reopened.accounts().map(\.revision), Array(repeating: 1, count: 5))
        storageTestTrue(try await reopened.accounts().allSatisfy { $0.status == .unverified })
        XCTAssertEqual(storage.changes.values, Array(repeating: "pan.accounts", count: 5))
        let cleared = try await service.clear(.tianyi)
        XCTAssertEqual(cleared.revision, 2); XCTAssertEqual(cleared.configured, false)
        XCTAssertEqual(cleared.status, .signedOut)
        storageTestEqual(try await reopened.accounts()[3], cleared)
        XCTAssertEqual(connections.values, [])
        await Task.yield()
        XCTAssertEqual(connections.values, [], "Offline settings must not leave connection work queued")
        do { _ = try await service.refresh(.baidu); XCTFail("An explicit upstream operation is still offline") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .closed) }
        XCTAssertEqual(connections.values, ["connection"])
        storageTestEqual(try await service.accounts()[0].status, .unverified)
    }

    func testConsumedCredentialsAreValidatedLocallyBeforeCommit() async throws {
        let storage = try PanTestStorage()
        let service = PanAccountService(document: storage.document, request: { _, _, _ in
            XCTFail("Credential validation is local"); throw RuntimeHTTPError.closed
        })
        let invalid: [(PanProviderID, RuntimeValue)] = [
            (.baidu, .array([])),
            (.baidu, .object(["cookie": .string("synthetic\r\nHeader: value")])),
            (.baidu, .object(["cookie": .string("synthetic\0cookie")])),
            (.baidu, .object(["cookie": .number(3)])),
            (.tianyi, .object(["username": .string("synthetic\nuser")])),
            (.tianyi, .object(["password": .string("synthetic\0password")])),
            (.quark, .object(["cookie": .string("synthetic\rcookie")])),
            (.uc, .object(["cookie": .bool(true)])),
            (.yidong, .object(["refreshedAt": .number(-1)])),
            (.yidong, .object(["refreshedAt": .number(0.5)])),
            (.yidong, .object(["refreshedAt": .number(.nan)])),
            (.yidong, .object(["refreshedAt": .number(9_007_199_254_740_992)])),
        ]
        for (provider, value) in invalid {
            await storageTestThrows({ try await service.replace(provider, credentials: value) }) {
                XCTAssertEqual($0 as? RuntimeHTTPError, .remote("INVALID_REQUEST"))
            }
        }
        XCTAssertNil(try storage.document.readForTest())
        XCTAssertEqual(storage.changes.values, [])
        let credentials: RuntimeValue = .object(["username": .string(" synthetic-user "),
            "password": .string(" \r\nsynthetic password\n "), "cookie": .string("")])
        _ = try await service.replace(.tianyi, credentials: credentials)
        XCTAssertEqual(try storage.credentials(.tianyi), credentials)
        let zero = try await service.replace(.yidong, credentials: .object(["refreshedAt": .number(0), "authorization": .string("")]))
        XCTAssertEqual(zero.configured, false); XCTAssertEqual(zero.fields, []); XCTAssertEqual(zero.status, .signedOut)
        _ = try await service.replace(.yidong, credentials: .object(["refreshedAt": .number(9_007_199_254_740_991)]))
        XCTAssertEqual(try storage.credentials(.yidong)?.panField("refreshedAt"), .number(9_007_199_254_740_991))
    }

    func testDocumentRootAndSelectedCredentialFailuresRemainExplicit() async throws {
        for value: RuntimeValue in [.null, .array([]), .object([:]),
            .object(["version": .number(2), "accounts": .object([:])])] {
            XCTAssertThrowsError(try PanAccountService.validateSettingsDocument(value)) {
                XCTAssertEqual($0 as? RuntimeHTTPError, .remote("CORRUPT_STORAGE"))
            }
        }
        let storage = try PanTestStorage()
        let service = PanAccountService(document: storage.document, request: { _, _, _ in throw RuntimeHTTPError.closed })
        let opaque = try RuntimeJSON.decode(Data(#"{"n":1e400,"é":1,"e\u0301":2}"#.utf8))
        await storageTestThrows({
            try await storage.document.update { _ in .object(["version": .number(1), "extra": opaque,
                "accounts": .object(["unknown": opaque])]) }
        }) { XCTAssertEqual($0 as? FilmSettingsStorageError, .invalidDocument) }
        XCTAssertNil(try storage.document.readForTest())
        let account = try await service.replace(.baidu, credentials: .object(["unknown": opaque]))
        XCTAssertEqual(account.configured, false)
        XCTAssertEqual(try storage.credentials(.baidu), .object([:]))
        let saved = try storage.read()
        XCTAssertNil(saved.panField("extra"))
        XCTAssertNil(saved.panField("accounts")?.panField("unknown"))
    }

    func testRuntimeProviderCASDoesNotRetryOverwriteCompetitorOrNotify() async throws {
        let storage = try PanTestStorage()
        try storage.seed(.baidu, credentials: panSyntheticCredentials(.baidu))
        let expected = try await storage.document.snapshot()
        let row = try XCTUnwrap(expected.document?.panField("accounts")?.panField("baidu"))
        let competitor = try storageTestOpen(storage.root)
        let competingDocument = FilmSettingsDocument(block: .panAccounts, database: competitor)
        let competingService = PanAccountService(document: competingDocument, request: { _, _, _ in throw RuntimeHTTPError.closed })
        _ = try await competingService.replace(.baidu, credentials: .object(["cookie": .string("synthetic-newer")]))
        _ = try await competingService.replace(.uc, credentials: panSyntheticCredentials(.uc))
        let before = try storage.read()
        let stale: RuntimeValue = .object(["provider": .string("baidu"), "mode": .string("replace"),
            "rowRevision": row.panField("rowRevision")!, "accountRevision": row.panField("revision")!,
            "credentials": .object(["cookie": .string("synthetic-stale")])])
        XCTAssertThrowsError(try storageTestIO { try storage.document.updatePanSync(stale) }) {
            XCTAssertEqual($0 as? FilmSettingsStorageError, .conflict)
        }
        XCTAssertEqual(try storage.read(), before)
        XCTAssertEqual(storage.changes.values, [])
        XCTAssertEqual(try storage.read().panField("accounts")?.panField("baidu")?.panField("revision"), .number(2))
        try await competitor.close()
    }

    func testFailedCommitPreservesPriorDocumentAndMaxRevisionCannotWrap() async throws {
        let storage = try PanTestStorage()
        try storage.seed(.baidu, credentials: panSyntheticCredentials(.baidu), revision: 7)
        let before = try storage.read()
        let service = PanAccountService(document: storage.document, request: { _, _, _ in throw RuntimeHTTPError.closed })
        try storage.failWrites(true)
        await storageTestThrows({ try await service.clear(.baidu) }) {
            XCTAssertEqual($0 as? RuntimeHTTPError, .remote("STORAGE_ERROR"))
        }
        XCTAssertEqual(try storage.read(), before); XCTAssertEqual(storage.changes.values, [])
        try storage.failWrites(false)
        try storage.seed(.baidu, credentials: panSyntheticCredentials(.baidu), revision: 9_007_199_254_740_991)
        await storageTestThrows({ try await service.clear(.baidu) }) {
            XCTAssertEqual($0 as? RuntimeHTTPError, .remote("STORAGE_ERROR"))
        }
        storageTestEqual(try await service.accounts()[0].revision, 9_007_199_254_740_991)
        XCTAssertEqual(storage.changes.values, [])
    }

    func testBadAccountIsExplicitWhileHealthySiblingCanCommit() async throws {
        let storage = try PanTestStorage()
        try storage.seed(.baidu, credentials: .object([:]), revision: -1)
        let before = try storage.read().panField("accounts")?.panField("baidu")
        let service = PanAccountService(document: storage.document, request: { _, _, _ in throw RuntimeHTTPError.closed })
        let bad = try await service.accounts()[0]
        XCTAssertEqual(bad.provider, .baidu); XCTAssertEqual(bad.errorCode, .corruptStorage)
        XCTAssertNil(bad.revision); XCTAssertNil(bad.configured)
        XCTAssertNil(bad.status); XCTAssertNil(bad.fields)
        await storageTestThrows({ try await service.replace(.baidu, credentials: panSyntheticCredentials(.baidu)) }) {
            XCTAssertEqual($0 as? RuntimeHTTPError, .remote("CORRUPT_STORAGE"))
        }
        await storageTestThrows({ try await service.clear(.baidu) }) {
            XCTAssertEqual($0 as? RuntimeHTTPError, .remote("CORRUPT_STORAGE"))
        }
        XCTAssertEqual(try storage.read().panField("accounts")?.panField("baidu"), before)
        let healthy = try await service.replace(.uc, credentials: panSyntheticCredentials(.uc))
        XCTAssertEqual(healthy.revision, 1); XCTAssertEqual(healthy.configured, true)
        XCTAssertEqual(healthy.status, .unverified); XCTAssertNil(healthy.errorCode)
        storageTestEqual(try await service.accounts()[2], healthy)
        storageTestEqual(try await service.accounts()[0], bad)
        XCTAssertEqual(try storage.read().panField("accounts")?.panField("baidu"),
            before)
        XCTAssertEqual(storage.changes.values, ["pan.accounts"])
    }

    func testCorruptReadInvalidatesPriorOnlineObservation() async throws {
        let runtime = try PanTestRuntime(), service = runtime.service
        try await runtime.setAccount(.baidu, status: .signedIn, revision: 3)
        _ = try await service.refresh(.baidu)
        storageTestEqual(try await service.accounts()[0].status, .signedIn)
        try runtime.storage.seed(.baidu, credentials: .object(["cookie": .string("synthetic\ninvalid")]), revision: 3)
        storageTestEqual(try await service.accounts()[0].errorCode, .corruptStorage)
        try runtime.storage.seed(.baidu, credentials: panSyntheticCredentials(.baidu), revision: 3)
        storageTestEqual(try await service.accounts()[0].status, .unverified)
        storageTestNil(try await service.accounts()[0].errorCode)
    }

    func testRuntimeWritebackIsVisibleAndConfirmedCannotPrecedePersistence() async throws {
        let storage = try PanTestStorage()
        let account = panAccountValue(.baidu, .signedIn, revision: 1)
        let service = PanAccountService(document: storage.document, request: { _, _, _ in
            panReceipt(.confirmed, account: account)
        })
        do { _ = try await service.poll("synthetic-session", provider: .baidu); XCTFail("Not yet committed") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .remote("STALE_ACCOUNT")) }
        let expected = try await storage.document.snapshot()
        try storage.persist(account, expected: expected)
        storageTestEqual(try await service.accounts()[0].status, .unverified)
        let receipt = try await service.poll("synthetic-session", provider: .baidu)
        XCTAssertEqual(receipt.progress, .confirmed)
        storageTestEqual(try await service.accounts()[0].status, .signedIn)
        _ = try await service.replace(.baidu, credentials: .object(["cookie": .string("synthetic-newer")]))
        do { _ = try await service.poll("synthetic-session", provider: .baidu); XCTFail("Retired receipt") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .remote("STALE_ACCOUNT")) }
        storageTestEqual(try await service.accounts()[0].status, .unverified)
        storageTestEqual(try await service.accounts()[0].revision, 2)
    }

    func testUnknownFailureCannotExposeUnderlyingErrorOrCredentialsInNotice() {
        let notice = PanAccountNotice(NSError(domain: "synthetic-sensitive-content", code: 1,
            userInfo: [NSLocalizedDescriptionKey: "synthetic-sensitive-content"]))
        XCTAssertEqual(notice.message, "网盘操作失败")
    }
}
