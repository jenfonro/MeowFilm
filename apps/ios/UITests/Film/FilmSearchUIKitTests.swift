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
        let variants = [("来源名称", "更新至12集"), ("聚合", "更新至24集"), ("电影", "2026"), ("剧集", "共2季78集"),
                        (String(repeating: "长站点名称", count: 8), String(repeating: "更新至第十二季十二集", count: 8))]
        for dark in [false, true] {
            let theme = PageTheme(dark: dark)
            for width in [CGFloat(90), 110, 160] {
                let cellHeight = width * 1.5 + 59
                let canvas = UIView(frame: CGRect(x: 0, y: 0, width: width * 3 + 40, height: (cellHeight + 16) * 2 + 16))
                canvas.backgroundColor = UIColor(theme.top)
                let cell = FilmPosterCell(frame: CGRect(x: 0, y: 0, width: width, height: width * 1.5 + 59))
                for (index, variant) in variants.enumerated() {
                    let (label, textBadge) = variant
                    cell.configure(item: item, images: images, theme: theme, textBadge: textBadge, label: label)
                    cell.layoutIfNeeded()
                    let badge = try XCTUnwrap(find("film.poster.badge", in: cell) as? UILabel)
                    let source = try XCTUnwrap(find("film.poster.label", in: cell) as? UILabel)
                    let badgeContainer = try XCTUnwrap(find("film.poster.badge.container", in: cell))
                    let sourceContainer = try XCTUnwrap(find("film.poster.label.container", in: cell))
                    let title = try XCTUnwrap(cell.contentView.subviews.compactMap { $0 as? UILabel }.first { $0.text == item.title })
                    XCTAssertEqual(title.numberOfLines, 2)
                    XCTAssertEqual(title.font, .systemFont(ofSize: 13))
                    XCTAssertTrue(cell.contentView.bounds.contains(title.frame))
                    XCTAssertGreaterThan(sourceContainer.frame.minY, title.frame.maxY)
                    XCTAssertEqual(badge.text, textBadge)
                    XCTAssertEqual(source.text, label)
                    XCTAssertTrue(cell.contentView.bounds.contains(sourceContainer.frame))
                    XCTAssertEqual(badgeContainer.frame.minY, 8, accuracy: 0.01)
                    XCTAssertEqual(badgeContainer.frame.maxX, width - 8, accuracy: 0.01)
                    XCTAssertLessThanOrEqual(badgeContainer.bounds.width, width * 0.7)
                    XCTAssertGreaterThanOrEqual(badgeContainer.frame.minX, 0)
                    XCTAssertLessThanOrEqual(badgeContainer.frame.maxY, width * 1.5)
                    XCTAssertEqual(badgeContainer.backgroundColor, UIColor(PageColor(rgb: 0xf97316)))
                    XCTAssertEqual(badgeContainer.layer.cornerRadius, 8)
                    XCTAssertEqual(badgeContainer.layer.shadowOpacity, 0.18, accuracy: 0.001)
                    XCTAssertNotNil(badgeContainer.layer.shadowPath)
                    XCTAssertEqual(badge.textColor, .white)
                    XCTAssertEqual(badge.font, .systemFont(ofSize: 10))
                    XCTAssertEqual(badge.frame, badgeContainer.bounds.insetBy(dx: 8, dy: 2))
                    XCTAssertEqual(badge.lineBreakMode, .byTruncatingTail)
                    XCTAssertEqual(badge.numberOfLines, 1)
                    XCTAssertEqual(sourceContainer.frame.midX, width / 2, accuracy: 0.01)
                    XCTAssertLessThanOrEqual(sourceContainer.bounds.width, width)
                    XCTAssertEqual(sourceContainer.layer.cornerRadius, 8)
                    XCTAssertEqual(sourceContainer.layer.borderWidth, 1)
                    XCTAssertEqual(sourceContainer.backgroundColor,
                        dark ? UIColor.white.withAlphaComponent(0.06) : UIColor(PageColor(rgb: 0xf8fafc)))
                    XCTAssertEqual(sourceContainer.layer.borderColor,
                        (dark ? UIColor.white.withAlphaComponent(0.1) : UIColor(PageColor(rgb: 0xe2e8f0))).cgColor)
                    XCTAssertEqual(source.textColor, UIColor(PageColor(rgb: dark ? 0x9ca3af : 0x6b7280)))
                    XCTAssertEqual(source.font, .systemFont(ofSize: 10))
                    XCTAssertEqual(source.frame, sourceContainer.bounds.insetBy(dx: 9, dy: 3))
                    XCTAssertEqual(source.textAlignment, .center)
                    XCTAssertEqual(source.lineBreakMode, .byTruncatingTail)
                    XCTAssertEqual(source.numberOfLines, 1)
                    if label == "电影" { XCTAssertLessThan(sourceContainer.bounds.width, width) }
                    if index == variants.count - 1 {
                        XCTAssertEqual(sourceContainer.bounds.width, width)
                        XCTAssertEqual(badgeContainer.bounds.width, width * 0.7)
                    }
                    XCTAssertFalse(badge.isHidden)
                    XCTAssertFalse(source.isHidden)
                    XCTAssertFalse(badgeContainer.isHidden)
                    XCTAssertFalse(sourceContainer.isHidden)
                    XCTAssertFalse(cell.accessibilityLabel?.contains("8.9") == true, "Relevance and film scores are not search badges")
                    let restingTitle = title.frame, restingBadge = badgeContainer.frame, restingSource = sourceContainer.frame
                    cell.isHighlighted = true; cell.setNeedsLayout(); cell.layoutIfNeeded()
                    XCTAssertEqual(title.frame, restingTitle, "Search titles keep both lines during press feedback")
                    XCTAssertEqual(badgeContainer.frame, restingBadge)
                    XCTAssertEqual(sourceContainer.frame, restingSource)
                    cell.resetPressFeedback()
                    let rendered = FilmPosterCell(frame: CGRect(x: 10 + CGFloat(index % 3) * (width + 10),
                        y: 8 + CGFloat(index / 3) * (cellHeight + 16), width: width, height: cellHeight))
                    rendered.configure(item: item, images: images, theme: theme, textBadge: textBadge, label: label)
                    canvas.addSubview(rendered)
                    rendered.layoutIfNeeded()
                }
                cell.prepareForReuse()
                cell.configure(item: item, images: images, theme: theme)
                cell.layoutIfNeeded()
                XCTAssertTrue(try XCTUnwrap(find("film.poster.badge", in: cell)).isHidden)
                XCTAssertTrue(try XCTUnwrap(find("film.poster.label", in: cell)).isHidden)
                XCTAssertTrue(try XCTUnwrap(find("film.poster.badge.container", in: cell)).isHidden)
                XCTAssertTrue(try XCTUnwrap(find("film.poster.label.container", in: cell)).isHidden)
                XCTAssertEqual(cell.accessibilityLabel, item.title, "Existing home presentation is unchanged")
                let format = UIGraphicsImageRendererFormat(); format.scale = 2
                let image = UIGraphicsImageRenderer(bounds: canvas.bounds, format: format).image { context in
                    canvas.layer.render(in: context.cgContext)
                }
                let attachment = XCTAttachment(image: image)
                attachment.name = "film-search-server-posters-\(Int(width))-\(dark ? "dark" : "light")"
                attachment.lifetime = .keepAlways
                add(attachment)
            }
        }
    }

    func testSearchContentUsesExistingPostersKeepsBothModesAndDeliversLiveContext() async throws {
        let reads = FilmSearchUIKitReads()
        let service = FilmSearchService(transport: reads.transport, sites: { [FilmSearchUIKitReads.site] })
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
        let modes = try XCTUnwrap(find("film.search.mode", in: content.view) as? UISwitch)
        modes.isOn = true; modes.sendActions(for: .valueChanged)
        await waitFor { grid.numberOfItems(inSection: 0) == 2 }
        XCTAssertEqual(grid.numberOfItems(inSection: 0), 2)
        content.collectionView(grid, didSelectItemAt: IndexPath(item: 1, section: 0))
        XCTAssertEqual(context?.sources.first?.catField("siteDetail"), .string("e\u{301}"),
                       "Raw selection preserves exact protocol identity, including canonically equivalent IDs")
        modes.isOn = false; modes.sendActions(for: .valueChanged)
        await waitFor { grid.numberOfItems(inSection: 0) == 1 }
        XCTAssertEqual(grid.numberOfItems(inSection: 0), 1)
        content.submit("原查询")
        XCTAssertTrue(content.session === session)
        let calls = await reads.calls
        XCTAssertEqual(calls.map(\.0), ["search.open", "search.command", "search.command"])
        XCTAssertEqual(calls.dropFirst().map { $0.1.filmObject?["action"] }, [.string("more"), .string("more")])
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
        let reads = FilmSearchUIKitReads(typedZero: true)
        let service = FilmSearchService(transport: reads.transport, sites: { [site] })
        addTeardownBlock { await service.close() }
        let images = ArtworkImages(maximumBytes: 4 * 1_024 * 1_024) { _ in throw FilmSearchError.unavailable }
        let content = FilmSearchResultsContent(service: service, images: images, theme: PageTheme())
        content.loadViewIfNeeded(); content.submit("原查询")
        let session = try XCTUnwrap(content.session)
        await session.drain()
        let modes = try XCTUnwrap(find("film.search.mode", in: content.view) as? UISwitch)
        modes.isOn = true; modes.sendActions(for: .valueChanged)
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
        let numericRequest = try await zero.playbackRequest(sources: [site.playbackSource], owner: nil)
        let stringRequest = try await stringZero.playbackRequest(sources: [site.playbackSource], owner: nil)
        XCTAssertEqual(numericRequest.target.value["source"]?.filmObject?["detailId"], .number(0))
        XCTAssertEqual(stringRequest.target.value["source"]?.filmObject?["detailId"], .string("0"))
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
    let pagePalette = PagePalette.preset(.lilac)
    let animatesPageColorway = false
    var pageThemeChanges: AnyPublisher<Void, Never> { Empty(completeImmediately: false).eraseToAnyPublisher() }
}

