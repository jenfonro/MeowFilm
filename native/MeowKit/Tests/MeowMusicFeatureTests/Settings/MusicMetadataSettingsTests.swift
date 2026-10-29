import Foundation
import XCTest
@testable import MeowMusicFeature

private actor MetadataSettingsGate {
    private var began = false, released = false
    private var starts: [CheckedContinuation<Void, Never>] = []
    private var waits: [CheckedContinuation<Void, Never>] = []
    func wait() async {
        began = true; starts.forEach { $0.resume() }; starts.removeAll()
        if !released { await withCheckedContinuation { waits.append($0) } }
    }
    func started() async {
        if !began { await withCheckedContinuation { starts.append($0) } }
    }
    func release() {
        released = true; waits.forEach { $0.resume() }; waits.removeAll()
    }
}

private actor MetadataSettingsFixture: MusicMetadataPreferencesServing {
    private var value = MusicMetadataPreferences(revision: 0, platforms: [.netease, .qq, .kugou],
                                                enabledPlatforms: [.netease, .qq, .kugou])
    private var readGate: MetadataSettingsGate?
    private var writeGate: MetadataSettingsGate?
    private var nextFailure: MusicServiceError?
    private var reads = 0
    private var writes: [MusicMetadataPreferencesChange] = []

    func blockRead(_ gate: MetadataSettingsGate) { readGate = gate }
    func blockWrite(_ gate: MetadataSettingsGate) { writeGate = gate }
    func fail(_ error: MusicServiceError) { nextFailure = error }
    func replace(_ value: MusicMetadataPreferences) { self.value = value }
    func counts() -> (reads: Int, writes: Int) { (reads, writes.count) }
    func changes() -> [MusicMetadataPreferencesChange] { writes }

    func metadataPreferences() async -> MusicMetadataPreferences {
        reads += 1
        let result = value, gate = readGate; readGate = nil
        if let gate { await gate.wait() } // Deliberately ignores cancellation.
        return result
    }

    func updateMetadataPreferences(_ change: MusicMetadataPreferencesChange) async throws -> MusicMetadataPreferences {
        writes.append(change)
        let failure = nextFailure, gate = writeGate
        nextFailure = nil; writeGate = nil
        if let failure, failure != .commitUnconfirmed { throw failure }
        guard change.ifRevision == value.revision else { throw MusicServiceError.staleRevision }
        value = .init(revision: value.revision + 1, platforms: change.platforms ?? value.platforms,
                      enabledPlatforms: change.enabledPlatforms ?? value.enabledPlatforms)
        let result = value
        if let gate { await gate.wait() } // Simulates a delayed commit acknowledgement.
        if let failure { throw failure }
        return result
    }

    func metadataCandidates(occurrenceID: String) throws -> MusicMetadataCandidates {
        XCTFail("A settings page must not start playback metadata work")
        throw MusicServiceError.invalidRequest
    }
}

@MainActor
final class MusicMetadataSettingsTests: XCTestCase {
    func testTogglesAllowAllDisabledAndOrderUsesLastConfirmedSharedRevision() async throws {
        let store = MetadataSettingsFixture()
        var published: [MusicMetadataPreferences] = []
        let model = MusicMetadataSettingsModel(service: store, onChanged: { published.append($0) })
        model.setAccess(available: true, generation: UUID()); model.reload(); await model.flush()
        XCTAssertEqual(model.value?.revision, 0)
        for platform in [MusicPublicBrowsePlatform.netease, .qq, .kugou] {
            model.setEnabled(platform, false); await model.flush()
        }
        XCTAssertEqual(model.value?.enabledPlatforms, [])
        model.reorder([.kugou, .qq, .netease]); await model.flush()
        XCTAssertEqual(model.value?.platforms, [.kugou, .qq, .netease])
        XCTAssertEqual(model.value?.revision, 4)
        let changes = await store.changes()
        XCTAssertEqual(changes.map(\.ifRevision), [0, 1, 2, 3])
        XCTAssertEqual(changes[2].enabledPlatforms, [])
        XCTAssertNil(changes[3].enabledPlatforms, "Reordering must not re-enable disabled rows")
        XCTAssertEqual(published.map(\.revision), [0, 1, 2, 3, 4])
        XCTAssertFalse(model.busy); XCTAssertNil(model.error)
    }

