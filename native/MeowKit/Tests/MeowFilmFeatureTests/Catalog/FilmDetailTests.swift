import Foundation
import XCTest
@testable import MeowFilmFeature

final class FilmDetailTests: XCTestCase {
    func testDetailWithoutUpstreamDataOnlyContainsTheSelectedFilm() {
        let item = FilmItem(sourceID: "douban", id: "100", title: "选中的影片",
                            posterURL: URL(string: "https://artwork.invalid/poster.jpg"), score: "8.6")
        let detail = FilmDetail(item: item)
        XCTAssertEqual(detail.item, item)
        XCTAssertEqual(detail.title, item.title)
        XCTAssertNil(detail.backdropURL)
        XCTAssertNil(detail.logoURL)
        XCTAssertNil(detail.releaseDate)
        XCTAssertNil(detail.seasonCount)
        XCTAssertNil(detail.episodeCount)
        XCTAssertNil(detail.playbackURL)
        XCTAssertTrue(detail.summary.isEmpty)
        XCTAssertTrue(detail.seasons.isEmpty)
        XCTAssertTrue(detail.cast.isEmpty)
        XCTAssertTrue(detail.similarItems.isEmpty)
    }
}
