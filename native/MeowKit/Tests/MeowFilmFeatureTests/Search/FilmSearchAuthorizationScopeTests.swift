#if os(iOS) || os(macOS)
import XCTest
import Combine
import MeowRuntime
import MeowStorage
@testable import MeowFilmFeature

@MainActor
final class FilmSearchAuthorizationScopeTests: XCTestCase {
    private typealias F = FilmSearchWireFixture
    func testCurrentGenerationIsPassedToNodeWithoutBlessingOldFacts() async throws {
        let rpc = FilmSearchRPCFixture(F.snapshot(cards: [F.group([F.row()])]))
        let service = filmSearchService(rpc), session = try service.session(for: "原查询")
        await session.drain()
        let context = session.context(for: try XCTUnwrap(session.snapshot.cards.first))
        let current = FilmPlaybackSource(key: "current-key", engine: "cat", installationID: "fixture", siteID: "site",
            generation: 2, name: "新来源")
        let hints: [RuntimeValue] = [.object(["sourceKey": .string(F.site.key), "items": .array([F.row().catField("raw")!])])]
        await rpc.setHandoff(F.handoff(sources: [current], hints: hints))
        let request = try await context.playbackRequest(sources: [current], owner: nil)
        XCTAssertEqual(request.sources, [current]); XCTAssertEqual(request.resultHints, hints)
        XCTAssertTrue(request.results.isEmpty); XCTAssertTrue(request.search.isEmpty); XCTAssertTrue(request.cacheEntries.isEmpty)
        let calls = await rpc.calls
        XCTAssertEqual(calls.last?.1.filmObject?["sources"], .array([.object(current.value)]))
        await service.close()
    }

    func testCrossRuntimeHandoffIsRejectedBeforeRPCAndDoesNotMintCacheEvidence() async throws {
        let original = try RuntimeHTTPClient(baseURL: URL(string: "http://127.0.0.1:43211")!)
        let different = try RuntimeHTTPClient(baseURL: URL(string: "http://127.0.0.1:43212")!)
        defer { Task { await original.close(); await different.close() } }
        let rpc = FilmSearchRPCFixture(F.snapshot(cards: [F.group([F.row()])]))
        let service = filmSearchService(rpc, owner: original), session = try service.session(for: "原查询")
        await session.drain()
        let context = session.context(for: try XCTUnwrap(session.snapshot.cards.first))
        do { _ = try await context.playbackRequest(sources: [F.site.playbackSource], owner: different); XCTFail("Different owner") }
        catch { XCTAssertTrue(error is FilmSearchError) }
        let calls = await rpc.calls
        XCTAssertEqual(calls.map(\.0), ["search.open"])
        await service.close()
    }

    func testUnknownScopeCanBeCancelledWithoutOpeningAndResumeAdmitsOnlyOnce() async throws {
        let entered = expectation(description: "source discovery"), gate = FilmRuleGate(), rpc = FilmSearchRPCFixture()
        entered.assertForOverFulfill = false
        let service = FilmSearchService(transport: rpc.transport(), sites: { entered.fulfill(); await gate.wait(); return [F.site] })
        let session = try service.session(for: "原查询")
        await fulfillment(of: [entered], timeout: 3)
        XCTAssertFalse(session.hasDifferentSources([:]), "An unadmitted scope is not a known empty scope")
        XCTAssertFalse(session.invalidated)
        XCTAssertTrue(try service.session(for: "原查询", reusing: session) === session)
        session.cancel()
        await gate.release(); await session.drain()
        let before = await rpc.calls
        XCTAssertTrue(before.isEmpty)
        XCTAssertTrue(try service.session(for: "原查询") === session)
        await session.drain()
        let after = await rpc.calls
        XCTAssertEqual(after.map(\.0), ["search.open"])
        await service.close()
    }

    func testSourceOptionsArePreservedOnWireAndSearchScopeIncludesOnlyEnabledSearchActions() {
        var source = F.site.source
        source.options.aggregateImages = true; source.order = 7
        let site = FilmSearchSite(cat: F.site.cat, source: source)
        XCTAssertEqual(site.playbackSource.value["order"], .number(7))
        XCTAssertEqual(site.playbackSource.value["aggregateImages"], .bool(true))
        let original = FilmSearchService.authorizationScope([site.playbackSource])
        source.options.aggregateImages = false
        XCTAssertNotEqual(original, FilmSearchService.authorizationScope([FilmSearchSite(cat: F.site.cat, source: source).playbackSource]))
        for options in [FilmVideoOptions(enabled: false), FilmVideoOptions(searchEnabled: false)] {
            source.options = options
            XCTAssertTrue(FilmSearchService.searchableSites(.init(cats: [F.site.cat], videos: [source], runtimeError: nil)).isEmpty)
        }
        source.options = .init(); source.actions = ["detail"]
        XCTAssertTrue(FilmSearchService.searchableSites(.init(cats: [F.site.cat], videos: [source], runtimeError: nil)).isEmpty)
    }

