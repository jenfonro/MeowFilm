import XCTest
import Combine
import MeowRuntime
@testable import MeowFilmFeature

@MainActor
final class FilmPlaybackSearchContextTests: XCTestCase {
    func testCloseCancelsUnadmittedScopeWaitWithoutClosingTheSearchOrCreatingASession() async throws {
        let gate = FilmPlaybackGate(), source = site(), metadata = SearchContextMetadata()
        let service = FilmSearchService(processor: try FilmSearchProcessor(),
            data: .init(read: { _, _, _ in .init(value: Self.page(.number(0))) }),
            sites: { await gate.wait(); return [source] }, rules: { [] }, metadata: metadata)
        addTeardownBlock { await gate.release(); await service.close() }
        let session = try service.session(for: "真实标题")
        await waitFor(session) { !$0.cards.isEmpty }
        let context = session.context(for: try XCTUnwrap(session.snapshot.cards.first))
        let server = FilmPlaybackWireServer()
        let request = FilmPlaybackRequest(target: try context.playbackTarget(), sources: [source.playbackSource])
        let requested = expectation(description: "request enters the unadmitted search scope")
        let model = FilmPlaybackModel(transport: server.transport, media: FilmPlaybackTestMedia(),
            openExternalURL: { _ in XCTFail("Unexpected external handoff"); return false },
            searchContext: context, request: { requested.fulfill(); return request })
        let opening = model.open()
        await fulfillment(of: [requested], timeout: 2)
        let ended = expectation(description: "close finishes before the unrelated source preparation")
        let closing = Task { await model.close(); ended.fulfill() }
        await fulfillment(of: [ended], timeout: 2)
        XCTAssertTrue(model.isReleased)
        XCTAssertTrue(session.snapshot.loading, "Closing this page does not cancel the search's separate owner")
        XCTAssertNil(model.actionError)
        let calls = await server.calls()
        XCTAssertTrue(calls.isEmpty, "Only admitted HTTP session.open is drained, never invented after close")
        await gate.release(); await opening?.value; await closing.value; await session.drain()
        let laterCalls = await server.calls()
        XCTAssertTrue(laterCalls.isEmpty)
    }

    func testFailedSourceAdmissionDoesNotWaitForTheUnrelatedMetadataDetail() async throws {
        let gate = FilmPlaybackGate(), metadata = SearchContextMetadata(detailGate: gate)
        let service = FilmSearchService(processor: try FilmSearchProcessor(),
            data: .init(read: { _, _, _ in throw FilmSearchError.unavailable }),
            sites: { throw FilmSearchError.unavailable }, rules: { [] }, metadata: metadata)
        addTeardownBlock { await gate.release(); await service.close() }
        let session = try service.session(for: "真实标题")
        await waitFor(session) { $0.siteState == .failed && !$0.cards.isEmpty }
        let context = session.context(for: try XCTUnwrap(session.snapshot.cards.first))
        let server = FilmPlaybackWireServer()
        let model = makeModel(transport: server.transport, media: FilmPlaybackTestMedia(), context: context,
                          request: .init(target: try context.playbackTarget(), sources: []))
        let ended = expectation(description: "failed admission finishes before the unrelated detail")
        let opening = model.open()
        let observed = Task { await opening?.value; ended.fulfill() }
        await fulfillment(of: [ended], timeout: 2)
        XCTAssertNotNil(model.actionError)
        XCTAssertTrue(session.snapshot.loading, "The unrelated detail is still intentionally gated")
        let calls = await server.calls()
        XCTAssertTrue(calls.isEmpty)
        await gate.release(); await observed.value; await session.drain(); await model.close()
    }

