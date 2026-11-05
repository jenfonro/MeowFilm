import Foundation
import XCTest
@testable import MeowFilmFeature

@MainActor
final class FilmLibraryTests: XCTestCase {
    func testFavoritesPersistWithSourceAndItemAsSeparateIdentityFields() throws {
        let suite = "MeowFilmFeatureTests." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        defaults.set("preserved", forKey: "unrelated-setting")
        let first = FilmItem(sourceID: "first", id: "100", title: "第一来源")
        let second = FilmItem(sourceID: "second", id: "100", title: "第二来源")
        let library = FilmLibrary(defaults: defaults)
        XCTAssertFalse(library.contains(first))
        XCTAssertTrue(library.toggle(first))
        XCTAssertTrue(library.contains(first))
        XCTAssertFalse(library.contains(second))

        let restored = FilmLibrary(defaults: defaults)
        XCTAssertTrue(restored.contains(first))
        XCTAssertFalse(restored.contains(second))
        XCTAssertTrue(restored.toggle(second))
        XCTAssertFalse(restored.toggle(first))

        let rebuilt = FilmLibrary(defaults: defaults)
        XCTAssertFalse(rebuilt.contains(first))
        XCTAssertTrue(rebuilt.contains(second))
        XCTAssertEqual(defaults.string(forKey: "unrelated-setting"), "preserved")

        let delimitedSource = FilmItem(sourceID: "a:b", id: "c", title: "不同身份一")
        let delimitedItem = FilmItem(sourceID: "a", id: "b:c", title: "不同身份二")
        XCTAssertTrue(rebuilt.toggle(delimitedSource))
        XCTAssertFalse(rebuilt.contains(delimitedItem))
        XCTAssertTrue(rebuilt.toggle(delimitedItem))
        XCTAssertEqual(FilmLibrary(defaults: defaults).favorites.count, 3)
    }

    func testUnreadableFavoriteDataDoesNotCrashOrEraseOtherSettings() throws {
        let suite = "MeowFilmFeatureTests." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        defaults.set(Data("invalid saved data".utf8), forKey: FilmLibrary.storageKey)
        let library = FilmLibrary(defaults: defaults)
        XCTAssertTrue(library.favorites.isEmpty)
        let item = FilmItem(sourceID: "first", id: "100", title: "影片")
        XCTAssertTrue(library.toggle(item))
        XCTAssertTrue(FilmLibrary(defaults: defaults).contains(item))
    }

}
