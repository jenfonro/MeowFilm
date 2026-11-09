import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeRecommendationFeedbackTests: XCTestCase {
    func testHeartbeatProofOnlyBelongsToCommittedOccurrenceAndDoesNotSurviveColdRestore() async throws {
        let store = NativeMusicMemoryDocument(try document(heartbeat: true))
        let owner = RecommendationFeedbackAccounts()
        let service = try await NativeMusicFixture.service(store, accounts: owner)
        let result = try await service.enterSimilarMode()
        XCTAssertEqual(result.action, .keepCurrent)
        let saved = try MusicDocument(store.snapshot().0, runID: "fixture")
        let original = try XCTUnwrap(saved.queue["entries"].array.first)
        let recommended = try XCTUnwrap(saved.queue["entries"].array.last)
        let originalTargets = try await service.similarFeedbackTargets(entry: original, context: original["context"])
        XCTAssertEqual(originalTargets.first?.source, "search")
        XCTAssertNil(originalTargets.first?.recommendationProof)
        let targets = try await service.similarFeedbackTargets(entry: recommended, context: recommended["context"])
        let target = try XCTUnwrap(targets.first), proof = try XCTUnwrap(target.recommendationProof)
        XCTAssertEqual(target.nativeID, "301")
        XCTAssertEqual(target.source, "aiMode"); XCTAssertEqual(target.sourceID, "900")
        XCTAssertEqual(target.algorithm, "synthetic-heart")
        XCTAssertEqual(proof.occurrenceID, recommended["id"].string)
        try proof.check(nativeID: "301", source: "aiMode", sourceID: "900", algorithm: "synthetic-heart")

        var copied = recommended
        copied["id"] = .string("entry-999")
        let copiedTargets = try await service.similarFeedbackTargets(entry: copied, context: copied["context"])
        XCTAssertNil(copiedTargets.first?.recommendationProof)
        XCTAssertEqual(copiedTargets.first?.algorithm, "")
        var mismatched = recommended
        mismatched["song"] = try MusicModel.normalize(RecommendationFeedbackAccounts.track("302"))
        let mismatchTargets = try await service.similarFeedbackTargets(entry: mismatched, context: mismatched["context"])
        XCTAssertNil(mismatchTargets.first?.recommendationProof)
        XCTAssertNotEqual(mismatchTargets.first?.source, "aiMode")
        var alternative = recommended
        let alternateSong = try MusicModel.normalize(RecommendationFeedbackAccounts.track("302"))
        let alternateSource = try XCTUnwrap(alternateSong["sources"].array.first)
        alternative["song"]["sources"] = .array(recommended["song"]["sources"].array + [alternateSource])
        let alternateTargets = try await service.similarFeedbackTargets(entry: alternative, context: alternative["context"],
            selectedSourceKey: alternateSource["sourceKey"].string)
        XCTAssertFalse(alternateTargets.contains { $0.recommendationProof != nil },
            "another native recording cannot borrow the recommended recording's proof")

        await service.close()
        XCTAssertThrowsError(try proof.check(nativeID: "301", source: "aiMode", sourceID: "900", algorithm: "synthetic-heart"))
        let cold = try await NativeMusicFixture.service(store, accounts: owner)
        _ = try await cold.similarModeSnapshot()
        let coldTargets = try await cold.similarFeedbackTargets(entry: recommended, context: recommended["context"])
        XCTAssertNil(coldTargets.first?.recommendationProof)
        XCTAssertEqual(coldTargets.first?.source, "")
        XCTAssertEqual(coldTargets.first?.algorithm, "")
        await cold.close()
    }

    func testQueuePreviewCannotAuthorizeFeedbackAndCommittedSourceMapKeepsOriginalSource() async throws {
        let store = NativeMusicMemoryDocument(try document())
        let owner = RecommendationFeedbackAccounts()
        let service = try await NativeMusicFixture.service(store, accounts: owner)
        let bytes = store.snapshot().0
        let before = try MusicDocument(bytes, runID: "preview")
        let options = (try await service.sourceAvailability(for: .init(kind: .queue, id: "entry-1"))).options
        let audio = try await service.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: "entry-1",
            selection: XCTUnwrap(options.first).selection(quality: "128k"))
        _ = try await service.recordSimilarPlaybackFact(.init(attemptId: audio.attemptId, occurrenceId: audio.occurrenceId,
            resourceId: audio.resourceId, kind: .playing, position: 0))
        let previewValue = try await service.previewQueueRecommendations()
        let preview = try XCTUnwrap(previewValue)
        XCTAssertTrue(preview.continueEnabled)
        let previewDocument = try MusicDocument(store.snapshot().0, runID: "preview")
        XCTAssertEqual(previewDocument.queue, before.queue)
        let original = try XCTUnwrap(before.queue["entries"].array.first)
        let originalTargets = try await service.similarFeedbackTargets(entry: original, context: original["context"])
        XCTAssertEqual(originalTargets.first?.source, "search")
        XCTAssertNil(originalTargets.first?.recommendationProof)
        let chosen = try XCTUnwrap(preview.candidates.first)
        _ = try await service.playQueueRecommendation(snapshotID: preview.id, candidateID: chosen.id)
        let saved = try MusicDocument(store.snapshot().0, runID: "committed")
        XCTAssertEqual(saved.queue["source"]["sourceType"].string, "queue-recommendations")
        XCTAssertEqual(saved.queue["entries"].array.first?["context"], original["context"])
        let entry = try XCTUnwrap(saved.queue["entries"].array.last)
        let targets = try await service.similarFeedbackTargets(entry: entry, context: entry["context"])
        let target = try XCTUnwrap(targets.first), proof = try XCTUnwrap(target.recommendationProof)
        XCTAssertEqual(target.source, "sourceMap-synthetic")
        XCTAssertEqual(target.sourceID, "")
        XCTAssertEqual(target.algorithm, "synthetic-queue")
        await owner.revoke()
        XCTAssertFalse(proof.authorization.isActive)
        let revoked = try await service.similarFeedbackTargets(entry: entry, context: entry["context"])
        XCTAssertNil(revoked.first?.recommendationProof)
        XCTAssertNotEqual(revoked.first?.source, "sourceMap-synthetic")
        await service.close()
    }

    func testPersistentRecommendationLabelsCannotSelectOrAttributeARecording() throws {
        let song = try MusicModel.normalize(RecommendationFeedbackAccounts.track("100"))
        let forged = MJ.obj([
            ("sourceType", .string("similar")),
            ("recommendation", .obj([("songId", .string("100")), ("alg", .string("forged")),
                                    ("source", .string("aiMode")), ("sourceId", .string("900"))]))
        ])
        let target = try XCTUnwrap(MusicNativeFeedbackMapping.targets(song: song, context: forged).first)
        XCTAssertEqual(target.source, ""); XCTAssertEqual(target.algorithm, "")
        XCTAssertNil(target.recommendationProof)
    }

    private func document(heartbeat: Bool = false) throws -> Data {
        var document = try MusicDocument(nil, runID: "fixture")
        let song = try MusicModel.normalize(RecommendationFeedbackAccounts.track("100"))
        document.queue["entries"] = .array([.obj([
            ("id", .string("entry-1")), ("key", song["songId"]), ("song", song),
            ("context", .obj([("sourceType", .string("search-song")), ("searchKeyword", .string("Synthetic"))]))
        ])])
        document.queue["currentId"] = .string("entry-1"); document.queue["serial"] = .n(1)
        if heartbeat {
            var rows = document.queue["entries"].array
            rows[0]["context"]["platform"] = .string("wy")
            rows[0]["context"]["sourceId"] = .string("favorites")
            document.queue["entries"] = .array(rows)
        }
        document.queue["source"] = heartbeat ? .obj([("platform", .string("wy")), ("sourceId", .string("favorites"))]) :
            .obj([("sourceType", .string("search-song")), ("seedEntryId", .string("entry-1"))])
        return try document.encode(changed: ["queue", "history", "playback"])
    }
}

