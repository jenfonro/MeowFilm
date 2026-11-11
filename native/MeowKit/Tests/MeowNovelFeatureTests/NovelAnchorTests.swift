import Foundation
import XCTest
@testable import MeowNovelFeature

final class NovelAnchorTests: XCTestCase {
    func testChangedParagraphsFallBackToRatioAndUnchangedImagesKeepTheirRelativePosition() throws {
        let before = NovelContent(title: "章", blocks: [.init(text: "0123456789"), .init(src: "image"), .init(text: "尾")])
        let image = NovelAnchor(chapterUrl: "chapter", chapterTitle: "章", chapterIndex: 0, blockIndex: 1, ratio: 10.6 / 12)
        let after = NovelContent(title: "章", blocks: [.init(text: "前文"), before.blocks[0], before.blocks[1], before.blocks[2]])
        let moved = try XCTUnwrap(NovelAnchorResolver.resolve(image, previous: before, next: after))
        XCTAssertEqual(moved.blockIndex, 2); XCTAssertEqual(moved.ratio, 12.6 / 14, accuracy: 0.000001)
        let text = NovelAnchor(chapterUrl: "chapter", chapterTitle: "章", chapterIndex: 0, blockIndex: 0, utf16Offset: 5, ratio: 0.5)
        let changed = NovelContent(title: "章", blocks: [.init(text: "一二"), .init(text: "三四五六")])
        let fallback = try XCTUnwrap(NovelAnchorResolver.resolve(text, previous: before, next: changed))
        XCTAssertEqual(fallback.blockIndex, 1); XCTAssertEqual(fallback.utf16Offset, 1); XCTAssertEqual(fallback.ratio, 0.5)
    }
    func testRestoredUTF16OffsetsStayOnComposedCharactersAndEmptyContentKeepsAValidAnchor() throws {
        let body = NovelContent(title: "章", blocks: [.init(text: "a👨‍👩‍👧‍👦z")])
        let anchor = NovelAnchor(chapterUrl: "chapter", chapterTitle: "章", chapterIndex: 0, utf16Offset: 4, ratio: 0.2)
        let restored = try XCTUnwrap(NovelAnchorResolver.resolve(anchor, previous: body, next: body))
        XCTAssertEqual(restored.utf16Offset, 1)
        let empty = try XCTUnwrap(NovelAnchorResolver.resolve(anchor, previous: body, next: .init(title: "章", blocks: [])))
        XCTAssertEqual(empty.blockIndex, 0); XCTAssertEqual(empty.utf16Offset, 0); XCTAssertEqual(empty.chapterUrl, "chapter")
    }
}
