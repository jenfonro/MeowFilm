import Foundation
import XCTest
import MeowStorage
import MeowMusicFeature
@testable import MeowMusicPlatform

final class MusicClientStorageFailureTests: XCTestCase {
    func testFirstDeviceWriteReportsDefiniteKeyFailuresWithoutCommittingForBothPlatforms() async throws {
        for platform in [MusicAccountPlatform.netease, .kugou] {
            for (cause, expected) in [
                (EncryptedDocumentKeyError.policyMismatch, MusicServiceError.storagePolicyMismatch),
                (.missingEntitlement, .storagePermissionDenied),
                (.locked, .accessUnavailable), (.backendFailure, .storageFailed)
            ] {
                let root = try directory()
                defer { try? FileManager.default.removeItem(at: root) }
                let keys = ClientStorageKeys(failure: cause)
                let store = try EncryptedDocumentStore(baseURL: root, namespace: "test.client", keyProvider: keys)
                let client = NativeMusicClientService(storeFactory: { _ in store })
                try client.setAccess(available: true, generation: 1)
                for _ in 0..<2 {
                    do {
                        _ = try await client.nativeClientDevice(platform: platform)
                        XCTFail("A failed device save must not publish an identity")
                    } catch { XCTAssertEqual(error as? MusicServiceError, expected) }
                    XCTAssertNil(try store.read(key: "client-device-v1"))
                }
                client.close()
            }
        }
    }

    func testQQProtectedDocumentReportsTheSameDefiniteFailures() throws {
        for (cause, expected) in [
            (EncryptedDocumentKeyError.policyMismatch, MusicServiceError.storagePolicyMismatch),
            (.missingEntitlement, .storagePermissionDenied)
        ] {
            let root = try directory()
            defer { try? FileManager.default.removeItem(at: root) }
            let store = try EncryptedDocumentStore(baseURL: root, namespace: "test.qq",
                                                  keyProvider: ClientStorageKeys(failure: cause))
            let document = MusicQQPublicPersistenceDocument(storeFactory: { store })
            XCTAssertNil(try document.read())
            XCTAssertThrowsError(try document.compareAndSwap(expected: nil, replacement: Data("fixture".utf8))) {
                XCTAssertEqual($0 as? MusicServiceError, expected)
            }
            XCTAssertNil(try document.read())
        }
    }

    func testNewProtectedIdentityPersistsAcrossClientRestart() async throws {
        for platform in [MusicAccountPlatform.netease, .kugou] {
            let root = try directory()
            defer { try? FileManager.default.removeItem(at: root) }
            let store = try EncryptedDocumentStore(baseURL: root, namespace: "test.identity", keyProvider: ClientStorageKeys())
            let first = NativeMusicClientService(storeFactory: { _ in store })
            try first.setAccess(available: true, generation: 1)
            let identity = try await first.nativeClientDevice(platform: platform)
            let committed = try XCTUnwrap(store.read(key: "client-device-v1"))
            first.close()
            let second = NativeMusicClientService(storeFactory: { _ in store })
            try second.setAccess(available: true, generation: 1)
            let restored = try await second.nativeClientDevice(platform: platform)
            XCTAssertEqual(restored.fields, identity.fields)
            XCTAssertEqual(try store.read(key: "client-device-v1"), committed)
            second.close()
        }
    }

    func testCancellationDuringAdmittedWriteStillCommitsAndNextReadSucceeds() async throws {
        let root = try directory()
        defer { try? FileManager.default.removeItem(at: root) }
        let entered = expectation(description: "key creation entered")
        let release = DispatchSemaphore(value: 0)
        let keys = ClientStorageKeys(beforeCreate: {
            entered.fulfill()
            guard release.wait(timeout: .now() + 5) == .success else {
                throw EncryptedDocumentKeyError.backendFailure
            }
        })
        let store = try EncryptedDocumentStore(baseURL: root, namespace: "test.cancel", keyProvider: keys)
        let client = NativeMusicClientService(storeFactory: { _ in store })
        try client.setAccess(available: true, generation: 1)
        let pending = Task.detached { try await client.nativeClientDevice(platform: .netease) }
        await fulfillment(of: [entered], timeout: 3)
        pending.cancel(); release.signal()
        do { _ = try await pending.value; XCTFail("Cancelled subscriber received a device") }
        catch { XCTAssertTrue(error is CancellationError) }
        let committed = try XCTUnwrap(store.read(key: "client-device-v1"))
        let next = try await client.nativeClientDevice(platform: .netease)
        let again = try await client.nativeClientDevice(platform: .netease)
        XCTAssertEqual(next.fields, again.fields)
        XCTAssertEqual(try store.read(key: "client-device-v1"), committed)
        client.close()
    }

    private func directory() throws -> URL {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: false)
        return root
    }
}

private final class ClientStorageKeys: EncryptedDocumentKeyProvider, @unchecked Sendable {
    private let lock = NSLock()
    private var value: Data?
    private let failure: EncryptedDocumentKeyError?
    private let beforeCreate: @Sendable () throws -> Void
    init(failure: EncryptedDocumentKeyError? = nil, beforeCreate: @escaping @Sendable () throws -> Void = {}) {
        self.failure = failure; self.beforeCreate = beforeCreate
    }
    func readKey(namespace: String) throws -> Data? {
        lock.lock(); defer { lock.unlock() }
        if let failure { throw failure }
        return value
    }
    func createKeyIfAbsent(namespace: String) throws -> Data {
        try beforeCreate()
        lock.lock(); defer { lock.unlock() }
        if let failure { throw failure }
        if let value { return value }
        let created = Data(repeating: 0x41, count: 32)
        value = created
        return created
    }
}
