import XCTest
import UIKit
import MeowUI
import MeowRuntime
import MeowMusicFeature
@testable import MeowFilmFeature
@testable import Meowfilm

/// TEST_HOST + the actual App assembly, NodeMobile, film.data and Artwork owner.
/// HTTP provider replies, the Cat directory and PNG bytes are local fixtures,
/// not evidence that an external TMDB/Douban endpoint is available.
@MainActor
final class MeowfilmFilmMetadataHostTests: XCTestCase {
    func testActualTMDBHandoffSeasonPreviewCastArtworkAndSimilarNavigation() async throws {
        let fixture = try await makeFixture()
        let installed = try await fixture.installDirectory()
        let search = try XCTUnwrap(fixture.assembly.filmSearch)
        let session = try search.session(for: fixture.values.title)
        fixture.searchSession = session
        await session.drain()
        XCTAssertEqual(session.snapshot.metadataState, .completed)
        let card = try XCTUnwrap(session.snapshot.cards.first {
            $0.tmdbType == "tv" && $0.tmdbID == fixture.values.tvID
        })
        let context = session.context(for: card)
        XCTAssertNil(context.snapshot.metadataSearch?.catField("results")?.catArray?.first?.catField("credits"),
                     "A search card is partial; it cannot substitute for the appended full detail")
        XCTAssertEqual(context.metadataDetail?.catField("overview"), .string("完整资料简介"))
        XCTAssertEqual(context.metadataDetail?.catField("credits")?.catField("cast")?.catArray?.count, 1)
        XCTAssertEqual(fixture.reads("/tmdb-api/3/tv/\(fixture.values.tvID)"), 1)
        XCTAssertEqual(fixture.seasonReads, [], "Search enrichment must not eagerly load seasons")

        let (coordinator, root) = try await fixture.coordinator()
        let main = try XCTUnwrap(root.navigation.topViewController as? MeowfilmPrimaryShellViewController)
        try await waitFor { main.isPageVisible && root.transitionCoordinator == nil }
        try await dismissKnownMusicNotice(root)
        try await waitFor { main.canPresentPageAlert }
        try await navigate(root.navigation, to: { $0 is FilmDetailViewController }) {
            coordinator.showFilmSearchResult(context)
        }
        let page = try XCTUnwrap(root.navigation.topViewController as? FilmDetailViewController)
        let model = try XCTUnwrap(page.filmPlaybackModel)
        fixture.models.append(model)
        try await waitFor {
            model.snapshot?.stage == "idle" && model.snapshot?.metadataError == nil &&
                model.snapshot?.metadataSources.first(where: { $0.provider == .tmdb })?.seasons != nil && !model.opening &&
                page.detail.cast.count == 2 && self.episodeIDs(page).count == 2
        }
        XCTAssertTrue(page.searchContext?.session === session)
        XCTAssertEqual(model.snapshot?.entrySurface, "search")
        XCTAssertEqual(page.detail.summary, "完整资料简介")
        XCTAssertEqual(page.detail.cast.map(\.name), ["合成导演", "合成演员"])
        XCTAssertEqual(page.detail.similarItems.map(\.id), [String(fixture.values.similarID)])
        XCTAssertEqual(page.detail.item.posterURL?.absoluteString,
                       "/v1/tmdb/images/original/\(fixture.values.prefix)-poster.png")
        XCTAssertEqual(page.detail.logoURL?.absoluteString,
                       "/v1/tmdb/images/original/\(fixture.values.prefix)-logo.png")
        XCTAssertEqual(fixture.reads("/tmdb-api/3/tv/\(fixture.values.tvID)"), 1,
                       "The real search-to-session handoff must reuse the same complete data-owner entry")
        XCTAssertEqual(fixture.reads("/tmdb-api/3/search/multi"), 1)
        XCTAssertEqual(fixture.seasonReads, [1])
        XCTAssertEqual(episodeIDs(page), ["film.episode.season-1-episode-1", "film.episode.season-1-episode-2"])
        let cast = descendants(page.view).compactMap { $0 as? FilmCastView }
        XCTAssertEqual(cast.map(\.accessibilityLabel), ["合成导演，导演", "合成演员，主角"])
        let portraits = cast.flatMap { descendants($0).compactMap { $0 as? ArtworkView } }
        let hero = try hero(page)
        try await waitFor {
            hero.image != nil && portraits.count == 2 && portraits.allSatisfy { $0.image != nil } &&
                page.detail.logoURL.flatMap { fixture.assembly.filmImages.cached($0) } != nil
        }
        for suffix in ["backdrop", "logo", "director", "actor"] {
            XCTAssertEqual(fixture.reads("/tmdb-images/t/p/original/\(fixture.values.prefix)-\(suffix).png"), 1)
        }
        // This is the actual source menu's action, not a hand-written model browse.
        let source = try button("film.detail.source", page)
        let directoryAction = try menuAction("Metadata directory · \(fixture.values.title)", in: source)
        invoke(directoryAction)
        try await waitFor {
            model.snapshot?.browsing.sourceID != nil && model.snapshot?.browsing.loading == false &&
                model.snapshot?.browsing.originalFiles.count == 4
        }
        XCTAssertEqual(model.snapshot?.browsing.originalFiles.map(\.name),
                       ["第1集.1080p", "第2集.1080p", "第3集.1080p", "第99集.1080p"])
        invoke(try menuAction("TMDB · \(fixture.values.title)", in: source))
        try await waitFor {
            model.snapshot?.browsing.sourceID == nil && model.snapshot?.browsing.loading == false &&
                self.episodeIDs(page).count == 2
        }
        let more = try button("film.detail.more", page)
        XCTAssertEqual(try menuAction("点映", in: more).state, .off)
        invoke(try menuAction("点映", in: more))
        try await waitFor { model.snapshot?.previewEpisodes == true && self.episodeIDs(page).count == 3 }
        XCTAssertEqual(try menuAction("点映", in: more).state, .on)
        XCTAssertEqual(episodeIDs(page), [
            "film.episode.season-1-episode-1", "film.episode.season-1-episode-2", "film.episode.season-1-episode-3",
        ], "Preview reveals only the real future episode, never the directory's maximum file number")
        XCTAssertEqual(model.snapshot?.episodes.last?.raw["air_date"], .string("2999-01-01"),
                       "Returning to the same metadata source must retain its already-read real future episode fields")
        invoke(try menuAction("点映", in: more))
        try await waitFor { model.snapshot?.previewEpisodes == false && self.episodeIDs(page).count == 2 }

        let season = try button("film.detail.season", page)
        XCTAssertEqual(season.menu?.children.compactMap { ($0 as? UIAction)?.title }, ["特别篇", "第一季", "第二季"])
        invoke(try menuAction("特别篇", in: season))
        try await waitFor {
            model.snapshot?.browsing.season == 0 && model.snapshot?.browsing.loading == false &&
                self.episodeIDs(page) == ["film.episode.season-0-episode-0"]
        }
        XCTAssertEqual(model.snapshot?.episodes.first?.position, .init(season: 0, episode: 0))
        XCTAssertEqual(fixture.seasonReads, [1, 0], "Selecting specials must not fetch all other seasons")
        invoke(try menuAction("第二季", in: season))
        try await waitFor {
            model.snapshot?.browsing.season == 2 && model.snapshot?.browsing.loading == false &&
                self.episodeIDs(page).isEmpty
        }
        XCTAssertNil(model.snapshot?.browsing.error)
        XCTAssertTrue(model.snapshot?.episodes.isEmpty == true)
        XCTAssertEqual(fixture.seasonReads, [1, 0, 2])
        invoke(try menuAction("点映", in: more))
        try await waitFor { model.snapshot?.previewEpisodes == true }
        XCTAssertTrue(episodeIDs(page).isEmpty,
                      "An explicitly empty season detail cannot fabricate episodes from a summary count or file 99")
        invoke(try menuAction("点映", in: more))
        try await waitFor { model.snapshot?.previewEpisodes == false }
        invoke(try menuAction("第一季", in: season))
        try await waitFor {
            model.snapshot?.browsing.season == 1 && model.snapshot?.browsing.loading == false &&
                self.episodeIDs(page).count == 2
        }
        XCTAssertEqual(fixture.seasonReads, [1, 0, 2], "Re-selecting a real season uses the original data owner")
        let calls = try await fixture.directoryCalls(installed)
        XCTAssertEqual(calls, ["search", "detail"],
                       "Handoff, menu browsing and preview do not re-search, resolve an address or start media")
        XCTAssertNil(model.snapshot?.attempt); XCTAssertNil(model.snapshot?.current)
        XCTAssertNil(model.mediaState.attemptID)

        let similar = try button("film.similar.tmdb.\(fixture.values.similarID)", page)
        XCTAssertEqual(similar.accessibilityLabel, fixture.values.title, "The same title must not determine navigation identity")
        try await navigate(root.navigation, to: { $0 is FilmDetailViewController && $0 !== page }) {
            similar.sendActions(for: .touchUpInside)
        }
        let related = try XCTUnwrap(root.navigation.topViewController as? FilmDetailViewController)
        let nextModel = try XCTUnwrap(related.filmPlaybackModel)
        fixture.models.append(nextModel)
        try await waitFor {
            nextModel.snapshot?.stage == "idle" && !nextModel.opening && related.detail.summary == "同标题另一作品"
        }
        XCTAssertEqual(nextModel.snapshot?.target.metadataProvider, .tmdb)
        XCTAssertEqual(nextModel.snapshot?.target.value["metadataId"], .string(String(fixture.values.similarID)))
        XCTAssertEqual(nextModel.snapshot?.target.id, "tmdb:tv:\(fixture.values.similarID)")
        XCTAssertNil(related.searchContext, "Exact related-item navigation must not manufacture a title-search context")
        XCTAssertEqual(fixture.reads("/tmdb-api/3/tv/\(fixture.values.similarID)"), 1)
        XCTAssertEqual(fixture.reads("/tmdb-api/3/search/multi"), 1, "Clicking a similar item must not search its title")
        XCTAssertEqual(fixture.upstream.requests.filter { $0.path.hasPrefix("/tmdb-api/3/search/") }.count, 1,
                       "Exact TMDB navigation must not add movie/tv searches under another title-search route")
        let afterRelated = try await fixture.directoryCalls(installed)
        XCTAssertEqual(afterRelated, calls)
        XCTAssertTrue(model.isReleased, "The real Coordinator drains the previous page's session")
        assertUpstreamBoundary(fixture)
    }

