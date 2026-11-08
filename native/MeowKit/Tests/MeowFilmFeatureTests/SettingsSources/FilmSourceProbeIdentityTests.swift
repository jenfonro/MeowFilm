import Foundation
import XCTest
import MeowRuntime
import MeowStorage
@testable import MeowFilmFeature

final class FilmSourceProbeIdentityTests: XCTestCase {
    private let composed = "\u{00e9}"
    private let decomposed = "e\u{0301}"

    private func probe(_ fixture: ProbeIdentityFixture) throws -> FilmSourceProbe {
        let cat = try parseFilmCat(SourcesRuntimeFixture.cat("a"))
        return FilmSourceProbe(transport: fixture.transport, cat: cat,
            site: .init(key: .init(installationId: "a", siteKey: "same"), name: "Site", api: "/api/a",
                actions: ["home", "category", "detail", "play", "search", "directory"], methods: [:]))
    }

    func testCategoryExactIDsAndFlagsPersistValidWithoutChangingOptions() async throws {
        for differingFlag in [false, true] {
            let first = composed, second = decomposed
            let flags = differingFlag ? [first, first, second] : ["line", "line", "line"]
            let ids = differingFlag ? ["episode", "episode", "episode"] : [first, first, second]
            let fixture = ProbeIdentityFixture { action, payload in
                if action == "category" { return identityList([identityVideo(flags: flags, ids: ids)]) }
                if action == "play" {
                    let field = differingFlag ? "flag" : "id"
                    let matches = payload.catField(field)?.catText?.utf8.elementsEqual(second.utf8) == true
                    return .object(["url": .string(matches ? "https://example.invalid/valid.m3u8" : "")])
                }
                return nil
            }
            let base = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
            try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
            defer { try? FileManager.default.removeItem(at: base) }
            let store = try DocumentStore(baseURL: base, namespace: FilmSourceService.documentNamespace)
            let service = FilmSourceService(transport: fixture.transport, storeFactory: { store })
            let key = FilmSourceKey(installationId: "a", siteKey: "same")
            let options = FilmVideoOptions(enabled: false, homeVisible: false, searchEnabled: false, aggregateImages: true)
            try await service.refresh()
            try await service.saveVideoOptions(key, options: options)
            let result = try await service.detect(key)
            XCTAssertEqual(result.result, "valid")
            XCTAssertTrue(result.playbackResolved)
            let calls = await fixture.calls
            let plays = calls.filter { $0.0 == "play" }.map(\.1)
            XCTAssertEqual(plays.count, 2, "The exact duplicate fails once; the distinct candidate must still run")
            XCTAssertEqual(plays.map { identityBytes($0, "flag") }, [Array(flags[0].utf8), Array(flags[2].utf8)])
            XCTAssertEqual(plays.map { identityBytes($0, "id") }, [Array(ids[0].utf8), Array(ids[2].utf8)])
            let snapshot = await service.snapshot
            XCTAssertEqual(snapshot.videos.first { $0.key == key }?.options, options)
            XCTAssertEqual(snapshot.videos.first { $0.key == key }?.detection, result)
            await service.close()
            let rebuilt = FilmSourceService(transport: fixture.transport, storeFactory: { store })
            try await rebuilt.initialize()
            let restored = await rebuilt.snapshot
            XCTAssertEqual(restored.videos.first { $0.key == key }?.detection, result)
            XCTAssertEqual(restored.videos.first { $0.key == key }?.options, options)
            await rebuilt.close()
        }
    }