    func testOpenDoesNotWaitForAllSourcesAndLateCarrierUpdatesTheSameSessionWithoutNativePlay() async throws {
        let owner = try client()
        let late = FilmPlaybackGate(), metadataGate = FilmPlaybackGate()
        let first = site(), second = site("late")
        let metadata = SearchContextMetadata(gate: metadataGate)
        let service = FilmSearchService(processor: try FilmSearchProcessor(), data: .init(read: { source, _, _ in
            if source.source.key.siteKey == "late" { await late.wait() }
            return .init(value: Self.page(source.source.key.siteKey == "late" ? .string("later") : .number(0)),
                         producer: owner)
        }), sites: { [first, second] }, rules: { [] }, metadata: metadata)
        addTeardownBlock { await late.release(); await metadataGate.release(); await service.close(); await owner.close() }
        let session = try service.session(for: "真实标题")
        await waitFor(session) { $0.cards.count == 1 && $0.rawCards.count == 1 }
        let context = session.context(for: try XCTUnwrap(session.snapshot.cards.first))
        let target = try context.playbackTarget()
        XCTAssertEqual(target.kind, .group)
        let replies = SearchContextReplies(target: .object(target.value))
        let server = FilmPlaybackWireServer(handler: { operation, input in await replies.call(operation, input) })
        let transport = FilmPlaybackTransport(request: { try await server.call($0, $1) },
            observation: { _, _, _ in throw CancellationError() }, producer: { owner })
        let media = FilmPlaybackTestMedia()
        let model = makeModel(transport: transport, media: media, context: context,
                          request: .init(target: target, sources: [first.playbackSource, second.playbackSource]))
        await model.open()?.value
        var calls = await server.calls()
        XCTAssertEqual(calls.map(\.0), ["session.open"])
        XCTAssertEqual(calls[0].1.filmObject?["sources"]?.filmArray?.count, 2)
        XCTAssertEqual(calls[0].1.filmObject?["results"]?.filmArray?.count, 1)
        XCTAssertEqual(calls[0].1.filmObject?["search"]?.filmArray?.first?.filmObject?["page"], .number(1))
        XCTAssertTrue(session.snapshot.loading, "An admitted first card must not await every source or metadata")

        await late.release(); await metadataGate.release()
        await session.drain()
        await model.waitForPendingWork()
        calls = await server.calls()
        let updates = calls.filter { $0.1.filmObject?["action"] == .string("search.update") }.map(\.1)
        let last = try XCTUnwrap(updates.last?.filmObject)
        XCTAssertEqual(calls.filter { $0.0 == "session.open" }.count, 1)
        XCTAssertFalse(updates.isEmpty)
        XCTAssertTrue(updates.allSatisfy { $0.filmObject?["sessionId"] == .string(FilmPlaybackFixture.sessionID.uuidString.lowercased()) })
        let revisions = updates.compactMap { $0.filmObject?["revision"]?.filmInteger }
        XCTAssertEqual(revisions, revisions.sorted())
        XCTAssertEqual(Set(revisions).count, revisions.count)
        XCTAssertEqual(last["selectionId"], .string(context.selectionID))
        XCTAssertEqual(last["results"]?.filmArray?.count, 2)
        XCTAssertEqual(last["metadataTarget"]?.filmObject?["kind"], .string("movie"))
        XCTAssertEqual(last["metadataTarget"]?.filmObject?["metadataId"], .string("7"))
        XCTAssertEqual(last["metadata"]?.filmObject?["overview"], .string("完整资料"))
        XCTAssertEqual(model.snapshot?.target.kind, .movie)
        XCTAssertEqual(model.snapshot?.target.id, context.selectionID)
        XCTAssertTrue(media.prepared.isEmpty)
        XCTAssertFalse(calls.contains { $0.0 == "session.attempt" || $0.1.filmObject?["action"] == .string("play") })
        await model.close()
    }