    func testHomeTVSeedPreservesRawMetadataThroughCoordinatorAndReentry() async throws {
        let fixture = try await makeFixture()
        let installed = try await fixture.installDirectory(title: fixture.values.doubanTitle)
        let source = DoubanFilmSource { fixture.client }
        let sections = try await source.loadHome()
        let item = try XCTUnwrap(sections.first { $0.id == "tv" }?.items.first)
        XCTAssertEqual(item.kind, .tv)
        let decoded = try JSONDecoder().decode(FilmItem.self, from: JSONEncoder().encode(item))
        XCTAssertEqual(decoded, item)
        let seed = try RuntimeJSON.decode(XCTUnwrap(decoded.metadataSeed))
        XCTAssertEqual(seed.catField("year"), .string("2020"))
        XCTAssertEqual(seed.catField("card_subtitle"), .string("2031 / 不作为年份来源"))
        XCTAssertEqual(seed.catField("fixture")?.catField("origin"), .string("home-row"))
        let (page, _) = try await openHomeItem(fixture, section: 1)
        let model = try XCTUnwrap(page.filmPlaybackModel)
        try await waitFor { model.snapshot?.entryResolution == "matched" && model.snapshot?.stage == "idle" && !model.opening }
        XCTAssertEqual(model.snapshot?.entrySurface, "home")
        XCTAssertEqual(model.snapshot?.entryTarget?.metadataProvider, .douban)
        XCTAssertEqual(model.snapshot?.entryTarget?.kind, .tv)
        XCTAssertEqual(model.snapshot?.entryTarget?.title, item.title)
        XCTAssertEqual(model.snapshot?.entryTarget?.value["metadataId"], .string(item.id))
        XCTAssertEqual(model.snapshot?.target.metadataProvider, .tmdb)
        XCTAssertEqual(model.snapshot?.target.value["metadataId"], .number(Double(fixture.values.tvID)),
                       "The raw seed year selects 2020, not the first same-title 2019 TMDB result")
        XCTAssertEqual(model.snapshot?.target.value["year"], .number(2020))
        XCTAssertEqual(page.detail.item.sourceID, "tmdb")
        XCTAssertEqual(page.detail.summary, "完整资料简介")
        XCTAssertTrue(try button("film.detail.search", page).isHidden)
        XCTAssertFalse(model.snapshot?.metadataSources.contains { $0.provider == .douban } == true)
        let before = try await fixture.directoryCalls(installed)
        XCTAssertEqual(before, [])
        let oldSession = try XCTUnwrap(model.sessionID)
        await model.close()
        await model.open()?.value
        try await waitFor {
            model.sessionID != oldSession && model.snapshot?.entryResolution == "matched" && model.snapshot?.stage == "idle"
        }
        XCTAssertEqual(model.snapshot?.entrySurface, "home")
        XCTAssertEqual(model.snapshot?.entryTarget?.metadataProvider, .douban)
        XCTAssertEqual(model.snapshot?.entryTarget?.value["metadataId"], .string(item.id))
        XCTAssertEqual(model.snapshot?.target.value["metadataId"], .number(Double(fixture.values.tvID)))
        XCTAssertEqual(fixture.reads("/tmdb-api/3/search/multi"), 1, "Reentry reuses Node's seed-search cache")
        XCTAssertEqual(fixture.reads("/tmdb-api/3/tv/\(fixture.values.similarID)"), 0)
        XCTAssertEqual(fixture.reads("/douban-api/rexxar/api/v2/tv/\(fixture.values.doubanID)"), 0,
                       "A home seed must not initialize an independent Douban playback source")
        let after = try await fixture.directoryCalls(installed)
        XCTAssertEqual(after, before)
        XCTAssertNil(model.snapshot?.attempt); XCTAssertNil(model.snapshot?.current)
        let artwork = try hero(page)
        try await waitFor { artwork.image != nil }
        assertUpstreamBoundary(fixture)
    }

