#if os(iOS)
import XCTest
import UIKit
import Combine
import MeowUI
import MeowRuntime
@testable import MeowFilmFeature

@MainActor
final class FilmSearchUIKitTests: XCTestCase {
    func testExistingPosterAddsSearchBadgeAndSourceLabelAtNarrowAndWideWidths() throws {
        let images = ArtworkImages(maximumBytes: 4 * 1_024 * 1_024) { _ in throw FilmSearchError.unavailable }
        let item = FilmItem(sourceID: "source", id: "id", title: String(repeating: "长标题", count: 10), score: "8.9")
        let variants = [("来源名称", "更新至12集"), ("聚合", "更新至24集"), ("电影", "2026"), ("剧集", "更新至第十二季十二集")]
        let canvas = UIView(frame: CGRect(x: 0, y: 0, width: 412, height: 220))
        canvas.backgroundColor = UIColor(PageTheme().top)
        for width in [CGFloat(90), 110, 160] {
            let cell = FilmPosterCell(frame: CGRect(x: 0, y: 0, width: width, height: width * 1.5 + 59))
            for (index, variant) in variants.enumerated() {
                let (label, textBadge) = variant
                cell.configure(item: item, images: images, theme: PageTheme(), textBadge: textBadge, label: label)
                cell.layoutIfNeeded()
                let badge = try XCTUnwrap(find("film.poster.badge", in: cell) as? UILabel)
                let source = try XCTUnwrap(find("film.poster.label", in: cell) as? UILabel)
                let title = try XCTUnwrap(cell.contentView.subviews.compactMap { $0 as? UILabel }.first { $0.text == item.title })
                XCTAssertEqual(title.numberOfLines, 2)
                XCTAssertTrue(cell.contentView.bounds.contains(title.frame))
                XCTAssertGreaterThan(source.frame.minY, title.frame.maxY)
                XCTAssertEqual(badge.text, textBadge)
                XCTAssertEqual(source.text, label)
                XCTAssertTrue(cell.contentView.bounds.contains(source.frame))
                XCTAssertLessThanOrEqual(badge.frame.maxX, width)
                XCTAssertGreaterThanOrEqual(badge.frame.minX, 0)
                XCTAssertLessThanOrEqual(badge.frame.maxY, width * 1.5)
                XCTAssertFalse(badge.isHidden)
                XCTAssertFalse(cell.accessibilityLabel?.contains("8.9") == true, "Relevance and film scores are not search badges")
                if width == 90 {
                    let rendered = FilmPosterCell(frame: CGRect(x: 12 + CGFloat(index) * 100, y: 12, width: width, height: width * 1.5 + 59))
                    rendered.configure(item: item, images: images, theme: PageTheme(), textBadge: textBadge, label: label)
                    canvas.addSubview(rendered)
                    rendered.layoutIfNeeded()
                }
            }
            cell.configure(item: item, images: images, theme: PageTheme())
            cell.layoutIfNeeded()
            XCTAssertTrue(try XCTUnwrap(find("film.poster.badge", in: cell)).isHidden)
            XCTAssertTrue(try XCTUnwrap(find("film.poster.label", in: cell)).isHidden)
            XCTAssertEqual(cell.accessibilityLabel, item.title, "Existing home presentation is unchanged")
        }
        let format = UIGraphicsImageRendererFormat(); format.scale = 2
        let image = UIGraphicsImageRenderer(bounds: canvas.bounds, format: format).image { context in
            canvas.layer.render(in: context.cgContext)
        }
        let attachment = XCTAttachment(image: image)
        attachment.name = "film-search-existing-posters-narrow-three-kinds"
        attachment.lifetime = .keepAlways
        add(attachment)
    }