    func testLateMetadataCannotReprepareOrCloseAnAlreadyStartedGroupFile() async throws {
        let gate = FilmPlaybackGate(), metadata = SearchContextMetadata(gate: gate), source = site()
        let service = try makeService(source, metadata: metadata)
        addTeardownBlock { await gate.release(); await service.close() }
        let session = try service.session(for: "真实标题")
        await waitFor(session) { !$0.cards.isEmpty }
        let context = session.context(for: try XCTUnwrap(session.snapshot.cards.first))
        let target = try context.playbackTarget()
        var displaySource = source.playbackSource.value; displaySource["detailId"] = .number(0)
        let candidate = FilmPlaybackFixture.candidate(source: .object(displaySource))
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("live", candidate: candidate), target: .object(target.value)))
        let media = FilmPlaybackTestMedia()
        let model = makeModel(transport: server.transport, media: media, context: context,
                          request: .init(target: target, sources: [source.playbackSource]))
        await model.open()?.value
        await model.waitForPendingWork()
        await server.awaitObservation()
        XCTAssertEqual(model.snapshot?.current?.attemptID, "live")
        let current: RuntimeValue = .object(["attemptId": .string("live"), "candidate": candidate,
            "position": .number(0), "duration": .number(600), "paused": .bool(false)])
        let reply = FilmPlaybackFixture.snapshot(20, stage: "playing", current: current,
            metadata: .object(["id": .number(7), "title": .string("真实标题"), "overview": .string("完整资料")]),
            target: .object(target.value),
            metadataSources: [.object(["id": .string("tmdb:movie:7"), "provider": .string("tmdb"),
                "metadataId": .string("7"), "kind": .string("movie"), "title": .string("真实标题")])],
            metadataSourceID: "tmdb:movie:7")
        await server.reply(to: "search.update", with: reply)
        await gate.release(); await session.drain(); await model.waitForPendingWork()
        XCTAssertEqual(model.snapshot?.target, target)
        XCTAssertEqual(model.snapshot?.current?.attemptID, "live")
        XCTAssertEqual(media.prepared.map(\.attemptID), ["live"])
        XCTAssertEqual(media.committed, ["live"])
        XCTAssertTrue(media.stopped.isEmpty); XCTAssertTrue(media.cancelled.isEmpty)
        let calls = await server.calls()
        XCTAssertEqual(calls.filter { $0.0 == "session.open" }.count, 1)
        XCTAssertEqual(calls.filter { $0.1.filmObject?["action"] == .string("media.started") }.count, 1)
        XCTAssertTrue(calls.contains { $0.1.filmObject?["metadataTarget"]?.filmObject?["metadataId"] == .string("7") })
        await model.close()
    }

    func testRawReentryKeepsTypedDetailLineAndOpaqueCandidateInsteadOfInitialClosureTarget() async throws {
        let source = site(), owner = try client()
        let service = FilmSearchService(processor: try FilmSearchProcessor(), data: .init(read: { _, _, _ in
            .init(value: Self.page(.number(0)), producer: owner)
        }), sites: { [source] }, rules: { [] })
        addTeardownBlock { await service.close(); await owner.close() }
        let session = try service.session(for: "真实标题")
        await session.drain()
        let context = session.context(for: try XCTUnwrap(session.snapshot.rawCards.first { $0.detailID == .number(0) }))
        let initial = try context.playbackTarget()
        var fields = initial.value
        fields["source"] = .object(["key": .string(source.key), "detailId": .number(0),
                                    "lineId": .number(0), "candidateId": .string("opaque-selected")])
        let selected = try FilmPlaybackTarget(.object(fields))
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(target: .object(initial.value)))
        let media = FilmPlaybackTestMedia()
        let requests = SearchContextRequest(.init(target: initial, sources: [source.playbackSource]))
        let model = FilmPlaybackModel(transport: server.transport(runtime: owner), media: media,
            openExternalURL: { _ in XCTFail("Unexpected external handoff"); return false },
            searchContext: context, request: { await requests.value })
        await model.open()?.value
        let firstOpen = await server.calls().first { $0.0 == "session.open" }
        XCTAssertEqual(firstOpen?.1.filmObject?["results"]?.filmArray?.count, 1)
        await server.reply(to: "select.candidate", with: FilmPlaybackFixture.snapshot(2, target: .object(selected.value)))
        model.selectCandidate("opaque-selected")
        await model.waitForPendingWork()
        await model.close()
        let current = FilmPlaybackSource(key: "new-generation-key", engine: "cat", installationID: "fixture",
                                         siteID: "site", generation: 2, name: "已更新的来源")
        await requests.set(.init(target: initial, sources: [current]))
        await model.open()?.value
        await model.waitForPendingWork()
        let opens = await server.calls().filter { $0.0 == "session.open" }
        XCTAssertEqual(opens.count, 2)
        XCTAssertEqual(opens.last?.1.filmObject?["target"], .object(selected.value))
        XCTAssertEqual(opens.last?.1.filmObject?["sources"]?.filmArray?.first?.filmObject?["key"], .string(source.key))
        XCTAssertEqual(opens.last?.1.filmObject?["sources"]?.filmArray?.first?.filmObject?["generation"], .number(2))
        XCTAssertEqual(opens.last?.1.filmObject?["results"], .array([]))
        XCTAssertEqual(opens.last?.1.filmObject?["search"], .array([]))
        XCTAssertEqual(opens.last?.1.filmObject?["cacheEntries"], .array([]))
        XCTAssertEqual(opens.last?.1.filmObject?["resultHints"]?.filmArray?.first?.filmObject?["items"]?.filmArray?.count, 1)
        XCTAssertEqual(opens.last?.1.filmObject?["resultHints"]?.filmArray?.first?.filmObject?["items"]?.filmArray?.first?.filmObject?["vod_id"], .number(0))
        XCTAssertNil(opens.last?.1.filmObject?["metadata"])
        XCTAssertTrue(media.prepared.isEmpty)
        await model.close()
    }

    func testMetadataReentryPreservesTheSelectedSpecialEpisodeZero() async throws {
        let source = site(), metadata = SearchContextMetadata(kind: "tv")
        let service = try makeService(source, metadata: metadata)
        addTeardownBlock { await service.close() }
        let session = try service.session(for: "真实标题")
        await session.drain()
        let context = session.context(for: try XCTUnwrap(session.snapshot.cards.first))
        let initial = try context.playbackTarget()
        var fields = initial.value
        fields["season"] = .number(0); fields["episode"] = .number(0)
        let selected = try FilmPlaybackTarget(.object(fields))
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(target: .object(initial.value)))
        let model = makeModel(transport: server.transport, media: FilmPlaybackTestMedia(), context: context,
                          request: .init(target: initial, sources: [source.playbackSource]))
        await model.open()?.value
        await server.reply(to: "select.episode", with: FilmPlaybackFixture.snapshot(2, target: .object(selected.value)))
        model.selectEpisode(.init(season: 0, episode: 0))
        await model.waitForPendingWork(); await model.close()
        await model.open()?.value
        await model.waitForPendingWork()
        let opens = await server.calls().filter { $0.0 == "session.open" }
        XCTAssertEqual(opens.count, 2)
        XCTAssertEqual(opens.last?.1.filmObject?["target"], .object(selected.value))
        XCTAssertNil(opens.last?.1.filmObject?["target"]?.filmObject?["globalEpisode"])
        await model.close()
    }

    func testLateCarrierDoesNotOverwriteAnExplicitlyChosenMetadataIdentity() async throws {
        let source = site(), gate = FilmPlaybackGate(), metadata = SearchContextMetadata(gate: gate)
        let service = try makeService(source, metadata: metadata)
        addTeardownBlock { await gate.release(); await service.close() }
        let session = try service.session(for: "真实标题")
        await waitFor(session) { !$0.cards.isEmpty }
        let context = session.context(for: try XCTUnwrap(session.snapshot.cards.first))
        let initial = try context.playbackTarget()
        let chosen = FilmPlaybackTarget(id: initial.id, kind: .movie, title: "明确选择",
                                        metadataProvider: .douban, metadataID: "8")
        let reply = FilmPlaybackFixture.snapshot(10, target: .object(chosen.value))
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(target: .object(initial.value)))
        let media = FilmPlaybackTestMedia()
        let model = makeModel(transport: server.transport, media: media, context: context,
                          request: .init(target: initial, sources: [source.playbackSource]))
        await model.open()?.value
        await server.reply(to: "browse.metadata", with: reply)
        model.browseMetadata("douban:movie:8")
        await model.waitForPendingWork()
        await server.reply(to: "search.update", with: reply)
        await gate.release(); await session.drain(); await model.waitForPendingWork()
        let calls = await server.calls()
        let update = try XCTUnwrap(calls.last { $0.1.filmObject?["action"] == .string("search.update") }?.1.filmObject)
        XCTAssertNil(update["metadataTarget"]); XCTAssertNil(update["metadata"])
        XCTAssertEqual(model.snapshot?.target, chosen)
        XCTAssertTrue(media.prepared.isEmpty)
        await model.close()
    }

    func testCloseOrConfirmedRuntimeRevocationFencesLateContextAndNeverReopensIt() async throws {
        for revoke in [false, true] {
            let source = site(), gate = FilmPlaybackGate(), metadata = SearchContextMetadata(gate: gate)
            let service = try makeService(source, metadata: metadata), owner = try client()
            addTeardownBlock { await gate.release(); await service.close(); await owner.close() }
            let session = try service.session(for: "真实标题")
            await waitFor(session) { !$0.cards.isEmpty }
            let context = session.context(for: try XCTUnwrap(session.snapshot.cards.first))
            let initial = try context.playbackTarget()
            let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(target: .object(initial.value)))
            let model = makeModel(transport: server.transport(runtime: owner), media: FilmPlaybackTestMedia(), context: context,
                              request: .init(target: initial, sources: [source.playbackSource]))
            await model.open()?.value
            if revoke {
                let closed = expectation(description: "confirmed runtime revocation")
                let observation = model.$observationError.first { $0?.code == "CLOSED" }.sink { _ in closed.fulfill() }
                await owner.close()
                await fulfillment(of: [closed], timeout: 2)
                observation.cancel()
                await model.waitForPendingWork()
                XCTAssertNil(model.open()); XCTAssertNil(model.send("play"))
            } else { await model.close() }
            let before = await server.calls().count
            await gate.release(); await session.drain(); await model.waitForPendingWork()
            let after = await server.calls()
            XCTAssertEqual(after.count, before)
            XCTAssertEqual(after.filter { $0.0 == "session.open" }.count, 1)
            XCTAssertFalse(after.contains { $0.1.filmObject?["action"] == .string("search.update") })
        }
    }

    private func makeModel(transport: FilmPlaybackTransport, media: FilmPlaybackTestMedia, context: FilmSearchContext,
                       request: FilmPlaybackRequest) -> FilmPlaybackModel {
        .init(transport: transport, media: media, openExternalURL: { _ in XCTFail("No external handoff"); return false },
              searchContext: context, request: { request })
    }
    private func site(_ id: String = "site") -> FilmSearchSite {
        .init(cat: .init(installationId: "fixture", name: "fixture", sourceType: "local", generation: 1),
              source: .init(key: .init(installationId: "fixture", siteKey: id), name: id,
                            api: "fixture", actions: ["search"], methods: [:]))
    }
    private func makeService(_ source: FilmSearchSite, metadata: (any FilmSearchMetadataReading)? = nil) throws -> FilmSearchService {
        FilmSearchService(processor: try FilmSearchProcessor(), data: .init(read: { _, _, _ in
            .init(value: Self.page(.number(0)))
        }), sites: { [source] }, rules: { [] }, metadata: metadata)
    }
    private nonisolated static func page(_ id: RuntimeValue) -> RuntimeValue {
        .object(["list": .array([.object(["vod_id": id, "vod_name": .string("真实标题"),
                                         "vod_remarks": .string("原备注")])]), "pagecount": .number(1)])
    }
    private func client() throws -> RuntimeHTTPClient {
        try .init(baseURL: URL(string: "http://127.0.0.1:43211")!)
    }
    private func waitFor(_ session: FilmSearchSession, _ predicate: @escaping (FilmSearchSnapshot) -> Bool) async {
        let ready = expectation(description: "an actual shared-program card was published")
        let subscription = session.$snapshot.first(where: predicate).sink { _ in ready.fulfill() }
        await fulfillment(of: [ready], timeout: 2)
        subscription.cancel()
        XCTAssertTrue(predicate(session.snapshot))
    }
}

