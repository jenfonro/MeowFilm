import XCTest
import Combine
import MeowStorage
@testable import Meowfilm

@MainActor
final class MeowfilmUserDataResetTests: XCTestCase {
    func testDatabaseAndDurableResourcesAreErasedOnlyAfterColdLaunch() throws {
        let f = try ResetFixture()
        defer { f.dispose() }
        XCTAssertTrue(f.hasDatabase)
        try f.store().request()
        XCTAssertTrue(f.hasDatabase); XCTAssertTrue(f.hasPayload)
        try f.store().completePendingReset()
        XCTAssertFalse(f.hasDatabase); XCTAssertFalse(f.hasPayload)
        XCTAssertEqual(try String(contentsOf: f.external), "original")
    }

    func testNoRequestDoesNotTouchDatabaseOrNetworkState() throws {
        let f = try ResetFixture()
        defer { f.dispose() }
        try f.store().completePendingReset()
        XCTAssertEqual(f.networkClears, 0)
        XCTAssertTrue(f.hasDatabase); XCTAssertTrue(f.hasPayload)
    }

    func testRequestOnlyRecordsConsentAndColdLaunchErasesExactlyOwnedData() throws {
        let f = try ResetFixture()
        defer { f.dispose() }
        try f.store().request(); try f.store().request()
        XCTAssertTrue(f.pending); XCTAssertTrue(f.hasDatabase); XCTAssertTrue(f.hasPayload)
        try f.store().completePendingReset()
        XCTAssertFalse(f.pending); XCTAssertFalse(f.hasDatabase); XCTAssertFalse(f.hasPayload)
        XCTAssertEqual(f.networkClears, 1)
        XCTAssertEqual(try String(contentsOf: f.external), "original")
        XCTAssertTrue(FileManager.default.fileExists(atPath: f.unrelated.path))
        try f.store().completePendingReset()
        XCTAssertEqual(f.networkClears, 1, "No repeated wipe after the marker is gone")
    }

    func testLastStepFailureRetainsRestartFenceAndRetryIsIdempotent() throws {
        let f = try ResetFixture()
        defer { f.dispose() }
        try f.store().request(); f.failNetwork = true
        XCTAssertThrowsError(try f.store().completePendingReset()) {
            XCTAssertEqual($0 as? MeowfilmUserDataReset.Failure, .cookies)
        }
        XCTAssertTrue(f.pending); XCTAssertFalse(f.hasPayload); XCTAssertFalse(f.hasDatabase)
        f.failNetwork = false
        try f.store().completePendingReset()
        XCTAssertFalse(f.pending); XCTAssertEqual(f.networkClears, 1)
    }

    func testNestedSymbolicLinkIsRemovedWithoutTouchingExternalOriginal() throws {
        let f = try ResetFixture()
        defer { f.dispose() }
        try FileManager.default.createSymbolicLink(
            at: f.payloads[0].appendingPathComponent("external-link"), withDestinationURL: f.external)
        try f.store().request(); try f.store().completePendingReset()
        XCTAssertEqual(try String(contentsOf: f.external), "original")
    }

    func testLinkedParentFailsClosedWithoutDeletingItsTarget() throws {
        let f = try ResetFixture()
        defer { f.dispose() }
        try f.store().request()
        let moved = f.root.appendingPathComponent("external-temporary")
        try FileManager.default.moveItem(at: f.temporary, to: moved)
        try FileManager.default.createSymbolicLink(at: f.temporary, withDestinationURL: moved)
        XCTAssertThrowsError(try f.store().completePendingReset()) {
            XCTAssertEqual($0 as? MeowfilmUserDataReset.Failure, .files)
        }
        XCTAssertTrue(f.pending)
        XCTAssertTrue(FileManager.default.fileExists(atPath: moved.appendingPathComponent("meowfilm/fixture").path))
        XCTAssertEqual(f.networkClears, 0)
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
        f.failNetwork = true
        let launch = MeowfilmLaunchController(factory: { f.store() })
        var admitted = 0
        let observation = launch.$phase.sink { if $0 == .ready { admitted += 1 } }
        defer { observation.cancel() }
        launch.start()
        await phase(.failed(.cookies), on: launch)
        XCTAssertEqual(admitted, 0)
        XCTAssertTrue(f.pending)
        f.failNetwork = false
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

/// Fresh synthetic database and resources, closed before a destructive cold reset.
/// Never touches the App's production data root or global network stores.
private final class ResetFixture: @unchecked Sendable {
    let root: URL, support: URL, temporary: URL, external: URL, unrelated: URL
    let payloads: [URL]
    private let lock = NSLock()
    private var clears = 0
    private var networkFailure = false
    var failNetwork: Bool {
        get { lock.lock(); defer { lock.unlock() }; return networkFailure }
        set { lock.lock(); defer { lock.unlock() }; networkFailure = newValue }
    }
    var networkClears: Int { lock.lock(); defer { lock.unlock() }; return clears }
    var pending: Bool { FileManager.default.fileExists(atPath: support.appendingPathComponent("meowfilm-reset.pending").path) }
    var hasPayload: Bool { payloads.contains { FileManager.default.fileExists(atPath: $0.path) } }
    var hasDatabase: Bool { FileManager.default.fileExists(atPath: payloads[0].appendingPathComponent("storage-v1/meowfilm.sqlite3").path) }

    @MainActor init() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        support = root.appendingPathComponent("support", isDirectory: true)
        temporary = root.appendingPathComponent("temporary", isDirectory: true)
        external = root.appendingPathComponent("original.js")
        unrelated = support.appendingPathComponent("other-owner")
        payloads = [support.appendingPathComponent("meowfilm"), temporary.appendingPathComponent("meowfilm")]
        for url in payloads {
            try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
            try Data("synthetic".utf8).write(to: url.appendingPathComponent("fixture"))
        }
        let database = try appStorageTestOpen(payloads[0])
        try appStorageTestWait {
            _ = try await database.transaction { try $0.execute("INSERT INTO ios_search_history(text,used_at) VALUES('synthetic',1)") }
            try await database.close()
        }
        let source = payloads[0].appendingPathComponent("cat/source.js")
        try FileManager.default.createDirectory(at: source.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("// synthetic third-party source".utf8).write(to: source)
        try Data("original".utf8).write(to: external)
        try Data("unrelated".utf8).write(to: unrelated)
    }

    func store() -> MeowfilmUserDataReset {
        .init(support: support, temporary: temporary, clearNetworkState: { [self] in
            lock.lock(); defer { lock.unlock() }
            if networkFailure { throw MeowfilmUserDataReset.Failure.cookies }
            clears += 1
        })
    }
    func dispose() { do { try FileManager.default.removeItem(at: root) } catch { XCTFail("Reset fixture cleanup failed: \(error)") } }
}
