import XCTest
import MeowRuntime
import MeowStorage
@testable import MeowFilmFeature

final class PanAccountServiceTests: XCTestCase {
    func testAllSevenProvidersExposeExactManualFieldsAndQRCapabilities() async throws {
        let runtime = try PanTestRuntime()
        let providers = runtime.service.providers()
        XCTAssertEqual(providers.map(\.id), PanProviderID.allCases)
        XCTAssertEqual(providers.filter(\.login).map(\.id), [.baidu, .quark, .uc])
        XCTAssertTrue(providers.allSatisfy(\.refresh))
        XCTAssertEqual(providers[3].credentialFields, [.cookie, .username, .password])
        XCTAssertEqual(providers[4].credentialFields, [.authorization, .refreshedAt])
        XCTAssertEqual(providers[5].credentialFields, [.refreshToken, .deviceId, .accessToken, .expiresAt])
        XCTAssertEqual(providers[6].credentialFields, providers[5].credentialFields)
    }

    func testStatusIsRequiredAndConfiguredNeverActsAsValidityProof() throws {
        let configuredSignedOut = try PanAccount(panAccountValue(.baidu, fields: ["cookie"]))
        XCTAssertTrue(configuredSignedOut.configured)
        XCTAssertEqual(configuredSignedOut.status, .signedOut)
        var fields: RuntimeObject = ["provider": .string("baidu"), "revision": .number(1),
            "configured": .bool(true), "fields": .array([.string("cookie")])]
        XCTAssertThrowsError(try PanAccount(.object(fields)))
        fields["status"] = .string("loading")
        XCTAssertThrowsError(try PanAccount(.object(fields)))
    }

