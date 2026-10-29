import XCTest
@testable import MeowUI

final class InteractionPolicyTests: XCTestCase {
    func testPagerKeepsUniqueIdentityAndNeverInventsNeighborsForEmptyOrSinglePage() {
        XCTAssertEqual(HorizontalPagerTopology.unique(["a", "b", "a", "c", "b"]), ["a", "b", "c"])
        XCTAssertNil(HorizontalPagerTopology.neighbor("a", step: 1, ids: [], wraps: true))
        XCTAssertNil(HorizontalPagerTopology.neighbor("a", step: 1, ids: ["a"], wraps: true))
        XCTAssertNil(HorizontalPagerTopology.neighbor("missing", step: 1, ids: ["a", "b"], wraps: true))
        for step in [0, 2, -2, Int.min, Int.max] {
            XCTAssertNil(HorizontalPagerTopology.neighbor("a", step: step, ids: ["a", "b"], wraps: true))
        }
    }
    func testThreeCategoryNeighborsWrapWithoutAnyHistoryClones() {
        let ids = ["0", "1", "2"]
        XCTAssertEqual(HorizontalPagerTopology.neighbor("0", step: -1, ids: ids, wraps: true), "2")
        XCTAssertEqual(HorizontalPagerTopology.neighbor("2", step: 1, ids: ids, wraps: true), "0")
        XCTAssertNil(HorizontalPagerTopology.neighbor("0", step: -1, ids: ids, wraps: false))
        XCTAssertNil(HorizontalPagerTopology.neighbor("2", step: 1, ids: ids, wraps: false))
        for from in ids {
            for to in ids where from != to {
                let next = HorizontalPagerTopology.neighbor(from, step: 1, ids: ids, wraps: true)
                XCTAssertEqual(HorizontalPagerTopology.forward(from: from, to: to, ids: ids, wraps: true), to == next)
            }
        }
    }
    func testChannelClicksUseCatalogDirectionWithoutWrapping() {
        let ids = ["latest", "rank", "playlist", "artist"]
        XCTAssertTrue(HorizontalPagerTopology.forward(from: "latest", to: "artist", ids: ids, wraps: false))
        XCTAssertFalse(HorizontalPagerTopology.forward(from: "artist", to: "latest", ids: ids, wraps: false))
        XCTAssertTrue(HorizontalPagerTopology.forward(from: nil, to: "latest", ids: ids, wraps: false))
    }
    func testMarqueeStartsOnlyForOverflowAndItsPeriodIncludesOneGap() {
        XCTAssertNil(LoopingTextGeometry.period(textWidth: 0, viewport: 120))
        XCTAssertNil(LoopingTextGeometry.period(textWidth: 120, viewport: 120))
        XCTAssertNil(LoopingTextGeometry.period(textWidth: 119.9, viewport: 120))
        XCTAssertEqual(LoopingTextGeometry.period(textWidth: 200, viewport: 120), 228)
        XCTAssertEqual(LoopingTextGeometry.period(textWidth: 200, viewport: 120, gap: 40), 240)
        for width in [Double.nan, .infinity] {
            XCTAssertNil(LoopingTextGeometry.period(textWidth: width, viewport: 120))
            XCTAssertNil(LoopingTextGeometry.period(textWidth: 200, viewport: width))
        }
        XCTAssertNil(LoopingTextGeometry.period(textWidth: 200, viewport: 120, gap: -1))
    }
}
