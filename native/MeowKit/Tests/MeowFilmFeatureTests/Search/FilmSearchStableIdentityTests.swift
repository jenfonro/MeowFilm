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

    func testDisplayCodeUnitArraysNeverReplaceTheOriginalCardValue() throws {
        let units: RuntimeValue = .array([.number(0xD800), .number(120), .number(0xDFFF)])
        let card = FilmSearchCard(value: .object(["displayKey": .string("opaque"), "title": units, "raw": .object(["kept": units])]))
        XCTAssertEqual(card.title, "\u{FFFD}x\u{FFFD}")
        XCTAssertEqual(card.value.catField("title"), units)
        XCTAssertEqual(card.value.catField("raw")?.catField("kept"), units)
    }
}
#endif
