import XCTest
import Combine
import Security
@testable import Meowfilm

final class MeowfilmUserDataResetTests: XCTestCase {
    func testFilmDocumentsAndScriptRuntimeAreErasedOnlyAfterColdLaunch() throws {
        let f = try ResetFixture()
        defer { f.dispose() }
        let paths = ["documents/film.magic-rules.v1/rules.json",
                     "documents/film.video-sources.v1/catalog.json",
                     "script-runtime/cat/fixture.json"]
            .map { f.support.appendingPathComponent("meowfilm/" + $0) }
        for path in paths {
            try FileManager.default.createDirectory(at: path.deletingLastPathComponent(), withIntermediateDirectories: true)
            try Data("synthetic".utf8).write(to: path)
        }
        try f.store().request()
        XCTAssertTrue(paths.allSatisfy { FileManager.default.fileExists(atPath: $0.path) })
        try f.store().completePendingReset()
        XCTAssertTrue(paths.allSatisfy { !FileManager.default.fileExists(atPath: $0.path) })
        XCTAssertEqual(try String(contentsOf: f.external), "original")
    }

    func testNoRequestDoesNotTouchFilesPreferencesOrKeychain() throws {
        let f = try ResetFixture()
        defer { f.dispose() }
        try f.store().completePendingReset()
        XCTAssertEqual(f.deletedServices, [])
        XCTAssertEqual(f.networkClears, 0)
        XCTAssertTrue(f.hasPayload)
        XCTAssertEqual(f.defaults.string(forKey: "fixture"), "saved")
    }

    func testRequestOnlyRecordsConsentAndColdLaunchErasesExactlyOwnedData() throws {
        let f = try ResetFixture()
        defer { f.dispose() }
        let firstProcess = f.store()
        try firstProcess.request()
        try firstProcess.request()
        XCTAssertTrue(f.pending)
        XCTAssertTrue(f.hasPayload)
        XCTAssertTrue(f.deletedServices.isEmpty)
        XCTAssertEqual(f.defaults.string(forKey: "fixture"), "saved")

        try f.store().completePendingReset()
        XCTAssertFalse(f.pending)
        XCTAssertFalse(f.hasPayload)
        XCTAssertNil(f.defaults.string(forKey: "fixture"))
        XCTAssertEqual(Set(f.deletedServices), Set(ResetFixture.expectedServices))
        XCTAssertEqual(f.remainingServices, ["other.product.credentials"])
        XCTAssertEqual(f.networkClears, 1)
        XCTAssertEqual(try String(contentsOf: f.external), "original")
        XCTAssertTrue(FileManager.default.fileExists(atPath: f.unrelated.path))
        let deletes = f.deletedServices
        try f.store().completePendingReset()
        XCTAssertEqual(f.deletedServices, deletes, "No repeated wipe after the marker is gone")
    }

    func testPartialKeychainFailureRetainsFenceAndRetryIsIdempotent() throws {
        let f = try ResetFixture()
        defer { f.dispose() }
        try f.store().request()
        f.failingService = ResetFixture.expectedServices[1]
        XCTAssertThrowsError(try f.store().completePendingReset()) {
            XCTAssertEqual($0 as? MeowfilmUserDataReset.Failure, .keychainPermission)
        }
        XCTAssertTrue(f.pending)
        XCTAssertFalse(f.hasPayload)
        XCTAssertEqual(f.defaults.string(forKey: "fixture"), "saved")
        f.failingService = nil
        try f.store().completePendingReset()
        XCTAssertFalse(f.pending)
        XCTAssertEqual(f.remainingServices, ["other.product.credentials"])
    }

    func testLastStepFailureCannotReportSuccessOrDiscardRestartFence() throws {
        let f = try ResetFixture()
        defer { f.dispose() }
        try f.store().request()
        f.failNetwork = true
        XCTAssertThrowsError(try f.store().completePendingReset())
        XCTAssertTrue(f.pending)
        f.failNetwork = false
        try f.store().completePendingReset()
        XCTAssertFalse(f.pending)
    }

    func testNestedSymbolicLinkIsRemovedWithoutTouchingExternalOriginal() throws {
        let f = try ResetFixture()
        defer { f.dispose() }
        try FileManager.default.createSymbolicLink(
            at: f.payloads[0].appendingPathComponent("external-link"), withDestinationURL: f.external)
        try f.store().request()
        try f.store().completePendingReset()
        XCTAssertEqual(try String(contentsOf: f.external), "original")
    }

