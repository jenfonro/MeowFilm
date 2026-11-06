import Foundation
import XCTest
import MeowJSON
@testable import MeowFilmFeature

final class FilmSourceProbeTests: XCTestCase {
    private func probe(_ runtime: SourcesRuntimeFixture, actions: [String] = ["home", "category", "detail", "play", "search"]) throws -> FilmSourceProbe {
        let cat = try parseFilmCat(SourcesRuntimeFixture.cat("a"))
        return FilmSourceProbe(transport: .init(rpc: { method, params, _ in try await runtime.rpc(method, params) }),
            cat: cat, site: .init(key: .init(installationId: "a", siteKey: "same"), name: "站点", api: "/api/a",
                actions: actions, methods: [:]))
    }

    func testUnsupportedEmptyAndFailedStagesAreDistinct() async throws {
        let unsupported = SourcesRuntimeFixture()
        let noSearch = try await probe(unsupported, actions: ["home", "category", "detail", "play"]).run()
        XCTAssertEqual(noSearch.result, "search_error")
        XCTAssertTrue(noSearch.steps.contains(.init("search", "unsupported", "CAPABILITY_UNAVAILABLE")))

        let empty = SourcesRuntimeFixture()
        await empty.setReply("category", .object(["list": .array([])]))
        let fallback = try await probe(empty).run()
        XCTAssertEqual(fallback.result, "category_error")
        XCTAssertTrue(fallback.steps.contains(.init("category", "empty", "EMPTY_LIST")))
        XCTAssertTrue(fallback.playbackResolved)

        let failed = SourcesRuntimeFixture()
        await failed.setReply("home", .object(["statusCode": .number(503)]))
        await failed.setReply("search", .object(["success": .bool(false), "message": .string("fixture failure")]))
        let failure = try await probe(failed).run()
        XCTAssertEqual(failure.result, "invalid")
        XCTAssertTrue(failure.steps.contains(.init("home", "failed", "HTTP_503")))
        XCTAssertTrue(failure.steps.contains(.init("search", "failed", "fixture failure")))
    }

    func testPlayFailureWithMetadataDoesNotTriggerSearchFallback() async throws {
        let runtime = SourcesRuntimeFixture()
        await runtime.setReply("play", .object(["url": .string("")]))
        let result = try await probe(runtime).run()
        XCTAssertEqual(result.result, "invalid")
        XCTAssertTrue(result.steps.contains(.init("play", "empty", "MISSING_PLAY_URL")))
        let actions = await runtime.invocations.map(\.0)
        XCTAssertFalse(actions.contains("search"))
    }

    func testPanResourceIsUnsupportedAndNeverDeclaredMediaPlayback() async throws {
        let runtime = SourcesRuntimeFixture()
        await runtime.setReply("play", .object(["url": .string("meow.pan.v1.fixture")]))
        let result = try await probe(runtime).run()
        XCTAssertFalse(result.playbackResolved)
        XCTAssertFalse(result.mediaPlaybackVerified)
        XCTAssertTrue(result.steps.contains(.init("play", "unsupported", "PAN_RESOLVER_UNAVAILABLE")))
    }

    func testDeclaredLinesUseFirstEpisodeAndNestedResponseShapes() async throws {
        let runtime = SourcesRuntimeFixture()
        await runtime.setReply("category", .object(["data": .object(["items": .array([.object([
            "vod_play_from": .string("一|||二$$$三"),
            "vod_play_url": .string("首集$a#次集$unused|||首集$b$$$首集$c")])])])]))
        await runtime.setReply("play", .object(["url": .string("")]))
        let result = try await probe(runtime).run()
        XCTAssertEqual(result.result, "invalid")
        let play = await runtime.invocations.filter { $0.0 == "play" }.map(\.1)
        XCTAssertEqual(play.compactMap { $0.catField("flag")?.catText }, ["一", "二", "三"])
        XCTAssertEqual(play.compactMap { $0.catField("id")?.catText }, ["a", "b", "c"])
    }

    func testNavigationFollowsDeclaredActionAndAdvancingPages() async throws {
        let runtime = SourcesRuntimeFixture()
        await runtime.setReply("detail", .object(["list": .array([folder("folder", action: "directory", payload: ["id": .string("folder"), "pg": .number(1)])])]))
        await runtime.setRoutedReply("directory", id: "folder", page: 1, reply: .object([
            "page": .number(1), "pagecount": .number(2), "list": .array([.object(["vod_id": .string("empty")])])]))
        await runtime.setRoutedReply("directory", id: "folder", page: 2, reply: .object([
            "page": .number(2), "pagecount": .number(2), "list": .array([.object([
                "vod_play_from": .string("线路"), "vod_play_url": .string("第一集$nested")])])]))
        let result = try await probe(runtime, actions: ["home", "category", "detail", "play", "search", "directory"]).run()
        XCTAssertEqual(result.result, "valid")
        let invocations = await runtime.invocations
        let pages = invocations.filter { $0.0 == "directory" }.map { $0.1.catField("pg") }
        XCTAssertEqual(pages, [.number(1), .number(2)])
        XCTAssertEqual(invocations.first { $0.0 == "play" }?.1.catField("id"), .string("nested"))
    }

    func testNavigationCycleAndRepeatedPageAreReportedWithoutUnboundedTraversal() async throws {
        for repeated in [false, true] {
            let runtime = SourcesRuntimeFixture()
            let folder = folder("folder", action: "directory", payload: ["id": .string("folder"), "page": .number(1)])
            await runtime.setReply("detail", .object(["list": .array([folder])]))
            if repeated {
                let leaf = JSONValue.object(["vod_id": .string("leaf")])
                await runtime.setRoutedReply("directory", id: "folder", page: 1,
                    reply: .object(["list": .array([leaf]), "page": .number(1), "pagecount": .number(3)]))
                await runtime.setRoutedReply("directory", id: "folder", page: 2,
                    reply: .object(["list": .array([leaf]), "page": .number(2), "pagecount": .number(3)]))
            } else { await runtime.setReply("directory", .object(["list": .array([folder])])) }
            await runtime.setReply("search", .object(["list": .array([])]))
            let result = try await probe(runtime, actions: ["home", "category", "detail", "play", "search", "directory"]).run()
            XCTAssertTrue(result.steps.contains(.init("directory", "failed", repeated ? "NAVIGATION_REPEATED_PAGE" : "NAVIGATION_CYCLE")))
            let calls = await runtime.invocations.filter { $0.0 == "directory" }
            XCTAssertEqual(calls.count, repeated ? 2 : 1)
        }
    }

    private func folder(_ id: String, action: String, payload: [String: JSONValue]) -> JSONValue {
        .object(["vod_id": .string(id), "vod_navigation": .object(["action": .string(action), "payload": .object(payload)])])
    }
}