    func testDetailNavigationCandidateMergeKeepsExactIDsAndFlags() async throws {
        for differingFlag in [false, true] {
            let first = composed, second = decomposed
            let fixture = ProbeIdentityFixture { action, payload in
                if action == "detail" { return identityList([identityFolder("one"), identityFolder("two")]) }
                if action == "directory" {
                    let isFirst = payload.catField("id")?.catText == "one"
                    let values = isFirst ? [first] : [first, second]
                    return identityList([identityVideo(flags: differingFlag ? values : values.map { _ in "line" },
                        ids: differingFlag ? values.map { _ in "episode" } : values)])
                }
                if action == "play" {
                    let field = differingFlag ? "flag" : "id"
                    let matches = payload.catField(field)?.catText?.utf8.elementsEqual(second.utf8) == true
                    return .object(["url": .string(matches ? "https://example.invalid/valid.m3u8" : "")])
                }
                return nil
            }
            let result = try await probe(fixture).run()
            XCTAssertEqual(result.result, "valid")
            let calls = await fixture.calls
            let plays = calls.filter { $0.0 == "play" }.map(\.1)
            XCTAssertEqual(plays.count, 2, "The duplicate from a second branch is removed, but the distinct candidate remains")
            XCTAssertEqual(plays.map { identityBytes($0, differingFlag ? "flag" : "id") },
                [Array(first.utf8), Array(second.utf8)])
            XCTAssertEqual(plays.map { identityBytes($0, differingFlag ? "id" : "flag") },
                Array(repeating: Array((differingFlag ? "episode" : "line").utf8), count: 2))
        }
    }

    func testNavigationSiblingPayloadsKeepExactIdentityAndSkipTrueDuplicate() async throws {
        let first = composed, second = decomposed
        let fixture = ProbeIdentityFixture { action, payload in
            if action == "detail" {
                return identityList([identityFolder(first), identityFolder(first), identityFolder(second)])
            }
            if action == "directory" {
                let isSecond = payload.catField("id")?.catText?.utf8.elementsEqual(second.utf8) == true
                return identityList(isSecond ? [identityVideo(flags: ["line"], ids: ["episode"])] : [])
            }
            return nil
        }
        let result = try await probe(fixture).run()
        XCTAssertEqual(result.result, "valid")
        let calls = await fixture.calls
        XCTAssertEqual(calls.filter { $0.0 == "directory" }.map { identityBytes($0.1, "id") },
            [Array(first.utf8), Array(second.utf8)])
        XCTAssertFalse(result.steps.contains { $0.error == "NAVIGATION_CYCLE" })
    }

    func testNavigationAncestorPayloadsKeepExactIdentity() async throws {
        let first = composed, second = decomposed
        let fixture = ProbeIdentityFixture { action, payload in
            if action == "detail" { return identityList([identityFolder(first)]) }
            if action == "directory" {
                let isFirst = payload.catField("id")?.catText?.utf8.elementsEqual(first.utf8) == true
                return identityList(isFirst ? [identityFolder(second)] : [identityVideo(flags: ["line"], ids: ["episode"])])
            }
            return nil
        }
        let result = try await probe(fixture).run()
        XCTAssertEqual(result.result, "valid")
        let calls = await fixture.calls
        XCTAssertEqual(calls.filter { $0.0 == "directory" }.map { identityBytes($0.1, "id") },
            [Array(first.utf8), Array(second.utf8)])
        XCTAssertFalse(result.steps.contains { $0.error == "NAVIGATION_CYCLE" })
    }