    func testHomeDoubanSeedSearchesTMDBThenRequiresExplicitSiteSearchOnMiss() async throws {
        let fixture = try await makeFixture()
        fixture.responses.forceNoTMDBMatch = true
        let installed = try await fixture.installDirectory(title: fixture.values.doubanTitle)
        let (page, _) = try await openHomeItem(fixture, section: 0)
        let model = try XCTUnwrap(page.filmPlaybackModel)
        try await waitFor { model.snapshot?.entryResolution == "unmatched" && model.snapshot?.stage == "idle" && !model.opening }
        let searchButton = try button("film.detail.search", page)
        try await waitFor { !searchButton.isHidden }
        XCTAssertEqual(page.detail.item.sourceID, "douban")
        XCTAssertEqual(page.detail.item.id, String(fixture.values.doubanID))
        XCTAssertEqual(model.snapshot?.entrySurface, "home")
        XCTAssertEqual(model.snapshot?.entryTarget?.metadataProvider, .douban)
        XCTAssertEqual(model.snapshot?.entryTarget?.kind, .movie)
        XCTAssertEqual(model.snapshot?.entryTarget?.title, fixture.values.doubanTitle)
        XCTAssertEqual(model.snapshot?.target.kind, .group)
        XCTAssertEqual(model.snapshot?.target.value["members"], .array([]))
        XCTAssertNil(model.snapshot?.target.metadataProvider)
        XCTAssertTrue(model.snapshot?.metadataSources.isEmpty == true)
        XCTAssertTrue(model.snapshot?.sources.isEmpty == true,
                      "A TMDB miss does not automatically search every site")
        let poster = try XCTUnwrap(page.detail.item.posterURL)
        XCTAssertEqual(poster.absoluteString, "/v1/douban/images/posters/\(fixture.values.prefix).png")
        XCTAssertNil(poster.scheme); XCTAssertNil(poster.baseURL)
        let artwork = try hero(page)
        try await waitFor { artwork.image != nil }
        let entryTarget = try XCTUnwrap(model.snapshot?.entryTarget)
        XCTAssertEqual(fixture.reads("/tmdb-api/3/search/multi"), 1)
        let initialCalls = try await fixture.directoryCalls(installed)
        XCTAssertEqual(initialCalls, [], "No site search is admitted before the explicit action")
        let oldSession = try XCTUnwrap(model.sessionID)
        await model.close()
        XCTAssertTrue(model.isReleased)
        await model.open()?.value
        try await waitFor {
            model.sessionID != oldSession && model.snapshot?.entryResolution == "unmatched" &&
                model.snapshot?.stage == "idle" && !model.opening
        }
        XCTAssertNil(model.actionError, "A seed-generated wait group must not be sent back as an invalid empty resume")
        XCTAssertNil(model.snapshot?.error); XCTAssertNil(model.snapshot?.browsing.error)
        XCTAssertEqual(model.snapshot?.entryTarget, entryTarget)
        XCTAssertEqual(model.snapshot?.entrySurface, "home")
        XCTAssertEqual(model.snapshot?.target.value["members"], .array([]),
                       "A legal no-match group must not become a revoked or withdrawn selection on reentry")
        XCTAssertTrue(model.snapshot?.sources.isEmpty == true)
        XCTAssertFalse(searchButton.isHidden)
        XCTAssertEqual(fixture.reads("/tmdb-api/3/search/multi"), 1,
                       "Reentry resolves the original seed through Node's cached no-match search")
        let before = try await fixture.directoryCalls(installed)
        XCTAssertEqual(before, [])
        searchButton.sendActions(for: .touchUpInside)
        try await waitFor { model.snapshot?.sources.contains { $0.source.name == "Metadata directory" } == true }
        let calls = try await fixture.directoryCalls(installed)
        XCTAssertEqual(calls, ["search"], "Only the explicit search button starts site aggregation")
        XCTAssertEqual(model.snapshot?.target.kind, .group)
        XCTAssertNil(model.snapshot?.target.metadataProvider)
        XCTAssertNil(model.snapshot?.attempt); XCTAssertNil(model.snapshot?.current)
        XCTAssertEqual(fixture.reads("/tmdb-api/3/search/multi"), 1)
        let warm = try await fixture.assembly.filmImages.read(poster)
        XCTAssertTrue(warm.0 === artwork.image); XCTAssertNil(warm.1)
        XCTAssertEqual(fixture.reads("/douban-images/posters/\(fixture.values.prefix).png"), 1)
        assertUpstreamBoundary(fixture)
    }

