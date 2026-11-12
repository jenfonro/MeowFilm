import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

private actor ExclusionHistoryScripts: MusicScriptResolving {
    let base = ExclusionTestScripts(["a": ["128k"], "b": ["flac"]])
    let gate: MusicNativeCandidateDiscoveryBarrier
    init(gate: MusicNativeCandidateDiscoveryBarrier) { self.gate = gate }
    func sources() async -> [MusicScriptSource] { await base.sources() }
    func source(installationID: String) async throws -> MusicScriptSource {
        try await base.source(installationID: installationID)
    }
    func resolve(request: MusicScriptRequest) async -> MusicScriptResult<MusicNativeAudio> {
        let value = await base.resolve(request: request)
        if request.installationID == "b" { await gate.enter() }
        return value
    }
    func lyrics(request: MusicScriptRequest) async -> MusicScriptResult<MusicNativeLyrics> {
        await base.lyrics(request: request)
    }
}

// Inject the real session notification while its final owner check is suspended.
// The public CAS/notification chain is covered separately by inFlight below.
private final class ExclusionPublicationDocument: MusicNativeDocumentStoring, @unchecked Sendable {
    let base: NativeMusicMemoryDocument
    private let lock = NSLock()
    private var remaining = 0, notifications = 0
    private var action: (@Sendable () throws -> Void)?
    init(_ base: NativeMusicMemoryDocument) { self.base = base }
    func arm(afterReads: Int, action: @escaping @Sendable () throws -> Void) {
        lock.lock(); defer { lock.unlock() }
        remaining = afterReads; self.action = action
    }
    func read() throws -> Data? {
        lock.lock()
        var notify: (@Sendable () throws -> Void)?
        if action != nil {
            remaining -= 1
            if remaining == 0 { notify = action; action = nil; notifications += 1 }
        }
        lock.unlock()
        try notify?()
        return try base.read()
    }
    func compareAndSwap(expected: Data?, replacement: Data) throws {
        try base.compareAndSwap(expected: expected, replacement: replacement)
    }
    func notificationCount() -> Int { lock.lock(); defer { lock.unlock() }; return notifications }
}

final class MusicSourceExclusionHistoryTests: XCTestCase {
    func testConfigurationRebuildHidesExcludedProviderHistoryWithoutRequests() async throws {
        try await history(requeue: false)
    }
    func testQueueReturnHidesExcludedProviderHistoryWithoutRequests() async throws {
        try await history(requeue: true)
    }
    func testOtherSongExclusionKeepsProviderHistoryWithoutRequests() async throws {
        try await history(requeue: false, otherSong: true)
        try await history(requeue: true, otherSong: true)
    }