private actor RecommendationFeedbackAccounts: MusicNativeAccountProviding, MusicNativeSimilarModeProviding {
    private var leases: [UUID: MusicAccountQueueLease] = [:]
    nonisolated static func track(_ id: String, source: String? = nil) -> MusicNativeTrackData {
        var value = MJ.obj([("songmid", .string(id)), ("name", .string("Song " + id)),
            ("singer", .string("Synthetic")), ("albumName", .string("Album")), ("duration", .n(180)),
            ("types", .array([.obj([("type", .string("128k"))])]))])
        if let source {
            let heartbeat = source == "heartbeat"
            value["_recommendation"] = .obj([
                ("songId", .string(id)), ("source", .string(source)),
                ("sourceId", .string(heartbeat ? "900" : "")),
                ("feedbackSource", .string(heartbeat ? "aiMode" : "sourceMap-synthetic")),
                ("alg", .string(heartbeat ? "synthetic-heart" : "synthetic-queue"))
            ])
        }
        return .init(platform: "wy", data: value.bytes)
    }
    func beginAccountQueue(platform: MusicAccountPlatform) -> MusicAccountQueueLease {
        let lease = MusicAccountQueueLease(platform: platform); leases[lease.id] = lease; return lease
    }
    func checkAccountQueue(_ lease: MusicAccountQueueLease) throws {
        guard leases[lease.id] === lease else { throw MusicServiceError.staleAccess }
        try lease.authorization.check()
    }
    func endAccountQueue(_ lease: MusicAccountQueueLease) { leases.removeValue(forKey: lease.id)?.revoke() }
    func revoke() { for lease in leases.values { lease.revoke() } }
    func similarModeScope(_ lease: MusicAccountQueueLease) throws -> String {
        try checkAccountQueue(lease); return "synthetic-scope"
    }
    func similarModeBatch(_ lease: MusicAccountQueueLease,
                          request: MusicNativeSimilarModeRequest) throws -> MusicNativeSimilarModeBatch {
        try checkAccountQueue(lease)
        switch request {
        case .heartbeat:
            return .init(items: [Self.track("301", source: "heartbeat")],
                heartbeatContext: .init(playlistID: "900", seedID: "100"))
        case .songRecommendations(_, let offset):
            return .init(items: [Self.track("401", source: "moreRcmd")], hasMore: false, offset: offset + 15)
        case .songRoaming, .artistRoaming: throw MusicServiceError.unsupported
        }
    }
    func accountQueuePage(_ lease: MusicAccountQueueLease, kind: MusicAccountQueueKind,
                          cursor: String?) throws -> MusicNativeTrackPage { throw MusicServiceError.unsupported }
    func nativeRecommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) throws -> MusicNativeTrackPage {
        throw MusicServiceError.unsupported
    }
    func nativeCollections(platform: MusicAccountPlatform, cursor: String?) throws -> MusicAccountCollectionPage {
        throw MusicServiceError.unsupported
    }
    func nativeTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) throws -> MusicNativeTrackPage {
        throw MusicServiceError.unsupported
    }
    func nativeOptions(platform: MusicAccountPlatform, nativeID: String, sourceKey: String) -> [MusicPlaybackOption] {
        [.init(installationId: "official:synthetic", generation: 1, siteId: "wy", sourceKey: sourceKey,
            qualities: ["128k"], title: "Synthetic", version: "1", contentHash: String(repeating: "a", count: 64))]
    }
    func nativeResolve(platform: MusicAccountPlatform, nativeID: String, selection: MusicPlaybackSelection) throws -> MusicNativeAudio {
        .init(url: URL(string: "https://m701.music.126.net/synthetic.mp3")!)
    }
}