    func testHomeTMDBFailureShowsRetryWithoutMasqueradingAsUnmatched() async throws {
        let fixture = try await makeFixture()
        fixture.responses.forceTMDBFailure = true
        let installed = try await fixture.installDirectory(title: fixture.values.doubanTitle)
        let source = DoubanFilmSource { fixture.client }
        let sections = try await source.loadHome()
        let item = try XCTUnwrap(sections.first { $0.id == "movie" }?.items.first)
        XCTAssertNotNil(item.metadataSeed)
        let model = try fixture.assembly.makeFilmPlayback(item: item, kind: .movie, entrySurface: "home")
        fixture.models.append(model)
        try await fixture.assembly.activateFilm(model)
        await model.open()?.value
        try await waitFor { model.snapshot?.entryResolution == "error" && !model.opening }
        let page = FilmDetailViewController(detail: .init(item: item), library: fixture.assembly.filmLibrary,
            images: fixture.assembly.filmImages, themeProvider: fixture.assembly.appearance, model: model)
        try await fixture.mount(page)
        try await waitFor { page.presentedViewController is UIAlertController }
        XCTAssertEqual(model.snapshot?.entrySurface, "home")
        XCTAssertEqual(model.snapshot?.entryTarget?.metadataProvider, .douban)
        XCTAssertEqual(model.snapshot?.stage, "error")
        XCTAssertNotNil(model.snapshot?.error)
        XCTAssertTrue(try button("film.detail.search", page).isHidden)
        let sourceActions = try button("film.detail.source", page).menu?.children.compactMap { $0 as? UIAction } ?? []
        XCTAssertFalse(sourceActions.contains { $0.title == "加载更多来源" })
        XCTAssertFalse(descendants(page.view).compactMap { $0 as? UILabel }.contains {
            $0.text?.contains(model.snapshot?.error ?? "UPSTREAM_HTTP") == true
        })
        XCTAssertEqual(model.snapshot?.entryResolution, "error")
        XCTAssertEqual(model.snapshot?.target.kind, .group)
        XCTAssertEqual(model.snapshot?.target.value["members"], .array([]))
        XCTAssertTrue(model.snapshot?.sources.isEmpty == true)
        let before = try await fixture.directoryCalls(installed)
        XCTAssertEqual(before, [])
        let alert = try XCTUnwrap(page.presentedViewController as? UIAlertController)
        XCTAssertEqual(alert.actions.map(\.title), ["确定", "重试"])
        fixture.responses.forceTMDBFailure = false
        fixture.responses.forceNoTMDBMatch = true
        // UIKit owns the retry action handler; invoking its actual UIButton
        // action is covered by component tests. Here exercise the same owner RPC.
        let dismissed = expectation(description: "Entry failure alert dismissed before owner retry")
        alert.dismiss(animated: false) { dismissed.fulfill() }
        await fulfillment(of: [dismissed], timeout: 10)
        await model.refreshBrowsing()?.value
        try await waitFor { model.snapshot?.entryResolution == "unmatched" && model.snapshot?.stage == "idle" }
        XCTAssertNil(model.snapshot?.error)
        XCTAssertFalse(try button("film.detail.search", page).isHidden)
        let after = try await fixture.directoryCalls(installed)
        XCTAssertEqual(after, [])
        XCTAssertNil(model.snapshot?.attempt); XCTAssertNil(model.snapshot?.current)
        let artwork = try hero(page)
        try await waitFor { artwork.image != nil }
        assertUpstreamBoundary(fixture)
    }

    func testActualDoubanDataArtworkOwnerRetainsAuthenticatedFailureRetryAndCache() async throws {
        let fixture = try await makeFixture()
        let path = "/rexxar/api/v2/movie/\(fixture.values.doubanID)"
        let parameters: RuntimeValue = .object([
            "provider": .string("douban"), "path": .string(path), "query": .object([:]),
        ])
        let data = try await fixture.client.call(module: .film, operation: "data.metadata", parameters: parameters)
        let posterPath = "/v1/douban/images/posters/\(fixture.values.prefix).png"
        let avatarPath = "/v1/douban/images/avatars/\(fixture.values.prefix).png"
        XCTAssertEqual(data.catField("pic")?.catField("normal"), .string(posterPath))
        XCTAssertEqual(data.catField("actors")?.catArray?.first?.catField("avatar")?.catField("normal"), .string(avatarPath))
        let poster = try XCTUnwrap(RuntimeHTTPClient.metadataImageURL(posterPath))
        let posterImage = try await fixture.assembly.filmImages.read(poster)
        XCTAssertNotNil(fixture.assembly.filmImages.cached(poster))
        XCTAssertNotNil(posterImage.0.cgImage)
        let avatar = ArtworkView(size: 0, images: fixture.assembly.filmImages)
        let reference = try XCTUnwrap(RuntimeHTTPClient.metadataImageURL(avatarPath))
        let failed = expectation(description: "Actual Douban Artwork reports the upstream failure")
        avatar.onFailure = { XCTAssertEqual($0, reference); failed.fulfill() }
        avatar.load(reference)
        await fulfillment(of: [fixture.responses.avatarEntered], timeout: 10)
        XCTAssertEqual(fixture.upstream.replyToHeldJSON(.object(["error": .string("Synthetic image failure")]), status: 503), 1)
        await fulfillment(of: [failed], timeout: 10)
        XCTAssertNil(avatar.image); XCTAssertNil(fixture.assembly.filmImages.cached(reference))
        let loaded = expectation(description: "The original Artwork retry performs a new authenticated read")
        avatar.onImage = { _, _ in loaded.fulfill() }
        avatar.retryFailedLoad()
        await fulfillment(of: [loaded], timeout: 10)
        XCTAssertNotNil(avatar.image)
        XCTAssertNotNil(fixture.assembly.filmImages.cached(reference))
        XCTAssertEqual(fixture.reads("/douban-images/avatars/\(fixture.values.prefix).png"), 2)
        let warm = try await fixture.assembly.filmImages.read(reference)
        XCTAssertTrue(warm.0 === avatar.image); XCTAssertNil(warm.1)
        XCTAssertEqual(fixture.reads("/douban-images/avatars/\(fixture.values.prefix).png"), 2,
                       "A warm image read must not create a third upstream request")
        let cached = try await fixture.client.call(module: .film, operation: "data.metadata", parameters: parameters)
        XCTAssertEqual(cached, data)
        XCTAssertEqual(fixture.reads("/douban-api" + path), 1,
                       "Metadata borrowers share one film.data entry without a Douban playback session")
        XCTAssertEqual(fixture.reads("/douban-images/posters/\(fixture.values.prefix).png"), 1)
        let beforeInvalid = fixture.imageReads
        for invalid in ["/v1/douban/images/", "/v1/douban/images/a/../b.png",
                        "/v1/douban/images/a/%2e.png", "/v1/douban/images/a.png?x=1",
                        "/v1/douban/images/a.png#fragment", "//127.0.0.1/a.png"] {
            XCTAssertNil(RuntimeHTTPClient.metadataImageURL(invalid), invalid)
            do {
                _ = try await fixture.client.readMetadataImage(try XCTUnwrap(URL(string: invalid)))
                XCTFail("An invalid image reference cannot perform authenticated HTTP")
            } catch { XCTAssertEqual(error as? RuntimeHTTPError, .invalidRequest) }
        }
        XCTAssertEqual(fixture.imageReads, beforeInvalid)
        assertUpstreamBoundary(fixture)
    }

