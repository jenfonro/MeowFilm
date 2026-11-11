#if os(iOS)
import XCTest
import UIKit
import Combine
import MeowUI
import MeowRuntime
@testable import MeowFilmFeature

@MainActor
final class FilmUIKitTests: XCTestCase {
    func testCategoryGridHasThreeColumnsAtPhoneWidthsAndSelectsExistingItems() async throws {
        let source = FilmCategoryUIKitSource()
        for category in FilmCategory.allCases {
            let model = FilmCategoryModel(category: category, source: source)
            await model.loadIfNeeded()?.value
            let page = FilmCategoryViewController(model: model, images: artworkImages(), themeProvider: FilmUIKitTheme())
            page.loadViewIfNeeded()
            let collection = try XCTUnwrap(identified("film.category.grid", in: page.view) as? UICollectionView)
            defer { PageViewController.invalidatePages(in: page); model.close() }
            var selected: FilmItem?
            page.onSelectItem = { selected = $0 }
            for width in [CGFloat(375), 402, 440] {
                let context = "\(category.rawValue) at width \(width)"
                page.view.frame = CGRect(x: 0, y: 0, width: width, height: 844)
                page.view.setNeedsLayout()
                page.view.layoutIfNeeded()
                page.view.layoutIfNeeded()
                collection.collectionViewLayout.invalidateLayout()
                collection.layoutIfNeeded()
                XCTAssertFalse(collection.hasAmbiguousLayout, context)
                XCTAssertGreaterThan(collection.bounds.height, 0, context)
                if category == .anime {
                    XCTAssertFalse(descendants(page.view).contains { $0 is FilterTagsView })
                    XCTAssertEqual(collection.frame.minY, page.pageHeader.frame.maxY, accuracy: 0.5, context)
                }
                let frames = try (0..<6).map {
                    try XCTUnwrap(collection.layoutAttributesForItem(at: IndexPath(item: $0, section: 0))).frame
                }
                for index in 0..<3 {
                    let cell = try XCTUnwrap(collection.cellForItem(at: IndexPath(item: index, section: 0)),
                                            context)
                    XCTAssertTrue(collection.bounds.contains(cell.frame), context)
                }
                XCTAssertEqual(frames[0].minY, frames[2].minY, accuracy: 0.5)
                XCTAssertGreaterThan(frames[3].minY, frames[0].maxY)
                XCTAssertEqual(frames[0].minX, 16, accuracy: 0.5)
                XCTAssertEqual(frames[2].maxX, width - 16, accuracy: 0.5)
                XCTAssertEqual(frames[1].width, frames[0].width, accuracy: 0.5)
                XCTAssertEqual(frames[3].minX, frames[0].minX, accuracy: 0.5)
                let attachment = XCTAttachment(image: UIGraphicsImageRenderer(bounds: page.view.bounds).image { context in
                    page.view.layer.render(in: context.cgContext)
                })
                attachment.name = "film-category-\(category.rawValue)-\(Int(width))"
                attachment.lifetime = .keepAlways
                add(attachment)
            }
            page.collectionView(collection, didSelectItemAt: IndexPath(item: 1, section: 0))
            XCTAssertEqual(selected?.identity, model.items[1].identity)
        }
    }

    func testCategoryFilterRowsExpandIndependentlyAndSelectCombinedValues() async throws {
        let model = FilmCategoryModel(category: .movie, source: FilmCategoryUIKitSource())
        await model.loadIfNeeded()?.value
        let page = FilmCategoryViewController(model: model, images: artworkImages(), themeProvider: FilmUIKitTheme())
        page.loadViewIfNeeded()
        defer { PageViewController.invalidatePages(in: page); model.close() }
        page.view.frame = CGRect(x: 0, y: 0, width: 320, height: 844)
        page.view.layoutIfNeeded()
        page.view.layoutIfNeeded()
        let category = try XCTUnwrap(identified("film.category.category.tags", in: page.view) as? FilterTagsView)
        let area = try XCTUnwrap(identified("film.category.area.tags", in: page.view) as? FilterTagsView)
        let categoryToggle = try XCTUnwrap(identified("film.category.category.tags.toggle", in: page.view) as? UIButton)
        let areaToggle = try XCTUnwrap(identified("film.category.area.tags.toggle", in: page.view) as? UIButton)
        XCTAssertTrue(category.hasOverflow)
        XCTAssertTrue(area.hasOverflow)
        let initialAreaY = area.convert(area.bounds, to: page.view).minY
        categoryToggle.sendActions(for: .touchUpInside)
        page.view.layoutIfNeeded()
        XCTAssertTrue(category.isExpanded)
        XCTAssertFalse(area.isExpanded)
        XCTAssertGreaterThan(area.convert(area.bounds, to: page.view).minY, initialAreaY)
        areaToggle.sendActions(for: .touchUpInside)
        page.view.layoutIfNeeded()
        XCTAssertTrue(area.isExpanded)
        let japan = try XCTUnwrap(identified("film.category.area.tag.日本", in: page.view) as? UIButton)
        japan.sendActions(for: .touchUpInside)
        XCTAssertFalse(area.isExpanded)
        XCTAssertTrue(category.isExpanded)
        let best = try XCTUnwrap(identified("film.category.category.tag.豆瓣高分", in: page.view) as? UIButton)
        best.sendActions(for: .touchUpInside)
        XCTAssertFalse(category.isExpanded)
        XCTAssertEqual(model.filters, ["category": "豆瓣高分", "area": "日本"])
        await model.loadMore()?.value
    }

    func testTVCategoryLayoutHidesAndRestoresInactiveRegionFilter() async throws {
        let model = FilmCategoryModel(category: .tv, source: FilmCategoryUIKitSource())
        await model.loadIfNeeded()?.value
        let page = FilmCategoryViewController(model: model, images: artworkImages(), themeProvider: FilmUIKitTheme())
        let window = mount(page)
        defer { release(page, window: window); model.close() }
        let collection = try XCTUnwrap(identified("film.category.grid", in: page.view) as? UICollectionView)
        let initialGridY = collection.frame.minY
        let japan = try XCTUnwrap(identified("film.category.area.tag.tv_japanese", in: page.view) as? UIButton)
        japan.sendActions(for: .touchUpInside)
        await model.loadMore()?.value
        for category in ["tv_animation", "tv_documentary"] {
            let option = try XCTUnwrap(identified("film.category.category.tag.\(category)", in: page.view) as? UIButton)
            option.sendActions(for: .touchUpInside)
            await model.loadMore()?.value
            await waitFor("inactive region row removed") {
                self.identified("film.category.area.tags", in: page.view) == nil
            }
            page.view.layoutIfNeeded()
            XCTAssertLessThan(collection.frame.minY, initialGridY)
            XCTAssertFalse(collection.hasAmbiguousLayout)
            XCTAssertGreaterThan(collection.bounds.height, 0)
            let tv = try XCTUnwrap(identified("film.category.category.tag.tv", in: page.view) as? UIButton)
            tv.sendActions(for: .touchUpInside)
            await model.loadMore()?.value
            await waitFor("region row restored") {
                self.identified("film.category.area.tags", in: page.view) != nil
            }
            page.view.layoutIfNeeded()
            XCTAssertEqual(collection.frame.minY, initialGridY, accuracy: 0.5)
            XCTAssertFalse(collection.hasAmbiguousLayout)
            XCTAssertEqual(model.filters["area"], "tv_japanese")
            let restored = try XCTUnwrap(identified("film.category.area.tag.tv_japanese", in: page.view) as? UIButton)
            XCTAssertTrue(restored.accessibilityTraits.contains(.selected))
        }
    }

    func testHomeMoreOpensMatchingCategoryAndUnsupportedSourceHasNoEntry() async throws {
        let model = FilmHomeModel(sources: [FilmCategoryUIKitSource()])
        await model.loadIfNeeded()?.value
        let page = FilmHomeViewController(model: model, images: artworkImages(), themeProvider: FilmUIKitTheme())
        var selected: FilmCategory?
        page.onSelectCategory = { selected = $0.category }
        let window = mount(page)
        defer { release(page, window: window); model.close() }
        let more = try XCTUnwrap(identified("film.home.more.movie", in: page.view) as? UIButton)
        more.sendActions(for: .touchUpInside)
        XCTAssertEqual(selected, .movie)
        let unsupported = FilmHomeModel(sources: [FilmUIKitSource(id: "other", title: "其他", sections: [])])
        XCTAssertNil(unsupported.categoryModel(sectionID: "movie"))
        unsupported.close()
    }