    func testSearchContentUsesExistingPostersKeepsBothModesAndDeliversLiveContext() async throws {
        let reads = FilmSearchUIKitReads()
        let service = FilmSearchService(processor: try FilmSearchProcessor(), data: .init(read: { _, _, page in
            .init(value: try await reads.read(page: page))
        }), sites: {
            [FilmSearchSite(cat: .init(installationId: "cat", name: "猫源", sourceType: "local", generation: 4),
                source: .init(key: .init(installationId: "cat", siteKey: "site"), name: "来源", api: "fixture", actions: ["search"], methods: [:]))]
        }, rules: { ["高清"] })
        let content = FilmSearchResultsContent(service: service, images: ArtworkImages(maximumBytes: 4 * 1_024 * 1_024) { _ in throw FilmSearchError.unavailable }, theme: PageTheme())
        content.view.frame = CGRect(x: 0, y: 0, width: 375, height: 720)
        content.loadViewIfNeeded()
        content.submit("原查询")
        let session = try XCTUnwrap(content.session)
        await session.drain()
        content.view.layoutIfNeeded()
        let grid = try XCTUnwrap(find("film.search.grid", in: content.view) as? UICollectionView)
        let more = try XCTUnwrap(find("film.search.more", in: content.view) as? UIButton)
        await waitFor { more.isEnabled && grid.backgroundView?.isHidden == true }
        XCTAssertTrue(session.snapshot.failed)
        XCTAssertTrue(grid.backgroundView?.isHidden == true, "All failed sources must not render a successful no-results label")
        more.sendActions(for: .touchUpInside)
        await session.drain()
        XCTAssertEqual(session.snapshot.siteState, .completed)
        await waitFor { content.view.layoutIfNeeded(); grid.layoutIfNeeded(); return grid.cellForItem(at: IndexPath(item: 0, section: 0)) != nil }
        XCTAssertTrue(grid.cellForItem(at: IndexPath(item: 0, section: 0)) is FilmPosterCell)
        var context: FilmSearchContext?
        content.onSelect = { context = $0 }
        content.collectionView(grid, didSelectItemAt: IndexPath(item: 0, section: 0))
        let selected = try XCTUnwrap(context)
        XCTAssertEqual(selected.sources.count, 1)
        more.sendActions(for: .touchUpInside)
        await session.drain()
        await waitFor { selected.sources.count == 2 && more.isHidden }
        XCTAssertEqual(selected.sources.count, 2, "An in-flight detail consumer keeps the same session after more")
        let modes = try XCTUnwrap(find("film.search.mode", in: content.view) as? UISegmentedControl)
        modes.selectedSegmentIndex = 1; modes.sendActions(for: .valueChanged)
        await waitFor { grid.numberOfItems(inSection: 0) == 2 }
        XCTAssertEqual(grid.numberOfItems(inSection: 0), 2)
        content.collectionView(grid, didSelectItemAt: IndexPath(item: 1, section: 0))
        XCTAssertEqual(context?.sources.first?.catField("siteDetail"), .string("e\u{301}"),
                       "Raw selection preserves exact protocol identity, including canonically equivalent IDs")
        modes.selectedSegmentIndex = 0; modes.sendActions(for: .valueChanged)
        await waitFor { grid.numberOfItems(inSection: 0) == 1 }
        XCTAssertEqual(grid.numberOfItems(inSection: 0), 1)
        content.submit("原查询")
        XCTAssertTrue(content.session === session)
        let calls = await reads.pages
        XCTAssertEqual(calls, [1, 1, 2], "Only the explicit retry repeats the failed first page")
        content.close()
        await service.close()
    }