    func testLinkedParentFailsClosedWithoutDeletingItsTarget() throws {
        let f = try ResetFixture()
        defer { f.dispose() }
        try f.store().request()
        let moved = f.root.appendingPathComponent("external-caches")
        try FileManager.default.moveItem(at: f.caches, to: moved)
        try FileManager.default.createSymbolicLink(at: f.caches, withDestinationURL: moved)
        XCTAssertThrowsError(try f.store().completePendingReset())
        XCTAssertTrue(f.pending)
        XCTAssertTrue(FileManager.default.fileExists(atPath: moved.appendingPathComponent("meowfilm/fixture").path))
        XCTAssertTrue(f.deletedServices.isEmpty)
    }

    func testKeychainDeleteIsExactServiceAndDoesNotRequireReadingOldPolicy() {
        XCTAssertEqual(Set(MeowfilmUserDataReset.keychainServices), Set(ResetFixture.expectedServices))
        for service in MeowfilmUserDataReset.keychainServices {
            let query = MeowfilmUserDataReset.keychainQuery(service: service)
            XCTAssertEqual(query[kSecClass as String] as? String, kSecClassGenericPassword as String)
            XCTAssertEqual(query[kSecAttrService as String] as? String, service)
            XCTAssertEqual(query[kSecAttrSynchronizable as String] as? Bool, false)
            XCTAssertEqual(query[kSecUseDataProtectionKeychain as String] as? Bool, true)
            XCTAssertNil(query[kSecAttrAccessible as String])
            XCTAssertNil(query[kSecAttrAccessGroup as String])
            XCTAssertNil(query[kSecReturnData as String])
        }
    }
}

@MainActor
final class MeowfilmLaunchResetTests: XCTestCase {
    func testRequestStopsBusinessButDoesNotEraseUntilANewLaunch() async throws {
        let f = try ResetFixture()
        defer { f.dispose() }
        let launch = MeowfilmLaunchController(factory: { f.store() })
        launch.start()
        await ready(launch)
        var stops = 0
        try await launch.requestReset { stops += 1 }
        XCTAssertEqual(stops, 1)
        XCTAssertEqual(launch.phase, .restartRequired)
        XCTAssertTrue(f.pending)
        XCTAssertTrue(f.hasPayload)
        XCTAssertTrue(f.deletedServices.isEmpty)
        launch.start() // Resume/reentry is not a cold process.
        try await launch.requestReset { stops += 1 }
        XCTAssertEqual(stops, 1)
        XCTAssertEqual(launch.phase, .restartRequired)
        XCTAssertTrue(f.hasPayload)

        let cold = MeowfilmLaunchController(factory: { f.store() })
        cold.start()
        await ready(cold)
        XCTAssertFalse(f.pending)
        XCTAssertFalse(f.hasPayload)
    }

    func testCleanupFailureNeverAdmitsBusinessAndExplicitRetryFinishes() async throws {
        let f = try ResetFixture()
        defer { f.dispose() }
        try f.store().request()
        f.failingService = ResetFixture.expectedServices[0]
        let launch = MeowfilmLaunchController(factory: { f.store() })
        var admitted = 0
        let observation = launch.$phase.sink { if $0 == .ready { admitted += 1 } }
        defer { observation.cancel() }
        launch.start()
        await phase(.failed(.keychainPermission), on: launch)
        XCTAssertEqual(admitted, 0)
        XCTAssertTrue(f.pending)
        f.failingService = nil
        launch.start()
        await ready(launch)
        XCTAssertEqual(admitted, 1)
        XCTAssertFalse(f.pending)
    }

    func testFailedRequestDoesNotStopBusinessAndCanBeRetried() async throws {
        let f = try ResetFixture()
        defer { f.dispose() }
        let launch = MeowfilmLaunchController(factory: { f.store() })
        launch.start()
        await ready(launch)
        try FileManager.default.removeItem(at: f.support)
        try Data("not a directory".utf8).write(to: f.support)
        var stops = 0
        do { try await launch.requestReset { stops += 1 }; XCTFail("Invalid request accepted") }
        catch { XCTAssertEqual(error as? MeowfilmUserDataReset.Failure, .request) }
        XCTAssertEqual(stops, 0)
        XCTAssertEqual(launch.phase, .ready)
        try FileManager.default.removeItem(at: f.support)
        try await launch.requestReset { stops += 1 }
        XCTAssertEqual(stops, 1)
        XCTAssertTrue(f.pending)
    }

