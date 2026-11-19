import Foundation
import XCTest
import MeowRuntime
import MeowStorage
import MeowStorageTestSupport
@testable import MeowFilmFeature

@MainActor
final class FilmLibraryTests: XCTestCase {
    private func library(_ database: SQLiteDatabase) async throws -> FilmLibrary {
        let library = FilmLibrary()
        try await library.bind(database: database)
        return library
    }
    private func rows(_ database: SQLiteDatabase) async throws -> [SQLiteRow] {
        try await database.snapshot { try $0.query("SELECT * FROM ios_film_favorites ORDER BY businessID") }
    }
    private func item(sourceID: String, id: String, title: String, kind: FilmItemKind? = nil) -> FilmItem {
        let businessID = sourceID + ":" + (kind?.rawValue ?? "") + ":" + id
        let target = FilmPlaybackTarget(id: businessID, kind: kind == .tv ? .tv : .movie, title: title)
        return FilmItem(sourceID: sourceID, id: id, title: title, kind: kind, businessID: businessID, playbackTarget: target)
    }

    func testFavoritesSaveAndRestoreTheSuppliedTarget() async throws {
        let database = try storageTestDatabase(self)
        let target = FilmPlaybackTarget(id: "node-content", kind: .tv, title: "影片", metadataProvider: .tmdb, metadataID: "19")
        let item = FilmItem(sourceID: "tmdb", id: "19", title: "影片", kind: .tv,
                            businessID: "node-content", playbackTarget: target)
        let owner = try await library(database)
        XCTAssertFalse(owner.contains(item))
        storageTestTrue(try await owner.toggle(item))
        let restored = try await library(database)
        let record = try XCTUnwrap(restored.favorites.first)
        XCTAssertEqual(record.businessID, item.businessID)
        XCTAssertEqual(record.playbackTarget, target)
    }

    func testFavoritesPersistWithSourceAndItemAsSeparateIdentityFields() async throws {
        let database = try storageTestDatabase(self)
        _ = try await database.transaction { try $0.execute("INSERT INTO ios_search_history(text,used_at) VALUES('preserved',1)") }
        let first = item(sourceID: "first", id: "100", title: "第一来源")
        let second = item(sourceID: "second", id: "100", title: "第二来源")
        let owner = try await library(database)
        XCTAssertFalse(owner.contains(first))
        storageTestTrue(try await owner.toggle(first))
        XCTAssertTrue(owner.contains(first))
        XCTAssertFalse(owner.contains(second))
        let restored = try await library(database)
        XCTAssertTrue(restored.contains(first))
        XCTAssertFalse(restored.contains(second))
        storageTestTrue(try await restored.toggle(second))
        storageTestFalse(try await restored.toggle(first))
        let rebuilt = try await library(database)
        XCTAssertFalse(rebuilt.contains(first))
        XCTAssertTrue(rebuilt.contains(second))
        let history = try await database.snapshot { try $0.query("SELECT text FROM ios_search_history").map { try $0.text("text") } }
        XCTAssertEqual(history, ["preserved"])
        let delimitedSource = item(sourceID: "a:b", id: "c", title: "不同身份一")
        let delimitedItem = item(sourceID: "a", id: "b:c", title: "不同身份二")
        storageTestTrue(try await rebuilt.toggle(delimitedSource))
        XCTAssertFalse(rebuilt.contains(delimitedItem))
        storageTestTrue(try await rebuilt.toggle(delimitedItem))
        storageTestEqual(try await library(database).favorites.count, 3)
    }

    func testUnreadableFavoriteDataDoesNotCrashOrEraseOtherSettings() async throws {
        let database = try storageTestDatabase(self)
        let saved = item(sourceID: "first", id: "100", title: "影片")
        storageTestTrue(try await library(database).toggle(saved))
        _ = try await database.transaction { db in
            try db.execute("UPDATE ios_film_favorites SET kind='invalid'")
            try db.execute("INSERT INTO ios_search_history(text,used_at) VALUES('preserved',1)")
        }
        let before = try await rows(database)
        let owner = FilmLibrary()
        await storageTestThrows { try await owner.bind(database: database) }
        XCTAssertTrue(owner.favorites.isEmpty)
        XCTAssertEqual(owner.loadError, .corruptedData)
        XCTAssertEqual(owner.loadError?.localizedDescription, "收藏数据已损坏，原数据已保留，无法更改收藏。")
        for _ in 0..<2 {
            await storageTestThrows({ try await owner.toggle(saved) }) {
                XCTAssertEqual($0 as? FilmLibraryError, .corruptedData)
            }
            XCTAssertFalse(owner.contains(saved))
            storageTestEqual(try await rows(database), before)
        }
        let restored = FilmLibrary()
        await storageTestThrows { try await restored.bind(database: database) }
        XCTAssertEqual(restored.loadError, .corruptedData)
        storageTestEqual(try await database.snapshot { try $0.query("SELECT text FROM ios_search_history").first?.text("text") }, "preserved")
    }

