import Foundation
import XCTest
import MeowJSON
import MeowStorage
@testable import MeowFilmFeature

final class FilmSourceIdentityTests: XCTestCase {
    private let composed = "\u{00e9}"
    private let decomposed = "e\u{0301}"

    private func setSites(_ keys: [String], runtime: SourcesRuntimeFixture) async throws {
        var cat = try JSONDecoder().decode(FilmCatSource.self, from: JSONEncoder().encode(SourcesRuntimeFixture.cat("a")))
        cat.sites = keys.map { key in
            FilmSourceSite(key: key, name: "Site", api: "/api/a", actions: ["home", "category", "detail", "play", "search"], methods: [:])
        }
        await runtime.setCat(try JSONDecoder().decode(JSONValue.self, from: JSONEncoder().encode(cat)))
    }

    func testExactIdentityRetainsCompatibleCodableShape() throws {
        let first = FilmSourceKey(installationId: "a", siteKey: composed)
        let second = FilmSourceKey(installationId: "a", siteKey: decomposed)
        XCTAssertNotEqual(first, second)
        XCTAssertEqual(Set([first, second]).count, 2)
        let legacy = Data(#"{"installationId":"a","siteKey":"e\u0301"}"#.utf8)
        let decoded = try JSONDecoder().decode(FilmSourceKey.self, from: legacy)
        XCTAssertEqual(decoded, second)
        XCTAssertEqual(Array(decoded.siteKey.utf8), Array(decomposed.utf8))
        let encoded = try JSONSerialization.jsonObject(with: JSONEncoder().encode(decoded)) as? [String: String]
        XCTAssertEqual(Set(try XCTUnwrap(encoded).keys), Set(["installationId", "siteKey"]))
        XCTAssertEqual(Array(try XCTUnwrap(encoded?["siteKey"]).utf8), Array(decomposed.utf8))
        XCTAssertNotEqual(FilmSourceSite(key: composed, name: "Site", api: "/api", actions: [], methods: [:]),
                          FilmSourceSite(key: decomposed, name: "Site", api: "/api", actions: [], methods: [:]))
    }

    func testEquivalentLookingKeysKeepOptionsDeletionDetectionAndReimportSeparateAcrossRebuild() async throws {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: base) }
        let store = try DocumentStore(baseURL: base, namespace: FilmSourceService.documentNamespace)
        let runtime = SourcesRuntimeFixture()
        try await setSites([composed, decomposed], runtime: runtime)
        let transport = CatScriptTransport(rpc: { method, params, _ in try await runtime.rpc(method, params) })
        var service = FilmSourceService(transport: transport, storeFactory: { store })
        let first = FilmSourceKey(installationId: "a", siteKey: composed)
        let second = FilmSourceKey(installationId: "a", siteKey: decomposed)
        try await service.refresh()
        var snapshot = await service.snapshot
        XCTAssertEqual(snapshot.videos.filter { $0.key.installationId == "a" }.count, 2)
        let firstOptions = FilmVideoOptions(enabled: false, homeVisible: false, searchEnabled: true, aggregateImages: true)
        let secondOptions = FilmVideoOptions(enabled: true, homeVisible: false, searchEnabled: false)
        try await service.saveVideoOptions(first, options: firstOptions, order: 7)
        try await service.saveVideoOptions(second, options: secondOptions, order: -3)
        let result = try await service.detect(second)
        snapshot = await service.snapshot
        XCTAssertEqual(snapshot.videos.first { $0.key == first }?.options, firstOptions)
        XCTAssertEqual(snapshot.videos.first { $0.key == second }?.options, secondOptions)
        XCTAssertNil(snapshot.videos.first { $0.key == first }?.detection)
        XCTAssertEqual(snapshot.videos.first { $0.key == second }?.detection, result)
        let calls = await runtime.invocationSiteKeys
        XCTAssertFalse(calls.isEmpty)
        XCTAssertTrue(calls.allSatisfy { Array($0.utf8) == Array(decomposed.utf8) }, "Wire must preserve exact siteId")
        await service.close()
        service = FilmSourceService(transport: transport, storeFactory: { store })
        try await service.initialize()
        snapshot = await service.snapshot
        XCTAssertEqual(snapshot.videos.filter { $0.key.installationId == "a" }.count, 2)
        XCTAssertEqual(snapshot.videos.first { $0.key == first }?.options, firstOptions)
        XCTAssertEqual(snapshot.videos.first { $0.key == first }?.order, 7)
        XCTAssertEqual(snapshot.videos.first { $0.key == second }?.options, secondOptions)
        XCTAssertEqual(snapshot.videos.first { $0.key == second }?.detection, result)
        try await service.refresh()
        try await service.deleteVideo(first)
        _ = try await service.updateCat("a")
        try await service.refresh()
        snapshot = await service.snapshot
        XCTAssertFalse(snapshot.videos.contains { $0.key == first })
        XCTAssertEqual(snapshot.videos.first { $0.key == second }?.options, secondOptions)
        XCTAssertEqual(snapshot.videos.first { $0.key == second }?.order, -3)
        await service.close()

