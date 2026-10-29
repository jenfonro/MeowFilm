import Foundation
import XCTest
@testable import MeowMusicFeature

final class MusicChannelTagLayoutTests: XCTestCase {
    func testFittingChipsRemainInOneRowIncludingExactBoundary() {
        let rows = MusicChannelTagRows(width: 208, items: [60, 80, 52].map { CGSize(width: $0, height: 42) })
        XCTAssertEqual(rows.frames.map(\.minX), [0, 68, 156])
        XCTAssertEqual(rows.frames.map(\.minY), [0, 0, 0])
        XCTAssertEqual(rows.size, CGSize(width: 208, height: 42))
    }
    func testExpandedTagsWrapByWidthNotOneTagPerRow() {
        let rows = MusicChannelTagRows(width: 207, items: [60, 80, 52, 90, 65].map { CGSize(width: $0, height: 42) })
        XCTAssertEqual(rows.frames.map(\.minX), [0, 68, 0, 60, 0])
        XCTAssertEqual(rows.frames.map(\.minY), [0, 0, 50, 50, 100])
        XCTAssertEqual(rows.size.height, 142)
    }
    func testUnconstrainedMeasurementUsesFullIntrinsicRow() {
        let rows = MusicChannelTagRows(width: nil, items: [60, 80, 52].map { CGSize(width: $0, height: 42) })
        XCTAssertEqual(rows.size, CGSize(width: 208, height: 42))
    }
    func testEmptyAndNarrowLayoutsStayWithinOfferedWidth() {
        XCTAssertEqual(MusicChannelTagRows(width: 200, items: []).size.height, 0)
        let rows = MusicChannelTagRows(width: 40, items: [60, 80].map { CGSize(width: $0, height: 42) })
        XCTAssertEqual(rows.frames.map(\.width), [40, 40])
        XCTAssertEqual(rows.frames.map(\.minY), [0, 50])
    }
    func testRowsRespectTallestChipAndResizeWithoutStalePositions() {
        let items = [CGSize(width: 60, height: 42), CGSize(width: 80, height: 60), CGSize(width: 52, height: 42)]
        XCTAssertEqual(MusicChannelTagRows(width: 200, items: items).frames.last?.minY, 68)
        let wider = MusicChannelTagRows(width: 300, items: items)
        XCTAssertEqual(wider.frames.map(\.minY), [0, 0, 0])
        XCTAssertEqual(wider.size.height, 60)
    }
}
