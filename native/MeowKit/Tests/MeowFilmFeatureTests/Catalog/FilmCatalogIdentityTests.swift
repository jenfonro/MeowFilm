import Foundation
import XCTest
@testable import MeowFilmFeature

final class FilmCatalogIdentityTests: XCTestCase {
    func testTMDBKindIsPartOfIdentityWithoutChangingDoubanOrSourceNamespaces() {
        let movie = FilmItemIdentity(sourceID: "tmdb", itemID: "19", kind: .movie)
        let tv = FilmItemIdentity(sourceID: "tmdb", itemID: "19", kind: .tv)
        let legacy = FilmItemIdentity(sourceID: "tmdb", itemID: "19")
        XCTAssertNotEqual(movie, tv)
        XCTAssertNotEqual(legacy, movie)
        XCTAssertNotEqual(legacy, tv)
        XCTAssertEqual(Set([movie, tv, legacy]).count, 3)
        XCTAssertEqual(FilmItemIdentity(sourceID: "douban", itemID: "19", kind: .tv),
                       FilmItemIdentity(sourceID: "douban", itemID: "19"))
        XCTAssertEqual(FilmItemIdentity(sourceID: "source-key", itemID: "19", kind: .movie),
                       FilmItemIdentity(sourceID: "source-key", itemID: "19"))
        XCTAssertNotEqual(movie, FilmItemIdentity(sourceID: "douban", itemID: "19"))
    }

    func testTypedItemAndIdentityPreserveExactKindThroughCodable() throws {
        for kind in [FilmItemKind.movie, .tv] {
            let item = FilmItem(sourceID: "tmdb", id: "19", title: "影片", kind: kind,
                posterURL: URL(string: "https://artwork.example.invalid/poster.jpg"),
                largePosterURL: URL(string: "https://artwork.example.invalid/large.jpg"),
                score: "9.0", subtitle: "2026")
            let data = try JSONEncoder().encode(item)
            let restored = try JSONDecoder().decode(FilmItem.self, from: data)
            XCTAssertEqual(restored, item)
            XCTAssertEqual(restored.kind, kind)
            XCTAssertEqual(restored.identity.kind, kind)
            let identity = try JSONDecoder().decode(FilmItemIdentity.self, from: JSONEncoder().encode(item.identity))
            XCTAssertEqual(identity, item.identity)
        }
    }

    func testLegacyJSONRetainsUnknownTMDBKindWithoutUsingTitleOrSubtitleAsEvidence() throws {
        let data = Data(#"{"sourceID":"tmdb","id":"19","title":"电影 第1季","score":"9.0","subtitle":"剧集"}"#.utf8)
        let item = try JSONDecoder().decode(FilmItem.self, from: data)
        XCTAssertNil(item.kind)
        XCTAssertNil(item.identity.kind)
        let restored = try JSONDecoder().decode(FilmItem.self, from: JSONEncoder().encode(item))
        XCTAssertEqual(restored, item)
        let legacy = try JSONDecoder().decode(FilmItemIdentity.self,
            from: Data(#"{"sourceID":"tmdb","itemID":"19"}"#.utf8))
        XCTAssertEqual(legacy, item.identity)
        XCTAssertNotEqual(legacy, FilmItemIdentity(sourceID: "tmdb", itemID: "19", kind: .movie))
        XCTAssertNotEqual(legacy, FilmItemIdentity(sourceID: "tmdb", itemID: "19", kind: .tv))
    }

    func testDecodedDoubanIdentityRemainsCompatibleWithItsLegacyGlobalID() throws {
        let legacy = try JSONDecoder().decode(FilmItemIdentity.self,
            from: Data(#"{"sourceID":"douban","itemID":"19"}"#.utf8))
        for kind in [FilmItemKind.movie, .tv] {
            let item = FilmItem(sourceID: "douban", id: "19", title: "豆瓣条目", kind: kind)
            XCTAssertEqual(item.identity, legacy)
            XCTAssertEqual(item.kind, kind)
            let data = Data(#"{"sourceID":"douban","itemID":"19","kind":"\#(kind.rawValue)"}"#.utf8)
            XCTAssertEqual(try JSONDecoder().decode(FilmItemIdentity.self, from: data), legacy)
        }
    }
}