    private func ready(_ launch: MeowfilmLaunchController) async { await phase(.ready, on: launch) }
    private func phase(_ expected: MeowfilmLaunchController.Phase, on launch: MeowfilmLaunchController) async {
        let arrived = expectation(description: "launch phase \(expected)")
        let observation = launch.$phase.first { $0 == expected }.sink { _ in arrived.fulfill() }
        await fulfillment(of: [arrived], timeout: 5)
        observation.cancel()
        XCTAssertEqual(launch.phase, expected)
    }
}

/// All files/defaults/services are synthetic. Never calls the real Keychain,
/// network stores, standard preferences or the App's production reset factory.
private final class ResetFixture: @unchecked Sendable {
    static let expectedServices = [
        "dev.meowfilm.aggregate.music.accounts.v1",
        "dev.meowfilm.aggregate.music.client-context.wy.v1",
        "dev.meowfilm.aggregate.music.client-context.kg.v1",
        "dev.meowfilm.aggregate.music.qq-public.v1",
        "dev.meowfilm.aggregate.music.library.v1",
        "dev.meowfilm.aggregate.music.playback-display.v1",
        "dev.meowfilm.aggregate.music.source-discovery.v1",
        "dev.meowfilm.aggregate.music.account-url-cache.v1"
    ]
    let root: URL, support: URL, caches: URL, temporary: URL, external: URL, unrelated: URL
    let payloads: [URL]
    let defaults: UserDefaults, domain: String
    private let lock = NSLock()
    private var services = Set(expectedServices + ["other.product.credentials"])
    private var deleted: [String] = []
    private var clears = 0
    var failingService: String?
    var failNetwork = false
    var deletedServices: [String] { lock.lock(); defer { lock.unlock() }; return deleted }
    var remainingServices: Set<String> { lock.lock(); defer { lock.unlock() }; return services }
    var networkClears: Int { lock.lock(); defer { lock.unlock() }; return clears }
    var pending: Bool { FileManager.default.fileExists(atPath: support.appendingPathComponent("meowfilm-reset.pending").path) }
    var hasPayload: Bool { payloads.contains { FileManager.default.fileExists(atPath: $0.path) } }

    init() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        support = root.appendingPathComponent("support", isDirectory: true)
        caches = root.appendingPathComponent("caches", isDirectory: true)
        temporary = root.appendingPathComponent("tmp", isDirectory: true)
        external = root.appendingPathComponent("original.js")
        unrelated = support.appendingPathComponent("other-owner")
        payloads = [support.appendingPathComponent("meowfilm"), caches.appendingPathComponent("meowfilm"),
                    caches.appendingPathComponent("meowfilm.music.audio-cache.v1"), temporary.appendingPathComponent("meowfilm")]
        domain = "MeowfilmResetTests." + UUID().uuidString
        defaults = try XCTUnwrap(UserDefaults(suiteName: domain))
        for url in payloads {
            try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
            try Data("synthetic".utf8).write(to: url.appendingPathComponent("fixture"))
        }
        try Data("original".utf8).write(to: external)
        try Data("unrelated".utf8).write(to: unrelated)
        defaults.set("saved", forKey: "fixture")
    }

    func store() -> MeowfilmUserDataReset {
        .init(support: support, caches: caches, temporary: temporary, defaults: defaults, defaultsDomain: domain,
            deleteKeychain: { [self] service in
                lock.lock(); defer { lock.unlock() }
                if service == failingService { throw MeowfilmUserDataReset.Failure.keychainPermission }
                deleted.append(service); services.remove(service)
            }, clearNetworkState: { [self] in
                lock.lock(); defer { lock.unlock() }
                if failNetwork { throw MeowfilmUserDataReset.Failure.cookies }
                clears += 1
            })
    }
    func dispose() {
        defaults.removePersistentDomain(forName: domain)
        try? FileManager.default.removeItem(at: root)
    }
}