private actor FilmSearchUIKitReads {
    static let id = UUID(uuidString: "7188ca12-b5ea-4f08-a967-f9af9b3ed5bf")!
    static let site = FilmSearchSite(cat: .init(installationId: "fixture", name: "fixture", sourceType: "local", generation: 4),
        source: .init(key: .init(installationId: "fixture", siteKey: "site"), name: "同一来源", api: "fixture", actions: ["search"], methods: [:]))
    private(set) var calls: [(String, RuntimeValue)] = []
    private var round = 0
    let typedZero: Bool
    init(typedZero: Bool = false) { self.typedZero = typedZero }
    nonisolated var transport: FilmSearchTransport {
        .init(request: { try await self.call($0, $1) }, observation: { _, _, _ in
            try await self.observe()
        })
    }
    private var observer: CheckedContinuation<Void, Error>?
    private func observe() async throws {
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                if Task.isCancelled { continuation.resume(throwing: CancellationError()) }
                else { observer = continuation }
            }
        } onCancel: { Task { await self.cancelObservation() } }
    }
    private func cancelObservation() {
        observer?.resume(throwing: CancellationError()); observer = nil
    }
    func call(_ operation: String, _ input: RuntimeValue) throws -> RuntimeValue {
        calls.append((operation, input))
        if operation == "search.release" { return .object(["released": .bool(true)]) }
        if operation == "search.command" { round += 1 }
        let a = row(typedZero ? .number(0) : .string("é"), key: "site:a")
        let b = row(typedZero ? .string("0") : .string("e\u{301}"), key: "site:b")
        if operation == "search.handoff" {
            let selection = try input.filmRequiredObject().filmString("selectionId")
            let target = FilmPlaybackTarget(id: selection, kind: .source, title: "Node单源", source: ["key": .string(Self.site.key), "detailId": selection == "site:a" ? .number(0) : .string("0")])
            return .object(["target": .object(target.value), "sources": .array([.object(Self.site.playbackSource.value)]),
                "selectionId": .string(selection), "revision": .number(1), "query": .string("原查询"),
                "results": .array([]), "resultHints": .array([]), "search": .array([]), "metadata": .null, "cacheEntries": .array([]),
                "searchReference": .object(["sessionId": .string(Self.id.uuidString.lowercased()), "selectionId": .string(selection)])])
        }
        let rows: [RuntimeValue] = typedZero || round > 1 ? [a, b] : round == 1 ? [a] : []
        let group: RuntimeValue = .object(["id": .string("group:opaque"), "displayKey": .string("group:opaque"), "title": .string("真实标题"),
            "sourceKind": .string("site"), "siteLabel": .string(rows.count > 1 ? "聚合" : "同一来源"), "aggregateItems": .array(rows)])
        return .object(["sessionId": .string(Self.id.uuidString.lowercased()), "query": .string("原查询"),
            "round": .number(Double(round + 1)), "revision": .number(Double(round + 1)), "sequence": .number(Double(round + 1)),
            "siteState": .string(rows.isEmpty ? "failed" : "completed"), "metadataState": .string("unavailable"),
            "loading": .bool(false), "hasMore": .bool(!typedZero && round < 2), "noSources": .bool(false),
            "cards": .array(rows.isEmpty ? [] : [group]), "rawCards": .array(rows), "siteItems": .array(rows), "tmdbItems": .array([]),
            "metadataSearch": .null, "metadataQuery": .string("原查询"), "metadataDetails": .object([:])])
    }
    private func row(_ id: RuntimeValue, key: String) -> RuntimeValue {
        .object(["id": .string(key), "displayKey": .string(key), "detailId": id,
            "siteDetail": typedZero ? .string("0") : id, "title": .string("真实标题"), "sourceKind": .string("site"),
            "siteKey": .string(Self.site.key), "siteLabel": .string("同一来源"), "textBadge": .string("更新至12集"),
            "raw": .object(["vod_id": id, "vod_name": .string("真实标题"), "vod_remarks": .string("更新至12集")])])
    }
}
#endif
