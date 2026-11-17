#if os(iOS) || os(macOS)
import XCTest
import MeowRuntime
@testable import MeowFilmFeature

@MainActor
final class FilmSearchStableIdentityTests: XCTestCase {
    private typealias F = FilmSearchWireFixture
    func testOpaqueNodeIDsPreserveZeroStringZeroAndCanonicalUTF8IdentityInFavorites() async throws {
        let rows = [F.row(.number(0), key: "site:number"), F.row(.string("0"), key: "site:string"),
            F.row(.string("é"), key: "site:é"), F.row(.string("e\u{301}"), key: "site:e\u{301}")]
        let rpc = FilmSearchRPCFixture(F.snapshot(cards: [F.group(rows)], raw: rows))
        let service = filmSearchService(rpc), session = try service.session(for: "原查询")
        await session.drain()
        XCTAssertEqual(session.snapshot.rawCards.map(\.detailID), [.number(0), .string("0"), .string("é"), .string("e\u{301}")])
        let keys = session.snapshot.rawCards.map { Data($0.displayKey.utf8) }
        XCTAssertEqual(Set(keys).count, 4)
        let name = "film-search-favorites-" + UUID().uuidString, defaults: UserDefaults
        defaults = try XCTUnwrap(UserDefaults(suiteName: name))
        defer { defaults.removePersistentDomain(forName: name) }
        let library = FilmLibrary(defaults: defaults)
        let details = session.snapshot.rawCards.map { session.context(for: $0).detail }
        for detail in details { try library.toggle(detail.item) }
        XCTAssertEqual(library.favorites.count, 4)
        let reopened = FilmLibrary(defaults: defaults)
        XCTAssertTrue(details.allSatisfy { reopened.contains($0.item) })
        XCTAssertEqual(session.snapshot.cards.first?.members, rows)
        await service.close()
    }

    func testDetailUsesAvailableOriginalPosterWithoutChangingCardOrTarget() async throws {
        let card: RuntimeValue = .object(["businessId": .string("tmdb:movie:7"),
            "playbackTarget": .object(["id": .string("tmdb:movie:7"), "kind": .string("movie"),
                "title": .string("影片"), "metadataProvider": .string("tmdb"), "metadataId": .string("7")]),
            "displayKey": .string("tmdb:movie:7"), "id": .string("7"), "sourceKind": .string("tmdb"),
            "tmdbId": .number(7), "tmdbType": .string("movie"), "title": .string("影片"),
            "poster": .string("/v1/tmdb/images/w500/poster.jpg")])
        var fields = try F.snapshot(metadata: "completed", cards: [card]).filmRequiredObject()
        fields["metadataDetails"] = .object(["movie:7": .object([
            "poster_path": .string("/v1/tmdb/images/original/poster.jpg"),
            "backdrop_path": .string("/v1/tmdb/images/original/backdrop.jpg")])])
        let rpc = FilmSearchRPCFixture(.object(fields)), service = filmSearchService(rpc)
        let session = try service.session(for: "原查询")
        await session.drain()
        let selected = try XCTUnwrap(session.snapshot.cards.first)
        let detail = session.context(for: selected).detail
        XCTAssertEqual(detail.item.posterURL?.absoluteString, "/v1/tmdb/images/original/poster.jpg")
        XCTAssertEqual(detail.backdropURL?.absoluteString, "/v1/tmdb/images/original/backdrop.jpg")
        XCTAssertEqual(detail.item.playbackTarget, selected.playbackTarget)
        XCTAssertEqual(detail.item.businessID, selected.businessID)
        XCTAssertEqual(selected.filmItem(imageURL: session.imageURL).posterURL?.absoluteString, "/v1/tmdb/images/w500/poster.jpg")
        await service.close()
    }

    func testDisplayCodeUnitArraysNeverReplaceTheOriginalCardValue() throws {
        let units: RuntimeValue = .array([.number(0xD800), .number(120), .number(0xDFFF)])
        let card = FilmSearchCard(value: .object(["displayKey": .string("opaque"), "title": units, "raw": .object(["kept": units])]))
        XCTAssertEqual(card.title, "\u{FFFD}x\u{FFFD}")
        XCTAssertEqual(card.value.catField("title"), units)
        XCTAssertEqual(card.value.catField("raw")?.catField("kept"), units)
    }
}
#endif