    func testCategoryExplicitRetryPresentsTheSameFailureAgain() async throws {
        let runner = XCUIApplication(bundleIdentifier: try XCTUnwrap(Bundle.main.bundleIdentifier))
        try await performRunnerAction { $0.activate() }
        let source = FilmFailingUIKitSource()
        let model = FilmCategoryModel(category: .anime, source: source)
        let page = FilmCategoryViewController(model: model, images: artworkImages(), themeProvider: FilmUIKitTheme())
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first)
        let previousKeyWindow = scene.windows.first { $0.isKeyWindow }
        let window = mount(page)
        defer {
            page.dismiss(animated: false)
            release(page, window: window)
            model.close()
            previousKeyWindow?.makeKey()
        }
        await waitFor("initial category error presented") {
            guard let alert = page.presentedViewController as? UIAlertController else { return false }
            return alert.view.window != nil && !alert.isBeingPresented
        }
        let first = try XCTUnwrap(page.presentedViewController as? UIAlertController)
        XCTAssertEqual(source.categoryReads, 1)
        XCTAssertEqual(first.actions.map(\.title), ["确定", "重试"])
        XCTAssertEqual(first.actions.map(\.style), [.cancel, .default])
        let attachment = XCTAttachment(image: UIGraphicsImageRenderer(bounds: window.bounds).image { _ in
            window.drawHierarchy(in: window.bounds, afterScreenUpdates: true)
        })
        attachment.name = "film-category-error-retry"
        attachment.lifetime = .keepAlways
        add(attachment)
        XCTAssertTrue(runner.alerts["错误"].buttons["重试"].waitForExistence(timeout: 2))
        try await performRunnerAction { $0.alerts["错误"].buttons["重试"].tap() }
        await waitFor("same category failure presented for the new request") {
            guard let alert = page.presentedViewController as? UIAlertController else { return false }
            return alert !== first && alert.view.window != nil && !alert.isBeingPresented
        }
        let second = try XCTUnwrap(page.presentedViewController as? UIAlertController)
        XCTAssertEqual(source.categoryReads, 2)
        XCTAssertFalse(model.isLoading)
        XCTAssertEqual(second.message, first.message)
        XCTAssertTrue(try XCTUnwrap(second.actions.first { $0.title == "重试" }).isEnabled)
        XCTAssertTrue(runner.alerts["错误"].buttons["重试"].isEnabled)
        try await performRunnerAction { $0.alerts["错误"].buttons["确定"].tap() }
        await waitFor("category error acknowledged") { page.presentedViewController == nil && page.canPresentPageAlert }
        page.pageDidFinishAppearing()
        await deliverMainRunLoop()
        XCTAssertNil(page.presentedViewController, "An acknowledged failure does not present again without a request")
        XCTAssertEqual(source.categoryReads, 2, "Acknowledging must not request again")
        let collection = try XCTUnwrap(identified("film.category.grid", in: page.view) as? UICollectionView)
        let refresh = try XCTUnwrap(collection.refreshControl)
        refresh.beginRefreshing()
        refresh.sendActions(for: .valueChanged)
        await waitFor("pull refresh can request after acknowledgement") {
            guard let alert = page.presentedViewController as? UIAlertController else { return false }
            return alert !== second && alert.view.window != nil && !alert.isBeingPresented
        }
        XCTAssertEqual(source.categoryReads, 3)
        XCTAssertFalse(model.isLoading)
        XCTAssertFalse(refresh.isRefreshing)
    }

    func testHomeExplicitRetryPresentsTheSameFailureAgain() async throws {
        let runner = XCUIApplication(bundleIdentifier: try XCTUnwrap(Bundle.main.bundleIdentifier))
        try await performRunnerAction { $0.activate() }
        let source = FilmFailingUIKitSource()
        let model = FilmHomeModel(sources: [source])
        let page = FilmHomeViewController(model: model, images: artworkImages(), themeProvider: FilmUIKitTheme())
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first)
        let previousKeyWindow = scene.windows.first { $0.isKeyWindow }
        let window = mount(page)
        defer {
            page.dismiss(animated: false)
            release(page, window: window)
            model.close()
            previousKeyWindow?.makeKey()
        }
        await waitFor("initial home error presented") {
            guard let alert = page.presentedViewController as? UIAlertController else { return false }
            return alert.view.window != nil && !alert.isBeingPresented
        }
        let first = try XCTUnwrap(page.presentedViewController as? UIAlertController)
        XCTAssertEqual(source.homeReads, 1)
        XCTAssertEqual(first.actions.map(\.title), ["确定", "重试"])
        XCTAssertEqual(first.actions.map(\.style), [.cancel, .default])
        let attachment = XCTAttachment(image: UIGraphicsImageRenderer(bounds: window.bounds).image { _ in
            window.drawHierarchy(in: window.bounds, afterScreenUpdates: true)
        })
        attachment.name = "film-home-error-retry"
        attachment.lifetime = .keepAlways
        add(attachment)
        XCTAssertTrue(runner.alerts["错误"].buttons["重试"].waitForExistence(timeout: 2))
        try await performRunnerAction { $0.alerts["错误"].buttons["重试"].tap() }
        await waitFor("same home failure presented for the new request") {
            guard let alert = page.presentedViewController as? UIAlertController else { return false }
            return alert !== first && alert.view.window != nil && !alert.isBeingPresented
        }
        let second = try XCTUnwrap(page.presentedViewController as? UIAlertController)
        XCTAssertEqual(source.homeReads, 2)
        XCTAssertFalse(model.isLoading)
        XCTAssertEqual(second.message, first.message)
        XCTAssertTrue(try XCTUnwrap(second.actions.first { $0.title == "重试" }).isEnabled)
        XCTAssertTrue(runner.alerts["错误"].buttons["重试"].isEnabled)
        try await performRunnerAction { $0.alerts["错误"].buttons["确定"].tap() }
        await waitFor("home error acknowledged") { page.presentedViewController == nil && page.canPresentPageAlert }
        page.pageDidFinishAppearing()
        await deliverMainRunLoop()
        XCTAssertNil(page.presentedViewController, "An acknowledged failure does not present again without a request")
        XCTAssertEqual(source.homeReads, 2, "Acknowledging must not request again")
        let collection = try homeCollection(in: page)
        let refresh = try XCTUnwrap(collection.refreshControl)
        refresh.beginRefreshing()
        refresh.sendActions(for: .valueChanged)
        await waitFor("pull refresh can request after acknowledgement") {
            guard let alert = page.presentedViewController as? UIAlertController else { return false }
            return alert !== second && alert.view.window != nil && !alert.isBeingPresented
        }
        XCTAssertEqual(source.homeReads, 3)
        XCTAssertFalse(model.isLoading)
        XCTAssertFalse(refresh.isRefreshing)
    }

    func testCategoryPaginationFailureRetriesSameOffsetAndKeepsItems() async throws {
        let runner = XCUIApplication(bundleIdentifier: try XCTUnwrap(Bundle.main.bundleIdentifier))
        try await performRunnerAction { $0.activate() }
        let source = FilmPaginationFailingUIKitSource()
        let model = FilmCategoryModel(category: .anime, source: source)
        let page = FilmCategoryViewController(model: model, images: artworkImages(), themeProvider: FilmUIKitTheme())
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first)
        let previousKeyWindow = scene.windows.first { $0.isKeyWindow }
        let window = mount(page)
        defer {
            page.dismiss(animated: false)
            release(page, window: window)
            model.close()
            previousKeyWindow?.makeKey()
        }
        await waitFor("initial category page completed") { !model.isLoading && model.items.count == 3 }
        let originalItems = model.items
        XCTAssertEqual(originalItems.count, 3)
        XCTAssertEqual(source.offsets, [0])
        let more = try button("film.category.more", in: page.view)
        let collection = try XCTUnwrap(identified("film.category.grid", in: page.view) as? UICollectionView)
        XCTAssertTrue(more.isEnabled)
        more.sendActions(for: .touchUpInside)
        await waitFor("next page failure presented") {
            guard let alert = page.presentedViewController as? UIAlertController else { return false }
            return alert.view.window != nil && !alert.isBeingPresented
        }
        let first = try XCTUnwrap(page.presentedViewController as? UIAlertController)
        XCTAssertEqual(source.offsets, [0, 24])
        XCTAssertEqual(model.items, originalItems)
        XCTAssertEqual(collection.numberOfItems(inSection: 0), originalItems.count)
        XCTAssertFalse(model.isLoading)
        XCTAssertFalse(more.isEnabled)
        try await performRunnerAction { $0.alerts["错误"].buttons["重试"].tap() }
        await waitFor("same offset retry failure presented") {
            guard let alert = page.presentedViewController as? UIAlertController else { return false }
            return alert !== first && alert.view.window != nil && !alert.isBeingPresented
        }
        XCTAssertEqual(source.offsets, [0, 24, 24])
        XCTAssertEqual(model.items, originalItems)
        XCTAssertEqual(collection.numberOfItems(inSection: 0), originalItems.count)
        XCTAssertFalse(model.isLoading)
        XCTAssertFalse(more.isEnabled)
        XCTAssertEqual((page.presentedViewController as? UIAlertController)?.message, first.message)
        XCTAssertTrue(runner.alerts["错误"].buttons["重试"].isEnabled)
        try await performRunnerAction { $0.alerts["错误"].buttons["确定"].tap() }
        await waitFor("pagination error acknowledged") { page.presentedViewController == nil && page.canPresentPageAlert }
        page.pageDidFinishAppearing()
        await deliverMainRunLoop()
        XCTAssertNil(page.presentedViewController)
        XCTAssertFalse(more.isEnabled)
        XCTAssertEqual(source.offsets, [0, 24, 24], "Neither rendering nor acknowledgement may retry pagination")
        XCTAssertEqual(model.items, originalItems)
    }

    func testHomeCachedSourcesWithTheSameEmptySectionUpdateItsHeaderAndCategoryEntry() async throws {
        let first = FilmEmptyCategoryUIKitSource()
        let second = FilmUIKitSource(id: "other", title: "其他源", sections: [
            .init(id: "movie", title: "其他源栏目", items: []),
        ])
        let model = FilmHomeModel(sources: [first, second])
        let page = FilmHomeViewController(model: model, images: artworkImages(), themeProvider: FilmUIKitTheme())
        let window = mount(page)
        defer { release(page, window: window); model.close() }
        let collection = try homeCollection(in: page)
        var selected: FilmCategoryModel?
        page.onSelectCategory = { selected = $0 }
        for id in ["empty-category", "other", "empty-category", "other"] {
            if model.selectedSourceID != id {
                model.selectSource(id: id)
            }
            await waitFor("source snapshot rendered: \(id)") {
                collection.layoutIfNeeded()
                guard !model.isLoading, model.selectedSourceID == id,
                    let header = collection.supplementaryView(
                        forElementKind: UICollectionView.elementKindSectionHeader, at: IndexPath(item: 0, section: 0))
                else { return false }
                return self.descendants(header).compactMap { $0 as? UILabel }.contains {
                    $0.text == (id == "other" ? "其他源栏目" : "分类源栏目")
                }
            }
            let header = try XCTUnwrap(collection.supplementaryView(
                forElementKind: UICollectionView.elementKindSectionHeader, at: IndexPath(item: 0, section: 0)))
            try assertText(id == "other" ? "其他源栏目" : "分类源栏目", in: header)
            let more = try button("film.home.more.movie", in: header)
            XCTAssertEqual(more.isHidden, id == "other")
            if id == "empty-category" {
                selected = nil
                more.sendActions(for: .touchUpInside)
                let category = try XCTUnwrap(selected)
                XCTAssertEqual(category.category, .movie)
                await category.loadIfNeeded()?.value
                XCTAssertEqual(category.items.first?.sourceID, id)
                category.close()
            }
        }
        let secondReads = await second.loadCount
        XCTAssertEqual(first.homeReads, 2)
        XCTAssertEqual(secondReads, 2)
    }

    func testPosterRailsFitThreeCompleteCardsAndTheEdgeOfAFourthAtPhoneWidths() async throws {
        let items = (0..<6).map {
            FilmItem(sourceID: "fixture", id: "\($0)", title: "影片 \($0)", score: "8.7")
        }
        let source = FilmUIKitSource(id: "fixture", title: "测试源", sections: [
            .init(id: "movies", title: "热门电影", items: items),
        ])
        let model = FilmHomeModel(sources: [source])
        let request = try XCTUnwrap(model.loadIfNeeded())
        await request.value
        let page = FilmHomeViewController(model: model, images: artworkImages(),
                                          themeProvider: FilmUIKitTheme())
        defer {
            PageViewController.invalidatePages(in: page)
            model.close()
        }
        page.loadViewIfNeeded()
        let collection = try homeCollection(in: page)

        for width in [CGFloat(375), 402, 440] {
            page.view.frame = CGRect(x: 0, y: 0, width: width, height: 844)
            page.view.setNeedsLayout()
            collection.collectionViewLayout.invalidateLayout()
            page.view.layoutIfNeeded()
            await waitFor("four poster cells at width \(width)") {
                page.view.layoutIfNeeded()
                collection.layoutIfNeeded()
                return (0..<4).allSatisfy {
                    collection.cellForItem(at: IndexPath(item: $0, section: 0)) is FilmPosterCell
                }
            }
            XCTAssertEqual(collection.bounds.width, width, accuracy: 0.5)
            let cells = try (0..<4).map {
                try XCTUnwrap(collection.cellForItem(at: IndexPath(item: $0, section: 0)) as? FilmPosterCell)
            }
            let frames = cells.map { $0.convert($0.bounds, to: collection) }
            let visibleFrames = cells.map { visibleFrame(of: $0, in: collection) }
            let viewport = collection.bounds
            XCTAssertGreaterThanOrEqual(frames[0].minX, viewport.minX)
            for (frame, visible) in zip(frames, visibleFrames).prefix(3) {
                XCTAssertGreaterThan(frame.width, 0)
                XCTAssertEqual(visible.width, frame.width, accuracy: 0.5,
                               "Width \(width) must show three complete posters")
            }
            XCTAssertGreaterThan(frames[3].minX, frames[2].maxX)
            let fourthVisibleWidth = visibleFrames[3].width
            XCTAssertGreaterThan(fourthVisibleWidth, 0, "Width \(width) must reveal the fourth poster")
            XCTAssertLessThan(fourthVisibleWidth, frames[3].width * 0.5,
                              "The fourth poster should show only a small edge")
            if let fifth = collection.cellForItem(at: IndexPath(item: 4, section: 0)) {
                XCTAssertEqual(visibleFrame(of: fifth, in: collection).width, 0, accuracy: 0.5,
                               "Width \(width) must not reveal a fifth poster")
            }
            let metrics = FilmPosterMetrics(viewportWidth: width)
            for cell in cells {
                let artwork = try XCTUnwrap(descendants(cell).compactMap { $0 as? ArtworkView }.first)
                XCTAssertEqual(cell.bounds.width, metrics.width, accuracy: 0.5)
                XCTAssertEqual(cell.bounds.height, metrics.posterHeight + 39, accuracy: 0.5)
                XCTAssertGreaterThan(artwork.bounds.height, 0)
                XCTAssertEqual(artwork.bounds.width / artwork.bounds.height, 2.0 / 3.0, accuracy: 0.001)
                XCTAssertEqual(artwork.bounds.width, cell.contentView.bounds.width, accuracy: 0.5)
                XCTAssertTrue(cell.accessibilityLabel?.hasPrefix("影片 ") == true)
                XCTAssertFalse(cell.accessibilityLabel?.contains("8.7") == true, "Home cards read only the title")
                XCTAssertFalse(descendants(cell).compactMap { $0 as? UILabel }.contains {
                    $0.text?.contains("★") == true || $0.text?.contains("8.7") == true
                }, "Home cards have no rating subtitle")
            }
        }
    }

    func testHeroGeometryZoomsOutWithoutGapsAndRevealsTheHeaderOnlyAsTheTitleArrives() {
        let height: CGFloat = 450, headerHeight: CGFloat = 118
        let arrival = height - headerHeight
        let resting = FilmHeroGeometry(offset: 0, height: height, headerHeight: headerHeight, reduceMotion: false)
        XCTAssertGreaterThan(resting.zoom, 1, "The poster starts enlarged inside its frame")
        XCTAssertEqual(resting.headerAlpha, 0)
        var previous = resting
        for offset in [CGFloat(0), 80, 160, arrival * 0.8, arrival * 0.9, arrival, arrival + 40, 4_000] {
            let normal = FilmHeroGeometry(offset: offset, height: height, headerHeight: headerHeight,
                                          reduceMotion: false)
            let reduced = FilmHeroGeometry(offset: offset, height: height, headerHeight: headerHeight,
                                           reduceMotion: true)
            XCTAssertGreaterThanOrEqual(normal.progress, previous.progress)
            XCTAssertLessThanOrEqual(normal.progress, 1)
            XCTAssertGreaterThanOrEqual(normal.zoom, 1, "Shrinking never uncovers the poster frame")
            XCTAssertLessThanOrEqual(normal.zoom, previous.zoom)
            XCTAssertGreaterThanOrEqual(normal.headerAlpha, previous.headerAlpha)
            XCTAssertLessThanOrEqual(normal.headerAlpha, 1)
            if offset <= arrival * 0.8 {
                XCTAssertEqual(normal.headerAlpha, 0, "The header stays clear while poster art is under it")
            }
            XCTAssertEqual(normal.translation, -offset, accuracy: 0.001,
                           "The poster frame and its fade scroll at the same speed as the text")
            XCTAssertEqual(reduced.zoom, 1)
            XCTAssertEqual(reduced.progress, normal.progress)
            XCTAssertEqual(reduced.headerAlpha, normal.headerAlpha)
            XCTAssertEqual(reduced.translation, -offset, accuracy: 0.001)
            previous = normal
        }
        let arrived = FilmHeroGeometry(offset: arrival, height: height, headerHeight: headerHeight,
                                       reduceMotion: false)
        XCTAssertEqual(arrived.progress, 1, accuracy: 0.000_1, "Progress completes when the title meets the header")
        XCTAssertEqual(arrived.zoom, 1, accuracy: 0.000_1)
        XCTAssertEqual(arrived.headerAlpha, 1, accuracy: 0.000_1)
        let overscrolled = FilmHeroGeometry(offset: -80, height: height, headerHeight: headerHeight,
                                            reduceMotion: false)
        XCTAssertEqual(overscrolled.progress, 0)
        XCTAssertEqual(overscrolled.zoom, resting.zoom)
        XCTAssertEqual(overscrolled.headerAlpha, 0)
        XCTAssertEqual(overscrolled.translation, 0, "Pulling down keeps the poster frame at the top")
        let zeroHeight = FilmHeroGeometry(offset: 100, height: 0, headerHeight: headerHeight, reduceMotion: true)
        XCTAssertTrue(zeroHeight.progress.isFinite)
        XCTAssertTrue(zeroHeight.zoom.isFinite)
        XCTAssertTrue(zeroHeight.headerAlpha.isFinite)
        XCTAssertTrue(zeroHeight.translation.isFinite)
    }

    func testHomeSourceSelectionRendersBothSourcesAndRestoresLoadedSourceWithoutRequestingAgain() async throws {
        let firstItem = FilmItem(sourceID: "first", id: "shared", title: "第一源影片", score: "8.1")
        let secondItem = FilmItem(sourceID: "second", id: "shared", title: "第二源影片", score: "9.2")
        let first = FilmUIKitSource(id: "first", title: "第一源", sections: [
            .init(id: "movies", title: "第一源栏目", items: [firstItem]),
        ])
        let second = FilmUIKitSource(id: "second", title: "第二源", sections: [
            .init(id: "movies", title: "第二源栏目", items: [secondItem]),
        ])
        let model = FilmHomeModel(sources: [first, second])
        let theme = FilmUIKitTheme()
        let page = FilmHomeViewController(model: model, images: artworkImages(), themeProvider: theme)
        let navigation = PageNavigationController(themeProvider: theme)
        navigation.setViewControllers([page], animated: false)
        let window = mount(navigation)
        defer { release(navigation, window: window) }
        let collection = try homeCollection(in: page)
        page.additionalSafeAreaInsets.left = 12
        page.additionalSafeAreaInsets.right = 18
        await waitFor("first source rendered") {
            page.view.layoutIfNeeded()
            collection.layoutIfNeeded()
            return self.identified("film.poster.first.shared", in: collection) != nil
        }
        let collectionFrame = collection.convert(collection.bounds, to: page.view)
        let safeFrame = page.view.safeAreaLayoutGuide.layoutFrame
        XCTAssertEqual(collectionFrame.minX, safeFrame.minX, accuracy: 0.5)
        XCTAssertEqual(collectionFrame.maxX, safeFrame.maxX, accuracy: 0.5)
        try assertText("影视", in: page.pageHeader)
        XCTAssertEqual(model.selectedSourceID, "first")
        try assertText("第一源栏目", in: collection)
        try assertText(firstItem.title, in: collection)
        var firstReads = await first.loadCount
        var secondReads = await second.loadCount
        XCTAssertEqual(firstReads, 1)
        XCTAssertEqual(secondReads, 0)

        model.selectSource(id: "second")
        await waitFor("second source rendered") {
            collection.layoutIfNeeded()
            return self.identified("film.poster.second.shared", in: collection) != nil
        }
        try assertText("影视", in: page.pageHeader)
        XCTAssertEqual(model.selectedSourceID, "second")
        try assertText("第二源栏目", in: collection)
        try assertText(secondItem.title, in: collection)
        XCTAssertNil(identified("film.poster.first.shared", in: collection))

        await model.selectSource(id: "first")?.value
        await waitFor("cached first source rendered") {
            collection.layoutIfNeeded()
            return self.identified("film.poster.first.shared", in: collection) != nil
        }
        try assertText(firstItem.title, in: collection)
        XCTAssertNil(identified("film.poster.second.shared", in: collection))
        try assertText("影视", in: page.pageHeader)
        XCTAssertEqual(model.selectedSourceID, "first")
        firstReads = await first.loadCount
        secondReads = await second.loadCount
        XCTAssertEqual(firstReads, 2)
        XCTAssertEqual(secondReads, 1)
    }

    func testHomeCardRoutesTheSelectedFilmAndReturningKeepsTheLoadedPage() async throws {
        let firstURL = URL(string: "https://artwork.example.invalid/first.png")!
        let selectedURL = URL(string: "https://artwork.example.invalid/selected.png")!
        let first = FilmItem(sourceID: "fixture", id: "first", title: "第一部", posterURL: firstURL)
        let selected = FilmItem(sourceID: "fixture", id: "selected", title: "选中的影片", posterURL: selectedURL)
        let images = artworkImages([firstURL: png(.red), selectedURL: png(.blue)])
        let (firstImage, _) = try await images.read(firstURL)
        let (selectedImage, _) = try await images.read(selectedURL)
        let source = FilmUIKitSource(id: "fixture", title: "测试源", sections: [
            .init(id: "movies", title: "热门电影", items: [first, selected]),
        ])
        let model = FilmHomeModel(sources: [source])
        let library = try isolatedLibrary()
        let theme = FilmUIKitTheme()
        let page = FilmHomeViewController(model: model, images: images, themeProvider: theme)
        let navigation = PageNavigationController(themeProvider: theme)
        var routed: [FilmItem] = []
        page.onSelectItem = { [weak navigation] item in
            routed.append(item)
            let detail = FilmDetailViewController(detail: .init(item: item), library: library,
                                                  images: images, themeProvider: theme)
            detail.loadViewIfNeeded()
            NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
            navigation?.pushViewController(detail, animated: false)
            navigation?.view.layoutIfNeeded()
        }
        navigation.setViewControllers([page], animated: false)
        let window = mount(navigation)
        defer { release(navigation, window: window) }
        let collection = try homeCollection(in: page)
        await waitFor("home visible before selecting a card") { page.isPageVisible }
        await waitFor("selected card visible") {
            collection.layoutIfNeeded()
            return self.identified("film.poster.fixture.selected", in: collection) != nil
        }
        XCTAssertTrue(routed.isEmpty, "Rendering cards must not navigate to details")
        XCTAssertEqual(navigation.viewControllers.count, 1)
        XCTAssertTrue(collection.delegate === page)
        let path = IndexPath(item: 1, section: 0)
        collection.selectItem(at: path, animated: false, scrollPosition: [])
        collection.delegate?.collectionView?(collection, didSelectItemAt: path)
        let detail = try XCTUnwrap(navigation.topViewController as? FilmDetailViewController)
        await waitFor("selected detail visible") { detail.isPageVisible }
        XCTAssertNotNil(detail.viewIfLoaded?.window, "A selected detail must be mounted in the navigation window")
        XCTAssertEqual(routed, [selected])
        XCTAssertEqual(try label("film.detail.title", in: detail.view).text, selected.title)
        let poster = try heroPoster(in: detail)
        XCTAssertTrue(poster.image === selectedImage)
        XCTAssertFalse(poster.image === firstImage)
        XCTAssertFalse(collection.indexPathsForSelectedItems?.contains(path) == true)

        let popped = navigation.popViewController(animated: false)
        XCTAssertTrue(popped === detail)
        navigation.view.layoutIfNeeded()
        await waitFor("home visible again") { page.isPageVisible }
        await deliverMainRunLoop()
        XCTAssertTrue(page.isPageActive)
        XCTAssertTrue(navigation.topViewController === page)
        XCTAssertEqual(routed, [selected])
        let reads = await source.loadCount
        XCTAssertEqual(reads, 2, "Returning keeps the visible snapshot while consulting the data owner again")
        XCTAssertNotNil(identified("film.poster.fixture.selected", in: collection))
    }

    func testDetailRendersSuppliedFieldsAndSeasonMenuReplacesEpisodes() async throws {
        let detail = suppliedDetail()
        let page = FilmDetailViewController(detail: detail, library: try isolatedLibrary(),
                                            images: artworkImages(), themeProvider: FilmUIKitTheme())
        var selected: [FilmItem] = []
        page.onSelectItem = { selected.append($0) }
        let window = mount(page)
        defer { release(page, window: window) }
        await waitFor("detail visible") { page.isPageVisible }
        page.view.layoutIfNeeded()

        XCTAssertEqual(try label("film.detail.title", in: page.view).text, "星海航线")
        try assertText("2 季 · 5 集 · 评分 8.7", in: page.view)
        try assertText("2026年11月1日", in: page.view)
        try assertText("两位领航员穿越星海，寻找失落的归途。", in: page.view)
        try assertText("演职人员", in: page.view)
        try assertText("相似作品", in: page.view)
        XCTAssertEqual(episodeButtons(in: page).map(\.accessibilityIdentifier), [
            "film.episode.first-unavailable", "film.episode.first-playable",
        ])
        let rails = descendants(page.view).compactMap { $0 as? FilmDetailRail }
        XCTAssertEqual(rails.count, 3)
        for rail in rails {
            XCTAssertFalse(rail.delaysContentTouches)
            XCTAssertTrue(rail.canCancelContentTouches)
            XCTAssertTrue(rail.touchesShouldCancel(in: UIButton(type: .custom)))
        }
        try assertText("1. 初见", in: page.view)
        try assertText("2. 夜航", in: page.view)
        try assertText("他们在港口相遇。", in: page.view)
        try assertText("飞船驶入漫长的夜色。", in: page.view)
        let cast = descendants(page.view).compactMap { $0 as? FilmCastView }
        XCTAssertEqual(cast.map(\.accessibilityLabel), ["林舟，导演", "周星，领航员"])
        for text in ["林舟", "导演", "周星", "领航员", "远方来信", "群星之间"] {
            try assertText(text, in: page.view)
        }
        let similar = descendants(page.view).compactMap { $0 as? FilmRelatedButton }
        XCTAssertEqual(similar.map(\.accessibilityIdentifier), [
            "film.similar.fixture.letters", "film.similar.other.stars",
        ])
        try button("film.similar.other.stars", in: page.view).sendActions(for: .touchUpInside)
        XCTAssertEqual(selected, [detail.similarItems[1]])

        let season = try button("film.detail.season", in: page.view)
        XCTAssertTrue(season.showsMenuAsPrimaryAction)
        XCTAssertTrue(season.isUserInteractionEnabled, "Several seasons expose the season menu")
        XCTAssertTrue(season.accessibilityTraits.contains(.button))
        XCTAssertTrue(season.title(for: .normal)?.contains("第一季") == true)
        XCTAssertEqual(try menuAction("第一季", in: season).state, .on)
        XCTAssertEqual(try menuAction("第二季", in: season).state, .off)
        invoke(try menuAction("第二季", in: season))
        page.view.layoutIfNeeded()
        XCTAssertTrue(season.title(for: .normal)?.contains("第二季") == true)
        XCTAssertEqual(try menuAction("第一季", in: season).state, .off)
        XCTAssertEqual(try menuAction("第二季", in: season).state, .on)
        XCTAssertEqual(episodeButtons(in: page).map(\.accessibilityIdentifier), [
            "film.episode.second-one", "film.episode.second-two", "film.episode.second-playable",
        ])
        XCTAssertNil(identified("film.episode.first-unavailable", in: page.view))
        XCTAssertNil(identified("film.episode.first-playable", in: page.view))
        try assertText("1. 归航", in: page.view)
        try assertText("2. 信号", in: page.view)
        try assertText("3. 重逢", in: page.view)
        try assertText("熟悉的声音再次响起。", in: page.view)
    }

    func testDetailSelectionUsesPublishedEpisodeCoordinatesAndExactMovieCandidateIdentity() async throws {
        let detail = suppliedDetail()
        let server = FilmUIKitPlaybackServer(), media = FilmUIKitPlaybackMedia()
        let model = playbackModel(server, media)
        await model.open()?.value
        await server.awaitObservation()
        let page = FilmDetailViewController(detail: detail, library: try isolatedLibrary(),
                                            images: artworkImages(), themeProvider: FilmUIKitTheme(), model: model)
        var presentations = 0
        page.onPresentPlayback = { presentations += 1 }
        let window = mount(page)
        defer { release(page, window: window) }
        await waitFor("playable detail visible") { page.isPageVisible }
        let play = try button("film.detail.play", in: page.view)
        XCTAssertTrue(play.isEnabled)
        XCTAssertTrue(media.inputs.isEmpty, "Mounting a detail must not create a media consumer")
        XCTAssertEqual(presentations, 0)
        play.sendActions(for: .touchUpInside)
        await model.waitForPendingWork()
        let episode = try button("film.episode.season-1-episode-2", in: page.view)
        XCTAssertTrue(episode.isEnabled)
        episode.sendActions(for: .touchUpInside)
        await model.waitForPendingWork()
        var commands = await server.calls().filter { $0.operation == "session.command" }
        XCTAssertEqual(commands.map(\.action), ["play", "select.episode"])
        XCTAssertEqual(commands.last?.input.filmObject?["episode"],
                       .object(["season": .number(1), "episode": .number(2)]))

        await server.reply(to: "browse.season", with: FilmUIKitPlaybackFixture.snapshot(
            browsing: FilmUIKitPlaybackFixture.browsing(season: 2),
            episodes: FilmUIKitPlaybackFixture.episodes(season: 2, numbers: [4, 7])))
        invoke(try menuAction("第二季", in: button("film.detail.season", in: page.view)))
        await model.waitForPendingWork()
        await deliverMainRunLoop()
        XCTAssertNil(identified("film.episode.season-1-episode-2", in: page.view))
        play.sendActions(for: .touchUpInside)
        await model.waitForPendingWork()
        try button("film.episode.season-2-episode-7", in: page.view).sendActions(for: .touchUpInside)
        await model.waitForPendingWork()
        commands = await server.calls().filter { $0.operation == "session.command" }
        XCTAssertEqual(commands.suffix(3).map(\.action), ["browse.season", "select.episode", "select.episode"])
        XCTAssertEqual(commands[3].input.filmObject?["episode"],
                       .object(["season": .number(2), "episode": .number(4)]),
                       "Primary play selects Node's actual first episode, never an invented S2E1")
        XCTAssertEqual(commands[4].input.filmObject?["episode"],
                       .object(["season": .number(2), "episode": .number(7)]))
        XCTAssertEqual(presentations, 4)
        XCTAssertTrue(media.inputs.isEmpty)

        let filmServer = FilmUIKitPlaybackServer(FilmUIKitPlaybackFixture.snapshot(kind: .movie,
            candidates: [FilmUIKitPlaybackFixture.candidate("opaque-movie")]))
        let filmModel = playbackModel(filmServer, FilmUIKitPlaybackMedia())
        await filmModel.open()?.value
        let film = FilmDetail(item: .init(sourceID: "fixture", id: "film", title: "电影"))
        let filmPage = FilmDetailViewController(detail: film, library: try isolatedLibrary(),
                                                images: artworkImages(), themeProvider: FilmUIKitTheme(), model: filmModel)
        let filmWindow = mount(filmPage)
        defer { release(filmPage, window: filmWindow) }
        await waitFor("film detail visible") { filmPage.isPageVisible }
        try button("film.candidate.opaque-movie", in: filmPage.view).sendActions(for: .touchUpInside)
        await filmModel.waitForPendingWork()
        let filmCommands = await filmServer.calls().filter { $0.operation == "session.command" }
        XCTAssertEqual(filmCommands.map(\.action), ["select.candidate"])
        XCTAssertEqual(filmCommands.last?.input.filmObject?["candidateId"], .string("opaque-movie"))
        XCTAssertNil(filmCommands.last?.input.filmObject?["episode"])
    }

    func testUnavailableDetailKeepsHonestEmptySectionsAndCannotPlay() async throws {
        let detail = FilmDetail(item: .init(sourceID: "fixture", id: "empty", title: "待补充影片"))
        let page = FilmDetailViewController(detail: detail, library: try isolatedLibrary(),
                                            images: artworkImages(), themeProvider: FilmUIKitTheme())
        var presentations = 0
        page.onPresentPlayback = { presentations += 1 }
        let window = mount(page)
        defer { release(page, window: window) }
        await waitFor("empty detail visible") { page.isPageVisible }
        page.view.layoutIfNeeded()
        for text in ["待补充影片", "暂无简介", "暂无集数", "演职人员", "暂无演职人员", "相似作品", "暂无相似作品"] {
            try assertText(text, in: page.view)
        }
        let season = try button("film.detail.season", in: page.view)
        let castHeading = try assertText("演职人员", in: page.view)
        XCTAssertEqual(season.title(for: .normal), "集数")
        XCTAssertFalse(season.isUserInteractionEnabled, "Without several seasons the heading is not a menu")
        XCTAssertEqual(season.accessibilityTraits, .header)
        XCTAssertEqual(season.titleColor(for: .normal), castHeading.textColor,
                       "A static episode heading matches the other section headings")
        XCTAssertEqual(season.titleLabel?.font, castHeading.font)
        XCTAssertEqual(page.preferredStatusBarStyle, .default, "Without poster art the chrome keeps the page style")
        XCTAssertEqual(try XCTUnwrap(identified("film.detail.scrim", in: page.view)).alpha, 0)
        XCTAssertTrue(episodeButtons(in: page).isEmpty)
        XCTAssertTrue(descendants(page.view).compactMap { $0 as? FilmCastView }.isEmpty)
        XCTAssertTrue(descendants(page.view).compactMap { $0 as? FilmRelatedButton }.isEmpty)
        let play = try button("film.detail.play", in: page.view)
        XCTAssertFalse(play.isEnabled)
        XCTAssertEqual(play.configuration?.title, "暂无片源")
        XCTAssertEqual(play.accessibilityLabel, "播放，暂无可用片源")
        play.sendActions(for: .touchUpInside)
        XCTAssertEqual(presentations, 0, "A dispatched action cannot fabricate a playable session")
    }

    func testSelectingASeasonWithoutPublishedEpisodesDisablesPrimaryPlayAndCanRecover() async throws {
        let server = FilmUIKitPlaybackServer(), media = FilmUIKitPlaybackMedia()
        let model = playbackModel(server, media)
        await model.open()?.value
        await server.reply(to: "browse.season", with: FilmUIKitPlaybackFixture.snapshot(
            browsing: FilmUIKitPlaybackFixture.browsing(season: 2), episodes: []))
        let page = FilmDetailViewController(detail: suppliedDetail(), library: try isolatedLibrary(),
                                            images: artworkImages(), themeProvider: FilmUIKitTheme(), model: model)
        var presentations = 0
        page.onPresentPlayback = { presentations += 1 }
        let window = mount(page)
        defer { release(page, window: window) }
        await waitFor("season availability detail visible") { page.isPageVisible }
        let play = try button("film.detail.play", in: page.view)
        let season = try button("film.detail.season", in: page.view)
        XCTAssertTrue(play.isEnabled)

        invoke(try menuAction("第二季", in: season))
        await model.waitForPendingWork()
        await deliverMainRunLoop()
        XCTAssertFalse(play.isEnabled, "A selected season without actual episodes cannot invent a target")
        XCTAssertTrue(episodeButtons(in: page).isEmpty)
        play.sendActions(for: .touchUpInside)
        await model.waitForPendingWork()
        XCTAssertEqual(presentations, 0)
        var commands = await server.calls().filter { $0.operation == "session.command" }
        XCTAssertEqual(commands.map(\.action), ["browse.season"])

        await server.reply(to: "browse.season", with: FilmUIKitPlaybackFixture.snapshot())
        invoke(try menuAction("第一季", in: season))
        await model.waitForPendingWork()
        await deliverMainRunLoop()
        XCTAssertTrue(play.isEnabled)
        play.sendActions(for: .touchUpInside)
        await model.waitForPendingWork()
        commands = await server.calls().filter { $0.operation == "session.command" }
        XCTAssertEqual(commands.map(\.action), ["browse.season", "browse.season", "play"])
        XCTAssertEqual(presentations, 1)
        XCTAssertTrue(media.inputs.isEmpty)
    }

    func testFavoriteButtonsStayInSyncAcrossDetailInstances() async throws {
        let library = try isolatedLibrary()
        let detail = FilmDetail(item: .init(sourceID: "fixture", id: "shared", title: "共同影片"))
        let first = FilmDetailViewController(detail: detail, library: library,
                                             images: artworkImages(), themeProvider: FilmUIKitTheme())
        let second = FilmDetailViewController(detail: detail, library: library,
                                              images: artworkImages(), themeProvider: FilmUIKitTheme())
        let firstWindow = mount(first)
        defer { release(first, window: firstWindow) }
        let secondWindow = mount(second)
        defer { release(second, window: secondWindow) }
        await waitFor("both detail instances active") { first.isPageActive && second.isPageActive }
        let firstFavorite = try button("film.detail.favorite", in: first.view)
        let secondFavorite = try button("film.detail.favorite", in: second.view)
        for favorite in [firstFavorite, secondFavorite] {
            XCTAssertEqual(favorite.configuration?.title, "收藏")
            XCTAssertFalse(favorite.accessibilityTraits.contains(.selected))
        }

        firstFavorite.sendActions(for: .touchUpInside)
        await waitFor("both favorite buttons selected") {
            [firstFavorite, secondFavorite].allSatisfy { $0.accessibilityTraits.contains(.selected) }
        }
        for favorite in [firstFavorite, secondFavorite] {
            XCTAssertEqual(favorite.configuration?.title, "已收藏")
            XCTAssertEqual(favorite.accessibilityLabel, "取消收藏 共同影片")
        }
        secondFavorite.sendActions(for: .touchUpInside)
        await waitFor("both favorite buttons cleared") {
            [firstFavorite, secondFavorite].allSatisfy { !$0.accessibilityTraits.contains(.selected) }
        }
        for favorite in [firstFavorite, secondFavorite] {
            XCTAssertEqual(favorite.configuration?.title, "收藏")
            XCTAssertEqual(favorite.accessibilityLabel, "收藏 共同影片")
        }
    }

    func testScrollingMountedDetailZoomsOutOnlyThePosterAndKeepsTheHeaderClearOverIt() async throws {
        let page = FilmDetailViewController(detail: suppliedDetail(), library: try isolatedLibrary(),
                                            images: artworkImages(), themeProvider: FilmUIKitTheme())
        let window = mount(page)
        defer { release(page, window: window) }
        await waitFor("scrolling detail visible") { page.isPageVisible }
        page.view.layoutIfNeeded()
        let scroll = try XCTUnwrap(identified("film.detail.scroll", in: page.view) as? UIScrollView)
        let poster = try heroPoster(in: page)
        let heroFrame = try XCTUnwrap(poster.superview)
        let gradient = try XCTUnwrap(heroFrame.layer.mask as? CAGradientLayer,
                                     "The fade belongs to the fixed poster frame, not the scaled image")
        let colors = try XCTUnwrap(gradient.colors as? [CGColor])
        XCTAssertTrue(scroll.delegate === page)
        XCTAssertGreaterThan(scroll.contentSize.height, scroll.bounds.height)
        XCTAssertTrue(heroFrame.clipsToBounds)
        XCTAssertEqual(gradient.startPoint, CGPoint(x: 0.5, y: 0))
        XCTAssertEqual(gradient.endPoint, CGPoint(x: 0.5, y: 1))
        XCTAssertEqual(try XCTUnwrap(colors.first).alpha, 1, accuracy: 0.001)
        XCTAssertEqual(try XCTUnwrap(colors.last).alpha, 0, accuracy: 0.001)
        let title = try label("film.detail.title", in: page.view)
        let play = try button("film.detail.play", in: page.view)
        func rect(_ view: UIView) -> CGRect { view.convert(view.bounds, to: page.view) }
        let restingFrameTop = rect(heroFrame).minY
        let restingTitleTop = rect(title).minY
        let restingZoom = poster.transform.a
        if UIAccessibility.isReduceMotionEnabled {
            XCTAssertEqual(restingZoom, 1)
        } else {
            XCTAssertGreaterThan(restingZoom, 1, "The poster starts enlarged inside its frame")
        }
        let headerEdge = page.view.safeAreaInsets.top + PageLayoutMetrics.navigation
        XCTAssertEqual(page.pageHeader.frame.maxY, headerEdge, accuracy: 0.5)
        let arrival = restingTitleTop - headerEdge
        XCTAssertGreaterThan(arrival, 0)
        XCTAssertEqual(page.pageHeader.background.alpha, 0, accuracy: 0.001)

        scroll.setContentOffset(CGPoint(x: 0, y: 10), animated: false)
        await waitFor("hero follows actual scroll") { rect(heroFrame).minY < restingFrameTop }
        var previousZoom = restingZoom
        for offset in stride(from: CGFloat(0), through: arrival + 20, by: 10) {
            scroll.setContentOffset(CGPoint(x: 0, y: offset), animated: false)
            let context = "offset \(offset)"
            XCTAssertEqual(rect(heroFrame).minY, restingFrameTop - offset, accuracy: 0.5, context)
            XCTAssertEqual(rect(title).minY, restingTitleTop - offset, accuracy: 0.5,
                           "The poster frame moves with the text, \(context)")
            XCTAssertLessThanOrEqual(rect(heroFrame).maxY, rect(title).minY + 0.5,
                                     "Scrolling never slides text over the poster, \(context)")
            XCTAssertLessThanOrEqual(rect(heroFrame).maxY, rect(play).minY, context)
            XCTAssertEqual(gradient.frame, heroFrame.bounds, context)
            let image = poster.convert(poster.bounds, to: heroFrame)
            XCTAssertTrue(image.insetBy(dx: -0.5, dy: -0.5).contains(heroFrame.bounds),
                          "The scaled poster must cover its frame without side gaps, \(context): \(image)")
            XCTAssertEqual(poster.transform.a, poster.transform.d, context)
            XCTAssertLessThanOrEqual(poster.transform.a, previousZoom, context)
            previousZoom = poster.transform.a
            let band = page.pageHeader.background.alpha
                * maskOpacity(gradient, at: headerEdge - rect(heroFrame).minY)
            XCTAssertLessThanOrEqual(band, 0.1, "The header must not draw a band over the poster, \(context)")
        }
        XCTAssertEqual(page.pageHeader.background.alpha, 1, accuracy: 0.001)
        XCTAssertEqual(page.pageHeader.title, "星海航线")
        XCTAssertEqual(poster.transform.a, 1, accuracy: 0.001, "The poster is back to its natural size on arrival")

        scroll.setContentOffset(.zero, animated: false)
        await waitFor("hero restored at the top") { page.pageHeader.background.alpha == 0 }
        XCTAssertEqual(rect(heroFrame).minY, restingFrameTop, accuracy: 0.001)
        XCTAssertEqual(poster.transform.a, restingZoom, accuracy: 0.001)
        XCTAssertEqual(poster.transform.d, restingZoom, accuracy: 0.001)
    }

    func testPosterChromeStaysLegibleUntilTheHeaderAppears() async throws {
        let stack = PageNavigationController(themeProvider: FilmUIKitTheme())
        let top = UIViewController()
        stack.setViewControllers([UIViewController(), top], animated: false)
        XCTAssertTrue(stack.childForStatusBarStyle === top, "The visible page, not the hidden bar, owns the status bar")

        let url = URL(string: "https://artwork.example.invalid/white.png")!
        let page = FilmDetailViewController(
            detail: .init(item: .init(sourceID: "fixture", id: "white", title: "白色海报", posterURL: url)),
            library: try isolatedLibrary(), images: artworkImages([url: png(.white)]),
            themeProvider: FilmUIKitTheme())
        let window = mount(page)
        defer { release(page, window: window) }
        let poster = try heroPoster(in: page)
        await waitFor("poster chrome applied") { page.isPageVisible && poster.image != nil }
        page.view.layoutIfNeeded()
        let scroll = try XCTUnwrap(identified("film.detail.scroll", in: page.view) as? UIScrollView)
        let scrim = try XCTUnwrap(identified("film.detail.scrim", in: page.view))
        let back = try button("page.header.back", in: page.view)
        XCTAssertEqual(page.preferredStatusBarStyle, .lightContent)
        XCTAssertEqual(back.tintColor, .white)
        XCTAssertEqual(scrim.alpha, 1, accuracy: 0.001)
        XCTAssertFalse(scrim.isUserInteractionEnabled)
        XCTAssertGreaterThanOrEqual(scrim.frame.maxY, page.pageHeader.frame.maxY,
                                    "The scrim must cover the status bar and the back button")

        scroll.setContentOffset(CGPoint(x: 0, y: scroll.contentSize.height), animated: false)
        await waitFor("header appeared") { page.pageHeader.background.alpha == 1 }
        XCTAssertEqual(page.preferredStatusBarStyle, .default)
        XCTAssertEqual(scrim.alpha, 0, accuracy: 0.001)
        XCTAssertNotEqual(back.tintColor, .white)

        scroll.setContentOffset(.zero, animated: false)
        await waitFor("poster chrome restored") { page.preferredStatusBarStyle == .lightContent }
        XCTAssertEqual(back.tintColor, .white)
        XCTAssertEqual(scrim.alpha, 1, accuracy: 0.001)
    }

    func testDetailHeroPrefersTheLargePosterAndFallsBackToTheCardPoster() async throws {
        let small = URL(string: "https://artwork.example.invalid/card.png")!
        let large = URL(string: "https://artwork.example.invalid/large.png")!
        let missing = URL(string: "https://artwork.example.invalid/missing.png")!
        let smallData = png(.red), largeData = png(.blue)
        let images = ArtworkImages(maximumBytes: 1_024 * 1_024) { url in
            if url == small { return smallData }
            if url == large { return largeData }
            throw URLError(.resourceUnavailable)
        }
        let (smallImage, _) = try await images.read(small)
        let (largeImage, _) = try await images.read(large)
        let sharp = FilmDetailViewController(
            detail: .init(item: .init(sourceID: "fixture", id: "sharp", title: "清晰海报",
                                      posterURL: small, largePosterURL: large)),
            library: try isolatedLibrary(), images: images, themeProvider: FilmUIKitTheme())
        let fallback = FilmDetailViewController(
            detail: .init(item: .init(sourceID: "fixture", id: "fallback", title: "回退海报",
                                      posterURL: small, largePosterURL: missing)),
            library: try isolatedLibrary(), images: images, themeProvider: FilmUIKitTheme())
        let sharpWindow = mount(sharp)
        defer { release(sharp, window: sharpWindow) }
        let fallbackWindow = mount(fallback)
        defer { release(fallback, window: fallbackWindow) }
        let sharpPoster = try heroPoster(in: sharp)
        let fallbackPoster = try heroPoster(in: fallback)
        await waitFor("both heroes rendered") { sharpPoster.image != nil && fallbackPoster.image != nil }
        XCTAssertTrue(sharpPoster.image === largeImage, "Details use the sharper poster when the source has one")
        XCTAssertTrue(fallbackPoster.image === smallImage, "A failed large poster falls back to the card poster")
    }

    func testPosterPressIsImmediateAndReleaseAndReuseRestoreItsVisuals() async throws {
        let url = URL(string: "https://artwork.example.invalid/pressed.png")!
        let images = artworkImages([url: png(.green)])
        let (image, _) = try await images.read(url)
        let cell = FilmPosterCell(frame: CGRect(x: 0, y: 0, width: 80, height: 174))
        cell.configure(item: .init(sourceID: "fixture", id: "first", title: "第一张", posterURL: url, score: "9.1"),
                       images: images, theme: PageTheme())
        cell.layoutIfNeeded()
        let artwork = try XCTUnwrap(descendants(cell).compactMap { $0 as? ArtworkView }.first)
        XCTAssertTrue(artwork.image === image)
        let originalBounds = cell.bounds
        cell.isHighlighted = true
        XCTAssertLessThan(cell.contentView.alpha, 1)
        if UIAccessibility.isReduceMotionEnabled {
            XCTAssertEqual(cell.contentView.transform, .identity)
        } else {
            XCTAssertGreaterThan(cell.contentView.transform.a, 0.9)
            XCTAssertLessThan(cell.contentView.transform.a, 1)
            XCTAssertEqual(cell.contentView.transform.a, cell.contentView.transform.d)
        }
        XCTAssertEqual(cell.bounds, originalBounds)
        cell.isHighlighted = false
        XCTAssertEqual(cell.contentView.transform, .identity)
        XCTAssertEqual(cell.contentView.alpha, 1, accuracy: 0.001)
        cell.isHighlighted = true
        cell.prepareForReuse()
        XCTAssertFalse(cell.isHighlighted)
        XCTAssertEqual(cell.contentView.transform, .identity)
        XCTAssertEqual(cell.contentView.alpha, 1, accuracy: 0.001)
        XCTAssertNil(artwork.image)
        cell.configure(item: .init(sourceID: "other", id: "second", title: "第二张"),
                       images: images, theme: PageTheme())
        cell.layoutIfNeeded()
        XCTAssertEqual(cell.accessibilityIdentifier, "film.poster.other.second")
        XCTAssertEqual(cell.accessibilityLabel, "第二张")
        XCTAssertFalse(descendants(cell).compactMap { $0 as? UILabel }.contains { $0.text == "★ 9.1" })
        XCTAssertEqual(cell.contentView.transform, .identity)
        XCTAssertEqual(cell.contentView.alpha, 1, accuracy: 0.001)
    }

    func testPressedPosterShrinksInPlaceWithoutReflowingItsTitle() async throws {
        let item = FilmItem(sourceID: "fixture", id: "pressed", title: "死神 千年血战篇 -祸进谭-")
        let source = FilmUIKitSource(id: "fixture", title: "测试源", sections: [
            .init(id: "movies", title: "热门电影", items: [item]),
        ])
        let model = FilmHomeModel(sources: [source])
        let request = try XCTUnwrap(model.loadIfNeeded())
        await request.value
        let page = FilmHomeViewController(model: model, images: artworkImages(), themeProvider: FilmUIKitTheme())
        let window = mount(page)
        defer {
            release(page, window: window)
            model.close()
        }
        let collection = try homeCollection(in: page)
        await waitFor("poster cell") {
            collection.layoutIfNeeded()
            return collection.cellForItem(at: IndexPath(item: 0, section: 0)) is FilmPosterCell
        }
        let cell = try XCTUnwrap(collection.cellForItem(at: IndexPath(item: 0, section: 0)) as? FilmPosterCell)
        let title = try XCTUnwrap(descendants(cell).compactMap { $0 as? UILabel }.first { $0.text == item.title })
        let artwork = try XCTUnwrap(descendants(cell).compactMap { $0 as? ArtworkView }.first)
        let restingContent = cell.contentView.bounds
        let restingTitle = title.frame
        let restingArtwork = artwork.convert(artwork.bounds, to: cell)

        cell.isHighlighted = true
        await deliverMainRunLoop()
        cell.setNeedsLayout()
        cell.layoutIfNeeded()
        XCTAssertEqual(cell.contentView.bounds, restingContent, "Pressing must not resize the content")
        XCTAssertEqual(title.frame, restingTitle, "The title keeps its width, so its lines cannot re-wrap")
        let pressedArtwork = artwork.convert(artwork.bounds, to: cell)
        let scale: CGFloat = UIAccessibility.isReduceMotionEnabled ? 1 : 0.97
        XCTAssertEqual(pressedArtwork.width, restingArtwork.width * scale, accuracy: 0.01,
                       "The poster must visibly shrink while pressed")
        XCTAssertEqual(pressedArtwork.midX, restingArtwork.midX, accuracy: 0.01)

        cell.isHighlighted = false
        cell.setNeedsLayout()
        cell.layoutIfNeeded()
        XCTAssertEqual(cell.contentView.transform, .identity)
        XCTAssertEqual(cell.contentView.bounds, restingContent)
        XCTAssertEqual(title.frame, restingTitle)
    }

    private func suppliedDetail() -> FilmDetail {
        FilmDetail(
            item: .init(sourceID: "fixture", id: "voyage", title: "星海", score: "8.7"),
            title: "星海航线",
            summary: "两位领航员穿越星海，寻找失落的归途。",
            releaseDate: "2026年11月1日",
            seasonCount: 2,
            episodeCount: 5,
            seasons: [
                .init(id: "first", title: "第一季", number: 1, episodes: [
                    .init(id: "first-unavailable", title: "初见", number: 1, summary: "他们在港口相遇。"),
                    .init(id: "first-playable", title: "夜航", number: 2, summary: "飞船驶入漫长的夜色。",
                          playbackURL: URL(string: "https://media.example.invalid/first/2.m3u8")),
                ]),
                .init(id: "second", title: "第二季", number: 2, episodes: [
                    .init(id: "second-one", title: "归航", number: 1, summary: "新的航程开始。"),
                    .init(id: "second-two", title: "信号", number: 2, summary: "远方传来陌生的信号。"),
                    .init(id: "second-playable", title: "重逢", number: 3, summary: "熟悉的声音再次响起。",
                          playbackURL: URL(string: "https://media.example.invalid/second/3.m3u8")),
                ]),
            ],
            cast: [
                .init(id: "director", name: "林舟", role: "导演"),
                .init(id: "actor", name: "周星", role: "领航员"),
            ],
            similarItems: [
                .init(sourceID: "fixture", id: "letters", title: "远方来信"),
                .init(sourceID: "other", id: "stars", title: "群星之间"),
            ]
        )
    }

    private func playbackModel(_ server: FilmUIKitPlaybackServer, _ media: FilmUIKitPlaybackMedia) -> FilmPlaybackModel {
        let model = FilmPlaybackModel(transport: server.transport, media: media, openExternalURL: { _ in
            XCTFail("Unexpected external-player launch"); return false
        }, request: { FilmUIKitPlaybackFixture.request() })
        addTeardownBlock { await model.close() }
        return model
    }

    private func artworkImages(_ data: [URL: Data] = [:]) -> ArtworkImages {
        ArtworkImages(maximumBytes: 1_024 * 1_024) { url in
            guard let bytes = data[url] else {
                XCTFail("Unregistered fixture artwork: \(url)")
                throw URLError(.resourceUnavailable)
            }
            return bytes
        }
    }

    private func png(_ color: UIColor) -> Data {
        UIGraphicsImageRenderer(size: CGSize(width: 4, height: 6)).pngData { context in
            color.setFill()
            context.fill(CGRect(x: 0, y: 0, width: 4, height: 6))
        }
    }

    private func isolatedLibrary() throws -> FilmLibrary {
        let suite = "FilmUIKitTests." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        addTeardownBlock { defaults.removePersistentDomain(forName: suite) }
        return FilmLibrary(defaults: defaults)
    }

    private func homeCollection(in page: FilmHomeViewController) throws -> UICollectionView {
        try XCTUnwrap(identified("film.home.sections", in: page.view) as? UICollectionView)
    }

    private func heroPoster(in page: FilmDetailViewController) throws -> ArtworkView {
        try XCTUnwrap(descendants(page.view).compactMap { $0 as? ArtworkView }.first {
            $0.superview?.layer.mask is CAGradientLayer
        })
    }

    /// Opacity a vertical gradient mask applies at `y` in the coordinates of the layer it masks.
    private func maskOpacity(_ mask: CAGradientLayer, at y: CGFloat) -> CGFloat {
        guard let colors = mask.colors as? [CGColor], let locations = mask.locations,
              colors.count == locations.count, !colors.isEmpty, mask.frame.height > 0 else {
            XCTFail("The poster fade must be a vertical gradient with explicit stops")
            return 1
        }
        let position = (y - mask.frame.minY) / mask.frame.height
        if position < 0 || position > 1 { return 0 }
        let stops = locations.map { CGFloat($0.doubleValue) }
        let alphas = colors.map { $0.alpha }
        guard let upper = stops.firstIndex(where: { $0 >= position }) else { return alphas[alphas.count - 1] }
        guard upper > 0 else { return alphas[0] }
        let span = stops[upper] - stops[upper - 1]
        let fraction = span > 0 ? (position - stops[upper - 1]) / span : 1
        return alphas[upper - 1] + (alphas[upper] - alphas[upper - 1]) * fraction
    }

    private func episodeButtons(in page: FilmDetailViewController) -> [FilmEpisodeButton] {
        descendants(page.view).compactMap { $0 as? FilmEpisodeButton }
    }

    private func button(_ id: String, in root: UIView) throws -> UIButton {
        try XCTUnwrap(identified(id, in: root) as? UIButton, id)
    }

    private func label(_ id: String, in root: UIView) throws -> UILabel {
        try XCTUnwrap(identified(id, in: root) as? UILabel, id)
    }

    @discardableResult
    private func assertText(_ text: String, in root: UIView,
                            file: StaticString = #filePath, line: UInt = #line) throws -> UILabel {
        let label = try XCTUnwrap(descendants(root).compactMap { $0 as? UILabel }.first { $0.text == text },
                                 text, file: file, line: line)
        var ancestor: UIView? = label
        while let current = ancestor {
            XCTAssertFalse(current.isHidden, "\(text) must be rendered", file: file, line: line)
            if current === root { break }
            ancestor = current.superview
        }
        XCTAssertGreaterThan(label.bounds.width, 0, text, file: file, line: line)
        XCTAssertGreaterThan(label.bounds.height, 0, text, file: file, line: line)
        return label
    }

    private func menuAction(_ title: String, in button: UIButton) throws -> UIAction {
        try XCTUnwrap(button.menu?.children.compactMap { $0 as? UIAction }.first { $0.title == title }, title)
    }

    private func invoke(_ action: UIAction) {
        let sender = UIButton(type: .system)
        sender.addAction(action, for: .touchUpInside)
        sender.sendActions(for: .touchUpInside)
    }

    private func identified(_ id: String, in root: UIView) -> UIView? {
        ([root] + descendants(root)).first { $0.accessibilityIdentifier == id }
    }

    private func descendants(_ view: UIView) -> [UIView] {
        view.subviews.flatMap { [$0] + descendants($0) }
    }

    private func visibleFrame(of view: UIView, in root: UIView) -> CGRect {
        var frame = view.convert(view.bounds, to: root)
        var ancestor = view.superview
        while let current = ancestor {
            if current.clipsToBounds || current === root {
                frame = frame.intersection(current.convert(current.bounds, to: root))
            }
            if current === root { return frame }
            ancestor = current.superview
        }
        return .null
    }

    private func waitFor(_ description: String, file: StaticString = #filePath, line: UInt = #line,
                         _ predicate: @escaping @MainActor () -> Bool) async {
        let ready = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated { predicate() } },
                                evaluatedWith: nil)
        let result = await XCTWaiter.fulfillment(of: [ready], timeout: 4)
        XCTAssertEqual(result, .completed, description, file: file, line: line)
    }

    private func deliverMainRunLoop() async {
        await withCheckedContinuation { continuation in
            RunLoop.main.perform { continuation.resume() }
        }
    }

    // XCTest requires the main thread, but its nested run loop must not hold
    // this async test's actor job while delivering touches to the runner itself.
    private func performRunnerAction(_ action: @escaping @MainActor (XCUIApplication) -> Void) async throws {
        let identifier = try XCTUnwrap(Bundle.main.bundleIdentifier)
        await withCheckedContinuation { continuation in
            RunLoop.main.perform {
                MainActor.assumeIsolated { action(XCUIApplication(bundleIdentifier: identifier)) }
                continuation.resume()
            }
        }
    }

    private func mount(_ controller: UIViewController) -> UIWindow {
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        let window = scene.map { UIWindow(windowScene: $0) }
            ?? UIWindow(frame: CGRect(x: 0, y: 0, width: 390, height: 844))
        controller.loadViewIfNeeded()
        window.rootViewController = controller
        window.makeKeyAndVisible()
        window.layoutIfNeeded()
        controller.view.layoutIfNeeded()
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        return window
    }

    private func release(_ controller: UIViewController, window: UIWindow) {
        PageViewController.invalidatePages(in: controller)
        window.isHidden = true
        window.rootViewController = nil
    }
}

