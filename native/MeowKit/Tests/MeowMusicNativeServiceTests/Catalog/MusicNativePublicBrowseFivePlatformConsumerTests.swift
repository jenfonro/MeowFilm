#if os(iOS) || os(macOS)
import Foundation
import XCTest
import MeowContracts
import MeowMusicFeature
@testable import MeowMusicNativeService

@MainActor private final class MusicPublicBrowseFivePlayer: MusicPlaybackDriving {
    var resources: [PlaybackResource] = []
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        resources.append(resource); return .init(id: UUID(), resourceID: resource.id)
    }
    func pause(_ attempt: MusicPlayerAttempt) {}
    func resume(_ attempt: MusicPlayerAttempt) {}
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool { false }
    func stop(_ attempt: MusicPlayerAttempt) {}
}

@MainActor final class MusicNativePublicBrowseFivePlatformConsumerTests: XCTestCase {
    private func detail(_ service: NativeMusicService, model: MusicViewModel,
                        collection: MusicPublicBrowseCollection) -> MusicPublicBrowseViewModel {
        let page = MusicPublicBrowseViewModel(service: service, request: .detail(collection),
            queuePage: { source, cursor in try await model.remoteQueuePage(source, cursor: cursor) },
            releaseSource: { model.releaseRemoteQueueSource($0) })
        page.setAccess(available: true, generation: model.accountAccessGeneration); page.appear()
        return page
    }

    func testBothSearchToDetailPagingToPlayAllOrSelectedOccurrenceUseOneQueueAndPlayerOwner() async throws {
        for platform in MusicNativePublicBrowseFiveFixture.platforms {
            for playAll in [true, false] {
                let store = NativeMusicMemoryDocument(), http = MusicNativePublicBrowseFiveHTTP(platform, repeats: true)
                let scripts = MusicNativePublicBrowseFiveScripts(platform: platform.rawValue), accounts = NativeMusicAccountFixture()
                let service = try await NativeMusicFixture.service(store, scripts: scripts, accounts: accounts, http: http)
                let state = try await service.state()
                _ = try await service.updateSettings(.init(ifRevision: state.settingsRevision, qualityEnabled: ["standard"]))
                let player = MusicPublicBrowseFivePlayer(), model = MusicViewModel(service: service, player: player)
                await model.load()
                let search = MusicPublicBrowseViewModel(service: service, request: .playlists(platform, query: "Synthetic"))
                search.setAccess(available: true, generation: model.accountAccessGeneration); search.appear(); await search.refresh()
                guard case .collection(let collection) = try XCTUnwrap(search.sections.first?.items.first).content else {
                    XCTFail("real search consumer"); await model.close(); await service.close(); return
                }
                let page = detail(service, model: model, collection: collection)
                let before = store.snapshot()
                await page.refresh()
                XCTAssertNil(page.error); XCTAssertEqual(page.total, 65); XCTAssertEqual(page.tracks.count, 30)
                await page.next()
                XCTAssertEqual(page.pageNumber, 2); XCTAssertEqual(page.tracks.count, 30)
                XCTAssertNil(page.remoteSource, "Passive detail paging must not open a playback queue")
                XCTAssertNotNil(page.queueRequest)
                XCTAssertTrue(player.resources.isEmpty)
                let requestsBefore = await scripts.calls(); XCTAssertTrue(requestsBefore.isEmpty)
                XCTAssertEqual(store.snapshot().2, before.2, "browse/open/page are passive")
                let intent = try XCTUnwrap(page.makeActionIntent())
                if playAll {
                    // Same explicit first-page re-read as the UI's Play All button.
                    await page.perform(intent: intent) {
                        let (source, _) = try await page.playbackSource()
                        let first = try await model.remoteQueuePage(source, cursor: nil)
                        let track = try XCTUnwrap(first.items.first)
                        await model.playRemoteQueue(source, start: .init(index: 0, track: track))
                    }
                } else {
                    let track = try XCTUnwrap(page.tracks.dropFirst(3).first)
                    await page.perform(intent: intent) {
                        let (source, index) = try await page.playbackSource(for: track)
                        XCTAssertEqual(index, 33, "The visible repeated recording retains its exact occurrence")
                        await model.playRemoteQueue(source, start: .init(index: index, track: track))
                    }
                }
                XCTAssertNil(page.actionError); XCTAssertNil(model.error)
                let source = try XCTUnwrap(page.remoteSource)
                let queue = try await service.queue(), requests = await scripts.calls()
                XCTAssertEqual(queue.entries.count, 65); XCTAssertEqual(Set(queue.entries.map(\.id)).count, 65)
                XCTAssertEqual(queue.currentId, queue.entries[playAll ? 0 : 33].id)
                XCTAssertEqual(model.state.currentId, queue.currentId); XCTAssertEqual(model.queue, queue.entries)
                XCTAssertEqual(player.resources.count, 1); XCTAssertEqual(requests.count, 1)
                XCTAssertEqual(requests.first?.siteID, platform.rawValue)
                XCTAssertEqual(requests.first?.installationID, NativeMusicFixture.installation)
                let native = try MJ.parse(XCTUnwrap(requests.first?.musicInfo))
                XCTAssertEqual(native["songmid"].string, playAll ? "1" : "2")
                if platform == .migu { XCTAssertEqual(native["copyrightId"].string, playAll ? "6001" : "6002") }
                XCTAssertEqual(store.snapshot().2, before.2 + 1)
                let accountCalls = await accounts.events(); XCTAssertTrue(accountCalls.isEmpty)
                let played = try await service.playedSongs(); XCTAssertTrue(played.isEmpty, "port play is not a real playing event")
                // Page action token is single-use; a duplicate tap cannot replace/play again.
                await page.perform(intent: intent) { XCTFail("replayed action") }
                page.disappear(); search.disappear()
                XCTAssertFalse(source.authorization.isActive)
                await source.close(); await model.close(); await service.close()
            }
        }
    }

    func testDepartureDuringNonCooperativeSelectedScriptResolveCannotSound() async throws {
        for platform in MusicNativePublicBrowseFiveFixture.platforms {
            let gate = NativeMusicGate(), scripts = MusicNativePublicBrowseFiveScripts(platform: platform.rawValue, gate: gate)
            let http = MusicNativePublicBrowseFiveHTTP(platform)
            let service = try await NativeMusicFixture.service(scripts: scripts, http: http)
            let player = MusicPublicBrowseFivePlayer(), model = MusicViewModel(service: service, player: player)
            let settings = try await service.state()
            _ = try await service.updateSettings(.init(ifRevision: settings.settingsRevision, qualityEnabled: ["standard"]))
            await model.load()
            let page = detail(service, model: model, collection: MusicNativePublicBrowseFiveFixture.collection(platform))
            await page.refresh()
            XCTAssertNil(page.remoteSource)
            let track = try XCTUnwrap(page.tracks.first)
            let intent = try XCTUnwrap(page.makeActionIntent())
            // Explicit user selection prepares the source, not passive browsing.
            let (source, index) = try await page.playbackSource(for: track)
            let task = Task {
                await page.perform(intent: intent) {
                    await model.playRemoteQueue(source, start: .init(index: index, track: track))
                }
            }
            await gate.started(); page.disappear()
            XCTAssertFalse(source.authorization.isActive)
            await gate.release(); await task.value
            XCTAssertTrue(player.resources.isEmpty); XCTAssertNil(model.activeResourceID)
            let requests = await scripts.calls(); XCTAssertEqual(requests.count, 1)
            XCTAssertEqual(requests.first?.siteID, platform.rawValue)
            await source.close(); await model.close(); await service.close()
        }
    }
}
#endif