    func testNavigationPayloadKeysSurviveStringParsingAndSkipOnlyExactDuplicateObjects() async throws {
        let first = try RuntimeJSON.decode(Data(#"{"id":"directory","page":1,"é":1,"e\u0301":2,"blob":{"token":"业务值"}}"#.utf8))
        let reordered = try RuntimeJSON.decode(Data(#"{"blob":{"token":"业务值"},"e\u0301":2,"é":1,"page":1,"id":"directory"}"#.utf8))
        let second = try RuntimeJSON.decode(Data(#"{"id":"directory","page":1,"é":2,"e\u0301":1,"blob":{"token":"业务值"}}"#.utf8))
        let detail = identityList([first, reordered, second].map { payload in
            .object(["vod_id": .string("directory"), "vod_navigation": .object([
                "action": .string("directory"), "payload": payload])])
        })
        for textResult in [false, true] {
            let response: RuntimeValue = textResult ? .string(String(decoding: try RuntimeJSON.encode(detail), as: UTF8.self)) : detail
            let fixture = ProbeIdentityFixture { action, payload in
                if action == "detail" { return response }
                if action == "directory" {
                    let matches = payload.catField("é") == .number(2) && payload.catField("e\u{0301}") == .number(1)
                    return identityList(matches ? [identityVideo(flags: ["line"], ids: ["episode"])] : [])
                }
                return nil
            }
            let result = try await probe(fixture).run()
            XCTAssertEqual(result.result, "valid")
            let calls = await fixture.calls
            let payloads = calls.filter { $0.0 == "directory" }.map(\.1)
            XCTAssertEqual(payloads.count, 2, "Object order does not create another request; exact Unicode keys remain distinct")
            XCTAssertEqual(payloads.map { $0.catField("é") }, [.number(1), .number(2)])
            XCTAssertEqual(payloads.map { $0.catField("e\u{0301}") }, [.number(2), .number(1)])
            XCTAssertTrue(payloads.allSatisfy { $0.catField("blob")?.catField("token") == .string("业务值") })
            XCTAssertFalse(result.steps.contains { $0.error == "NAVIGATION_CYCLE" })
        }
    }

    func testConsecutivePageListsWithDifferentRawTextAreNotRepeatedPages() async throws {
        let first = composed, second = decomposed
        let fixture = ProbeIdentityFixture { action, payload in
            if action == "detail" { return identityList([identityFolder("pages")]) }
            if action == "directory" {
                let page = payload.catField("page")?.catInteger ?? 1
                let items: [RuntimeValue]
                if page < 3 { items = [.object(["vod_id": .string(page == 1 ? first : second)])] }
                else { items = [identityVideo(flags: ["line"], ids: ["episode"])] }
                return .object(["list": .array(items), "page": .number(Double(page)), "pagecount": .number(3)])
            }
            return nil
        }
        let result = try await probe(fixture).run()
        XCTAssertEqual(result.result, "valid")
        let calls = await fixture.calls
        let pages = calls.filter { $0.0 == "directory" }.map(\.1)
        XCTAssertEqual(pages.map { $0.catField("page")?.catInteger }, [1, 2, 3])
        XCTAssertEqual(pages.map { identityBytes($0, "id") }, Array(repeating: Array("pages".utf8), count: 3))
        XCTAssertFalse(result.steps.contains { $0.error == "NAVIGATION_REPEATED_PAGE" })
    }
    func testLeadingCombiningEpisodeIDFromCategoryAndDetailPersistsExactResult() async throws {
        for fromDetail in [false, true] {
            let id = "\u{0301}episode"
            let fixture = ProbeIdentityFixture { action, payload in
                if action.utf8.elementsEqual((fromDetail ? "detail" : "category").utf8) {
                    return identityList([identityVideo(flags: ["line"], ids: [id])])
                }
                if action.utf8.elementsEqual("play".utf8) {
                    let matches = identityBytes(payload, "flag") == Array("line".utf8) &&
                        identityBytes(payload, "id") == Array(id.utf8)
                    return .object(["url": .string(matches ? "https://example.invalid/valid.m3u8" : "")])
                }
                return nil
            }
            let base = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
            try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
            defer { try? FileManager.default.removeItem(at: base) }
            let store = try DocumentStore(baseURL: base, namespace: FilmSourceService.documentNamespace)
            let service = FilmSourceService(transport: fixture.transport, storeFactory: { store })
            let key = FilmSourceKey(installationId: "a", siteKey: "same")
            let options = FilmVideoOptions(enabled: false, homeVisible: false, searchEnabled: false, aggregateImages: true)
            try await service.refresh()
            try await service.saveVideoOptions(key, options: options)
            let result = try await service.detect(key)
            XCTAssertEqual(Array(result.result.utf8), Array("valid".utf8))
            XCTAssertTrue(result.playbackResolved)
            let calls = await fixture.calls
            let plays = calls.filter { $0.0.utf8.elementsEqual("play".utf8) }.map(\.1)
            XCTAssertEqual(plays.map { identityBytes($0, "flag") }, [Array("line".utf8)])
            XCTAssertEqual(plays.map { identityBytes($0, "id") }, [Array(id.utf8)])
            XCTAssertEqual(calls.map { Array($0.0.utf8) },
                (fromDetail ? ["home", "category", "detail", "play", "search"] :
                    ["home", "category", "play", "search"]).map { Array($0.utf8) })
            let snapshot = await service.snapshot
            XCTAssertEqual(snapshot.videos.first { $0.key == key }?.detection, result)
            XCTAssertEqual(snapshot.videos.first { $0.key == key }?.options, options)
            await service.close()
            let rebuilt = FilmSourceService(transport: fixture.transport, storeFactory: { store })
            try await rebuilt.initialize()
            let restored = await rebuilt.snapshot
            let saved = try XCTUnwrap(restored.videos.first { $0.key == key })
            XCTAssertEqual(saved.detection, result)
            XCTAssertEqual(Array(try XCTUnwrap(saved.detection).result.utf8), Array("valid".utf8))
            XCTAssertTrue(try XCTUnwrap(saved.detection).playbackResolved)
            XCTAssertEqual(saved.options, options)
            await rebuilt.close()
        }
    }

    func testLineSeparatorsPreserveLeadingCombiningFlagsAndUntitledIDs() async throws {
        for separator in ["$$$", "|||"] {
            let flag = "\u{0301}line", id = "\u{0301}episode"
            // The first candidate fails, its true duplicate is skipped, and the
            // distinct second line succeeds only with both original parameters.
            for duplicate in [false, true] {
                try await assertLiteralCandidates(
                    flags: (duplicate ? ["first", "first", flag] : ["first", flag]).joined(separator: separator),
                    urls: (duplicate ? ["first-id", "first-id", id] : ["first-id", id]).joined(separator: separator),
                    expected: [("first", "first-id"), (flag, id)])
            }
        }
    }

    func testEpisodeSeparatorKeepsOnlyFirstEpisodeBeforeCombiningSecondEpisode() async throws {
        for first in ["首集$first-id", "first-id"] {
            for second in ["\u{0301}次集$second-id", "\u{0301}second-id"] {
                try await assertLiteralCandidates(flags: "line", urls: first + "#" + second,
                    expected: [("line", "first-id")])
            }
        }
    }

    func testLiteralCandidateControlsKeepEmptySegmentsDefaultsAndExactDeduplication() async throws {
        try await assertLiteralCandidates(
            flags: ["", "line", "", "", ""].joined(separator: "$$$"),
            urls: ["leading", "Title$ascii", "", "", "tail"].joined(separator: "$$$"),
            expected: [("源1", "leading"), ("line", "ascii"), ("源5", "tail")])
        try await assertLiteralCandidates(
            flags: "", urls: ["raw", "", "", "end"].joined(separator: "$$$"),
            expected: [("源1", "raw"), ("源4", "end")])
        try await assertLiteralCandidates(
            flags: ["", "line", "", "", ""].joined(separator: "$$$"),
            urls: ["", "Title$ascii", "", "", ""].joined(separator: "$$$"),
            expected: [("line", "ascii")])
        try await assertLiteralCandidates(
            flags: "line|||line|||line|||line",
            urls: ["", "Title$ascii", "", ""].joined(separator: "|||"),
            expected: [("line", "ascii")])
        try await assertLiteralCandidates(
            flags: "|||line", urls: "raw|||Title$ascii",
            expected: [("|||line", "raw"), ("line", "ascii")])
        try await assertLiteralCandidates(
            flags: "line|||line|||line|||line|||line",
            urls: ["start", "", "", "finish", ""].joined(separator: "|||"),
            expected: [("line", "start"), ("line", "finish")])
        try await assertLiteralCandidates(
            flags: "line$$$line$$$line$$$line",
            urls: ["Title$ascii", "ascii", "", "last"].joined(separator: "$$$"),
            expected: [("line", "ascii"), ("line", "last")])
        for emptyID in ["", "$", "Title$"] {
            let fixture = ProbeIdentityFixture { action, _ in
                if action.utf8.elementsEqual("category".utf8) {
                    return identityList([.object([
                        "vod_play_from": .string("line"), "vod_play_url": .string(emptyID)])])
                }
                return nil
            }
            let result = try await probe(fixture).run()
            XCTAssertEqual(Array(result.result.utf8), Array("invalid".utf8))
            XCTAssertFalse(result.playbackResolved)
            let calls = await fixture.calls
            XCTAssertEqual(calls.map { Array($0.0.utf8) }, ["home", "category"].map { Array($0.utf8) })
        }
    }

    private func assertLiteralCandidates(flags: String, urls: String, expected: [(String, String)],
                                         file: StaticString = #filePath, line: UInt = #line) async throws {
        let accepted = try XCTUnwrap(expected.last, file: file, line: line)
        let fixture = ProbeIdentityFixture { action, payload in
            if action.utf8.elementsEqual("category".utf8) {
                return identityList([.object(["vod_play_from": .string(flags), "vod_play_url": .string(urls)])])
            }
            if action.utf8.elementsEqual("play".utf8) {
                let matches = identityBytes(payload, "flag") == Array(accepted.0.utf8) &&
                    identityBytes(payload, "id") == Array(accepted.1.utf8)
                return .object(["url": .string(matches ? "https://example.invalid/valid.m3u8" : "")])
            }
            return nil
        }
        let result = try await probe(fixture).run()
        XCTAssertEqual(Array(result.result.utf8), Array("valid".utf8), file: file, line: line)
        XCTAssertTrue(result.playbackResolved, file: file, line: line)
        let calls = await fixture.calls
        let plays = calls.filter { $0.0.utf8.elementsEqual("play".utf8) }.map(\.1)
        XCTAssertEqual(plays.map { identityBytes($0, "flag") }, expected.map { Array($0.0.utf8) }, file: file, line: line)
        XCTAssertEqual(plays.map { identityBytes($0, "id") }, expected.map { Array($0.1.utf8) }, file: file, line: line)
        XCTAssertEqual(calls.map { Array($0.0.utf8) },
            (["home", "category"] + Array(repeating: "play", count: expected.count) + ["search"])
                .map { Array($0.utf8) }, file: file, line: line)
    }
}

private func identityBytes(_ payload: RuntimeValue, _ field: String) -> [UInt8] {
    payload.catField(field)?.catText.map { Array($0.utf8) } ?? []
}

private func identityList(_ items: [RuntimeValue]) -> RuntimeValue { .object(["list": .array(items)]) }

private func identityVideo(flags: [String], ids: [String]) -> RuntimeValue {
    .object(["vod_play_from": .string(flags.joined(separator: "$$$")),
        "vod_play_url": .string(ids.map { "首集$" + $0 }.joined(separator: "$$$"))])
}

private func identityFolder(_ id: String) -> RuntimeValue {
    .object(["vod_id": .string(id), "vod_navigation": .object([
        "action": .string("directory"), "payload": .object(["id": .string(id), "page": .number(1)])])])
}

private actor ProbeIdentityFixture {
    private let runtime = SourcesRuntimeFixture()
    private let reply: @Sendable (String, RuntimeValue) -> RuntimeValue?
    var calls: [(String, RuntimeValue)] = []

    init(reply: @escaping @Sendable (String, RuntimeValue) -> RuntimeValue?) { self.reply = reply }

    nonisolated var transport: CatScriptTransport {
        CatScriptTransport(request: { operation, params in try await self.request(operation, params) },
            upload: { _, _, _ in throw FilmSourceError(code: "UNEXPECTED_UPLOAD") })
    }

    private func request(_ operation: String, _ params: RuntimeValue) async throws -> RuntimeValue {
        if operation == "invoke" {
            let action = try XCTUnwrap(params.catField("operation")?.catText)
            let payload = try XCTUnwrap(params.catField("payload"))
            calls.append((action, payload))
            if let value = reply(action, payload) { return value }
        }
        return try await runtime.request(operation, params)
    }
}