private actor SearchContextMetadata: FilmSearchMetadataReading {
    let gate: FilmPlaybackGate?
    let detailGate: FilmPlaybackGate?
    let kind: String
    init(gate: FilmPlaybackGate? = nil, kind: String = "movie", detailGate: FilmPlaybackGate? = nil) {
        self.gate = gate; self.kind = kind; self.detailGate = detailGate
    }
    func search(query: String) async -> RuntimeValue {
        await gate?.wait()
        return .object(["results": .array([.object(["id": .number(7), "media_type": .string(kind),
            "title": .string("真实标题"), "name": .string("真实标题")])])])
    }
    func detail(type: String, id: Int) async -> RuntimeValue {
        await detailGate?.wait()
        return .object(["id": .number(7), "title": .string("真实标题"), "name": .string("真实标题"),
            "overview": .string("完整资料"), "credits": .object(["cast": .array([])]),
            "similar": .object(["results": .array([])]), "images": .object(["logos": .array([])])])
    }
    nonisolated func imageURL(path: String) -> URL? { nil }
}

private actor SearchContextReplies {
    private var target: RuntimeValue
    private var sequence = 0
    init(target: RuntimeValue) { self.target = target }
    func call(_ operation: String, _ input: RuntimeValue) -> RuntimeValue {
        if operation == "session.release" { return .object(["released": .bool(true)]) }
        sequence += 1
        if input.filmObject?["action"] == .string("search.update"), let carrier = input.filmObject?["metadataTarget"] {
            target = carrier
        }
        return FilmPlaybackFixture.snapshot(sequence, metadata: input.filmObject?["metadata"] ?? .null, target: target)
    }
}

private actor SearchContextRequest {
    var value: FilmPlaybackRequest
    init(_ value: FilmPlaybackRequest) { self.value = value }
    func set(_ value: FilmPlaybackRequest) { self.value = value }
}
