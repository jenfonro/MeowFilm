import Foundation
import XCTest
import MeowContracts
@testable import MeowAccounts

final class CredentialStoreTests: XCTestCase {
    func testIdentitySeparatesInstallationSiteAndAccount() throws {
        let installation = UUID(), account = UUID()
        let first = try AccountFixtures.identity(installation: installation, site: "same", account: account)
        let identities = [
            first,
            try AccountFixtures.identity(site: "same", account: account),
            try AccountFixtures.identity(installation: installation, site: "other", account: account),
            try AccountFixtures.identity(installation: installation, site: "same")
        ]
        XCTAssertEqual(Set(identities).count, 4)
        XCTAssertEqual(first, try AccountFixtures.identity(installation: installation, site: "same", account: account))
        let encoded = try JSONEncoder().encode(first)
        XCTAssertEqual(try JSONDecoder().decode(AccountIdentity.self, from: encoded), first)
    }

    func testIdentityDecodingStillValidatesSource() throws {
        let invalid = Data("""
        {"source":{"installationID":"00000000-0000-0000-0000-000000000001","siteKey":""},
         "accountID":"00000000-0000-0000-0000-000000000002"}
        """.utf8)
        XCTAssertThrowsError(try JSONDecoder().decode(AccountIdentity.self, from: invalid))
    }

    func testOpaqueCredentialRejectsEmptyAndOversizeValues() throws {
        XCTAssertThrowsError(try AccountCredential(data: Data())) {
            XCTAssertEqual($0 as? CredentialStoreError, .invalidCredential)
        }
        XCTAssertThrowsError(try AccountCredential(data: Data(repeating: 1, count: AccountCredential.maximumByteCount + 1)))
        let maximum = try AccountCredential(data: Data(repeating: 1, count: AccountCredential.maximumByteCount))
        XCTAssertEqual(maximum.dataRepresentation().count, AccountCredential.maximumByteCount)
    }

    func testCredentialAndRecordDescriptionsAndReflectionAreRedacted() throws {
        let marker = "unit-test-only-redaction-marker"
        let credential = try AccountCredential(data: Data(marker.utf8))
        let record = CredentialRecord(credential: credential)
        for text in [
            String(describing: credential), String(reflecting: credential),
            String(describing: record), String(reflecting: record)
        ] {
            XCTAssertTrue(text.contains("redacted"))
            XCTAssertFalse(text.contains(marker))
        }
        var dumpText = ""
        dump(credential, to: &dumpText)
        dump(record, to: &dumpText)
        XCTAssertFalse(dumpText.contains(marker))
        XCTAssertFalse(dumpText.contains(Data(marker.utf8).base64EncodedString()))
        XCTAssertEqual(credential.dataRepresentation(), Data(marker.utf8))
    }

    func testMemoryStoreCRUDAndIdempotentRemoval() throws {
        let store = InMemoryCredentialStore()
        let identity = try AccountFixtures.identity()
        let first = try AccountFixtures.record(), second = try AccountFixtures.record("two")
        XCTAssertNil(try store.read(for: identity))
        try store.write(first, for: identity)
        XCTAssertEqual(try store.read(for: identity), first)
        try store.write(second, for: identity)
        XCTAssertEqual(try store.read(for: identity), second)
        try store.remove(for: identity)
        try store.remove(for: identity)
        XCTAssertNil(try store.read(for: identity))
    }

    func testMemoryScopeIsolationIncludingSameSiteNames() throws {
        let store = InMemoryCredentialStore()
        let installation = UUID(), account = UUID()
        let identities = [
            try AccountFixtures.identity(installation: installation, site: "same", account: account),
            try AccountFixtures.identity(site: "same", account: account),
            try AccountFixtures.identity(installation: installation, site: "other", account: account),
            try AccountFixtures.identity(installation: installation, site: "same")
        ]
        let records = try identities.indices.map { try AccountFixtures.record(String($0)) }
        for (identity, record) in zip(identities, records) { try store.write(record, for: identity) }
        try store.remove(for: identities[0])
        XCTAssertNil(try store.read(for: identities[0]))
        for index in 1..<identities.count {
            XCTAssertEqual(try store.read(for: identities[index]), records[index])
        }
    }

    func testIndependentMemoryInstancesDoNotShareCredentials() throws {
        let identity = try AccountFixtures.identity()
        let first = InMemoryCredentialStore(), second = InMemoryCredentialStore()
        try first.write(AccountFixtures.record(), for: identity)
        XCTAssertNil(try second.read(for: identity))
    }

    func testMemoryStoreConcurrentDifferentScopes() async throws {
        let store = InMemoryCredentialStore()
        let pairs = try (0..<50).map { index in
            (try AccountFixtures.identity(site: "site-\(index)"), try AccountFixtures.record(String(index)))
        }
        try await withThrowingTaskGroup(of: Void.self) { group in
            for (identity, record) in pairs {
                group.addTask { try store.write(record, for: identity) }
            }
            try await group.waitForAll()
        }
        for (identity, record) in pairs {
            XCTAssertEqual(try store.read(for: identity), record)
        }
    }

    func testPersistenceEnvelopeRoundTripAndRevision() throws {
        let record = try AccountFixtures.record()
        let encoded = CredentialRecordCodec.encode(record)
        XCTAssertEqual(try CredentialRecordCodec.decode(encoded), record)
        XCTAssertNotEqual(CredentialRecord(credential: record.credential).revision, record.revision)
    }

    func testCorruptAndFutureEnvelopesAreRejected() throws {
        let record = try AccountFixtures.record()
        let encoded = CredentialRecordCodec.encode(record)
        var future = encoded
        future[3] = 0x32
        for invalid in [Data(), Data(encoded.prefix(40)), Data(repeating: 0, count: 50), future] {
            XCTAssertThrowsError(try CredentialRecordCodec.decode(invalid)) {
                XCTAssertEqual($0 as? CredentialStoreError, .corruptItem)
            }
        }
        var invalidUUID = encoded
        invalidUUID.replaceSubrange(4..<40, with: Data(repeating: 120, count: 36))
        XCTAssertThrowsError(try CredentialRecordCodec.decode(invalidUUID))
    }
}
