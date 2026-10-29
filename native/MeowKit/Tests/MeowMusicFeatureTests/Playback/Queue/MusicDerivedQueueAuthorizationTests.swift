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
}