    private func selection(_ provider: String, source: MusicNativeSource) -> MusicPlaybackSelection {
        .init(installationId: provider, generation: 1, siteId: source.platform,
              sourceKey: source.sourceKey, quality: "flac", scriptIdentity: ExclusionTestScripts.identity)
    }
    private func history(requeue: Bool, otherSong: Bool = false) async throws {
        let tx = try MusicNativeCandidateDiscoveryFixture.song()
        let wy = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("wy", id: "321"))
        let song = try MusicModel.mergeSources(tx, adding: wy["sources"].array)
        let store = try ExclusionTestFixture.store(song: song)
        let scripts = ExclusionTestScripts(["a": ["flac"], "b": ["flac"]], sites: ["tx", "wy"])
        let http = MusicNativeCandidateDiscoveryHTTP(), qq = MusicNativeCandidateDiscoveryQQ()
        let service = try await NativeMusicFixture.service(store, scripts: scripts, qq: qq, http: http)
        let queue = try await service.queue()
        XCTAssertNotEqual(queue.entries[0].track.songId, queue.entries[1].track.songId)
        let sources = queue.entries[0].track.sources
        let txSource = try XCTUnwrap(sources.first { $0.platform == "tx" })
        let wySource = try XCTUnwrap(sources.first { $0.platform == "wy" })
        let a = selection("a", source: txSource), b = selection("b", source: txSource)
        let otherPlatform = selection("a", source: wySource)
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: queue.revision)
        // Fill all histories through the public exact path, never seed owner dictionaries.
        for selected in [a, b, otherPlatform] {
            let value = try await session.match(intent: .exact(selected))
            XCTAssertEqual(value.selection, selected)
            await service.discardSource(attemptID: value.candidate.attemptId)
        }
        let populated = try await ExclusionTestFixture.snapshot(session)
        XCTAssertEqual(populated.sources.count, 3)
        XCTAssertTrue(populated.sources.contains { $0.selection == a })
        XCTAssertTrue(populated.sources.contains { $0.selection == b })
        XCTAssertTrue(populated.sources.contains { $0.selection == otherPlatform })
        let before = await scripts.requests(), searchesBefore = await http.calls(), qqBefore = await qq.calls()
        XCTAssertEqual(before.resolutions, 3); XCTAssertTrue(searchesBefore.isEmpty); XCTAssertTrue(qqBefore.isEmpty)
        try await service.excludeSource(for: .init(kind: .queue, id: otherSong ? "entry-2" : "entry-1"),
                                        provider: "a", platform: "tx")
        let exclusions = try await service.sourceExclusions(), exclusion = try XCTUnwrap(exclusions.first)
        XCTAssertEqual(exclusions.count, 1)
        XCTAssertEqual(exclusion.songID, queue.entries[otherSong ? 1 : 0].track.songId)
        if requeue {
            let away = try await service.changeQueue(.select("entry-2"), expectedRevision: queue.revision)
            // Exercise the other occurrence's actual session so return cannot
            // accidentally reuse the original current-session object.
            let otherLease = try await service.inspectSources(for: .init(kind: .queue, id: "entry-2"))
            let empty = try await ExclusionTestFixture.snapshot(otherLease)
            XCTAssertTrue(empty.sources.isEmpty)
            await otherLease.close()
            _ = try await service.changeQueue(.select("entry-1"), expectedRevision: away.revision)
        } else { await service.sourceConfigurationChanged() }
        let lease = try await service.inspectSources(for: .init(kind: .queue, id: "entry-1"))
        let visible = try await ExclusionTestFixture.snapshot(lease)
        XCTAssertEqual(visible.sources.count, otherSong ? 3 : 2)
        XCTAssertEqual(visible.sources.contains { $0.selection == a }, otherSong)
        XCTAssertTrue(visible.sources.contains { $0.selection == b }, "Exclusion must not hide another provider")
        XCTAssertTrue(visible.sources.contains { $0.selection == otherPlatform }, "Exclusion must not hide another platform")
        let after = await scripts.requests(), searchesAfter = await http.calls(), qqAfter = await qq.calls()
        XCTAssertEqual(after, before, "Opening the rebuilt inspection must not list declarations or resolve URLs")
        XCTAssertEqual(searchesAfter, searchesBefore); XCTAssertEqual(qqAfter, qqBefore)
        let state = try await service.state()
        let fresh = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: state.revision)
        if !otherSong {
            do {
                _ = try await fresh.match(intent: .exact(a))
                XCTFail("The excluded historical provider must remain inadmissible")
            } catch { XCTAssertEqual(error as? MusicServiceError, .sourceUnavailable) }
        }
        let valid = try await fresh.match(intent: .exact(b))
        XCTAssertEqual(valid.selection, b)
        await service.discardSource(attemptID: valid.candidate.attemptId)
        let allowed = try await fresh.match(intent: .exact(otherPlatform))
        XCTAssertEqual(allowed.selection, otherPlatform)
        await service.discardSource(attemptID: allowed.candidate.attemptId)
        let reused = await scripts.requests()
        XCTAssertEqual(reused.resolutions, before.resolutions, "Legal histories remain reusable")
        let stateBeforeRestore = try await service.state(), historyBeforeRestore = try await service.playedSongs()
        try await service.restoreSourceExclusion(id: exclusion.id)
        let afterRestore = await scripts.requests(), stateAfterRestore = try await service.state()
        let historyAfterRestore = try await service.playedSongs()
        XCTAssertEqual(afterRestore, reused, "Restoring the exclusion must not request another URL")
        XCTAssertEqual(stateAfterRestore, stateBeforeRestore)
        XCTAssertEqual(historyAfterRestore.map(\.id), historyBeforeRestore.map(\.id),
                       "Restoring the exclusion must not create a listening fact")
        let restored = try await fresh.match(intent: .exact(a))
        XCTAssertEqual(restored.selection, a)
        try await service.activateSource(attemptID: restored.candidate.attemptId)
        let accepted = try await service.record(.init(attemptId: restored.candidate.attemptId, occurrenceId: "entry-1",
            resourceId: restored.candidate.resourceId, kind: .playing, position: 23, duration: 180))
        XCTAssertTrue(accepted)
        let remaining = try await service.sourceExclusions()
        XCTAssertTrue(remaining.isEmpty)
        await lease.close(); await service.close()
    }

    func testInFlightPageNeverRepublishesExcludedProviderAndRestoresExplicitly() async throws {
        try await inFlight(.page)
    }
    func testInFlightAutomaticKeepsLegalDonorWithoutRepublishingExcludedProvider() async throws {
        try await inFlight(.automatic)
    }
    func testInFlightQualityNeverRepublishesExcludedProvider() async throws {
        try await inFlight(.quality)
    }
    func testInFlightExactNeverRepublishesExcludedProvider() async throws {
        try await inFlight(.exact)
    }
    func testInFlightOtherSongPlatformAndProviderExclusionsKeepLegalResults() async throws {
        for scope in ["song", "platform", "provider"] { try await inFlight(.page, otherScope: scope) }
    }
    private enum Entry { case page, automatic, quality, exact }
    private func settled(_ updates: AsyncThrowingStream<MusicResolvedSourcesSnapshot, Error>) async throws
        -> [MusicResolvedSourcesSnapshot] {
        try await MusicCandidateDiscoveryDeadline.run(seconds: 3, timing: .init()) {
            var values: [MusicResolvedSourcesSnapshot] = []
            for try await value in updates {
                values.append(value)
                if !value.isLoading { return values }
            }
            throw MusicServiceError.closed
        }
    }
    private func inFlight(_ entry: Entry, otherScope: String? = nil) async throws {
        let gate = MusicNativeCandidateDiscoveryBarrier(), scripts = ExclusionHistoryScripts(gate: gate)
        let service = NativeMusicService(document: try ExclusionTestFixture.store(), scripts: scripts,
            qq: MusicNativeCandidateDiscoveryQQ(), enabledPlatforms: [.qq],
            transport: NativeMusicNoNetwork(), clock: { NativeMusicFixture.date })
        try await service.setAccess(available: true, generation: 1)
        let queue = try await service.queue()
        XCTAssertNotEqual(queue.entries[0].track.songId, queue.entries[1].track.songId)
        let source = try XCTUnwrap(queue.entries[0].track.sources.first)
        let a = MusicPlaybackSelection(installationId: "a", generation: 1, siteId: source.platform,
            sourceKey: source.sourceKey, quality: "128k", scriptIdentity: ExclusionTestScripts.identity)
        let b = selection("b", source: source)
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: queue.revision)
        let donor = try await session.match(intent: .exact(a))
        try await service.activateSource(attemptID: donor.candidate.attemptId)
        let accepted = try await service.record(.init(attemptId: donor.candidate.attemptId, occurrenceId: "entry-1",
            resourceId: donor.candidate.resourceId, kind: .playing, position: 23, duration: 180))
        XCTAssertTrue(accepted)
        let pending = Task<MusicPreparedSource?, Error> {
            switch entry {
            case .page: _ = try await session.loadMore(refresh: false); return nil
            case .automatic: return try await session.match(intent: .automatic)
            case .quality: return try await session.match(intent: .quality("flac"))
            case .exact: return try await session.match(intent: .exact(b))
            }
        }
        await gate.started(1)
        try await service.excludeSource(for: .init(kind: .queue, id: otherScope == "song" ? "entry-2" : "entry-1"),
            provider: otherScope == "provider" ? "unrelated" : "b", platform: otherScope == "platform" ? "wy" : "tx")
        let rows = try await service.sourceExclusions(), exclusion = try XCTUnwrap(rows.first)
        XCTAssertEqual(rows.count, 1)
        XCTAssertEqual(exclusion.songID, queue.entries[otherScope == "song" ? 1 : 0].track.songId)
        XCTAssertEqual(exclusion.provider, otherScope == "provider" ? "unrelated" : "b")
        XCTAssertEqual(exclusion.platform, otherScope == "platform" ? "wy" : "tx")
        let notified = try await ExclusionTestFixture.snapshot(session)
        XCTAssertTrue(notified.isLoading)
        XCTAssertEqual(notified.sources.map(\.selection), [a])
        // Subscribe after the public exclusion has completed its notification,
        // but before B can return. Every subsequently delivered update matters.
        let updates = await session.updates()
        await gate.release()
        let observed = try await settled(updates)
        let rejected = otherScope == nil
        for value in observed {
            XCTAssertTrue(value.sources.contains { $0.selection == a })
            if rejected { XCTAssertFalse(value.sources.contains { $0.selection == b }) }
        }
        if rejected && (entry == .quality || entry == .exact) {
            do { _ = try await pending.value; XCTFail("An excluded target must not prepare") }
            catch { XCTAssertEqual(error as? MusicServiceError, .sourceUnavailable) }
        } else if let prepared = try await pending.value {
            XCTAssertEqual(prepared.selection, rejected ? a : b)
            await service.discardSource(attemptID: prepared.candidate.attemptId)
        }
        let beforeInspect = await scripts.base.requests()
        let lease = try await service.inspectSources(for: .init(kind: .queue, id: "entry-1"))
        let inspected = try await ExclusionTestFixture.snapshot(lease)
        XCTAssertTrue(inspected.sources.contains { $0.selection == a })
        XCTAssertEqual(inspected.sources.contains { $0.selection == b }, !rejected)
        let afterInspect = await scripts.base.requests()
        XCTAssertEqual(afterInspect, beforeInspect, "Inspect must not perform a new lookup to hide the late result")
        XCTAssertTrue(donor.candidate.playbackAuthorization?.isActive == true)
        let progress = try await service.record(.init(attemptId: donor.candidate.attemptId, occurrenceId: "entry-1",
            resourceId: donor.candidate.resourceId, kind: .progress, position: 24, duration: 180))
        XCTAssertTrue(progress, "Excluding B must preserve A's admitted fact owner")
        let stateBeforeRestore = try await service.state(), historyBeforeRestore = try await service.playedSongs()
        XCTAssertEqual(historyBeforeRestore.map { $0.track.songId }, [queue.entries[0].track.songId])
        try await service.restoreSourceExclusion(id: exclusion.id)
        let afterRestore = await scripts.base.requests()
        let visibleAfterRestore = try await ExclusionTestFixture.snapshot(session)
        let stateAfterRestore = try await service.state(), historyAfterRestore = try await service.playedSongs()
        let remaining = try await service.sourceExclusions()
        XCTAssertEqual(afterRestore, afterInspect, "Restore itself must not request another URL")
        XCTAssertEqual(visibleAfterRestore.sources, inspected.sources, "Restoration leaves obtained results intact")
        XCTAssertEqual(stateAfterRestore, stateBeforeRestore)
        XCTAssertEqual(historyAfterRestore.map(\.id), historyBeforeRestore.map(\.id))
        XCTAssertTrue(remaining.isEmpty)
        let restored = try await ExclusionTestFixture.load(session)
        XCTAssertTrue(restored.sources.contains { $0.selection == a })
        XCTAssertTrue(restored.sources.contains { $0.selection == b }, "B is available after explicit exploration")
        let afterExploration = await scripts.base.requests()
        if rejected {
            XCTAssertEqual(afterExploration.resolutions, afterRestore.resolutions + 1,
                           "Exploration may resolve the result withdrawn by the matching exclusion")
        } else {
            XCTAssertEqual(afterExploration.resolutions, afterRestore.resolutions,
                           "An unrelated exclusion must keep the successful B reusable")
            XCTAssertEqual(restored.sources, inspected.sources)
        }
        let stateAfterExploration = try await service.state()
        XCTAssertEqual(stateAfterExploration, stateBeforeRestore)
        let history = try await service.playedSongs()
        XCTAssertEqual(history.count, 1)
        XCTAssertEqual(history.map(\.id), historyBeforeRestore.map(\.id))
        let continued = try await service.record(.init(attemptId: donor.candidate.attemptId, occurrenceId: "entry-1",
            resourceId: donor.candidate.resourceId, kind: .progress, position: 25, duration: 180))
        XCTAssertTrue(continued, "Restoration and exploration must preserve A's admitted fact owner")
        await lease.close(); await service.close()
    }

    func testSessionNotificationDuringFinalOwnerCheckPreventsLatePublication() async throws {
        let document = ExclusionPublicationDocument(try ExclusionTestFixture.store())
        let notification = MusicNativeCandidateDiscoveryBarrier()
        let scripts = ExclusionHistoryScripts(gate: notification)
        let service = NativeMusicService(document: document, scripts: scripts,
            qq: MusicNativeCandidateDiscoveryQQ(), enabledPlatforms: [.qq],
            transport: NativeMusicNoNetwork(), clock: { NativeMusicFixture.date })
        try await service.setAccess(available: true, generation: 1)
        let queue = try await service.queue(), source = try XCTUnwrap(queue.entries[0].track.sources.first)
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: queue.revision)
        let native = try XCTUnwrap(session as? MusicNativeSourceInspection)
        let a = MusicPlaybackSelection(installationId: "a", generation: 1, siteId: source.platform,
            sourceKey: source.sourceKey, quality: "128k", scriptIdentity: ExclusionTestScripts.identity)
        let initial = try await session.match(intent: .exact(a))
        await service.discardSource(attemptID: initial.candidate.attemptId)
        let b = selection("b", source: source)
        let pending = Task { try await session.match(intent: .exact(b)) }
        await notification.started(1)
        // B's return reads: pre-declaration inspectionCheck, post-declaration
        // inspectionCheck, recording admission and provider admission, then
        // the session's owner check. Notify only in the last read, after resolve.
        document.arm(afterReads: 6) {
            let completed = DispatchSemaphore(value: 0)
            Task.detached {
                await native.exclusionsChanged(songID: queue.entries[0].track.songId,
                    provider: "b", platform: source.platform, excluded: true)
                completed.signal()
            }
            guard completed.wait(timeout: .now() + 3) == .success else { throw MusicServiceError.timeout }
        }
        let updates = await session.updates()
        await notification.release()
        do { _ = try await pending.value; XCTFail("Notification must fence publication after owner admission") }
        catch { XCTAssertEqual(error as? MusicServiceError, .sourceUnavailable) }
        XCTAssertEqual(document.notificationCount(), 1)
        for value in try await settled(updates) { XCTAssertEqual(value.sources.map(\.selection), [a]) }
        let lease = try await service.inspectSources(for: .init(kind: .queue, id: "entry-1"))
        let inspected = try await ExclusionTestFixture.snapshot(lease)
        XCTAssertEqual(inspected.sources.map(\.selection), [a])
        await native.exclusionsChanged(songID: queue.entries[0].track.songId,
            provider: "b", platform: source.platform, excluded: false)
        let restored = try await session.match(intent: .exact(b))
        XCTAssertEqual(restored.selection, b)
        await service.discardSource(attemptID: restored.candidate.attemptId)
        await lease.close(); await service.close()
    }
}