    func testOptionalTimestampsAndTokensFollowProtocolAndPasswordSpacesArePreserved() throws {
        for id in [PanProviderID.quarkTV, .ucTV] {
            let provider = try PanProvider(panProviderValue(id))
            let fields = try provider.credentials(from: ["refreshToken": "synthetic-refresh", "deviceId": "synthetic-device"])
            XCTAssertEqual(fields, .object(["refreshToken": .string("synthetic-refresh"), "deviceId": .string("synthetic-device")]))
            let full = try provider.credentials(from: ["expiresAt": "1700000000000", "accessToken": "synthetic-access"])
            XCTAssertEqual(full.panField("expiresAt"), .number(1_700_000_000_000))
        }
        let mobile = try PanProvider(panProviderValue(.yidong))
        XCTAssertEqual(try mobile.credentials(from: ["authorization": "synthetic-authorization"]),
                       .object(["authorization": .string("synthetic-authorization")]))
        let cloud = try PanProvider(panProviderValue(.tianyi))
        XCTAssertEqual(try cloud.credentials(from: ["username": "synthetic-user", "password": "  synthetic pass  "]),
                       .object(["username": .string("synthetic-user"), "password": .string("  synthetic pass  ")]))
        XCTAssertEqual(try cloud.credentials(from: ["cookie": "synthetic-cookie"]),
                       .object(["cookie": .string("synthetic-cookie")]))
        for bad in ["-1", "1.25", "NaN", "infinity", "9007199254740992", "not-a-number"] {
            XCTAssertThrowsError(try mobile.credentials(from: ["refreshedAt": bad]))
        }
        XCTAssertEqual(try mobile.credentials(from: ["refreshedAt": "0"]).panField("refreshedAt"), .number(0))
        for field in [PanCredentialField.cookie, .password, .authorization, .refreshToken, .accessToken] { XCTAssertTrue(field.isSecret) }
        for field in [PanCredentialField.username, .deviceId, .refreshedAt, .expiresAt] { XCTAssertFalse(field.isSecret) }
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
        XCTAssertEqual(service.providers().count, 7)
        XCTAssertTrue(try service.accounts().allSatisfy { !$0.configured && $0.status == .signedOut })
        XCTAssertTrue(try FileManager.default.contentsOfDirectory(atPath: storage.root.path).isEmpty)
        for provider in PanProviderID.allCases {
            let saved = try service.replace(provider, credentials: panSyntheticCredentials(provider))
            XCTAssertEqual(saved.revision, 1); XCTAssertTrue(saved.configured)
            XCTAssertEqual(saved.status, .unverified)
        }
        let reopenedDocument = FilmSettingsDocument(block: .panAccounts, storage: .protected { storage.store })
        let reopened = PanAccountService(document: reopenedDocument, request: { _, _, _ in
            XCTFail("Reopening native settings cannot request runtime"); throw RuntimeHTTPError.closed
        })
        XCTAssertEqual(try reopened.accounts().map(\.revision), Array(repeating: 1, count: 7))
        XCTAssertTrue(try reopened.accounts().allSatisfy { $0.status == .unverified })
        XCTAssertEqual(storage.changes.values, Array(repeating: "pan.accounts", count: 7))
        let cleared = try service.clear(.tianyi)
        XCTAssertEqual(cleared.revision, 2); XCTAssertFalse(cleared.configured)
        XCTAssertEqual(cleared.status, .signedOut)
        XCTAssertEqual(try reopened.accounts()[3], cleared)
        XCTAssertEqual(connections.values, [])
        await Task.yield()
        XCTAssertEqual(connections.values, [], "Offline settings must not leave connection work queued")
        do { _ = try await service.refresh(.baidu); XCTFail("An explicit upstream operation is still offline") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .closed) }
        XCTAssertEqual(connections.values, ["connection"])
        XCTAssertEqual(try service.accounts()[0].status, .unverified)
    }

    func testCredentialValidationIsLocalExactAndNeverCommitsInvalidInput() throws {
        let storage = try PanTestStorage()
        let service = PanAccountService(document: storage.document, request: { _, _, _ in
            XCTFail("Credential validation is local"); throw RuntimeHTTPError.closed
        })
        let invalid: [(PanProviderID, RuntimeValue)] = [
            (.baidu, .array([])), (.baidu, .object(["unknown": .string("synthetic")])),
            (.baidu, .object(["cookie": .string("synthetic\r\nHeader: value")])),
            (.baidu, .object(["cookie": .string("synthetic\0cookie")])),
            (.baidu, .object(["cookie": .number(3)])),
            (.tianyi, .object(["username": .string("synthetic\nuser")])),
            (.tianyi, .object(["password": .string("synthetic\0password")])),
            (.quarkTV, .object(["deviceId": .string("synthetic\rdevice")])),
            (.ucTV, .object(["accessToken": .bool(true)])),
            (.yidong, .object(["refreshedAt": .number(-1)])),
            (.yidong, .object(["refreshedAt": .number(0.5)])),
            (.yidong, .object(["refreshedAt": .number(.nan)])),
            (.ucTV, .object(["expiresAt": .number(9_007_199_254_740_992)])),
        ]
        for (provider, value) in invalid {
            XCTAssertThrowsError(try service.replace(provider, credentials: value)) {
                XCTAssertEqual($0 as? RuntimeHTTPError, .remote("INVALID_REQUEST"))
            }
        }
        XCTAssertNil(try storage.document.read())
        XCTAssertEqual(storage.changes.values, [])
        let credentials: RuntimeValue = .object(["username": .string(" synthetic-user "),
            "password": .string(" \r\nsynthetic password\n "), "cookie": .string("")])
        _ = try service.replace(.tianyi, credentials: credentials)
        XCTAssertEqual(try storage.credentials(.tianyi), credentials)
        let zero = try service.replace(.yidong, credentials: .object(["refreshedAt": .number(0), "authorization": .string("")]))
        XCTAssertFalse(zero.configured); XCTAssertEqual(zero.fields, []); XCTAssertEqual(zero.status, .signedOut)
        _ = try service.replace(.ucTV, credentials: .object(["expiresAt": .number(9_007_199_254_740_991)]))
        XCTAssertEqual(try storage.credentials(.ucTV)?.panField("expiresAt"), .number(9_007_199_254_740_991))
    }

    func testPublicRuntimeDocumentValidatorRejectsCorruptionWithoutStorageEffects() throws {
        let valid: RuntimeValue = .object(["version": .number(1), "accounts": .object([
            "baidu": .object(["revision": .number(1), "credentials": panSyntheticCredentials(.baidu)]),
        ])])
        try PanAccountService.validateSettingsDocument(valid)
        var invalid: [RuntimeValue] = [.null, .array([]), .object([:]),
            .object(["version": .number(2), "accounts": .object([:])]),
            .object(["version": .number(1), "accounts": .object([:]), "unknown": .bool(true)])]
        for revision in [Double(0), -1, 0.5, 9_007_199_254_740_992] {
            invalid.append(.object(["version": .number(1), "accounts": .object([
                "baidu": .object(["revision": .number(revision), "credentials": .object([:])]),
            ])]))
        }
        invalid += [
            .object(["version": .number(1), "accounts": .object(["unknown": .object(["revision": .number(1), "credentials": .object([:])])])]),
            .object(["version": .number(1), "accounts": .object(["baidu": .object(["revision": .number(1), "credentials": .object(["password": .string("synthetic")])])])]),
            .object(["version": .number(1), "accounts": .object(["baidu": .object(["revision": .number(1), "credentials": .object([:]), "status": .string("signedIn")])])]),
        ]
        for value in invalid {
            XCTAssertThrowsError(try PanAccountService.validateSettingsDocument(value)) {
                XCTAssertEqual($0 as? RuntimeHTTPError, .remote("CORRUPT_STORAGE"))
            }
        }
    }

    func testCASConflictDoesNotRetryOverwriteCompetitorOrNotify() throws {
        let storage = try PanTestStorage(), competitor = try PanTestStorage()
        try storage.seed(.baidu, credentials: panSyntheticCredentials(.baidu))
        try competitor.seed(.baidu, credentials: .object(["cookie": .string("synthetic-newer")]), revision: 2)
        try competitor.seed(.uc, credentials: panSyntheticCredentials(.uc))
        let destination = try encryptedFile(storage.root)
        let replacement = try Data(contentsOf: encryptedFile(competitor.root))
        storage.keys.onNextRead { try replacement.write(to: destination, options: .atomic) }
        let service = PanAccountService(document: storage.document, request: { _, _, _ in
            XCTFail("CAS cannot fall back to runtime"); throw RuntimeHTTPError.closed
        })
        XCTAssertThrowsError(try service.replace(.baidu, credentials: .object(["cookie": .string("synthetic-stale")]))) {
            XCTAssertEqual($0 as? RuntimeHTTPError, .remote("STALE_ACCOUNT"))
        }
        XCTAssertEqual(try storage.read(), try competitor.read())
        XCTAssertEqual(storage.changes.values, [])
        XCTAssertEqual(try service.accounts()[0].revision, 2)
    }

    func testFailedCommitPreservesPriorDocumentAndMaxRevisionCannotWrap() throws {
        let storage = try PanTestStorage()
        try storage.seed(.baidu, credentials: panSyntheticCredentials(.baidu), revision: 7)
        let before = try storage.read()
        let service = PanAccountService(document: storage.document, request: { _, _, _ in throw RuntimeHTTPError.closed })
        storage.committer.fail(true)
        XCTAssertThrowsError(try service.clear(.baidu)) {
            XCTAssertEqual($0 as? RuntimeHTTPError, .remote("STORAGE_ERROR"))
        }
        XCTAssertEqual(try storage.read(), before); XCTAssertEqual(storage.changes.values, [])
        storage.committer.fail(false)
        try storage.seed(.baidu, credentials: panSyntheticCredentials(.baidu), revision: 9_007_199_254_740_991)
        XCTAssertThrowsError(try service.clear(.baidu)) {
            XCTAssertEqual($0 as? RuntimeHTTPError, .remote("STORAGE_ERROR"))
        }
        XCTAssertEqual(try service.accounts()[0].revision, 9_007_199_254_740_991)
        XCTAssertEqual(storage.changes.values, [])
    }

    func testCorruptExistingDocumentDoesNotSilentlyBecomeAnEmptyAccount() throws {
        let storage = try PanTestStorage()
        try storage.store.write(Data(#"{"version":1,"accounts":{"baidu":{"revision":0,"credentials":{}}}}"#.utf8),
                                key: FilmSettingsDocument.key)
        let before = try storage.store.read(key: FilmSettingsDocument.key)
        let service = PanAccountService(document: storage.document, request: { _, _, _ in throw RuntimeHTTPError.closed })
        XCTAssertThrowsError(try service.accounts()) { XCTAssertEqual($0 as? RuntimeHTTPError, .remote("CORRUPT_STORAGE")) }
        XCTAssertThrowsError(try service.replace(.uc, credentials: panSyntheticCredentials(.uc)))
        XCTAssertEqual(try storage.store.read(key: FilmSettingsDocument.key), before)
        XCTAssertEqual(storage.changes.values, [])
    }

    func testRuntimeWritebackIsVisibleAndConfirmedCannotPrecedePersistence() async throws {
        let storage = try PanTestStorage()
        let account = panAccountValue(.baidu, .signedIn, revision: 1)
        let service = PanAccountService(document: storage.document, request: { _, _, _ in
            panReceipt(.confirmed, account: account)
        })
        do { _ = try await service.poll("synthetic-session", provider: .baidu); XCTFail("Not yet committed") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .remote("STALE_ACCOUNT")) }
        let expected = try storage.document.snapshot().fingerprint
        try storage.persist(account, expected: expected)
        XCTAssertEqual(try service.accounts()[0].status, .unverified)
        let receipt = try await service.poll("synthetic-session", provider: .baidu)
        XCTAssertEqual(receipt.progress, .confirmed)
        XCTAssertEqual(try service.accounts()[0].status, .signedIn)
        _ = try service.replace(.baidu, credentials: .object(["cookie": .string("synthetic-newer")]))
        do { _ = try await service.poll("synthetic-session", provider: .baidu); XCTFail("Retired receipt") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .remote("STALE_ACCOUNT")) }
        XCTAssertEqual(try service.accounts()[0].status, .unverified)
        XCTAssertEqual(try service.accounts()[0].revision, 2)
    }

    private func encryptedFile(_ root: URL) throws -> URL {
        let files = try XCTUnwrap(FileManager.default.enumerator(at: root, includingPropertiesForKeys: nil))
        return try XCTUnwrap(files.compactMap { $0 as? URL }.first { $0.pathExtension == "meowenc" })
    }

    func testUnknownFailureCannotExposeUnderlyingErrorOrCredentialsInNotice() {
        let notice = PanAccountNotice(NSError(domain: "synthetic-sensitive-content", code: 1,
            userInfo: [NSLocalizedDescriptionKey: "synthetic-sensitive-content"]))
        XCTAssertEqual(notice.message, "网盘操作失败")
    }
}
