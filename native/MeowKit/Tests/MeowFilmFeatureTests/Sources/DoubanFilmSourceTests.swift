import Foundation
import XCTest
import MeowRuntime
@testable import MeowFilmFeature

final class DoubanFilmSourceTests: XCTestCase {
    func testFourHomeColumnsUseRuntimeEntryAndKeepOrderWhenResponsesFinishInReverse() async throws {
        let expected = [
            (id: "movie", title: "热门电影", kind: "movie", category: "热门", type: "全部"),
            (id: "tv", title: "热门剧集", kind: "tv", category: "tv", type: "tv"),
            (id: "anime", title: "新番放送", kind: "tv", category: "tv", type: "tv_animation"),
            (id: "show", title: "热门综艺", kind: "tv", category: "show", type: "show")
        ]
        let started = expectation(description: "four runtime requests started")
        started.expectedFulfillmentCount = 4
        let fixture = FilmMetadataHTTPFixture(held: true, started: { started.fulfill() }) { _ in
            Self.entry(items: (100..<120).map {
                .object(["id": .number(Double($0)), "title": .string("真实首页项")])
            })
        }
        defer { fixture.close() }
        let client = try fixture.connection()
        addTeardownBlock { await client.close() }
        let source = DoubanFilmSource { client }
        XCTAssertEqual(source.descriptor, FilmSourceDescriptor(id: "douban", title: "豆瓣"))
        let load = Task { try await source.loadHome() }
        await fulfillment(of: [started], timeout: 3)
        fixture.finish()
        let sections = try await load.value
        XCTAssertEqual(sections.map(\.id), expected.map(\.id))
        XCTAssertEqual(sections.map(\.title), expected.map(\.title))
        XCTAssertEqual(sections.map { $0.items.first?.sourceID }, ["douban", "douban", "douban", "douban"])
        XCTAssertEqual(sections.map { $0.items.first?.id }, ["100", "100", "100", "100"])
        XCTAssertEqual(sections.map { $0.items.count }, [12, 12, 12, 12])
        XCTAssertEqual(sections.map { $0.cache?.status }, [.fresh, .fresh, .fresh, .fresh])
        let inputs = fixture.inputs
        XCTAssertEqual(inputs.count, 4)
        for definition in expected {
            let input = try XCTUnwrap(inputs.first { $0["query"]?.filmObject?["type"] == .string(definition.type) })
            XCTAssertEqual(input["provider"], .string("douban"))
            XCTAssertEqual(input["path"], .string("/rexxar/api/v2/subject/recent_hot/" + definition.kind))
            XCTAssertEqual(input["query"], .object(["start": .number(0), "limit": .number(12),
                "category": .string(definition.category), "type": .string(definition.type)]))
            XCTAssertNil(input["refresh"])
        }
        for request in fixture.requests {
            XCTAssertEqual(request.url?.host, fixture.baseURL.host)
            XCTAssertEqual(request.url?.path, "/v1/film/data.metadata.entry")
            XCTAssertEqual(request.httpMethod, "POST")
            XCTAssertFalse(request.httpShouldHandleCookies)
            XCTAssertNil(request.value(forHTTPHeaderField: "Cookie"))
            XCTAssertNil(request.value(forHTTPHeaderField: "Authorization"))
        }
    }