    func testUnavailablePlatformInvalidPermutationAndDuplicateInputCannotWrite() async {
        let store = MetadataSettingsFixture(), model = MusicMetadataSettingsModel(service: store)
        model.setAccess(available: true, generation: UUID()); model.reload(); await model.flush()
        model.setEnabled(.migu, true)
        model.setEnabled(.qq, true)
        model.reorder([.qq])
        model.reorder([.qq, .qq, .netease])
        model.reorder([.netease, .qq, .kugou])
        await model.flush()
        let counts = await store.counts(); XCTAssertEqual(counts.writes, 0)
    }

    func testBusyWriteIsNotOptimisticallyDisplayedAndRejectsAdditionalMutations() async {
        let store = MetadataSettingsFixture(), gate = MetadataSettingsGate()
        let model = MusicMetadataSettingsModel(service: store)
        model.setAccess(available: true, generation: UUID()); model.reload(); await model.flush()
        await store.blockWrite(gate)
        model.setEnabled(.qq, false)
        await gate.started()
        XCTAssertTrue(model.busy); XCTAssertEqual(model.value?.enabledPlatforms.contains(.qq), true)
        model.setEnabled(.netease, false); model.reorder([.qq, .netease, .kugou])
        let during = await store.counts(); XCTAssertEqual(during.writes, 1)
        await gate.release(); await model.flush()
        XCTAssertEqual(model.value?.enabledPlatforms, [.netease, .kugou])
        XCTAssertFalse(model.busy)
    }

    func testStaleRevisionRereadsOnceWithoutReplayingTheUserPatch() async {
        let store = MetadataSettingsFixture(), model = MusicMetadataSettingsModel(service: store)
        model.setAccess(available: true, generation: UUID()); model.reload(); await model.flush()
        let concurrent = MusicMetadataPreferences(revision: 5, platforms: [.qq, .kugou, .netease],
                                                  enabledPlatforms: [.qq])
        await store.replace(concurrent)
        model.setEnabled(.netease, false); await model.flush()
        XCTAssertEqual(model.value, concurrent)
        XCTAssertEqual(model.error, MusicServiceError.staleRevision.localizedDescription)
        let counts = await store.counts(); XCTAssertEqual(counts.reads, 2); XCTAssertEqual(counts.writes, 1)
        model.setEnabled(.qq, false); await model.flush()
        let changes = await store.changes()
        XCTAssertEqual(changes.map(\.ifRevision), [0, 5])
        XCTAssertEqual(model.value?.revision, 6)
    }

    func testStorageConflictAndUnconfirmedCommitReadAuthorityWithoutRetryingWrite() async {
        for failure in [MusicServiceError.storageConflict, .commitUnconfirmed] {
            let store = MetadataSettingsFixture()
            var published: [MusicMetadataPreferences] = []
            let model = MusicMetadataSettingsModel(service: store, onChanged: { published.append($0) })
            model.setAccess(available: true, generation: UUID()); model.reload(); await model.flush()
            await store.fail(failure)
            model.setEnabled(.qq, false); await model.flush()
            let counts = await store.counts()
            XCTAssertEqual(counts.reads, 2); XCTAssertEqual(counts.writes, 1)
            XCTAssertEqual(model.error, failure.localizedDescription)
            XCTAssertEqual(model.value?.revision, failure == .commitUnconfirmed ? 1 : 0)
            XCTAssertEqual(model.value?.enabledPlatforms.contains(.qq), failure != .commitUnconfirmed)
            XCTAssertEqual(published.last, model.value)
        }
    }