        let rebuilt = FilmSourceService(transport: transport, storeFactory: { store })
        try await rebuilt.initialize()
        snapshot = await rebuilt.snapshot
        XCTAssertFalse(snapshot.videos.contains { $0.key == first })
        XCTAssertEqual(snapshot.videos.first { $0.key == second }?.detection, result)
        try await rebuilt.refresh()
        _ = try await rebuilt.importNetwork(name: "A", sourceURL: "https://example.invalid/a.js", installationId: "a")
        snapshot = await rebuilt.snapshot
        XCTAssertEqual(snapshot.videos.filter { $0.key.installationId == "a" }.count, 2)
        XCTAssertEqual(snapshot.videos.first { $0.key == first }?.options, .init())
        XCTAssertEqual(snapshot.videos.first { $0.key == second }?.options, secondOptions)
        XCTAssertEqual(snapshot.videos.first { $0.key == second }?.detection, result)
        try await rebuilt.deleteVideo(second)
        try await rebuilt.refresh()
        snapshot = await rebuilt.snapshot
        XCTAssertTrue(snapshot.videos.contains { $0.key == first })
        XCTAssertFalse(snapshot.videos.contains { $0.key == second })
        await rebuilt.close()

        let finalOwner = FilmSourceService(transport: transport, storeFactory: { store })
        try await finalOwner.initialize()
        snapshot = await finalOwner.snapshot
        XCTAssertTrue(snapshot.videos.contains { $0.key == first })
        XCTAssertFalse(snapshot.videos.contains { $0.key == second })
        await finalOwner.close()
    }

    func testOrdinaryUpdateToDifferentExactKeyDoesNotInheritOptionsOrDeletion() async throws {
        for deleted in [false, true] {
            let base = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
            try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
            defer { try? FileManager.default.removeItem(at: base) }
            let store = try DocumentStore(baseURL: base, namespace: FilmSourceService.documentNamespace)
            let runtime = SourcesRuntimeFixture()
            try await setSites([composed], runtime: runtime)
            let service = FilmSourceService(transport: .init(rpc: { method, params, _ in try await runtime.rpc(method, params) }), storeFactory: { store })
            let old = FilmSourceKey(installationId: "a", siteKey: composed)
            let new = FilmSourceKey(installationId: "a", siteKey: decomposed)
            try await service.refresh()
            try await service.saveVideoOptions(old, options: .init(enabled: false), order: 8)
            if deleted { try await service.deleteVideo(old) }
            try await setSites([decomposed], runtime: runtime)
            _ = try await service.updateCat("a")
            let snapshot = await service.snapshot
            XCTAssertFalse(snapshot.videos.contains { $0.key == old })
            let video = try XCTUnwrap(snapshot.videos.first { $0.key == new })
            XCTAssertEqual(Array(video.key.siteKey.utf8), Array(decomposed.utf8))
            XCTAssertEqual(video.options, .init(), "New raw key is a new site")
            if deleted {
                try await setSites([composed, decomposed], runtime: runtime)
                try await service.refresh()
                let returned = await service.snapshot
                XCTAssertFalse(returned.videos.contains { $0.key == old })
                XCTAssertTrue(returned.videos.contains { $0.key == new })
            }
            await service.close()
        }
    }
}