    private func openHomeItem(_ fixture: FilmMetadataHostFixture, section: Int) async throws
        -> (FilmDetailViewController, MeowfilmRootContainerViewController) {
        let (_, root) = try await fixture.coordinator()
        try await waitFor { root.navigation.topViewController is MeowfilmPrimaryShellViewController }
        let main = try XCTUnwrap(root.navigation.topViewController as? MeowfilmPrimaryShellViewController)
        try await waitFor { main.isPageVisible && root.transitionCoordinator == nil }
        try await dismissKnownMusicNotice(root)
        main.select(.film)
        try await waitFor {
            self.controllers(main).compactMap { $0 as? FilmHomeViewController }.first?.isPageVisible == true
        }
        let home = try XCTUnwrap(controllers(main).compactMap { $0 as? FilmHomeViewController }.first)
        let grid = try XCTUnwrap(descendants(home.view).compactMap { $0 as? UICollectionView }
            .first { $0.accessibilityIdentifier == "film.home.sections" })
        try await waitFor { grid.numberOfSections == 4 && grid.numberOfItems(inSection: section) == 1 }
        try await navigate(root.navigation, to: { $0 is FilmDetailViewController }) {
            home.collectionView(grid, didSelectItemAt: IndexPath(item: 0, section: section))
        }
        let page = try XCTUnwrap(root.navigation.topViewController as? FilmDetailViewController)
        fixture.models.append(try XCTUnwrap(page.filmPlaybackModel))
        return (page, root)
    }

    private func controllers(_ controller: UIViewController) -> [UIViewController] {
        controller.children.flatMap { [$0] + controllers($0) }
    }

    private func makeFixture() async throws -> FilmMetadataHostFixture {
        let resources = try await hostResources()
        try await waitFor { UIApplication.shared.connectedScenes.contains { $0.activationState == .foregroundActive } }
        let app = try XCTUnwrap(UIApplication.shared.delegate as? MeowfilmAppDelegate)
        let assembly = try XCTUnwrap(app.readyAssembly())
        let client = try await resources.scriptSession.connection()
        try await client.validateAppModules()
        let image = UIGraphicsImageRenderer(size: CGSize(width: 24, height: 36)).pngData {
            UIColor.systemTeal.setFill(); $0.fill(CGRect(x: 0, y: 0, width: 24, height: 36))
        }
        let fixture = try FilmMetadataHostFixture(assembly: assembly, resources: resources, client: client, image: image)
        addTeardownBlock { try await fixture.close() }
        try await fixture.configure()
        return fixture
    }

    private func assertUpstreamBoundary(_ fixture: FilmMetadataHostFixture) {
        let requests = fixture.upstream.requests
        XCTAssertTrue(requests.allSatisfy { $0.headers["x-meow-session"] == nil && $0.headers["x-meow-request-id"] == nil })
        let tmdb = requests.filter { $0.path.hasPrefix("/tmdb-api/") }
        XCTAssertTrue(tmdb.allSatisfy { $0.headers["authorization"] == "Bearer synthetic-metadata-token" })
        XCTAssertTrue(tmdb.filter { $0.path.contains("/season/") }.allSatisfy { $0.query == ["language": "zh-CN"] })
        XCTAssertTrue(tmdb.filter { $0.path == "/tmdb-api/3/tv/\(fixture.values.tvID)" ||
            $0.path == "/tmdb-api/3/tv/\(fixture.values.similarID)" }.allSatisfy {
                $0.query == ["language": "zh-CN", "append_to_response": "credits,similar,images"]
            })
        XCTAssertTrue(tmdb.filter { request in
            request.path.contains("/person/") || ["/credits", "/similar", "/images"].contains { request.path.hasSuffix($0) }
        }.isEmpty, "Cast is displayed from appended detail, never a new actor navigation or duplicate appendix request")
        let douban = requests.filter { $0.path.hasPrefix("/douban-api/") || $0.path.hasPrefix("/douban-search/") }
        XCTAssertTrue(douban.allSatisfy { $0.headers["cookie"] == "metadata-fixture=synthetic" && $0.headers["authorization"] == nil })
        let images = requests.filter { $0.path.hasPrefix("/tmdb-images/") || $0.path.hasPrefix("/douban-images/") }
        XCTAssertFalse(images.isEmpty)
        XCTAssertTrue(images.allSatisfy { $0.headers["authorization"] == nil && $0.headers["cookie"] == nil && $0.query.isEmpty })
        XCTAssertTrue(images.filter { $0.path.hasPrefix("/tmdb-images/") }.allSatisfy {
            $0.path.hasPrefix("/tmdb-images/t/p/original/")
        })
        XCTAssertTrue(images.filter { $0.path.hasPrefix("/douban-images/") }.allSatisfy {
            $0.headers["referer"] == "https://movie.douban.com/"
        })
    }