private actor FilmUIKitSource: FilmSource {
    nonisolated let descriptor: FilmSourceDescriptor
    private let sections: [FilmHomeSection]
    private(set) var loadCount = 0

    init(id: String, title: String, sections: [FilmHomeSection]) {
        descriptor = .init(id: id, title: title)
        self.sections = sections
    }

    func loadHome(refresh: Bool) async throws -> [FilmHomeSection] {
        loadCount += 1
        return sections
    }
}

private struct FilmCategoryUIKitSource: FilmCategorySource {
    let descriptor = FilmSourceDescriptor(id: "fixture", title: "测试")
    let categories = FilmCategory.allCases
    func loadHome(refresh: Bool) async throws -> [FilmHomeSection] {
        [.init(id: "movie", title: "热门电影", items: try await loadCategory(.movie, filters: [:], offset: 0, refresh: refresh).items)]
    }
    func loadCategory(_ category: FilmCategory, filters: [String: String], offset: Int, refresh: Bool) async throws -> FilmCategoryPage {
        .init(items: (0..<12).map { FilmItem(sourceID: "fixture", id: "\($0)", title: "影片 \($0)") }, nextOffset: nil)
    }
}

@MainActor
private final class FilmFailingUIKitSource: FilmCategorySource {
    nonisolated let descriptor = FilmSourceDescriptor(id: "failure", title: "失败源")
    nonisolated let categories = FilmCategory.allCases
    private(set) var homeReads = 0
    private(set) var categoryReads = 0