    func testOrdinaryFailureClearsStaleControlsAndRequiresExplicitReread() async {
        let store = MetadataSettingsFixture(), model = MusicMetadataSettingsModel(service: store)
        model.setAccess(available: true, generation: UUID()); model.reload(); await model.flush()
        await store.fail(.storageFailed)
        model.setEnabled(.qq, false); await model.flush()
        XCTAssertNil(model.value); XCTAssertEqual(model.error, MusicServiceError.storageFailed.localizedDescription)
        model.setEnabled(.netease, false); await model.flush()
        var counts = await store.counts(); XCTAssertEqual(counts.reads, 1); XCTAssertEqual(counts.writes, 1)
        model.reload(); await model.flush()
        XCTAssertEqual(model.value?.revision, 0); XCTAssertNil(model.error)
        counts = await store.counts(); XCTAssertEqual(counts.writes, 1)
    }

    func testLateOldScopeReadIsSuppressedAndRepeatedReentryKeepsOnlyOnePhysicalSlot() async {
        let store = MetadataSettingsFixture(), gate = MetadataSettingsGate()
        var published: [MusicMetadataPreferences] = []
        let model = MusicMetadataSettingsModel(service: store, onChanged: { published.append($0) })
        await store.blockRead(gate)
        model.setAccess(available: true, generation: UUID()); model.reload()
        await gate.started()
        for _ in 0..<20 {
            model.setAccess(available: false, generation: UUID())
            model.setAccess(available: true, generation: UUID()); model.reload()
        }
        let latest = MusicMetadataPreferences(revision: 99, platforms: [.kugou, .qq, .netease], enabledPlatforms: [])
        await store.replace(latest)
        var counts = await store.counts()
        XCTAssertEqual(counts.reads, 1); XCTAssertNil(model.value); XCTAssertTrue(published.isEmpty)
        await gate.release(); await model.flush()
        counts = await store.counts()
        XCTAssertEqual(counts.reads, 2); XCTAssertEqual(counts.writes, 0)
        XCTAssertEqual(model.value, latest); XCTAssertEqual(published, [latest])
    }

    func testAccountChangeDoesNotPublishOldCommitOrReplayWriteAndReentryReadsNewScope() async {
        let store = MetadataSettingsFixture(), gate = MetadataSettingsGate()
        var published: [MusicMetadataPreferences] = []
        let model = MusicMetadataSettingsModel(service: store, onChanged: { published.append($0) })
        model.setAccess(available: true, generation: UUID()); model.reload(); await model.flush()
        await store.blockWrite(gate)
        model.setEnabled(.qq, false); await gate.started()
        model.setAccess(available: true, generation: UUID()); model.reload()
        let latest = MusicMetadataPreferences(revision: 20, platforms: [.netease, .qq, .kugou], enabledPlatforms: [.qq])
        await store.replace(latest)
        await gate.release(); await model.flush()
        XCTAssertEqual(model.value, latest); XCTAssertEqual(published.map(\.revision), [0, 20])
        let counts = await store.counts(); XCTAssertEqual(counts.writes, 1); XCTAssertEqual(counts.reads, 2)
    }

    func testDisappearCancelsQueuedSaveAndDelayedReadCannotRepopulateHiddenPage() async {
        let store = MetadataSettingsFixture(), model = MusicMetadataSettingsModel(service: store)
        let generation = UUID()
        model.setAccess(available: true, generation: generation); model.reload(); await model.flush()
        model.setEnabled(.qq, false)
        model.setAccess(available: false, generation: generation) // Before the MainActor task can begin.
        await model.flush()
        var counts = await store.counts(); XCTAssertEqual(counts.writes, 0)
        let gate = MetadataSettingsGate(); await store.blockRead(gate)
        model.setAccess(available: true, generation: generation); model.reload(); await gate.started()
        model.setAccess(available: false, generation: generation)
        await gate.release(); await model.flush()
        XCTAssertNil(model.value); XCTAssertNil(model.error); XCTAssertFalse(model.busy)
        counts = await store.counts(); XCTAssertEqual(counts.writes, 0)
    }
}