    func testSourceOwnerGenerationChangeFencesAcceptedMoreAndReentryUsesCurrentWire() async throws {
        let runtime = SourcesRuntimeFixture(), entered = expectation(description: "old more accepted"), gate = FilmRuleGate()
        let (sources, directory) = try await sourceOwner(runtime)
        defer { try? FileManager.default.removeItem(at: directory) }
        let nextID = UUID(), row = F.row(), late = F.row(.string("late"), key: "late")
        let rpc = FilmSearchRPCFixture(handler: { operation, input in
            if operation == "search.release" { return .object(["released": .bool(true)]) }
            if operation == "search.open" {
                let generation = input.filmObject?["sources"]?.filmArray?.first?.filmObject?["generation"]
                return F.snapshot(hasMore: true, cards: [row], raw: [row], id: generation == .number(2) ? nextID : F.id)
            }
            if input.filmObject?["action"] == .string("more") {
                entered.fulfill(); await gate.wait()
                XCTAssertFalse(Task.isCancelled)
                return F.snapshot(3, cards: [late], raw: [late])
            }
            return F.snapshot(4, site: "cancelled", metadata: "cancelled", cards: [row], raw: [row])
        })
        let service = FilmSearchService(sources: sources, transport: rpc.transport())
        let old = try service.session(for: "原查询")
        await old.drain()
        let context = old.context(for: try XCTUnwrap(old.snapshot.cards.first))
        old.loadMore(); await fulfillment(of: [entered], timeout: 3)
        let revoked = expectation(description: "source generation revoked old publication")
        let subscription = old.$snapshot.first { _ in old.invalidated }.sink { _ in revoked.fulfill() }
        await runtime.changeGeneration(); try await sources.refresh()
        await fulfillment(of: [revoked], timeout: 3); subscription.cancel()
        XCTAssertTrue(old.invalidated)
        let before = await rpc.calls
        old.loadMore(); old.startIfNeeded()
        let after = await rpc.calls
        XCTAssertEqual(after.count, before.count, "A revoked consumer cannot schedule new work")
        let current = try service.session(for: "原查询", reusing: context.session)
        XCTAssertFalse(current === old)
        await current.drain()
        XCTAssertEqual(current.snapshot.sessionID, nextID, "Reentry does not wait for the revoked physical read")
        let calls = await rpc.calls.filter { $0.0 == "search.open" }
        XCTAssertEqual(calls.count, 2)
        XCTAssertEqual(calls.last?.1.filmObject?["sources"]?.filmArray?.first?.filmObject?["generation"], .number(2))
        await gate.release(); await old.drain()
        XCTAssertEqual(old.snapshot.cards.map(\.value), [row])
        XCTAssertEqual(old.snapshot.rawCards.map(\.value), [row])
        await service.close(); await sources.close()
    }

    func testSourceOwnerOptionsAndCatDisableRevokeRetainedScopeWithoutAutomaticallySearching() async throws {
        for change in ["enabled", "searchEnabled", "coverAndOrder", "catDisabled"] {
            let runtime = SourcesRuntimeFixture(), (sources, directory) = try await sourceOwner(runtime)
            defer { try? FileManager.default.removeItem(at: directory) }
            let rpc = FilmSearchRPCFixture(F.snapshot(cards: [F.row()], raw: [F.row()]))
            let service = FilmSearchService(sources: sources, transport: rpc.transport())
            let old = try service.session(for: "原查询")
            await old.drain()
            let context = old.context(for: try XCTUnwrap(old.snapshot.cards.first))
            let revoked = expectation(description: change + " revokes retained source scope")
            let subscription = old.$snapshot.first { _ in old.invalidated }.sink { _ in revoked.fulfill() }
            if change == "catDisabled" { _ = try await sources.enableCat("a", enabled: false) }
            else {
                var options = FilmVideoOptions()
                if change == "enabled" { options.enabled = false }
                if change == "searchEnabled" { options.searchEnabled = false }
                if change == "coverAndOrder" { options.aggregateImages = true }
                try await sources.saveVideoOptions(.init(installationId: "a", siteKey: "same"),
                    options: options, order: change == "coverAndOrder" ? 7 : nil)
            }
            await fulfillment(of: [revoked], timeout: 3); subscription.cancel()
            await old.drain()
            let calls = await rpc.calls
            XCTAssertEqual(calls.filter { $0.0 == "search.open" }.count, 1)
            XCTAssertEqual(old.snapshot.cards.map(\.value), [F.row()], "Configuration revokes publication, not the retained navigation display")
            let current = try service.session(for: "原查询", reusing: context.session)
            XCTAssertFalse(current === old)
            await current.drain()
            let reopened = await rpc.calls.filter { $0.0 == "search.open" }
            let wire = try XCTUnwrap(reopened.last?.1.filmObject?["sources"]?.filmArray)
            if change == "coverAndOrder" {
                XCTAssertEqual(wire.first?.filmObject?["order"], .number(7))
                XCTAssertEqual(wire.first?.filmObject?["aggregateImages"], .bool(true))
            } else { XCTAssertTrue(wire.isEmpty) }
            await service.close(); await sources.close()
        }
    }

    private func sourceOwner(_ runtime: SourcesRuntimeFixture) async throws -> (FilmSourceService, URL) {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let store = try DocumentStore(baseURL: directory, namespace: FilmSourceService.documentNamespace)
        await runtime.setCat(SourcesRuntimeFixture.cat("b", state: "disabled", enabled: false))
        let sources = FilmSourceService(transport: runtime.transport, storeFactory: { store })
        try await sources.refresh()
        return (sources, directory)
    }
}
#endif
