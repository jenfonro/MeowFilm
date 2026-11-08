import XCTest
import MeowMusicFeature

final class MusicDerivedQueueAuthorizationTests: XCTestCase {
    func testClosingDerivedQueueDoesNotRevokeParentOrSibling() throws {
        let parent = MusicQueueAuthorization(), child = MusicQueueAuthorization(parent: parent)
        let sibling = MusicQueueAuthorization(parent: parent)
        child.revoke()
        XCTAssertFalse(child.isActive)
        XCTAssertThrowsError(try child.perform { XCTFail("revoked child ran") })
        XCTAssertTrue(parent.isActive); XCTAssertTrue(sibling.isActive)
        XCTAssertNoThrow(try parent.check())
        XCTAssertNoThrow(try sibling.perform {})
    }
    func testParentRevokesDescendantsSynchronouslyAndFinishesObservers() async throws {
        let root = MusicQueueAuthorization(), child = MusicQueueAuthorization(parent: root)
        let leaf = MusicQueueAuthorization(parent: child)
        var events = leaf.revocations().makeAsyncIterator()
        root.revoke()
        XCTAssertFalse(child.isActive); XCTAssertFalse(leaf.isActive)
        XCTAssertThrowsError(try leaf.perform { XCTFail("late write") })
        let event = await events.next()
        XCTAssertNotNil(event)
        let ended = await events.next()
        XCTAssertNil(ended)
        let late = MusicQueueAuthorization(parent: root)
        XCTAssertFalse(late.isActive)
    }
    func testOperationCancellationIsLeaseLocalAndRemovedOnlyByItsOwner() throws {
        let parent = MusicQueueAuthorization(), child = MusicQueueAuthorization(parent: parent)
        let sibling = MusicQueueAuthorization(parent: parent), counter = QueueCancellationCounter()
        let completed = UUID()
        try child.register(UUID()) { counter.add("child") }
        try child.register(completed) { counter.add("completed") }
        try sibling.register(UUID()) { counter.add("sibling") }
        child.remove(completed)
        child.revoke()
        XCTAssertEqual(counter.values, ["child"])
        XCTAssertTrue(parent.isActive); XCTAssertTrue(sibling.isActive)
        XCTAssertThrowsError(try child.register(UUID()) { counter.add("late") })
        XCTAssertEqual(counter.values, ["child", "late"])
        parent.revoke()
        XCTAssertEqual(counter.values, ["child", "late", "sibling"])
    }
}

private final class QueueCancellationCounter: @unchecked Sendable {
    private let lock = NSLock()
    private var storage: [String] = []
    var values: [String] { lock.lock(); defer { lock.unlock() }; return storage }
    func add(_ value: String) { lock.lock(); storage.append(value); lock.unlock() }
}
