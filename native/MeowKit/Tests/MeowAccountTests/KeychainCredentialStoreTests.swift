import Foundation
import Security
import XCTest
@testable import MeowAccounts

/// Every operation uses an injected fake driver. No SecItem/LAContext interaction.
final class KeychainCredentialStoreTests: XCTestCase {
    private let service = "test.meowfilm.accounts"

    func testInitializationIsPassiveAndDefaultPolicyIsDeviceOnly() throws {
        let driver = FakeKeychainDriver()
        let store = try KeychainCredentialStore(service: service, driver: driver)
        XCTAssertEqual(store.accessibility, .whenUnlockedThisDeviceOnly)
        XCTAssertTrue(driver.calls.isEmpty)
        XCTAssertEqual(Set(CredentialAccessibility.allCases), [
            .whenUnlockedThisDeviceOnly, .afterFirstUnlockThisDeviceOnly
        ])
    }

    func testStoredAccessibilityMustMatchExceptEarlierStricterItemsForBackgroundPolicy() {
        let unlocked = kSecAttrAccessibleWhenUnlockedThisDeviceOnly as String
        let background = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly as String
        XCTAssertEqual(KeychainAccessibilityAdmission.classify(
            stored: background, configured: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly), .current)
        XCTAssertEqual(KeychainAccessibilityAdmission.classify(
            stored: unlocked, configured: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly), .adopt)
        XCTAssertEqual(KeychainAccessibilityAdmission.classify(
            stored: unlocked, configured: kSecAttrAccessibleWhenUnlockedThisDeviceOnly), .current)
        XCTAssertEqual(KeychainAccessibilityAdmission.classify(
            stored: background, configured: kSecAttrAccessibleWhenUnlockedThisDeviceOnly), .rejected)
        for stored in [nil, kSecAttrAccessibleAfterFirstUnlock as String, "unknown"] {
            XCTAssertEqual(KeychainAccessibilityAdmission.classify(
                stored: stored, configured: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly), .rejected)
            XCTAssertEqual(KeychainAccessibilityAdmission.classify(
                stored: stored, configured: kSecAttrAccessibleWhenUnlockedThisDeviceOnly), .rejected)
        }
    }

    func testInvalidConfigurationNeverCallsDriver() {
        let driver = FakeKeychainDriver()
        for service in ["", " ", " example", "example\n", "a\u{0}b"] {
            XCTAssertThrowsError(try KeychainCredentialStore(service: service, driver: driver)) {
                XCTAssertEqual($0 as? CredentialStoreError, .invalidConfiguration)
            }
        }
        XCTAssertTrue(driver.calls.isEmpty)
    }

    func testReadIsExplicitlyScopedAndDecodesRecord() throws {
        let record = try AccountFixtures.record()
        let identity = try AccountFixtures.identity()
        let driver = FakeKeychainDriver(readResult: KeychainReadResult(
            status: .success, data: CredentialRecordCodec.encode(record)
        ))
        let store = try KeychainCredentialStore(service: service, driver: driver)
        XCTAssertEqual(try store.read(for: identity), record)
        XCTAssertEqual(driver.calls, [
            .init(operation: .read, key: KeychainItemKey(service: service, identity: identity),
                  accessibility: .whenUnlockedThisDeviceOnly)
        ])
    }

    func testMissingRecordIsNilButMalformedSuccessIsError() throws {
        let identity = try AccountFixtures.identity()
        let absent = try KeychainCredentialStore(service: service, driver: FakeKeychainDriver())
        XCTAssertNil(try absent.read(for: identity))
        let malformed: [Data?] = [nil, Data(), Data("unit-test-only-malformed".utf8)]
        for data in malformed {
            let driver = FakeKeychainDriver(readResult: KeychainReadResult(status: .success, data: data))
            let store = try KeychainCredentialStore(service: service, driver: driver)
            XCTAssertThrowsError(try store.read(for: identity)) {
                XCTAssertEqual($0 as? CredentialStoreError, .corruptItem)
            }
            XCTAssertEqual(driver.calls.map(\.operation), [.read])
        }
    }

    func testExistingItemUsesOneUpdateAndExplicitBackgroundPolicy() throws {
        let driver = FakeKeychainDriver()
        let store = try KeychainCredentialStore(
            service: service, accessibility: .afterFirstUnlockThisDeviceOnly, driver: driver
        )
        try store.write(AccountFixtures.record(), for: AccountFixtures.identity())
        XCTAssertEqual(driver.calls.map(\.operation), [.update])
        XCTAssertEqual(driver.calls.first?.accessibility, .afterFirstUnlockThisDeviceOnly)
    }

    func testMissingItemUsesUpdateThenAddWithSameScopeAndPolicy() throws {
        let driver = FakeKeychainDriver(updates: [.notFound])
        let store = try KeychainCredentialStore(service: service, driver: driver)
        let record = try AccountFixtures.record()
        try store.write(record, for: AccountFixtures.identity())
        XCTAssertEqual(driver.calls.map(\.operation), [.update, .add])
        XCTAssertEqual(driver.calls[0].key, driver.calls[1].key)
        XCTAssertEqual(driver.calls[0].accessibility, driver.calls[1].accessibility)
        XCTAssertEqual(try driver.writtenRecords(), [record, record])
    }