    private func waitFor(_ predicate: @escaping @MainActor () -> Bool) async throws {
        let ready = XCTNSPredicateExpectation(predicate: NSPredicate { _, _ in
            MainActor.assumeIsolated { predicate() }
        }, object: nil)
        let result = await XCTWaiter.fulfillment(of: [ready], timeout: 10)
        XCTAssertEqual(result, .completed)
        guard result == .completed, predicate() else { throw FilmMetadataHostFailure.notReady }
    }
    private func navigate(_ navigation: PageNavigationController, to matches: @escaping @MainActor (UIViewController) -> Bool,
                          action: () -> Void) async throws {
        action()
        try await waitFor {
            guard let page = navigation.topViewController else { return false }
            return navigation.transitionCoordinator == nil && matches(page) &&
                (page as? PageViewController)?.isPageVisible == true
        }
        XCTAssertNotNil(navigation.topViewController?.viewIfLoaded?.window)
    }
    private func dismissKnownMusicNotice(_ root: MeowfilmRootContainerViewController) async throws {
        guard let alert = root.presentedViewController as? UIAlertController else { return }
        // A dedicated simulator can retain an unrelated music Keychain-policy
        // or document error. Accept only the exact music read notice; a film or
        // unexpected application failure must still throw below.
        let known = alert.title == "错误" && alert.actions.map(\.title) == ["取消", "重试"] &&
            alert.actions.map(\.style) == [.cancel, .default] &&
            [MusicServiceError.storagePolicyMismatch, .corruptDocument].contains {
                let prefix = $0.message(operation: "音乐内容读取")
                let pattern = "^" + NSRegularExpression.escapedPattern(for: prefix) +
                    "(?:（诊断 [A-Fa-f0-9]{8}）)?$"
                return alert.message?.range(of: pattern, options: .regularExpression) != nil
            }
        XCTAssertTrue(known, "Do not dismiss a metadata or unexpected application failure")
        guard known else { throw FilmMetadataHostFailure.unexpectedNotice }
        try await waitFor { !alert.isBeingPresented && alert.transitionCoordinator == nil }
        let dismissed = expectation(description: "Known unrelated music notice dismissed without retry")
        alert.dismiss(animated: false) { dismissed.fulfill() }
        await fulfillment(of: [dismissed], timeout: 10)
    }
    private func descendants(_ view: UIView) -> [UIView] { view.subviews.flatMap { [$0] + descendants($0) } }
    private func episodeIDs(_ page: FilmDetailViewController) -> [String] {
        descendants(page.view).compactMap { $0 as? FilmEpisodeButton }.compactMap(\.accessibilityIdentifier)
    }
    private func button(_ id: String, _ page: UIViewController) throws -> UIButton {
        try XCTUnwrap(descendants(page.view).compactMap { $0 as? UIButton }.first { $0.accessibilityIdentifier == id }, id)
    }
    private func hero(_ page: FilmDetailViewController) throws -> ArtworkView {
        try XCTUnwrap(descendants(page.view).compactMap { $0 as? ArtworkView }.first {
            $0.superview?.layer.mask is CAGradientLayer
        })
    }
    private func menuAction(_ title: String, in button: UIButton) throws -> UIAction {
        try XCTUnwrap(button.menu?.children.compactMap { $0 as? UIAction }.first { $0.title == title }, title)
    }
    private func invoke(_ action: UIAction) {
        let control = UIButton(type: .system)
        control.addAction(action, for: .touchUpInside); control.sendActions(for: .touchUpInside)
    }
}

private enum FilmMetadataHostFailure: Error { case dedicatedSimulatorRequired, notReady, unexpectedNotice }

@MainActor
private final class FilmMetadataHostFixture {
    let assembly: MeowfilmAssembly
    let resources: MeowfilmResources
    let client: RuntimeHTTPClient
    let values: FilmMetadataHostValues
    let responses: FilmMetadataHostResponses
    let upstream: TMDBTestUpstream
    var models: [FilmPlaybackModel] = []
    var searchSession: FilmSearchSession?
    private var originalTMDB: RuntimeObject?, originalDouban: RuntimeObject?
    private var originalPlayback: RuntimeValue?
    private var installedID: String?
    private var window: UIWindow?, previousWindow: UIWindow?
    private var coordinatorOwner: MeowfilmCoordinator?
    private var originalRestore: (@MainActor (UUID) async -> Bool)?

    init(assembly: MeowfilmAssembly, resources: MeowfilmResources, client: RuntimeHTTPClient, image: Data) throws {
        self.assembly = assembly; self.resources = resources; self.client = client
        let values = FilmMetadataHostValues()
        self.values = values
        let responses = FilmMetadataHostResponses(values: values, image: image)
        self.responses = responses
        upstream = try TMDBTestUpstream { responses.reply($0) }
    }
    var seasonReads: [Int] {
        upstream.requests.compactMap {
            guard $0.path.hasPrefix("/tmdb-api/3/tv/\(values.tvID)/season/") else { return nil }
            return Int($0.path.split(separator: "/").last!)
        }
    }
    func reads(_ path: String) -> Int { upstream.requests.filter { $0.path == path }.count }
    var imageReads: Int {
        upstream.requests.filter { $0.path.hasPrefix("/tmdb-images/") || $0.path.hasPrefix("/douban-images/") }.count
    }

    func configure() async throws {
        let tmdb = try await client.call(module: .tmdb, operation: "settings.get")
        let douban = try await client.call(module: .douban, operation: "settings.get")
        try await resources.filmSourceSettings.refresh()
        let catalog = await resources.filmSourceSettings.snapshot
        guard tmdb.catField("apiKey") == .string(""), tmdb.catField("accessToken") == .string(""),
              douban.catField("cookie") == .string(""), catalog.cats.isEmpty,
              assembly.filmPlayback?.isReleased != false else { throw FilmMetadataHostFailure.dedicatedSimulatorRequired }
        originalTMDB = ["apiBase": try XCTUnwrap(tmdb.catField("apiBase")), "imageBase": try XCTUnwrap(tmdb.catField("imageBase")),
                        "apiKey": .string(""), "accessToken": .string("")]
        var restore: RuntimeObject = ["cookie": .string("")]
        for name in ["apiBase", "searchBase", "imageBase", "apiProxy", "imageProxy"] {
            restore[name] = try XCTUnwrap(douban.catField(name))
        }
        originalDouban = restore
        originalPlayback = try await client.call(module: .film, operation: "settings.get")
        var settings = try XCTUnwrap(originalPlayback).filmRequiredObject()
        settings["iosAutoPrepare"] = .bool(false)
        _ = try await client.call(module: .film, operation: "settings.set", parameters: .object(settings))
        try await upstream.start()
        _ = try await client.call(module: .tmdb, operation: "settings.update", parameters: .object([
            "apiBase": .string(upstream.base + "/tmdb-api/3"),
            "imageBase": .string(upstream.base + "/tmdb-images"),
            "apiKey": .string(""), "accessToken": .string("synthetic-metadata-token"),
        ]))
        _ = try await client.call(module: .douban, operation: "settings.update", parameters: .object([
            "apiBase": .string(upstream.base + "/douban-api"), "searchBase": .string(upstream.base + "/douban-search"),
            "imageBase": .string(upstream.base + "/douban-images"), "apiProxy": .string(""), "imageProxy": .string(""),
            "cookie": .string("metadata-fixture=synthetic"),
        ]))
        assembly.filmSearch?.metadataSettingsChanged()
    }

