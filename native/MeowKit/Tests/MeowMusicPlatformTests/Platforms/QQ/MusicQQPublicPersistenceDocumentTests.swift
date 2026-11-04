import Foundation
import XCTest
import MeowMusicFeature
import MeowStorage
@testable import MeowMusicPlatform

final class MusicQQPublicPersistenceDocumentTests: XCTestCase {
    func testStoreFactoryIsLazyAndFailureNeverBecomesEmptyDocument() async throws {
        let counter = QQPublicPersistenceCounter(), http = QQPublicPersistenceHTTP()
        let service = NativeMusicQQPublicService(storeFactory: {
            counter.add()
            throw EncryptedDocumentStoreError.keyProvider(.locked)
        }, transport: http)
        XCTAssertEqual(counter.get(), 0)
        try await service.setAccess(available: true, generation: 1)
        XCTAssertEqual(counter.get(), 0)
        do { _ = try await service.search(query: "synthetic", page: 1, limit: 30); XCTFail("Locked factory bypassed") }
        catch { XCTAssertEqual(error as? MusicServiceError, .accessUnavailable) }
        XCTAssertEqual(counter.get(), 1)
        let methods = try await http.methods()
        XCTAssertTrue(methods.isEmpty)
        await service.close()
    }

    func testActualEncryptedDocumentColdRestoreAndUnrelatedNamespaceRemainIndependent() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("QQPublicPersistence-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: false)
        defer { try? FileManager.default.removeItem(at: root) }
        let keys = QQPublicPersistenceKeys(), clock = QQPublicPersistenceClock()
        let store = try EncryptedDocumentStore(baseURL: root, namespace: MusicQQPublicPersistenceDocument.namespace, keyProvider: keys)
        let other = try EncryptedDocumentStore(baseURL: root, namespace: "synthetic.other", keyProvider: keys)
        let untouched = Data("synthetic-unrelated".utf8)
        try other.write(untouched, key: MusicQQPublicPersistenceDocument.key)
        let http = QQPublicPersistenceHTTP()
        let first = NativeMusicQQPublicService(storeFactory: { store }, transport: http, clock: { clock.now() })
        try await first.setAccess(available: true, generation: 1)
        _ = try await first.search(query: "synthetic", page: 1, limit: 30)
        let plaintext = try XCTUnwrap(store.read(key: MusicQQPublicPersistenceDocument.key))
        await first.close()
        let reopenedStore = try EncryptedDocumentStore(baseURL: root, namespace: MusicQQPublicPersistenceDocument.namespace, keyProvider: keys)
        let coldHTTP = QQPublicPersistenceHTTP()
        let cold = NativeMusicQQPublicService(storeFactory: { reopenedStore }, transport: coldHTTP, clock: { clock.now() })
        try await cold.setAccess(available: true, generation: 1)
        _ = try await cold.search(query: "synthetic", page: 1, limit: 30)
        let methods = try await coldHTTP.methods()
        XCTAssertEqual(methods, ["DoSearchForQQMusicMobile"])
        XCTAssertEqual(try reopenedStore.read(key: MusicQQPublicPersistenceDocument.key), plaintext)
        XCTAssertEqual(try other.read(key: MusicQQPublicPersistenceDocument.key), untouched)
        let files = try FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: nil)
        for folder in files {
            for file in try FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil) {
                let encrypted = try Data(contentsOf: file)
                XCTAssertNotEqual(encrypted, plaintext)
                XCTAssertNil(encrypted.range(of: Data("synthetic-session".utf8)))
            }
        }
        await cold.close()
    }

    func testDocumentCASDistinguishesMissingFromEncryptedEmptyAndKeepsWinner() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("QQPublicPersistence-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: false)
        defer { try? FileManager.default.removeItem(at: root) }
        let store = try EncryptedDocumentStore(baseURL: root, namespace: MusicQQPublicPersistenceDocument.namespace,
                                               keyProvider: QQPublicPersistenceKeys())
        let document = MusicQQPublicPersistenceDocument(storeFactory: { store })
        try store.write(Data(), key: MusicQQPublicPersistenceDocument.key)
        let bytes = try MusicQQPublicPersistenceCodec.encode(QQPublicPersistenceFixture.seed())
        XCTAssertThrowsError(try document.compareAndSwap(expected: nil, replacement: bytes)) {
            XCTAssertEqual($0 as? MusicServiceError, .storageConflict)
        }
        XCTAssertEqual(try document.read(), Data())
        try document.compareAndSwap(expected: Data(), replacement: bytes)
        XCTAssertThrowsError(try document.compareAndSwap(expected: nil, replacement: bytes)) {
            XCTAssertEqual($0 as? MusicServiceError, .storageConflict)
        }
        XCTAssertEqual(try document.read(), bytes)
    }

    func testProtectedReadFailureAndCorruptPlaintextDoNotOverwriteOrStartHTTP() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("QQPublicPersistence-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: false)
        defer { try? FileManager.default.removeItem(at: root) }
        let keys = QQPublicPersistenceKeys()
        let store = try EncryptedDocumentStore(baseURL: root, namespace: MusicQQPublicPersistenceDocument.namespace, keyProvider: keys)
        let bytes = Data("synthetic-corrupt-json".utf8)
        try store.write(bytes, key: MusicQQPublicPersistenceDocument.key)
        let http = QQPublicPersistenceHTTP()
        let service = NativeMusicQQPublicService(storeFactory: { store }, transport: http)
        keys.setLocked(true)
        try await service.setAccess(available: true, generation: 1)
        do { _ = try await service.search(query: "synthetic", page: 1, limit: 30); XCTFail("Protected read bypassed") }
        catch { XCTAssertEqual(error as? MusicServiceError, .accessUnavailable) }
        service.revokeAccess(generation: 1)
        try await service.setAccess(available: false, generation: 1)
        keys.setLocked(false)
        try await service.setAccess(available: true, generation: 2)
        do { _ = try await service.search(query: "synthetic", page: 1, limit: 30); XCTFail("Corrupt JSON reset") }
        catch { XCTAssertEqual(error as? MusicServiceError, .corruptDocument) }
        XCTAssertEqual(try store.read(key: MusicQQPublicPersistenceDocument.key), bytes)
        let methods = try await http.methods()
        XCTAssertTrue(methods.isEmpty)
        await service.close()
    }

    func testStrictSchemaRejectsMismatchedDatesBooleansPartialIdentityAndOversizedFields() throws {
        let clock = QQPublicPersistenceClock()
        var seed = QQPublicPersistenceFixture.seed()
        seed["qimei"] = ["q16": "synthetic-q16", "q36": "synthetic-q36", "savedAt": clock.now()]
        seed["session"] = ["uid": "synthetic", "sid": "synthetic", "day": MusicQQPublicPersistenceCodec.chinaDay(clock.now()),
                           "savedAt": clock.now()]
        let encoded = try MusicQQPublicPersistenceCodec.encode(seed)
        let original = try XCTUnwrap(JSONSerialization.jsonObject(with: encoded) as? [String: Any])
        var variants: [[String: Any]] = []
        var value = original; value["scope"] = "qq-account"; variants.append(value)
        value = original; value["version"] = true; variants.append(value)
        var device = try XCTUnwrap(original["device"] as? [String: Any])
        device["open_udid"] = nil
        value = original; value["device"] = device; variants.append(value)
        device = try XCTUnwrap(original["device"] as? [String: Any]); device["sdk"] = true
        value = original; value["device"] = device; variants.append(value)
        var session = try XCTUnwrap(original["session"] as? [String: Any]); session["day"] = "2000-01-01"
        value = original; value["session"] = session; variants.append(value)
        session = try XCTUnwrap(original["session"] as? [String: Any]); session["savedAt"] = nil
        value = original; value["session"] = session; variants.append(value)
        var qimei = try XCTUnwrap(original["qimei"] as? [String: Any]); qimei["q36"] = String(repeating: "q", count: 129)
        value = original; value["qimei"] = qimei; variants.append(value)
        for variant in variants {
            XCTAssertThrowsError(try MusicQQPublicPersistenceCodec.decode(JSONSerialization.data(withJSONObject: variant))) {
                XCTAssertEqual($0 as? MusicServiceError, .corruptDocument)
            }
        }
        XCTAssertEqual(try MusicQQPublicPersistenceCodec.encode(MusicQQPublicPersistenceCodec.decode(encoded)), encoded)
    }

    func testRevokeWaitsForEnteredAtomicCommitAndPreventsAllFollowingCommits() async throws {
        // Exercise the production synchronous fence with a synthetic blocking
        // CAS; no main-actor/UI thread is used for the blocking operation.
        let document = QQPublicPersistenceBlockingDocument()
        let state = MusicQQPublicPersistenceState(document: document)
        let token = try state.setAccess(available: true, generation: 1)
        _ = try state.read(token)
        let entered = expectation(description: "atomic commit entered")
        document.onEnter = { entered.fulfill() }
        let writer = Task {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                DispatchQueue.global().async {
                    do {
                        try state.save(QQPublicPersistenceFixture.seed(), token: token)
                        continuation.resume(returning: ())
                    } catch { continuation.resume(throwing: error) }
                }
            }
        }
        await fulfillment(of: [entered], timeout: 3)
        let attempting = expectation(description: "revoker dispatched")
        let revoked = expectation(description: "revoke returned")
        let returned = QQPublicPersistenceCounter()
        let revoker = Task {
            await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
                DispatchQueue.global().async {
                    attempting.fulfill()
                    state.revoke(generation: 1)
                    XCTAssertNotNil(document.peek(), "Revoke returned before the entered commit settled")
                    returned.add(); revoked.fulfill()
                    continuation.resume(returning: ())
                }
            }
        }
        await fulfillment(of: [attempting], timeout: 3)
        // A bounded negative observation after an explicit start signal, not a
        // fixed number of scheduler yields. Blocking work stays off the Swift
        // cooperative pool, including on low-core test hosts.
        try await Task.sleep(nanoseconds: 50_000_000)
        XCTAssertEqual(returned.get(), 0)
        document.release.signal()
        try await writer.value
        await revoker.value
        await fulfillment(of: [revoked], timeout: 3)
        XCTAssertNotNil(document.peek())
        XCTAssertThrowsError(try state.save(QQPublicPersistenceFixture.seed("b"), token: token)) {
            XCTAssertEqual($0 as? MusicServiceError, .accessUnavailable)
        }
    }
}

private final class QQPublicPersistenceBlockingDocument: MusicQQPublicPersisting, @unchecked Sendable {
    let release = DispatchSemaphore(value: 0)
    var onEnter: (@Sendable () -> Void)?
    private let lock = NSLock()
    private var bytes: Data?
    func read() -> Data? { peek() }
    func peek() -> Data? { lock.lock(); defer { lock.unlock() }; return bytes }
    func compareAndSwap(expected: Data?, replacement: Data) throws {
        onEnter?()
        guard release.wait(timeout: .now() + 3) == .success else { throw MusicServiceError.storageFailed }
        lock.lock(); defer { lock.unlock() }
        guard bytes == expected else { throw MusicServiceError.storageConflict }
        bytes = replacement
    }
}
