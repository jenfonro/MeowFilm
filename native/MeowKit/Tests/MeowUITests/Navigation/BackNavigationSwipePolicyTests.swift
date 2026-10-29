import XCTest
@testable import MeowUI

final class BackNavigationSwipePolicyTests: XCTestCase {
    func testHalfWidthCommitAndShortDragCancellation() {
        XCTAssertTrue(BackNavigationSwipePolicy.commits(distance: 196, velocity: 0, width: 390))
        XCTAssertFalse(BackNavigationSwipePolicy.commits(distance: 195, velocity: 0, width: 390))
        XCTAssertFalse(BackNavigationSwipePolicy.commits(distance: 18, velocity: 900, width: 390))
        XCTAssertFalse(BackNavigationSwipePolicy.commits(distance: -196, velocity: -900, width: 390))
    }
    func testForwardFlingHasMinimumDistanceAndVelocity() {
        XCTAssertTrue(BackNavigationSwipePolicy.commits(distance: 24, velocity: 301, width: 390))
        XCTAssertFalse(BackNavigationSwipePolicy.commits(distance: 23, velocity: 301, width: 390))
        XCTAssertFalse(BackNavigationSwipePolicy.commits(distance: 24, velocity: 300, width: 390))
        XCTAssertFalse(BackNavigationSwipePolicy.commits(distance: 24, velocity: -301, width: 390))
    }
    func testInvalidMeasurementsNeverCommit() {
        for value in [Double.nan, .infinity, -.infinity] {
            XCTAssertFalse(BackNavigationSwipePolicy.commits(distance: value, velocity: 400, width: 390))
            XCTAssertFalse(BackNavigationSwipePolicy.commits(distance: 200, velocity: value, width: 390))
            XCTAssertFalse(BackNavigationSwipePolicy.commits(distance: 200, velocity: 400, width: value))
        }
        XCTAssertFalse(BackNavigationSwipePolicy.commits(distance: 200, velocity: 400, width: 0))
        XCTAssertFalse(BackNavigationSwipePolicy.commits(distance: 200, velocity: 400, width: -390))
    }
}