    func testDuplicateAddHasExactlyOneBoundedUpdateRetry() throws {
        let driver = FakeKeychainDriver(updates: [.notFound, .success], additions: [.duplicate])
        let store = try KeychainCredentialStore(service: service, driver: driver)
        try store.write(AccountFixtures.record(), for: AccountFixtures.identity())
        XCTAssertEqual(driver.calls.map(\.operation), [.update, .add, .update])
    }

    func testRaceThatRemainsUnresolvedFailsRatherThanLooping() throws {
        let driver = FakeKeychainDriver(updates: [.notFound, .notFound], additions: [.duplicate])
        let store = try KeychainCredentialStore(service: service, driver: driver)
        XCTAssertThrowsError(try store.write(AccountFixtures.record(), for: AccountFixtures.identity())) {
            XCTAssertEqual($0 as? CredentialStoreError, .conflict)
        }
        XCTAssertEqual(driver.calls.map(\.operation), [.update, .add, .update])
    }

    func testReadErrorsAreExplicitAndNeverFallbackOrMutate() throws {
        let errors: [CredentialStoreError] = [
            .accessDenied, .missingEntitlement, .interactionNotAllowed, .unavailable,
            .accessibilityMismatch, .systemStatus(-1)
        ]
        for error in errors {
            let driver = FakeKeychainDriver(readResult: KeychainReadResult(status: .failure(error), data: nil))
            let store = try KeychainCredentialStore(service: service, driver: driver)
            XCTAssertThrowsError(try store.read(for: AccountFixtures.identity())) {
                XCTAssertEqual($0 as? CredentialStoreError, error)
            }
            XCTAssertEqual(driver.calls.map(\.operation), [.read])
        }
    }

    func testUpdateDeniedDoesNotTryAddOrDelete() throws {
        let driver = FakeKeychainDriver(updates: [.failure(.accessDenied)])
        let store = try KeychainCredentialStore(service: service, driver: driver)
        XCTAssertThrowsError(try store.write(AccountFixtures.record(), for: AccountFixtures.identity())) {
            XCTAssertEqual($0 as? CredentialStoreError, .accessDenied)
        }
        XCTAssertEqual(driver.calls.map(\.operation), [.update])
    }

    func testMissingDeleteIsSuccessAndDeletionIsScoped() throws {
        let identity = try AccountFixtures.identity()
        let driver = FakeKeychainDriver(deleteResult: .notFound)
        let store = try KeychainCredentialStore(service: service, driver: driver)
        try store.remove(for: identity)
        XCTAssertEqual(driver.calls, [
            .init(operation: .delete, key: KeychainItemKey(service: service, identity: identity), accessibility: nil)
        ])
    }

    func testFailedDeleteIsNotReportedAsSuccess() throws {
        let driver = FakeKeychainDriver(deleteResult: .failure(.interactionNotAllowed))
        let store = try KeychainCredentialStore(service: service, driver: driver)
        XCTAssertThrowsError(try store.remove(for: AccountFixtures.identity())) {
            XCTAssertEqual($0 as? CredentialStoreError, .interactionNotAllowed)
        }
    }

    func testStorageKeysSeparateAllIdentityDimensionsAndServices() throws {
        let installation = UUID(), account = UUID()
        let identities = [
            try AccountFixtures.identity(installation: installation, site: "a:b/c", account: account),
            try AccountFixtures.identity(site: "a:b/c", account: account),
            try AccountFixtures.identity(installation: installation, site: "a:b", account: account),
            try AccountFixtures.identity(installation: installation, site: "a:b/c")
        ]
        let keys = identities.map { KeychainItemKey(service: service, identity: $0) }
        XCTAssertEqual(Set(keys).count, 4)
        XCTAssertNotEqual(keys[0], KeychainItemKey(service: service + ".other", identity: identities[0]))
        XCTAssertFalse(keys[0].account.contains("a:b/c"))
    }

    func testVisuallyEquivalentOpaqueSiteIDsRemainSeparate() throws {
        let installation = UUID(), account = UUID()
        let a = try AccountFixtures.identity(installation: installation, site: "\u{00e9}", account: account)
        let b = try AccountFixtures.identity(installation: installation, site: "e\u{0301}", account: account)
        XCTAssertNotEqual(a, b)
        XCTAssertNotEqual(KeychainItemKey(service: service, identity: a), KeychainItemKey(service: service, identity: b))
        let memory = InMemoryCredentialStore()
        let record = try AccountFixtures.record()
        try memory.write(record, for: a)
        XCTAssertNil(try memory.read(for: b))
    }

    #if !os(iOS) && !os(macOS)
    func testUnsupportedPlatformNeverFallsBackToMemory() {
        XCTAssertThrowsError(try KeychainCredentialStore(service: service)) {
            XCTAssertEqual($0 as? CredentialStoreError, .unsupportedPlatform)
        }
    }
    #endif
}