    func installDirectory(title: String? = nil) async throws -> FilmCatSource {
        installedID = values.prefix
        return try await resources.filmSourceSettings.importLocal(name: "Metadata directory",
            source: filmMetadataHostCat.replacingOccurrences(of: "__TITLE__", with: title ?? values.title),
            installationId: values.prefix)
    }
    func directoryCalls(_ installed: FilmCatSource) async throws -> [String] {
        let value = try await client.call(module: .cat, operation: "invoke", parameters: .object([
            "installationId": .string(installed.installationId), "generation": .number(Double(installed.generation)),
            "siteId": .string("metadata"), "operation": .string("home"), "payload": .object([:]),
        ]))
        return try XCTUnwrap(value.catField("calls")?.catArray).map { try XCTUnwrap($0.catText) }
    }

    func mount(_ page: UIViewController) async throws {
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
            .first { $0.activationState == .foregroundActive })
        previousWindow = scene.windows.first { $0.isKeyWindow }
        let window = UIWindow(windowScene: scene)
        self.window = window
        window.frame = scene.coordinateSpace.bounds; window.windowLevel = .normal + 1
        window.rootViewController = page; window.makeKeyAndVisible()
        page.loadViewIfNeeded(); window.layoutIfNeeded()
    }
    func coordinator() async throws -> (MeowfilmCoordinator, MeowfilmRootContainerViewController) {
        try await mount(UIViewController())
        let window = try XCTUnwrap(window)
        originalRestore = assembly.restoreFilmPlaybackUI
        let coordinator = MeowfilmCoordinator(window: window, assembly: assembly, resetUserData: {})
        coordinatorOwner = coordinator
        coordinator.start()
        let root = try XCTUnwrap(window.rootViewController as? MeowfilmRootContainerViewController)
        root.loadViewIfNeeded()
        return (coordinator, root)
    }

    func close() async throws {
        upstream.stop() // Release held image HTTP before any owner drain.
        searchSession?.cancel()
        await searchSession?.drain()
        if let root = window?.rootViewController as? MeowfilmRootContainerViewController {
            // Also drain a page admitted before a navigation assertion failed.
            for page in root.navigation.viewControllers.compactMap({ $0 as? FilmDetailViewController }) {
                if let model = page.filmPlaybackModel, !models.contains(where: { $0 === model }) { models.append(model) }
            }
            root.navigation.didShowPage = nil; root.navigation.willShowPage = nil
        }
        for model in models {
            if model.snapshot?.previewEpisodes == true {
                await model.send("preview", parameters: ["enabled": .bool(false)])?.value
            }
        }
        if let root = window?.rootViewController { PageViewController.invalidatePages(in: root) }
        for model in models {
            await model.close(allowSystemOwner: false)
            XCTAssertTrue(model.isReleased)
            XCTAssertNil(model.mediaStopError); XCTAssertNil(model.sessionReleaseError)
        }
        window?.isHidden = true; window?.rootViewController = nil; window = nil; previousWindow?.makeKey()
        if coordinatorOwner != nil { assembly.restoreFilmPlaybackUI = originalRestore; coordinatorOwner = nil }
        var failure: Error?
        if let installedID {
            do {
                try await resources.filmSourceSettings.refresh()
                let catalog = await resources.filmSourceSettings.snapshot
                if catalog.cats.contains(where: { $0.installationId == installedID }) {
                    try await resources.filmSourceSettings.deleteCat(installedID)
                }
            }
            catch { failure = error }
        }
        if let originalPlayback {
            do { _ = try await client.call(module: .film, operation: "settings.set", parameters: originalPlayback) }
            catch { failure = failure ?? error }
        }
        for (module, settings) in [(RuntimeModule.tmdb, originalTMDB), (.douban, originalDouban)] {
            if let settings {
                do { _ = try await client.call(module: module, operation: "settings.update", parameters: .object(settings)) }
                catch { failure = failure ?? error }
            }
        }
        if originalTMDB != nil { assembly.filmSearch?.metadataSettingsChanged() }
        if let failure { throw failure }
    }
}

private struct FilmMetadataHostValues: Sendable {
    let prefix = "metadata-host-" + UUID().uuidString.lowercased()
    let tvID = Int.random(in: 1_000_000_000...1_500_000_000)
    let similarID = Int.random(in: 1_500_000_001...2_000_000_000)
    let doubanID = Int.random(in: 2_000_000_001...2_100_000_000)
    var title: String { prefix }
    var doubanTitle: String { "Douban-" + prefix }
    func image(_ suffix: String) -> RuntimeValue { .string("/\(prefix)-\(suffix).png") }
    var home: RuntimeValue {
        .object(["items": .array([.object([
            "id": .string(String(doubanID)), "title": .string(doubanTitle), "year": .string("2020"),
            "card_subtitle": .string("2031 / 不作为年份来源"),
            "pic": .object(["normal": .string("/v1/douban/images/posters/\(prefix).png")]),
            "fixture": .object(["origin": .string("home-row")]),
        ])])])
    }
    var seedSearch: RuntimeValue {
        .object(["page": .number(1), "total_pages": .number(1), "results": .array([
            .object(["id": .number(Double(similarID)), "media_type": .string("tv"), "name": .string(title),
                     "first_air_date": .string("2019-01-01")]),
            .object(["id": .number(Double(tvID)), "media_type": .string("tv"), "name": .string(title),
                     "first_air_date": .string("2020-01-01")]),
        ])])
    }
    var search: RuntimeValue {
        .object(["page": .number(1), "total_pages": .number(1), "results": .array([
            .object(["id": .number(Double(tvID)), "media_type": .string("tv"), "name": .string(title),
                     "overview": .string("仅搜索摘要"), "poster_path": image("poster")]),
        ])])
    }
    var detail: RuntimeValue {
        .object(["id": .number(Double(tvID)), "name": .string(title), "overview": .string("完整资料简介"),
            "genres": .array([]), "status": .string("Returning Series"), "poster_path": image("poster"),
            "backdrop_path": image("backdrop"), "first_air_date": .string("2020-01-01"),
            "number_of_seasons": .number(2), "number_of_episodes": .number(5),
            "next_episode_to_air": .object(["season_number": .number(1), "episode_number": .number(3), "air_date": .string("2999-01-01")]),
            "seasons": .array([
                .object(["season_number": .number(0), "name": .string("特别篇"), "episode_count": .number(1)]),
                .object(["season_number": .number(1), "name": .string("第一季"), "episode_count": .number(3)]),
                .object(["season_number": .number(2), "name": .string("第二季"), "episode_count": .number(1)]),
            ]),
            "credits": .object([
                "crew": .array([.object(["id": .number(81), "name": .string("合成导演"), "job": .string("Director"), "profile_path": image("director")])]),
                "cast": .array([.object(["id": .number(82), "name": .string("合成演员"), "character": .string("主角"), "profile_path": image("actor")])]),
            ]),
            "similar": .object(["results": .array([.object([
                "id": .number(Double(similarID)), "name": .string(title), "poster_path": image("similar"),
            ])])]),
            "images": .object(["logos": .array([.object(["file_path": image("logo")])])]),
        ])
    }
    var related: RuntimeValue {
        .object(["id": .number(Double(similarID)), "name": .string(title), "overview": .string("同标题另一作品"),
            "genres": .array([]), "seasons": .array([]), "credits": .object(["cast": .array([]), "crew": .array([])]),
            "similar": .object(["results": .array([])]), "images": .object(["logos": .array([])])])
    }
    func season(_ number: Int) -> RuntimeValue {
        let episodes: [RuntimeValue]
        if number == 0 {
            episodes = [.object(["season_number": .number(0), "episode_number": .number(0),
                                 "name": .string("真实第零特别篇"), "air_date": .string("2020-01-01")])]
        } else if number == 1 {
            episodes = (1...3).map { .object([
                "season_number": .number(1), "episode_number": .number(Double($0)),
                "name": .string("真实单集\($0)"), "air_date": .string($0 == 3 ? "2999-01-01" : "2020-01-01"),
                "still_path": image("episode-\($0)"),
            ]) }
        } else { episodes = [] }
        return .object(["season_number": .number(Double(number)), "episodes": .array(episodes)])
    }
    var douban: RuntimeValue {
        .object(["id": .string(String(doubanID)), "title": .string(doubanTitle), "genres": .array([.string("剧情")]),
            "intro": .string("豆瓣合成完整资料"), "pic": .object(["normal": .string("/posters/\(prefix).png")]),
            "actors": .array([.object(["id": .string("91"), "name": .string("豆瓣合成演员"), "role": .string("演员"),
                "avatar": .object(["normal": .string("/avatars/\(prefix).png")])])])])
    }
}

