import Foundation
import XCTest
@testable import MeowFilmFeature

final class DoubanFilmSourceTests: XCTestCase {
    func testFourHomeColumnsUseWindowsRoutesAndKeepOrderWhenResponsesFinishInReverse() async throws {
        let expected = [
            (id: "movie", title: "热门电影", kind: "movie", category: "热门", type: "全部"),
            (id: "tv", title: "热门剧集", kind: "tv", category: "tv", type: "tv"),
            (id: "anime", title: "新番放送", kind: "tv", category: "tv", type: "tv_animation"),
            (id: "show", title: "热门综艺", kind: "tv", category: "show", type: "show")
        ]
        let started = expectation(description: "four bounded requests started")
        started.expectedFulfillmentCount = 4
        let stubs = expected.map { section in
            FilmHTTPStub(url: address(kind: section.kind, category: section.category, type: section.type),
                         bodyChunks: [Data(#"{"items":[{"id":100,"title":"真实首页项"}]}"#.utf8)],
                         held: true, started: { started.fulfill() })
        }
        defer { stubs.forEach { $0.close() } }
        let source = DoubanFilmSource(http: FilmHTTP(configuration: FilmHTTPStub.configuration()))
        XCTAssertEqual(source.descriptor, FilmSourceDescriptor(id: "douban", title: "豆瓣"))
        let load = Task { try await source.loadHome() }
        await fulfillment(of: [started], timeout: 3)
        for stub in stubs.reversed() { stub.finish() }
        let sections = try await load.value
        XCTAssertEqual(sections.map(\.id), expected.map(\.id))
        XCTAssertEqual(sections.map(\.title), expected.map(\.title))
        XCTAssertEqual(sections.map { $0.items.first?.sourceID }, ["douban", "douban", "douban", "douban"])
        XCTAssertEqual(sections.map { $0.items.first?.id }, ["100", "100", "100", "100"])
        for (definition, stub) in zip(expected, stubs) {
            let request = try XCTUnwrap(stub.requests.first)
            XCTAssertEqual(stub.requests.count, 1)
            let url = try XCTUnwrap(request.url)
            XCTAssertEqual(url.scheme, "https")
            XCTAssertEqual(url.host, "m.douban.com")
            XCTAssertEqual(url.path, "/rexxar/api/v2/subject/recent_hot/" + definition.kind)
            let parts = try XCTUnwrap(URLComponents(url: url, resolvingAgainstBaseURL: false))
            let query = Dictionary(uniqueKeysWithValues: (parts.queryItems ?? []).map { ($0.name, $0.value ?? "") })
            XCTAssertEqual(query, ["start": "0", "limit": "12",
                                   "category": definition.category, "type": definition.type])
            XCTAssertEqual(request.httpMethod, "GET")
            XCTAssertEqual(request.timeoutInterval, 15)
            XCTAssertEqual(request.value(forHTTPHeaderField: "User-Agent"), "Mozilla/5.0")
            XCTAssertEqual(request.value(forHTTPHeaderField: "Accept"), "application/json, text/plain, */*")
            XCTAssertEqual(request.value(forHTTPHeaderField: "Referer"), "https://m.douban.com/")
            XCTAssertEqual(request.value(forHTTPHeaderField: "Origin"), "https://m.douban.com")
            XCTAssertFalse(request.httpShouldHandleCookies)
            XCTAssertNil(request.value(forHTTPHeaderField: "Cookie"))
            XCTAssertNil(request.value(forHTTPHeaderField: "Authorization"))
        }
    }

    func testParsingUsesUpstreamIdentityPosterFallbacksAndNormalizedScores() throws {
        let payload = Data(#"""
        {"items":[
          {"id":100,"title":" 第一部 ","pic":{"normal":"http://img1.doubanio.com/a.jpg",
            "large":"https://img2.doubanio.com/a-large.jpg"},"rating":{"value":8.6},"card_subtitle":"真实副标题"},
          {"id":"blank","title":"   "},
          {"id":"300","title":"第二部","pic":{"large":"https://img2.doubanio.com/large.jpg"},"rating":{"value":0}},
          {"id":400,"title":"第三部","cover":{"url":"//img3.doubanio.com/cover.jpg"},"rating":{"value":"9.0"}},
          {"id":"500","title":"第四部","cover_url":"https://user@artwork.invalid/private.jpg","rating":{"value":"NaN"}},
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

    func testEmptyItemsAreValidButMissingItemsAndInvalidJSONAreErrors() throws {
        XCTAssertEqual(try DoubanFilmSource.parse(Data(#"{"items":[]}"#.utf8)), [])
        for payload in ["[]", "{}", #"{"items":null}"#, #"{"items":{}}"#, "invalid JSON"] {
            XCTAssertThrowsError(try DoubanFilmSource.parse(Data(payload.utf8))) {
                XCTAssertEqual($0 as? FilmHomeError, .invalidResponse)
            }
        }
    }

    func testSourcePropagatesHTTPFailureInsteadOfReturningAnEmptyHome() async throws {
        let definitions = [("movie", "热门", "全部"), ("tv", "tv", "tv"),
                           ("tv", "tv", "tv_animation"), ("tv", "show", "show")]
        let stubs = definitions.enumerated().map { index, definition in
            FilmHTTPStub(url: address(kind: definition.0, category: definition.1, type: definition.2),
                         status: index == 0 ? 503 : 200,
                         bodyChunks: [Data(#"{"items":[]}"#.utf8)])
        }
        defer { stubs.forEach { $0.close() } }
        let source = DoubanFilmSource(http: FilmHTTP(configuration: FilmHTTPStub.configuration()))
        do {
            _ = try await source.loadHome()
            XCTFail("An upstream failure must not look like an empty success.")
        } catch {
            XCTAssertEqual(error as? FilmHomeError, .upstream(statusCode: 503))
        }
    }

    func testCategoryUsesBothMovieFiltersAndKeepsFullPagesAndRawOffsets() async throws {
        let url = DoubanFilmSource.categoryURL(.movie, filters: ["category": "豆瓣高分", "area": "日本"], offset: 24)
        let query = Dictionary(uniqueKeysWithValues: URLComponents(url: url, resolvingAgainstBaseURL: false)!.queryItems!.map {
            ($0.name, $0.value!)
        })
        XCTAssertEqual(query, ["category": "豆瓣高分", "type": "日本", "start": "24", "limit": "24"])
        var rows: [[String: Any]] = (0..<23).map { ["id": $0, "title": "影片 \($0)"] }
        rows.append(["id": 0, "title": "重复影片"])
        let stub = FilmHTTPStub(url: url, status: 200, bodyChunks: [try JSONSerialization.data(withJSONObject: ["items": rows])])
        defer { stub.close() }
        let source = DoubanFilmSource(http: FilmHTTP(configuration: FilmHTTPStub.configuration()))
        let page = try await source.loadCategory(.movie, filters: ["category": "豆瓣高分", "area": "日本"], offset: 24)
        XCTAssertEqual(page.items.count, 23, "Category results must not be truncated to the home preview's 12 cards.")
        XCTAssertEqual(page.nextOffset, 48, "Duplicate or invalid rows still consume an upstream offset.")
    }

    func testCategoryRoutesKeepExistingAnimeSourceAndShortPageEndsPagination() async throws {
        let definitions: [(FilmCategory, [String: String], String, String)] = [
            (.tv, ["area": "tv_japanese"], "tv", "tv_japanese"),
            (.tv, ["category": "tv_documentary", "area": "tv_japanese"], "tv", "tv_documentary"),
            (.anime, [:], "tv", "tv_animation"),
            (.show, ["area": "show_foreign"], "show", "show_foreign")
        ]
        for (category, filters, expectedCategory, expectedType) in definitions {
            let url = DoubanFilmSource.categoryURL(category, filters: filters, offset: 0)
            let query = Dictionary(uniqueKeysWithValues: URLComponents(url: url, resolvingAgainstBaseURL: false)!.queryItems!.map {
                ($0.name, $0.value!)
            })
            XCTAssertEqual(query["category"], expectedCategory)
            XCTAssertEqual(query["type"], expectedType)
            let stub = FilmHTTPStub(url: url, status: 200, bodyChunks: [Data(#"{"items":[]}"#.utf8)])
            let source = DoubanFilmSource(http: FilmHTTP(configuration: FilmHTTPStub.configuration()))
            let page = try await source.loadCategory(category, filters: filters, offset: 0)
            XCTAssertTrue(page.items.isEmpty)
            XCTAssertNil(page.nextOffset)
            stub.close()
        }
    }

    private func address(kind: String, category: String, type: String) -> URL {
        var parts = URLComponents(string: "https://m.douban.com/rexxar/api/v2/subject/recent_hot/" + kind)!
        parts.queryItems = [URLQueryItem(name: "start", value: "0"), URLQueryItem(name: "limit", value: "12"),
                            URLQueryItem(name: "category", value: category), URLQueryItem(name: "type", value: type)]
        return parts.url!
    }
}