    func testRawTypedZeroCardsKeepSeparateNavigationAndRealFavoritesAcrossReload() async throws {
        let name = "film-search-favorites-" + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
        defer { defaults.removePersistentDomain(forName: name) }
        let site = FilmSearchSite(cat: .init(installationId: "fixture", name: "fixture", sourceType: "local", generation: 4),
            source: .init(key: .init(installationId: "fixture", siteKey: "site"), name: "同一来源",
                          api: "fixture", actions: ["search"], methods: [:]))
        let service = FilmSearchService(processor: try FilmSearchProcessor(), data: .init(read: { _, _, _ in
            .init(value: .object(["list": .array([.number(0), .string("0")].map { id in
                .object(["vod_id": id, "vod_name": .string("同一标题"), "vod_remarks": .string("原备注")])
            }), "pagecount": .number(1)]))
        }), sites: { [site] }, rules: { [] })
        addTeardownBlock { await service.close() }
        let images = ArtworkImages(maximumBytes: 4 * 1_024 * 1_024) { _ in throw FilmSearchError.unavailable }
        let content = FilmSearchResultsContent(service: service, images: images, theme: PageTheme())
        content.loadViewIfNeeded(); content.submit("原查询")
        let session = try XCTUnwrap(content.session)
        await session.drain()
        let modes = try XCTUnwrap(find("film.search.mode", in: content.view) as? UISegmentedControl)
        modes.selectedSegmentIndex = 1; modes.sendActions(for: .valueChanged)
        let grid = try XCTUnwrap(find("film.search.grid", in: content.view) as? UICollectionView)
        await waitFor { grid.numberOfItems(inSection: 0) == 2 }
        let library = FilmLibrary(defaults: defaults)
        var contexts: [FilmSearchContext] = []
        content.onSelect = { contexts.append($0) }
        for index in 0..<2 { content.collectionView(grid, didSelectItemAt: IndexPath(item: index, section: 0)) }
        XCTAssertEqual(contexts.count, 2)
        let zero = try XCTUnwrap(contexts.first { $0.selectedCard.detailID == .number(0) })
        let stringZero = try XCTUnwrap(contexts.first { $0.selectedCard.detailID == .string("0") })
        XCTAssertEqual(zero.selectedCard.text("siteDetail"), stringZero.selectedCard.text("siteDetail"))
        XCTAssertNotEqual(zero.detail.item.identity, stringZero.detail.item.identity)
        XCTAssertEqual(zero.detail.item.id, zero.selectedCard.id)
        XCTAssertEqual(stringZero.detail.item.id, stringZero.selectedCard.id)
        XCTAssertEqual(try zero.playbackTarget().value["source"]?.filmObject?["detailId"], .number(0))
        XCTAssertEqual(try stringZero.playbackTarget().value["source"]?.filmObject?["detailId"], .string("0"))
        let pages = [zero, stringZero].map {
            FilmDetailViewController(searchContext: $0, library: library, images: images, themeProvider: SearchUIKitTheme())
        }
        for page in pages { page.loadViewIfNeeded() }
        let numericFavorite = try XCTUnwrap(find("film.detail.favorite", in: pages[0].view) as? UIButton)
        let stringFavorite = try XCTUnwrap(find("film.detail.favorite", in: pages[1].view) as? UIButton)
        numericFavorite.sendActions(for: .touchUpInside)
        XCTAssertTrue(library.contains(zero.detail.item)); XCTAssertFalse(library.contains(stringZero.detail.item))
        stringFavorite.sendActions(for: .touchUpInside)
        XCTAssertEqual(library.favorites.count, 2)
        let restored = FilmLibrary(defaults: defaults)
        XCTAssertTrue(restored.contains(zero.detail.item)); XCTAssertTrue(restored.contains(stringZero.detail.item))
        numericFavorite.sendActions(for: .touchUpInside)
        XCTAssertFalse(library.contains(zero.detail.item)); XCTAssertTrue(library.contains(stringZero.detail.item))
        XCTAssertEqual(FilmLibrary(defaults: defaults).favorites, [stringZero.detail.item.identity])
        content.close()
    }

    private func find(_ id: String, in view: UIView) -> UIView? {
        if view.accessibilityIdentifier == id { return view }
        for child in view.subviews { if let found = find(id, in: child) { return found } }
        return nil
    }
    private func waitFor(_ predicate: @escaping @MainActor () -> Bool) async {
        let ready = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated { predicate() } }, evaluatedWith: nil)
        await fulfillment(of: [ready], timeout: 3)
        XCTAssertTrue(predicate())
    }
}

@MainActor
private final class SearchUIKitTheme: PageThemeProviding {
    let pageColorway = PageColorway.allCases[0]
    let animatesPageColorway = false
    var pageThemeChanges: AnyPublisher<Void, Never> { Empty(completeImmediately: false).eraseToAnyPublisher() }
}

private actor FilmSearchUIKitReads {
    var pages: [Int] = []
    private var failFirst = true
    func read(page: Int) throws -> RuntimeValue {
        pages.append(page)
        if failFirst { failFirst = false; throw FilmSearchError.unavailable }
        return .object(["pagecount": .number(2), "list": .array([
            .object(["vod_id": .string(page == 1 ? "\u{e9}" : "e\u{301}"), "vod_name": .string(page == 1 ? "真实标题" : "真实标题高清"), "vod_remarks": .string("更新至12集")])])])
    }
}
#endif