private final class FilmMetadataHostResponses: @unchecked Sendable {
    let avatarEntered = XCTestExpectation(description: "Douban avatar reached the real HTTP upstream")
    private let lock = NSLock()
    private var noTMDBMatch = false
    private var tmdbFailure = false
    private var avatarReads = 0
    private let values: FilmMetadataHostValues, image: Data
    init(values: FilmMetadataHostValues, image: Data) { self.values = values; self.image = image }
    var forceNoTMDBMatch: Bool {
        get { lock.lock(); defer { lock.unlock() }; return noTMDBMatch }
        set { lock.lock(); defer { lock.unlock() }; noTMDBMatch = newValue }
    }
    var forceTMDBFailure: Bool {
        get { lock.lock(); defer { lock.unlock() }; return tmdbFailure }
        set { lock.lock(); defer { lock.unlock() }; tmdbFailure = newValue }
    }
    func reply(_ request: TMDBTestUpstream.Request) -> TMDBTestUpstream.Response {
        if request.path == "/tmdb-api/3/search/multi" {
            if forceTMDBFailure { return .json(.object(["error": .string("Synthetic TMDB failure")]), status: 503) }
            return .json(forceNoTMDBMatch ? .object(["page": .number(1), "total_pages": .number(1),
                                                      "results": .array([])]) :
                request.query["query"] == values.doubanTitle ? values.seedSearch : values.search)
        }
        if request.path == "/tmdb-api/3/search/movie" || request.path == "/tmdb-api/3/search/tv" {
            return .json(.object(["page": .number(1), "total_pages": .number(1), "results": .array([])]))
        }
        if request.path == "/tmdb-api/3/tv/\(values.tvID)" { return .json(values.detail) }
        if request.path == "/tmdb-api/3/tv/\(values.similarID)" { return .json(values.related) }
        for number in 0...2 where request.path == "/tmdb-api/3/tv/\(values.tvID)/season/\(number)" {
            return .json(values.season(number))
        }
        if request.path == "/douban-search/movie/subject_search" {
            return .bytes(Data(#"<html><script>window.__DATA__ = {"items":[],"total":0};</script></html>"#.utf8), type: "text/html")
        }
        if request.path == "/douban-api/rexxar/api/v2/movie/\(values.doubanID)" { return .json(values.douban) }
        if request.path == "/douban-api/rexxar/api/v2/subject/recent_hot/movie" ||
            request.path == "/douban-api/rexxar/api/v2/subject/recent_hot/tv" { return .json(values.home) }
        if request.path == "/douban-images/avatars/\(values.prefix).png" {
            lock.lock(); avatarReads += 1; let first = avatarReads == 1; lock.unlock()
            if first { avatarEntered.fulfill(); return .hold(onDisconnect: {}) }
        }
        if request.path.hasPrefix("/tmdb-images/t/p/original/") || request.path == "/douban-images/posters/\(values.prefix).png" ||
            request.path == "/douban-images/avatars/\(values.prefix).png" { return .bytes(image, type: "image/png") }
        return .json(.object(["error": .string("Unexpected metadata fixture route")]), status: 404)
    }
}

private let filmMetadataHostCat = #"""
const http = require('http');
let server;
const calls = [];
const json = (response, value) => {
  response.writeHead(200, { 'content-type': 'application/json' });
  response.end(JSON.stringify(value));
};
module.exports.start = () => new Promise(resolve => {
  server = http.createServer(async (request, response) => {
    const url = new URL(request.url, 'http://localhost');
    if (url.pathname === '/config' || url.pathname === '/full-config') {
      json(response, { video: { sites: [{ key: 'metadata', name: 'Metadata directory', api: '/api/metadata',
        actions: ['home', 'search', 'detail', 'play'] }] } }); return;
    }
    const chunks = [];
    for await (const chunk of request) chunks.push(Buffer.from(chunk));
    const action = url.pathname.split('/').pop();
    if (action === 'home') { json(response, { calls }); return; }
    calls.push(action);
    if (action === 'search') {
      json(response, { page: 1, pagecount: 1, list: [{ vod_id: 0, vod_name: '__TITLE__' }] });
    } else if (action === 'detail') {
      json(response, { list: [{ vod_id: 0, vod_name: '__TITLE__', vod_play_from: '真实目录',
        vod_play_url: '第1集.1080p$one#第2集.1080p$two#第3集.1080p$three#第99集.1080p$ninety-nine' }] });
    } else { response.writeHead(500); response.end('Address resolution is forbidden in metadata browsing'); }
  });
  server.listen(9987, '127.0.0.1', resolve);
});
module.exports.stop = () => { server?.closeAllConnections(); server?.close(); };
"""#