    func testParsingKeepsRuntimeBusinessIdentityPosterFallbacksAndNormalizedScores() throws {
        let payload = Data(#"""
        {"items":[
          {"businessId":"bad-first","playbackTarget":null,"id":100,"title":"坏目标先项"},
          {"businessId":"fixture-100","playbackTarget":{"id":"fixture-100","kind":"movie","title":"第一部","metadataProvider":"douban","metadataId":"100"},"id":100,"title":" 第一部 ","pic":{"normal":"http://img1.doubanio.com/a.jpg",
            "large":"https://img2.doubanio.com/a-large.jpg"},"rating":{"value":8.6},"card_subtitle":"真实副标题"},
          {"id":"blank","title":"   "},
          {"businessId":false,"playbackTarget":{"id":"bad-200","kind":"movie","title":"坏身份"},"id":200,"title":"坏身份"},
          {"businessId":"bad-250","playbackTarget":{"id":"bad-250","kind":"future","title":"坏类型"},"id":250,"title":"坏类型"},
          {"id":"missing-target","title":"缺少新的身份材料"},
          {"businessId":"fixture-300","playbackTarget":{"id":"fixture-300","kind":"movie","title":"第二部","metadataProvider":"douban","metadataId":"300"},"id":"300","title":"第二部","pic":{"large":"https://img2.doubanio.com/large.jpg"},"rating":{"value":0}},
          {"businessId":"fixture-400","playbackTarget":{"id":"fixture-400","kind":"movie","title":"第三部","metadataProvider":"douban","metadataId":"400"},"id":400,"title":"第三部","cover":{"url":"//img3.doubanio.com/cover.jpg"},"rating":{"value":"9.0"}},
          {"businessId":"fixture-500","playbackTarget":{"id":"fixture-500","kind":"movie","title":"第四部","metadataProvider":"douban","metadataId":"500"},"id":"500","title":"第四部","cover_url":"https://user@artwork.invalid/private.jpg","rating":{"value":"NaN"}},
          {"id":true,"title":"布尔值不是影片 ID"},
          {"title":"缺少稳定身份"},
          {"id":100,"title":"重复影片"},
          null
        ]}
        """#.utf8)
        let items = try DoubanFilmSource.parse(payload)
        XCTAssertEqual(items.map(\.id), ["100", "300", "400", "500"])
        XCTAssertEqual(items.map(\.sourceID), ["douban", "douban", "douban", "douban"])
        XCTAssertEqual(items[0].title, "第一部")
        XCTAssertEqual(items[0].businessID, "fixture-100")
        XCTAssertEqual(items[0].playbackTarget?.id, "fixture-100")
        XCTAssertEqual(items[0].posterURL?.absoluteString, "https://img.doubanio.cmliussss.net/a.jpg")
        XCTAssertEqual(items[0].largePosterURL?.absoluteString, "https://img.doubanio.cmliussss.net/a-large.jpg",
                       "Cards keep pic.normal while details receive the normalized pic.large")
        XCTAssertEqual(items[0].score, "8.6")
        XCTAssertEqual(items[0].subtitle, "真实副标题")
        XCTAssertEqual(items[1].posterURL?.absoluteString, "https://img.doubanio.cmliussss.net/large.jpg")
        XCTAssertNil(items[1].largePosterURL, "A large-only row already uses that image as its card poster")
        XCTAssertEqual(items[1].score, "")
        XCTAssertEqual(items[2].posterURL?.absoluteString, "https://img.doubanio.cmliussss.net/cover.jpg")
        XCTAssertNil(items[2].largePosterURL)
        XCTAssertEqual(items[2].score, "9")
        XCTAssertEqual(items[3].posterURL?.absoluteString, "https://user@artwork.invalid/private.jpg")
        XCTAssertNil(items[3].largePosterURL)
        XCTAssertEqual(items[3].score, "")
    }

    func testPosterNormalizationPreservesEndpointsAndKeepsTheDoubanCDNMapping() {
        for host in ["img.doubanio.com", "img.douban.com", "img12.doubanio.com",
                     "img9.douban.com", "img.doubanio.cmliussss.com", "img.doubanio.cmliussss.net"] {
            XCTAssertEqual(FilmPosterAddress.normalize("http://" + host + "/poster.jpg")?.absoluteString,
                           "https://img.doubanio.cmliussss.net/poster.jpg")
        }
        XCTAssertEqual(FilmPosterAddress.normalize("http://artwork.invalid:80/poster.jpg")?.absoluteString,
                       "http://artwork.invalid:80/poster.jpg")
        XCTAssertEqual(FilmPosterAddress.normalize("https://imgx.doubanio.com/poster.jpg")?.host,
                       "imgx.doubanio.com")
        for address in ["http://127.0.0.1:19191/poster.jpg", "http://localhost:19191/poster.jpg",
                        "http://[::1]:19191/poster.jpg", "http://192.168.1.2:19191/poster.jpg",
                        "https://user:synthetic@artwork.invalid/a.jpg",
                        "https://artwork.invalid:8443/a.jpg", "https://artwork.invalid/a.jpg#fragment",
                        "https://artwork.invalid/a.jpg?token=" + String(repeating: "x", count: 8_193)] {
            XCTAssertEqual(FilmPosterAddress.normalize(address)?.absoluteString, address)
        }
        for address in ["file:///poster.jpg", "/relative.jpg", "https://artwork.invalid/a\n.jpg"] {
            XCTAssertNil(FilmPosterAddress.normalize(address))
        }
    }

    func testEmptyItemsAreValidButAllBadItemsMissingItemsAndInvalidJSONAreErrors() throws {
        XCTAssertEqual(try DoubanFilmSource.parse(Data(#"{"items":[]}"#.utf8)), [])
        for payload in ["[]", "{}", #"{"items":null}"#, #"{"items":{}}"#, "invalid JSON",
                        #"{"items":[{}]}"#, #"{"items":[{"title":"缺少身份"},{"id":"missing-title"}]}"#,
                        #"{"items":[{"id":"1","title":"  "},null,{"id":true,"title":"错误身份"}]}"#,
                        #"{"items":[{"id":"1","title":"缺少新身份"}]}"#,
                        #"{"items":[{"id":"1","title":"坏身份","businessId":null,"playbackTarget":{"id":"1","kind":"movie","title":"坏身份"}}]}"#,
                        #"{"items":[{"id":"1","title":"坏目标","businessId":"1","playbackTarget":{"id":"1","kind":"future","title":"坏目标"}}]}"#] {
            XCTAssertThrowsError(try DoubanFilmSource.parse(Data(payload.utf8))) {
                XCTAssertEqual($0 as? FilmHomeError, .invalidResponse)
            }
        }
    }

    func testAllBadNonemptyPagesFailThroughBothSourceEntries() async throws {
        let fixture = FilmMetadataHTTPFixture()
        defer { fixture.close() }
        let client = try fixture.connection()
        addTeardownBlock { await client.close() }
        let source = DoubanFilmSource { client }
        for rows in [
            [RuntimeValue.object([:])],
            [.object(["title": .string("缺少身份")]), .object(["id": .string("blank"), "title": .string(" ")])],
            Array(repeating: .object([:]), count: 24),
            [.object(["id": .number(0), "title": .string("坏业务身份"), "businessId": .null])],
            Array(repeating: .object(["id": .number(0), "title": .string("坏目标"), "playbackTarget": .object([:])]), count: 24),
        ] {
            fixture.setResponse(Self.entry(items: rows))
            do {
                _ = try await source.loadHome()
                XCTFail("An unusable nonempty home cannot publish successful empty sections")
            } catch { XCTAssertEqual(error as? FilmHomeError, .invalidResponse) }
            do {
                _ = try await source.loadCategory(.movie, filters: [:], offset: 24)
                XCTFail("An unusable nonempty page cannot advance or end pagination")
            } catch { XCTAssertEqual(error as? FilmHomeError, .invalidResponse) }
        }
    }

    func testNewIdentityFailuresStayLocalAndHealthyDuplicatesKeepRawOffsetsAndTargets() async throws {
        let first: RuntimeValue = .object(["id": .number(0), "title": .string("健康前项")])
        let lastTarget = FilmPlaybackTarget(id: "opaque-tv", kind: .tv, title: "健康后项",
            metadataProvider: .douban, metadataID: "1")
        let last: RuntimeValue = .object(["id": .number(1), "title": .string("健康后项"),
            "businessId": .string("opaque-tv"), "playbackTarget": .object(lastTarget.value)])
        let badFirst: RuntimeValue = .object(["id": .number(0), "title": .string("坏身份先项"), "businessId": .bool(false)])
        let badLast: RuntimeValue = .object(["id": .number(1), "title": .string("坏目标先项"), "playbackTarget": .object([:])])
        let rows = [badFirst, first] + Array(repeating: badLast, count: 21) + [last]
        let fixture = FilmMetadataHTTPFixture(response: { _ in Self.entry(items: rows) })
        defer { fixture.close() }
        let client = try fixture.connection()
        addTeardownBlock { await client.close() }
        let source = DoubanFilmSource { client }
        let page = try await source.loadCategory(.movie, filters: [:], offset: 24)
        XCTAssertEqual(page.items.map(\.id), ["0", "1"])
        XCTAssertEqual(page.items.map(\.businessID), ["fixture-0", "opaque-tv"])
        XCTAssertEqual(page.items.map(\.kind), [.movie, .tv])
        XCTAssertEqual(page.items.last?.playbackTarget, lastTarget)
        XCTAssertEqual(page.nextOffset, 48)
        XCTAssertEqual(fixture.inputs.first?["query"]?.filmObject?["start"], .number(24))
        let home = try await source.loadHome()
        XCTAssertEqual(home.map { $0.items.map(\.id) }, Array(repeating: ["0", "1"], count: 4))
        XCTAssertTrue(home.allSatisfy { $0.items.last?.playbackTarget == lastTarget })
    }

    @MainActor
    func testHomeAllBadRefreshKeepsCompletedContentAndRecoveryAndTrueEmptyStillPublish() async throws {
        let first: RuntimeValue = .object(["id": .number(0), "title": .string("旧内容")])
        let fixture = FilmMetadataHTTPFixture(response: { _ in Self.entry(items: [first]) })
        defer { fixture.close() }
        let client = try fixture.connection()
        addTeardownBlock { await client.close() }
        let model = FilmHomeModel(sources: [DoubanFilmSource { client }])
        defer { model.close() }
        let initial = try XCTUnwrap(model.loadIfNeeded()); await initial.value
        let original = model.sections
        XCTAssertEqual(original.map { $0.items.map(\.id) }, Array(repeating: ["0"], count: 4))
        for rows in [[RuntimeValue.object([:])],
                     [.object(["id": .number(0), "title": .string("坏目标"), "playbackTarget": .null])]] {
            fixture.setResponse(Self.entry(items: rows))
            let failed = try XCTUnwrap(model.refresh()); await failed.value
            XCTAssertEqual(model.error, .invalidResponse)
            XCTAssertEqual(model.sections, original)
            XCTAssertFalse(model.isLoading)
        }
        let last: RuntimeValue = .object(["id": .string("last"), "title": .string("新内容")])
        fixture.setResponse(Self.entry(items: [first, .null, last]))
        let recovered = try XCTUnwrap(model.refresh()); await recovered.value
        XCTAssertNil(model.error)
        XCTAssertEqual(model.sections.map { $0.items.map(\.id) }, Array(repeating: ["0", "last"], count: 4))
        fixture.setResponse(Self.entry(items: []))
        let empty = try XCTUnwrap(model.refresh()); await empty.value
        XCTAssertNil(model.error)
        XCTAssertEqual(model.sections.map { $0.items.count }, [0, 0, 0, 0])
        XCTAssertFalse(model.isLoading)
    }

    @MainActor
    func testCategoryAllBadPagesKeepContentAndOffsetUntilRetryOrTrueEmptySucceeds() async throws {
        let first: RuntimeValue = .object(["id": .number(0), "title": .string("健康前项")])
        let last: RuntimeValue = .object(["id": .number(1), "title": .string("健康后项")])
        let rows = [first] + Array(repeating: RuntimeValue.object([:]), count: 22) + [last]
        let fixture = FilmMetadataHTTPFixture(response: { _ in Self.entry(items: rows) })
        defer { fixture.close() }
        let client = try fixture.connection()
        addTeardownBlock { await client.close() }
        let model = FilmCategoryModel(category: .movie, source: DoubanFilmSource { client })
        defer { model.close() }
        let initial = try XCTUnwrap(model.loadIfNeeded()); await initial.value
        XCTAssertEqual(model.items.map(\.id), ["0", "1"])
        XCTAssertTrue(model.hasMore)
        let original = model.items
        for count in [1, 24] {
            fixture.setResponse(Self.entry(items: Array(repeating: .object([:]), count: count)))
            let failed = try XCTUnwrap(model.loadMore()); await failed.value
            XCTAssertEqual(model.error, .invalidResponse)
            XCTAssertEqual(model.items, original)
            XCTAssertTrue(model.hasMore); XCTAssertFalse(model.isLoading)
        }
        let next: RuntimeValue = .object(["id": .number(2), "title": .string("重试成功")])
        fixture.setResponse(Self.entry(items: [last] + Array(repeating: .object([:]), count: 22) + [next]))
        let retry = try XCTUnwrap(model.loadMore()); await retry.value
        XCTAssertNil(model.error)
        XCTAssertEqual(model.items.map(\.id), ["0", "1", "2"])
        XCTAssertTrue(model.hasMore)
        let expanded = model.items
        fixture.setResponse(Self.entry(items: Array(repeating: .object([:]), count: 24)))
        let failedRefresh = try XCTUnwrap(model.refresh()); await failedRefresh.value
        XCTAssertEqual(model.error, .invalidResponse)
        XCTAssertEqual(model.items, expanded)
        XCTAssertTrue(model.hasMore)
        fixture.setResponse(Self.entry(items: []))
        let end = try XCTUnwrap(model.loadMore()); await end.value
        XCTAssertNil(model.error)
        XCTAssertEqual(model.items, expanded)
        XCTAssertFalse(model.hasMore)
        XCTAssertNil(model.loadMore())
        let emptyRefresh = try XCTUnwrap(model.refresh()); await emptyRefresh.value
        XCTAssertNil(model.error)
        XCTAssertTrue(model.items.isEmpty); XCTAssertFalse(model.hasMore)
        XCTAssertEqual(fixture.inputs.compactMap { $0["query"]?.filmObject?["start"]?.filmInteger },
                       [0, 24, 24, 24, 0, 48, 0])
        XCTAssertEqual(fixture.inputs.map { $0["refresh"] == .bool(true) },
                       [false, false, false, false, true, false, true])
    }

    func testSourcePropagatesRuntimeFailureInsteadOfReturningAnEmptyHome() async throws {
        let fixture = FilmMetadataHTTPFixture(response: { _ in throw RuntimeHTTPError.remote("UPSTREAM_HTTP") })
        defer { fixture.close() }
        let client = try fixture.connection()
        addTeardownBlock { await client.close() }
        let source = DoubanFilmSource { client }
        do {
            _ = try await source.loadHome()
            XCTFail("An upstream failure must not look like an empty success.")
        } catch {
            XCTAssertEqual(error as? RuntimeHTTPError, .remote("UPSTREAM_HTTP"))
        }
    }

    func testCategoryUsesBothMovieFiltersAndKeepsFullPagesAndRawOffsets() async throws {
        let fixture = FilmMetadataHTTPFixture(response: { _ in
            var rows: [RuntimeValue] = (0..<23).map {
                .object(["id": .number(Double($0)), "title": .string("影片 \($0)")])
            }
            rows.append(.object(["id": .number(0), "title": .string("重复影片")]))
            return Self.entry(items: rows)
        })
        defer { fixture.close() }
        let client = try fixture.connection()
        addTeardownBlock { await client.close() }
        let source = DoubanFilmSource { client }
        let page = try await source.loadCategory(.movie, filters: ["category": "豆瓣高分", "area": "日本"], offset: 24)
        XCTAssertEqual(page.items.count, 23, "Category results must not be truncated to the home preview's 12 cards.")
        XCTAssertEqual(page.nextOffset, 48, "Duplicate or invalid rows still consume an upstream offset.")
        let input = try XCTUnwrap(fixture.inputs.first)
        XCTAssertEqual(input["path"], .string("/rexxar/api/v2/subject/recent_hot/movie"))
        XCTAssertEqual(input["query"], .object(["category": .string("豆瓣高分"), "type": .string("日本"),
            "start": .number(24), "limit": .number(24)]))
        XCTAssertNil(input["refresh"])
    }

    func testCategoryRoutesKeepExistingAnimeSourceAndShortPageEndsPagination() async throws {
        let definitions: [(FilmCategory, [String: String], String, String)] = [
            (.tv, ["area": "tv_japanese"], "tv", "tv_japanese"),
            (.tv, ["category": "tv_documentary", "area": "tv_japanese"], "tv", "tv_documentary"),
            (.anime, [:], "tv", "tv_animation"),
            (.show, ["area": "show_foreign"], "show", "show_foreign")
        ]
        let fixture = FilmMetadataHTTPFixture()
        defer { fixture.close() }
        let client = try fixture.connection()
        addTeardownBlock { await client.close() }
        let source = DoubanFilmSource { client }
        for (category, filters, expectedCategory, expectedType) in definitions {
            let page = try await source.loadCategory(category, filters: filters, offset: 0)
            XCTAssertTrue(page.items.isEmpty)
            XCTAssertNil(page.nextOffset)
            let input = try XCTUnwrap(fixture.inputs.last)
            XCTAssertEqual(input["path"], .string("/rexxar/api/v2/subject/recent_hot/tv"))
            XCTAssertEqual(input["query"], .object(["category": .string(expectedCategory), "type": .string(expectedType),
                "start": .number(0), "limit": .number(24)]))
        }
    }

    func testAuxiliaryCacheChangesDoNotDiscardAUsableHomeOrCategory() async throws {
        for cache in [
            RuntimeValue?.none,
            .object(["status": .string("future-status"), "components": .object([:]),
                     "error": .object(["code": .array([])])]),
            .object(["status": .string("fresh"), "components": .string("unusable")]),
        ] {
            let fixture = FilmMetadataHTTPFixture(response: { _ in
                var fields = try Self.entry(items: [
                    .object(["id": .string("100"), "title": .string("主业务")]),
                ]).filmRequiredObject()
                fields["cache"] = cache
                return .object(fields)
            })
            defer { fixture.close() }
            let client = try fixture.connection()
            addTeardownBlock { await client.close() }
            let source = DoubanFilmSource { client }
            let home = try await source.loadHome()
            XCTAssertEqual(home.map { $0.items.map(\.id) }, Array(repeating: ["100"], count: 4))
            XCTAssertTrue(home.allSatisfy { $0.cache?.status == .stale && $0.cache?.errorCode == nil })
            let category = try await source.loadCategory(.movie, filters: [:], offset: 0)
            XCTAssertEqual(category.items.map(\.id), ["100"])
            XCTAssertEqual(category.cache?.status, .stale)
        }
    }

    func testExplicitRefreshAndStaleEntryPreserveContentErrorAndRuntimeImageReferences() async throws {
        let fixture = FilmMetadataHTTPFixture(response: { _ in
            Self.entry(items: [.object(["id": .string("100"), "title": .string("旧资料"), "pic": .object([
                "normal": .string("/v1/douban/images/view/photo/s_ratio_poster/public/p100.jpg"),
                "large": .string("/v1/douban/images/view/photo/raw/public/p100.jpg")
            ])])], status: "stale", error: "NETWORK_ERROR")
        })
        defer { fixture.close() }
        let client = try fixture.connection()
        addTeardownBlock { await client.close() }
        let source = DoubanFilmSource { client }
        let home = try await source.loadHome(refresh: true)
        XCTAssertTrue(home.allSatisfy { $0.cache?.status == .stale && $0.cache?.errorCode == "NETWORK_ERROR" })
        let page = try await source.loadCategory(.anime, filters: [:], offset: 0, refresh: true)
        XCTAssertEqual(page.items.first?.id, "100")
        XCTAssertEqual(page.cache?.status, .stale)
        XCTAssertEqual(page.cache?.errorCode, "NETWORK_ERROR")
        XCTAssertEqual(page.cache?.components["items"], .object(["state": .string("ready")]))
        XCTAssertEqual(page.items.first?.posterURL?.absoluteString,
                       "/v1/douban/images/view/photo/s_ratio_poster/public/p100.jpg")
        XCTAssertEqual(page.items.first?.largePosterURL?.absoluteString,
                       "/v1/douban/images/view/photo/raw/public/p100.jpg")
        XCTAssertTrue(fixture.inputs.allSatisfy { $0["refresh"] == .bool(true) })
        XCTAssertEqual(fixture.inputs.count, 5)
        _ = try await source.loadCategory(.anime, filters: [:], offset: 0)
        XCTAssertNil(fixture.inputs.last?["refresh"])
        XCTAssertEqual(fixture.inputs.count, 6, "The source must consult Node even when it returned an old entry before.")
    }

    private static func entry(items: [RuntimeValue], status: String = "fresh", error: String? = nil) -> RuntimeValue {
        let items = items.map { value -> RuntimeValue in
            guard var row = value.filmObject, let id = row["id"]?.filmScalar, !id.isEmpty,
                  let title = row["title"]?.filmText, !title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                return value
            }
            let target: RuntimeObject = ["id": .string("fixture-" + id), "kind": .string("movie"),
                "title": .string(title), "metadataProvider": .string("douban"), "metadataId": .string(id)]
            row["businessId"] = row["businessId"] ?? target["id"]
            row["playbackTarget"] = row["playbackTarget"] ?? .object(target)
            return .object(row)
        }
        var cache: RuntimeObject = ["status": .string(status),
            "components": .object(["items": .object(["state": .string("ready")])])]
        cache["error"] = error.map { .object(["code": .string($0)]) }
        return .object(["value": .object(["items": .array(items)]), "completeness": .string("complete"),
            "fetchedAt": .number(1), "expiresAt": .number(2), "cache": .object(cache)])
    }
}
