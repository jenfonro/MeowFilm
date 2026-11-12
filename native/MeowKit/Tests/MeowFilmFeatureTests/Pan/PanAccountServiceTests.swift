import XCTest
import MeowRuntime
@testable import MeowFilmFeature

final class PanAccountServiceTests: XCTestCase {
    func testAllSevenProvidersExposeExactManualFieldsAndQRCapabilities() async throws {
        let runtime = PanTestRuntime()
        let providers = try await runtime.service.providers()
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
        let runtime = PanTestRuntime(), service = runtime.service
        _ = try await service.poll("synthetic-session", provider: .baidu)
        _ = try await service.poll("synthetic-session", provider: .quark)
        _ = try await service.providers()
        let calls = await runtime.calls
        XCTAssertEqual(calls.map(\.operation), ["login.poll", "login.poll", "providers"])
        XCTAssertEqual(calls.map(\.timeout), [45, nil, nil])
        XCTAssertEqual(calls[0].parameters, .object(["sessionId": .string("synthetic-session")]))
    }

    func testMalformedStartWithSessionIDIsCancelledAndConfirmedNeedsSignedInReceipt() async {
        let cancelled = expectation(description: "malformed start session cancelled")
        let service = PanAccountService(request: { operation, parameters, _ in
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

    func testPollRejectsAnAccountFromAnotherSessionEvenWhenItsProviderMatches() async {
        let service = PanAccountService(request: { operation, _, _ in
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

    func testUnknownFailureCannotExposeUnderlyingErrorOrCredentialsInNotice() {
        let notice = PanAccountNotice(NSError(domain: "synthetic-sensitive-content", code: 1,
            userInfo: [NSLocalizedDescriptionKey: "synthetic-sensitive-content"]))
        XCTAssertEqual(notice.message, "网盘操作失败")
    }
}