    func loadHome(refresh: Bool) async throws -> [FilmHomeSection] {
        homeReads += 1
        throw URLError(.notConnectedToInternet)
    }

    func loadCategory(_ category: FilmCategory, filters: [String: String], offset: Int, refresh: Bool) async throws -> FilmCategoryPage {
        categoryReads += 1
        throw URLError(.notConnectedToInternet)
    }
}

@MainActor
private final class FilmPaginationFailingUIKitSource: FilmCategorySource {
    nonisolated let descriptor = FilmSourceDescriptor(id: "pagination-failure", title: "分页失败源")
    nonisolated let categories: [FilmCategory] = [.anime]
    private(set) var offsets: [Int] = []

    func loadHome(refresh: Bool) async throws -> [FilmHomeSection] { [] }

    func loadCategory(_ category: FilmCategory, filters: [String: String], offset: Int, refresh: Bool) async throws -> FilmCategoryPage {
        offsets.append(offset)
        guard offset == 0 else { throw URLError(.notConnectedToInternet) }
        return .init(items: (0..<3).map {
            FilmItem(sourceID: descriptor.id, id: "\($0)", title: "影片 \($0)")
        }, nextOffset: 24)
    }
}

@MainActor
private final class FilmEmptyCategoryUIKitSource: FilmCategorySource {
    nonisolated let descriptor = FilmSourceDescriptor(id: "empty-category", title: "分类源")
    nonisolated let categories: [FilmCategory] = [.movie]
    private(set) var homeReads = 0

    func loadHome(refresh: Bool) async throws -> [FilmHomeSection] {
        homeReads += 1
        return [.init(id: "movie", title: "分类源栏目", items: [])]
    }

    func loadCategory(_ category: FilmCategory, filters: [String: String], offset: Int, refresh: Bool) async throws -> FilmCategoryPage {
        .init(items: [.init(sourceID: descriptor.id, id: "selected", title: "分类源影片")], nextOffset: nil)
    }
}

@MainActor
private final class FilmUIKitTheme: PageThemeProviding {
    let pagePalette = PagePalette.preset(.lilac)
    let animatesPageColorway = false
    var pageThemeChanges: AnyPublisher<Void, Never> {
        Empty(completeImmediately: false).eraseToAnyPublisher()
    }
}
#endif