    func testMissingFavoriteDataIsAnEmptyHealthyLibrary() async throws {
        let database = try storageTestDatabase(self)
        let owner = try await library(database)
        XCTAssertTrue(owner.favorites.isEmpty)
        XCTAssertNil(owner.loadError)
        storageTestEqual(try await rows(database), [])
        let item = item(sourceID: "douban", id: "19", title: "影片", kind: .movie)
        storageTestTrue(try await owner.toggle(item))
        let restored = try await library(database)
        XCTAssertNil(restored.loadError)
        XCTAssertTrue(restored.contains(item))
        storageTestFalse(try await restored.toggle(item))
        let empty = try await library(database)
        XCTAssertTrue(empty.favorites.isEmpty)
        XCTAssertNil(empty.loadError)
    }

    func testCurrentTargetConstraintsRejectInvalidRowsAndRollBack() async throws {
        let database = try storageTestDatabase(self)
        storageTestTrue(try await library(database).toggle(item(sourceID: "a", id: "1", title: "A")))
        let before = try await rows(database)
        for sql in ["UPDATE ios_film_favorites SET target_kind='invalid'",
                    "UPDATE ios_film_favorites SET row_revision=-1",
                    "UPDATE ios_film_favorites SET target_title=NULL"] {
            await storageTestThrows { try await database.transaction { try $0.execute(sql) } }
            storageTestEqual(try await rows(database), before)
        }
        storageTestEqual(try await library(database).favorites.count, 1)
    }

    func testTMDBMovieAndTVWithSameIDAreIndependentAcrossRestorationAndRemoval() async throws {
        let database = try storageTestDatabase(self)
        let movie = item(sourceID: "tmdb", id: "19", title: "电影", kind: .movie)
        let tv = item(sourceID: "tmdb", id: "19", title: "剧集", kind: .tv)
        let owner = try await library(database)
        storageTestTrue(try await owner.toggle(movie))
        XCTAssertTrue(owner.contains(movie)); XCTAssertFalse(owner.contains(tv))
        storageTestTrue(try await owner.toggle(tv))
        XCTAssertEqual(owner.favorites.count, 2)
        let restored = try await library(database)
        XCTAssertNil(restored.loadError)
        XCTAssertTrue(restored.contains(movie)); XCTAssertTrue(restored.contains(tv))
        storageTestFalse(try await restored.toggle(movie))
        let rebuilt = try await library(database)
        XCTAssertFalse(rebuilt.contains(movie)); XCTAssertTrue(rebuilt.contains(tv))
        storageTestFalse(try await rebuilt.toggle(tv))
        storageTestTrue(try await library(database).favorites.isEmpty)
    }

    func testNewUntypedTMDBFavoriteFailsWithoutWritingOrClaimingSuccess() async throws {
        let database = try storageTestDatabase(self)
        let owner = try await library(database)
        await storageTestThrows({ try await owner.toggle(FilmItem(sourceID: "tmdb", id: "19", title: "电影或剧集")) }) {
            XCTAssertEqual($0 as? FilmLibraryError, .missingKind)
        }
        XCTAssertNil(owner.loadError); XCTAssertTrue(owner.favorites.isEmpty)
        storageTestEqual(try await rows(database), [])
    }

    func testFavoriteActionRequiresTheSuppliedTargetToMatchItsBusinessIdentity() async throws {
        let database = try storageTestDatabase(self)
        let owner = try await library(database)
        for target: FilmPlaybackTarget? in [nil, .init(id: "other", kind: .movie, title: "影片")] {
            let item = FilmItem(sourceID: "douban", id: "19", title: "影片", businessID: "selected", playbackTarget: target)
            await storageTestThrows({ try await owner.toggle(item) }) { XCTAssertEqual($0 as? FilmLibraryError, .missingTarget) }
        }
        storageTestEqual(try await rows(database), [])
        XCTAssertTrue(owner.favorites.isEmpty)
    }

    func testFavoriteToggleChangesOnlyItsOwnRelationshipRows() async throws {
        let database = try storageTestDatabase(self)
        let a = item(sourceID: "a", id: "1", title: "A"), b = item(sourceID: "b", id: "2", title: "B")
        let owner = try await library(database)
        storageTestTrue(try await owner.toggle(a))
        let saved = try await rows(database)
        let first = try XCTUnwrap(saved.first)
        storageTestTrue(try await owner.toggle(b))
        storageTestEqual(try await rows(database).first { $0["businessID"] == first["businessID"] }, first)
        storageTestFalse(try await owner.toggle(b))
        storageTestEqual(try await rows(database), [first])
    }
}
